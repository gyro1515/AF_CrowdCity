using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Crowd feature의 root다. simulation kernel, CrowdModel, Human clone의 생성/구동/정리를 소유하며
/// 두 개의 bus 이벤트(CrowdCountChangedEvent, CrowdEliminatedEvent)를 SimTick 마지막 단계에서만 발행한다.
/// presentation(월드 라벨 등)은 소유하지 않는 sim 전용 root이며, GameplayRoot가 tick을 구동한다.
/// </summary>
public sealed class CrowdRoot : MonoBehaviour
{
    // ---- 스폰/배치 계약 상수 (DESIGN.md §2 spawn/placement contract) ----
    private const float RegionShrinkMeters = 2f;          // Ground renderer bounds를 이만큼 안쪽으로 줄인다.
    private const float CornerInset = 0.15f;               // 라이벌 코너 배치 inset 비율.
    private const float SpawnCheckHeight = 0.9f;           // 유효성 검사 sphere의 높이 offset.
    private const float SpawnCheckRadius = 0.6f;           // 유효성 검사 sphere 반지름.
    private const int LeaderProbeMax = 50;                  // 리더 spiral probe 최대 횟수.
    private const float LeaderProbeSpacing = 1f;            // spiral probe 간격(m).
    private const int NeutralAttemptMax = 20;               // 중립 1명당 배치 시도 상한.
    private const float GoldenAngleRad = 2.39996f;          // spiral probe 각도 증분(라디안).

    // ---- 이동/조향 상수 ----
    private const float GridCellSize = 1.5f;                // SpatialGrid cell 크기(DESIGN.md §4).
    private const float SlotChaseGain = 8f;                 // 팔로워가 슬롯을 향하는 비례 gain(1/s).
    private const float WanderRayHeight = 0.9f;             // 중립 배회 방향 검사 raycast 높이.
    private const float WanderRayDistance = 1.5f;           // 중립 배회 방향 검사 raycast 거리.
    private const int WanderRepickTries = 8;                // 배회 방향 재선택 시 최대 후보 수.

    // ---- 리더 CharacterController 스펙 (pinned) ----
    private const float ControllerRadius = 0.35f;
    private const float ControllerHeight = 1.8f;
    private const float ControllerCenterY = 0.9f;
    private const float ControllerSkinWidth = 0.08f;

    // 라이벌 코너 배치의 정규화 좌표. team id 순으로 결정적으로 할당한다.
    private static readonly Vector2[] RivalCornerLerp =
    {
        new Vector2(CornerInset, CornerInset),
        new Vector2(1f - CornerInset, CornerInset),
        new Vector2(CornerInset, 1f - CornerInset),
        new Vector2(1f - CornerInset, 1f - CornerInset),
    };

    private GameConfigSO _config;
    private GameObject _humanPrefab;
    private SimTuning _tuning;
    private System.Random _rng;

    // 걷기 가능 영역: Ground renderer bounds를 2m 줄인 XZ 사각형.
    private float _regionMinX;
    private float _regionMaxX;
    private float _regionMinZ;
    private float _regionMaxZ;
    private float _groundY;

    // simulation kernel (Project.CrowdCity.Core)
    private AgentBuffer _buffer;
    private SpatialGrid _grid;
    private RecruitResolver _recruitResolver;
    private CombatResolver _combatResolver;
    private CombatState _combatState;
    private CombatOutcome _combatOutcome;
    private List<RecruitAssignment> _recruits;
    private List<int> _neighborScratch;

    // crowd/agent 상태 (agent index로 병렬 접근; 스폰 시 capacity로 확보 후 재할당 없음)
    private List<CrowdModel> _crowds;
    private RivalAiDriver[] _aiDrivers;
    private Human[] _humanByAgent;
    private Transform[] _transformByAgent;
    private CharacterController[] _controllerByAgent;
    private float[] _leaderYawDeg;       // team별 리더의 현재 실제 yaw(도).
    private float[] _wanderHeadingDeg;   // 중립 agent의 배회 heading(도).
    private float[] _wanderTimer;        // 중립 agent의 방향 재선택 잔여 시간(초).
    private int[] _lastPublishedCounts;  // team별 마지막 발행 인원 수(coalesce 기준).

    private IEventPublisher<CrowdCountChangedEvent> _countPublisher;
    private IEventPublisher<CrowdEliminatedEvent> _eliminatedPublisher;

    private int _teamCount;
    private int _agentCapacity;
    private Vector2 _playerHeadingDir;
    private bool _playerHasHeading;
    private float _aiTimer;
    private MatchState _matchState = MatchState.Ready;
    private MatchState _pendingState;
    private bool _hasPendingState;
    private bool _initialized;
    private bool _spawned;
    private bool _shutdown;
    private Transform _playerLeaderTransform;

    /// <summary>
    /// 중립 스폰 sampling의 기각률(0..1)이다. rejected 시도 수 ÷ 전체 시도 수이며 SpawnInitial이 기록한다.
    /// play-smoke가 0.8 이하를 단언한다.
    /// </summary>
    public float RejectionRate { get; private set; }

    /// <summary>
    /// team id 순서(0=player, 1..=rival)의 CrowdModel 목록이다. SpawnInitial 이전에는 비어 있다.
    /// </summary>
    public IReadOnlyList<CrowdModel> Crowds
    {
        get { return _crowds; }
    }

    /// <summary>
    /// player 리더 clone의 transform이다. player 탈락 이후에는 null이다.
    /// </summary>
    public Transform PlayerLeaderTransform
    {
        get { return _playerLeaderTransform; }
    }

    /// <summary>
    /// 설정과 소유 자원 참조를 받아 kernel과 내부 상태를 capacity만큼 미리 할당한다.
    /// 걷기 가능 영역은 cityRoot의 "Ground" 자식 renderer bounds를 2m 줄여 계산하며,
    /// Ground가 없으면 fallback 없이 즉시 예외를 던진다.
    /// </summary>
    /// <exception cref="ArgumentNullException">필수 참조가 null이면 발생한다.</exception>
    /// <exception cref="InvalidOperationException">cityRoot 아래에 "Ground" 자식 또는 그 renderer가 없으면 발생한다.</exception>
    public void Initialize(GameConfigSO config, GameObject humanPrefab, Transform cityRoot)
    {
        if (config == null)
        {
            throw new ArgumentNullException(nameof(config));
        }

        if (humanPrefab == null)
        {
            throw new ArgumentNullException(nameof(humanPrefab));
        }

        if (cityRoot == null)
        {
            throw new ArgumentNullException(nameof(cityRoot));
        }

        _config = config;
        _humanPrefab = humanPrefab;
        _tuning = config.Sim;

        ComputeWalkableRegion(cityRoot);

        _teamCount = 1 + config.RivalCount;
        _agentCapacity = _teamCount + config.NeutralCount;

        _buffer = new AgentBuffer(_agentCapacity);
        _grid = new SpatialGrid(GridCellSize, _agentCapacity);
        _recruitResolver = new RecruitResolver();
        _combatResolver = new CombatResolver(_teamCount, _agentCapacity);
        _combatState = new CombatState(_teamCount);
        _combatOutcome = new CombatOutcome(_agentCapacity);
        _recruits = new List<RecruitAssignment>(Mathf.Max(1, config.NeutralCount));
        _neighborScratch = new List<int>(_agentCapacity); // 분리용 이웃 조회 buffer. capacity(4+NeutralCount)로 확보해 per-tick 재할당을 막는다.

        _crowds = new List<CrowdModel>(_teamCount);
        _aiDrivers = new RivalAiDriver[_teamCount];
        for (int t = 1; t < _teamCount; t++)
        {
            _aiDrivers[t] = new RivalAiDriver(t, config, config.Seed + t);
        }

        _humanByAgent = new Human[_agentCapacity];
        _transformByAgent = new Transform[_agentCapacity];
        _controllerByAgent = new CharacterController[_agentCapacity];
        _leaderYawDeg = new float[_teamCount];
        _wanderHeadingDeg = new float[_agentCapacity];
        _wanderTimer = new float[_agentCapacity];
        _lastPublishedCounts = new int[_teamCount];
        for (int t = 0; t < _teamCount; t++)
        {
            _lastPublishedCounts[t] = -1; // 첫 coalesced publish가 반드시 나가도록 한다.
        }

        _rng = new System.Random(config.Seed);
        _countPublisher = EventManager.GetPublisher<CrowdCountChangedEvent>();
        _eliminatedPublisher = EventManager.GetPublisher<CrowdEliminatedEvent>();

        _initialized = true;
    }

    /// <summary>
    /// 초기 스폰을 수행한다. 리더와 중립의 모든 배치 좌표를 Instantiate 이전에 먼저 계산해
    /// CheckSphere가 city collider만 보게 한 뒤(런타임 CharacterController가 아직 없음),
    /// prefab clone을 생성하고 마지막에 첫 coalesced 인원 수 publish로 끝난다.
    /// player는 영역 중앙, 라이벌은 15% inset 코너(team id 순), 중립은 seeded System.Random 기각 sampling이다.
    /// </summary>
    /// <exception cref="InvalidOperationException">Initialize 이전에 호출하면 발생한다.</exception>
    public void SpawnInitial()
    {
        if (!_initialized)
        {
            throw new InvalidOperationException("CrowdRoot.SpawnInitial은 Initialize 이후에 호출해야 합니다.");
        }

        if (_spawned)
        {
            return;
        }

        // ---- 1) 모든 배치 좌표를 Instantiate 이전에 계산한다 ----
        Vector3[] leaderSpots = new Vector3[_teamCount];
        leaderSpots[0] = ResolveLeaderSpot(RegionPoint(0.5f, 0.5f));
        for (int t = 1; t < _teamCount; t++)
        {
            Vector2 corner = RivalCornerLerp[t - 1];
            leaderSpots[t] = ResolveLeaderSpot(RegionPoint(corner.x, corner.y));
        }

        int neutralCount = _config.NeutralCount;
        Vector3[] neutralSpots = new Vector3[Mathf.Max(1, neutralCount)];
        float[] neutralHeadings = new float[Mathf.Max(1, neutralCount)];
        float[] neutralTimers = new float[Mathf.Max(1, neutralCount)];
        int placedNeutrals = 0;
        int totalAttempts = 0;
        int rejectedAttempts = 0;
        int skippedNeutrals = 0;

        for (int n = 0; n < neutralCount; n++)
        {
            bool placed = false;
            for (int attempt = 0; attempt < NeutralAttemptMax; attempt++)
            {
                totalAttempts++;
                float x = Mathf.Lerp(_regionMinX, _regionMaxX, (float)_rng.NextDouble());
                float z = Mathf.Lerp(_regionMinZ, _regionMaxZ, (float)_rng.NextDouble());
                Vector3 candidate = new Vector3(x, _groundY, z);
                if (IsSpotValid(candidate))
                {
                    neutralSpots[placedNeutrals] = candidate;
                    neutralHeadings[placedNeutrals] = (float)(_rng.NextDouble() * 360.0);
                    neutralTimers[placedNeutrals] = Mathf.Lerp(
                        _config.WanderRepickMinSeconds, _config.WanderRepickMaxSeconds, (float)_rng.NextDouble());
                    placedNeutrals++;
                    placed = true;
                    break;
                }

                rejectedAttempts++;
            }

            if (!placed)
            {
                skippedNeutrals++;
            }
        }

        RejectionRate = totalAttempts > 0 ? (float)rejectedAttempts / totalAttempts : 0f;
        if (skippedNeutrals > 0)
        {
            Debug.LogWarning(
                $"[CrowdRoot] 중립 {skippedNeutrals}명이 {NeutralAttemptMax}회 시도 후에도 유효 위치를 찾지 못해 생략되었습니다. " +
                $"RejectionRate={RejectionRate:F2}");
        }

        // ---- 2) 리더 Instantiate + CrowdModel 생성 (team id 오름차순 = agent id 오름차순) ----
        int nextId = 0;
        for (int t = 0; t < _teamCount; t++)
        {
            Vector3 spot = leaderSpots[t];
            Human leader = SpawnClone(spot, "Human_Leader_" + t);
            leader.Init(_config.TeamMaterials[t], true);

            CharacterController controller = leader.gameObject.AddComponent<CharacterController>();
            controller.radius = ControllerRadius;
            controller.height = ControllerHeight;
            controller.center = new Vector3(0f, ControllerCenterY, 0f);
            controller.skinWidth = ControllerSkinWidth;

            int index = _buffer.Add(nextId, t, true, new Vector2(spot.x, spot.z));
            nextId++;

            _humanByAgent[index] = leader;
            _transformByAgent[index] = leader.transform;
            _controllerByAgent[index] = controller;
            _leaderYawDeg[t] = 0f;

            _crowds.Add(new CrowdModel(t, _config.TeamMaterials[t], leader, index));
            if (t == MatchRules.PlayerTeam)
            {
                _playerLeaderTransform = leader.transform;
            }
        }

        // ---- 3) 중립 Instantiate ----
        Material neutralMaterial = _config.TeamMaterials[4];
        for (int n = 0; n < placedNeutrals; n++)
        {
            Vector3 spot = neutralSpots[n];
            Human neutral = SpawnClone(spot, "Human_Neutral_" + n);
            neutral.Init(neutralMaterial, false);

            int index = _buffer.Add(nextId, AgentBuffer.NeutralTeam, false, new Vector2(spot.x, spot.z));
            nextId++;

            _humanByAgent[index] = neutral;
            _transformByAgent[index] = neutral.transform;
            _wanderHeadingDeg[index] = neutralHeadings[n];
            _wanderTimer[index] = neutralTimers[n];
        }

        // 첫 tick의 AI/조향이 유효한 이웃 정보를 읽도록 grid를 한 번 채워 둔다.
        _grid.Rebuild(_buffer);

        _spawned = true;

        // ---- 4) 첫 coalesced 인원 수 publish (모두 -1 → 1로 바뀌므로 전 팀 발행) ----
        PublishTickEvents();
    }

    /// <summary>
    /// GameplayRoot가 매 sim step 전달하는 player의 목표 heading을 저장한다.
    /// hasHeading이 false면 player 리더는 이동하지 않는다(첫 드래그 이전).
    /// </summary>
    public void SetPlayerHeading(Vector2 dir, bool hasHeading)
    {
        _playerHeadingDir = dir;
        _playerHasHeading = hasHeading;
    }

    /// <summary>
    /// 고정 step 시뮬레이션 1회를 수행한다. 단계 순서는 pinned 계약을 따른다:
    /// ①pending 상태 소비 → ②heading(플레이어/AI) → ③리더 CC.Move(고정 Y) → ④팔로워 조향+중립 배회
    /// → ⑤위치 미러링 → ⑥grid 재구축 → ⑦recruit → ⑧combat → ⑨원자적 commit → ⑩coalesced publish.
    /// Playing이 아니면(②~⑨)를 건너뛰어 월드를 동결한다. 이벤트 발행은 모든 변경 이후 마지막에만 일어난다.
    /// </summary>
    public void SimTick(float dt)
    {
        if (!_spawned || _shutdown)
        {
            return;
        }

        // ① 이벤트 handler가 세워 둔 pending 상태를 tick 시작 시점에만 반영한다(재진입 계약).
        if (_hasPendingState)
        {
            _matchState = _pendingState;
            _hasPendingState = false;
        }

        if (_matchState == MatchState.Playing)
        {
            UpdateHeadings(dt);                                                          // ②
            MoveLeaders(dt);                                                             // ③
            SteerFollowersAndNeutrals(dt);                                               // ④
            MirrorPositionsToBuffer();                                                   // ⑤
            _grid.Rebuild(_buffer);                                                      // ⑥
            _recruitResolver.Resolve(_buffer, _grid, _tuning.RecruitRadius, _recruits);  // ⑦
            _combatResolver.Resolve(_buffer, _grid, in _tuning, dt, _combatState, _combatOutcome); // ⑧
            CommitOutcomes();                                                            // ⑨
        }

        PublishTickEvents();                                                             // ⑩
    }

    /// <summary>
    /// GameSession 상태 변화 알림을 받는다. pending 플래그만 세우며 즉시 아무것도 변경하지 않는다.
    /// pending 값은 다음 SimTick 시작 시점에 소비된다(bus 재진입 안전 계약).
    /// </summary>
    public void OnMatchStateChanged(MatchState s)
    {
        _pendingState = s;
        _hasPendingState = true;
    }

    /// <summary>
    /// 생성한 모든 Human clone을 파괴한다. prefab asset은 절대 파괴하지 않으며 여러 번 호출해도 안전하다.
    /// </summary>
    public void Shutdown()
    {
        if (_shutdown)
        {
            return;
        }

        _shutdown = true;

        if (_humanByAgent == null)
        {
            return;
        }

        // clone만 파괴한다. _humanByAgent에는 prefab asset이 절대 들어가지 않는다.
        for (int i = 0; i < _humanByAgent.Length; i++)
        {
            Human human = _humanByAgent[i];
            if (human != null)
            {
                Destroy(human.gameObject);
            }

            _humanByAgent[i] = null;
            _transformByAgent[i] = null;
            _controllerByAgent[i] = null;
        }

        _playerLeaderTransform = null;
    }

    // ---- 스폰/배치 내부 구현 ----

    // cityRoot의 "Ground" 자식 renderer bounds를 2m 줄여 걷기 가능 영역을 계산한다. fallback 없음.
    private void ComputeWalkableRegion(Transform cityRoot)
    {
        Transform ground = cityRoot.Find("Ground");
        if (ground == null)
        {
            throw new InvalidOperationException(
                $"CrowdRoot: cityRoot '{cityRoot.name}' 아래에서 'Ground' 자식을 찾지 못했습니다. " +
                "스폰 영역 계산에 Ground renderer가 필수이며 fallback은 없습니다.");
        }

        Renderer[] renderers = ground.GetComponentsInChildren<Renderer>();
        if (renderers == null || renderers.Length == 0)
        {
            throw new InvalidOperationException(
                "CrowdRoot: 'Ground' 아래에 Renderer가 없습니다. 스폰 영역 계산에 Ground renderer bounds가 필수입니다.");
        }

        Bounds bounds = renderers[0].bounds;
        for (int i = 1; i < renderers.Length; i++)
        {
            bounds.Encapsulate(renderers[i].bounds);
        }

        _regionMinX = bounds.min.x + RegionShrinkMeters;
        _regionMaxX = bounds.max.x - RegionShrinkMeters;
        _regionMinZ = bounds.min.z + RegionShrinkMeters;
        _regionMaxZ = bounds.max.z - RegionShrinkMeters;
        _groundY = bounds.max.y;
    }

    // 영역 내 정규화 좌표(tx, tz)를 world 좌표로 바꾼다.
    private Vector3 RegionPoint(float tx, float tz)
    {
        return new Vector3(
            Mathf.Lerp(_regionMinX, _regionMaxX, tx),
            _groundY,
            Mathf.Lerp(_regionMinZ, _regionMaxZ, tz));
    }

    // 배치 유효성: 몸통 높이의 sphere가 어떤 collider와도 겹치지 않아야 한다.
    // 모든 배치는 Instantiate 이전에 계산되므로 이 검사는 city collider만 만난다.
    private static bool IsSpotValid(Vector3 pos)
    {
        return !Physics.CheckSphere(pos + SpawnCheckHeight * Vector3.up, SpawnCheckRadius);
    }

    // 리더 배치점이 무효하면 1m 간격 spiral로 바깥쪽을 최대 50회 탐색한다.
    private Vector3 ResolveLeaderSpot(Vector3 desired)
    {
        if (IsSpotValid(desired))
        {
            return desired;
        }

        for (int i = 1; i <= LeaderProbeMax; i++)
        {
            float radius = LeaderProbeSpacing * Mathf.Sqrt(i);
            float angle = i * GoldenAngleRad;
            Vector3 candidate = new Vector3(
                Mathf.Clamp(desired.x + radius * Mathf.Cos(angle), _regionMinX, _regionMaxX),
                _groundY,
                Mathf.Clamp(desired.z + radius * Mathf.Sin(angle), _regionMinZ, _regionMaxZ));
            if (IsSpotValid(candidate))
            {
                return candidate;
            }
        }

        Debug.LogWarning(
            $"[CrowdRoot] 리더 spiral probe {LeaderProbeMax}회가 모두 무효라 원래 위치 {desired}를 그대로 사용합니다.");
        return desired;
    }

    // prefab asset을 복제하고 활성화한 뒤 Human component를 보장해 반환한다.
    private Human SpawnClone(Vector3 pos, string cloneName)
    {
        GameObject clone = Instantiate(_humanPrefab, pos, Quaternion.identity, transform);
        clone.name = cloneName;
        clone.SetActive(true); // prefab은 이미 active지만 안전을 위해 유지한다(Animator는 활성화 시점에 bind된다).

        Human human = clone.GetComponent<Human>();
        if (human == null)
        {
            human = clone.AddComponent<Human>();
        }

        return human;
    }

    // ---- SimTick 내부 단계 ----

    // ② player heading 반영 + 라이벌 AI를 AiDecideInterval(시뮬레이션 시간) 주기로 재결정한다.
    private void UpdateHeadings(float dt)
    {
        if (_playerHasHeading)
        {
            _crowds[MatchRules.PlayerTeam].HeadingDeg =
                Mathf.Atan2(_playerHeadingDir.x, _playerHeadingDir.y) * Mathf.Rad2Deg;
        }

        _aiTimer -= dt;
        if (_aiTimer <= 0f)
        {
            _aiTimer += _config.AiDecideInterval;
            for (int t = 1; t < _teamCount; t++)
            {
                CrowdModel model = _crowds[t];
                if (model.Eliminated)
                {
                    continue;
                }

                // AI는 직전 tick 말의 grid/buffer snapshot을 읽는다(이번 tick의 이동 이전 상태).
                model.HeadingDeg = _aiDrivers[t].DecideHeadingDeg(model, _crowds, _buffer, _grid);
            }
        }
    }

    // ③ 리더를 TurnRate로 회전시키며 CharacterController.Move로 이동시킨다. Y는 스폰 높이로 고정한다.
    private void MoveLeaders(float dt)
    {
        float turnRate = _config.TurnRateDegPerSec;
        float speed = _config.LeaderSpeed;

        for (int t = 0; t < _teamCount; t++)
        {
            CrowdModel model = _crowds[t];
            if (model.Eliminated)
            {
                continue;
            }

            int index = model.LeaderAgentIndex;
            float yaw = Mathf.MoveTowardsAngle(_leaderYawDeg[t], model.HeadingDeg, turnRate * dt);
            _leaderYawDeg[t] = yaw;

            bool moving = t != MatchRules.PlayerTeam || _playerHasHeading;
            if (moving)
            {
                float rad = yaw * Mathf.Deg2Rad;
                Vector3 delta = new Vector3(Mathf.Sin(rad), 0f, Mathf.Cos(rad)) * (speed * dt);
                _controllerByAgent[index].Move(delta);

                // 걷기 가능 영역(중립과 동일한 region) 밖으로 나가지 못하게 XZ를 clamp하고,
                // 경사/충돌로 생긴 수직 편차를 제거해 Y를 고정한다.
                Transform leaderTransform = _transformByAgent[index];
                Vector3 pos = leaderTransform.position;
                float clampedX = Mathf.Clamp(pos.x, _regionMinX, _regionMaxX);
                float clampedZ = Mathf.Clamp(pos.z, _regionMinZ, _regionMaxZ);
                if (clampedX != pos.x || clampedZ != pos.z || pos.y != _groundY)
                {
                    leaderTransform.position = new Vector3(clampedX, _groundY, clampedZ);
                }
            }

            _humanByAgent[index].SetHeadingAndSpeed(yaw, moving ? 1f : 0f);
        }
    }

    // ④ 팔로워를 golden-angle 슬롯 + 같은 crowd 분리로 조향하고, 중립을 배회시킨다.
    private void SteerFollowersAndNeutrals(float dt)
    {
        float slotSpacing = _config.SlotSpacing;
        float sepRadius = _config.SeparationRadius;
        float sepPush = _config.SeparationPush;
        float maxSpeed = _config.FollowerMaxSpeed;
        float leaderSpeed = _config.LeaderSpeed;

        for (int t = 0; t < _teamCount; t++)
        {
            CrowdModel model = _crowds[t];
            if (model.Eliminated)
            {
                continue;
            }

            Vector3 leaderPos = _transformByAgent[model.LeaderAgentIndex].position;
            Vector2 leaderXZ = new Vector2(leaderPos.x, leaderPos.z);
            List<int> followerIndices = model.FollowerAgentIndices;

            for (int f = 0; f < followerIndices.Count; f++)
            {
                int index = followerIndices[f];
                Transform followerTransform = _transformByAgent[index];
                Vector3 current = followerTransform.position;
                Vector2 pos = new Vector2(current.x, current.z);

                Vector2 target = leaderXZ + FollowerSteering.SlotOffset(f, slotSpacing);
                Vector2 velocity = (target - pos) * SlotChaseGain;

                // 같은 crowd 분리: 직전 tick의 grid/buffer snapshot을 이웃 기준으로 쓴다.
                _grid.QueryCircle(pos, sepRadius, _neighborScratch);
                Vector2 separation = Vector2.zero;
                for (int c = 0; c < _neighborScratch.Count; c++)
                {
                    int neighbor = _neighborScratch[c];
                    if (neighbor == index || _buffer.Team[neighbor] != t)
                    {
                        continue;
                    }

                    Vector2 away = pos - _buffer.Pos[neighbor];
                    float dist = away.magnitude;
                    if (dist > 0.0001f)
                    {
                        // 가까울수록 강하게 밀어낸다(반경 경계에서 0).
                        separation += away * ((1f - dist / sepRadius) / dist);
                    }
                    else
                    {
                        // 완전히 겹친 경우 index 대소로 결정적인 방향을 준다.
                        separation += new Vector2(index > neighbor ? 1f : -1f, 0f);
                    }
                }

                velocity += separation * sepPush;

                float speed = velocity.magnitude;
                if (speed > maxSpeed)
                {
                    velocity *= maxSpeed / speed;
                    speed = maxSpeed;
                }

                Vector2 next = pos + velocity * dt;
                float nx = Mathf.Clamp(next.x, _regionMinX, _regionMaxX);
                float nz = Mathf.Clamp(next.y, _regionMinZ, _regionMaxZ);
                Human human = _humanByAgent[index];
                human.SetPosition(new Vector3(nx, _groundY, nz));

                if (speed > 0.001f)
                {
                    human.SetHeadingAndSpeed(
                        Mathf.Atan2(velocity.x, velocity.y) * Mathf.Rad2Deg, speed / leaderSpeed);
                }
                else
                {
                    human.SetHeadingAndSpeed(followerTransform.eulerAngles.y, 0f);
                }
            }
        }

        // 중립 배회: 2~5초마다 seeded rng로 방향을 재선택하고 영역 안으로 clamp한다.
        float wanderSpeed = _config.NeutralWanderSpeed;
        int agentCount = _buffer.Count;
        for (int i = 0; i < agentCount; i++)
        {
            if (_buffer.Team[i] != AgentBuffer.NeutralTeam)
            {
                continue;
            }

            _wanderTimer[i] -= dt;
            if (_wanderTimer[i] <= 0f)
            {
                RepickWanderHeading(i);
            }

            float rad = _wanderHeadingDeg[i] * Mathf.Deg2Rad;
            Transform neutralTransform = _transformByAgent[i];
            Vector3 pos = neutralTransform.position;
            float nx = Mathf.Clamp(pos.x + Mathf.Sin(rad) * wanderSpeed * dt, _regionMinX, _regionMaxX);
            float nz = Mathf.Clamp(pos.z + Mathf.Cos(rad) * wanderSpeed * dt, _regionMinZ, _regionMaxZ);

            Human human = _humanByAgent[i];
            human.SetPosition(new Vector3(nx, _groundY, nz));
            human.SetHeadingAndSpeed(_wanderHeadingDeg[i], wanderSpeed / leaderSpeed);
        }
    }

    // 중립의 새 배회 방향을 고른다. 1.5m raycast가 막히는 방향은 기각하고 최대 8회 재시도한다.
    private void RepickWanderHeading(int agentIndex)
    {
        _wanderTimer[agentIndex] = Mathf.Lerp(
            _config.WanderRepickMinSeconds, _config.WanderRepickMaxSeconds, (float)_rng.NextDouble());

        Vector3 origin = _transformByAgent[agentIndex].position + Vector3.up * WanderRayHeight;
        for (int attempt = 0; attempt < WanderRepickTries; attempt++)
        {
            float heading = (float)(_rng.NextDouble() * 360.0);
            float rad = heading * Mathf.Deg2Rad;
            Vector3 dir = new Vector3(Mathf.Sin(rad), 0f, Mathf.Cos(rad));
            if (!Physics.Raycast(origin, dir, WanderRayDistance))
            {
                _wanderHeadingDeg[agentIndex] = heading;
                return;
            }
        }

        // 모든 후보가 막히면 기존 heading을 유지한다. 다음 repick에서 다시 시도한다.
    }

    // ⑤ Unity transform이 저작한 위치를 buffer로 미러링한다(buffer는 team/IsLeader의 최종 권한).
    private void MirrorPositionsToBuffer()
    {
        int agentCount = _buffer.Count;
        Vector2[] bufferPos = _buffer.Pos;
        for (int i = 0; i < agentCount; i++)
        {
            Vector3 pos = _transformByAgent[i].position;
            bufferPos[i] = new Vector2(pos.x, pos.z);
        }
    }

    // ⑨ resolver 결과를 원자적으로 commit한다: recruit → 일반 전향 → 리더 탈락(강등) 순서.
    // buffer, CrowdModel, 시각(sharedMaterial/그림자/CC)이 이 단계 안에서만 함께 변경된다.
    private void CommitOutcomes()
    {
        // recruit: 중립이 팀에 합류한다.
        for (int r = 0; r < _recruits.Count; r++)
        {
            RecruitAssignment recruit = _recruits[r];
            CrowdModel toModel = _crowds[recruit.ToTeam];
            _buffer.Team[recruit.AgentIndex] = recruit.ToTeam;

            Human human = _humanByAgent[recruit.AgentIndex];
            human.SetTeamMaterial(toModel.TeamMaterial);
            toModel.AddFollower(human, recruit.AgentIndex);
        }

        // 일반 전향: 팔로워가 다른 crowd로 이동한다. 리더 전향은 아래 탈락 처리에서 수행한다.
        List<CrowdConversion> conversions = _combatOutcome.Conversions;
        for (int c = 0; c < conversions.Count; c++)
        {
            CrowdConversion conversion = conversions[c];
            if (_buffer.IsLeader[conversion.AgentIndex])
            {
                continue;
            }

            int fromTeam = _buffer.Team[conversion.AgentIndex];
            if (!_crowds[fromTeam].RemoveFollowerByAgentIndex(conversion.AgentIndex, out Human moved))
            {
                continue;
            }

            _buffer.Team[conversion.AgentIndex] = conversion.ToTeam;
            CrowdModel toModel = _crowds[conversion.ToTeam];
            moved.SetTeamMaterial(toModel.TeamMaterial);
            toModel.AddFollower(moved, conversion.AgentIndex);
        }

        // 리더 탈락: buffer의 IsLeader를 내리고(최종 권한), CC 제거·그림자 off 후
        // ex-리더가 eliminator의 팔로워로 합류한다. player 리더도 동일하게 처리한다.
        List<CrowdElimination> eliminations = _combatOutcome.Eliminations;
        for (int e = 0; e < eliminations.Count; e++)
        {
            CrowdElimination elimination = eliminations[e];
            CrowdModel loser = _crowds[elimination.Team];
            Human exLeader = loser.Leader;
            int leaderIndex = elimination.LeaderAgentIndex;

            _buffer.IsLeader[leaderIndex] = false;
            _buffer.Team[leaderIndex] = elimination.ByTeam;

            CharacterController controller = _controllerByAgent[leaderIndex];
            if (controller != null)
            {
                Destroy(controller);
                _controllerByAgent[leaderIndex] = null;
            }

            CrowdModel winner = _crowds[elimination.ByTeam];
            exLeader.SetLeader(false);
            exLeader.SetTeamMaterial(winner.TeamMaterial);
            winner.AddFollower(exLeader, leaderIndex);

            loser.MarkEliminated();
            if (elimination.Team == MatchRules.PlayerTeam)
            {
                _playerLeaderTransform = null;
            }
        }
    }

    // ⑩ pinned publish 순서: 값이 실제로 바뀐 팀의 CrowdCountChangedEvent를 team 오름차순으로 먼저,
    // 그다음 CrowdEliminatedEvent를 player 우선, 나머지 team 오름차순으로 발행한다(패배 우선 계약).
    private void PublishTickEvents()
    {
        for (int t = 0; t < _teamCount; t++)
        {
            int count = _crowds[t].MemberCount;
            if (count != _lastPublishedCounts[t])
            {
                _lastPublishedCounts[t] = count;
                _countPublisher.Publish(new CrowdCountChangedEvent(t, count));
            }
        }

        List<CrowdElimination> eliminations = _combatOutcome.Eliminations;
        if (eliminations.Count > 0)
        {
            for (int e = 0; e < eliminations.Count; e++)
            {
                if (eliminations[e].Team == MatchRules.PlayerTeam)
                {
                    _eliminatedPublisher.Publish(
                        new CrowdEliminatedEvent(eliminations[e].Team, eliminations[e].ByTeam));
                    break;
                }
            }

            for (int t = 1; t < _teamCount; t++)
            {
                for (int e = 0; e < eliminations.Count; e++)
                {
                    if (eliminations[e].Team == t)
                    {
                        _eliminatedPublisher.Publish(
                            new CrowdEliminatedEvent(eliminations[e].Team, eliminations[e].ByTeam));
                        break;
                    }
                }
            }
        }

        // frozen tick에서 지난 tick의 탈락이 재발행되지 않도록 발행 직후 비운다.
        _combatOutcome.Clear();
    }
}

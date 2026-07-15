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
    private const float WanderRayHeight = 0.9f;             // 중립 배회 방향 검사 raycast 높이.
    private const float WanderRayDistance = 1.5f;           // 중립 배회 방향 검사 raycast 거리.
    private const int WanderRepickTries = 8;                // 배회 방향 재선택 시 최대 후보 수.

    // ---- 리더 CharacterController 스펙 (pinned) ----
    private const float ControllerRadius = 0.35f;
    private const float ControllerHeight = 1.8f;
    private const float ControllerCenterY = 0.9f;
    private const float ControllerSkinWidth = 0.08f;

    // 모든 Human(리더/팔로워/중립)이 올라가는 전용 물리 레이어 이름. 유닛끼리 CC 충돌을 끄는 데 쓴다.
    private const string UnitLayerName = "Unit";

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
    // 같은 tick 제거 그래프 해소용 scratch(CommitOutcomes 전용). teamCount(<=4)로 확보해 per-tick 재할당을 막는다.
    private int[] _killerOf;               // [loserTeam]=killerTeam(-1=이번 tick 미제거).
    private bool[] _isEliminatedThisTick;  // [team]=이번 tick 제거 여부.
    private bool[] _terminalVisited;       // ResolveTerminalSurvivor의 순환 감지 방문 집합.

    // crowd/agent 상태 (agent index로 병렬 접근; 스폰 시 capacity로 확보 후 재할당 없음)
    private List<CrowdModel> _crowds;
    private RivalAiDriver[] _aiDrivers;
    private Human[] _humanByAgent;
    private Transform[] _transformByAgent;
    private CharacterController[] _controllerByAgent;
    private Vector2[] _followerVelocity; // 팔로워 조향의 현재 속도 상태(agent index별). 가속 제한 적분에 쓴다.
    private Vector3[] _visualPrev;       // 렌더 보간용 직전 sim step 논리 위치(agent index별). 시각 전용, 커널/미러 미참조.
    private Vector3[] _visualCur;        // 렌더 보간용 최신 sim step 논리 위치(agent index별). 시각 전용, 커널/미러 미참조.
    private float[] _leaderYawDeg;       // team별 리더의 현재 실제 yaw(도).
    private float[] _wanderHeadingDeg;   // 중립 agent의 배회 heading(도).
    private float[] _wanderTimer;        // 중립 agent의 방향 재선택 잔여 시간(초).
    private int[] _lastPublishedCounts;  // team별 마지막 발행 인원 수(coalesce 기준).

    private IEventPublisher<CrowdCountChangedEvent> _countPublisher;
    private IEventPublisher<CrowdEliminatedEvent> _eliminatedPublisher;

    private int _teamCount;
    private int _agentCapacity;
    private int _unitLayer = -1;         // 모든 Human root의 물리 레이어. -1이면 레이어 미해결(무시 설정/레이어 지정을 건너뜀).
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
        if (_initialized || _shutdown)
        {
            return; // 초기화 완료 또는 종료 이후의 재초기화는 no-op다(중복/무효 init 방지).
        }

        // 유닛(리더/팔로워/중립)의 CharacterController 캡슐끼리 서로의 이동을 막지 않도록 전용 레이어를 확보해
        // 자기 자신과의 충돌만 끈다. 환경(건물/소품/차량/공원, Default)과의 충돌은 기본값 그대로 유지한다.
        // 레이어가 없으면 오류를 한 번 남기고 무시 설정을 건너뛴다(fail-safe: 크래시 대신 유닛끼리 충돌 복귀).
        _unitLayer = LayerMask.NameToLayer(UnitLayerName);
        if (_unitLayer < 0)
        {
            Debug.LogError(
                $"[CrowdRoot] '{UnitLayerName}' 레이어를 찾지 못했습니다. 유닛끼리 서로 충돌하게 됩니다. " +
                "ProjectSettings > Tags and Layers에 레이어를 추가한 뒤 에디터에 포커스를 주세요.");
        }
        else
        {
            Physics.IgnoreLayerCollision(_unitLayer, _unitLayer, true);
        }

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
        _tuning.MaxScale = _config.NeutralMaxScale; // kernel이 스케일 인지 질의를 worst-case pair까지 넓힐 수 있게 최대 스케일을 알린다.

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
        _killerOf = new int[_teamCount]; // 제거 그래프 해소용. teamCount(<=4)로 확보해 per-tick 재할당을 막는다.
        _isEliminatedThisTick = new bool[_teamCount];
        _terminalVisited = new bool[_teamCount];

        _crowds = new List<CrowdModel>(_teamCount);
        _aiDrivers = new RivalAiDriver[_teamCount];
        for (int t = 1; t < _teamCount; t++)
        {
            _aiDrivers[t] = new RivalAiDriver(t, config, config.Seed + t);
        }

        _humanByAgent = new Human[_agentCapacity];
        _transformByAgent = new Transform[_agentCapacity];
        _controllerByAgent = new CharacterController[_agentCapacity];
        _followerVelocity = new Vector2[_agentCapacity];
        _visualPrev = new Vector3[_agentCapacity];
        _visualCur = new Vector3[_agentCapacity];
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
        if (_shutdown)
        {
            return; // 종료 이후의 재스폰은 no-op다(중복/무효 spawn 방지).
        }

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
            // 이 중립이 스폰 시 받을 최종 스케일. instantiate에서 쓰는 nextId(=_teamCount + placedNeutrals)와 같은 id라 값이 일치한다.
            // 큰 중립이 지오메트리에 겹쳐 스폰되지 않도록 clearance 검사 반경을 스케일에 비례해 키운다(스케일 1이면 기존과 byte-identical).
            float neutralScale = ComputeNeutralScale(_config.Seed, _teamCount + placedNeutrals);
            float checkRadius = SpawnCheckRadius * neutralScale;
            bool placed = false;
            for (int attempt = 0; attempt < NeutralAttemptMax; attempt++)
            {
                totalAttempts++;
                float x = Mathf.Lerp(_regionMinX, _regionMaxX, (float)_rng.NextDouble());
                float z = Mathf.Lerp(_regionMinZ, _regionMaxZ, (float)_rng.NextDouble());
                Vector3 candidate = new Vector3(x, _groundY, z);
                if (IsSpotValid(candidate, checkRadius))
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

            CharacterController controller = AddController(leader.gameObject);

            int index = _buffer.Add(nextId, t, true, new Vector2(spot.x, spot.z));
            nextId++;

            _humanByAgent[index] = leader;
            _transformByAgent[index] = leader.transform;
            _controllerByAgent[index] = controller;
            _visualPrev[index] = _visualCur[index] = spot; // 첫 프레임 Lerp가 정적이도록 스폰 위치로 시드.
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

            // 결정적 해시 기반 split 분포로 스케일을 준다: baselineChance 확률로 정확히 1.0, 나머지는 1.1~neutralMaxScale
            // 큰 버킷에서 0.1 단위로 균일 양자화해 뽑아 큰 개체를 희소화한다(ComputeNeutralScale 참고).
            // 시뮬레이션 RNG(_rng)를 소비하지 않아 배회/시뮬레이션 draw 순서가 그대로 유지된다. 발/피벗이 바닥에 있어
            // 스케일이 접지를 보존하고, CharacterController 충돌 캡슐도 lossyScale로 함께 스케일되므로 상수를 따로 스케일하지 않는다.
            // 이 단일 원천이 localScale과 buffer.Scale에 모두 흘러간다.
            float scale = ComputeNeutralScale(_config.Seed, nextId);
            neutral.transform.localScale = Vector3.one * scale;

            // scale을 buffer의 단일 진실 원천에 기록한다(kernel의 스케일 인지 접촉/영입/분리가 buffer.Scale을 읽는다).
            // 영입/팀 변경으로도 스케일은 불변이라 이후 갱신 없음.
            int index = _buffer.Add(nextId, AgentBuffer.NeutralTeam, false, new Vector2(spot.x, spot.z), scale);
            nextId++;

            _humanByAgent[index] = neutral;
            _transformByAgent[index] = neutral.transform;
            _controllerByAgent[index] = AddController(neutral.gameObject);
            _visualPrev[index] = _visualCur[index] = spot; // 첫 프레임 Lerp가 정적이도록 스폰 위치로 시드.
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
            // 직전 프레임 RenderInterpolate가 덮어쓴 시각 위치를 논리 위치(_visualCur)로 되돌린다.
            // 이후 모든 transform 읽기/CC.Move/미러링이 항상 논리 위치를 보게 한다(결정성 보장).
            for (int i = 0; i < _buffer.Count; i++) _transformByAgent[i].position = _visualCur[i];
            UpdateHeadings(dt);                                                          // ②
            MoveLeaders(dt);                                                             // ③
            SteerFollowersAndNeutrals(dt);                                               // ④
            MirrorPositionsToBuffer();                                                   // ⑤
            _grid.Rebuild(_buffer);                                                      // ⑥
            _recruitResolver.Resolve(_buffer, _grid, _tuning.RecruitRadius, _tuning.MaxScale, _recruits);  // ⑦
            _combatResolver.Resolve(_buffer, _grid, in _tuning, dt, _combatState, _combatOutcome); // ⑧
            CommitOutcomes();                                                            // ⑨
        }

        PublishTickEvents();                                                             // ⑩
    }

    /// <summary>
    /// 렌더 프레임마다 논리 위치 prev→cur를 alpha로 보간해 transform.position만 덮어쓴다(시각 전용, 회전 미보간).
    /// GameplayRoot가 accumulator 루프 종료 후에만 호출한다. 다음 SimTick 시작의 restore가 논리 위치로 되돌리므로
    /// 커널/미러는 항상 논리 위치만 본다. prev==cur이면 무해하다.
    /// </summary>
    public void RenderInterpolate(float alpha)
    {
        if (_buffer == null || _transformByAgent == null || _shutdown)
        {
            return;
        }

        // Playing이 아니면 tick이 prev/cur를 더 이상 갱신하지 않아 alpha가 마지막 tick의 prev→cur 구간을 계속 sawtooth해 무리가 진동한다. 논리 위치(_visualCur)로 스냅해 정적으로 고정한다(시각 전용).
        if (_matchState != MatchState.Playing)
        {
            for (int i = 0; i < _buffer.Count; i++) _transformByAgent[i].position = _visualCur[i];
            return;
        }

        for (int i = 0; i < _buffer.Count; i++)
        {
            _transformByAgent[i].position = Vector3.Lerp(_visualPrev[i], _visualCur[i], alpha);
        }
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

    private void OnDestroy()
    {
        // 안전망: 정상 경로에서는 GameplayRoot.Shutdown이 이미 해제했다.
        Shutdown();
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
        // Y는 config에서 명시적으로 받는다. bounds.max.y는 메시 두께/융기 지오메트리 때문에 걷기 표면을 넘어서고 Ground에 collider가 없어 raycast 보정도 불가하다.
        _groundY = _config.GroundY;
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
        return IsSpotValid(pos, SpawnCheckRadius);
    }

    // 검사 반경을 명시하는 오버로드. 리더는 기본 SpawnCheckRadius, 큰 중립은 스케일 배수 반경으로 clearance를 검사한다.
    private static bool IsSpotValid(Vector3 pos, float checkRadius)
    {
        return !Physics.CheckSphere(pos + SpawnCheckHeight * Vector3.up, checkRadius);
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
        if (_unitLayer >= 0)
        {
            clone.layer = _unitLayer; // CC를 얹는 root만 유닛 레이어로 옮긴다. 렌더러가 붙은 자식은 Default 그대로 둔다(충돌 매트릭스는 root만 본다).
        }

        Human human = clone.GetComponent<Human>();
        if (human == null)
        {
            human = clone.AddComponent<Human>();
        }

        return human;
    }

    // pinned 스펙의 CharacterController를 붙여 반환한다. 리더/팔로워/중립 모두 같은 스펙을 쓴다.
    private static CharacterController AddController(GameObject go)
    {
        CharacterController controller = go.AddComponent<CharacterController>();
        controller.radius = ControllerRadius;
        controller.height = ControllerHeight;
        controller.center = new Vector3(0f, ControllerCenterY, 0f);
        controller.skinWidth = ControllerSkinWidth;
        return controller;
    }

    // 중립 스폰 스케일용 결정적 해시. (seed, id)만으로 t∈[0,1)을 만들며 _rng를 소비하지 않아 시뮬레이션 draw 순서를 보존한다.
    // splitmix32 스타일 정수 mix라 GetHashCode/UnityEngine.Random과 달리 플랫폼에 무관하게 같은 seed면 같은 스케일을 준다.
    private static float NeutralScaleT(int seed, int id)
    {
        unchecked
        {
            uint x = (uint)seed * 0x9E3779B1u + (uint)id * 0x85EBCA77u;
            x ^= x >> 16;
            x *= 0x7FEB352Du;
            x ^= x >> 15;
            x *= 0x846CA68Bu;
            x ^= x >> 16;
            return (x >> 8) * (1f / 16777216f); // 상위 24비트를 [0,1)로 매핑한다.
        }
    }

    // 중립 스폰 스케일의 최종 값을 계산한다: baselineChance 확률로 정확히 1.0, 나머지는 1.1~maxScale 큰 버킷에서
    // 0.1 단위로 균일 양자화해 뽑는다(큰 개체 희소화). NeutralScaleT의 단일 해시 draw만 소비해 결정성/draw 순서를 보존한다.
    // clearance 검사와 instantiate 양쪽이 같은 (seed, id)로 이 메서드를 호출해 같은 최종 스케일을 쓴다.
    private float ComputeNeutralScale(int seed, int id)
    {
        float t = NeutralScaleT(seed, id); // 단일 draw, ∈[0,1).
        float maxScale = _config.NeutralMaxScale;
        float baselineChance = _config.NeutralBaselineScaleChance;

        // 큰 버킷이 없거나, baseline 분기가 선택됐거나, P>=1이면 기본 크기(1.0)다.
        if (maxScale <= 1.0f + 1e-4f || baselineChance >= 1f || t < baselineChance)
        {
            return 1f;
        }

        // maxScale이 (1.0, 1.1) 사이면 0.1 단위 양자화로 유효한 큰 버킷이 없다.
        if (maxScale < 1.1f)
        {
            return 1f;
        }

        float u = (t - baselineChance) / (1f - baselineChance); // ∈[0,1).
        float scale = Mathf.Round((1f + u * (maxScale - 1f)) * 10f) / 10f;
        return Mathf.Clamp(scale, 1.1f, maxScale);
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

    // ③ 리더를 CharacterController.Move로 이동시킨다. player는 heading으로 즉시 스냅하고 rival만 TurnRate로 슬루한다. Y는 스폰 높이로 고정한다.
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
            // player는 드래그 방향으로 즉시 스냅(슬루 없음)해 이번 tick에 바로 그 방향으로 이동한다.
            // rival AI는 스냅 턴이 twitchy하게 보이지 않도록 기존 TurnRate 슬루를 유지한다.
            float yaw = t == MatchRules.PlayerTeam
                ? model.HeadingDeg
                : Mathf.MoveTowardsAngle(_leaderYawDeg[t], model.HeadingDeg, turnRate * dt);
            _leaderYawDeg[t] = yaw;

            // prev = 이동 전 논리 위치. player 미이동 분기 포함 모든 live 리더에서 캡처(미이동 시 prev==cur).
            _visualPrev[index] = _transformByAgent[index].position;
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

            // cur = 이동+clamp 후 논리 위치. 모든 live 리더에서 캡처(미이동 리더는 prev와 동일).
            _visualCur[index] = _transformByAgent[index].position;
            _humanByAgent[index].SetHeadingAndSpeed(yaw, moving ? 1f : 0f);
        }
    }

    // ④ 팔로워를 가속 제한 arrive 조향으로 리더 뒤 blob에 모으고(CC.Move로 충돌), 중립을 배회시킨다.
    private void SteerFollowersAndNeutrals(float dt)
    {
        float sepRadius = _config.SeparationRadius;
        float sepPush = _config.SeparationPush;
        float maxSpeed = _config.FollowerMaxSpeed;
        float leaderSpeed = _config.LeaderSpeed;
        float cohesionGain = _config.FollowerCohesionGain;
        float arriveRadius = _config.FollowerArriveRadius;
        float maxAccel = _config.FollowerMaxAccel;
        float trailingOffset = _config.FollowerTrailingOffset;

        for (int t = 0; t < _teamCount; t++)
        {
            CrowdModel model = _crowds[t];
            if (model.Eliminated)
            {
                continue;
            }

            Vector3 leaderPos = _transformByAgent[model.LeaderAgentIndex].position;
            Vector2 leaderXZ = new Vector2(leaderPos.x, leaderPos.z);
            // 팔로워 desired = 리더 뒤 단일 중심으로의 arrive(그다음 분리, 클램프). 리더 진행 방향 뒤 하나의
            // 중심점으로 점진적으로 모여 compact blob을 이룬다. yaw→방향 매핑((sin,cos))이 MoveLeaders 규약과
            // 일치하도록 팀당 한 번만 sin/cos를 구해 재사용한다.
            float leaderRad = _leaderYawDeg[t] * Mathf.Deg2Rad;
            float sinYaw = Mathf.Sin(leaderRad);
            float cosYaw = Mathf.Cos(leaderRad);
            Vector2 leaderForward = new Vector2(sinYaw, cosYaw);
            Vector2 center = leaderXZ - leaderForward * trailingOffset; // 리더 진행 방향 뒤의 단일 중심점.
            List<int> followerIndices = model.FollowerAgentIndices;
            // 무리 크기에 따라 arrive 반경을 √인원에 비례해 키운다(footprint ∝ √N, areal packing 근사). 팀당 1회만 계산한다.
            // base arriveRadius를 floor로 유지해(가산항 >= 0) count=0/1에서 기존 동작과 사실상 동일하고 오버슈트 불변식이 그대로 성립한다.
            float effectiveArriveRadius = arriveRadius + _config.FollowerArriveRadiusPerSqrtMember * Mathf.Sqrt(followerIndices.Count);

            for (int f = 0; f < followerIndices.Count; f++)
            {
                int index = followerIndices[f];
                Transform followerTransform = _transformByAgent[index];
                Vector3 current = followerTransform.position;
                _visualPrev[index] = current; // prev = 이동 전 논리 위치.
                Vector2 pos = new Vector2(current.x, current.z);

                // (a) 리더 뒤 단일 중심으로의 arrive: 중심에서 멀수록 최대 속력, 가까울수록 0으로 선형 감쇠해
                //     빠른/느린 유닛의 속도 차로 점진적으로 합류한다(중앙 스냅 방지). 감속 반경 안에서 오버슈트를 막는다.
                Vector2 toCenter = center - pos;
                float d = toCenter.magnitude;
                Vector2 desired = Vector2.zero;
                if (d > 0.0001f)
                {
                    float arriveSpeed = maxSpeed * Mathf.Min(1f, d / effectiveArriveRadius);
                    desired = (toCenter / d) * (arriveSpeed * cohesionGain);
                }

                // 단일 이웃 조회: 같은 팀 이웃만 밀어내 간격을 유지한다. 스케일 인지 간격 때문에 두 유닛이 모두 최대
                // 스케일일 때의 pairSepRadius(sepRadius*NeutralMaxScale)까지 이웃이 잡히도록 조회 반경을 넓힌다.
                // 직전 tick의 grid/buffer snapshot을 이웃 기준으로 쓴다.
                _grid.QueryCircle(pos, sepRadius * Mathf.Max(1f, _config.NeutralMaxScale), _neighborScratch);
                Vector2 separation = Vector2.zero;
                for (int c = 0; c < _neighborScratch.Count; c++)
                {
                    int neighbor = _neighborScratch[c];
                    if (neighbor == index || _buffer.Team[neighbor] != t)
                    {
                        continue;
                    }

                    // 스케일 인지 간격: 두 유닛 스케일 평균으로 pair별 분리 반경을 정한다(둘 다 1.0이면 sepRadius와 동일).
                    float pairSepRadius = sepRadius * 0.5f * (_buffer.Scale[index] + _buffer.Scale[neighbor]);
                    // 분리: pairSepRadius 미만 이웃만 명시적으로 밀어낸다(간격 유지).
                    Vector2 away = pos - _buffer.Pos[neighbor];
                    float dn = away.magnitude;
                    if (dn < pairSepRadius)
                    {
                        if (dn > 0.0001f)
                        {
                            // 가까울수록 강하게 밀어낸다(반경 경계에서 0).
                            separation += away * ((1f - dn / pairSepRadius) / dn);
                        }
                        else
                        {
                            // 완전히 겹친 경우 index 대소로 결정적인 방향을 준다.
                            separation += new Vector2(index > neighbor ? 1f : -1f, 0f);
                        }
                    }
                }

                // 고밀도에서 분리 합력이 arrive 방향을 덮어써 지우지 않도록,
                // desired에 더하기 전에 분리 기여를 followerMaxSpeed로 상한한다(방향 보존).
                Vector2 sepForce = separation * sepPush;
                float sepMag = sepForce.magnitude;
                if (sepMag > maxSpeed)
                {
                    sepForce *= maxSpeed / sepMag;
                }
                desired += sepForce;

                float desiredSpeed = desired.magnitude;
                if (desiredSpeed > maxSpeed)
                {
                    desired *= maxSpeed / desiredSpeed;
                }

                // 가속 제한 적분: 현재 속도를 목표 속도 쪽으로 maxAccel*dt 이내에서만 이동시켜 출렁거림을 없앤다.
                Vector2 velocity = _followerVelocity[index];
                Vector2 dv = desired - velocity;
                float dvMag = dv.magnitude;
                float maxDelta = maxAccel * dt;
                if (dvMag > maxDelta)
                {
                    dv *= maxDelta / dvMag;
                }
                velocity += dv;

                // CC.Move로 이동해 건물 collider와 충돌시킨다(수평 delta만; Y는 아래에서 다시 고정).
                Vector2 move = velocity * dt;
                Vector2 commandedVel = velocity; // Move 직전의 명령 속도(아래 속도 재조정에서 방향 기준으로 사용).
                _controllerByAgent[index].Move(new Vector3(move.x, 0f, move.y));

                Vector3 moved = followerTransform.position;
                float clampedX = Mathf.Clamp(moved.x, _regionMinX, _regionMaxX);
                float clampedZ = Mathf.Clamp(moved.z, _regionMinZ, _regionMaxZ);
                if (clampedX != moved.x || clampedZ != moved.z || moved.y != _groundY)
                {
                    followerTransform.position = new Vector3(clampedX, _groundY, clampedZ);
                }

                // 충돌/clamp로 실제 수평 이동이 명령 속도와 달라질 수 있으므로, 실제 수평 변위를 dt로 나눠
                // 저장 속도를 재조정한다. 다음 tick 적분과 애니메이션(heading/speed)이 현실을 반영해 장애물 뒤 lurch를 막는다.
                // 단, 벽에서 밀려나는 depenetration의 역방향 성분이 속도로 굳어 cohesion과 진동(bounce)하지 않도록,
                // 실제 속도를 명령 방향 기준으로 분해해 접선 성분은 유지(벽 미끄러짐), 전진 성분은 [0, |명령|]로 상한한다(역방향 제거).
                Vector3 finalPos = followerTransform.position;
                _visualCur[index] = finalPos; // cur = 이동+clamp 후 논리 위치.
                Vector2 actualVel = new Vector2(finalPos.x - pos.x, finalPos.z - pos.y) / dt;
                if (commandedVel.sqrMagnitude > 1e-6f)
                {
                    Vector2 dir = commandedVel.normalized;
                    float along = Vector2.Dot(actualVel, dir);
                    Vector2 tangential = actualVel - along * dir;
                    float alongKept = Mathf.Clamp(along, 0f, commandedVel.magnitude);
                    velocity = tangential + alongKept * dir;
                }
                else
                {
                    velocity = actualVel;
                }

                // blocked-damping(옵션 d, raycast 없음): 명령 속도 대비 실제 이동 비율(progress)로 막힘 정도를 추정해
                // 명확히 막힌 팔로워의 속도만 감쇠한다. 벽을 우회하지는 않고 램밍/떨림을 진정시키는 증상 완화다.
                // progress>=BlockedThreshold(자유 이동/벽 미끄러짐)면 dampFactor==1이라 위 bounce-fix 속도가 그대로 유지된다.
                float commandedMag = commandedVel.magnitude;
                float progress = commandedMag > 1e-4f ? actualVel.magnitude / commandedMag : 1f; // 1 = 명령대로 이동(자유), ~0 = 막힘
                const float BlockedThreshold = 0.5f; // 이 미만이면 막힘으로 간주(하드코딩). 벽을 따라 미끄러지는 슬라이더는 접선 속도가 있어 이 위를 유지한다.
                float dampFactor = Mathf.Lerp(_config.FollowerBlockedDamping, 1f, Mathf.Clamp01(progress / BlockedThreshold)); // progress>=threshold -> 1(감쇠 없음), progress 0 -> FollowerBlockedDamping
                velocity *= dampFactor;

                _followerVelocity[index] = velocity;

                float speed = velocity.magnitude;
                Human human = _humanByAgent[index];
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

        // 중립 배회: 2~5초마다 seeded rng로 방향을 재선택하고 CC.Move로 이동한 뒤 영역 안으로 clamp한다.
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
            _visualPrev[i] = neutralTransform.position; // prev = 이동 전 논리 위치.
            _controllerByAgent[i].Move(new Vector3(Mathf.Sin(rad), 0f, Mathf.Cos(rad)) * (wanderSpeed * dt));

            Vector3 pos = neutralTransform.position;
            float nx = Mathf.Clamp(pos.x, _regionMinX, _regionMaxX);
            float nz = Mathf.Clamp(pos.z, _regionMinZ, _regionMaxZ);
            if (nx != pos.x || nz != pos.z || pos.y != _groundY)
            {
                neutralTransform.position = new Vector3(nx, _groundY, nz);
            }

            _visualCur[i] = neutralTransform.position; // cur = 이동+clamp 후 논리 위치.
            _humanByAgent[i].SetHeadingAndSpeed(_wanderHeadingDeg[i], (wanderSpeed / leaderSpeed) * _config.NeutralAnimationSpeed);
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

    // 새 팔로워의 조향 상태를 초기화한다. 첫 tick stutter를 막도록 조향 속도를 0으로 둔다
    // (중립·이전 리더는 팔로워 속도 이력이 없고, 전향 팔로워도 정지 상태에서 시작한다).
    private void InitFollowerSteering(int agentIndex, int team)
    {
        _followerVelocity[agentIndex] = Vector2.zero;
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
            InitFollowerSteering(recruit.AgentIndex, recruit.ToTeam);
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
            InitFollowerSteering(conversion.AgentIndex, conversion.ToTeam);
        }

        // 리더 탈락: 같은 tick에 여러 팀이 제거될 수 있으므로(체인/순환), 먼저 이번 tick 제거 그래프를 한 번 만든 뒤
        // 각 loser의 member를 정규화 해소 결과(최종 live 생존 팀 또는 없음)로만 보낸다. 이렇게 하면 어떤 member도
        // 이미 제거된 팀이나 loser 자신으로 들어가지 않아 freeze(follower 재유입 무한루프)와 zombie(제거된 팀에 남는
        // ex-리더)가 둘 다 사라진다. 국소 판정 제거는 map-separated straggler가 남은 팀도 제거할 수 있으므로,
        // MarkEliminated 전에 loser의 남은 팔로워를 먼저 배출한다. buffer의 IsLeader를 내리는 것이 최종 권한이다.
        List<CrowdElimination> eliminations = _combatOutcome.Eliminations;
        if (eliminations.Count > 0)
        {
            bool absorb = _tuning.LeaderProtection;
            Material neutralMaterial = _config.TeamMaterials[4];

            // PRE-PASS(tick당 1회, 적용 전): 모든 제거 기록에서 loser->killer 그래프를 만든다.
            // 각 팀은 리더가 하나뿐이라 tick당 최대 한 번 제거되므로 killerOf는 함수다(loser 중복 없음).
            for (int t = 0; t < _teamCount; t++)
            {
                _killerOf[t] = -1;
                _isEliminatedThisTick[t] = false;
            }

            for (int e = 0; e < eliminations.Count; e++)
            {
                _killerOf[eliminations[e].Team] = eliminations[e].ByTeam;
                _isEliminatedThisTick[eliminations[e].Team] = true;
            }

            // APPLY: 기록 순서로 순회한다. 대상(terminal)은 pre-pass 그래프에서만 나오므로 순서에 무관하게 결정적이다.
            for (int e = 0; e < eliminations.Count; e++)
            {
                CrowdElimination elimination = eliminations[e];
                CrowdModel loser = _crowds[elimination.Team];

                // 이번 tick 제거 그래프를 걸어 최종 live 생존 팀을, 순환이면 -1(생존 팀 없음)을 구한다.
                // terminal은 절대 loser 자신이나 이번 tick 제거된 팀이 아니다(따라서 아래 배출 루프는 반드시 종료한다).
                int terminal = ResolveTerminalSurvivor(elimination.Team);

                // 팔로워: 보호 ON + 생존 팀 존재 시 흡수, 그 외(ON 순환 / OFF)는 중립화. MarkEliminated 전에 전량 배출한다.
                List<int> loserFollowers = loser.FollowerAgentIndices;
                while (loserFollowers.Count > 0)
                {
                    int agentIndex = loserFollowers[0]; // 앞에서 뽑아 swap-remove O(1)로 비운다(O(n) 전체 배출).
                    if (!loser.RemoveFollowerByAgentIndex(agentIndex, out Human orphan))
                    {
                        break;
                    }

                    if (absorb && terminal >= 0)
                    {
                        RouteToSurvivor(agentIndex, orphan, terminal);
                    }
                    else
                    {
                        Neutralize(agentIndex, orphan, neutralMaterial);
                    }
                }

                // ex-리더(양 모드 공통): 생존 팀으로 강등 전향, 순환이면 중립화(resolver의 leader->killer 전향과 일치하되
                // 체인을 최종 생존 팀까지 해소해 zombie를 막는다). buffer의 IsLeader를 내리는 것이 최종 권한이다.
                Human exLeader = loser.Leader;
                int leaderIndex = elimination.LeaderAgentIndex;
                _buffer.IsLeader[leaderIndex] = false;
                exLeader.SetLeader(false);
                if (terminal >= 0)
                {
                    RouteToSurvivor(leaderIndex, exLeader, terminal);
                }
                else
                {
                    Neutralize(leaderIndex, exLeader, neutralMaterial);
                }

                loser.MarkEliminated();
                if (elimination.Team == MatchRules.PlayerTeam)
                {
                    _playerLeaderTransform = null;
                }
            }
        }
    }

    // 이번 tick 제거된 팀 t의 member가 최종적으로 합류할 live 생존 팀을 구한다. _killerOf 그래프를 따라가며 이번 tick에
    // 제거되지 않은 첫 팀을 반환하고, 방문 집합으로 순환을 감지하면 -1(생존 팀 없음)을 반환한다. 반환값은 항상 live 팀이거나
    // -1이며, 절대 t 자신이나 이번 tick 제거된 팀이 아니다. t는 반드시 이번 tick 제거된 팀이라 _killerOf[t]는 유효한
    // killer(>=0)다. teamCount(<=4)라 walk는 상수 비용이고 할당이 없다.
    private int ResolveTerminalSurvivor(int t)
    {
        for (int i = 0; i < _teamCount; i++)
        {
            _terminalVisited[i] = false;
        }

        _terminalVisited[t] = true;
        int cur = _killerOf[t];
        while (_isEliminatedThisTick[cur])
        {
            if (_terminalVisited[cur])
            {
                return -1; // 순환: live 생존 팀이 없다.
            }

            _terminalVisited[cur] = true;
            cur = _killerOf[cur];
        }

        return cur; // 이번 tick 제거되지 않은 첫 팀(live).
    }

    // 이번 tick 제거된 팀의 member(팔로워 또는 ex-리더)를 live 생존 팀으로 옮긴다: buffer 팀·model·머티리얼·조향을 함께 갱신한다.
    private void RouteToSurvivor(int agentIndex, Human human, int survivorTeam)
    {
        CrowdModel survivor = _crowds[survivorTeam];
        _buffer.Team[agentIndex] = survivorTeam;
        human.SetTeamMaterial(survivor.TeamMaterial);
        survivor.AddFollower(human, agentIndex);
        InitFollowerSteering(agentIndex, survivorTeam);
    }

    // member를 중립으로 되돌린다(다음 tick 배회 방향 즉시 재선택). RecruitResolver가 이후 다시 영입한다.
    private void Neutralize(int agentIndex, Human human, Material neutralMaterial)
    {
        _buffer.Team[agentIndex] = AgentBuffer.NeutralTeam;
        human.SetTeamMaterial(neutralMaterial);
        InitFollowerSteering(agentIndex, AgentBuffer.NeutralTeam);
        _wanderTimer[agentIndex] = 0f; // 다음 tick에 배회 방향을 즉시 재선택하도록(결정적).
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

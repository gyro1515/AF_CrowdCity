using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Jobs;
using UnityEngine;

/// <summary>
/// Crowd feature의 root다. simulation kernel, CrowdModel, Human clone의 생성/구동/정리를 소유하며
/// 두 개의 bus 이벤트(CrowdCountChangedEvent, CrowdEliminatedEvent)를 SimTick 마지막 단계에서만 발행한다.
/// presentation(월드 라벨 등)은 소유하지 않는 sim 전용 root이며, GameplayRoot가 tick을 구동한다.
/// </summary>
public sealed class CrowdRoot : MonoBehaviour
{
    // ---- 스폰/배치 계약 상수 (소비처: PrepareSpawnPlacements / SpawnLeaders / SpawnNeutralRange). 아래 CheckSphere 검사는 전부 Instantiate 이전에 끝난다 — baked CC가 Instantiate 즉시 live이라 그 사이에 물리/overlap 질의가 들어가면 안 된다(CLAUDE.md §11.5 "Baked physics & determinism exception"). ----
    private const float RegionShrinkMeters = 2f;          // Ground renderer bounds를 이만큼 안쪽으로 줄인다.
    private const float CornerInset = 0.15f;               // 라이벌 코너 배치 inset 비율.
    private const float SpawnCheckHeight = 0.9f;           // 유효성 검사 sphere의 높이 offset.
    private const float SpawnCheckRadius = 0.6f;           // 유효성 검사 sphere 반지름.
    private const int LeaderProbeMax = 50;                  // 리더 spiral probe 최대 횟수.
    private const float LeaderProbeSpacing = 1f;            // spiral probe 간격(m).
    private const int NeutralAttemptMax = 20;               // 중립 1명당 배치 시도 상한.
    private const float GoldenAngleRad = 2.39996f;          // spiral probe 각도 증분(라디안).

    // ---- 이동/조향 상수 ----
    private const float GridCellSize = 1.5f;                // SpatialGrid cell 크기(m). 아래 _grid 생성 인자이며 결정성 고정 상수다.
    private const float WanderRayHeight = 0.9f;             // 중립 배회 방향 검사 raycast 높이.
    private const float WanderRayDistance = 1.5f;           // 중립 배회 방향 검사 raycast 거리.
    private const int WanderRepickTries = 8;                // 배회 방향 재선택 시 최대 후보 수.
    private const int SteeringForceBatch = 64;              // M2-a3 팔로워 FORCE IJobParallelFor의 innerloop batch 크기(byte-identity와 무관).

    // Phase C 단계 2: SDF solver의 벽 standoff 기준 거리. Human.prefab CC 스펙 radius(0.35) + skinWidth(0.08).
    // per-agent scale(=CC lossyScale=buffer.Scale)로 곱해 CC.Move의 스케일 비례 접촉 거리를 재현한다.
    private const float WallCollisionClearance = 0.43f;

    // GPU-anim Stage 1 Chunk B: VAT 걷기 클립 주기(초). Chunk A 베이크(LoopClose 21행 @30Hz)와 일치하며 phase 적분에 쓴다.
    private const float WalkPeriodSeconds = 0.7f;

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

    // CrowdRoot가 자기 스폰 대상(Human clone)을 소유한다. 프리팹에 직렬화 저작되며 GameSceneSetup이 배선한다.
    // 상위(GameplayRoot/GameSceneController)의 humanPrefab 주입 체인은 없다. 미배선이면 Initialize가 즉시 예외를 던진다(하드 페일).
    [SerializeField] private GameObject _humanPrefab;

    private SimTuning _tuning;
    private System.Random _rng;

    // Phase C 단계 2: 정적 도시 벽의 read-only SDF asset(City/Generated/WallSdf.asset). 프리팹에 직렬화 저작되며 GameSceneSetup이 배선한다.
    // 미배선(null)이면 solver 경로는 CC.Move로 안전 폴백한다(하드 페일 아님).
    [SerializeField] private WallSdfAsset _wallSdfAsset;

    // Phase C 단계 2: 정적 도시 벽의 read-only SDF. Initialize에서 세션 수명으로 로드(Persistent), Shutdown에서 Dispose한다.
    // solver 경로(_useSdfSolver ON)만 조회하며, null/미로드면 solver 경로는 CC.Move로 안전 폴백한다.
    private WallField _wallField;

    // CC.Move(false, 기본·안전 롤백) ↔ 결정적 SDF solver(true) 이동 경로 선택 스위치. 기본 OFF로 두고, 이질감 게이트
    // 비교/통과 후에만 ON으로 전환한다. serialize해 인스펙터/프리팹에서도 바꿀 수 있고 테스트/하네스는 프로퍼티로 토글한다.
    [SerializeField] private bool _useSdfSolver;

    // GPU 경로에서 CrowdRoot가 소유/구동하는 프리팹 저작 자식 렌더러다. 미배선(null)이면 GPU 경로는 비활성(SMR 유지).
    [SerializeField] private CrowdRenderer _crowdRenderer;

    // 걷기 가능 영역: Ground renderer bounds를 2m 줄인 XZ 사각형.
    private float _regionMinX;
    private float _regionMaxX;
    private float _regionMinZ;
    private float _regionMaxZ;
    private float _groundY;

    // simulation kernel (Project.CrowdCity.Core)
    // M2-a1: 권한 있는 agent 상태(SoA buffer + 조향 상태)의 Persistent NativeArray 소유자. Initialize에서 1회 생성, Shutdown에서 1회 해제.
    private CrowdSimState _simState;
    private AgentBuffer _buffer;         // _simState.Agents 별칭(캐시). 소유/해제는 _simState가 한다.
    private SpatialGrid _grid;
    private RecruitResolver _recruitResolver;
    private CombatResolver _combatResolver;
    private CombatState _combatState;
    private CombatOutcome _combatOutcome;
    private List<RecruitAssignment> _recruits;
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
    // 아래 4개는 _simState 소유 NativeArray의 별칭(캐시)이다. 인덱싱 읽기/쓰기는 공유 메모리로 반영되며, 해제는 _simState만 한다.
    private NativeArray<Vector2> _followerVelocity; // 팔로워 조향의 현재 속도 상태(agent index별). 가속 제한 적분에 쓴다.
    private Vector3[] _visualPrev;       // 렌더 보간용 직전 sim step 논리 위치(agent index별). 시각 전용, 커널/미러 미참조.
    private Vector3[] _visualCur;        // 렌더 보간용 최신 sim step 논리 위치(agent index별). 시각 전용, 커널/미러 미참조.
    private Vector3[] _visualRender;     // 이번 프레임 보간 결과 렌더 위치(agent index별). RenderGpuCrowd가 transform 재읽기 없이 재사용. 시각 전용, 커널/미러 미참조.
    private float[] _visualYaw;          // 렌더용 정규화 yaw(도, agent index별). SetHeadingAndSpeed와 동일 값을 저장해 RenderGpuCrowd가 transform 회전 재읽기 없이 재사용. 시각 전용, 커널/미러 미참조.
    private NativeArray<float> _leaderYawDeg;       // team별 리더의 현재 실제 yaw(도).
    private NativeArray<float> _wanderHeadingDeg;   // 중립 agent의 배회 heading(도).
    private NativeArray<float> _wanderTimer;        // 중립 agent의 방향 재선택 잔여 시간(초).
    private int[] _lastPublishedCounts;  // team별 마지막 발행 인원 수(coalesce 기준).

    // ---- GPU-anim Stage 1 Chunk B presentation 상태(시각 전용, 커널/미러/이벤트 미참조) ----
    private float[] _visualSpeed01;    // agent별 애니메이션 재생 속도(SetHeadingAndSpeed의 speed01 미러). phase 적분에만 쓴다.
    private float[] _phase01;          // agent별 걷기 사이클 위상 [0,1). id 해시로 시드 후 매 렌더 프레임 dt*speed로 적분한다.
    private uint[] _teamPackedColor;   // [0..3]=팀 색, [4]=중립 색. sRGB 바이트 팩(셰이더가 linear로 변환). Initialize에서 1회 계산.
    private CrowdRenderer.InstanceData[] _instanceScratch; // GPU 업로드용 per-frame 스크래치(GPU 활성 시에만 할당).
    private int[] _leaderScratch;      // 리더 agent index 스크래치(그림자 draw용, teamCount 이하).
    private bool _gpuRenderActive;     // CrowdRenderer.Init 성공 + 스위치 ON일 때만 true. false면 SMR 경로.
    private bool _visualRenderFilled;  // RenderInterpolate가 _visualRender를 최소 1회 채웠는지. TryGetCrowdRenderBounds가 렌더 이전(전부 0) bounds를 반환하지 않게 하는 가드.
    private Bounds _crowdWorldBounds;  // 인스턴스 draw의 world bounds(region + 유닛 높이). SpawnInitial에서 계산.

    private IEventPublisher<CrowdCountChangedEvent> _countPublisher;
    private IEventPublisher<CrowdEliminatedEvent> _eliminatedPublisher;

    private int _teamCount;
    private int _agentCapacity;
    private int _neutralCount;
    private int _lastScheduledFollowerCount; // 직전 SimTick에서 조향/이동 job에 스케줄된 팔로워 수(harness의 0-팔로워 degenerate run 방지 단언용).
    private int _unitLayer = -1;         // 모든 Human root의 물리 레이어. -1이면 레이어 미해결(무시 설정/레이어 지정을 건너뜀).
    private int _wallProbeMask;          // 벽 탐지 raycast용 레이어 마스크(기본 raycast 레이어에서 Unit 제외). Initialize에서 1회 계산.
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

    // ---- 청크(다중 프레임) 스폰 스냅샷 상태 ----
    // PrepareSpawnPlacements가 계산해 채우고 SpawnLeaders/SpawnNeutralRange가 읽는다. 동기(SpawnInitial)·청크 경로가 같은 스냅샷을 공유한다.
    private Vector3[] _leaderSpots;
    private Vector3[] _neutralSpots;
    private float[] _neutralHeadings;
    private float[] _neutralTimers;
    private float[] _neutralScales;
    private int _placedNeutrals;
    private int _neutralCursor;      // StepChunkedSpawn가 지금까지 채운 중립 수(다음 배치 시작 인덱스).
    private bool _spawnInProgress;   // BeginChunkedSpawn부터 마지막 배치까지 true. 중복 Begin을 막는 가드에서 읽는다.
    [SerializeField, Min(1)] private int _spawnBatchSize = 128; // 프레임당 인스턴스화할 중립 수(byte-identity와 무관).

    /// <summary>
    /// 중립 스폰 sampling의 기각률(0..1)이다. rejected 시도 수 ÷ 전체 시도 수이며 SpawnInitial이 기록한다.
    /// play-smoke가 0.8 이하를 단언한다.
    /// </summary>
    public float RejectionRate { get; private set; }

    /// <summary>
    /// 이동 경로 스위치다. false(기본)면 리더/팔로워/중립 이동에 <c>CharacterController.Move</c>(기존/안전 롤백)를,
    /// true면 결정적 <see cref="WallSolver"/>(SDF)를 쓴다. WallField 미로드 시 ON이라도 CC.Move로 폴백한다.
    /// 이질감 게이트 비교/통과 전까지는 OFF를 유지하는 롤백 경로다. 커널 순서/RNG/이벤트 발행에는 영향이 없다.
    /// </summary>
    public bool UseSdfSolver
    {
        get { return _useSdfSolver; }
        set { _useSdfSolver = value; }
    }

    /// <summary>
    /// SDF solver 이동 경로가 실제로 활성인지 여부다(스위치 ON + WallField 로드 성공). <see cref="ApplyHorizontalMove"/>의
    /// SDF 분기 진입 조건과 동일하다. 플래그만이 아니라 WallField가 실제 로드됐는지까지 확인하는 읽기 전용 관찰 API로,
    /// harness가 SDF ON baseline의 유효성을 단언하는 데 쓴다. 로드/이동 동작을 바꾸지 않는다.
    /// </summary>
    public bool IsSdfActive => _useSdfSolver && _wallField != null && _wallField.IsLoaded;

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
    /// Phase C 단계 0 oracle 스냅샷용 읽기 전용 agent 수다(스폰 이후 buffer.Count, 미스폰 시 0).
    /// harness/테스트 전용 관찰 API이며 시뮬 상태를 변경하지 않는다.
    /// </summary>
    public int OracleAgentCount => _buffer != null ? _buffer.Count : 0;

    /// <summary>
    /// 직전 SimTick에서 조향/이동 job에 스케줄된 팔로워 수다(0=미틱/전멸). harness가 Burst job이 실제로
    /// 팔로워를 처리했는지(0-팔로워 degenerate run 방지) 단언하는 읽기 전용 관찰 API다. 시뮬 상태를 바꾸지 않는다.
    /// </summary>
    public int LastScheduledFollowerCount => _lastScheduledFollowerCount;

    /// <summary>
    /// agent index i의 현재 SoA 스냅샷을 out으로 복사한다(위치는 이번 tick 미러링된 world XZ, team/IsLeader는 commit 후 값).
    /// 내부 배열 참조를 노출하지 않고 값만 복사하는 읽기 전용 관찰 API다(oracle harness 전용).
    /// </summary>
    public void OracleReadAgent(int i, out int id, out int team, out bool isLeader, out Vector2 pos, out float scale)
    {
        id = _buffer.Id[i];
        team = _buffer.Team[i];
        isLeader = _buffer.IsLeader[i];
        pos = _buffer.Pos[i];
        scale = _buffer.Scale[i];
    }

    /// <summary>
    /// 설정과 소유 자원 참조를 받아 kernel과 내부 상태를 capacity만큼 미리 할당한다.
    /// 걷기 가능 영역은 cityRoot의 "Ground" 자식 renderer bounds를 2m 줄여 계산하며,
    /// Ground가 없으면 fallback 없이 즉시 예외를 던진다.
    /// </summary>
    /// <exception cref="ArgumentNullException">필수 참조가 null이면 발생한다.</exception>
    /// <exception cref="InvalidOperationException">cityRoot 아래에 "Ground" 자식 또는 그 renderer가 없으면 발생한다.</exception>
    public void Initialize(GameConfigSO config, Transform cityRoot, int spawnCount)
    {
        if (_initialized || _shutdown)
        {
            return; // 초기화 완료 또는 종료 이후의 재초기화는 no-op다(중복/무효 init 방지).
        }

        // 유닛(리더/팔로워/중립)의 CharacterController 캡슐끼리 서로의 이동을 막지 않도록 전용 레이어를 확보해
        // 자기 자신과의 충돌만 끈다. 환경(건물/소품/차량/공원, Default)과의 충돌은 기본값 그대로 유지한다.
        // 레이어가 없으면 오류를 한 번 남기고 무시 설정을 건너뛴다(fail-safe: 크래시 대신 유닛끼리 충돌 복귀).
        _unitLayer = LayerMask.NameToLayer(UnitLayerName);
        // 벽 탐지 raycast(배회/라이벌)는 IgnoreLayerCollision의 영향을 받지 않으므로, 유닛 캡슐을 벽으로 오인하지 않도록
        // 기본 raycast 레이어를 기준으로 두고 아래에서 Unit 레이어가 해결되면 마스크에서 제외한다(미해결이면 기본 마스크 그대로).
        _wallProbeMask = Physics.DefaultRaycastLayers;
        if (_unitLayer < 0)
        {
            Debug.LogError(
                $"[CrowdRoot] '{UnitLayerName}' 레이어를 찾지 못했습니다. 유닛끼리 서로 충돌하게 됩니다. " +
                "ProjectSettings > Tags and Layers에 레이어를 추가한 뒤 에디터에 포커스를 주세요.");
        }
        else
        {
            Physics.IgnoreLayerCollision(_unitLayer, _unitLayer, true);
            _wallProbeMask &= ~(1 << _unitLayer); // 유닛(_unitLayer) 캡슐을 벽 탐지 raycast에서 제외.
        }

        if (config == null)
        {
            throw new ArgumentNullException(nameof(config));
        }

        if (cityRoot == null)
        {
            throw new ArgumentNullException(nameof(cityRoot));
        }

        _config = config;
        _neutralCount = spawnCount;

        // CrowdRoot가 자기 스폰 대상을 소유한다(프리팹 직렬화 필드). 미배선이면 즉시 예외(하드 페일)로 스폰 불가를 알린다.
        if (_humanPrefab == null)
        {
            throw new InvalidOperationException(
                "[CrowdRoot] _humanPrefab 직렬화 필드가 비어 있습니다(프리팹 배선 누락). " +
                "GameSceneSetup으로 CrowdRoot 프리팹의 _humanPrefab을 배선하세요.");
        }

        _tuning = config.Sim;
        _tuning.MaxScale = _config.NeutralMaxScale; // kernel이 스케일 인지 질의를 worst-case pair까지 넓힐 수 있게 최대 스케일을 알린다.

        ComputeWalkableRegion(cityRoot);

        _teamCount = 1 + config.RivalCount;
        _agentCapacity = _teamCount + spawnCount;

        // M2-a1: 권한 있는 agent 상태를 Persistent NativeArray로 1회 할당한다(storage 이관, 결과 byte-identical).
        // _buffer와 아래 조향 필드는 _simState 소유 배열의 별칭이다(해제는 Shutdown에서 _simState만).
        _simState = new CrowdSimState(_agentCapacity, _teamCount);
        _buffer = _simState.Agents;
        _grid = new SpatialGrid(GridCellSize, _agentCapacity);
        _recruitResolver = new RecruitResolver();
        _combatResolver = new CombatResolver(_teamCount, _agentCapacity);
        _combatState = new CombatState(_teamCount);
        _combatOutcome = new CombatOutcome(_agentCapacity);
        _recruits = new List<RecruitAssignment>(Mathf.Max(1, spawnCount));
        _killerOf = new int[_teamCount]; // 제거 그래프 해소용. teamCount(<=4)로 확보해 per-tick 재할당을 막는다.
        _isEliminatedThisTick = new bool[_teamCount];
        _terminalVisited = new bool[_teamCount];

        _crowds = new List<CrowdModel>(_teamCount);
        _aiDrivers = new RivalAiDriver[_teamCount];
        for (int t = 1; t < _teamCount; t++)
        {
            _aiDrivers[t] = new RivalAiDriver(t, config, config.Seed + t, spawnCount);
        }

        _humanByAgent = new Human[_agentCapacity];
        _transformByAgent = new Transform[_agentCapacity];
        _controllerByAgent = new CharacterController[_agentCapacity];
        // 조향 상태는 _simState 소유 NativeArray를 별칭으로 캐시한다(별도 할당 없음; 인덱싱은 공유 메모리에 반영).
        _followerVelocity = _simState.FollowerVelocity;
        _leaderYawDeg = _simState.LeaderYawDeg;
        _wanderHeadingDeg = _simState.WanderHeadingDeg;
        _wanderTimer = _simState.WanderTimer;
        _visualPrev = new Vector3[_agentCapacity];
        _visualCur = new Vector3[_agentCapacity];
        _visualRender = new Vector3[_agentCapacity];
        _visualYaw = new float[_agentCapacity]; // 시각 전용(기본 0=스폰 Quaternion.identity의 eulerAngles.y와 일치).
        _visualSpeed01 = new float[_agentCapacity]; // 시각 전용(기본 0=정지 포즈, 첫 Playing 틱에서 갱신).
        _phase01 = new float[_agentCapacity];
        _lastPublishedCounts = new int[_teamCount];
        for (int t = 0; t < _teamCount; t++)
        {
            _lastPublishedCounts[t] = -1; // 첫 coalesced publish가 반드시 나가도록 한다.
        }

        // GPU-anim Stage 1 Chunk B: 팀 색을 sRGB 바이트로 1회 팩한다([0..3]=팀, [4]=중립). 셰이더가 linear로 변환한다.
        _teamPackedColor = new uint[5];
        for (int t = 0; t < 4; t++)
        {
            _teamPackedColor[t] = PackSrgbColor(config.TeamColors[t]);
        }

        _teamPackedColor[4] = PackSrgbColor(config.NeutralColor);

        _rng = new System.Random(config.Seed);
        _countPublisher = EventManager.GetPublisher<CrowdCountChangedEvent>();
        _eliminatedPublisher = EventManager.GetPublisher<CrowdEliminatedEvent>();

        // Phase C 단계 2: 정적 도시 벽의 SDF(WallField)를 세션 수명으로 로드한다(Persistent NativeArray, read-only).
        // asset은 프리팹 직렬화 필드(_wallSdfAsset)로 소유한다. solver 스위치(_useSdfSolver) ON일 때만 조회한다.
        // 미배선(null)은 치명적이지 않다: solver 경로가 CC.Move로 폴백하므로 오류만 남기고 계속한다(스위치 OFF 기본 동작 불변).
        WallSdfAsset wallSdf = _wallSdfAsset;
        if (wallSdf == null)
        {
            Debug.LogError(
                "[CrowdRoot] _wallSdfAsset 직렬화 필드가 비어 있습니다(프리팹 배선 누락). " +
                "SDF solver 스위치가 켜져도 CC.Move로 폴백합니다.");
        }
        else
        {
            try
            {
                _wallField = new WallField();
                _wallField.Load(wallSdf);
            }
            catch (Exception e)
            {
                Debug.LogError("[CrowdRoot] WallField 로드에 실패했습니다(SDF solver는 CC.Move로 폴백): " + e);
                if (_wallField != null)
                {
                    _wallField.Dispose();
                    _wallField = null;
                }
            }
        }

        _initialized = true;
    }

    /// <summary>
    /// 초기 스폰을 동기(한 프레임) 경로로 수행한다. 배치 계산 → 리더 → 중립 전체 → 마무리를 순서대로 호출한다.
    /// 리더와 중립의 모든 배치 좌표를 Instantiate 이전에 먼저 계산해 CheckSphere가 city collider만 보게 한 뒤
    /// (런타임 CharacterController가 아직 없음), prefab clone을 생성하고 마지막에 첫 coalesced 인원 수 publish로 끝난다.
    /// player는 영역 중앙, 라이벌은 15% inset 코너(team id 순), 중립은 seeded System.Random 기각 sampling이다.
    /// 오라클/샷 하네스가 부르는 결정성 계약 경로이며, _rng draw·CheckSphere 순서/횟수는 청크 경로 도입과 무관하게 불변이다.
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

        PrepareSpawnPlacements();
        SpawnLeaders();
        SpawnNeutralRange(0, _placedNeutrals);
        FinalizeSpawn();
    }

    /// <summary>
    /// 인터랙티브 청크(다중 프레임) 스폰을 시작한다. 배치 계산과 리더 생성까지 이 프레임에 수행하고 진행 플래그를 세운다.
    /// 남은 중립은 <see cref="StepChunkedSpawn"/>가 프레임마다 배치 단위로 채운다. 동기 <see cref="SpawnInitial"/>와
    /// 같은 헬퍼를 같은 순서로 호출하므로 최종 상태는 byte-identical하다.
    /// </summary>
    /// <exception cref="InvalidOperationException">Initialize 이전에 호출하면 발생한다.</exception>
    public void BeginChunkedSpawn()
    {
        if (_shutdown)
        {
            return;
        }

        if (!_initialized)
        {
            throw new InvalidOperationException("CrowdRoot.BeginChunkedSpawn은 Initialize 이후에 호출해야 합니다.");
        }

        if (_spawned || _spawnInProgress)
        {
            return;
        }

        PrepareSpawnPlacements();
        SpawnLeaders();
        _neutralCursor = 0;
        _spawnInProgress = true;
    }

    /// <summary>
    /// 청크 스폰을 한 배치(최대 _spawnBatchSize명) 진행한다. 남은 중립을 모두 채우면 <see cref="FinalizeSpawn"/>를
    /// 호출하고 true(완료)를 반환하며, 아직 남았으면 false를 반환한다. 종료(_shutdown) 중이면 즉시 true를 반환한다.
    /// </summary>
    public bool StepChunkedSpawn()
    {
        if (_shutdown)
        {
            return true;
        }

        int n = Mathf.Min(Mathf.Max(1, _spawnBatchSize), _placedNeutrals - _neutralCursor);
        SpawnNeutralRange(_neutralCursor, _neutralCursor + n);
        _neutralCursor += n;
        if (_neutralCursor >= _placedNeutrals)
        {
            FinalizeSpawn();
            _spawnInProgress = false;
            return true;
        }

        return false;
    }

    // 배치 계산 phase: GPU 렌더러 활성화 + 리더/중립의 모든 배치 좌표·헤딩·타이머·스케일을 필드에 스냅샷한다.
    // 이 메서드만 _rng를 뽑고 Physics.CheckSphere를 호출한다(Instantiate 이전이라 CheckSphere는 city collider만 만난다).
    private void PrepareSpawnPlacements()
    {
        // GPU-anim Stage 1 Chunk B: 스위치 ON이면 스폰 루프 이전에 GPU 렌더러를 초기화한다(Init 인자 capacity/teamCount/
        // worldBounds는 스폰 전 이미 확정). 성공(_gpuRenderActive)하면 아래 스폰 루프가 각 유닛을 Instantiate+Init 직후
        // rig를 파괴해 10k rig가 동시에 상주하지 않게 한다(peak 억제). 실패/스위치 OFF/미배선이면 SMR 경로를 그대로 유지한다.
        TryActivateGpuRenderer();

        // ---- 1) 모든 배치 좌표를 Instantiate 이전에 계산한다 ----
        _leaderSpots = new Vector3[_teamCount];
        _leaderSpots[0] = ResolveLeaderSpot(RegionPoint(0.5f, 0.5f));
        for (int t = 1; t < _teamCount; t++)
        {
            Vector2 corner = RivalCornerLerp[t - 1];
            _leaderSpots[t] = ResolveLeaderSpot(RegionPoint(corner.x, corner.y));
        }

        int neutralCount = _neutralCount;
        _neutralSpots = new Vector3[Mathf.Max(1, neutralCount)];
        _neutralHeadings = new float[Mathf.Max(1, neutralCount)];
        _neutralTimers = new float[Mathf.Max(1, neutralCount)];
        _neutralScales = new float[Mathf.Max(1, neutralCount)];
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
                    _neutralSpots[placedNeutrals] = candidate;
                    _neutralHeadings[placedNeutrals] = (float)(_rng.NextDouble() * 360.0);
                    _neutralTimers[placedNeutrals] = Mathf.Lerp(
                        _config.WanderRepickMinSeconds, _config.WanderRepickMaxSeconds, (float)_rng.NextDouble());
                    // 위에서 이미 계산한 스케일 값(_rng·physics 미소비)을 그대로 스냅샷한다. 인스턴스화 phase가 이 값을 읽어 재계산을 없앤다(draw/physics 불변).
                    _neutralScales[placedNeutrals] = neutralScale;
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

        _placedNeutrals = placedNeutrals;
        RejectionRate = totalAttempts > 0 ? (float)rejectedAttempts / totalAttempts : 0f;
        if (skippedNeutrals > 0)
        {
            Debug.LogWarning(
                $"[CrowdRoot] 중립 {skippedNeutrals}명이 {NeutralAttemptMax}회 시도 후에도 유효 위치를 찾지 못해 생략되었습니다. " +
                $"RejectionRate={RejectionRate:F2}");
        }
    }

    // 리더 생성 phase: ≤4명의 리더를 Instantiate하고 CrowdModel·per-agent 배열·buffer에 등록한다(team id=agent id 오름차순).
    // PrepareSpawnPlacements가 스냅샷한 _leaderSpots만 읽으며 _rng draw/physics 질의를 하지 않는다.
    private void SpawnLeaders()
    {
        // ---- 2) 리더 Instantiate + CrowdModel 생성 (team id 오름차순 = agent id 오름차순) ----
        int nextId = 0;
        for (int t = 0; t < _teamCount; t++)
        {
            Vector3 spot = _leaderSpots[t];
            Human leader = SpawnClone(spot, "Human_Leader_" + t);

            // Init/CC 취득은 buffer 등록(_buffer.Add) 이전에 끝낸다. 실패 시 미등록 clone을 파괴하고 다시 던진다.
            CharacterController controller;
            try
            {
                // phase01은 id 해시(Hash01(nextId)=Hash01(_buffer.Id[index]))로 준다. UnityEngine.Random 의존을 없애 스폰을 결정화한다.
                leader.Init(_config.TeamMaterials[t], true, Hash01(nextId));
                controller = GetBakedController(leader);
            }
            catch
            {
                Destroy(leader.gameObject);
                throw;
            }

            int index = _buffer.Add(nextId, t, true, new Vector2(spot.x, spot.z));
            nextId++;

            _humanByAgent[index] = leader;
            _transformByAgent[index] = leader.transform;
            _controllerByAgent[index] = controller;
            _visualPrev[index] = _visualCur[index] = spot; // 첫 프레임 Lerp가 정적이도록 스폰 위치로 시드.
            _leaderYawDeg[t] = 0f;

            // followerCapacity = 전체 agent 용량. 한 crowd가 전원을 흡수해도 FollowerAgentIndices/Followers 재할당이 없도록
            // 넉넉히 사전할당한다(용량만 상향; List 결과/순서/판정 불변). 과거 상수 160은 중립 800에서 tick 중 재할당을 유발했다.
            _crowds.Add(new CrowdModel(t, _config.TeamMaterials[t], leader, index, _agentCapacity));
            if (t == MatchRules.PlayerTeam)
            {
                _playerLeaderTransform = leader.transform;
            }

            // GPU 경로 활성 시 이 clone의 죽은 rig를 즉시 파괴하고(스킨/애니메이터/본 transform 비용 제거) phase를 id 해시로 시드한다.
            if (_gpuRenderActive)
            {
                _phase01[index] = Hash01(_buffer.Id[index]);
                leader.DestroyVisualRig();
            }
        }
    }

    // 중립 생성 phase: [start, endExclusive) 연속 구간의 중립을 Instantiate한다. PrepareSpawnPlacements가 스냅샷한
    // _neutralSpots/_neutralHeadings/_neutralTimers/_neutralScales만 읽으며 _rng draw/physics 질의를 하지 않는다.
    // id·buffer 인덱스는 _teamCount 이후로 단조 증가한다(동기·청크 경로가 같은 순서로 채운다).
    private void SpawnNeutralRange(int start, int endExclusive)
    {
        // ---- 3) 중립 Instantiate ----
        Material neutralMaterial = _config.TeamMaterials[4];
        for (int n = start; n < endExclusive; n++)
        {
            int nextId = _teamCount + n;
            Vector3 spot = _neutralSpots[n];
            Human neutral = SpawnClone(spot, "Human_Neutral_" + n);

            // Init/스케일/CC 취득은 buffer 등록(_buffer.Add) 이전에 끝낸다. 실패 시 미등록 clone을 파괴하고 다시 던진다.
            float scale;
            CharacterController controller;
            try
            {
                // phase01은 id 해시(Hash01(nextId)=Hash01(_buffer.Id[index]))로 준다. UnityEngine.Random 의존을 없애 스폰을 결정화한다.
                neutral.Init(neutralMaterial, false, Hash01(nextId));

                // 스케일은 PrepareSpawnPlacements가 같은 (seed,id)로 계산해 스냅샷한 값이다(clearance 검사 반경과 동일 원천).
                // 시뮬레이션 RNG(_rng)를 소비하지 않아 배회/시뮬레이션 draw 순서가 그대로 유지된다. 발/피벗이 바닥에 있어
                // 스케일이 접지를 보존하고, CharacterController 충돌 캡슐도 lossyScale로 함께 스케일되므로 상수를 따로 스케일하지 않는다.
                // 이 단일 원천이 localScale과 buffer.Scale에 모두 흘러간다.
                scale = _neutralScales[n];
                neutral.transform.localScale = Vector3.one * scale;

                controller = GetBakedController(neutral);
            }
            catch
            {
                Destroy(neutral.gameObject);
                throw;
            }

            // scale을 buffer의 단일 진실 원천에 기록한다(kernel의 스케일 인지 접촉/영입/분리가 buffer.Scale을 읽는다).
            // 영입/팀 변경으로도 스케일은 불변이라 이후 갱신 없음.
            int index = _buffer.Add(nextId, AgentBuffer.NeutralTeam, false, new Vector2(spot.x, spot.z), scale);

            _humanByAgent[index] = neutral;
            _transformByAgent[index] = neutral.transform;
            _controllerByAgent[index] = controller;
            _visualPrev[index] = _visualCur[index] = spot; // 첫 프레임 Lerp가 정적이도록 스폰 위치로 시드.
            _wanderHeadingDeg[index] = _neutralHeadings[n];
            _wanderTimer[index] = _neutralTimers[n];

            // GPU 경로 활성 시 이 clone의 죽은 rig를 즉시 파괴하고(스킨/애니메이터/본 transform 비용 제거) phase를 id 해시로 시드한다.
            if (_gpuRenderActive)
            {
                _phase01[index] = Hash01(_buffer.Id[index]);
                neutral.DestroyVisualRig();
            }
        }
    }

    // 마무리 phase: grid를 한 번 채우고 spawned 플래그를 세운 뒤 첫 coalesced 인원 수 publish로 끝낸다.
    private void FinalizeSpawn()
    {
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

        // 아래 CrowdSimProfiler 호출은 무침습 계측이다(기본 Enabled=false → no-op).
        // 시뮬레이션 값/순서/RNG/이벤트 발행에 영향이 없으며, 켜져 있어도 별도 static 배열에만 기록한다.
        CrowdSimProfiler.Begin(CrowdSimProfiler.Seg.Total);
        if (_matchState == MatchState.Playing)
        {
            // 직전 tick 말에 미러링된 buffer.Pos를 tick 시작 시점에 스냅샷한다. 이동 단계(②~④)의 '직전 tick 위치' 읽기가
            // 이 스냅샷을 보게 해, 이후 단계가 이동 중 buffer.Pos를 저작하더라도 그 읽기가 오염되지 않게 한다.
            NativeArray<Vector2>.Copy(_buffer.Pos, _simState.PrevPos, _buffer.Count);

            // S3 GUARD 0: SDF 이동 경로 활성 여부를 tick당 1회만 캡처한다. 이동 제시 저작(SteerFollowersAndNeutrals)과
            // 미러 모드(MirrorPositionsToBuffer)가 반드시 같은 플래그를 보게 해, 두 단계가 서로 다른 경로로 갈리는 것을 막는다.
            bool sdfActive = IsSdfActive;

            // 직전 프레임 RenderInterpolate가 덮어쓴 시각 위치를 논리 위치(_visualCur)로 되돌린다.
            // 이후 모든 transform 읽기/CC.Move/미러링이 항상 논리 위치를 보게 한다(결정성 보장).
            CrowdSimProfiler.Begin(CrowdSimProfiler.Seg.Restore);
            // S4a: SDF 경로에서는 팔로워/중립 transform을 되돌리지 않는다(이동이 buffer.Pos를 직접 저작하고 sim이 transform을 읽지 않으므로). 리더는 항상 되돌린다(MoveLeaders/카메라/HUD가 live 리더 transform을 읽음). !sdfActive면 전 agent 복원(CC 경로가 transform 사용).
            for (int i = 0; i < _buffer.Count; i++) { if (!sdfActive || _buffer.IsLeader[i]) _transformByAgent[i].position = _visualCur[i]; }
            CrowdSimProfiler.End(CrowdSimProfiler.Seg.Restore);
            CrowdSimProfiler.Begin(CrowdSimProfiler.Seg.Heading);
            UpdateHeadings(dt);                                                          // ②
            CrowdSimProfiler.End(CrowdSimProfiler.Seg.Heading);
            CrowdSimProfiler.Begin(CrowdSimProfiler.Seg.LeaderMove);
            MoveLeaders(dt);                                                             // ③
            CrowdSimProfiler.End(CrowdSimProfiler.Seg.LeaderMove);
            SteerFollowersAndNeutrals(dt, sdfActive);                                    // ④ (FollowerSteer/NeutralMove는 내부에서 계측)
            CrowdSimProfiler.Begin(CrowdSimProfiler.Seg.Mirror);
            MirrorPositionsToBuffer(sdfActive);                                          // ⑤
            CrowdSimProfiler.End(CrowdSimProfiler.Seg.Mirror);
            CrowdSimProfiler.Begin(CrowdSimProfiler.Seg.GridRebuild);
            _grid.Rebuild(_buffer);                                                      // ⑥
            CrowdSimProfiler.End(CrowdSimProfiler.Seg.GridRebuild);
            CrowdSimProfiler.Begin(CrowdSimProfiler.Seg.Recruit);
            _recruitResolver.Resolve(_buffer, _grid, _tuning.RecruitRadius, _tuning.MaxScale, _recruits);  // ⑦
            CrowdSimProfiler.End(CrowdSimProfiler.Seg.Recruit);
            CrowdSimProfiler.Begin(CrowdSimProfiler.Seg.Combat);
            _combatResolver.Resolve(_buffer, _grid, in _tuning, dt, _combatState, _combatOutcome); // ⑧
            CrowdSimProfiler.End(CrowdSimProfiler.Seg.Combat);
            CrowdSimProfiler.Begin(CrowdSimProfiler.Seg.CommitPublish);
            CommitOutcomes();                                                            // ⑨
            CrowdSimProfiler.End(CrowdSimProfiler.Seg.CommitPublish);
        }

        CrowdSimProfiler.Begin(CrowdSimProfiler.Seg.CommitPublish);
        PublishTickEvents();                                                             // ⑩
        CrowdSimProfiler.End(CrowdSimProfiler.Seg.CommitPublish);
        CrowdSimProfiler.End(CrowdSimProfiler.Seg.Total);
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

        int count = _buffer.Count;

        // Playing이 아니면 tick이 prev/cur를 더 이상 갱신하지 않아 alpha가 마지막 tick의 prev→cur 구간을 계속 sawtooth해 무리가 진동한다. 논리 위치(_visualCur)로 스냅해 정적으로 고정한다(시각 전용).
        // S4b2: _visualRender는 항상 채운다(아래 RenderGpuCrowd와 TryGetCrowdRenderBounds가 같은 값을 읽는다). transform.position 쓰기만 게이트한다.
        // GPU 활성 시 팔로워/중립은 _visualRender/_visualYaw로 렌더되고 rig가 파괴돼 transform이 불필요하므로 리더만 쓴다(카메라/HUD가 리더 transform을 읽음).
        // SMR 폴백(!_gpuRenderActive)이면 SMR이 transform으로 렌더하므로 전원 쓴다.
        // (의도된 stranded CC) GPU+SDF 경로에서 팔로워/중립 transform이 동결되면 그들의 베이크 CharacterController도 stale 위치에 남는다. 안전하다:
        // 런타임 물리 질의가 이 유닛 CC의 위치에 의존하지 않는다(배회/AI raycast는 Unit 레이어 제외, CC.Move는 !sdfActive 폴백에서만 실행). 물리 broadphase 정리/CC 제거는 향후 M-sim-3 패스로 미룬다.
        if (_matchState != MatchState.Playing)
        {
            for (int i = 0; i < count; i++)
            {
                _visualRender[i] = _visualCur[i];
                if (!_gpuRenderActive || _buffer.IsLeader[i]) _transformByAgent[i].position = _visualRender[i];
            }
        }
        else
        {
            for (int i = 0; i < count; i++)
            {
                _visualRender[i] = Vector3.Lerp(_visualPrev[i], _visualCur[i], alpha);
                if (!_gpuRenderActive || _buffer.IsLeader[i]) _transformByAgent[i].position = _visualRender[i];
            }
        }

        _visualRenderFilled = true; // TryGetCrowdRenderBounds 가드: _visualRender가 이제 유효하다.

        // GPU-anim Stage 1 Chunk B: 리더 transform(카메라/HUD)은 위에서 갱신했고, 같은 _visualRender 위치에서
        // GPU 인스턴스 버퍼를 병렬로 채워 draw한다. 스위치 OFF/미초기화면 _gpuRenderActive=false라 no-op다.
        if (_gpuRenderActive)
        {
            RenderGpuCrowd(count);
        }
    }

    /// <summary>
    /// 네이티브 렌더 위치(_visualRender)로 크라우드의 world Bounds를 계산해 out으로 돌려준다(Editor harness 카메라 프레이밍 전용).
    /// GPU+SDF 경로에서 팔로워/중립 transform이 동결돼도 프레이밍이 stale transform 대신 최신 렌더 위치를 보게 한다.
    /// _visualRender는 RenderInterpolate가 채우므로 최소 1회 렌더 이후에만 true를 돌려준다. 미스폰/렌더 이전이면 false를
    /// 돌려주고 호출측이 transform 폴백을 쓴다. 시뮬 상태를 바꾸지 않는 읽기 전용 관찰 API다.
    /// </summary>
    public bool TryGetCrowdRenderBounds(out Bounds bounds)
    {
        bounds = default;
        if (!_visualRenderFilled || _visualRender == null || _buffer == null || _buffer.Count <= 0)
        {
            return false;
        }

        Vector3 min = _visualRender[0];
        Vector3 max = _visualRender[0];
        for (int i = 1; i < _buffer.Count; i++)
        {
            min = Vector3.Min(min, _visualRender[i]);
            max = Vector3.Max(max, _visualRender[i]);
        }

        bounds = new Bounds((min + max) * 0.5f, max - min);
        return true;
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

        // GPU-anim Stage 1 Chunk B: GraphicsBuffer를 해제한다(소유자 lifecycle; humanByAgent 미할당 경로의 이른 return 이전).
        // Init되지 않았거나 스위치 OFF여도 안전하다(idempotent, 버퍼 null). 렌더러 OnDestroy가 최종 안전망이다.
        _gpuRenderActive = false;
        if (_crowdRenderer != null)
        {
            _crowdRenderer.Dispose();
        }

        // 벽 SDF의 Persistent NativeArray를 해제한다(소유자 lifecycle에서 해제; humanByAgent 미할당 경로에서도 누수 방지).
        if (_wallField != null)
        {
            _wallField.Dispose();
            _wallField = null;
        }

        // M2-a1: 권한 있는 agent 상태의 Persistent NativeArray를 해제한다(소유자 lifecycle; humanByAgent 미할당 경로의 이른 return 이전, WallField와 동일 규약).
        // idempotent(IsCreated 가드). 별칭 필드(_buffer/_followerVelocity 등)는 여기서 해제하지 않는다(_simState가 유일 소유자).
        if (_simState != null)
        {
            _simState.Dispose();
            _simState = null;
        }

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

    // ---- GPU-anim Stage 1 Chunk B: 인스턴스 렌더 경로(스위치 ON일 때만 활성) ----

    // 스위치 ON이면 스폰 루프 이전에 GPU 렌더러를 초기화한다(Init 인자 capacity/teamCount/worldBounds는 스폰 전 이미 확정).
    // 성공(그래픽 device/capability 충족 + 저작 자원 배선)했을 때에만 _gpuRenderActive=true로 두고 per-frame 스크래치를 할당한다.
    // per-agent phase 시드와 각 clone의 rig 파괴는 이후 스폰 루프가 유닛별로 수행한다(10k rig 동시 상주 방지, peak 억제).
    // 실패하면 SMR 경로를 그대로 유지한다(SMR을 절대 끄지 않는다). 스위치 OFF/미배선이면 아무 것도 하지 않는다.
    private void TryActivateGpuRenderer()
    {
        if (!_config.UseGpuCrowdRenderer || _crowdRenderer == null || _shutdown)
        {
            return;
        }

        // 인스턴스 draw의 world bounds: walkable region XZ + 유닛 키 여유(union VAT 높이 ~1.9m를 넉넉히 덮는다).
        Vector3 boundsCenter = new Vector3(
            (_regionMinX + _regionMaxX) * 0.5f, _groundY + 1.5f, (_regionMinZ + _regionMaxZ) * 0.5f);
        Vector3 boundsSize = new Vector3(
            (_regionMaxX - _regionMinX) + 4f, 6f, (_regionMaxZ - _regionMinZ) + 4f);
        _crowdWorldBounds = new Bounds(boundsCenter, boundsSize);

        _gpuRenderActive = _crowdRenderer.Init(_agentCapacity, _teamCount, _crowdWorldBounds);
        if (!_gpuRenderActive)
        {
            return; // 초기화 실패(무device/미지원/미배선): SMR 경로 유지.
        }

        _instanceScratch = new CrowdRenderer.InstanceData[_agentCapacity];
        _leaderScratch = new int[_teamCount];
    }

    // 렌더 프레임마다 per-agent phase를 적분하고(dt*speed01/주기) 렌더 위치/yaw/scale/색을 인스턴스 스크래치에 채운 뒤
    // 렌더러에 업로드+draw를 위임한다. 리더 index를 모아 그림자 전용 draw로 넘긴다. transform은 위(RenderInterpolate)에서 이미 갱신됐다.
    private void RenderGpuCrowd(int count)
    {
        float dt = Time.deltaTime;
        float invPeriod = 1f / WalkPeriodSeconds;
        int leaderCount = 0;

        for (int i = 0; i < count; i++)
        {
            // phase 적분: Animator가 재생 속도 speed01로 매 프레임 전진하던 것과 동일하게 프레임 실시간으로 누적한다.
            float p = _phase01[i] + dt * _visualSpeed01[i] * invPeriod;
            p -= Mathf.Floor(p); // frac → [0,1)
            _phase01[i] = p;

            Vector3 pos = _visualRender[i];                  // 위에서 쓴 렌더 위치(Lerp 또는 snap)를 그대로 읽는다(transform 재읽기 없이 CPU 경로와 동일).
            float yawRad = _visualYaw[i] * Mathf.Deg2Rad;    // 네이티브 yaw(SetHeadingAndSpeed와 동일 값)를 읽는다. yaw는 보간하지 않는다(SMR 경로도 회전 미보간).

            CrowdRenderer.InstanceData d;
            d.Pos = pos;
            d.Yaw = yawRad;
            d.Scale = _buffer.Scale[i];
            d.Phase01 = p;
            d.PackedColor = _teamPackedColor[TeamColorIndex(_buffer.Team[i])];
            _instanceScratch[i] = d;

            if (_buffer.IsLeader[i] && leaderCount < _leaderScratch.Length)
            {
                _leaderScratch[leaderCount++] = i;
            }
        }

        _crowdRenderer.Render(_instanceScratch, count, _leaderScratch, leaderCount);
    }

    // team → _teamPackedColor 인덱스. 중립(-1)은 4번, 팀 0..3은 그대로.
    private static int TeamColorIndex(int team)
    {
        return team < 0 ? 4 : team;
    }

    // sRGB 0..1 Color를 R|G<<8|B<<16 바이트로 팩한다(셰이더가 linear로 변환). alpha는 쓰지 않는다.
    private static uint PackSrgbColor(Color c)
    {
        uint r = (uint)Mathf.Clamp(Mathf.RoundToInt(c.r * 255f), 0, 255);
        uint g = (uint)Mathf.Clamp(Mathf.RoundToInt(c.g * 255f), 0, 255);
        uint b = (uint)Mathf.Clamp(Mathf.RoundToInt(c.b * 255f), 0, 255);
        return r | (g << 8) | (b << 16);
    }

    // agent id의 결정적 정수 해시 → [0,1) phase 시드(splitmix32 스타일; UnityEngine.Random.value 대체).
    private static float Hash01(int id)
    {
        unchecked
        {
            uint x = (uint)id * 0x9E3779B1u + 0x85EBCA77u;
            x ^= x >> 16;
            x *= 0x7FEB352Du;
            x ^= x >> 15;
            x *= 0x846CA68Bu;
            x ^= x >> 16;
            return (x >> 8) * (1f / 16777216f); // 상위 24비트를 [0,1)로 매핑한다.
        }
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

    // prefab clone을 생성/활성화하고 baking된 Human component를 반환한다(없으면 명확히 실패). 런타임 AddComponent는 하지 않는다.
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
            // buffer 등록 전 미등록 clone이므로 여기서 파괴하고 실패한다(결정론 buffer/배열에 잔존시키지 않음).
            Destroy(clone);
            throw new InvalidOperationException(
                $"[CrowdRoot] Human prefab '{_humanPrefab.name}' 루트에 Human 컴포넌트가 baking되어 있지 않습니다. " +
                "GameSceneSetup으로 prefab을 수렴시키세요.");
        }

        return human;
    }

    // 공유 프리팹에 baking된 CharacterController(enabled)를 취득한다. 리더/팔로워/중립 전원이 같은 CC 스펙을 쓴다.
    // 런타임 스펙 수리는 하지 않는다(스펙 상수 일치는 Editor 검증기가 강제). baking되어 있지 않으면 명확히 실패한다.
    private static CharacterController GetBakedController(Human human)
    {
        CharacterController controller = human.GetComponent<CharacterController>();
        if (controller == null)
        {
            throw new InvalidOperationException(
                "[CrowdRoot] Human prefab 루트에 CharacterController가 baking되어 있지 않습니다. " +
                "GameSceneSetup으로 prefab을 수렴시키세요.");
        }

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
                model.HeadingDeg = _aiDrivers[t].DecideHeadingDeg(model, _crowds, _buffer, _simState.PrevPos, _grid);
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
                CrowdSimProfiler.Begin(CrowdSimProfiler.Seg.CCMove);
                ApplyHorizontalMove(index, delta);
                CrowdSimProfiler.End(CrowdSimProfiler.Seg.CCMove);

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
            float leaderSpeed01 = moving ? 1f : 0f;
            _visualSpeed01[index] = leaderSpeed01; // 시각 전용(GPU phase 적분용).
            _visualYaw[index] = Quaternion.Euler(0f, yaw, 0f).eulerAngles.y; // 렌더용 정규화 yaw(SetHeadingAndSpeed와 동일 값).
            _humanByAgent[index].SetHeadingAndSpeed(yaw, leaderSpeed01);
        }
    }

    // ④ 팔로워를 가속 제한 arrive 조향으로 리더 뒤 blob에 모으고(CC.Move로 충돌), 중립을 배회시킨다.
    // sdfActive는 SimTick이 tick당 1회 캡처해 넘긴 플래그다(미러 모드와 반드시 동일한 값).
    private void SteerFollowersAndNeutrals(float dt, bool sdfActive)
    {
        CrowdSimProfiler.Begin(CrowdSimProfiler.Seg.FollowerSteer);
        CrowdSimProfiler.Begin(CrowdSimProfiler.Seg.FollowerPrepass);
        float sepRadius = _config.SeparationRadius;
        float sepPush = _config.SeparationPush;
        float maxSpeed = _config.FollowerMaxSpeed;
        float cohesionGain = _config.FollowerCohesionGain;
        float arriveRadius = _config.FollowerArriveRadius;
        float maxAccel = _config.FollowerMaxAccel;
        float trailingOffset = _config.FollowerTrailingOffset;

        // ── M2-a3: 팔로워 FORCE(리더 뒤 중심으로의 arrive + 같은 팀 분리 + 가속 제한 적분)를 Burst IJobParallelFor로 병렬화한다.
        //    (1) 직렬 프리패스: team별 arrive 중심/유효 반경을 이번 tick 리더 위치로 계산하고, 팔로워 작업 리스트를 team·f 오름차순으로 평탄화하며,
        //        grid를 native로 스냅샷한다. (2) 병렬 job: 팔로워별 명령 속도를 산출한다. (3) 직렬 이동: 명령 속도로 CC/SDF 이동 후 read-back·속도 재조정.
        //    직렬 등가 근거: 이동 전이라 팔로워 자기 위치는 transform==buffer.Pos(월드 아이덴티티), 이웃/거리 합은 불변 snapshot을 grid 방출 순서 그대로 누적,
        //    각 Execute는 자기 슬롯에만 쓰므로 parallel-for 인덱스 순서와 무관. math/Mathf 치환 없음. job은 Burst(FloatMode.Strict)라 관리형 직렬 경로와 near-Mono지만 bit-identical하지는 않다.
        //
        // 이동(MOVE): SDF 경로 활성(_useSdfSolver ON + WallField 로드)이면 순수 SDF 수학이라 FollowerSdfMoveJob으로 병렬화한다(read-only 조회, 자기 슬롯만 쓰기).
        //    CC fallback은 CharacterController.Move가 main-thread 물리라 병렬화하지 않고 기존 직렬 이동을 그대로 유지한다.
        //    sdfActive는 SimTick이 캡처해 파라미터로 넘긴다(GUARD 0: 미러 모드와 동일 플래그).

        // LeaderRadial 분리 모드일 때만 O(N) 프리컴퓨트(팀 무리 중심 + per-bucket per-team 점유)를 돌린다. Pairwise는 이 블록을 전부 건너뛴다(추가 O(N) 비용 0).
        bool leaderRadial = _config.CrowdSeparationMode == GameConfigSO.SeparationMode.LeaderRadial;
        int teamCount = _teamCount;
        float gridInvCellSize = _grid.InvCellSize;
        int gridTableMask = _grid.TableMask;
        if (leaderRadial)
        {
            // 결정성 가드: per-bucket per-team 점유 카운트를 이번 tick 처음부터 다시 채우려 매 tick 0으로 초기화한다.
            for (int i = 0; i < _simState.BucketTeamCount.Length; i++)
            {
                _simState.BucketTeamCount[i] = 0;
            }
        }

        int followerCount = 0;
        for (int t = 0; t < _teamCount; t++)
        {
            CrowdModel model = _crowds[t];
            if (model.Eliminated)
            {
                continue;
            }

            Vector3 leaderPos = _transformByAgent[model.LeaderAgentIndex].position;
            Vector2 leaderXZ = new Vector2(leaderPos.x, leaderPos.z);
            // yaw→방향 매핑((sin,cos))이 MoveLeaders 규약과 일치하도록 팀당 한 번만 sin/cos를 구해 재사용한다.
            float leaderRad = _leaderYawDeg[t] * Mathf.Deg2Rad;
            float sinYaw = Mathf.Sin(leaderRad);
            float cosYaw = Mathf.Cos(leaderRad);
            Vector2 leaderForward = new Vector2(sinYaw, cosYaw);
            Vector2 center = leaderXZ - leaderForward * trailingOffset; // 리더 진행 방향 뒤의 단일 중심점.
            List<int> followerIndices = model.FollowerAgentIndices;
            // 무리 크기에 따라 arrive 반경을 √인원에 비례해 키운다(footprint ∝ √N, areal packing 근사). 팀당 1회만 계산한다.
            float effectiveArriveRadius = arriveRadius + _config.FollowerArriveRadiusPerSqrtMember * Mathf.Sqrt(followerIndices.Count);
            _simState.CenterPerTeam[t] = center;
            _simState.ArriveRadiusPerTeam[t] = effectiveArriveRadius;

            // LeaderRadial: 팀 무리 중심(centroid)과 per-bucket per-team 점유를 직전 tick 위치(buffer.Pos)로 고정 team·f 순서로 누적한다(결정성 가드: 고정 순서). 리더 먼저, 이어서 f 오름차순 팔로워.
            Vector2 centroidSum = Vector2.zero;
            int centroidCount = 0;
            if (leaderRadial)
            {
                Vector2 leaderPrev = _simState.PrevPos[model.LeaderAgentIndex];
                centroidSum = leaderPrev;
                centroidCount = 1;
                int lcx = Mathf.FloorToInt(leaderPrev.x * gridInvCellSize);
                int lcy = Mathf.FloorToInt(leaderPrev.y * gridInvCellSize);
                _simState.BucketTeamCount[(SpatialGrid.HashCell(lcx, lcy) & gridTableMask) * teamCount + t]++;
            }

            for (int f = 0; f < followerIndices.Count; f++)
            {
                int followerIndex = followerIndices[f];
                _simState.FollowerList[followerCount] = followerIndex;

                if (sdfActive)
                {
                    // 이동 job이 쓸 현재 위치를 tick 시작 PrevPos에서 취한다(Restore 후 transform.position과 bit-identical, S4a: SDF 경로 transform 읽기 제거).
                    _simState.MovePositionCurrent[followerCount] = new Vector2(_simState.PrevPos[followerIndex].x, _simState.PrevPos[followerIndex].y);
                }

                if (leaderRadial)
                {
                    Vector2 followerPrev = _simState.PrevPos[followerIndex];
                    centroidSum += followerPrev;
                    centroidCount++;
                    int fcx = Mathf.FloorToInt(followerPrev.x * gridInvCellSize);
                    int fcy = Mathf.FloorToInt(followerPrev.y * gridInvCellSize);
                    _simState.BucketTeamCount[(SpatialGrid.HashCell(fcx, fcy) & gridTableMask) * teamCount + t]++;
                }

                followerCount++;
            }

            if (leaderRadial)
            {
                _simState.CentroidPerTeam[t] = centroidSum / centroidCount;
            }
        }
        CrowdSimProfiler.End(CrowdSimProfiler.Seg.FollowerPrepass);

        // 이번 tick 팔로워 루프가 보는 grid(== 직전 tick 끝에서 rebuild된 상태)를 native로 스냅샷한다. job이 QueryCircle/QueryCircleCapped 열거를 재현한다.
        CrowdSimProfiler.Begin(CrowdSimProfiler.Seg.FollowerGridSnapshot);
        _grid.CopyNativeSnapshot(
            _simState.GridBucketHead, _simState.GridNext, _simState.GridCellX, _simState.GridCellY, _simState.GridPos);
        CrowdSimProfiler.End(CrowdSimProfiler.Seg.FollowerGridSnapshot);

        var forceJob = new SteeringForceJob
        {
            FollowerList = _simState.FollowerList,
            CenterPerTeam = _simState.CenterPerTeam,
            ArriveRadiusPerTeam = _simState.ArriveRadiusPerTeam,
            Team = _buffer.Team,
            Scale = _buffer.Scale,
            Pos = _simState.PrevPos,
            FollowerVelocityIn = _followerVelocity,
            BucketHead = _simState.GridBucketHead,
            NextInBucket = _simState.GridNext,
            CellX = _simState.GridCellX,
            CellY = _simState.GridCellY,
            GridPos = _simState.GridPos,
            TableMask = _grid.TableMask,
            GridCount = _grid.Count,
            InvCellSize = _grid.InvCellSize,
            SepRadius = sepRadius,
            SepPush = sepPush,
            MaxSpeed = maxSpeed,
            CohesionGain = cohesionGain,
            MaxAccel = maxAccel,
            QueryRadius = sepRadius * Mathf.Max(1f, _config.NeutralMaxScale),
            SepBudget = _config.Sim.SeparationVisitBudget,
            Dt = dt,
            // 분리 모드 + LeaderRadial 스냅샷. Pairwise(=0)에서는 job이 아래 4필드/2배열을 읽지 않으므로 값은 무시된다(job 안전 시스템 충족용으로 항상 유효 배열을 배선).
            SeparationMode = (int)_config.CrowdSeparationMode,
            OvercrowdThreshold = _config.OvercrowdThreshold,
            RadialGain = _config.RadialSeparationGain,
            TangentialFraction = _config.SeparationTangentialFraction,
            BucketTeamCount = _simState.BucketTeamCount,
            TeamCount = _teamCount,
            CentroidPerTeam = _simState.CentroidPerTeam,
            Id = _buffer.Id,
            CommandedVelocity = _simState.CommandedVelocity,
        };
        JobHandle forceHandle = forceJob.Schedule(followerCount, SteeringForceBatch);
        _lastScheduledFollowerCount = followerCount; // 이번 tick 스케줄된 팔로워 수(force/move job 공통). harness의 0-팔로워 degenerate 단언용.

        if (sdfActive)
        {
            // ── SDF 이동 병렬화: 팔로워별 SDF 해소 → walkable clamp → 실제 변위 기반 속도 재조정 → blocked-damping을 FollowerSdfMoveJob으로 산출한다.
            //    각 팔로워는 자기 슬롯에만 쓰고 WallField를 read-only 조회하므로 직렬 등가다(job은 Burst Strict라 near-Mono지만 bit-identical하지는 않다). 이동의 제시(transform/애니메이션)는 아래 직렬 패스가 수행한다.
            WallFieldView wallView = _wallField.AsView();
            var moveJob = new FollowerSdfMoveJob
            {
                FollowerList = _simState.FollowerList,
                PositionCurrent = _simState.MovePositionCurrent,
                CommandedVelocity = _simState.CommandedVelocity,
                Scale = _buffer.Scale,
                WallDist = wallView.Dist,
                WallOriginX = wallView.OriginX,
                WallOriginZ = wallView.OriginZ,
                WallCellSize = wallView.CellSize,
                WallInvCellSize = wallView.InvCellSize,
                WallCols = wallView.Cols,
                WallRows = wallView.Rows,
                WallMaxDistance = wallView.MaxDistance,
                WallBilinearBias = wallView.BilinearBias,
                Dt = dt,
                WallClearance = WallCollisionClearance,
                RegionMinX = _regionMinX,
                RegionMaxX = _regionMaxX,
                RegionMinZ = _regionMinZ,
                RegionMaxZ = _regionMaxZ,
                BlockedDamping = _config.FollowerBlockedDamping,
                PositionNext = _simState.MovePositionNext,
                VelocityOut = _simState.MoveVelocityOut,
            };
            CrowdSimProfiler.Begin(CrowdSimProfiler.Seg.CCMove); // SDF ON: 이 구간은 WallSolver.Resolve(SDF 이동 해소)의 병렬 벽시계다.
            CrowdSimProfiler.Begin(CrowdSimProfiler.Seg.FollowerJobWait);
            moveJob.Schedule(followerCount, SteeringForceBatch, forceHandle).Complete();
            CrowdSimProfiler.End(CrowdSimProfiler.Seg.FollowerJobWait);
            CrowdSimProfiler.End(CrowdSimProfiler.Seg.CCMove);

            // 직렬 제시 패스(프리패스와 동일 순서 team·f 오름차순): job 산출 위치/속도를 transform·시각 배열·애니메이션에 반영한다(managed, main-thread).
            CrowdSimProfiler.Begin(CrowdSimProfiler.Seg.FollowerPresent);
            int followerSlot = 0;
            for (int t = 0; t < _teamCount; t++)
            {
                CrowdModel model = _crowds[t];
                if (model.Eliminated)
                {
                    continue;
                }

                List<int> followerIndices = model.FollowerAgentIndices;
                for (int f = 0; f < followerIndices.Count; f++)
                {
                    int index = followerIndices[f];
                    Transform followerTransform = _transformByAgent[index];

                    Vector2 finalXZ = _simState.MovePositionNext[followerSlot];
                    Vector2 velocity = _simState.MoveVelocityOut[followerSlot];
                    followerSlot++;

                    // S3: 이동 결과(job이 해소·clamp한 위치, 항상 (x, groundY, z))를 buffer.Pos에 직접 저작한다. SDF 경로는
                    // transform을 미러링하지 않으므로 여기서 transform.position은 쓰지 않는다(RenderInterpolate가 렌더 프레임에만 쓴다).
                    // finalXZ는 예전에 transform에 쓰던 XZ와 동일하고, _visualPrev는 이번 tick 시작의 PrevPos(=직전 tick 미러 위치,
                    // Restore가 transform에 되돌린 값과 bit-identical)로 만든다.
                    _buffer.Pos[index] = finalXZ;
                    _visualPrev[index] = new Vector3(_simState.PrevPos[index].x, _groundY, _simState.PrevPos[index].y); // prev = 이동 전 논리 위치.
                    _visualCur[index] = new Vector3(finalXZ.x, _groundY, finalXZ.y); // cur = 이동+clamp 후 논리 위치.
                    _followerVelocity[index] = velocity;

                    float speed = velocity.magnitude;
                    _visualSpeed01[index] = 1f; // 팔로워는 항상 1(시각 전용, GPU phase 적분용).
                    if (speed > 0.001f)
                    {
                        _visualYaw[index] = Quaternion.Euler(0f, Mathf.Atan2(velocity.x, velocity.y) * Mathf.Rad2Deg, 0f).eulerAngles.y; // 렌더용 정규화 yaw(SetHeadingAndSpeed와 동일 값).
                        if (!_gpuRenderActive) // GPU 경로는 _visualYaw/_visualSpeed01로 렌더하므로 이 transform.rotation·Animator 쓰기는 dead다.
                        {
                            _humanByAgent[index].SetHeadingAndSpeed(
                                Mathf.Atan2(velocity.x, velocity.y) * Mathf.Rad2Deg, 1f);
                        }
                    }
                    else
                    {
                        float headingDeg = _visualYaw[index]; // 정지 시 저장된 yaw 유지(과거 transform.eulerAngles.y 읽기 대체).
                        _visualYaw[index] = Quaternion.Euler(0f, headingDeg, 0f).eulerAngles.y; // 정규화 재적용(멱등).
                        if (!_gpuRenderActive) // GPU 경로는 _visualYaw/_visualSpeed01로 렌더하므로 이 transform.rotation·Animator 쓰기는 dead다.
                        {
                            _humanByAgent[index].SetHeadingAndSpeed(headingDeg, 1f);
                        }
                    }
                }
            }
            CrowdSimProfiler.End(CrowdSimProfiler.Seg.FollowerPresent);
        }
        else
        {
            forceHandle.Complete(); // 직렬 등가(교차 agent 쓰기 없음). CC.Move는 main-thread 물리라 이동은 직렬 유지.

            // 직렬 이동: 프리패스와 동일 순서(team·f 오름차순)로 팔로워를 돌며 job 산출 명령 속도로 CC.Move/되읽기/속도 재조정을 그대로 수행한다.
            int followerSlot = 0;
            for (int t = 0; t < _teamCount; t++)
            {
                CrowdModel model = _crowds[t];
                if (model.Eliminated)
                {
                    continue;
                }

                List<int> followerIndices = model.FollowerAgentIndices;
                for (int f = 0; f < followerIndices.Count; f++)
                {
                    int index = followerIndices[f];
                    Transform followerTransform = _transformByAgent[index];
                    Vector3 current = followerTransform.position;
                    _visualPrev[index] = current; // prev = 이동 전 논리 위치.
                    Vector2 pos = new Vector2(current.x, current.z);

                    Vector2 velocity = _simState.CommandedVelocity[followerSlot]; // job이 산출한 이동 이전 명령 속도(직렬 적분 결과와 near-Mono; Burst Strict라 bit-identical 아님).
                    followerSlot++;

                    // CC.Move로 이동해 건물 collider와 충돌시킨다(수평 delta만; Y는 아래에서 다시 고정).
                    Vector2 move = velocity * dt;
                    Vector2 commandedVel = velocity; // Move 직전의 명령 속도(아래 속도 재조정에서 방향 기준으로 사용).
                    CrowdSimProfiler.Begin(CrowdSimProfiler.Seg.CCMove);
                    ApplyHorizontalMove(index, new Vector3(move.x, 0f, move.y));
                    CrowdSimProfiler.End(CrowdSimProfiler.Seg.CCMove);

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
                    _visualSpeed01[index] = 1f; // 팔로워는 항상 1(시각 전용, GPU phase 적분용).
                    if (speed > 0.001f)
                    {
                        _visualYaw[index] = Quaternion.Euler(0f, Mathf.Atan2(velocity.x, velocity.y) * Mathf.Rad2Deg, 0f).eulerAngles.y; // 렌더용 정규화 yaw(SetHeadingAndSpeed와 동일 값).
                        if (!_gpuRenderActive) // GPU 경로는 _visualYaw/_visualSpeed01로 렌더하므로 이 transform.rotation·Animator 쓰기는 dead다.
                        {
                            _humanByAgent[index].SetHeadingAndSpeed(
                                Mathf.Atan2(velocity.x, velocity.y) * Mathf.Rad2Deg, 1f);
                        }
                    }
                    else
                    {
                        float headingDeg = _visualYaw[index]; // 정지 시 저장된 yaw 유지(과거 transform.eulerAngles.y 읽기 대체).
                        _visualYaw[index] = Quaternion.Euler(0f, headingDeg, 0f).eulerAngles.y; // 정규화 재적용(멱등).
                        if (!_gpuRenderActive) // GPU 경로는 _visualYaw/_visualSpeed01로 렌더하므로 이 transform.rotation·Animator 쓰기는 dead다.
                        {
                            _humanByAgent[index].SetHeadingAndSpeed(headingDeg, 1f);
                        }
                    }
                }
            }
        }

        CrowdSimProfiler.End(CrowdSimProfiler.Seg.FollowerSteer);

        // 중립 배회: 2~5초마다 seeded rng로 방향을 재선택하고 이동(SDF ON: NeutralSdfMoveJob 병렬 / OFF: CC.Move 직렬)한 뒤 영역 안으로 clamp한다.
        CrowdSimProfiler.Begin(CrowdSimProfiler.Seg.NeutralMove);
        float wanderSpeed = _config.NeutralWanderSpeed;
        int agentCount = _buffer.Count;
        if (sdfActive)
        {
            // ── Stage A: SDF 경로 활성이면 순수 SDF 수학이라 NeutralSdfMoveJob으로 병렬화한다(read-only 조회, 자기 슬롯만 쓰기 → 직렬 등가; job은 Burst Strict라 near-Mono지만 bit-identical하지는 않다).
            //    (1) 직렬 프리패스: 원래 agent-index 오름차순 그대로 배회 타이머 감산·방향 재선택(shared _rng draw + Physics.Raycast는 반드시 직렬·순서 유지)과 Mathf.Sin/Cos 명령 변위 산출을 하고 작업 리스트를 평탄화한다.
            //    (2) 병렬 job: 중립별 SDF 해소+walkable clamp를 산출한다. (3) 직렬 제시: transform/시각 배열/애니메이션에 반영한다.
            CrowdSimProfiler.Begin(CrowdSimProfiler.Seg.NeutralPrepass);
            int neutralCount = 0;
            for (int i = 0; i < agentCount; i++)
            {
                if (_buffer.Team[i] != AgentBuffer.NeutralTeam)
                {
                    continue;
                }

                _wanderTimer[i] -= dt;
                if (_wanderTimer[i] <= 0f)
                {
                    RepickWanderHeading(i); // shared _rng draw + Physics.Raycast. 반드시 직렬·agent-index 오름차순 유지(결정성).
                }

                int k = neutralCount;
                _simState.NeutralList[k] = i;
                _simState.NeutralMovePositionCurrent[k] = new Vector2(_simState.PrevPos[i].x, _simState.PrevPos[i].y); // 이동 전 위치를 PrevPos에서 취한다(Restore 후 transform.position과 bit-identical, S4a).
                float rad = _wanderHeadingDeg[i] * Mathf.Deg2Rad;
                Vector3 d = new Vector3(Mathf.Sin(rad), 0f, Mathf.Cos(rad)) * (wanderSpeed * dt); // Mathf.Sin/Cos는 여기(직렬)서 산출, job 안이 아님.
                _simState.NeutralCommandedDelta[k] = new Vector2(d.x, d.z);
                CrowdSimCounters.CountSdfResolve(); // 원본 SDF 분기와 동일: 직렬로 중립당 1회(무침습 계측; Enabled=false면 no-op).
                neutralCount++;
            }
            CrowdSimProfiler.End(CrowdSimProfiler.Seg.NeutralPrepass);

            if (neutralCount > 0)
            {
                WallFieldView wallView = _wallField.AsView();
                var moveJob = new NeutralSdfMoveJob
                {
                    NeutralList = _simState.NeutralList,
                    PositionCurrent = _simState.NeutralMovePositionCurrent,
                    CommandedDelta = _simState.NeutralCommandedDelta,
                    Scale = _buffer.Scale,
                    WallDist = wallView.Dist,
                    WallOriginX = wallView.OriginX,
                    WallOriginZ = wallView.OriginZ,
                    WallCellSize = wallView.CellSize,
                    WallInvCellSize = wallView.InvCellSize,
                    WallCols = wallView.Cols,
                    WallRows = wallView.Rows,
                    WallMaxDistance = wallView.MaxDistance,
                    WallBilinearBias = wallView.BilinearBias,
                    WallClearance = WallCollisionClearance,
                    RegionMinX = _regionMinX,
                    RegionMaxX = _regionMaxX,
                    RegionMinZ = _regionMinZ,
                    RegionMaxZ = _regionMaxZ,
                    PositionNext = _simState.NeutralMovePositionNext,
                };
                CrowdSimProfiler.Begin(CrowdSimProfiler.Seg.CCMove); // SDF ON: 이 구간은 WallSolver.Resolve(중립 SDF 이동 해소)의 병렬 벽시계다.
                CrowdSimProfiler.Begin(CrowdSimProfiler.Seg.NeutralJobWait);
                moveJob.Schedule(neutralCount, SteeringForceBatch).Complete();
                CrowdSimProfiler.End(CrowdSimProfiler.Seg.NeutralJobWait);
                CrowdSimProfiler.End(CrowdSimProfiler.Seg.CCMove);
            }

            // 직렬 제시 패스(프리패스와 동일 순서 = agent-index 오름차순): job 산출 위치를 transform·시각 배열·애니메이션에 반영한다(managed, main-thread).
            CrowdSimProfiler.Begin(CrowdSimProfiler.Seg.NeutralPresent);
            for (int k = 0; k < neutralCount; k++)
            {
                int i = _simState.NeutralList[k];

                Vector2 fx = _simState.NeutralMovePositionNext[k];
                // S3: 이동 결과(job이 해소·clamp한 위치, 항상 (x, groundY, z))를 buffer.Pos에 직접 저작한다. SDF 경로는
                // transform을 미러링하지 않으므로 여기서 transform.position은 쓰지 않는다(RenderInterpolate가 렌더 프레임에만 쓴다).
                // _visualPrev는 이번 tick 시작의 PrevPos(=직전 tick 미러 위치, Restore가 transform에 되돌린 값과 bit-identical)로 만든다.
                _buffer.Pos[i] = new Vector2(fx.x, fx.y);
                _visualPrev[i] = new Vector3(_simState.PrevPos[i].x, _groundY, _simState.PrevPos[i].y); // prev = 이동 전 논리 위치.
                _visualCur[i] = new Vector3(fx.x, _groundY, fx.y); // cur = 이동+clamp 후 논리 위치.
                // GPU phase 적분에 쓰는 저장 속도를 CPU 경로(Human.SetHeadingAndSpeed의 Animator.speed clamp)와 동일하게 clamp해
                // 범위 밖 config 값에서도 플래그와 무관하게 동일하게 동작시킨다.
                _visualSpeed01[i] = Mathf.Clamp(_config.NeutralAnimationSpeed, 0f, Human.MaxAnimatorSpeed); // 시각 전용(GPU phase 적분용).
                _visualYaw[i] = Quaternion.Euler(0f, _wanderHeadingDeg[i], 0f).eulerAngles.y; // 렌더용 정규화 yaw(SetHeadingAndSpeed와 동일 값).
                if (!_gpuRenderActive) // GPU 경로는 _visualYaw/_visualSpeed01로 렌더하므로 이 transform.rotation·Animator 쓰기는 dead다.
                {
                    _humanByAgent[i].SetHeadingAndSpeed(_wanderHeadingDeg[i], _config.NeutralAnimationSpeed);
                }
            }
            CrowdSimProfiler.End(CrowdSimProfiler.Seg.NeutralPresent);
        }
        else
        {
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
                CrowdSimProfiler.Begin(CrowdSimProfiler.Seg.CCMove);
                ApplyHorizontalMove(i, new Vector3(Mathf.Sin(rad), 0f, Mathf.Cos(rad)) * (wanderSpeed * dt));
                CrowdSimProfiler.End(CrowdSimProfiler.Seg.CCMove);

                Vector3 pos = neutralTransform.position;
                float nx = Mathf.Clamp(pos.x, _regionMinX, _regionMaxX);
                float nz = Mathf.Clamp(pos.z, _regionMinZ, _regionMaxZ);
                if (nx != pos.x || nz != pos.z || pos.y != _groundY)
                {
                    neutralTransform.position = new Vector3(nx, _groundY, nz);
                }

                _visualCur[i] = neutralTransform.position; // cur = 이동+clamp 후 논리 위치.
                // GPU phase 적분에 쓰는 저장 속도를 CPU 경로(Human.SetHeadingAndSpeed의 Animator.speed clamp)와 동일하게 clamp해
                // 범위 밖 config 값에서도 플래그와 무관하게 동일하게 동작시킨다.
                _visualSpeed01[i] = Mathf.Clamp(_config.NeutralAnimationSpeed, 0f, Human.MaxAnimatorSpeed); // 시각 전용(GPU phase 적분용).
                _visualYaw[i] = Quaternion.Euler(0f, _wanderHeadingDeg[i], 0f).eulerAngles.y; // 렌더용 정규화 yaw(SetHeadingAndSpeed와 동일 값).
                if (!_gpuRenderActive) // GPU 경로는 _visualYaw/_visualSpeed01로 렌더하므로 이 transform.rotation·Animator 쓰기는 dead다.
                {
                    _humanByAgent[i].SetHeadingAndSpeed(_wanderHeadingDeg[i], _config.NeutralAnimationSpeed);
                }
            }
        }

        CrowdSimProfiler.End(CrowdSimProfiler.Seg.NeutralMove);
    }

    // 중립의 새 배회 방향을 고른다. 1.5m raycast가 막히는 방향은 기각하고 최대 8회 재시도한다.
    private void RepickWanderHeading(int agentIndex)
    {
        _wanderTimer[agentIndex] = Mathf.Lerp(
            _config.WanderRepickMinSeconds, _config.WanderRepickMaxSeconds, (float)_rng.NextDouble());

        // S4a: SDF 경로가 중립 transform을 더는 Restore하지 않으므로 raycast 원점을 PrevPos에서 취한다(양 경로 모두 Restore 후 transform.position과 bit-identical).
        Vector3 origin = new Vector3(_simState.PrevPos[agentIndex].x, _groundY, _simState.PrevPos[agentIndex].y) + Vector3.up * WanderRayHeight;
        for (int attempt = 0; attempt < WanderRepickTries; attempt++)
        {
            float heading = (float)(_rng.NextDouble() * 360.0);
            float rad = heading * Mathf.Deg2Rad;
            Vector3 dir = new Vector3(Mathf.Sin(rad), 0f, Mathf.Cos(rad));
            if (!Physics.Raycast(origin, dir, WanderRayDistance, _wallProbeMask))
            {
                _wanderHeadingDeg[agentIndex] = heading;
                CrowdOracleRecorder.RecordWanderRepick(agentIndex, attempt + 1, true); // 무침습 관찰(Enabled=false면 no-op).
                return;
            }
        }

        // 모든 후보가 막히면 기존 heading을 유지한다. 다음 repick에서 다시 시도한다.
        CrowdOracleRecorder.RecordWanderRepick(agentIndex, WanderRepickTries, false); // 무침습 관찰(Enabled=false면 no-op).
    }

    // 수평 이동을 적용한다: 스위치 OFF(_useSdfSolver=false, 기본)면 CharacterController.Move(기존/안전 롤백),
    // ON이면 결정적 WallField SDF solver를 쓴다. 두 경로 모두 결과 world 위치를 같은 transform.position에 써서,
    // 이후의 되읽기 · walkable clamp · Y-pin · 팔로워 되먹임(실제 변위 기반)이 입력만 바뀔 뿐 구조·수식 그대로 동작하게 한다.
    // solver는 명령 변위가 아니라 실제 해소 위치를 반환하므로 되먹임이 명령 변위로 오염되지 않는다.
    // WallField 미로드 시(스위치 ON이라도) CC.Move로 폴백해 크래시 대신 기존 동작을 유지한다.
    private void ApplyHorizontalMove(int index, Vector3 horizontalDelta)
    {
        if (_useSdfSolver && _wallField != null && _wallField.IsLoaded)
        {
            CrowdSimCounters.CountSdfResolve(); // 무침습 계측(Enabled=false면 no-op).
            Transform tr = _transformByAgent[index];
            Vector3 pos = tr.position;
            // clearance는 per-agent scale(=CC lossyScale=buffer.Scale) 비례. Y는 그대로 두고 XZ만 해소한다(기존 Y-pin이 뒤에서 고정).
            float clearance = WallCollisionClearance * _buffer.Scale[index];
            Vector2 solved = WallSolver.Resolve(
                _wallField,
                new Vector2(pos.x, pos.z),
                new Vector2(horizontalDelta.x, horizontalDelta.z),
                clearance);
            tr.position = new Vector3(solved.x, pos.y, solved.y);
        }
        else
        {
            CrowdSimCounters.CountCcMoveFallback(); // 무침습 계측(Enabled=false면 no-op).
            _controllerByAgent[index].Move(horizontalDelta);
        }
    }

    // ⑤ 이동 결과를 buffer로 반영한다(buffer는 team/IsLeader의 최종 권한).
    // !sdfActive(CC 경로): 이동이 모든 agent의 transform을 저작하므로 전 agent의 transform XZ를 미러링한다(기존 경로, byte-unchanged).
    // sdfActive(SDF 경로): 팔로워/중립은 이동 단계가 buffer.Pos를 이미 직접 저작했으므로, 여기서는 live 리더의 transform XZ만
    // 미러링한다(MoveLeaders가 transform을 저작한 것과 정확히 같은 집합). 탈락 팀(LeaderAgentIndex==-1)은 건너뛴다.
    private void MirrorPositionsToBuffer(bool sdfActive)
    {
        NativeArray<Vector2> bufferPos = _buffer.Pos;
        if (!sdfActive)
        {
            int agentCount = _buffer.Count;
            for (int i = 0; i < agentCount; i++)
            {
                Vector3 pos = _transformByAgent[i].position;
                bufferPos[i] = new Vector2(pos.x, pos.z);
            }

            return;
        }

        // SDF 경로: MoveLeaders와 동일한 순회/집합(live 팀)의 리더 transform XZ만 미러링한다. 팔로워/중립 인덱스는 이동 단계가 직접 저작했으므로 건드리지 않는다.
        for (int t = 0; t < _teamCount; t++)
        {
            CrowdModel model = _crowds[t];
            if (model.Eliminated || model.LeaderAgentIndex < 0)
            {
                continue;
            }

            int leaderIndex = model.LeaderAgentIndex;
            Vector3 pos = _transformByAgent[leaderIndex].position;
            bufferPos[leaderIndex] = new Vector2(pos.x, pos.z);
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

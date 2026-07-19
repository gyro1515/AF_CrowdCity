using System.Collections.ObjectModel;
using UnityEngine;

/// <summary>
/// Crowd City MVP의 밸런스 수치, 팀 색상, 팀 material 참조를 담는 원본 설정 SO다.
/// 현재 play state는 저장하지 않으며 값 편집은 editor에서만 이루어진다.
/// </summary>
[CreateAssetMenu(menuName = "AF/CrowdCity/Game Config", fileName = "GameConfig")]
public sealed class GameConfigSO : ScriptableObject
{
    [Header("Match")]
    [Tooltip("매치 제한 시간(초). 이 시간이 지나면 라운드가 종료됨.")]
    [SerializeField] private float matchSeconds = 120f;
    /// <summary>매치 제한 시간을 초 단위로 정의한다.</summary>
    public float MatchSeconds => matchSeconds;

    [Tooltip("AI 라이벌 crowd 수(1~3으로 clamp).")]
    [SerializeField] private int rivalCount = 3;
    /// <summary>AI 라이벌 crowd 수다.</summary>
    public int RivalCount => rivalCount;

    [Tooltip("맵에 처음 배치되는 중립 인원 수. 클수록 초반에 영입할 대상이 많아짐.")]
    [SerializeField] private int neutralCount = 3000;
    /// <summary>초기 중립 인원 수다.</summary>
    public int NeutralCount => neutralCount;

    [Tooltip("중립 배치용 결정적 시드. 0도 랜덤이 아닌 고정 시드로 해석해 재현성을 보장함.")]
    [SerializeField] private int seed = 12345;
    /// <summary>중립 배치용 결정적 시드다. 0을 랜덤 의미로 해석하지 않는다(play-smoke 재현성).</summary>
    public int Seed => seed;

    [Header("Movement")]
    [Tooltip("리더(플레이어/라이벌 선두)의 이동 속도(m/s).")]
    [SerializeField] private float leaderSpeed = 5f;
    /// <summary>리더 이동 속도(m/s)다.</summary>
    public float LeaderSpeed => leaderSpeed;

    [Tooltip("팔로워 최대 이동 속도(m/s). 리더보다 커야 뒤처진 팔로워가 따라잡음.")]
    [SerializeField] private float followerMaxSpeed = 10f;
    /// <summary>팔로워 최대 이동 속도(m/s)다.</summary>
    public float FollowerMaxSpeed => followerMaxSpeed;

    [Tooltip("리더 회전 속도(도/초). 클수록 방향 전환이 즉각적임.")]
    [SerializeField] private float turnRateDegPerSec = 720f;
    /// <summary>리더 회전 속도(도/초)다.</summary>
    public float TurnRateDegPerSec => turnRateDegPerSec;

    [Tooltip("팔로워 golden-angle slot 간격(m). 클수록 무리가 더 넓게 퍼짐.")]
    [SerializeField] private float slotSpacing = 0.6f;
    /// <summary>팔로워 golden-angle slot 간격(m)이다.</summary>
    public float SlotSpacing => slotSpacing;

    [Tooltip("같은 crowd 팔로워 간 분리 반경(m). 유닛 몸통 지름(0.70)보다 커야 겹침이 풀리기 전에 반발력이 사라지지 않음.")]
    [SerializeField] private float separationRadius = 2f;
    /// <summary>같은 crowd 팔로워 간 분리 반경(m)이다. 현재는 팔로워 조향에서만 소비되는 팔로워 전용 값이다.
    /// 유닛 몸통 지름(캡슐 반지름 0.35 → 지름 0.70)보다 커야 겹침이 풀리기 전에 선형 반발력이 0이 되지 않는다.</summary>
    public float SeparationRadius => separationRadius;

    [Tooltip("팔로워 분리 밀어내기 세기(gain). 클수록 겹친 팔로워를 더 강하게 밀어냄.")]
    [SerializeField] private float separationPush = 3f;
    /// <summary>팔로워 분리 밀어내기 세기(gain)다. separationRadius와 함께 현재 팔로워 조향에서만 쓰이는 팔로워 전용 값이다.</summary>
    public float SeparationPush => separationPush;

    [Tooltip("유닛이 고정되는 걷기 가능 지면 표면 높이(Y)다. Ground renderer bounds의 max.y는 메시 두께/융기 지오메트리 때문에 실제 걷기 표면을 넘어서고, Ground에 collider가 없어 raycast로 표면을 잡을 수 없으므로 이 값을 명시적으로 지정한다.")]
    [SerializeField] private float groundY = 0.5f;
    /// <summary>유닛이 고정되는 걷기 가능 지면 표면 높이(Y)다.</summary>
    public float GroundY => groundY;

    [Header("Follower Steering")]
    [Tooltip("팔로워가 리더 뒤 지점으로 모이는 응집(arrive) 세기(gain). 클수록 무리가 빠르게 뭉침.")]
    [SerializeField] private float followerCohesionGain = 1f;
    /// <summary>팔로워가 리더 뒤 지점으로 모이는 응집(arrive) gain이다.</summary>
    public float FollowerCohesionGain => followerCohesionGain;

    [Tooltip("팔로워 arrive 감속 반경(m). 이 안에서 목표 속력을 0까지 선형 감쇠하며, 넓을수록 합류가 완만함.")]
    [SerializeField] private float followerArriveRadius = 4.5f;
    /// <summary>팔로워 arrive 감속 반경(m)이다. 이 안에서 목표 속력을 0까지 선형 감쇠해 오버슈트를 막는다. 넓을수록 합류가 완만하다.</summary>
    public float FollowerArriveRadius => followerArriveRadius;

    [Tooltip("무리 수에 따라 퍼지는 반경 증가 계수(반경 ∝ √인원). 클수록 큰 무리가 더 넓게 퍼짐. 0이면 고정 크기(기존 동작).")]
    [SerializeField] private float followerArriveRadiusPerSqrtMember = 0.6f;
    /// <summary>무리 수에 따라 arrive 반경을 √인원에 비례해 키우는 계수다. base followerArriveRadius를 floor로 두어 0이면 기존 고정 반경 동작이 된다.</summary>
    public float FollowerArriveRadiusPerSqrtMember => followerArriveRadiusPerSqrtMember;

    [Tooltip("팔로워 속도 변화 상한(m/s²). 출렁거림을 없애는 감쇠 계수. 오버슈트 방지 불변식: >= followerCohesionGain * followerMaxSpeed^2 / followerArriveRadius. 위반 시 OnValidate가 이 하한으로 끌어올린다.")]
    [SerializeField] private float followerMaxAccel = 40f;
    /// <summary>팔로워 속도 변화 상한(m/s²)이다. 출렁거림을 없애는 감쇠 계수 역할을 한다.</summary>
    public float FollowerMaxAccel => followerMaxAccel;

    [Tooltip("팔로워 무리 중심이 리더 진행 방향 뒤로 놓이는 거리(m). 무리가 리더 뒤에 자리 잡게 함.")]
    [SerializeField] private float followerTrailingOffset = 1f;
    /// <summary>팔로워 crowd가 모이는 단일 중심이 리더 진행 방향 뒤로 놓이는 거리(m)다. blob이 리더 뒤에 자리 잡게 한다.</summary>
    public float FollowerTrailingOffset => followerTrailingOffset;

    [Tooltip("부하가 벽/장애물에 막혔을 때 속도 감쇠 계수(0~1). 낮을수록 벽에서 더 빨리 정착(덜 떨림). 1이면 감쇠 없음.")]
    [SerializeField] private float followerBlockedDamping = 0.4f;
    /// <summary>부하가 벽/장애물에 막혔을 때 적용하는 속도 감쇠 계수(0~1)다. 낮을수록 벽에서 더 빨리 정착하며 1이면 감쇠 없음이다.</summary>
    public float FollowerBlockedDamping => followerBlockedDamping;

    [Header("Rules")]
    [Tooltip("영입/전투 규칙에 쓰이는 시뮬레이션 커널 튜닝 값 묶음(각 하위 항목 tooltip 참고).")]
    [SerializeField] private SimTuning sim = new SimTuning { RecruitRadius = 1.2f, CombatRadius = 0.5f, LeaderAloneRadius = 0.7f, ConvertPerSecond = 100f, PairNormalizer = 15, RateLimitConversion = true, LeaderProtection = true, SeparationVisitBudget = 48, CombatFlatConvertRate = true };
    /// <summary>영입/전투 규칙에 쓰이는 시뮬레이션 커널 튜닝 값이다.</summary>
    public SimTuning Sim => sim;

    [Header("AI")]
    [Tooltip("라이벌 AI가 방향을 다시 결정하는 주기(초, 시뮬레이션 시간). 작을수록 더 자주 판단함.")]
    [SerializeField] private float aiDecideInterval = 0.4f;
    /// <summary>라이벌 AI의 방향 재결정 주기(초, 시뮬레이션 시간)다.</summary>
    public float AiDecideInterval => aiDecideInterval;

    [Tooltip("상대 crowd가 자기 크기의 이 배율보다 크면 도주(예: 1.15 = 15% 이상 크면 도망).")]
    [SerializeField] private float fleeSizeRatio = 1.15f;
    /// <summary>상대가 자기 크기의 이 배율보다 크면 도주한다.</summary>
    public float FleeSizeRatio => fleeSizeRatio;

    [Tooltip("상대 crowd가 자기 크기의 이 배율보다 작으면 추격(예: 0.7 = 70% 이하면 사냥).")]
    [SerializeField] private float huntSizeRatio = 0.7f;
    /// <summary>상대가 자기 크기의 이 배율보다 작으면 추격한다.</summary>
    public float HuntSizeRatio => huntSizeRatio;

    [Tooltip("추격을 시작하는 데 필요한 최소 자기 인원 수.")]
    [SerializeField] private int huntMinCount = 15;
    /// <summary>추격을 시작하는 최소 자기 인원 수다.</summary>
    public int HuntMinCount => huntMinCount;

    [Tooltip("AI가 다른 crowd와 중립을 인지하는 시야 반경(m).")]
    [SerializeField] private float aiVisionRadius = 25f;
    /// <summary>AI가 다른 crowd와 중립을 인지하는 반경(m)이다.</summary>
    public float AiVisionRadius => aiVisionRadius;

    [Tooltip("벽 회피용 raycast 거리(m). 클수록 더 일찍 벽을 감지해 피함.")]
    [SerializeField] private float wallProbeDistance = 3f;
    /// <summary>벽 회피 raycast 거리(m)다.</summary>
    public float WallProbeDistance => wallProbeDistance;

    [Header("Neutrals")]
    [Tooltip("중립 인원의 배회 이동 속도(m/s).")]
    [SerializeField] private float neutralWanderSpeed = 0.5f;
    /// <summary>중립 배회 속도(m/s)다.</summary>
    public float NeutralWanderSpeed => neutralWanderSpeed;

    [Tooltip("중립이 배회 방향을 다시 고르는 최소 간격(초).")]
    [SerializeField] private float wanderRepickMinSeconds = 2f;
    /// <summary>중립 배회 방향 재선택 최소 간격(초)이다.</summary>
    public float WanderRepickMinSeconds => wanderRepickMinSeconds;

    [Tooltip("중립이 배회 방향을 다시 고르는 최대 간격(초, 최소값 이상이어야 함).")]
    [SerializeField] private float wanderRepickMaxSeconds = 5f;
    /// <summary>중립 배회 방향 재선택 최대 간격(초)이다.</summary>
    public float WanderRepickMaxSeconds => wanderRepickMaxSeconds;

    [Tooltip("중립 캐릭터 애니메이션 재생 속도 배율 (1 = 현재). Human의 최대 1.5로 클램프됨.")]
    [SerializeField] private float neutralAnimationSpeed = 1.0f;
    /// <summary>중립 캐릭터 애니메이션 재생 속도 배율이다.</summary>
    public float NeutralAnimationSpeed => neutralAnimationSpeed;

    [Tooltip("중립 캐릭터 최대 스케일. neutralBaselineScaleChance 확률로 기본 크기(1.0), 나머지는 1.1~이 값 사이에서 0.1 단위 랜덤 -> 큰 개체 희소화. CharacterController 충돌 크기도 자동으로 함께 스케일됨.")]
    [SerializeField] private float neutralMaxScale = 1.3f;
    /// <summary>중립 캐릭터 최대 스케일이다.</summary>
    public float NeutralMaxScale => neutralMaxScale;

    [Tooltip("중립이 기본 크기(1.0)로 스폰될 확률(0~1). 나머지는 1.1~최대에서 랜덤 -> 큰 개체 희소화.")]
    [SerializeField] private float neutralBaselineScaleChance = 0.5f;
    /// <summary>중립이 기본 크기(1.0)로 스폰될 확률이다.</summary>
    public float NeutralBaselineScaleChance => neutralBaselineScaleChance;

    [Header("Camera")]
    [Tooltip("카메라가 아래를 내려다보는 pitch 각도(도, 10~89로 clamp).")]
    [SerializeField] private float camPitchDeg = 55f;
    /// <summary>카메라 pitch 각도(도)다.</summary>
    public float CamPitchDeg => camPitchDeg;

    [Tooltip("플레이어 1명 기준 카메라 기본 거리(m).")]
    [SerializeField] private float camBaseDistance = 16f;
    /// <summary>플레이어 1명 기준 카메라 기본 거리(m)다.</summary>
    public float CamBaseDistance => camBaseDistance;

    [Tooltip("플레이어 인원 수 제곱근당 추가되는 카메라 거리(m). 클수록 무리가 커질 때 더 멀리 빠짐.")]
    [SerializeField] private float camDistancePerSqrtCount = 0.8f;
    /// <summary>플레이어 인원 수 제곱근당 추가 카메라 거리(m)다.</summary>
    public float CamDistancePerSqrtCount => camDistancePerSqrtCount;

    [Tooltip("카메라 최대 거리(m, 기본 거리 이상으로 clamp).")]
    [SerializeField] private float camMaxDistance = 40f;
    /// <summary>카메라 최대 거리(m)다.</summary>
    public float CamMaxDistance => camMaxDistance;

    [Tooltip("카메라 추적 SmoothDamp 시간(초). 클수록 부드럽지만 반응이 느려짐.")]
    [SerializeField] private float camFollowSmoothTime = 0.25f;
    /// <summary>카메라 추적 SmoothDamp 시간(초)이다.</summary>
    public float CamFollowSmoothTime => camFollowSmoothTime;

    [Header("Rendering")]
    [Tooltip("VAT 인스턴스 크라우드 렌더러 스위치(기본 OFF, 동작 보존). OFF면 기존 SkinnedMeshRenderer+Animator 경로가 바이트 동일하게 유지된다. ON이면 스폰 후 CrowdRenderer 초기화 성공 시에만 각 유닛의 SMR+Animator를 런타임에 끄고 인스턴스 렌더 경로로 그린다(저작 프리팹 불변). 시뮬/커널/RNG/이벤트/결정성에는 영향이 없다.")]
    [SerializeField] private bool useGpuCrowdRenderer = true;
    /// <summary>VAT 인스턴스 크라우드 렌더러 스위치다(기본 OFF, 동작 보존). ON이면 CrowdRenderer 초기화 성공 시에만 SMR+Animator 대신 인스턴스 렌더 경로로 그린다. 시뮬/결정성에는 영향이 없다.</summary>
    public bool UseGpuCrowdRenderer => useGpuCrowdRenderer;

    [Header("Visuals — [0]=player, [1..3]=rivals; TeamMaterials[4]=neutral (wired by editor setup)")]
    [Tooltip("팀 색상 배열. [0]=플레이어, [1..3]=라이벌. 길이 4를 유지함.")]
    [SerializeField] private Color[] teamColors =   // EXPLICIT literals — never new Color[4] (transparent black!)
    {
        new Color(0.180f, 0.525f, 1.000f), // #2E86FF player blue
        new Color(1.000f, 0.255f, 0.212f), // #FF4136 rival red
        new Color(1.000f, 0.522f, 0.106f), // #FF851B rival orange
        new Color(0.180f, 0.800f, 0.251f), // #2ECC40 rival green
    };
    private ReadOnlyCollection<Color> _teamColorsView;
    /// <summary>팀 색상이다. [0]=플레이어, [1..3]=라이벌. 길이 4를 유지한다. 원본 배열로 캐스팅되지 않는 read-only view를 돌려준다.</summary>
    public System.Collections.Generic.IReadOnlyList<Color> TeamColors => _teamColorsView ??= System.Array.AsReadOnly(teamColors);

    [Tooltip("중립 인원의 색상.")]
    [SerializeField] private Color neutralColor = Color.white;
    /// <summary>중립 인원 색상이다.</summary>
    public Color NeutralColor => neutralColor;

    [Tooltip("팀 material 참조 배열. [0]=플레이어, [1..3]=라이벌, [4]=중립. editor setup이 연결하며 길이 5를 유지함.")]
    [SerializeField] private Material[] teamMaterials = new Material[5];
    private ReadOnlyCollection<Material> _teamMaterialsView;
    /// <summary>팀 material 참조다. [0]=플레이어, [1..3]=라이벌, [4]=중립. editor setup이 연결하며 길이 5를 유지한다. 원본 배열로 캐스팅되지 않는 read-only view를 돌려준다.</summary>
    public System.Collections.Generic.IReadOnlyList<Material> TeamMaterials => _teamMaterialsView ??= System.Array.AsReadOnly(teamMaterials);

    private void OnValidate()
    {
        matchSeconds = Mathf.Max(1f, matchSeconds);
        rivalCount = Mathf.Clamp(rivalCount, 1, 3);
        neutralCount = Mathf.Max(0, neutralCount);
        // Seed는 어떤 int든 결정적으로 유효하므로 clamp하지 않는다.

        leaderSpeed = Mathf.Max(0.1f, leaderSpeed);
        followerMaxSpeed = Mathf.Max(0.1f, followerMaxSpeed);
        turnRateDegPerSec = Mathf.Max(1f, turnRateDegPerSec);
        slotSpacing = Mathf.Max(0.05f, slotSpacing);
        separationRadius = Mathf.Max(0.01f, separationRadius);
        separationPush = Mathf.Max(0f, separationPush);

        followerCohesionGain = Mathf.Max(0.05f, followerCohesionGain); // 0이면 응집이 사라져 blob이 조용히 흩어지므로 작은 양수 하한을 둔다.
        followerArriveRadius = Mathf.Max(0.01f, followerArriveRadius);
        followerArriveRadiusPerSqrtMember = Mathf.Max(0f, followerArriveRadiusPerSqrtMember);
        followerMaxAccel = Mathf.Max(0.1f, followerMaxAccel);
        followerTrailingOffset = Mathf.Max(0f, followerTrailingOffset);
        followerBlockedDamping = Mathf.Clamp01(followerBlockedDamping);

        // 오버슈트 방지 불변식: followerMaxAccel >= followerCohesionGain * followerMaxSpeed^2 / followerArriveRadius.
        // 위반 시 blob이 리더 주위를 도는 orbit/wobble이 생기므로 계산된 하한으로 소프트 상향한다(arriveRadius 0-나눗셈 방지).
        if (followerArriveRadius > 0.0001f)
        {
            float minAccel = followerCohesionGain * followerMaxSpeed * followerMaxSpeed / followerArriveRadius;
            followerMaxAccel = Mathf.Max(followerMaxAccel, minAccel);
        }

        sim.RecruitRadius = Mathf.Max(0.01f, sim.RecruitRadius);
        sim.CombatRadius = Mathf.Max(0.01f, sim.CombatRadius);
        sim.LeaderAloneRadius = Mathf.Max(0.01f, sim.LeaderAloneRadius);
        sim.ConvertPerSecond = Mathf.Max(0f, sim.ConvertPerSecond);
        sim.PairNormalizer = Mathf.Max(1, sim.PairNormalizer);
        sim.SeparationVisitBudget = Mathf.Max(0, sim.SeparationVisitBudget);

        aiDecideInterval = Mathf.Max(0.02f, aiDecideInterval);
        fleeSizeRatio = Mathf.Max(0.01f, fleeSizeRatio);
        huntSizeRatio = Mathf.Max(0.01f, huntSizeRatio);
        huntMinCount = Mathf.Max(1, huntMinCount);
        aiVisionRadius = Mathf.Max(1f, aiVisionRadius);
        wallProbeDistance = Mathf.Max(0.1f, wallProbeDistance);

        neutralWanderSpeed = Mathf.Max(0f, neutralWanderSpeed);
        wanderRepickMinSeconds = Mathf.Max(0.1f, wanderRepickMinSeconds);
        wanderRepickMaxSeconds = Mathf.Max(wanderRepickMinSeconds, wanderRepickMaxSeconds);
        neutralAnimationSpeed = Mathf.Max(0f, neutralAnimationSpeed);
        neutralMaxScale = Mathf.Max(1f, neutralMaxScale);
        neutralBaselineScaleChance = Mathf.Clamp01(neutralBaselineScaleChance);

        camPitchDeg = Mathf.Clamp(camPitchDeg, 10f, 89f);
        camBaseDistance = Mathf.Max(1f, camBaseDistance);
        camDistancePerSqrtCount = Mathf.Max(0f, camDistancePerSqrtCount);
        camMaxDistance = Mathf.Max(camBaseDistance, camMaxDistance);
        camFollowSmoothTime = Mathf.Max(0.01f, camFollowSmoothTime);

        EnsureTeamColorsLength();
        EnsureTeamMaterialsLength();

        // 길이 보정 헬퍼가 새 배열을 할당했을 수 있으므로 read-only view 캐시를 무효화해 다음 접근 시 현재 배열을 다시 감싸게 한다.
        _teamColorsView = null;
        _teamMaterialsView = null;
    }

    private void EnsureTeamColorsLength()
    {
        if (teamColors != null && teamColors.Length == 4)
        {
            return;
        }

        // 길이가 어긋나면 기존 값을 보존하고 빈 slot은 기본 팀 색으로 채운다(투명 검정 방지).
        Color[] repaired = new Color[]
        {
            new Color(0.180f, 0.525f, 1.000f), // #2E86FF player blue
            new Color(1.000f, 0.255f, 0.212f), // #FF4136 rival red
            new Color(1.000f, 0.522f, 0.106f), // #FF851B rival orange
            new Color(0.180f, 0.800f, 0.251f), // #2ECC40 rival green
        };

        if (teamColors != null)
        {
            int copyCount = Mathf.Min(teamColors.Length, repaired.Length);
            for (int i = 0; i < copyCount; i++)
            {
                repaired[i] = teamColors[i];
            }
        }

        teamColors = repaired;
    }

    private void EnsureTeamMaterialsLength()
    {
        if (teamMaterials != null && teamMaterials.Length == 5)
        {
            return;
        }

        Material[] repaired = new Material[5];
        if (teamMaterials != null)
        {
            int copyCount = Mathf.Min(teamMaterials.Length, repaired.Length);
            for (int i = 0; i < copyCount; i++)
            {
                repaired[i] = teamMaterials[i];
            }
        }

        teamMaterials = repaired;
    }
}

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
    [SerializeField] private float matchSeconds = 120f;
    /// <summary>매치 제한 시간을 초 단위로 정의한다.</summary>
    public float MatchSeconds => matchSeconds;

    [SerializeField] private int rivalCount = 3;
    /// <summary>AI 라이벌 crowd 수다.</summary>
    public int RivalCount => rivalCount;

    [SerializeField] private int neutralCount = 150;
    /// <summary>초기 중립 인원 수다.</summary>
    public int NeutralCount => neutralCount;

    [SerializeField] private int seed = 12345;
    /// <summary>중립 배치용 결정적 시드다. 0을 랜덤 의미로 해석하지 않는다(play-smoke 재현성).</summary>
    public int Seed => seed;

    [Header("Movement")]
    [SerializeField] private float leaderSpeed = 5f;
    /// <summary>리더 이동 속도(m/s)다.</summary>
    public float LeaderSpeed => leaderSpeed;

    [SerializeField] private float followerMaxSpeed = 6.5f;
    /// <summary>팔로워 최대 이동 속도(m/s)다.</summary>
    public float FollowerMaxSpeed => followerMaxSpeed;

    [SerializeField] private float turnRateDegPerSec = 720f;
    /// <summary>리더 회전 속도(도/초)다.</summary>
    public float TurnRateDegPerSec => turnRateDegPerSec;

    [SerializeField] private float slotSpacing = 0.6f;
    /// <summary>팔로워 golden-angle slot 간격(m)이다.</summary>
    public float SlotSpacing => slotSpacing;

    [SerializeField] private float separationRadius = 0.45f;
    /// <summary>같은 crowd 팔로워 간 분리 반경(m)이다.</summary>
    public float SeparationRadius => separationRadius;

    [SerializeField] private float separationPush = 1.5f;
    /// <summary>분리 밀어내기 세기다.</summary>
    public float SeparationPush => separationPush;

    [Header("Rules")]
    [SerializeField] private SimTuning sim = new SimTuning { RecruitRadius = 1.2f, CombatRadius = 1f, ConvertPerSecond = 10f, PairNormalizer = 8 };
    /// <summary>영입/전투 규칙에 쓰이는 시뮬레이션 커널 튜닝 값이다.</summary>
    public SimTuning Sim => sim;

    [Header("AI")]
    [SerializeField] private float aiDecideInterval = 0.4f;
    /// <summary>라이벌 AI의 방향 재결정 주기(초, 시뮬레이션 시간)다.</summary>
    public float AiDecideInterval => aiDecideInterval;

    [SerializeField] private float fleeSizeRatio = 1.15f;
    /// <summary>상대가 자기 크기의 이 배율보다 크면 도주한다.</summary>
    public float FleeSizeRatio => fleeSizeRatio;

    [SerializeField] private float huntSizeRatio = 0.7f;
    /// <summary>상대가 자기 크기의 이 배율보다 작으면 추격한다.</summary>
    public float HuntSizeRatio => huntSizeRatio;

    [SerializeField] private int huntMinCount = 15;
    /// <summary>추격을 시작하는 최소 자기 인원 수다.</summary>
    public int HuntMinCount => huntMinCount;

    [SerializeField] private float aiVisionRadius = 25f;
    /// <summary>AI가 다른 crowd와 중립을 인지하는 반경(m)이다.</summary>
    public float AiVisionRadius => aiVisionRadius;

    [SerializeField] private float wallProbeDistance = 3f;
    /// <summary>벽 회피 raycast 거리(m)다.</summary>
    public float WallProbeDistance => wallProbeDistance;

    [Header("Neutrals")]
    [SerializeField] private float neutralWanderSpeed = 0.5f;
    /// <summary>중립 배회 속도(m/s)다.</summary>
    public float NeutralWanderSpeed => neutralWanderSpeed;

    [SerializeField] private float wanderRepickMinSeconds = 2f;
    /// <summary>중립 배회 방향 재선택 최소 간격(초)이다.</summary>
    public float WanderRepickMinSeconds => wanderRepickMinSeconds;

    [SerializeField] private float wanderRepickMaxSeconds = 5f;
    /// <summary>중립 배회 방향 재선택 최대 간격(초)이다.</summary>
    public float WanderRepickMaxSeconds => wanderRepickMaxSeconds;

    [Header("Camera")]
    [SerializeField] private float camPitchDeg = 55f;
    /// <summary>카메라 pitch 각도(도)다.</summary>
    public float CamPitchDeg => camPitchDeg;

    [SerializeField] private float camBaseDistance = 16f;
    /// <summary>플레이어 1명 기준 카메라 기본 거리(m)다.</summary>
    public float CamBaseDistance => camBaseDistance;

    [SerializeField] private float camDistancePerSqrtCount = 0.8f;
    /// <summary>플레이어 인원 수 제곱근당 추가 카메라 거리(m)다.</summary>
    public float CamDistancePerSqrtCount => camDistancePerSqrtCount;

    [SerializeField] private float camMaxDistance = 40f;
    /// <summary>카메라 최대 거리(m)다.</summary>
    public float CamMaxDistance => camMaxDistance;

    [SerializeField] private float camFollowSmoothTime = 0.25f;
    /// <summary>카메라 추적 SmoothDamp 시간(초)이다.</summary>
    public float CamFollowSmoothTime => camFollowSmoothTime;

    [Header("Visuals — [0]=player, [1..3]=rivals; TeamMaterials[4]=neutral (wired by editor setup)")]
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

    [SerializeField] private Color neutralColor = Color.white;
    /// <summary>중립 인원 색상이다.</summary>
    public Color NeutralColor => neutralColor;

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

        sim.RecruitRadius = Mathf.Max(0.01f, sim.RecruitRadius);
        sim.CombatRadius = Mathf.Max(0.01f, sim.CombatRadius);
        sim.ConvertPerSecond = Mathf.Max(0f, sim.ConvertPerSecond);
        sim.PairNormalizer = Mathf.Max(1, sim.PairNormalizer);

        aiDecideInterval = Mathf.Max(0.02f, aiDecideInterval);
        fleeSizeRatio = Mathf.Max(0.01f, fleeSizeRatio);
        huntSizeRatio = Mathf.Max(0.01f, huntSizeRatio);
        huntMinCount = Mathf.Max(1, huntMinCount);
        aiVisionRadius = Mathf.Max(1f, aiVisionRadius);
        wallProbeDistance = Mathf.Max(0.1f, wallProbeDistance);

        neutralWanderSpeed = Mathf.Max(0f, neutralWanderSpeed);
        wanderRepickMinSeconds = Mathf.Max(0.1f, wanderRepickMinSeconds);
        wanderRepickMaxSeconds = Mathf.Max(wanderRepickMinSeconds, wanderRepickMaxSeconds);

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

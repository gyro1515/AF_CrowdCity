using UnityEngine;

/// <summary>
/// 정적 도시 벽에서 에디터로 1회 베이크한 2D signed distance field(SDF)의 직렬화 컨테이너다.
/// 소스/카탈로그 성격의 불변 데이터이며 런타임에는 로드만 한다(재베이크 금지, 이동 로직 미연결 — Phase C 단계 1).
///
/// 거리 payload는 ForceText 직렬화에서 .asset이 비대해지지 않도록 별도 raw 바이너리(.bytes TextAsset)에 담고,
/// 이 SO에는 스키마/그리드 메타와 payload 참조·무결성 값만 둔다.
///
/// 그리드 규약:
///   - 셀 (col,row): col∈[0,Cols), row∈[0,Rows). 인덱스 idx = row*Cols + col (row-major).
///   - 셀 중심 world XZ = (OriginX + (col+0.5)*CellSize, OriginZ + (row+0.5)*CellSize).
///   - Payload는 리틀엔디안 float32 Cols*Rows개. 값은 signed distance(내부 음수/외부 양수, 단위 m),
///     외부는 +MaxDistance, 내부 깊은 곳은 -MaxDistance로 clamp된 참 거리다(BilinearBias 미반영).
/// 소비자는 [SerializeField] private + get-only로만 읽는다(런타임 불변).
/// </summary>
public sealed class WallSdfAsset : ScriptableObject
{
    /// <summary>현재 layout 스키마 버전이다. 구조가 바뀌면 증가시킨다.</summary>
    public const int CurrentSchemaVersion = 1;

    [SerializeField] private int schemaVersion;
    [SerializeField] private float originX;
    [SerializeField] private float originZ;
    [SerializeField] private float cellSize;
    [SerializeField] private int cols;
    [SerializeField] private int rows;
    [SerializeField] private float yMin;
    [SerializeField] private float yMax;
    [SerializeField] private float maxDistance;
    [SerializeField] private float bilinearBias;
    [SerializeField] private int colliderCount;
    [SerializeField] private int triangleCount;

    // 무결성/결정성 확인용 해시. sourceHash는 베이크 입력(순서 고정된 collider mesh+params)에서, payloadCrc는 출력 바이트에서 계산한다.
    [SerializeField] private uint sourceHash;
    [SerializeField] private uint payloadCrc;

    // 거리장 raw 바이너리(리틀엔디안 float32 Cols*Rows). ForceText 비대화를 피하려고 SO 밖 .bytes에 둔다.
    [SerializeField] private TextAsset payload;

    /// <summary>직렬화된 스키마 버전이다.</summary>
    public int SchemaVersion => schemaVersion;

    /// <summary>그리드 최소 코너의 world X다(셀 (0,·)의 최소 X).</summary>
    public float OriginX => originX;

    /// <summary>그리드 최소 코너의 world Z다(셀 (·,0)의 최소 Z).</summary>
    public float OriginZ => originZ;

    /// <summary>셀 한 변의 길이(m)다.</summary>
    public float CellSize => cellSize;

    /// <summary>X축(열) 셀 수다.</summary>
    public int Cols => cols;

    /// <summary>Z축(행) 셀 수다.</summary>
    public int Rows => rows;

    /// <summary>베이크에 쓴 캡슐 y-band 하한(world Y)이다. 이 band와 교차한 삼각형만 footprint에 기여한다.</summary>
    public float YMin => yMin;

    /// <summary>베이크에 쓴 캡슐 y-band 상한(world Y)이다.</summary>
    public float YMax => yMax;

    /// <summary>거리 clamp 상한(m)이다. 외부는 +이 값, 내부는 -이 값으로 포화된다.</summary>
    public float MaxDistance => maxDistance;

    /// <summary>
    /// 권장 tunneling 방지 bias(m)다. 저장된 값은 참 SDF이며, Stage 2 solver가 조회 시 이 값만큼 빼서
    /// bilinear 과대평가를 보수적으로 보정하도록 남긴 메타데이터다(현재 이동 미연결이라 반영은 solver 몫).
    /// </summary>
    public float BilinearBias => bilinearBias;

    /// <summary>베이크에 사용된 정적 벽 collider 수다(기대값 40 = 건물 37 + Parks/Vehicles/StreetProps).</summary>
    public int ColliderCount => colliderCount;

    /// <summary>y-band 필터 후 footprint에 기여한 삼각형 수다.</summary>
    public int TriangleCount => triangleCount;

    /// <summary>베이크 입력(순서 고정 collider mesh + 파라미터)에서 계산한 FNV-1a 해시다. 같은 입력 → 같은 값.</summary>
    public uint SourceHash => sourceHash;

    /// <summary>payload 바이트에 대한 CRC32다.</summary>
    public uint PayloadCrc => payloadCrc;

    /// <summary>거리장 raw 바이너리(리틀엔디안 float32 Cols*Rows)를 담은 TextAsset이다.</summary>
    public TextAsset Payload => payload;

    /// <summary>셀 수(Cols*Rows)다.</summary>
    public int CellCount => cols * rows;

#if UNITY_EDITOR
    /// <summary>
    /// 베이커(Editor 전용)가 메타를 채운다. private 필드에 직접 접근할 방법이 SO 내부 API뿐이라
    /// 런타임 불변 계약을 지키면서 에디터에서만 쓰기를 허용한다(UNITY_EDITOR 가드).
    /// </summary>
    public void EditorInitialize(
        float originX, float originZ, float cellSize, int cols, int rows,
        float yMin, float yMax, float maxDistance, float bilinearBias,
        int colliderCount, int triangleCount, uint sourceHash, uint payloadCrc, TextAsset payload)
    {
        this.schemaVersion = CurrentSchemaVersion;
        this.originX = originX;
        this.originZ = originZ;
        this.cellSize = cellSize;
        this.cols = cols;
        this.rows = rows;
        this.yMin = yMin;
        this.yMax = yMax;
        this.maxDistance = maxDistance;
        this.bilinearBias = bilinearBias;
        this.colliderCount = colliderCount;
        this.triangleCount = triangleCount;
        this.sourceHash = sourceHash;
        this.payloadCrc = payloadCrc;
        this.payload = payload;
    }
#endif
}

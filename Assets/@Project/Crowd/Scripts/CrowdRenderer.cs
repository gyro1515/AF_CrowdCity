using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// GPU-anim Stage 1 Chunk B: VAT 샘플링 인스턴스 크라우드 렌더러다(CrowdRoot의 프리팹 저작 자식 feature).
/// CrowdRoot가 소유/구동한다 — 스폰 후 <see cref="Init"/>로 1회 주입받고, 매 렌더 프레임 <see cref="Render"/>를 호출받는다.
/// 시뮬레이션/커널을 전혀 참조하지 않는 presentation 전용 뷰이며 자기 GraphicsBuffer 수명만 책임진다.
/// graphics device가 없거나(-nographics 하네스) vertex StructuredBuffer(shader level 4.5)를 지원하지 않으면
/// <see cref="Init"/>가 false를 반환하고 CrowdRoot는 기존 SkinnedMeshRenderer 경로를 유지한다(SMR을 절대 끄지 않는다).
/// VAT 텍스처/메쉬/머티리얼은 [SerializeField] 저작 자원이다(Resources.Load 폴백 없음, 미배선은 실패로 노출).
/// </summary>
public sealed class CrowdRenderer : MonoBehaviour
{
    /// <summary>
    /// per-instance 레코드다. 셰이더 <c>VatInstance</c> struct와 반드시 일치해야 한다(순차 레이아웃, stride 28바이트).
    /// speed는 phase01 적분에 접혀 들어가므로 여기 없다.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct InstanceData
    {
        public Vector3 Pos;      // 12B: Human 루트 world 위치(발밑, Y=groundY)
        public float Yaw;        // 4B: Y축 회전(라디안)
        public float Scale;      // 4B: 균일 스케일
        public float Phase01;    // 4B: 걷기 사이클 위상 [0,1)
        public uint PackedColor; // 4B: sRGB 팀 색(R=bit0-7, G=8-15, B=16-23)
    }

    /// <summary>InstanceData의 stride(바이트). 셰이더 VatInstance와의 계약이며 Init에서 assert한다.</summary>
    public const int InstanceStride = 28;

    [SerializeField] private Mesh _vatMesh;
    [SerializeField] private Material _vatMaterial;
    [SerializeField] private Texture2D _positionVat;
    [SerializeField] private Texture2D _normalVat;
    [SerializeField] private int _vatRows = 21; // Chunk A 베이크: LoopClose 21행. 재베이크로 바뀌면 여기와 셰이더가 함께 따라간다.

    private static readonly int PositionVatId = Shader.PropertyToID("_PositionVat");
    private static readonly int NormalVatId = Shader.PropertyToID("_NormalVat");
    private static readonly int VatRowsId = Shader.PropertyToID("_VatRows");
    private static readonly int InstancesId = Shader.PropertyToID("_Instances");

    private GraphicsBuffer _mainBuffer;    // 전체 인스턴스(그림자 Off, 그림자 수신).
    private GraphicsBuffer _leaderBuffer;  // <=teamCount 리더(그림자 전용 draw).
    private InstanceData[] _leaderScratch; // 매 프레임 리더 레코드를 최신값으로 채워 업로드(stale 금지).
    private MaterialPropertyBlock _mainProps;
    private MaterialPropertyBlock _leaderProps;
    private Bounds _worldBounds;
    private int _capacity;
    private bool _active;
    private bool _disposed;

    /// <summary>렌더러가 초기화에 성공해 GPU 경로가 활성인지 여부다. false면 CrowdRoot가 SMR 경로를 유지한다.</summary>
    public bool IsActive => _active;

    /// <summary>
    /// CrowdRoot가 스폰 완료 후 1회 주입한다. 저작 자원 유효성 + stride 계약 + graphics API capability를 검사하고
    /// GraphicsBuffer/MaterialPropertyBlock을 지연 할당한다. 성공하면 true, 미배선/불일치/미지원/무device면 false다.
    /// false를 반환하면 호출부(CrowdRoot)는 SMR을 절대 끄지 않고 기존 경로를 유지한다.
    /// </summary>
    public bool Init(int capacity, int leaderCapacity, Bounds worldBounds)
    {
        if (_active || _disposed)
        {
            return _active;
        }

        // 저작 자원 검사: 미배선(null)은 실패로 노출한다(silent Resources.Load 폴백 금지, CLAUDE 11.5).
        if (_vatMesh == null || _vatMaterial == null || _positionVat == null || _normalVat == null)
        {
            Debug.LogError(
                "[CrowdRenderer] VAT mesh/material/position/normal 직렬화 필드가 비어 있습니다(프리팹 배선 누락). " +
                "GPU 크라우드 경로를 비활성화하고 SMR 경로를 유지합니다. GameSceneSetup으로 CrowdRoot 프리팹을 수렴하세요.");
            return false;
        }

        int stride = Marshal.SizeOf<InstanceData>();
        if (stride != InstanceStride)
        {
            Debug.LogError(
                $"[CrowdRenderer] InstanceData stride={stride} != 계약 {InstanceStride}바이트. 셰이더 VatInstance와 불일치.");
            return false;
        }

        // null graphics device(-batchmode -nographics 하네스) → no-op으로 SMR 경로를 유지한다(오라클/프로파일 하네스 불변).
        if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null)
        {
            return false;
        }

        // vertex StructuredBuffer는 shader model 4.5(compute 지원)를 요구한다. 미지원 API면 SMR 경로로 폴백한다.
        if (SystemInfo.graphicsShaderLevel < 45 || !SystemInfo.supportsComputeShaders)
        {
            Debug.LogWarning(
                "[CrowdRenderer] 이 graphics API는 vertex StructuredBuffer(shader level 4.5)를 지원하지 않아 GPU 크라우드 경로를 비활성화합니다. SMR 경로를 유지합니다.");
            return false;
        }

        _capacity = Mathf.Max(1, capacity);
        _worldBounds = worldBounds;

        _mainBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, _capacity, InstanceStride);
        int leaderCap = Mathf.Max(1, leaderCapacity);
        _leaderBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, leaderCap, InstanceStride);
        _leaderScratch = new InstanceData[leaderCap];

        _mainProps = new MaterialPropertyBlock();
        _mainProps.SetTexture(PositionVatId, _positionVat);
        _mainProps.SetTexture(NormalVatId, _normalVat);
        _mainProps.SetFloat(VatRowsId, _vatRows);
        _mainProps.SetBuffer(InstancesId, _mainBuffer);

        _leaderProps = new MaterialPropertyBlock();
        _leaderProps.SetTexture(PositionVatId, _positionVat);
        _leaderProps.SetTexture(NormalVatId, _normalVat);
        _leaderProps.SetFloat(VatRowsId, _vatRows);
        _leaderProps.SetBuffer(InstancesId, _leaderBuffer);

        _active = true;
        return true;
    }

    /// <summary>
    /// 매 렌더 프레임 CrowdRoot가 채운 instances[0..count)를 업로드하고 draw를 발행한다.
    /// 메인 draw는 전체 count(그림자 Off, 그림자 수신), 리더 draw는 leaderIndices[0..leaderCount)가 가리키는
    /// 최신 레코드만 ShadowsOnly로 발행한다(리더만 애니메이션 그림자를 드리운다). 미활성/빈 프레임이면 no-op다.
    /// </summary>
    public void Render(InstanceData[] instances, int count, int[] leaderIndices, int leaderCount)
    {
        if (!_active || instances == null || count <= 0)
        {
            return;
        }

        count = Mathf.Min(count, _capacity);
        _mainBuffer.SetData(instances, 0, 0, count);

        RenderParams main = new RenderParams(_vatMaterial)
        {
            worldBounds = _worldBounds,
            matProps = _mainProps,
            shadowCastingMode = ShadowCastingMode.Off,
            receiveShadows = true,
        };
        Graphics.RenderMeshPrimitives(main, _vatMesh, 0, count);

        // 리더 그림자: <=leaderCap개 리더 레코드를 매 프레임 최신값으로 업로드(stale transform 금지)하고 ShadowsOnly draw.
        int lc = leaderIndices != null ? Mathf.Min(leaderCount, _leaderScratch.Length) : 0;
        int packed = 0;
        for (int i = 0; i < lc; i++)
        {
            int idx = leaderIndices[i];
            if ((uint)idx < (uint)count)
            {
                _leaderScratch[packed++] = instances[idx];
            }
        }

        if (packed > 0)
        {
            _leaderBuffer.SetData(_leaderScratch, 0, 0, packed);
            RenderParams leader = new RenderParams(_vatMaterial)
            {
                worldBounds = _worldBounds,
                matProps = _leaderProps,
                shadowCastingMode = ShadowCastingMode.ShadowsOnly,
                receiveShadows = false,
            };
            Graphics.RenderMeshPrimitives(leader, _vatMesh, 0, packed);
        }
    }

    /// <summary>
    /// GraphicsBuffer를 해제한다. CrowdRoot.Shutdown(이른 return 이전)과 OnDestroy 안전망에서 호출되며
    /// 여러 번 호출해도 안전하다(idempotent).
    /// </summary>
    public void Dispose()
    {
        _active = false;
        _disposed = true;
        if (_mainBuffer != null)
        {
            _mainBuffer.Dispose();
            _mainBuffer = null;
        }

        if (_leaderBuffer != null)
        {
            _leaderBuffer.Dispose();
            _leaderBuffer = null;
        }
    }

    private void OnDestroy()
    {
        Dispose(); // 안전망: 정상 경로에서는 CrowdRoot.Shutdown이 이미 해제했다.
    }
}

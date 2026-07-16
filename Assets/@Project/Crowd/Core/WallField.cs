using System;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;

/// <summary>
/// 베이크된 <see cref="WallSdfAsset"/>를 로드해 read-only로 조회하는 런타임 컨테이너다(Phase C 단계 1).
/// asset의 거리 payload를 Persistent <see cref="NativeArray{T}"/>로 1회 복사해 소유하며, 이후 값은 불변이다.
/// 조회는 순수 float 산술(bilinear + central difference)이라 결정적이다.
///
/// 이번 단계에서는 이동/시뮬 로직에 연결하지 않는다. API(Phi/Gradient)만 준비하며,
/// 소유자는 이 인스턴스를 자신의 lifecycle에서 <see cref="Dispose"/>해야 한다(NativeArray 누수 방지).
/// allowUnsafeCode=0을 유지하려고 NativeArray 안전 인덱싱만 사용한다.
/// </summary>
public sealed class WallField : IDisposable
{
    private NativeArray<float> _dist;
    private bool _loaded;

    private float _originX;
    private float _originZ;
    private float _cellSize;
    private float _invCellSize;
    private int _cols;
    private int _rows;
    private float _maxDistance;
    private float _bilinearBias;

    /// <summary>거리장이 로드되어 조회 가능한 상태인지 여부다.</summary>
    public bool IsLoaded => _loaded;

    /// <summary>열 수(X)다.</summary>
    public int Cols => _cols;

    /// <summary>행 수(Z)다.</summary>
    public int Rows => _rows;

    /// <summary>셀 크기(m)다.</summary>
    public float CellSize => _cellSize;

    /// <summary>거리 clamp 상한(m)이다.</summary>
    public float MaxDistance => _maxDistance;

    /// <summary>권장 tunneling 방지 bias(m)다(Stage 2 solver가 조회 시 빼서 사용).</summary>
    public float BilinearBias => _bilinearBias;

    /// <summary>
    /// asset의 payload를 Persistent NativeArray로 복사해 로드한다. 같은 인스턴스에 재로드 시 이전 배열을 먼저 해제한다.
    /// </summary>
    /// <exception cref="ArgumentNullException">asset 또는 그 payload가 null이면 발생한다.</exception>
    /// <exception cref="ArgumentException">payload 크기가 그리드 셀 수와 맞지 않으면 발생한다.</exception>
    public void Load(WallSdfAsset asset)
    {
        if (asset == null)
        {
            throw new ArgumentNullException(nameof(asset));
        }

        if (asset.Payload == null)
        {
            throw new ArgumentNullException(nameof(asset), "WallSdfAsset.Payload(.bytes)가 null입니다.");
        }

        int cellCount = asset.CellCount;
        byte[] bytes = asset.Payload.bytes;
        if (cellCount <= 0 || bytes == null || bytes.Length != cellCount * sizeof(float))
        {
            throw new ArgumentException(
                $"WallSdf payload 크기 불일치: bytes={(bytes == null ? -1 : bytes.Length)}, expected={cellCount * sizeof(float)}",
                nameof(asset));
        }

        if (_loaded && _dist.IsCreated)
        {
            _dist.Dispose();
        }

        float[] managed = new float[cellCount];
        Buffer.BlockCopy(bytes, 0, managed, 0, bytes.Length);

        _dist = new NativeArray<float>(cellCount, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
        _dist.CopyFrom(managed);

        _originX = asset.OriginX;
        _originZ = asset.OriginZ;
        _cellSize = asset.CellSize;
        _invCellSize = 1f / asset.CellSize;
        _cols = asset.Cols;
        _rows = asset.Rows;
        _maxDistance = asset.MaxDistance;
        _bilinearBias = asset.BilinearBias;
        _loaded = true;
    }

    /// <summary>
    /// world XZ (x,z)에서의 signed distance(내부 음수/외부 양수, m)를 bilinear 보간해 반환한다.
    /// 그리드 밖은 가장자리 셀 값(벽에서 먼 +MaxDistance 부근)으로 clamp된다.
    /// </summary>
    public float Phi(float x, float z)
    {
        // 셀 중심 인덱스 공간으로 변환(셀 중심이 (col+0.5)*cell + origin이라 -0.5).
        float fx = (x - _originX) * _invCellSize - 0.5f;
        float fz = (z - _originZ) * _invCellSize - 0.5f;

        int c0 = (int)math.floor(fx);
        int r0 = (int)math.floor(fz);
        float tx = fx - c0;
        float tz = fz - r0;

        int c0c = math.clamp(c0, 0, _cols - 1);
        int c1c = math.clamp(c0 + 1, 0, _cols - 1);
        int r0c = math.clamp(r0, 0, _rows - 1);
        int r1c = math.clamp(r0 + 1, 0, _rows - 1);

        float d00 = _dist[r0c * _cols + c0c];
        float d10 = _dist[r0c * _cols + c1c];
        float d01 = _dist[r1c * _cols + c0c];
        float d11 = _dist[r1c * _cols + c1c];

        float dx0 = math.lerp(d00, d10, tx);
        float dx1 = math.lerp(d01, d11, tx);
        return math.lerp(dx0, dx1, tz);
    }

    /// <summary>
    /// world XZ (x,z)에서의 거리장 gradient(거리가 증가하는 방향 = 벽에서 멀어지는 방향)를 정규화해 반환한다.
    /// central difference로 계산하며, medial-axis/평탄 개활지에서 크기가 사실상 0이면 float2.zero를 반환한다.
    /// </summary>
    public float2 Gradient(float x, float z)
    {
        float h = _cellSize;
        float gx = Phi(x + h, z) - Phi(x - h, z);
        float gz = Phi(x, z + h) - Phi(x, z - h);
        float2 g = new float2(gx, gz);
        float lenSq = math.lengthsq(g);
        if (lenSq < 1e-12f)
        {
            return float2.zero;
        }

        return g * math.rsqrt(lenSq);
    }

    /// <summary>소유한 Persistent NativeArray를 해제한다. 소유자 lifecycle에서 반드시 호출한다.</summary>
    public void Dispose()
    {
        if (_dist.IsCreated)
        {
            _dist.Dispose();
        }

        _loaded = false;
    }
}

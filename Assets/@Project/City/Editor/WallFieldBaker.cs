#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>
/// 정적 도시 벽에서 2D signed distance field(SDF)를 에디터 1회 베이크하는 결정적 도구다(Phase C 단계 1).
/// CC.Move가 보는 정적 벽(건물 37 + Parks/Vehicles/StreetProps 결합 non-convex MeshCollider)만 입력이며,
/// 결과를 <see cref="WallSdfAsset"/>(메타) + 동반 .bytes(거리 raw 바이너리)로 <c>City/Generated</c>에 직렬화한다.
/// 런타임 재베이크는 하지 않으며, 이번 단계에서는 이동 로직에 연결하지 않는다(WallField가 로드만).
///
/// 표현: 수평 이동 캡슐이 실제로 부딪히는 것을 재현하려고, 각 mesh를 캡슐 y-band 안 여러 높이에서 슬라이스해
/// 2D footprint 세그먼트를 얻는다(수평 바닥/잔디/지붕은 슬라이스 평면과 교차하지 않아 자연히 제외되고,
/// 벽 루프의 scanline even-odd fill로 건물 내부가 채워지며, band 위 오버행은 walkable-under로 남는다).
/// signed distance = 내부(음수)/외부(양수), 셀 중심에서 최근접 세그먼트까지 거리(가속 그리드로 국소 탐색).
///
/// 결정성: collider 순서(이름 오름차순 고정), 정점/삼각형 순회, 슬라이스/버킷 순서가 모두 고정이라
/// 같은 입력 → 같은 필드 → 같은 sourceHash/payloadCrc다.
/// </summary>
public static class WallFieldBaker
{
    private const string ScenePath = "Assets/@Project/Scenes/GameScene.unity";
    private const string GeneratedFolder = "Assets/@Project/City/Generated";
    private const string AssetPath = GeneratedFolder + "/WallSdf.asset";
    private const string BytesPath = GeneratedFolder + "/WallSdf.bytes";

    // 캡슐 높이(Human.prefab에 baked; GameSceneSetup 상수와 동일). y-band 상한 산정에만 쓴다(이동/시뮬 불변).
    private const float CapsuleHeight = 1.8f;

    /// <summary>베이크 파라미터. 이질감 게이트에서 조정 가능하도록 노출한다.</summary>
    public struct BakeParams
    {
        public float CellSize;         // 셀 크기(m). 시작값 0.1(설계 0.05~0.2).
        public float MaxDistance;      // 거리 clamp 상한(m). 캡슐 반경 대비 넉넉히.
        public float GroundY;          // 걷기 표면 Y(config.GroundY).
        public float MaxScale;         // 중립 최대 스케일(config.NeutralMaxScale).
        public float BandBottomOffset; // 최하 슬라이스를 지면에서 이만큼 올려 잔디/바닥 grazing을 제외.
        public int SliceCount;         // 캡슐 band 내 슬라이스 수.
        public float BilinearBias;     // 권장 tunneling 방지 bias(m). 저장은 참 SDF, bias는 메타로만.

        public static BakeParams Default(float groundY, float maxScale)
        {
            float cell = 0.1f;
            return new BakeParams
            {
                CellSize = cell,
                MaxDistance = 2.5f,
                GroundY = groundY,
                MaxScale = maxScale,
                BandBottomOffset = 0.3f,
                SliceCount = 6,
                BilinearBias = 0.5f * cell,
            };
        }
    }

    /// <summary>베이크 결과(에셋 I/O 없이 순수 계산). 검증기가 재사용/재베이크 비교에 쓴다.</summary>
    public sealed class BakeResult
    {
        public float OriginX;
        public float OriginZ;
        public float CellSize;
        public int Cols;
        public int Rows;
        public float YMin;
        public float YMax;
        public float MaxDistance;
        public float BilinearBias;
        public int ColliderCount;
        public int TriangleCount;
        public int SegmentCount;
        public float[] Dist;          // Cols*Rows, row-major idx=row*Cols+col, signed.
        public bool[] Inside;         // Cols*Rows, true=벽 내부.
        public byte[] PayloadBytes;   // Dist를 LE float32로.
        public uint SourceHash;
        public uint PayloadCrc;
        public string[] ColliderNames;
        public int[] ColliderInsideCells;   // collider별 AABB 내 inside 셀 수(leak 진단).
        public Rect[] ColliderFootprint;    // collider별 2D AABB(XZ).
        public float RegionMinX;
        public float RegionMaxX;
        public float RegionMinZ;
        public float RegionMaxZ;
    }

    [MenuItem("AF/CrowdCity/Bake Wall SDF")]
    public static void BakeFromMenu()
    {
        Bake(null, "menu");
    }

    /// <summary>배치 진입점. -sdfCell / -sdfMaxDist / -sdfSlices 커스텀 인자를 파싱해 베이크한다.</summary>
    public static void BakeFromBatch()
    {
        int exitCode = 0;
        try
        {
            BakeParams? overrides = ParseArgOverrides();
            Bake(overrides, "batch");
        }
        catch (Exception e)
        {
            Debug.LogError("[WallFieldBaker] 실패: " + e);
            exitCode = 4;
        }

        EditorApplication.Exit(exitCode);
    }

    private static BakeParams? ParseArgOverrides()
    {
        string[] args = Environment.GetCommandLineArgs();
        float? cell = null, maxDist = null;
        int? slices = null;
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == "-sdfCell" && float.TryParse(args[i + 1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float c)) cell = c;
            else if (args[i] == "-sdfMaxDist" && float.TryParse(args[i + 1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float m)) maxDist = m;
            else if (args[i] == "-sdfSlices" && int.TryParse(args[i + 1], out int s)) slices = s;
        }

        if (cell == null && maxDist == null && slices == null)
        {
            return null;
        }

        // groundY/maxScale은 Bake 내부에서 채워지므로 여기선 표식만; 실제 병합은 Bake에서 한다.
        var p = new BakeParams { CellSize = cell ?? -1f, MaxDistance = maxDist ?? -1f, SliceCount = slices ?? -1 };
        return p;
    }

    /// <summary>씬을 열어 cityRoot/config를 확보하고 베이크 후 asset(.asset + .bytes)을 기록한다.</summary>
    public static void Bake(BakeParams? overrides, string label)
    {
        Debug.Log($"[WallFieldBaker] 시작 label={label}");
        EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);

        ResolveScene(out Transform cityRoot, out float groundY, out float maxScale);

        BakeParams p = BakeParams.Default(groundY, maxScale);
        if (overrides.HasValue)
        {
            BakeParams o = overrides.Value;
            if (o.CellSize > 0f) { p.CellSize = o.CellSize; p.BilinearBias = 0.5f * o.CellSize; }
            if (o.MaxDistance > 0f) p.MaxDistance = o.MaxDistance;
            if (o.SliceCount > 0) p.SliceCount = o.SliceCount;
        }

        BakeResult r = ComputeBake(cityRoot, p);

        WriteAssets(r);

        Debug.Log($"[WallFieldBaker] 완료: cols={r.Cols} rows={r.Rows} cells={r.Cols * r.Rows} " +
                  $"colliders={r.ColliderCount} tris={r.TriangleCount} segs={r.SegmentCount} " +
                  $"cell={r.CellSize} maxDist={r.MaxDistance} sourceHash={r.SourceHash:X8} crc={r.PayloadCrc:X8} " +
                  $"bytes={r.PayloadBytes.Length} -> {AssetPath}");
    }

    /// <summary>씬에서 City root와 groundY/maxScale을 GameSceneController(config)로부터 확보한다(harness와 동일 경로).</summary>
    internal static void ResolveScene(out Transform cityRoot, out float groundY, out float maxScale)
    {
        GameSceneController gsc = UnityEngine.Object.FindFirstObjectByType<GameSceneController>();
        if (gsc == null)
        {
            throw new InvalidOperationException("GameScene에서 GameSceneController를 찾지 못했습니다.");
        }

        SerializedObject so = new SerializedObject(gsc);
        GameConfigSO config = so.FindProperty("config").objectReferenceValue as GameConfigSO;
        cityRoot = so.FindProperty("cityRoot").objectReferenceValue as Transform;
        if (config == null || cityRoot == null)
        {
            throw new InvalidOperationException("GameSceneController에서 config 또는 cityRoot를 읽지 못했습니다.");
        }

        groundY = config.GroundY;
        maxScale = config.NeutralMaxScale;
    }

    /// <summary>순수 계산 베이크(에셋 I/O 없음). 같은 입력 → 같은 결과(결정적).</summary>
    internal static BakeResult ComputeBake(Transform cityRoot, BakeParams p)
    {
        // ---- 1) 걷기 가능 영역(ComputeWalkableRegion과 동일 규약: Ground renderer world bounds를 2m inset) ----
        Transform ground = cityRoot.Find("Ground");
        if (ground == null)
        {
            throw new InvalidOperationException("cityRoot 아래에 Ground가 없습니다.");
        }

        Renderer[] groundRenderers = ground.GetComponentsInChildren<Renderer>();
        if (groundRenderers.Length == 0)
        {
            throw new InvalidOperationException("Ground에 renderer가 없습니다.");
        }

        Bounds gb = groundRenderers[0].bounds;
        for (int i = 1; i < groundRenderers.Length; i++) gb.Encapsulate(groundRenderers[i].bounds);
        float regionMinX = gb.min.x + 2f;
        float regionMaxX = gb.max.x - 2f;
        float regionMinZ = gb.min.z + 2f;
        float regionMaxZ = gb.max.z - 2f;

        float cell = p.CellSize;
        float originX = regionMinX;
        float originZ = regionMinZ;
        int cols = Mathf.Max(1, Mathf.CeilToInt((regionMaxX - regionMinX) / cell));
        int rows = Mathf.Max(1, Mathf.CeilToInt((regionMaxZ - regionMinZ) / cell));

        // ---- 2) 정적 벽 collider를 결정적 순서(이름 오름차순)로 수집 ----
        MeshCollider[] allColliders = cityRoot.GetComponentsInChildren<MeshCollider>(true);
        var colliders = new List<MeshCollider>();
        for (int i = 0; i < allColliders.Length; i++)
        {
            if (allColliders[i].sharedMesh != null) colliders.Add(allColliders[i]);
        }

        colliders.Sort((a, b) => string.CompareOrdinal(a.gameObject.name, b.gameObject.name));

        // ---- 3) 캡슐 y-band 슬라이스 높이 ----
        float bandBottom = p.GroundY + p.BandBottomOffset;
        float bandTop = p.GroundY + CapsuleHeight * p.MaxScale;
        int sliceCount = Mathf.Max(1, p.SliceCount);
        var sliceY = new float[sliceCount];
        for (int k = 0; k < sliceCount; k++)
        {
            sliceY[k] = sliceCount == 1 ? 0.5f * (bandBottom + bandTop)
                : bandBottom + (bandTop - bandBottom) * k / (sliceCount - 1);
        }

        // ---- 4) 각 collider mesh를 world로 변환, y-band 교차 삼각형을 슬라이스 → 2D 세그먼트 ----
        // 세그먼트는 슬라이스별로 모으고(내부 scanline fill), 전역 리스트(거리)에도 누적한다.
        var segAx = new List<float>();
        var segAz = new List<float>();
        var segBx = new List<float>();
        var segBz = new List<float>();
        var segSlice = new List<int>();
        var inside = new bool[cols * rows];

        int triCount = 0;
        var colliderNames = new string[colliders.Count];
        var colliderFootprint = new Rect[colliders.Count];

        // sourceHash: 입력에서 계산(collider 순서/mesh/params). 같은 입력 → 같은 값.
        uint sourceHash = FnvOffset;
        FnvParams(ref sourceHash, cell, p.MaxDistance, sliceCount, bandBottom, bandTop, p.GroundY, originX, originZ, cols, rows);

        // 슬라이스 재사용 버퍼.
        var rowCrossings = new List<float>[rows];
        for (int r = 0; r < rows; r++) rowCrossings[r] = new List<float>();

        for (int ci = 0; ci < colliders.Count; ci++)
        {
            MeshCollider mc = colliders[ci];
            Mesh mesh = mc.sharedMesh;
            colliderNames[ci] = mc.gameObject.name;

            Vector3[] verts = mesh.vertices;
            int[] tris = mesh.triangles;
            Matrix4x4 l2w = mc.transform.localToWorldMatrix;

            // sourceHash에 mesh/transform 반영.
            FnvString(ref sourceHash, mc.gameObject.name);
            FnvInt(ref sourceHash, verts.Length);
            FnvInt(ref sourceHash, tris.Length);

            // world 정점 미리 변환.
            var wv = new Vector3[verts.Length];
            for (int v = 0; v < verts.Length; v++)
            {
                wv[v] = l2w.MultiplyPoint3x4(verts[v]);
                FnvFloat(ref sourceHash, wv[v].x);
                FnvFloat(ref sourceHash, wv[v].y);
                FnvFloat(ref sourceHash, wv[v].z);
            }

            float fpMinX = float.MaxValue, fpMaxX = float.MinValue, fpMinZ = float.MaxValue, fpMaxZ = float.MinValue;

            for (int t = 0; t + 2 < tris.Length; t += 3)
            {
                Vector3 a = wv[tris[t]];
                Vector3 b = wv[tris[t + 1]];
                Vector3 c = wv[tris[t + 2]];

                float triMinY = Mathf.Min(a.y, Mathf.Min(b.y, c.y));
                float triMaxY = Mathf.Max(a.y, Mathf.Max(b.y, c.y));
                if (triMaxY < bandBottom || triMinY > bandTop)
                {
                    continue; // 캡슐 band 밖(지면 아래/오버행 위) 삼각형은 제외.
                }

                triCount++;

                for (int k = 0; k < sliceCount; k++)
                {
                    if (TriPlaneSegment(a, b, c, sliceY[k], out Vector2 s0, out Vector2 s1))
                    {
                        segAx.Add(s0.x); segAz.Add(s0.y);
                        segBx.Add(s1.x); segBz.Add(s1.y);
                        segSlice.Add(k);
                        if (s0.x < fpMinX) fpMinX = s0.x; if (s0.x > fpMaxX) fpMaxX = s0.x;
                        if (s1.x < fpMinX) fpMinX = s1.x; if (s1.x > fpMaxX) fpMaxX = s1.x;
                        if (s0.y < fpMinZ) fpMinZ = s0.y; if (s0.y > fpMaxZ) fpMaxZ = s0.y;
                        if (s1.y < fpMinZ) fpMinZ = s1.y; if (s1.y > fpMaxZ) fpMaxZ = s1.y;
                    }
                }
            }

            colliderFootprint[ci] = fpMaxX >= fpMinX
                ? Rect.MinMaxRect(fpMinX, fpMinZ, fpMaxX, fpMaxZ)
                : new Rect(0, 0, 0, 0);
        }

        // ---- 5) 슬라이스별 scanline even-odd fill로 inside[] union ----
        // 서로 다른 높이의 겹치는 닫힌 루프가 even-odd를 뒤집지 않도록, 세그먼트 슬라이스 태그로 분리해 fill한 뒤 OR한다.
        FillInsideBySlices(segAx, segAz, segBx, segBz, segSlice, sliceCount, originX, originZ, cell, cols, rows, rowCrossings, inside);

        // ---- 6) 거리장: 전역 세그먼트에 대한 최근접 거리(가속 그리드) ----
        int segCount = segAx.Count;
        float maxDist = p.MaxDistance;
        BuildSegmentAccel(segAx, segAz, segBx, segBz, maxDist, out float accelOriginX, out float accelOriginZ,
            out int accelCols, out int accelRows, out List<int>[] accelBuckets);

        var dist = new float[cols * rows];
        for (int r = 0; r < rows; r++)
        {
            float cz = originZ + (r + 0.5f) * cell;
            for (int col = 0; col < cols; col++)
            {
                float cx = originX + (col + 0.5f) * cell;
                float best = maxDist;

                int ac = Mathf.FloorToInt((cx - accelOriginX) / maxDist);
                int ar = Mathf.FloorToInt((cz - accelOriginZ) / maxDist);
                for (int dar = -1; dar <= 1; dar++)
                {
                    int rr = ar + dar;
                    if (rr < 0 || rr >= accelRows) continue;
                    for (int dac = -1; dac <= 1; dac++)
                    {
                        int cc = ac + dac;
                        if (cc < 0 || cc >= accelCols) continue;
                        List<int> bucket = accelBuckets[rr * accelCols + cc];
                        if (bucket == null) continue;
                        for (int bi = 0; bi < bucket.Count; bi++)
                        {
                            int si = bucket[bi];
                            float d = DistPointSeg(cx, cz, segAx[si], segAz[si], segBx[si], segBz[si]);
                            if (d < best) best = d;
                        }
                    }
                }

                int idx = r * cols + col;
                float signed = inside[idx] ? -best : best;
                if (signed > maxDist) signed = maxDist;
                if (signed < -maxDist) signed = -maxDist;
                dist[idx] = signed;
            }
        }

        // ---- 7) collider별 footprint AABB 내 inside 셀 수(leak 진단) ----
        var colliderInside = new int[colliders.Count];
        for (int ci = 0; ci < colliders.Count; ci++)
        {
            Rect fp = colliderFootprint[ci];
            if (fp.width <= 0f && fp.height <= 0f) { colliderInside[ci] = 0; continue; }
            int c0 = Mathf.Clamp(Mathf.FloorToInt((fp.xMin - originX) / cell), 0, cols - 1);
            int c1 = Mathf.Clamp(Mathf.CeilToInt((fp.xMax - originX) / cell), 0, cols - 1);
            int r0 = Mathf.Clamp(Mathf.FloorToInt((fp.yMin - originZ) / cell), 0, rows - 1);
            int r1 = Mathf.Clamp(Mathf.CeilToInt((fp.yMax - originZ) / cell), 0, rows - 1);
            int cnt = 0;
            for (int r = r0; r <= r1; r++)
                for (int col = c0; col <= c1; col++)
                    if (inside[r * cols + col]) cnt++;
            colliderInside[ci] = cnt;
        }

        // ---- 8) payload 바이트 + CRC ----
        var payload = new byte[dist.Length * sizeof(float)];
        Buffer.BlockCopy(dist, 0, payload, 0, payload.Length);
        uint crc = Crc32(payload);

        return new BakeResult
        {
            OriginX = originX,
            OriginZ = originZ,
            CellSize = cell,
            Cols = cols,
            Rows = rows,
            YMin = bandBottom,
            YMax = bandTop,
            MaxDistance = maxDist,
            BilinearBias = p.BilinearBias,
            ColliderCount = colliders.Count,
            TriangleCount = triCount,
            SegmentCount = segCount,
            Dist = dist,
            Inside = inside,
            PayloadBytes = payload,
            SourceHash = sourceHash,
            PayloadCrc = crc,
            ColliderNames = colliderNames,
            ColliderInsideCells = colliderInside,
            ColliderFootprint = colliderFootprint,
            RegionMinX = regionMinX,
            RegionMaxX = regionMaxX,
            RegionMinZ = regionMinZ,
            RegionMaxZ = regionMaxZ,
        };
    }

    // 슬라이스별 scanline even-odd fill(닫힌 루프 union). 메인 패스에서 태그된 세그먼트를 재사용한다(mesh 재read 없음).
    private static void FillInsideBySlices(
        List<float> ax, List<float> az, List<float> bx, List<float> bz, List<int> slice,
        int sliceCount, float originX, float originZ, float cell, int cols, int rows,
        List<float>[] rowCrossings, bool[] inside)
    {
        int n = ax.Count;
        for (int k = 0; k < sliceCount; k++)
        {
            for (int r = 0; r < rows; r++) rowCrossings[r].Clear();

            for (int i = 0; i < n; i++)
            {
                if (slice[i] != k) continue;
                float x0 = ax[i], z0 = az[i], x1 = bx[i], z1 = bz[i];
                float zLo = Mathf.Min(z0, z1);
                float zHi = Mathf.Max(z0, z1);
                int rLo = Mathf.Max(0, Mathf.CeilToInt((zLo - originZ) / cell - 0.5f));
                int rHi = Mathf.Min(rows - 1, Mathf.FloorToInt((zHi - originZ) / cell - 0.5f));
                for (int r = rLo; r <= rHi; r++)
                {
                    float zr = originZ + (r + 0.5f) * cell;
                    // half-open 규약으로 정점 걸침 이중계수 방지.
                    if ((z0 <= zr) != (z1 <= zr))
                    {
                        float tt = (zr - z0) / (z1 - z0);
                        float xr = x0 + tt * (x1 - x0);
                        rowCrossings[r].Add(xr);
                    }
                }
            }

            // 각 행 even-odd fill → inside union.
            for (int r = 0; r < rows; r++)
            {
                List<float> xs = rowCrossings[r];
                if (xs.Count < 2) continue;
                xs.Sort();
                int rowBase = r * cols;
                for (int i = 0; i + 1 < xs.Count; i += 2)
                {
                    int c0 = Mathf.CeilToInt((xs[i] - originX) / cell - 0.5f);
                    int c1 = Mathf.FloorToInt((xs[i + 1] - originX) / cell - 0.5f);
                    if (c0 < 0) c0 = 0;
                    if (c1 > cols - 1) c1 = cols - 1;
                    for (int col = c0; col <= c1; col++) inside[rowBase + col] = true;
                }
            }
        }
    }

    // 삼각형과 수평 평면 Y=planeY의 교차 세그먼트(XZ)를 구한다. 걸치지 않으면 false.
    private static bool TriPlaneSegment(Vector3 a, Vector3 b, Vector3 c, float planeY, out Vector2 s0, out Vector2 s1)
    {
        s0 = default;
        s1 = default;
        Span<Vector2> pts = stackalloc Vector2[3];
        int n = 0;
        AddEdgeCross(a, b, planeY, pts, ref n);
        AddEdgeCross(b, c, planeY, pts, ref n);
        AddEdgeCross(c, a, planeY, pts, ref n);
        if (n < 2) return false;
        s0 = pts[0];
        s1 = pts[1];
        return true;
    }

    private static void AddEdgeCross(Vector3 a, Vector3 b, float planeY, Span<Vector2> pts, ref int n)
    {
        bool aBelow = a.y < planeY;
        bool bBelow = b.y < planeY;
        if (aBelow == bBelow) return;
        if (n >= 2) return;
        float t = (planeY - a.y) / (b.y - a.y);
        pts[n++] = new Vector2(a.x + t * (b.x - a.x), a.z + t * (b.z - a.z));
    }

    // 세그먼트 최근접 거리 가속 그리드(accelCell = maxDist). 각 세그먼트를 그 AABB가 겹치는 셀에 버킷팅.
    private static void BuildSegmentAccel(
        List<float> ax, List<float> az, List<float> bx, List<float> bz, float accelCell,
        out float originX, out float originZ, out int accelCols, out int accelRows, out List<int>[] buckets)
    {
        int n = ax.Count;
        if (n == 0)
        {
            originX = 0; originZ = 0; accelCols = 1; accelRows = 1;
            buckets = new List<int>[1];
            return;
        }

        float minX = float.MaxValue, minZ = float.MaxValue, maxX = float.MinValue, maxZ = float.MinValue;
        for (int i = 0; i < n; i++)
        {
            float lx = Mathf.Min(ax[i], bx[i]), hx = Mathf.Max(ax[i], bx[i]);
            float lz = Mathf.Min(az[i], bz[i]), hz = Mathf.Max(az[i], bz[i]);
            if (lx < minX) minX = lx; if (hx > maxX) maxX = hx;
            if (lz < minZ) minZ = lz; if (hz > maxZ) maxZ = hz;
        }

        originX = minX;
        originZ = minZ;
        accelCols = Mathf.Max(1, Mathf.FloorToInt((maxX - minX) / accelCell) + 1);
        accelRows = Mathf.Max(1, Mathf.FloorToInt((maxZ - minZ) / accelCell) + 1);
        buckets = new List<int>[accelCols * accelRows];

        for (int i = 0; i < n; i++)
        {
            int c0 = Mathf.Clamp(Mathf.FloorToInt((Mathf.Min(ax[i], bx[i]) - originX) / accelCell), 0, accelCols - 1);
            int c1 = Mathf.Clamp(Mathf.FloorToInt((Mathf.Max(ax[i], bx[i]) - originX) / accelCell), 0, accelCols - 1);
            int r0 = Mathf.Clamp(Mathf.FloorToInt((Mathf.Min(az[i], bz[i]) - originZ) / accelCell), 0, accelRows - 1);
            int r1 = Mathf.Clamp(Mathf.FloorToInt((Mathf.Max(az[i], bz[i]) - originZ) / accelCell), 0, accelRows - 1);
            for (int r = r0; r <= r1; r++)
            {
                for (int c = c0; c <= c1; c++)
                {
                    int bidx = r * accelCols + c;
                    (buckets[bidx] ??= new List<int>()).Add(i);
                }
            }
        }
    }

    private static float DistPointSeg(float px, float pz, float ax, float az, float bx, float bz)
    {
        float abx = bx - ax, abz = bz - az;
        float apx = px - ax, apz = pz - az;
        float denom = abx * abx + abz * abz;
        float t = denom > 1e-20f ? (apx * abx + apz * abz) / denom : 0f;
        if (t < 0f) t = 0f; else if (t > 1f) t = 1f;
        float cx = ax + t * abx, cz = az + t * abz;
        float dx = px - cx, dz = pz - cz;
        return Mathf.Sqrt(dx * dx + dz * dz);
    }

    private static void WriteAssets(BakeResult r)
    {
        EnsureFolder(GeneratedFolder);

        // .bytes(raw payload) 먼저 기록·임포트 후 TextAsset 참조를 SO에 담는다.
        // File I/O는 프로세스 CWD에 의존하지 않도록 project 절대경로로 쓴다(AssetDatabase 경로는 project-relative 유지).
        string projectRoot = Directory.GetParent(Application.dataPath).FullName;
        string bytesAbs = Path.Combine(projectRoot, BytesPath);
        Directory.CreateDirectory(Path.GetDirectoryName(bytesAbs));
        File.WriteAllBytes(bytesAbs, r.PayloadBytes);
        AssetDatabase.ImportAsset(BytesPath, ImportAssetOptions.ForceUpdate);
        TextAsset payload = AssetDatabase.LoadAssetAtPath<TextAsset>(BytesPath);
        if (payload == null)
        {
            throw new InvalidOperationException("WallSdf.bytes를 TextAsset으로 임포트하지 못했습니다.");
        }

        WallSdfAsset asset = AssetDatabase.LoadAssetAtPath<WallSdfAsset>(AssetPath);
        bool created = false;
        if (asset == null)
        {
            asset = ScriptableObject.CreateInstance<WallSdfAsset>();
            created = true;
        }

        asset.EditorInitialize(
            r.OriginX, r.OriginZ, r.CellSize, r.Cols, r.Rows,
            r.YMin, r.YMax, r.MaxDistance, r.BilinearBias,
            r.ColliderCount, r.TriangleCount, r.SourceHash, r.PayloadCrc, payload);

        if (created)
        {
            AssetDatabase.CreateAsset(asset, AssetPath);
        }
        else
        {
            EditorUtility.SetDirty(asset);
        }

        AssetDatabase.SaveAssetIfDirty(asset);
        AssetDatabase.SaveAssets();
    }

    private static void EnsureFolder(string folder)
    {
        if (AssetDatabase.IsValidFolder(folder)) return;
        string parent = Path.GetDirectoryName(folder).Replace('\\', '/');
        string leaf = Path.GetFileName(folder);
        EnsureFolder(parent);
        AssetDatabase.CreateFolder(parent, leaf);
    }

    // ---- 해시 유틸(FNV-1a 32bit) ----
    private const uint FnvOffset = 2166136261u;
    private const uint FnvPrime = 16777619u;

    private static void FnvByte(ref uint h, byte b) { h = (h ^ b) * FnvPrime; }

    private static void FnvInt(ref uint h, int v)
    {
        FnvByte(ref h, (byte)v); FnvByte(ref h, (byte)(v >> 8)); FnvByte(ref h, (byte)(v >> 16)); FnvByte(ref h, (byte)(v >> 24));
    }

    private static void FnvFloat(ref uint h, float f)
    {
        FnvInt(ref h, BitConverter.SingleToInt32Bits(f));
    }

    private static void FnvString(ref uint h, string s)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(s);
        for (int i = 0; i < bytes.Length; i++) FnvByte(ref h, bytes[i]);
    }

    private static void FnvParams(ref uint h, float cell, float maxDist, int slices, float bandBottom, float bandTop, float groundY, float originX, float originZ, int cols, int rows)
    {
        FnvFloat(ref h, cell); FnvFloat(ref h, maxDist); FnvInt(ref h, slices);
        FnvFloat(ref h, bandBottom); FnvFloat(ref h, bandTop); FnvFloat(ref h, groundY);
        FnvFloat(ref h, originX); FnvFloat(ref h, originZ); FnvInt(ref h, cols); FnvInt(ref h, rows);
    }

    // ---- CRC32 ----
    private static readonly uint[] Crc32Table = BuildCrc32Table();

    private static uint[] BuildCrc32Table()
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint c = i;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            table[i] = c;
        }

        return table;
    }

    internal static uint Crc32(byte[] data)
    {
        uint crc = 0xFFFFFFFFu;
        for (int i = 0; i < data.Length; i++) crc = Crc32Table[(crc ^ data[i]) & 0xFF] ^ (crc >> 8);
        return crc ^ 0xFFFFFFFFu;
    }

    // 검증기용: 현재 asset 경로 상수 노출.
    internal static string AssetPathConst => AssetPath;
    internal static string BytesPathConst => BytesPath;
}
#endif

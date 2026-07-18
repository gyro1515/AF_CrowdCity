#if UNITY_EDITOR
using System;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Human walk 애니메이션을 Vertex Animation Texture(VAT)로 에디터 1회 베이크하는 결정적 도구다(GPU-anim Stage 1 Chunk A).
/// Human.prefab을 인스턴스화해 SkinnedMeshRenderer의 sharedMesh 정점 수를 VAT 폭으로 삼고,
/// HumanWalk 클립을 프레임별로 <see cref="AnimationClip.SampleAnimation"/> → <see cref="SkinnedMeshRenderer.BakeMesh"/>로
/// 굽는다. BakeMesh 출력은 SMR-local(−90°X + 0.0001 scale) 이고 모델 부모가 180°Y라, 각 정점을
/// Human 루트 공간으로 <c>humanRoot.worldToLocalMatrix * smr.localToWorldMatrix</c>(법선은 그 역전치)로 변환해 저장한다.
///
/// 출력: 위치 텍스처 + 법선 텍스처(RGBAHalf/LINEAR/no-mip/Point/Clamp, 폭=정점수, 높이=행수) +
/// 동반 정적 메쉬(frame-0 토폴로지 + UV2=(i+0.5)/width 열 주소, bounds=전 프레임 union)를 NON-Resources 폴더에 직렬화한다.
/// 런타임 렌더러/셰이더/CrowdRoot/Human.cs는 건드리지 않는다(Chunk B). 이 도구는 SMR/Animator를 비활성화하지 않는다.
///
/// 루프: 22 샘플 = 21 간격. frame0 vs frame(N-1) seam을 측정해 loop-close(≈0)면 21 행 + t*rows modulo(seamless),
/// 아니면 22 행 + t*(rows-1) clamped를 택한다. 헤드리스 self-verify로 bake-space 정확도/seam 연속성/포맷/비영을 단언한다.
/// </summary>
public static class HumanVatBaker
{
    private const string PrefabPath = "Assets/@Project/Human/Prefabs/Human.prefab";
    private const string FbxPath = "Assets/@Project/Human/Externals/Human_Base.fbx";
    private const string ClipName = "HumanWalk";

    private const string OutputFolder = "Assets/@Project/Human/VAT";
    private const string PositionTexPath = OutputFolder + "/HumanWalkVatPosition.asset";
    private const string NormalTexPath = OutputFolder + "/HumanWalkVatNormal.asset";
    private const string MeshPath = OutputFolder + "/HumanWalkVatMesh.asset";

    // frame0 == frame(N-1) 판정 임계(m). 1mm 미만이면 loop-close로 보고 중복 마지막 행을 버린다.
    private const float SeamThreshold = 0.001f;

    // 셰이더가 가정하는 texel-center 열 주소. 소비자(Chunk B)와 반드시 일치해야 한다.
    // 열(정점) = (i + 0.5) / width  → 동반 메쉬 UV2.x 에 저장. 행 = (row + 0.5) / height.

    /// <summary>베이크 + self-verify 결과 요약(헤드리스 리포트/단언용).</summary>
    public sealed class BakeReport
    {
        public int VertexCount;
        public int SampleCount;      // 클립에서 계산한 원 샘플 수(예: 22)
        public int Rows;             // 실제 저장 행 수(loop-close면 SampleCount-1)
        public bool LoopClose;       // true=21행 modulo, false=22행 clamp
        public float Period;         // 초. loop-close: rows/frameRate, clamp: rows/frameRate
        public float SeamMax;        // frame0 vs frame(N-1) 최대 정점 델타(m)
        public float FrameRate;
        public float ClipLength;
        public string TexFormat;
        public int TexWidth, TexHeight;
        public Bounds UnionBounds;

        public bool VerifyPassed;
        public int MaxTextureSize;
        public float BakeSpaceMaxError;   // m. VAT 재구성 vs 라이브 재베이크 최대 오차
        public float ContinuityDelta;     // m. t=0 vs t=1-eps 재구성 델타
        public string VerifyDetail;

        public string SchemeName => LoopClose ? "LoopClose(21행 modulo)" : "Clamp(22행 clamp)";
    }

    [MenuItem("AF/CrowdCity/Bake Human VAT")]
    public static void BakeFromMenu()
    {
        BakeReport r = BakeAndVerify("menu");
        Debug.Log(FormatReport(r));
    }

    /// <summary>배치 진입점: 베이크 + self-verify 후 종료코드로 결과를 노출한다.</summary>
    public static void BakeFromBatch()
    {
        int exitCode = 0;
        try
        {
            BakeReport r = BakeAndVerify("batch");
            Debug.Log(FormatReport(r));
            if (!r.VerifyPassed)
            {
                Debug.LogError("[HumanVatBaker] self-verify 실패");
                exitCode = 5;
            }
        }
        catch (Exception e)
        {
            Debug.LogError("[HumanVatBaker] 실패: " + e);
            exitCode = 4;
        }

        EditorApplication.Exit(exitCode);
    }

    /// <summary>
    /// FBX 메쉬는 isReadable=0이라 배치에서 BakeMesh가 0 정점을 낸다.
    /// 베이크 동안만 Read/Write를 켜고 <c>finally</c>에서 isReadable=false로 원상 복원해(바이트 동일) FBX 에셋을 불변으로 유지한다.
    /// </summary>
    public static BakeReport BakeAndVerify(string label)
    {
        Debug.Log($"[HumanVatBaker] 시작 label={label}");

        var importer = AssetImporter.GetAtPath(FbxPath) as ModelImporter;
        if (importer == null)
        {
            throw new InvalidOperationException($"ModelImporter를 찾지 못했습니다: {FbxPath}");
        }

        bool toggled = false;
        if (!importer.isReadable)
        {
            importer.isReadable = true;
            importer.SaveAndReimport();
            toggled = true;
            Debug.Log("[HumanVatBaker] FBX mesh Read/Write 임시 활성화(베이크 후 복원)");
        }

        try
        {
            return BakeAndVerifyCore();
        }
        finally
        {
            if (toggled)
            {
                var imp2 = AssetImporter.GetAtPath(FbxPath) as ModelImporter;
                if (imp2 != null && imp2.isReadable)
                {
                    imp2.isReadable = false;
                    imp2.SaveAndReimport();
                    Debug.Log("[HumanVatBaker] FBX mesh Read/Write 복원 완료(isReadable=false)");
                }
            }
        }
    }

    private static BakeReport BakeAndVerifyCore()
    {
        GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
        if (prefab == null)
        {
            throw new InvalidOperationException($"프리팹을 찾지 못했습니다: {PrefabPath}");
        }

        AnimationClip clip = LoadClip();

        GameObject instance = (GameObject)UnityEngine.Object.Instantiate(prefab);
        Mesh tmp = new Mesh { name = "HumanVatBake_Temp" };
        var report = new BakeReport();

        try
        {
            Transform humanRoot = instance.transform;
            SkinnedMeshRenderer smr = instance.GetComponentInChildren<SkinnedMeshRenderer>(true);
            Animator animator = instance.GetComponentInChildren<Animator>(true);
            if (smr == null || smr.sharedMesh == null)
            {
                throw new InvalidOperationException("인스턴스에서 SkinnedMeshRenderer/sharedMesh를 찾지 못했습니다.");
            }

            if (animator == null)
            {
                throw new InvalidOperationException("인스턴스에서 Animator를 찾지 못했습니다(클립 경로 해석 루트).");
            }

            GameObject animRoot = animator.gameObject; // 클립 커브 경로는 이 노드(180°Y) 기준으로 해석된다.
            smr.updateWhenOffscreen = true; // 클론 한정: 오프스크린에서도 스킨 갱신(배치 BakeMesh 신뢰성).
            Mesh sharedMesh = smr.sharedMesh;
            int vertexCount = sharedMesh.vertexCount;
            Debug.Log($"[HumanVatBaker] diag: sharedMesh.isReadable={sharedMesh.isReadable} verts={vertexCount}");

            report.MaxTextureSize = SystemInfo.maxTextureSize;
            if (report.MaxTextureSize > 0 && report.MaxTextureSize < vertexCount)
            {
                throw new InvalidOperationException(
                    $"maxTextureSize({report.MaxTextureSize}) < vertexCount({vertexCount}): VAT 폭 초과.");
            }

            float frameRate = clip.frameRate > 0f ? clip.frameRate : 30f;
            int sampleCount = Mathf.Max(2, Mathf.RoundToInt(clip.length * frameRate) + 1);

            // ---- 1) 전 샘플(0..sampleCount-1) 베이크 → Human 루트 공간 위치/법선 ----
            var pos = new Vector3[sampleCount][];
            var nrm = new Vector3[sampleCount][];
            for (int f = 0; f < sampleCount; f++)
            {
                float time = f / frameRate;
                BakeFrame(clip, animRoot, smr, humanRoot, tmp, vertexCount, time, out pos[f], out nrm[f]);
            }

            float f0MaxMag = 0f;
            for (int i = 0; i < vertexCount; i++)
            {
                float mag = pos[0][i].magnitude;
                if (mag > f0MaxMag) f0MaxMag = mag;
            }

            Bounds worldRef = smr.bounds; // 라이브 SMR world AABB(교정 기준). 변환 결과가 이와 스케일이 맞아야 한다.
            Debug.Log($"[HumanVatBaker] diag: frame0 maxVertexMag={f0MaxMag:F4}m (0이면 BakeMesh 실패) " +
                      $"| liveSMR.bounds size={worldRef.size} (world 참조, ~사람키)");

            // ---- 2) seam 측정 → loop 스킴 선택 ----
            float seamMax = MaxVertexDelta(pos[0], pos[sampleCount - 1]);
            bool loopClose = seamMax <= SeamThreshold;
            int rows = loopClose ? sampleCount - 1 : sampleCount;
            float period = rows / frameRate;

            // ---- 3) union bounds(전 프레임 변환 후) ----
            Bounds union = ComputeUnionBounds(pos, sampleCount, vertexCount);

            // ---- 4) 텍스처 기록(위치/법선) ----
            Texture2D posTex = BuildTexture(pos, vertexCount, rows, isPosition: true);
            Texture2D nrmTex = BuildTexture(nrm, vertexCount, rows, isPosition: false);
            EnsureFolder(OutputFolder);
            WriteTextureAsset(posTex, PositionTexPath);
            WriteTextureAsset(nrmTex, NormalTexPath);

            // ---- 5) 동반 정적 메쉬(frame-0 토폴로지 + UV2 열주소 + union bounds) ----
            Mesh companion = BuildCompanionMesh(sharedMesh, pos[0], nrm[0], vertexCount, union);
            WriteMeshAsset(companion, MeshPath);

            report.VertexCount = vertexCount;
            report.SampleCount = sampleCount;
            report.Rows = rows;
            report.LoopClose = loopClose;
            report.Period = period;
            report.SeamMax = seamMax;
            report.FrameRate = frameRate;
            report.ClipLength = clip.length;
            report.TexFormat = posTex.format.ToString();
            report.TexWidth = posTex.width;
            report.TexHeight = posTex.height;
            report.UnionBounds = union;

            Debug.Log($"[HumanVatBaker] 베이크 완료: verts={vertexCount} samples={sampleCount} rows={rows} " +
                      $"scheme={report.SchemeName} seam={seamMax:F6}m period={period:F4}s fmt={report.TexFormat} " +
                      $"bounds(size={union.size}) -> {OutputFolder}");

            // ---- 6) 같은 클론으로 self-verify ----
            Verify(report, clip, animRoot, smr, humanRoot, tmp, vertexCount, frameRate);
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(tmp);
            UnityEngine.Object.DestroyImmediate(instance);
        }

        return report;
    }

    private static AnimationClip LoadClip()
    {
        UnityEngine.Object[] all = AssetDatabase.LoadAllAssetsAtPath(FbxPath);
        foreach (UnityEngine.Object o in all)
        {
            if (o is AnimationClip c && c.name == ClipName && !c.name.StartsWith("__preview"))
            {
                return c;
            }
        }

        throw new InvalidOperationException($"'{ClipName}' 클립을 {FbxPath}에서 찾지 못했습니다.");
    }

    /// <summary>
    /// BakeMesh 출력(기본 useScale=false)을 Human 루트 공간으로 옮기는 변환.
    /// 이 리그는 SMR(Human_Base)에 0.0001 lossyScale이 걸려 있고 본은 그만큼 큰 스케일로 스킨되므로,
    /// 기본 BakeMesh는 lossyScale을 제외한 ~월드 스케일(~1.9m) 정점을 낸다. 따라서 localToWorldMatrix(스케일 포함)로
    /// 곱하면 10000배 축소된다. 회전+이동만(scale=1) 적용해야 라이브 SMR world와 일치한다.
    /// 법선은 이 행렬의 역전치(회전만이라 회전 그대로)로 변환 후 정규화한다.
    /// </summary>
    private static Matrix4x4 ConversionMatrix(SkinnedMeshRenderer smr, Transform humanRoot)
    {
        Matrix4x4 rendererNoScale = Matrix4x4.TRS(smr.transform.position, smr.transform.rotation, Vector3.one);
        return humanRoot.worldToLocalMatrix * rendererNoScale;
    }

    /// <summary>한 프레임을 포즈→BakeMesh→Human 루트 공간 변환한다. 토폴로지 변경은 거부한다.</summary>
    private static void BakeFrame(
        AnimationClip clip, GameObject animRoot, SkinnedMeshRenderer smr, Transform humanRoot, Mesh tmp,
        int vertexCount, float time, out Vector3[] outPos, out Vector3[] outNrm)
    {
        clip.SampleAnimation(animRoot, time);
        smr.BakeMesh(tmp);
        if (tmp.vertexCount != vertexCount)
        {
            throw new InvalidOperationException(
                $"프레임 정점 수 변경 감지: {tmp.vertexCount} != {vertexCount} (VAT 폭 불변 위반).");
        }

        // 렌더러 노드(Human_Base)는 클립 애니메이션 대상이 아니지만, root motion이 상위(animRoot)에 실릴 수 있어
        // 매 프레임 행렬을 다시 읽어 안전하게 변환한다(라이브 SMR과 동일 공간 보장).
        Matrix4x4 m = ConversionMatrix(smr, humanRoot);
        Matrix4x4 nMat = m.inverse.transpose;

        Vector3[] bv = tmp.vertices;
        Vector3[] bn = tmp.normals;
        outPos = new Vector3[vertexCount];
        outNrm = new Vector3[vertexCount];
        bool hasNormals = bn != null && bn.Length == vertexCount;
        for (int i = 0; i < vertexCount; i++)
        {
            outPos[i] = m.MultiplyPoint3x4(bv[i]);
            Vector3 n = hasNormals ? nMat.MultiplyVector(bn[i]) : Vector3.up;
            outNrm[i] = n.sqrMagnitude > 1e-12f ? n.normalized : Vector3.up;
        }
    }

    private static float MaxVertexDelta(Vector3[] a, Vector3[] b)
    {
        float max = 0f;
        for (int i = 0; i < a.Length; i++)
        {
            float d = (a[i] - b[i]).magnitude;
            if (d > max) max = d;
        }

        return max;
    }

    private static Bounds ComputeUnionBounds(Vector3[][] pos, int sampleCount, int vertexCount)
    {
        Vector3 min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
        Vector3 max = new Vector3(float.MinValue, float.MinValue, float.MinValue);
        for (int f = 0; f < sampleCount; f++)
        {
            Vector3[] p = pos[f];
            for (int i = 0; i < vertexCount; i++)
            {
                min = Vector3.Min(min, p[i]);
                max = Vector3.Max(max, p[i]);
            }
        }

        var b = new Bounds();
        b.SetMinMax(min, max);
        return b;
    }

    /// <summary>행=프레임, 열=정점으로 half 텍스처를 채운다. RGBAHalf/LINEAR/no-mip/Point/Clamp.</summary>
    private static Texture2D BuildTexture(Vector3[][] data, int width, int rows, bool isPosition)
    {
        var tex = new Texture2D(width, rows, TextureFormat.RGBAHalf, mipChain: false, linear: true)
        {
            name = isPosition ? "HumanWalkVatPosition" : "HumanWalkVatNormal",
            filterMode = FilterMode.Point,
            wrapMode = TextureWrapMode.Clamp,
            anisoLevel = 0,
        };

        float w = isPosition ? 1f : 0f;
        var pixels = new Color[width * rows];
        for (int row = 0; row < rows; row++)
        {
            Vector3[] p = data[row];
            int rowBase = row * width;
            for (int x = 0; x < width; x++)
            {
                Vector3 v = p[x];
                pixels[rowBase + x] = new Color(v.x, v.y, v.z, w);
            }
        }

        tex.SetPixels(pixels);
        tex.Apply(updateMipmaps: false, makeNoLongerReadable: false); // verify가 GetPixel로 읽어야 하므로 readable 유지
        return tex;
    }

    /// <summary>frame-0 토폴로지의 정적 메쉬 + UV2 열주소 + union bounds. Optimize/RecalculateBounds 금지.</summary>
    private static Mesh BuildCompanionMesh(Mesh sharedMesh, Vector3[] pos0, Vector3[] nrm0, int vertexCount, Bounds union)
    {
        var mesh = new Mesh
        {
            name = "HumanWalkVatMesh",
            indexFormat = vertexCount > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16,
        };

        mesh.SetVertices(pos0);
        mesh.SetNormals(nrm0);

        // 셰이더가 텍스처 열을 texel-center로 읽도록 UV2.x = (i+0.5)/width.
        var uv2 = new Vector2[vertexCount];
        float invW = 1f / vertexCount;
        for (int i = 0; i < vertexCount; i++)
        {
            uv2[i] = new Vector2((i + 0.5f) * invW, 0f);
        }

        mesh.SetUVs(1, uv2);

        // 베이스 UV0(있으면) 보존(향후 텍스처링용). 없으면 생략.
        Vector2[] baseUv = sharedMesh.uv;
        if (baseUv != null && baseUv.Length == vertexCount)
        {
            mesh.SetUVs(0, baseUv);
        }

        mesh.subMeshCount = sharedMesh.subMeshCount;
        for (int s = 0; s < sharedMesh.subMeshCount; s++)
        {
            mesh.SetTriangles(sharedMesh.GetTriangles(s), s, calculateBounds: false);
        }

        mesh.bounds = union; // 셰이더가 위치를 덮어쓰므로 draw/culling용 union bounds를 명시.
        return mesh;
    }

    private static void WriteTextureAsset(Texture2D tex, string path)
    {
        if (AssetDatabase.LoadAssetAtPath<Texture2D>(path) != null)
        {
            AssetDatabase.DeleteAsset(path);
        }

        AssetDatabase.CreateAsset(tex, path);
    }

    private static void WriteMeshAsset(Mesh mesh, string path)
    {
        if (AssetDatabase.LoadAssetAtPath<Mesh>(path) != null)
        {
            AssetDatabase.DeleteAsset(path);
        }

        AssetDatabase.CreateAsset(mesh, path);
        AssetDatabase.SaveAssets();
    }

    // ---- self-verify: bake-space 정확도 / seam 연속성 / 포맷·차원 / 비영 ----
    private static void Verify(
        BakeReport r, AnimationClip clip, GameObject animRoot, SkinnedMeshRenderer smr, Transform humanRoot,
        Mesh tmp, int vertexCount, float frameRate)
    {
        var sb = new StringBuilder();
        bool pass = true;

        Texture2D posTex = AssetDatabase.LoadAssetAtPath<Texture2D>(PositionTexPath);
        Texture2D nrmTex = AssetDatabase.LoadAssetAtPath<Texture2D>(NormalTexPath);
        Mesh mesh = AssetDatabase.LoadAssetAtPath<Mesh>(MeshPath);
        if (posTex == null || nrmTex == null || mesh == null)
        {
            r.VerifyPassed = false;
            r.VerifyDetail = "저장 에셋 재로드 실패";
            return;
        }

        // (a) 포맷/차원
        bool fmtOk = posTex.format == TextureFormat.RGBAHalf && nrmTex.format == TextureFormat.RGBAHalf
                     && posTex.filterMode == FilterMode.Point && posTex.wrapMode == TextureWrapMode.Clamp
                     && posTex.width == vertexCount && posTex.height == r.Rows
                     && !posTex.mipmapCount.Equals(0) && posTex.mipmapCount == 1;
        pass &= fmtOk;
        sb.AppendLine($"  format: pos={posTex.format} {posTex.width}x{posTex.height} filter={posTex.filterMode} " +
                      $"wrap={posTex.wrapMode} mips={posTex.mipmapCount} -> {(fmtOk ? "OK" : "FAIL")}");

        // (b) 정점 수 == sharedMesh.vertexCount, 메쉬 정점 수 일치
        bool vcOk = vertexCount == smr.sharedMesh.vertexCount && mesh.vertexCount == vertexCount
                    && posTex.width == vertexCount;
        pass &= vcOk;
        sb.AppendLine($"  vertexCount: used={vertexCount} shared={smr.sharedMesh.vertexCount} " +
                      $"mesh={mesh.vertexCount} texW={posTex.width} -> {(vcOk ? "OK" : "FAIL")}");

        // (c) bake-space: mid-cycle 프레임에서 5개 정점을 라이브 재베이크와 비교
        int probeRow = Mathf.Clamp(r.Rows / 2, 0, r.Rows - 1);
        int[] probes = PickProbes(vertexCount);
        float bakeErr = 0f;
        {
            float time = probeRow / frameRate;
            clip.SampleAnimation(animRoot, time);
            smr.BakeMesh(tmp);
            Matrix4x4 m = ConversionMatrix(smr, humanRoot);
            Vector3[] bv = tmp.vertices;
            foreach (int vi in probes)
            {
                Vector3 direct = m.MultiplyPoint3x4(bv[vi]);
                Color c = posTex.GetPixel(vi, probeRow);
                Vector3 recon = new Vector3(c.r, c.g, c.b);
                float e = (direct - recon).magnitude;
                if (e > bakeErr) bakeErr = e;
            }
        }

        r.BakeSpaceMaxError = bakeErr;
        bool bakeOk = bakeErr < 0.001f; // 1mm
        pass &= bakeOk;
        sb.AppendLine($"  bake-space(row={probeRow}): maxErr={bakeErr * 1000f:F4}mm -> {(bakeOk ? "OK" : "FAIL")}");

        // (d) seam/period: wrap 점프(재구성의 단측 극한, eps-free) = 스킴이 phase 경계에서 만드는 불연속.
        // t→1⁻ 의 rowB 극한 vs phase 0 값을 직접 비교하므로(유한 eps의 walk-motion 아티팩트 없음),
        // LoopClose는 rowB가 row0로 wrap → 0(seamless), Clamp는 frame(N-1) vs frame0 = rawSeam.
        // 참고로 유한 eps(1e-3) 위상차도 보고하지만(순수 motion), 판정에는 쓰지 않는다.
        int wrapRowB = r.LoopClose ? 0 : r.Rows - 1;
        float wrapJump = 0f;
        float phaseGapEps = 0f;
        foreach (int vi in probes)
        {
            Color s = posTex.GetPixel(vi, 0);
            Color e = posTex.GetPixel(vi, wrapRowB);
            float j = (new Vector3(s.r, s.g, s.b) - new Vector3(e.r, e.g, e.b)).magnitude;
            if (j > wrapJump) wrapJump = j;

            float g = (SampleVat(posTex, vi, 0f, r.Rows, r.LoopClose)
                       - SampleVat(posTex, vi, 1f - 1e-3f, r.Rows, r.LoopClose)).magnitude;
            if (g > phaseGapEps) phaseGapEps = g;
        }

        r.ContinuityDelta = wrapJump;
        bool contOk = wrapJump < 0.001f; // 1mm: 재구성 wrap 불연속 없음
        pass &= contOk;
        sb.AppendLine($"  seam/period: scheme={r.SchemeName} rawSeam={r.SeamMax * 1000f:F4}mm " +
                      $"wrapJump={wrapJump * 1000f:F4}mm (phaseGap@eps1e-3={phaseGapEps * 1000f:F3}mm, motion) " +
                      $"period={r.Period:F4}s -> {(contOk ? "OK" : "FAIL")}");

        // (e) 비영/비퇴화: bounds 크기 및 정점 비영, 사람 크기 타당성(0.3~5m)
        Vector3 sz = r.UnionBounds.size;
        float maxDim = Mathf.Max(sz.x, Mathf.Max(sz.y, sz.z));
        bool nonZero = maxDim > 0.01f && HasNonZeroTexel(posTex, r.Rows, vertexCount);
        bool plausible = maxDim > 0.3f && maxDim < 5f;
        pass &= nonZero && plausible;
        sb.AppendLine($"  non-zero: boundsSize={sz} maxDim={maxDim:F3}m nonZeroTexel={nonZero} " +
                      $"plausibleHuman(0.3~5m)={plausible} -> {((nonZero && plausible) ? "OK" : "FAIL")}");

        // (f) 동반 메쉬 UV2 열주소 계약: (i+0.5)/width, bounds=union
        var uv2 = new System.Collections.Generic.List<Vector2>();
        mesh.GetUVs(1, uv2);
        float expFirst = 0.5f / vertexCount;
        float expLast = (vertexCount - 0.5f) / vertexCount;
        bool uv2Ok = uv2.Count == vertexCount
                     && Mathf.Abs(uv2[0].x - expFirst) < 1e-6f
                     && Mathf.Abs(uv2[vertexCount - 1].x - expLast) < 1e-6f
                     && (mesh.bounds.size - r.UnionBounds.size).magnitude < 1e-4f;
        pass &= uv2Ok;
        sb.AppendLine($"  mesh UV2/bounds: count={uv2.Count} uv2[0].x={uv2[0].x:F6}(exp {expFirst:F6}) " +
                      $"uv2[last].x={uv2[vertexCount - 1].x:F6}(exp {expLast:F6}) meshBounds={mesh.bounds.size} " +
                      $"-> {(uv2Ok ? "OK" : "FAIL")}");

        r.VerifyPassed = pass;
        r.VerifyDetail = sb.ToString();
    }

    private static int[] PickProbes(int vertexCount)
    {
        return new[]
        {
            0,
            vertexCount / 4,
            vertexCount / 2,
            (3 * vertexCount) / 4,
            vertexCount - 1,
        };
    }

    // 소비자(Chunk B 셰이더) 계약과 동일한 2행 lerp 포즈 재구성.
    private static Vector3 SampleVat(Texture2D tex, int vtx, float t, int rows, bool loopClose)
    {
        int rowA, rowB;
        float frac;
        if (loopClose)
        {
            float ff = t * rows;
            int baseRow = Mathf.FloorToInt(ff);
            frac = ff - baseRow;
            rowA = ((baseRow % rows) + rows) % rows;
            rowB = (rowA + 1) % rows;
        }
        else
        {
            float ff = t * (rows - 1);
            int baseRow = Mathf.FloorToInt(ff);
            frac = ff - baseRow;
            rowA = Mathf.Clamp(baseRow, 0, rows - 1);
            rowB = Mathf.Clamp(baseRow + 1, 0, rows - 1);
        }

        Color ca = tex.GetPixel(vtx, rowA);
        Color cb = tex.GetPixel(vtx, rowB);
        Vector3 a = new Vector3(ca.r, ca.g, ca.b);
        Vector3 b = new Vector3(cb.r, cb.g, cb.b);
        return Vector3.Lerp(a, b, frac);
    }

    private static bool HasNonZeroTexel(Texture2D tex, int rows, int width)
    {
        for (int row = 0; row < rows; row++)
        {
            for (int x = 0; x < width; x += Mathf.Max(1, width / 32))
            {
                Color c = tex.GetPixel(x, row);
                if (Mathf.Abs(c.r) > 1e-5f || Mathf.Abs(c.g) > 1e-5f || Mathf.Abs(c.b) > 1e-5f)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static string FormatReport(BakeReport r)
    {
        var sb = new StringBuilder();
        sb.AppendLine("[HumanVatBaker] ===== VAT BAKE SELF-VERIFY =====");
        sb.AppendLine($"  vertexCount={r.VertexCount}  sampleCount={r.SampleCount}  rows={r.Rows}");
        sb.AppendLine($"  scheme={r.SchemeName}  period={r.Period:F4}s  frameRate={r.FrameRate}  clipLen={r.ClipLength:F4}s");
        sb.AppendLine($"  texFormat={r.TexFormat}  dims={r.TexWidth}x{r.TexHeight}  maxTextureSize={r.MaxTextureSize}");
        sb.AppendLine($"  unionBounds center={r.UnionBounds.center} size={r.UnionBounds.size}");
        sb.Append(r.VerifyDetail);
        sb.AppendLine($"  VERIFY = {(r.VerifyPassed ? "PASS" : "FAIL")}  " +
                      $"(bakeSpaceMaxErr={r.BakeSpaceMaxError * 1000f:F4}mm, continuity={r.ContinuityDelta * 1000f:F4}mm)");
        return sb.ToString();
    }

    private static void EnsureFolder(string folder)
    {
        if (AssetDatabase.IsValidFolder(folder)) return;
        string parent = Path.GetDirectoryName(folder).Replace('\\', '/');
        string leaf = Path.GetFileName(folder);
        EnsureFolder(parent);
        AssetDatabase.CreateFolder(parent, leaf);
    }
}
#endif

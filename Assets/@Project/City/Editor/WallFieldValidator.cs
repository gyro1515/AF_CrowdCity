#if UNITY_EDITOR
using System;
using System.Globalization;
using System.IO;
using System.Text;
using Unity.Mathematics;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>
/// 베이크된 WallSdf asset의 유효성을 검사한다(Phase C 단계 1 게이트).
/// 확인 항목: (1) collider 40개 커버, (2) 그리드가 walkable region 커버, (3) 재베이크 hash 안정성(결정성),
/// (4) 내부/개활 비율 sanity, (5) 얇은 장애물(Vehicles/StreetProps/Parks) 누수(collider별 내부 셀 수),
/// (6) 최대 scale 캡슐 기준 여유(clearance), (7) WallField 로드 + Phi/Gradient 조회 + Dispose sanity.
/// 결과를 scratchpad 리포트로 저장하고, 치명 항목 실패 시 non-zero exit code로 종료한다.
/// </summary>
public static class WallFieldValidator
{
    private const int ExpectedColliderCount = 40; // 건물 37 + Parks/Vehicles/StreetProps.
    private const float CapsuleRadius = 0.35f;
    private const float SkinWidth = 0.08f;

    private static string DefaultReportPath =>
        Path.Combine(Directory.GetParent(Application.dataPath).FullName, "phaseC_stage1_validator.txt");

    [MenuItem("AF/CrowdCity/Validate Wall SDF")]
    public static void ValidateFromMenu()
    {
        Validate(DefaultReportPath);
    }

    public static void ValidateFromBatch()
    {
        int exitCode = 0;
        string reportPath = DefaultReportPath;
        try
        {
            string[] args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (args[i] == "-sdfReport") reportPath = args[i + 1];
            }

            bool ok = Validate(reportPath);
            exitCode = ok ? 0 : 5;
        }
        catch (Exception e)
        {
            Debug.LogError("[WallFieldValidator] 실패: " + e);
            exitCode = 6;
        }

        EditorApplication.Exit(exitCode);
    }

    public static bool Validate(string reportPath)
    {
        EditorSceneManager.OpenScene("Assets/@Project/Scenes/GameScene.unity", OpenSceneMode.Single);
        WallFieldBaker.ResolveScene(out Transform cityRoot, out float groundY, out float maxScale);

        var sb = new StringBuilder();
        bool allCritical = true;

        sb.AppendLine("# Phase C 단계 1 — Wall SDF validator");
        sb.AppendLine("# generated: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));

        WallSdfAsset asset = AssetDatabase.LoadAssetAtPath<WallSdfAsset>(WallFieldBaker.AssetPathConst);
        if (asset == null)
        {
            sb.AppendLine("[FAIL] asset을 로드하지 못했습니다: " + WallFieldBaker.AssetPathConst);
            File.WriteAllText(reportPath, sb.ToString());
            Debug.LogError("[WallFieldValidator] asset 없음.");
            return false;
        }

        sb.AppendLine($"# asset: {WallFieldBaker.AssetPathConst}");
        sb.AppendLine($"# grid: cols={asset.Cols} rows={asset.Rows} cells={asset.CellCount} cell={asset.CellSize}m " +
                      $"origin=({F(asset.OriginX)},{F(asset.OriginZ)}) yBand=[{F(asset.YMin)},{F(asset.YMax)}] " +
                      $"maxDist={asset.MaxDistance} bias={F(asset.BilinearBias)}");
        sb.AppendLine($"# integrity(asset): colliders={asset.ColliderCount} tris={asset.TriangleCount} " +
                      $"sourceHash={asset.SourceHash:X8} payloadCrc={asset.PayloadCrc:X8} schema={asset.SchemaVersion}");
        sb.AppendLine();

        // ---- 재베이크(결정성/hash 안정성). asset 메타에서 파라미터를 복원해 동일 조건으로 두 번 굽는다. ----
        var p = WallFieldBaker.BakeParams.Default(groundY, maxScale);
        p.CellSize = asset.CellSize;
        p.MaxDistance = asset.MaxDistance;
        p.BandBottomOffset = asset.YMin - groundY;
        p.BilinearBias = asset.BilinearBias;

        WallFieldBaker.BakeResult r1 = WallFieldBaker.ComputeBake(cityRoot, p);
        WallFieldBaker.BakeResult r2 = WallFieldBaker.ComputeBake(cityRoot, p);

        bool rebakeStable = r1.SourceHash == r2.SourceHash && r1.PayloadCrc == r2.PayloadCrc && ArraysEqual(r1.Dist, r2.Dist);
        bool matchesAsset = r1.SourceHash == asset.SourceHash && r1.PayloadCrc == asset.PayloadCrc;
        Check(sb, ref allCritical, "hash 안정성(재베이크 2회 동일)", rebakeStable,
            $"r1={r1.SourceHash:X8}/{r1.PayloadCrc:X8} r2={r2.SourceHash:X8}/{r2.PayloadCrc:X8}");
        Check(sb, ref allCritical, "asset이 재베이크와 일치(결정성)", matchesAsset,
            $"asset={asset.SourceHash:X8}/{asset.PayloadCrc:X8} rebake={r1.SourceHash:X8}/{r1.PayloadCrc:X8}");

        // ---- collider 40개 커버 ----
        Check(sb, ref allCritical, $"collider 수 = {ExpectedColliderCount}", r1.ColliderCount == ExpectedColliderCount,
            $"actual={r1.ColliderCount}");

        // ---- 그리드가 walkable region 커버 ----
        float eps = asset.CellSize;
        bool coversX = asset.OriginX <= r1.RegionMinX + eps && asset.OriginX + asset.Cols * asset.CellSize >= r1.RegionMaxX - eps;
        bool coversZ = asset.OriginZ <= r1.RegionMinZ + eps && asset.OriginZ + asset.Rows * asset.CellSize >= r1.RegionMaxZ - eps;
        Check(sb, ref allCritical, "그리드가 walkable region 커버", coversX && coversZ,
            $"regionX=[{F(r1.RegionMinX)},{F(r1.RegionMaxX)}] regionZ=[{F(r1.RegionMinZ)},{F(r1.RegionMaxZ)}] " +
            $"gridX=[{F(asset.OriginX)},{F(asset.OriginX + asset.Cols * asset.CellSize)}] gridZ=[{F(asset.OriginZ)},{F(asset.OriginZ + asset.Rows * asset.CellSize)}]");

        // ---- 내부/개활 비율 sanity ----
        int total = r1.Cols * r1.Rows;
        int insideCells = 0;
        for (int i = 0; i < r1.Inside.Length; i++) if (r1.Inside[i]) insideCells++;
        float insideFrac = (float)insideCells / total;
        Check(sb, ref allCritical, "내부(벽) 비율 sanity(0<frac<0.5)", insideFrac > 0f && insideFrac < 0.5f,
            $"insideFrac={F(insideFrac)} ({insideCells}/{total})");

        // ---- 최대 scale 캡슐 clearance ----
        float crMax = (CapsuleRadius + SkinWidth) * maxScale;
        int openCells = 0;
        for (int i = 0; i < r1.Dist.Length; i++) if (r1.Dist[i] > crMax) openCells++;
        float openFrac = (float)openCells / total;
        Check(sb, ref allCritical, "개활(Phi>cr_max) 비율(>0.5)", openFrac > 0.5f,
            $"cr_max={F(crMax)}m openFrac={F(openFrac)}");

        // 알려진 개활점(region 중심) 여유.
        WallField field = new WallField();
        bool loadSanity;
        string loadDetail;
        try
        {
            field.Load(asset);
            float cx = 0.5f * (r1.RegionMinX + r1.RegionMaxX);
            float cz = 0.5f * (r1.RegionMinZ + r1.RegionMaxZ);
            float phiCenter = field.Phi(cx, cz);
            float2 gCenter = field.Gradient(cx, cz);

            // 벽 근처 샘플: 첫 번째 건물 footprint 경계 부근.
            float2 nearWall = SampleNearWall(r1);
            float phiWall = field.Phi(nearWall.x, nearWall.y);
            float2 gWall = field.Gradient(nearWall.x, nearWall.y);

            loadSanity = field.IsLoaded && !float.IsNaN(phiCenter) && !float.IsNaN(phiWall);
            loadDetail = $"IsLoaded={field.IsLoaded} center=({F(cx)},{F(cz)}) Phi={F(phiCenter)} grad=({F(gCenter.x)},{F(gCenter.y)}) | " +
                         $"nearWall=({F(nearWall.x)},{F(nearWall.y)}) Phi={F(phiWall)} grad=({F(gWall.x)},{F(gWall.y)})";

            Check(sb, ref allCritical, "region 중심 개활(Phi>cr_max)", phiCenter > crMax, $"phiCenter={F(phiCenter)} cr_max={F(crMax)}");
        }
        finally
        {
            field.Dispose();
        }

        Check(sb, ref allCritical, "WallField 로드/조회 sanity", loadSanity, loadDetail);
        Check(sb, ref allCritical, "WallField Dispose 후 IsLoaded=false", !field.IsLoaded, $"IsLoaded={field.IsLoaded}");

        // ---- 얇은 장애물 누수: collider별 내부 셀 수(특히 결합 프롭 3종) ----
        sb.AppendLine();
        sb.AppendLine("## collider별 내부 셀 수(leak 진단; footprint AABB 내 inside 셀)");
        int zeroInsideProps = 0;
        int combinedFound = 0;
        for (int i = 0; i < r1.ColliderNames.Length; i++)
        {
            string name = r1.ColliderNames[i];
            bool combined = name == "Parks" || name == "Vehicles" || name == "StreetProps";
            Rect fp = r1.ColliderFootprint[i];
            bool hasFootprint = fp.width > 0f || fp.height > 0f;
            if (combined)
            {
                combinedFound++;
                sb.AppendLine($"  [{name}] insideCells={r1.ColliderInsideCells[i]} footprint={F(fp.width)}x{F(fp.height)}m");
                if (hasFootprint && r1.ColliderInsideCells[i] == 0) zeroInsideProps++;
            }
        }

        // 건물은 요약만(37개 개별 나열 생략), 0-inside 건물만 표기.
        int zeroInsideBuildings = 0;
        for (int i = 0; i < r1.ColliderNames.Length; i++)
        {
            string name = r1.ColliderNames[i];
            if (name == "Parks" || name == "Vehicles" || name == "StreetProps") continue;
            Rect fp = r1.ColliderFootprint[i];
            bool hasFootprint = fp.width > 0f || fp.height > 0f;
            if (hasFootprint && r1.ColliderInsideCells[i] == 0)
            {
                zeroInsideBuildings++;
                sb.AppendLine($"  [WARN] 건물 {name} inside=0 (sub-cell 누수 가능)");
            }
        }

        Check(sb, ref allCritical, "결합 프롭 3종 모두 존재/누수 없음(inside>0)", combinedFound == 3 && zeroInsideProps == 0,
            $"combinedFound={combinedFound} zeroInsideProps={zeroInsideProps} zeroInsideBuildings={zeroInsideBuildings}");

        sb.AppendLine();
        sb.AppendLine(allCritical ? "## RESULT: PASS" : "## RESULT: FAIL");

        Directory.CreateDirectory(Path.GetDirectoryName(reportPath));
        File.WriteAllText(reportPath, sb.ToString());
        Debug.Log($"[WallFieldValidator] {(allCritical ? "PASS" : "FAIL")} -> {reportPath}");
        return allCritical;
    }

    // 첫 번째 inside 셀의 world 중심을 벽 근처 샘플로 반환(없으면 region 중심).
    private static float2 SampleNearWall(WallFieldBaker.BakeResult r)
    {
        for (int idx = 0; idx < r.Inside.Length; idx++)
        {
            if (!r.Inside[idx]) continue;
            int col = idx % r.Cols;
            int row = idx / r.Cols;
            return new float2(r.OriginX + (col + 0.5f) * r.CellSize, r.OriginZ + (row + 0.5f) * r.CellSize);
        }

        return new float2(0.5f * (r.RegionMinX + r.RegionMaxX), 0.5f * (r.RegionMinZ + r.RegionMaxZ));
    }

    private static bool ArraysEqual(float[] a, float[] b)
    {
        if (a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
        return true;
    }

    private static void Check(StringBuilder sb, ref bool all, string label, bool ok, string detail)
    {
        if (!ok) all = false;
        sb.AppendLine($"[{(ok ? "PASS" : "FAIL")}] {label} :: {detail}");
    }

    private static string F(float v)
    {
        return v.ToString("0.####", CultureInfo.InvariantCulture);
    }
}
#endif

#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>
/// Phase C 단계 0 전용 헤드리스 성능 프로파일 harness다(Editor 전용, 런타임 빌드 미포함).
/// GameScene을 edit 모드로 열어 city collider/Ground를 확보한 뒤, GameConfig를 복제(원본 asset 불변)해
/// neutralCount만 바꿔 CrowdRoot를 직접 스폰하고 SimTick을 고정 스텝으로 반복 구동하면서
/// CrowdSimProfiler로 구간별 벽시계와 tick당 GC 할당을 측정해 CSV로 저장한다.
/// 정상 게임 루프(GameplayRoot)는 구동하지 않으며(SimTick 직접 호출), 결정론에 영향을 주지 않는 계측만 켠다.
///
/// 배치 실행 예:
///   Unity.exe -batchmode -nographics -projectPath &lt;proj&gt; -quit \
///     -executeMethod CrowdProfileHarness.RunFromBatch \
///     -profileOut &lt;out.txt&gt; -profileLabel &lt;label&gt;
/// </summary>
public static class CrowdProfileHarness
{
    private const string ScenePath = "Assets/@Project/Scenes/GameScene.unity";
    private const float Dt = 0.02f;

    // 스케일 스윕 및 tick 예산(baseline 무게 대비 합리적 구간; 결정론과 무관).
    private static readonly int[] Scales = { 100, 300, 500, 800 };
    private const int WarmupTicks = 200;   // 리스트 high-water/조향 상태 안정화(post-fix alloc 측정의 warmup)
    private const int TimingTicks = 1000;  // 벽시계 분포(median/p95/max)
    private const int GcTicks = 500;       // tick당 GC 할당 측정

    [MenuItem("AF/CrowdCity/Run Phase C Baseline Profile")]
    public static void RunFromMenu()
    {
        string outPath = Path.Combine(Directory.GetParent(Application.dataPath).FullName, "phaseC_baseline_profile.txt");
        Run(outPath, "menu");
    }

    /// <summary>배치 진입점. -profileOut / -profileLabel 커스텀 인자를 파싱해 실행한다.</summary>
    public static void RunFromBatch()
    {
        string outPath = null;
        string label = "batch";
        string[] args = Environment.GetCommandLineArgs();
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == "-profileOut") outPath = args[i + 1];
            else if (args[i] == "-profileLabel") label = args[i + 1];
        }

        if (string.IsNullOrEmpty(outPath))
        {
            outPath = Path.Combine(Directory.GetParent(Application.dataPath).FullName, "phaseC_baseline_profile.txt");
        }

        int exitCode = 0;
        try
        {
            Run(outPath, label);
        }
        catch (Exception e)
        {
            Debug.LogError("[CrowdProfileHarness] 실패: " + e);
            exitCode = 3;
        }

        EditorApplication.Exit(exitCode);
    }

    private static void Run(string outPath, string label)
    {
        Debug.Log($"[CrowdProfileHarness] 시작 label={label} out={outPath}");

        // 상한 해제(측정 루프는 수동이지만 방어적으로).
        QualitySettings.vSyncCount = 0;
        Application.targetFrameRate = -1;

        EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);

        GameSceneController gsc = UnityEngine.Object.FindFirstObjectByType<GameSceneController>();
        if (gsc == null)
        {
            throw new InvalidOperationException("GameScene에서 GameSceneController를 찾지 못했습니다.");
        }

        SerializedObject so = new SerializedObject(gsc);
        GameConfigSO baseConfig = so.FindProperty("config").objectReferenceValue as GameConfigSO;
        Transform cityRoot = so.FindProperty("cityRoot").objectReferenceValue as Transform;
        if (baseConfig == null || cityRoot == null)
        {
            throw new InvalidOperationException(
                $"config({baseConfig}) 또는 cityRoot({cityRoot}) 참조를 GameSceneController에서 읽지 못했습니다.");
        }

        FieldInfo neutralCountField = typeof(GameConfigSO).GetField(
            "neutralCount", BindingFlags.Instance | BindingFlags.NonPublic);
        if (neutralCountField == null)
        {
            throw new InvalidOperationException("GameConfigSO.neutralCount 필드를 리플렉션으로 찾지 못했습니다.");
        }

        var sb = new StringBuilder();
        AppendHeader(sb, label, baseConfig);

        var segNames = new List<string>();
        for (int s = 0; s < (int)CrowdSimProfiler.Seg.Count; s++)
        {
            segNames.Add(((CrowdSimProfiler.Seg)s).ToString());
        }

        // 요약 표 헤더.
        sb.AppendLine("## SUMMARY (ms/tick, bytes/tick)");
        sb.AppendLine("neutralCount,agents,simtick_median_ms,simtick_p95_ms,simtick_max_ms," +
                      "alloc_total_bytes,alloc_mean_bytes_per_tick,alloc_max_bytes_per_tick,moved_agents,leader_disp_m");

        var perScaleSegMs = new List<double[]>();      // 각 스케일의 구간별 mean ms/tick
        var perScaleAgents = new List<int>();

        foreach (int scale in Scales)
        {
            GameConfigSO cfg = UnityEngine.Object.Instantiate(baseConfig);
            cfg.name = baseConfig.name + "_n" + scale;
            neutralCountField.SetValue(cfg, scale);

            var go = new GameObject("ProfileCrowdRoot_n" + scale);
            CrowdRoot crowd = go.AddComponent<CrowdRoot>();

            double[] segMeanMs = null;
            double medianMs = 0, p95Ms = 0, maxMs = 0;
            long allocTotal = 0, allocMax = 0;
            double allocMean = 0;
            int movedAgents = 0;
            float leaderDisp = 0f;
            int agents = 0;

            try
            {
                crowd.Initialize(cfg, cityRoot);
                crowd.SpawnInitial();
                crowd.OnMatchStateChanged(MatchState.Playing); // 다음 SimTick이 Playing을 소비.
                Physics.SyncTransforms();

                agents = crowd.Crowds != null ? CountAgents(crowd) : 0;

                // 자기검증용 초기 위치 스냅샷.
                Vector3[] startPos = SnapshotPositions(crowd);
                Vector3 leaderStart = crowd.PlayerLeaderTransform != null
                    ? crowd.PlayerLeaderTransform.position : Vector3.zero;

                // Warmup (계측 off).
                CrowdSimProfiler.Enabled = false;
                for (int i = 0; i < WarmupTicks; i++) crowd.SimTick(Dt);

                // Timing pass.
                CrowdSimProfiler.Reset();
                CrowdSimProfiler.Enabled = true;
                var perTick = new double[TimingTicks];
                double toMs = 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                for (int i = 0; i < TimingTicks; i++)
                {
                    long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                    crowd.SimTick(Dt);
                    long t1 = System.Diagnostics.Stopwatch.GetTimestamp();
                    perTick[i] = (t1 - t0) * toMs;
                }
                CrowdSimProfiler.Enabled = false;

                long[] accum = CrowdSimProfiler.Accum;
                segMeanMs = new double[accum.Length];
                for (int sIdx = 0; sIdx < accum.Length; sIdx++)
                {
                    segMeanMs[sIdx] = accum[sIdx] * toMs / TimingTicks;
                }

                Array.Sort(perTick);
                medianMs = Percentile(perTick, 0.50);
                p95Ms = Percentile(perTick, 0.95);
                maxMs = perTick[perTick.Length - 1];

                // GC pass (계측 off; tick당 할당만 측정).
                // GC.GetTotalAllocatedBytes는 이 API 레벨에 없어 GC.GetTotalMemory(false) 델타를 managed-alloc 프록시로 쓴다.
                // GC 수집이 끼면 델타가 음수가 될 수 있어 0으로 clamp한다(즉 per-tick 관리 힙 할당의 하한 지표).
                var perAlloc = new long[GcTicks];
                GC.Collect();
                for (int i = 0; i < GcTicks; i++)
                {
                    long a0 = GC.GetTotalMemory(false);
                    crowd.SimTick(Dt);
                    long a1 = GC.GetTotalMemory(false);
                    long d = a1 - a0;
                    if (d < 0) d = 0;
                    perAlloc[i] = d;
                    allocTotal += d;
                    if (d > allocMax) allocMax = d;
                }
                allocMean = (double)allocTotal / GcTicks;

                // 자기검증: 이동한 agent 수 + 리더 순변위. edit 모드 CC.Move 동작 확인용.
                Vector3[] endPos = SnapshotPositions(crowd);
                for (int i = 0; i < startPos.Length && i < endPos.Length; i++)
                {
                    if ((endPos[i] - startPos[i]).sqrMagnitude > 1e-4f) movedAgents++;
                }
                if (crowd.PlayerLeaderTransform != null)
                {
                    leaderDisp = Vector3.Distance(leaderStart, crowd.PlayerLeaderTransform.position);
                }
            }
            finally
            {
                CrowdSimProfiler.Enabled = false;
                UnityEngine.Object.DestroyImmediate(go);        // CrowdRoot + 자식 Human clone 전부 즉시 파괴(edit 모드).
                UnityEngine.Object.DestroyImmediate(cfg);       // config 복제본 정리.
            }

            perScaleSegMs.Add(segMeanMs);
            perScaleAgents.Add(agents);

            sb.AppendLine(string.Join(",", new[]
            {
                scale.ToString(),
                agents.ToString(),
                F(medianMs), F(p95Ms), F(maxMs),
                allocTotal.ToString(), F(allocMean), allocMax.ToString(),
                movedAgents.ToString(), F(leaderDisp),
            }));

            Debug.Log($"[CrowdProfileHarness] n={scale} agents={agents} " +
                      $"median={F(medianMs)}ms p95={F(p95Ms)}ms max={F(maxMs)}ms " +
                      $"alloc/t={F(allocMean)}B max={allocMax}B moved={movedAgents} leaderDisp={F(leaderDisp)}");
        }

        // 구간별 상세(long format): 스케일 x 구간 mean ms/tick + Total 대비 %.
        sb.AppendLine();
        sb.AppendLine("## SEGMENTS (mean ms/tick, % of Total)");
        sb.AppendLine("neutralCount,segment,mean_ms,pct_of_total");
        for (int si = 0; si < Scales.Length; si++)
        {
            double[] segMs = perScaleSegMs[si];
            if (segMs == null) continue;
            double total = segMs[(int)CrowdSimProfiler.Seg.Total];
            for (int s = 0; s < segMs.Length; s++)
            {
                double pct = total > 0 ? segMs[s] / total * 100.0 : 0;
                sb.AppendLine($"{Scales[si]},{segNames[s]},{F(segMs[s])},{F(pct)}");
            }

            // 파생 지표: Transform 마샬링(restore+mirror), 순수 이동 커널(이동구간-CC.Move), 커널 리졸버(grid+recruit+combat).
            double transformMarshal = segMs[(int)CrowdSimProfiler.Seg.Restore] + segMs[(int)CrowdSimProfiler.Seg.Mirror];
            double moveMathPure = segMs[(int)CrowdSimProfiler.Seg.LeaderMove]
                + segMs[(int)CrowdSimProfiler.Seg.FollowerSteer]
                + segMs[(int)CrowdSimProfiler.Seg.NeutralMove]
                - segMs[(int)CrowdSimProfiler.Seg.CCMove];
            double kernelResolvers = segMs[(int)CrowdSimProfiler.Seg.GridRebuild]
                + segMs[(int)CrowdSimProfiler.Seg.Recruit]
                + segMs[(int)CrowdSimProfiler.Seg.Combat];
            double ccMove = segMs[(int)CrowdSimProfiler.Seg.CCMove];
            sb.AppendLine($"{Scales[si]},DERIVED_CCMove,{F(ccMove)},{F(total > 0 ? ccMove / total * 100 : 0)}");
            sb.AppendLine($"{Scales[si]},DERIVED_TransformMarshal,{F(transformMarshal)},{F(total > 0 ? transformMarshal / total * 100 : 0)}");
            sb.AppendLine($"{Scales[si]},DERIVED_MoveMathPure,{F(moveMathPure)},{F(total > 0 ? moveMathPure / total * 100 : 0)}");
            sb.AppendLine($"{Scales[si]},DERIVED_KernelResolvers,{F(kernelResolvers)},{F(total > 0 ? kernelResolvers / total * 100 : 0)}");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(outPath));
        File.WriteAllText(outPath, sb.ToString());
        Debug.Log($"[CrowdProfileHarness] 완료. 결과 저장: {outPath}");
    }

    private static void AppendHeader(StringBuilder sb, string label, GameConfigSO cfg)
    {
        sb.AppendLine("# Phase C 단계 0 baseline profile");
        sb.AppendLine("# label: " + label);
        sb.AppendLine("# generated: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        sb.AppendLine($"# scene: {ScenePath}, seed={cfg.Seed}, rivalCount={cfg.RivalCount}, groundY={cfg.GroundY}");
        sb.AppendLine($"# dt={Dt}, warmupTicks={WarmupTicks}, timingTicks={TimingTicks}, gcTicks={GcTicks}");
        sb.AppendLine($"# Stopwatch.Frequency={System.Diagnostics.Stopwatch.Frequency}, HighResolution={System.Diagnostics.Stopwatch.IsHighResolution}");
        sb.AppendLine("# 주의: edit-mode headless 측정(Animator/스키닝/렌더 제외). CC.Move/Raycast/CheckSphere는 실제 physics 수행.");
        sb.AppendLine("# CCMove는 리더/팔로워/중립 CC.Move 격리 합산이며 LeaderMove/FollowerSteer/NeutralMove 총합에 중첩됨.");
        sb.AppendLine("# alloc_*는 GC.GetTotalMemory(false) tick 델타(음수 clamp) 프록시 = per-tick 관리 힙 할당 하한 지표(GC 수집 노이즈 존재).");
        sb.AppendLine();
    }

    private static int CountAgents(CrowdRoot crowd)
    {
        // Crowds의 member 합(리더+팔로워)으로 실제 스폰 규모를 근사. 중립은 team=-1이라 포함 안 되므로
        // 대신 자기검증 스냅샷 길이를 쓴다(SnapshotPositions가 buffer.Count 기반). 여기선 라벨용으로만 사용.
        int sum = 0;
        var crowds = crowd.Crowds;
        for (int i = 0; i < crowds.Count; i++) sum += crowds[i].MemberCount;
        return sum;
    }

    // CrowdRoot 내부 transform 배열은 private이므로, 스폰된 Human clone(자식)에서 위치를 읽는다.
    private static Vector3[] SnapshotPositions(CrowdRoot crowd)
    {
        var humans = crowd.GetComponentsInChildren<Human>(true);
        var arr = new Vector3[humans.Length];
        for (int i = 0; i < humans.Length; i++) arr[i] = humans[i].transform.position;
        return arr;
    }

    private static double Percentile(double[] sortedAsc, double q)
    {
        if (sortedAsc.Length == 0) return 0;
        int idx = (int)Math.Round(q * (sortedAsc.Length - 1), MidpointRounding.AwayFromZero);
        if (idx < 0) idx = 0;
        if (idx >= sortedAsc.Length) idx = sortedAsc.Length - 1;
        return sortedAsc[idx];
    }

    private static string F(double v)
    {
        return v.ToString("0.####", CultureInfo.InvariantCulture);
    }
}
#endif

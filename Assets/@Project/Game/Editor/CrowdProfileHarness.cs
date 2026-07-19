#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using Unity.Burst;
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

    // 스케일 스윕 기본값(-profileScales로 재정의). M-sim-0 baseline 대상 = 2000/5000. 결정론과 무관.
    private static readonly int[] DefaultScales = { 2000, 5000 };
    private const int WarmupTicks = 200;   // 리스트 high-water/조향 상태 안정화(post-fix alloc 측정의 warmup)
    private const int TimingTicks = 1000;  // 벽시계 분포(median/p95/max)
    private const int GcTicks = 500;       // tick당 GC 할당 측정
    private const int SdfVerifyTicks = 20; // SDF 활성 검증용 짧은 counter run(CC.Move 폴백 0 + SDF resolve>0 단언)
    private const int CounterTicks = 300;  // 진단 counter run tick 수(-profileCounters, timing과 분리)

    [MenuItem("AF/CrowdCity/Run Phase C Baseline Profile")]
    public static void RunFromMenu()
    {
        string outPath = Path.Combine(Directory.GetParent(Application.dataPath).FullName, "phaseC_baseline_profile.txt");
        Run(outPath, "menu", DefaultScales, false, -1);
    }

    /// <summary>배치 진입점. -profileOut / -profileLabel / -profileScales / -profileCounters 인자를 파싱해 실행한다.</summary>
    public static void RunFromBatch()
    {
        string outPath = null;
        string label = "batch";
        int[] scales = DefaultScales;
        bool enableCounters = false; // -profileCounters: 진단 work counter run 활성(timing run과 분리). 기본 OFF.
        int[] sepBudgets = null;     // -profileSepBudget: 분리 조회 후보 방문 예산 스윕(measurement-only). null이면 config 기본값 사용.
        string[] args = Environment.GetCommandLineArgs();
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "-profileOut" && i + 1 < args.Length) outPath = args[i + 1];
            else if (args[i] == "-profileLabel" && i + 1 < args.Length) label = args[i + 1];
            else if (args[i] == "-profileScales" && i + 1 < args.Length) scales = ParseInts(args[i + 1]);
            else if (args[i] == "-profileCounters") enableCounters = true;
            else if (args[i] == "-profileSepBudget" && i + 1 < args.Length) sepBudgets = ParseInts(args[i + 1]);
        }

        if (scales == null || scales.Length == 0)
        {
            scales = DefaultScales;
        }

        if (string.IsNullOrEmpty(outPath))
        {
            outPath = Path.Combine(Directory.GetParent(Application.dataPath).FullName, "phaseC_baseline_profile.txt");
        }

        int exitCode = 0;
        try
        {
            if (sepBudgets == null || sepBudgets.Length == 0)
            {
                Run(outPath, label, scales, enableCounters, -1); // -1 = config 기본 SeparationVisitBudget 유지.
            }
            else
            {
                // 예산별로 분리된 out 파일에 기록한다(파일명에 _b<budget> suffix). 각 파일이 독립 요약/counter 표를 담는다.
                string dir = Path.GetDirectoryName(outPath);
                string stem = Path.GetFileNameWithoutExtension(outPath);
                string ext = Path.GetExtension(outPath);
                foreach (int b in sepBudgets)
                {
                    string bOut = Path.Combine(dir, stem + "_b" + b + ext);
                    Run(bOut, label + "_b" + b, scales, enableCounters, b);
                }
            }
        }
        catch (Exception e)
        {
            Debug.LogError("[CrowdProfileHarness] 실패: " + e);
            exitCode = 3;
        }

        EditorApplication.Exit(exitCode);
    }

    private static void Run(string outPath, string label, int[] scales, bool enableCounters, int sepBudget)
    {
        Debug.Log($"[CrowdProfileHarness] 시작 label={label} out={outPath} " +
                  $"scales=[{string.Join(",", scales)}] counters={(enableCounters ? "ON(diagnostic)" : "OFF(timing)")} " +
                  $"sepBudget={(sepBudget >= 0 ? sepBudget.ToString() : "config-default")}");

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
        sb.AppendLine($"# SeparationVisitBudget override: {(sepBudget >= 0 ? sepBudget.ToString() : "config-default(off)")}");

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
        var countersSb = new StringBuilder();          // 진단 counter 덤프(-profileCounters일 때만 채운다).

        // live 프리팹을 로드해 인스턴스화한다(직렬화 deps + _useSdfSolver=1 이 그대로 넘어온다).
        // AddComponent<CrowdRoot>()는 직렬화 필드가 비어 Initialize가 하드 페일하고 SDF도 우회하므로 baseline 불인정.
        CrowdRoot crowdPrefab = ResourceLoader.LoadPrefab<CrowdRoot>();

        // Burst 옵션은 전역 에디터 상태다. 원본을 캡처해 각 스케일 finally에서 원복한다(하네스 밖 상태 오염 방지).
        bool origBurstEnabled = BurstCompiler.Options.EnableBurstCompilation;
        bool origBurstSync = BurstCompiler.Options.EnableBurstCompileSynchronously;

        foreach (int scale in scales)
        {
            // Unity 임시 오브젝트는 try 안에서 생성하고 finally에서 non-null일 때만 파괴한다(인스턴스화/리플렉션/초기화 실패 시 누수 방지).
            GameConfigSO cfg = null;
            CrowdRoot crowd = null;

            double[] segMeanMs = null;
            double medianMs = 0, p95Ms = 0, maxMs = 0;
            long allocTotal = 0, allocMax = 0;
            double allocMean = 0;
            int movedAgents = 0;
            float leaderDisp = 0f;
            int agents = 0;

            try
            {
                // Burst 강제 동기 컴파일(첫 job 호출 stall이 timing을 오염시키지 않도록) + 활성 단언. 첫 Schedule(아래 SDF 검증 루프) 이전에 설정한다.
                BurstCompiler.Options.EnableBurstCompileSynchronously = true;
                if (!BurstCompiler.Options.EnableBurstCompilation)
                {
                    throw new InvalidOperationException(
                        "[CrowdProfileHarness] Burst 컴파일이 비활성입니다(EnableBurstCompilation=false). Burst job 측정을 인정할 수 없습니다.");
                }

                Debug.Log($"BURST-ACTIVE: EnableBurstCompilation={BurstCompiler.Options.EnableBurstCompilation} Synchronous={BurstCompiler.Options.EnableBurstCompileSynchronously}");

                cfg = UnityEngine.Object.Instantiate(baseConfig);
                cfg.name = baseConfig.name + "_n" + scale;
                neutralCountField.SetValue(cfg, scale);
                if (sepBudget >= 0)
                {
                    SetSepBudget(cfg, sepBudget); // 측정 전용: 복제본의 SeparationVisitBudget만 덮어쓴다(원본 asset 불변).
                }

                crowd = UnityEngine.Object.Instantiate(crowdPrefab);
                crowd.gameObject.name = "ProfileCrowdRoot_n" + scale;

                // SDF solver 경로를 명시적으로 켠 뒤 초기화한다. Initialize가 WallField를 로드한다.
                crowd.UseSdfSolver = true;
                crowd.Initialize(cfg, cityRoot, cfg.NeutralCount);

                // 플래그가 아니라 WallField가 실제 로드돼 SDF 경로가 활성인지 단언한다(아니면 baseline 불인정 → 하드 페일).
                if (!crowd.IsSdfActive)
                {
                    throw new InvalidOperationException(
                        $"[CrowdProfileHarness] n={scale}: SDF solver가 활성이 아닙니다(IsSdfActive=false). " +
                        "프리팹의 _wallSdfAsset 배선/로드를 확인하세요. SDF ON baseline 불인정.");
                }

                crowd.SpawnInitial();
                crowd.OnMatchStateChanged(MatchState.Playing); // 다음 SimTick이 Playing을 소비.
                Physics.SyncTransforms();

                agents = crowd.OracleAgentCount; // 실제 스폰 규모(중립 포함). 구 CountAgents는 중립 제외 버그가 있었다.

                // 자기검증용 초기 위치 스냅샷.
                Vector3[] startPos = SnapshotPositions(crowd);
                Vector3 leaderStart = crowd.PlayerLeaderTransform != null
                    ? crowd.PlayerLeaderTransform.position : Vector3.zero;

                // SDF 활성 검증(짧은 counter run, timing과 분리): 실제 이동이 SDF 경로로 가고 CC.Move 폴백이 0인지 단언한다.
                CrowdSimCounters.Enabled = true;
                CrowdSimCounters.Reset();
                for (int i = 0; i < SdfVerifyTicks; i++) crowd.SimTick(Dt);
                long sdfResolves = CrowdSimCounters.SdfResolves;
                long ccFallbacks = CrowdSimCounters.CcMoveFallbacks;
                CrowdSimCounters.Enabled = false;
                CrowdSimCounters.Reset();
                if (sdfResolves <= 0 || ccFallbacks > 0)
                {
                    throw new InvalidOperationException(
                        $"[CrowdProfileHarness] n={scale}: SDF 이동 검증 실패 " +
                        $"(sdfResolves={sdfResolves}, ccMoveFallbacks={ccFallbacks}). " +
                        "SDF ON에서 CC.Move 폴백은 0이어야 baseline 인정.");
                }

                // Warmup (계측 off).
                CrowdSimProfiler.Enabled = false;
                for (int i = 0; i < WarmupTicks; i++) crowd.SimTick(Dt);

                // Timing pass.
                CrowdSimProfiler.Reset();
                CrowdSimProfiler.Enabled = true;
                var perTick = new double[TimingTicks];
                double toMs = 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                int maxScheduledFollowers = 0; // timing pass 동안 job에 스케줄된 팔로워 수의 최대치(0-팔로워 degenerate 감지).
                for (int i = 0; i < TimingTicks; i++)
                {
                    long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                    crowd.SimTick(Dt);
                    long t1 = System.Diagnostics.Stopwatch.GetTimestamp();
                    perTick[i] = (t1 - t0) * toMs;
                    if (crowd.LastScheduledFollowerCount > maxScheduledFollowers) maxScheduledFollowers = crowd.LastScheduledFollowerCount; // 계측 span(t0..t1) 밖에서 샘플.
                }
                CrowdSimProfiler.Enabled = false;

                // Burst job이 실제로 팔로워를 처리했는지 단언한다. 전체 timing pass에서 팔로워가 0이면 job 무작업이라 timing이 무의미(degenerate).
                if (maxScheduledFollowers <= 0)
                {
                    Debug.LogError("BURST-GUARD-FAIL: 0 followers");
                    throw new InvalidOperationException(
                        $"[CrowdProfileHarness] n={scale}: timing pass 전체에서 스케줄된 팔로워가 0입니다(Burst job 무작업). degenerate run은 baseline 불인정.");
                }

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

                // 진단 counter run(-profileCounters): timing/GC와 분리된 별도 pass. work counter로 주범 sub-stage를 지목한다.
                if (enableCounters)
                {
                    CrowdSimCounters.Enabled = true;
                    CrowdSimCounters.Reset();
                    for (int i = 0; i < CounterTicks; i++) crowd.SimTick(Dt);
                    CrowdSimCounters.Enabled = false;
                    AppendCounters(countersSb, scale, agents);
                    CrowdSimCounters.Reset();
                }
            }
            finally
            {
                CrowdSimProfiler.Enabled = false;
                CrowdSimCounters.Enabled = false;
                BurstCompiler.Options.EnableBurstCompilation = origBurstEnabled;          // 전역 Burst 옵션 원복(정리보다 먼저 실행해 정리 예외 시에도 원복 보장).
                BurstCompiler.Options.EnableBurstCompileSynchronously = origBurstSync;
                if (crowd != null) UnityEngine.Object.DestroyImmediate(crowd.gameObject); // CrowdRoot + 자식 Human clone 전부 즉시 파괴(edit 모드).
                if (cfg != null) UnityEngine.Object.DestroyImmediate(cfg);                // config 복제본 정리.
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
        for (int si = 0; si < scales.Length; si++)
        {
            double[] segMs = perScaleSegMs[si];
            if (segMs == null) continue;
            double total = segMs[(int)CrowdSimProfiler.Seg.Total];
            for (int s = 0; s < segMs.Length; s++)
            {
                double pct = total > 0 ? segMs[s] / total * 100.0 : 0;
                sb.AppendLine($"{scales[si]},{segNames[s]},{F(segMs[s])},{F(pct)}");
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
            sb.AppendLine($"{scales[si]},DERIVED_CCMove,{F(ccMove)},{F(total > 0 ? ccMove / total * 100 : 0)}");
            sb.AppendLine($"{scales[si]},DERIVED_TransformMarshal,{F(transformMarshal)},{F(total > 0 ? transformMarshal / total * 100 : 0)}");
            sb.AppendLine($"{scales[si]},DERIVED_MoveMathPure,{F(moveMathPure)},{F(total > 0 ? moveMathPure / total * 100 : 0)}");
            sb.AppendLine($"{scales[si]},DERIVED_KernelResolvers,{F(kernelResolvers)},{F(total > 0 ? kernelResolvers / total * 100 : 0)}");
        }

        // 진단 counter 덤프(-profileCounters일 때만 채워져 있다). timing과 분리된 counter run 결과다.
        if (enableCounters && countersSb.Length > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## COUNTERS (per-tick over CounterTicks, separate counter run — NOT a timing run)");
            sb.AppendLine("neutralCount,metric,source,per_tick,total");
            sb.Append(countersSb.ToString());
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
        sb.AppendLine($"# dt={Dt}, warmupTicks={WarmupTicks}, timingTicks={TimingTicks}, gcTicks={GcTicks}, sdfVerifyTicks={SdfVerifyTicks}, counterTicks={CounterTicks}");
        sb.AppendLine($"# Stopwatch.Frequency={System.Diagnostics.Stopwatch.Frequency}, HighResolution={System.Diagnostics.Stopwatch.IsHighResolution}");
        sb.AppendLine("# 주의: edit-mode headless 측정(Animator/스키닝/렌더 제외). Raycast/CheckSphere는 실제 physics 수행.");
        sb.AppendLine("# 소스: live 프리팹 인스턴스(_useSdfSolver=1). SDF ON 강제 + IsSdfActive 단언 + CC.Move 폴백 0 단언(baseline 인정 조건).");
        sb.AppendLine("# 주의(SDF ON): CCMove 구간은 ApplyHorizontalMove(리더/팔로워/중립)를 감싸므로 SDF ON에서는 WallSolver.Resolve(SDF 이동 해소) 벽시계다.");
        sb.AppendLine("#   즉 CCMove≈CharacterController.Move가 아니라 SDF 이동 적용 비용이며(실제 CC.Move 호출 수=0, CcMoveFallbacks 카운터로 별도 검증), 각 이동 구간(LeaderMove/FollowerSteer/NeutralMove)에 중첩된다.");
        sb.AppendLine("# work counter는 timing을 교란하므로 timing/GC pass는 counter OFF, 진단 counter는 -profileCounters 별도 pass에서만 집계.");
        sb.AppendLine("# alloc_*는 GC.GetTotalMemory(false) tick 델타(음수 clamp) 프록시 = per-tick 관리 힙 할당 하한 지표(GC 수집 노이즈 존재).");
        sb.AppendLine();
    }

    // 진단 counter run 결과를 per-tick으로 정규화해 덤프한다(CounterTicks 기준). timing과 분리된 counter run이다.
    private static void AppendCounters(StringBuilder sb, int scale, int agents)
    {
        double denom = CounterTicks;
        long rebuilds = CrowdSimCounters.GridRebuilds;
        double entriesPerRebuild = rebuilds > 0 ? CrowdSimCounters.GridEntries / (double)rebuilds : 0;
        sb.AppendLine($"{scale},grid_entries_per_rebuild,-,{F(entriesPerRebuild)},{CrowdSimCounters.GridEntries}");
        sb.AppendLine($"{scale},grid_rebuilds,-,{F(rebuilds / denom)},{rebuilds}");

        for (int s = 0; s < (int)CrowdSimCounters.QuerySource.Count; s++)
        {
            var src = (CrowdSimCounters.QuerySource)s;
            sb.AppendLine($"{scale},query_calls,{src},{F(CrowdSimCounters.QueryCalls(src) / denom)},{CrowdSimCounters.QueryCalls(src)}");
            sb.AppendLine($"{scale},candidate_visits,{src},{F(CrowdSimCounters.CandidateVisits(src) / denom)},{CrowdSimCounters.CandidateVisits(src)}");
            sb.AppendLine($"{scale},radius_qualifiers,{src},{F(CrowdSimCounters.RadiusQualifiers(src) / denom)},{CrowdSimCounters.RadiusQualifiers(src)}");
        }

        // 분리 조회 밀도 cap 발화율: 예산 도달로 절단된 분리 조회 / 전체 분리 조회.
        long sepCalls = CrowdSimCounters.QueryCalls(CrowdSimCounters.QuerySource.Separation);
        long sepCapHits = CrowdSimCounters.SepCapHits;
        double capHitFrac = sepCalls > 0 ? sepCapHits / (double)sepCalls : 0;
        sb.AppendLine($"{scale},sep_cap_hits,-,{F(sepCapHits / denom)},{sepCapHits}");
        sb.AppendLine($"{scale},sep_cap_hit_fraction,-,{F(capHitFrac)},-");

        sb.AppendLine($"{scale},combat_touching,-,{F(CrowdSimCounters.CombatTouching / denom)},{CrowdSimCounters.CombatTouching}");
        sb.AppendLine($"{scale},combat_unique_pairs,-,{F(CrowdSimCounters.CombatUniquePairs / denom)},{CrowdSimCounters.CombatUniquePairs}");
        sb.AppendLine($"{scale},victim_total,-,{F(CrowdSimCounters.VictimTotal / denom)},{CrowdSimCounters.VictimTotal}");
        sb.AppendLine($"{scale},victim_comparisons,-,{F(CrowdSimCounters.VictimComparisons / denom)},{CrowdSimCounters.VictimComparisons}");

        long vt = CrowdSimCounters.VictimTotal;
        double cmpPerVictim = vt > 0 ? CrowdSimCounters.VictimComparisons / (double)vt : 0;
        sb.AppendLine($"{scale},victim_comparisons_per_victim,-,{F(cmpPerVictim)},-");

        Debug.Log($"[CrowdProfileHarness] COUNTERS n={scale} agents={agents} " +
                  $"candVisits/tick sep={F(CrowdSimCounters.CandidateVisits(CrowdSimCounters.QuerySource.Separation) / denom)} " +
                  $"recruit={F(CrowdSimCounters.CandidateVisits(CrowdSimCounters.QuerySource.Recruit) / denom)} " +
                  $"combat={F(CrowdSimCounters.CandidateVisits(CrowdSimCounters.QuerySource.Combat) / denom)} " +
                  $"leader={F(CrowdSimCounters.CandidateVisits(CrowdSimCounters.QuerySource.Leader) / denom)} " +
                  $"rivalAi={F(CrowdSimCounters.CandidateVisits(CrowdSimCounters.QuerySource.RivalAi) / denom)} | " +
                  $"sepVisits/query={F(sepCalls > 0 ? CrowdSimCounters.CandidateVisits(CrowdSimCounters.QuerySource.Separation) / (double)sepCalls : 0)} " +
                  $"capHit={F(capHitFrac)} | " +
                  $"victims/tick={F(vt / denom)} victimCmp/tick={F(CrowdSimCounters.VictimComparisons / denom)} cmp/victim={F(cmpPerVictim)}");
    }

    // 측정 전용: 복제 config의 SimTuning.SeparationVisitBudget만 덮어쓴다(struct라 box→set→unbox).
    private static void SetSepBudget(GameConfigSO cfg, int budget)
    {
        FieldInfo simField = typeof(GameConfigSO).GetField("sim", BindingFlags.Instance | BindingFlags.NonPublic);
        FieldInfo budgetField = typeof(SimTuning).GetField("SeparationVisitBudget");
        if (simField == null || budgetField == null)
        {
            throw new InvalidOperationException("GameConfigSO.sim 또는 SimTuning.SeparationVisitBudget 필드를 리플렉션으로 찾지 못했습니다.");
        }

        object boxed = simField.GetValue(cfg);   // SimTuning struct를 박싱.
        budgetField.SetValue(boxed, budget);      // 박싱된 복사본의 예산만 변경.
        simField.SetValue(cfg, boxed);            // config로 되쓴다.
    }

    // "2000,5000" 형식의 CSV를 int[]로 파싱한다(-profileScales 전용).
    private static int[] ParseInts(string csv)
    {
        string[] parts = csv.Split(',');
        var list = new List<int>();
        for (int i = 0; i < parts.Length; i++)
        {
            if (int.TryParse(parts[i].Trim(), out int v)) list.Add(v);
        }

        return list.ToArray();
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

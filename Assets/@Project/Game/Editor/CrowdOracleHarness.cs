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
/// Phase C 단계 0 oracle 스냅샷 harness다(Editor 전용). 현행(CC.Move 유지) 시뮬을 동일 seed로 N tick 구동하며
/// agent별 위치·실제 변위·RNG draw 수·중립 repick 결과·라이벌 결정·이벤트 transcript를 기록한다.
/// 이후 SDF 대체본과 bit/근접 비교할 oracle이며, 시뮬 로직/순서/결정론은 전혀 건드리지 않는다(관찰만).
///
/// 산출물(-oracleOut 디렉터리):
///   phaseC_oracle_snapshot_n{scale}_s{seed}.bin  : 헤더+정적 agent표+프레임별 per-agent 위치/팀(바이너리).
///   phaseC_oracle_summary.csv                     : combo·tick별 RNG draw/repick/라이벌/이동/팀수/리더 위치.
///   phaseC_oracle_events.csv                      : 발행 이벤트 transcript(발행 순서 보존).
///   phaseC_oracle_determinism.txt                 : 첫 combo 2회 실행 바이너리 동일성(결정성) 확인.
///
/// 배치 실행 예:
///   Unity.exe -batchmode -nographics -projectPath &lt;proj&gt; -quit \
///     -executeMethod CrowdOracleHarness.RunFromBatch \
///     -oracleOut &lt;dir&gt; -oracleTicks 1000 -oracleScales 100,300,500,800 -oracleSeeds 12345,777,2024
/// </summary>
public static class CrowdOracleHarness
{
    private const string ScenePath = "Assets/@Project/Scenes/GameScene.unity";
    private const float Dt = 0.02f;
    private const uint Magic = 0x3153434F; // "OCS1" LE.

    public static void RunFromBatch()
    {
        int exitCode = 0;
        try
        {
            string outDir = null;
            int ticks = 1000;
            int[] scales = { 100, 300, 500, 800 };
            int[] seeds = null; // null이면 config seed 사용.
            bool verify = true;

            string[] args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (args[i] == "-oracleOut") outDir = args[i + 1];
                else if (args[i] == "-oracleTicks" && int.TryParse(args[i + 1], out int t)) ticks = t;
                else if (args[i] == "-oracleScales") scales = ParseInts(args[i + 1]);
                else if (args[i] == "-oracleSeeds") seeds = ParseInts(args[i + 1]);
                else if (args[i] == "-oracleNoVerify") verify = false;
            }

            if (string.IsNullOrEmpty(outDir))
            {
                outDir = Path.Combine(Directory.GetParent(Application.dataPath).FullName, "phaseC_oracle");
            }

            Run(outDir, ticks, scales, seeds, verify);
        }
        catch (Exception e)
        {
            Debug.LogError("[CrowdOracleHarness] 실패: " + e);
            exitCode = 7;
        }

        EditorApplication.Exit(exitCode);
    }

    private static void Run(string outDir, int ticks, int[] scales, int[] seeds, bool verify)
    {
        Debug.Log($"[CrowdOracleHarness] 시작 out={outDir} ticks={ticks} scales=[{string.Join(",", scales)}] " +
                  $"seeds=[{(seeds == null ? "config" : string.Join(",", seeds))}]");

        QualitySettings.vSyncCount = 0;
        Application.targetFrameRate = -1;
        Directory.CreateDirectory(outDir);

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
            throw new InvalidOperationException("GameSceneController에서 config/cityRoot를 읽지 못했습니다.");
        }

        FieldInfo neutralCountField = typeof(GameConfigSO).GetField("neutralCount", BindingFlags.Instance | BindingFlags.NonPublic);
        FieldInfo seedField = typeof(GameConfigSO).GetField("seed", BindingFlags.Instance | BindingFlags.NonPublic);
        if (neutralCountField == null || seedField == null)
        {
            throw new InvalidOperationException("GameConfigSO.neutralCount/seed 필드를 리플렉션으로 찾지 못했습니다.");
        }

        if (seeds == null || seeds.Length == 0)
        {
            seeds = new[] { baseConfig.Seed };
        }

        string summaryPath = Path.Combine(outDir, "phaseC_oracle_summary.csv");
        string eventsPath = Path.Combine(outDir, "phaseC_oracle_events.csv");

        using (var summary = new StreamWriter(summaryPath, false))
        using (var events = new StreamWriter(eventsPath, false))
        {
            summary.WriteLine("scale,seed,tick,agentCount,rngDraws,repick,accepted,failed,rivalDecisions,rivalChanged," +
                              "movedAgents,meanDisp_m,maxDisp_m,team0,team1,team2,team3,neutral,leaderX,leaderZ");
            events.WriteLine("scale,seed,tick,order,type,arg0,arg1");

            bool firstCombo = true;
            foreach (int scale in scales)
            {
                foreach (int seed in seeds)
                {
                    string binPath = Path.Combine(outDir, $"phaseC_oracle_snapshot_n{scale}_s{seed}.bin");
                    RunCombo(baseConfig, cityRoot, neutralCountField, seedField, scale, seed, ticks, binPath, summary, events);
                    Debug.Log($"[CrowdOracleHarness] combo n={scale} seed={seed} -> {binPath}");

                    if (firstCombo && verify)
                    {
                        firstCombo = false;
                        VerifyDeterminism(baseConfig, cityRoot, neutralCountField, seedField, scale, seed, ticks, binPath, outDir);
                    }
                }
            }
        }

        Debug.Log($"[CrowdOracleHarness] 완료. summary={summaryPath}");
    }

    private static void RunCombo(
        GameConfigSO baseConfig, Transform cityRoot, FieldInfo neutralCountField, FieldInfo seedField,
        int scale, int seed, int ticks, string binPath, StreamWriter summary, StreamWriter events)
    {
        GameConfigSO cfg = UnityEngine.Object.Instantiate(baseConfig);
        cfg.name = baseConfig.name + $"_oracle_n{scale}_s{seed}";
        neutralCountField.SetValue(cfg, scale);
        seedField.SetValue(cfg, seed);

        var go = new GameObject($"OracleCrowdRoot_n{scale}_s{seed}");
        CrowdRoot crowd = go.AddComponent<CrowdRoot>();

        // 이벤트 transcript 버퍼(현재 tick).
        var tickEvents = new List<int[]>(); // {type, arg0, arg1}
        Action<CrowdCountChangedEvent> onCount = e => tickEvents.Add(new[] { 0, e.CrowdId, e.MemberCount });
        Action<CrowdEliminatedEvent> onElim = e => tickEvents.Add(new[] { 1, e.CrowdId, e.ByCrowdId });

        try
        {
            crowd.Initialize(cfg, cityRoot);
            crowd.SpawnInitial();
            crowd.OnMatchStateChanged(MatchState.Playing);
            Physics.SyncTransforms();

            int agentCount = crowd.OracleAgentCount;

            // 이벤트 구독은 SpawnInitial의 첫 coalesced publish 이후(= per-tick 이벤트만 캡처).
            EventManager.GetSubscriber<CrowdCountChangedEvent>().Subscribe(onCount);
            EventManager.GetSubscriber<CrowdEliminatedEvent>().Subscribe(onElim);
            CrowdOracleRecorder.Enabled = true;

            var prevX = new float[agentCount];
            var prevZ = new float[agentCount];

            using (var bw = new BinaryWriter(File.Open(binPath, FileMode.Create, FileAccess.Write)))
            {
                // ---- 헤더 ----
                bw.Write(Magic);
                bw.Write(scale);
                bw.Write(seed);
                bw.Write(ticks);
                bw.Write(agentCount);
                bw.Write(Dt);

                // ---- 정적 agent 표 + 초기 위치(프레임 baseline) ----
                for (int i = 0; i < agentCount; i++)
                {
                    crowd.OracleReadAgent(i, out int id, out int team, out bool _, out Vector2 pos, out float aScale);
                    bw.Write(id);
                    bw.Write(team);
                    bw.Write(aScale);
                    bw.Write(pos.x);
                    bw.Write(pos.y); // Vector2.y == world Z.
                    prevX[i] = pos.x;
                    prevZ[i] = pos.y;
                }

                // ---- 프레임 루프 ----
                for (int frame = 1; frame <= ticks; frame++)
                {
                    CrowdOracleRecorder.ResetTick();
                    tickEvents.Clear();

                    crowd.SimTick(Dt);

                    int moved = 0;
                    double sumDisp = 0;
                    float maxDisp = 0;
                    int team0 = 0, team1 = 0, team2 = 0, team3 = 0, neutral = 0;

                    for (int i = 0; i < agentCount; i++)
                    {
                        crowd.OracleReadAgent(i, out int _, out int team, out bool _, out Vector2 pos, out float _);
                        bw.Write(pos.x);
                        bw.Write(pos.y);
                        bw.Write((sbyte)Mathf.Clamp(team, -1, 3));

                        float dx = pos.x - prevX[i];
                        float dz = pos.y - prevZ[i];
                        float d = Mathf.Sqrt(dx * dx + dz * dz);
                        if (d > 1e-6f) moved++;
                        sumDisp += d;
                        if (d > maxDisp) maxDisp = d;
                        prevX[i] = pos.x;
                        prevZ[i] = pos.y;

                        switch (team)
                        {
                            case 0: team0++; break;
                            case 1: team1++; break;
                            case 2: team2++; break;
                            case 3: team3++; break;
                            default: neutral++; break;
                        }
                    }

                    float leaderX = crowd.PlayerLeaderTransform != null ? crowd.PlayerLeaderTransform.position.x : float.NaN;
                    float leaderZ = crowd.PlayerLeaderTransform != null ? crowd.PlayerLeaderTransform.position.z : float.NaN;
                    double meanDisp = agentCount > 0 ? sumDisp / agentCount : 0;

                    summary.WriteLine(string.Join(",", new[]
                    {
                        scale.ToString(), seed.ToString(), frame.ToString(), agentCount.ToString(),
                        CrowdOracleRecorder.RngDrawsThisTick.ToString(),
                        CrowdOracleRecorder.WanderRepickCount.ToString(),
                        CrowdOracleRecorder.WanderAcceptedCount.ToString(),
                        CrowdOracleRecorder.WanderFailedCount.ToString(),
                        CrowdOracleRecorder.RivalDecisions.ToString(),
                        CrowdOracleRecorder.RivalHeadingChanged.ToString(),
                        moved.ToString(), F(meanDisp), F(maxDisp),
                        team0.ToString(), team1.ToString(), team2.ToString(), team3.ToString(), neutral.ToString(),
                        F(leaderX), F(leaderZ),
                    }));

                    for (int e = 0; e < tickEvents.Count; e++)
                    {
                        int[] ev = tickEvents[e];
                        events.WriteLine($"{scale},{seed},{frame},{e},{(ev[0] == 0 ? "CountChanged" : "Eliminated")},{ev[1]},{ev[2]}");
                    }
                }
            }
        }
        finally
        {
            CrowdOracleRecorder.Enabled = false;
            EventManager.GetSubscriber<CrowdCountChangedEvent>().Unsubscribe(onCount);
            EventManager.GetSubscriber<CrowdEliminatedEvent>().Unsubscribe(onElim);
            UnityEngine.Object.DestroyImmediate(go);
            UnityEngine.Object.DestroyImmediate(cfg);
        }
    }

    // 첫 combo를 임시 파일로 재실행해 바이너리 동일성을 확인한다(결정성 게이트).
    private static void VerifyDeterminism(
        GameConfigSO baseConfig, Transform cityRoot, FieldInfo neutralCountField, FieldInfo seedField,
        int scale, int seed, int ticks, string firstBinPath, string outDir)
    {
        string tmpPath = Path.Combine(outDir, $"_verify_n{scale}_s{seed}.bin");
        using (var nullSummary = new StreamWriter(Path.Combine(outDir, "_verify_summary.tmp"), false))
        using (var nullEvents = new StreamWriter(Path.Combine(outDir, "_verify_events.tmp"), false))
        {
            nullSummary.WriteLine("h");
            nullEvents.WriteLine("h");
            RunCombo(baseConfig, cityRoot, neutralCountField, seedField, scale, seed, ticks, tmpPath, nullSummary, nullEvents);
        }

        bool identical = FilesEqual(firstBinPath, tmpPath);
        string notePath = Path.Combine(outDir, "phaseC_oracle_determinism.txt");
        File.WriteAllText(notePath,
            $"# Phase C 단계 0 oracle 결정성 확인\n" +
            $"# {DateTime.Now:yyyy-MM-dd HH:mm:ss}\n" +
            $"combo: n={scale} seed={seed} ticks={ticks}\n" +
            $"result: {(identical ? "PASS (동일 seed 2회 실행 바이너리 동일 = 계측이 결정론 불변)" : "FAIL (바이너리 불일치)")}\n");

        try { File.Delete(tmpPath); } catch { /* best-effort 정리 */ }
        try { File.Delete(Path.Combine(outDir, "_verify_summary.tmp")); } catch { }
        try { File.Delete(Path.Combine(outDir, "_verify_events.tmp")); } catch { }

        Debug.Log($"[CrowdOracleHarness] 결정성 {(identical ? "PASS" : "FAIL")} -> {notePath}");
        if (!identical)
        {
            throw new InvalidOperationException("oracle 결정성 검증 실패: 동일 seed 2회 실행 결과가 다릅니다.");
        }
    }

    private static bool FilesEqual(string a, string b)
    {
        var fa = new FileInfo(a);
        var fb = new FileInfo(b);
        if (!fa.Exists || !fb.Exists || fa.Length != fb.Length) return false;
        using (var sa = fa.OpenRead())
        using (var sb = fb.OpenRead())
        {
            const int bufSize = 1 << 16;
            var ba = new byte[bufSize];
            var bb = new byte[bufSize];
            int n;
            while ((n = sa.Read(ba, 0, bufSize)) > 0)
            {
                int m = sb.Read(bb, 0, bufSize);
                if (m != n) return false;
                for (int i = 0; i < n; i++) if (ba[i] != bb[i]) return false;
            }

            return true;
        }
    }

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

    private static string F(double v)
    {
        return v.ToString("0.######", CultureInfo.InvariantCulture);
    }
}
#endif

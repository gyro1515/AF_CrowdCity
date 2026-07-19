#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using Unity.Profiling;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// GPU 크라우드 + 밀도 cap을 강제 ON한 상태에서 FULL play-mode 프레임 시간(sim + 실제 GPU 렌더)을
/// 유닛 수 스윕({500..10000})으로 측정하는 Editor 전용 harness다(런타임 빌드 미포함).
///
/// 단일 play 세션이 모든 count를 오름차순으로 순차 측정하고, 각 count 결과를 측정 직후 즉시
/// out 파일에 append+flush한다(고 count 크래시가 나도 앞선 결과는 보존).
///
/// edit 진입점(RunFromBatch)은 GameScene을 열고 auto-bootstrap(GameSceneController)을 disable해
/// play mode에서 두 번째 crowd가 스폰되지 않게 한 뒤 EnterPlaymode한다. play mode로 넘어가면
/// RuntimeInitializeOnLoadMethod(PlayBoot)가 domain reload 후 CrowdPerfDriver를 재생성해 측정을 구동한다.
///
/// 배치 실행 예(그래픽 필요, -nographics/-quit 금지):
///   Unity -batchmode -projectPath &lt;proj&gt; -executeMethod CrowdPerfHarness.RunFromBatch \
///     -logFile &lt;log&gt; -perfOut &lt;out.txt&gt; [-perfCounts "500,1000,..."]
/// </summary>
public static class CrowdPerfHarness
{
    private const string ScenePath = "Assets/@Project/Scenes/GameScene.unity";
    private static readonly int[] DefaultCounts = { 500, 1000, 2000, 3000, 5000, 7000, 10000 };

    // SessionState 키(domain reload 생존, Unity 종료 시 소거).
    private const string KeyActive = "CrowdPerfHarness.Active";
    private const string KeyOut = "CrowdPerfHarness.Out";
    private const string KeyCounts = "CrowdPerfHarness.Counts";

    /// <summary>-executeMethod 진입점(edit 모드). 씬을 열고 auto-bootstrap을 disable한 뒤 EnterPlaymode하고 return한다.</summary>
    public static void RunFromBatch()
    {
        try
        {
            string outPath = null;
            string countsCsv = null;
            string[] args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "-perfOut" && i + 1 < args.Length) outPath = args[i + 1];
                else if (args[i] == "-perfCounts" && i + 1 < args.Length) countsCsv = args[i + 1];
            }

            if (string.IsNullOrEmpty(outPath))
            {
                outPath = Path.Combine(Directory.GetParent(Application.dataPath).FullName, "perf_out.txt");
            }

            int[] counts = ParseCounts(countsCsv);

            EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);

            GameSceneController gsc = UnityEngine.Object.FindFirstObjectByType<GameSceneController>(FindObjectsInactive.Include);
            if (gsc == null)
            {
                throw new InvalidOperationException("GameScene에서 GameSceneController를 찾지 못했습니다.");
            }

            // auto-bootstrap(Start)이 play mode에서 두 번째 crowd를 스폰하지 못하게 컴포넌트만 disable한다(삭제 금지: 직렬화 참조를 읽어야 함).
            // enabled=false는 edit→play 직렬화로 보존된다.
            gsc.enabled = false;

            // out 파일 header를 새로 쓴다(이후 count별로 append).
            Directory.CreateDirectory(Path.GetDirectoryName(outPath));
            string header =
                "# CrowdPerfHarness FULL play-mode frame time (GPU crowd ON + SeparationVisitBudget=48)\n" +
                "# generated: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "\n" +
                "# scene: " + ScenePath + "\n" +
                "# render resolution: 1920x1080, camera framed on crowd, GPU-sync via 1x1 readback each measured frame\n" +
                "# full_ms = wall-clock frame period(realtime delta, sim+render+everything); fps determinant\n" +
                "# render_ms = per-frame scene render + GPU sync wall time; simtick_ms = per 0.02s sim step\n" +
                "# main_cpu_ms / gpu_ms = ProfilerRecorder/FrameTiming best-effort (may be unavailable in batchmode)\n" +
                "# columns: count,agents,spawnOK,gpuActive,smr,anim,full_median_ms,full_p95_ms,render_median_ms,simtick_ms,main_cpu_ms,gpu_ms,bound\n";
            File.WriteAllText(outPath, header);

            SessionState.SetBool(KeyActive, true);
            SessionState.SetString(KeyOut, outPath);
            SessionState.SetString(KeyCounts, string.Join(",", counts));

            Debug.Log($"[CrowdPerf] RunFromBatch: scene opened, GameSceneController disabled, counts=[{string.Join(",", counts)}], out={outPath}. Entering play mode.");
            EditorApplication.EnterPlaymode();
        }
        catch (Exception e)
        {
            Debug.LogError("[CrowdPerf] RunFromBatch 실패: " + e);
            SessionState.SetBool(KeyActive, false);
            EditorApplication.Exit(3);
        }
    }

    // domain reload 후 play mode boot에서 driver를 재생성한다(runtime-registered playModeStateChanged는 reload를 못 넘기므로 이 방식이 견고).
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void PlayBoot()
    {
        if (!SessionState.GetBool(KeyActive, false))
        {
            return;
        }

        var go = new GameObject("CrowdPerfDriver");
        UnityEngine.Object.DontDestroyOnLoad(go);
        go.AddComponent<CrowdPerfDriver>();
    }

    internal static int[] ParseCounts(string csv)
    {
        if (string.IsNullOrEmpty(csv))
        {
            return DefaultCounts;
        }

        var list = new List<int>();
        foreach (string part in csv.Split(','))
        {
            if (int.TryParse(part.Trim(), out int v) && v > 0) list.Add(v);
        }

        return list.Count > 0 ? list.ToArray() : DefaultCounts;
    }
}

/// <summary>
/// play mode에서 실제 측정을 구동하는 MonoBehaviour다(Editor 어셈블리, play mode에서 AddComponent 가능).
/// 각 count마다: config 복제(neutralCount + SeparationVisitBudget=48 + useGpuCrowdRenderer=true) → CrowdRoot 프리팹 스폰
/// → warmup(~2s, 크라우드 spread + rig 파괴 처리) → 120프레임 측정(fixed-step sim + 실제 씬 렌더 + GPU sync) → 결과 append.
/// </summary>
public sealed class CrowdPerfDriver : MonoBehaviour
{
    private const int RenderWidth = 1920;
    private const int RenderHeight = 1080;
    private const float WarmupSeconds = 2f;
    private const int MeasureFrames = 120;
    private const float FixedStep = 0.02f;
    private const int MaxStepsPerFrame = 4;

    private const string KeyActive = "CrowdPerfHarness.Active";
    private const string KeyOut = "CrowdPerfHarness.Out";
    private const string KeyCounts = "CrowdPerfHarness.Counts";

    private IEnumerator Start()
    {
        yield return Run();
    }

    private IEnumerator Run()
    {
        string outPath = SessionState.GetString(KeyOut, "");
        int[] counts = CrowdPerfHarness.ParseCounts(SessionState.GetString(KeyCounts, ""));

        int exitCode = 0;
        RenderTexture rt = null;
        Texture2D readTex = null;
        try
        {
            // 상한 해제: 실제 compute를 deltaTime이 반영하도록.
            Application.targetFrameRate = -1;
            QualitySettings.vSyncCount = 0;

            Debug.Log($"[CrowdPerf] graphicsDeviceType={SystemInfo.graphicsDeviceType} graphicsDeviceName={SystemInfo.graphicsDeviceName} " +
                      $"shaderLevel={SystemInfo.graphicsShaderLevel} supportsComputeShaders={SystemInfo.supportsComputeShaders} supportsInstancing={SystemInfo.supportsInstancing}");

            // 비활성화된 GameSceneController에서 직렬화 참조를 리플렉션으로 읽는다.
            GameSceneController gsc = UnityEngine.Object.FindFirstObjectByType<GameSceneController>(FindObjectsInactive.Include);
            if (gsc == null)
            {
                throw new InvalidOperationException("play mode에서 GameSceneController를 찾지 못했습니다.");
            }

            GameConfigSO baseConfig = (GameConfigSO)GetField(typeof(GameSceneController), "config").GetValue(gsc);
            Transform cityRoot = (Transform)GetField(typeof(GameSceneController), "cityRoot").GetValue(gsc);
            Camera cam = (Camera)GetField(typeof(GameSceneController), "mainCamera").GetValue(gsc);
            if (baseConfig == null || cityRoot == null || cam == null)
            {
                throw new InvalidOperationException($"config({baseConfig}) / cityRoot({cityRoot}) / mainCamera({cam}) 참조를 읽지 못했습니다.");
            }

            cam.gameObject.SetActive(true);
            cam.enabled = true;
            cam.nearClipPlane = 0.1f;
            cam.farClipPlane = 3000f;
            cam.fieldOfView = 50f;

            FieldInfo neutralCountField = GetField(typeof(GameConfigSO), "neutralCount");
            FieldInfo gpuFlagField = GetField(typeof(GameConfigSO), "useGpuCrowdRenderer");

            CrowdRoot crowdPrefab = ResourceLoader.LoadPrefab<CrowdRoot>();

            rt = new RenderTexture(RenderWidth, RenderHeight, 24, RenderTextureFormat.ARGB32) { antiAliasing = 1 };
            rt.Create();
            readTex = new Texture2D(1, 1, TextureFormat.RGB24, false);

            foreach (int count in counts)
            {
                yield return MeasureCount(count, baseConfig, cityRoot, cam, neutralCountField, gpuFlagField, crowdPrefab, rt, readTex, outPath);
            }

            Debug.Log("[CrowdPerf] 모든 count 측정 완료.");
        }
        finally
        {
            if (readTex != null) UnityEngine.Object.Destroy(readTex);
            if (rt != null) rt.Release();
            SessionState.SetBool(KeyActive, false);
            EditorApplication.Exit(exitCode);
        }
    }

    private IEnumerator MeasureCount(
        int count, GameConfigSO baseConfig, Transform cityRoot, Camera cam,
        FieldInfo neutralCountField, FieldInfo gpuFlagField, CrowdRoot crowdPrefab,
        RenderTexture rt, Texture2D readTex, string outPath)
    {
        GameConfigSO cfg = null;
        CrowdRoot crowd = null;

        // 아래는 예외가 나도 다음 count로 계속하기 위해 상태를 바깥에서 잡는다(coroutine이라 try/catch로 yield를 감쌀 수 없어 예외 플래그로 처리).
        Exception spawnEx = null;
        bool spawnOk = false;

        // 1) 스폰 준비 + Initialize/SpawnInitial(예외 시 기록만 하고 teardown으로).
        try
        {
            cfg = UnityEngine.Object.Instantiate(baseConfig);
            cfg.name = baseConfig.name + "_perf_n" + count;
            neutralCountField.SetValue(cfg, count);
            SetSepBudget(cfg, 48); // 밀도 cap 강제 ON.

            crowd = UnityEngine.Object.Instantiate(crowdPrefab);
            crowd.gameObject.name = "PerfCrowdRoot_n" + count;
            gpuFlagField.SetValue(cfg, true); // GPU 크라우드 경로 강제 ON(in-memory only, 복제 config·원본 asset 불변).

            crowd.Initialize(cfg, cityRoot, cfg.NeutralCount);
            crowd.SpawnInitial();
            crowd.OnMatchStateChanged(MatchState.Playing);
            crowd.SetPlayerHeading(new Vector2(0.85f, 0.53f).normalized, true);
            spawnOk = true;
        }
        catch (Exception e)
        {
            spawnEx = e;
        }

        if (!spawnOk)
        {
            AppendLine(outPath, $"count={count} FAILED(spawn): {Short(spawnEx)}");
            Debug.LogError($"[CrowdPerf] count={count} FAILED(spawn): {spawnEx}");
            if (crowd != null) UnityEngine.Object.Destroy(crowd.gameObject);
            if (cfg != null) UnityEngine.Object.Destroy(cfg);
            yield return null;
            yield return null;
            GC.Collect();
            yield return null;
            yield break;
        }

        // 2) Warmup: fixed-step sim + RenderInterpolate로 크라우드를 퍼뜨리고 rig 파괴(deferred Destroy)를 소화한다.
        float accumulator = 0f;
        float warmStart = Time.realtimeSinceStartup;
        bool headingFlipped = false;
        while (Time.realtimeSinceStartup - warmStart < WarmupSeconds)
        {
            if (!headingFlipped && Time.realtimeSinceStartup - warmStart > WarmupSeconds * 0.5f)
            {
                crowd.SetPlayerHeading(new Vector2(-0.4f, 0.9f).normalized, true); // 곡선으로 퍼지게 heading 한 번 틀기.
                headingFlipped = true;
            }

            StepSim(crowd, ref accumulator);
            yield return null;
        }

        // 3) GPU 활성 + rig 파괴 검증.
        CrowdRenderer renderer = crowd.GetComponentInChildren<CrowdRenderer>(true);
        bool gpuActive = renderer != null && renderer.IsActive;
        int smrCount = crowd.GetComponentsInChildren<SkinnedMeshRenderer>(true).Length;
        int animCount = crowd.GetComponentsInChildren<Animator>(true).Length;
        int agents = crowd.OracleAgentCount;

        // 4) 카메라를 크라우드에 프레임(instanced draw가 frustum 밖으로 컬링되지 않도록).
        FrameCameraOnCrowd(cam, crowd);

        // 5) 측정: 120프레임. per-frame full 프레임 시간(realtime delta) + render 구간 + sim 구간을 수집.
        var fullMs = new List<double>(MeasureFrames);
        var renderMs = new List<double>(MeasureFrames);
        double totalSimMs = 0;
        long totalSteps = 0;
        double toMs = 1000.0 / System.Diagnostics.Stopwatch.Frequency;

        ProfilerRecorder recMain = default;
        ProfilerRecorder recGpu = default;
        try { recMain = ProfilerRecorder.StartNew(ProfilerCategory.Internal, "Main Thread"); } catch { }
        recGpu = TryStartGpuRecorder();
        double mainSum = 0; int mainN = 0;
        double gpuRecSum = 0; int gpuRecN = 0;
        double gpuFtSum = 0; int gpuFtN = 0;

        double prev = Time.realtimeSinceStartupAsDouble;
        for (int f = 0; f < MeasureFrames; f++)
        {
            // sim 구간(fixed-step accumulator + RenderInterpolate: GPU 인스턴스 버퍼를 채워 이번 프레임 draw를 발행).
            long s0 = System.Diagnostics.Stopwatch.GetTimestamp();
            int steps = StepSim(crowd, ref accumulator);
            long s1 = System.Diagnostics.Stopwatch.GetTimestamp();
            totalSimMs += (s1 - s0) * toMs;
            totalSteps += steps;

            // render 구간(씬 카메라를 RT로 실제 렌더 + 1x1 readback으로 GPU 완료 동기화 → wall time에 GPU 포함).
            long r0 = System.Diagnostics.Stopwatch.GetTimestamp();
            RenderSceneAndSync(cam, rt, readTex);
            long r1 = System.Diagnostics.Stopwatch.GetTimestamp();
            renderMs.Add((r1 - r0) * toMs);

            // best-effort CPU/GPU split.
            if (recMain.Valid) { mainSum += recMain.LastValue; mainN++; }
            if (recGpu.Valid) { long g = recGpu.LastValue; if (g > 0) { gpuRecSum += g; gpuRecN++; } }
            double ftGpu = TryFrameTimingGpuMs();
            if (ftGpu > 0) { gpuFtSum += ftGpu; gpuFtN++; }

            yield return null;

            double now = Time.realtimeSinceStartupAsDouble;
            fullMs.Add((now - prev) * 1000.0);
            prev = now;
        }

        if (recMain.Valid) recMain.Dispose();
        if (recGpu.Valid) recGpu.Dispose();

        // 6) 집계.
        double fullMedian = Percentile(fullMs, 0.50);
        double fullP95 = Percentile(fullMs, 0.95);
        double renderMedian = Percentile(renderMs, 0.50);
        double simtickMs = totalSteps > 0 ? totalSimMs / totalSteps : 0;
        double mainCpuMs = mainN > 0 ? mainSum / mainN / 1e6 : -1;      // ns → ms
        double gpuRecMs = gpuRecN > 0 ? gpuRecSum / gpuRecN / 1e6 : -1; // ns → ms
        double gpuMs = gpuRecMs > 0 ? gpuRecMs : (gpuFtN > 0 ? gpuFtSum / gpuFtN : -1); // recorder 우선, 없으면 FrameTiming(ms)

        string bound;
        if (gpuMs > 0 && mainCpuMs > 0)
        {
            bound = gpuMs > mainCpuMs
                ? $"GPU-bound(gpu={F(gpuMs)}>main={F(mainCpuMs)})"
                : $"CPU-bound(main={F(mainCpuMs)}>=gpu={F(gpuMs)})";
        }
        else
        {
            // GPU 계측 불가: render 구간(render+GPU wall) vs pure simtick 비교로 추론.
            bound = renderMedian > simtickMs
                ? $"render/GPU-bound(renderMed={F(renderMedian)}>simtick={F(simtickMs)},profilerGPU=NA)"
                : $"sim/CPU-bound(simtick={F(simtickMs)}>=renderMed={F(renderMedian)},profilerGPU=NA)";
        }

        string gpuStr = gpuMs > 0 ? F(gpuMs) : "unavailable";
        string mainStr = mainCpuMs > 0 ? F(mainCpuMs) : "unavailable";

        string line = string.Join(",", new[]
        {
            count.ToString(), agents.ToString(), spawnOk ? "Y" : "N",
            gpuActive ? "T" : "F", smrCount.ToString(), animCount.ToString(),
            F(fullMedian), F(fullP95), F(renderMedian), F(simtickMs),
            mainStr, gpuStr, bound,
        });
        AppendLine(outPath, line);
        Debug.Log($"[CrowdPerf] count={count} agents={agents} median={F(fullMedian)}ms p95={F(fullP95)}ms " +
                  $"render={F(renderMedian)}ms simtick={F(simtickMs)}ms cpuMain={mainStr}ms gpu={gpuStr}ms " +
                  $"bound={bound} gpuActive={gpuActive} rigs={smrCount} anims={animCount}");

        // 7) teardown.
        if (crowd != null) UnityEngine.Object.Destroy(crowd.gameObject);
        if (cfg != null) UnityEngine.Object.Destroy(cfg);
        yield return null;
        yield return null;
        GC.Collect();
        yield return null;
        yield return null;
    }

    // GameplayRoot.Update의 fixed-step accumulator 루프를 충실히 재현한다. 실행한 sim step 수를 반환한다.
    private int StepSim(CrowdRoot crowd, ref float accumulator)
    {
        accumulator += Time.unscaledDeltaTime;
        int steps = 0;
        while (accumulator >= FixedStep && steps < MaxStepsPerFrame)
        {
            accumulator -= FixedStep;
            steps++;
            crowd.SimTick(FixedStep);
        }

        if (accumulator >= FixedStep)
        {
            accumulator %= FixedStep;
        }

        float alpha = Mathf.Clamp01(accumulator / FixedStep);
        crowd.RenderInterpolate(alpha); // GPU 인스턴스 draw는 여기서 매 프레임 발행된다.
        return steps;
    }

    // 씬 카메라를 RT로 실제 렌더하고 1x1 readback으로 GPU 완료를 동기화한다(batchmode에서도 실제 GPU 실행 보장 + wall time에 GPU 포함).
    private static void RenderSceneAndSync(Camera cam, RenderTexture rt, Texture2D readTex)
    {
        RenderTexture prevTarget = cam.targetTexture;
        RenderTexture prevActive = RenderTexture.active;
        cam.targetTexture = rt;

        bool rendered = false;
        RenderPipeline.StandardRequest req = new RenderPipeline.StandardRequest { destination = rt };
        if (RenderPipeline.SupportsRenderRequest(cam, req))
        {
            RenderPipeline.SubmitRenderRequest(cam, req);
            rendered = true;
        }

        if (!rendered)
        {
#pragma warning disable CS0618
            cam.Render();
#pragma warning restore CS0618
        }

        // 1x1 ReadPixels는 rt 렌더 완료까지 CPU를 블록한다(GPU sync). 크기 무관 고정 비용이라 count 스케일 분석을 왜곡하지 않는다.
        RenderTexture.active = rt;
        readTex.ReadPixels(new Rect(0, 0, 1, 1), 0, 0);
        readTex.Apply(false);
        RenderTexture.active = prevActive;
        cam.targetTexture = prevTarget;
    }

    // 스폰된 Human clone(rig 파괴돼도 root transform은 생존) 위치로 크라우드 bounds를 구해 카메라를 3/4 뷰로 배치한다.
    private static void FrameCameraOnCrowd(Camera cam, CrowdRoot crowd)
    {
        Human[] humans = crowd.GetComponentsInChildren<Human>(true);
        Vector3 center;
        float radius;
        if (humans.Length == 0)
        {
            center = Vector3.zero;
            radius = 40f;
        }
        else
        {
            Bounds b = new Bounds(humans[0].transform.position, Vector3.zero);
            for (int i = 1; i < humans.Length; i++) b.Encapsulate(humans[i].transform.position);
            center = b.center;
            radius = Mathf.Max(8f, Mathf.Max(b.extents.x, b.extents.z));
        }

        Vector3 lookAt = center + Vector3.up * (radius * 0.15f);
        float dist = radius * 2.1f + 6f;
        Quaternion rot = Quaternion.Euler(32f, 35f, 0f);
        Vector3 forward = rot * Vector3.forward;
        cam.transform.position = lookAt - forward * dist;
        cam.transform.rotation = rot;
    }

    // 알려진 GPU frame time counter를 시도한다(플랫폼별 이름/카테고리 편차 흡수). 실패하면 invalid recorder를 반환한다.
    private static ProfilerRecorder TryStartGpuRecorder()
    {
        (ProfilerCategory cat, string name)[] candidates =
        {
            (ProfilerCategory.Render, "GPU Frame Time"),
            (ProfilerCategory.Internal, "GPU Frame Time"),
            (ProfilerCategory.Render, "GPU Total Frame Time"),
        };

        foreach (var c in candidates)
        {
            try
            {
                ProfilerRecorder r = ProfilerRecorder.StartNew(c.cat, c.name);
                if (r.Valid) return r;
                r.Dispose();
            }
            catch { }
        }

        return default;
    }

    // FrameTimingManager로 최신 GPU frame time(ms)을 best-effort로 읽는다(Metal 지원, editor에서 미가용일 수 있음). 미가용이면 0.
    private static readonly FrameTiming[] FtScratch = new FrameTiming[1];
    private static double TryFrameTimingGpuMs()
    {
        try
        {
            FrameTimingManager.CaptureFrameTimings();
            uint got = FrameTimingManager.GetLatestTimings(1, FtScratch);
            if (got > 0)
            {
                return FtScratch[0].gpuFrameTime; // ms
            }
        }
        catch { }

        return 0;
    }

    // 측정 전용: 복제 config의 SimTuning.SeparationVisitBudget만 덮어쓴다(struct라 box→set→unbox). CrowdProfileHarness.SetSepBudget과 동일 패턴.
    private static void SetSepBudget(GameConfigSO cfg, int budget)
    {
        FieldInfo simField = GetField(typeof(GameConfigSO), "sim");
        FieldInfo budgetField = typeof(SimTuning).GetField("SeparationVisitBudget");
        if (budgetField == null)
        {
            throw new InvalidOperationException("SimTuning.SeparationVisitBudget 필드를 리플렉션으로 찾지 못했습니다.");
        }

        object boxed = simField.GetValue(cfg);
        budgetField.SetValue(boxed, budget);
        simField.SetValue(cfg, boxed);
    }

    private static FieldInfo GetField(Type type, string name)
    {
        FieldInfo f = type.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
        if (f == null)
        {
            throw new InvalidOperationException($"{type.Name}.{name} 필드를 리플렉션으로 찾지 못했습니다.");
        }

        return f;
    }

    private static void AppendLine(string path, string line)
    {
        File.AppendAllText(path, line + "\n"); // AppendAllText는 open/write/close로 flush된다(고 count 크래시에도 앞선 결과 보존).
    }

    private static double Percentile(List<double> values, double q)
    {
        if (values.Count == 0) return 0;
        var sorted = new List<double>(values);
        sorted.Sort();
        int idx = (int)Math.Round(q * (sorted.Count - 1), MidpointRounding.AwayFromZero);
        if (idx < 0) idx = 0;
        if (idx >= sorted.Count) idx = sorted.Count - 1;
        return sorted[idx];
    }

    private static string F(double v)
    {
        return v.ToString("0.###", CultureInfo.InvariantCulture);
    }

    private static string Short(Exception e)
    {
        if (e == null) return "unknown";
        string m = e.Message.Replace('\n', ' ').Replace('\r', ' ').Replace(',', ';');
        return e.GetType().Name + ": " + (m.Length > 200 ? m.Substring(0, 200) : m);
    }
}
#endif

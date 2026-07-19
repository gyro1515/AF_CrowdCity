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
/// CrowdPerfHarness의 P95 변형이다(TEMP, 미추적). base harness와 완전히 분리된 타입/SessionState 키를 쓰며
/// GPU 크라우드 + 밀도 cap을 강제 ON한 상태에서 FULL play-mode 프레임 시간을 유닛 수 스윕으로 측정한다.
///
/// base 대비 차이: 측정 프레임 기본 300(-perfFrames 조절), warmup 기본 2.0s(-perfWarmup 조절),
/// full_ms의 median/p95/max, ticksPerFrame, Main Thread ProfilerRecorder의 median/p95/max(불가 시 "unavailable")를
/// 방출하는 CSV(comma-separated) 포맷. sim cadence(FixedStep=0.02, MaxSteps=4, 실제 Time.deltaTime)와
/// RenderPipeline.SubmitRenderRequest + 1x1 ReadPixels GPU-sync 렌더 경로는 base와 동일하다.
///
/// 배치 실행 예(그래픽 필요, -nographics/-quit 금지):
///   Unity -batchmode -projectPath &lt;proj&gt; -executeMethod CrowdPerfHarnessP95.RunFromBatch \
///     -logFile &lt;log&gt; -perfOut &lt;out.csv&gt; [-perfCounts "4000,6000,8000,10000"] [-perfFrames 300] [-perfWarmup 2.0]
/// </summary>
public static class CrowdPerfHarnessP95
{
    private const string ScenePath = "Assets/@Project/Scenes/GameScene.unity";
    private static readonly int[] DefaultCounts = { 4000, 6000, 8000, 10000 };

    internal const int DefaultFrames = 300;
    internal const float DefaultWarmup = 2.0f;

    // base harness와 절대 겹치지 않는 SessionState 키(domain reload 생존, Unity 종료 시 소거).
    // base의 "CrowdPerfHarness.*" 키는 건드리지 않는다(base PlayBoot이 오발되지 않도록 서로 독립).
    internal const string KeyActive = "CrowdPerfP95.Active";
    internal const string KeyOut = "CrowdPerfP95.Out";
    internal const string KeyCounts = "CrowdPerfP95.Counts";
    internal const string KeyFrames = "CrowdPerfP95.Frames";
    internal const string KeyWarmup = "CrowdPerfP95.Warmup";

    // CSV 열 계약(14개). 에러 행도 이 열 수에 맞춘다.
    internal const string CsvHeader =
        "neutralCount,agents,gpuActive,smr,anim,full_median_ms,full_p95_ms,full_max_ms,render_median_ms,simtick_median_ms,ticksPerFrame,mainthread_median_ms,mainthread_p95_ms,mainthread_max_ms";
    internal const int CsvColumnCount = 14;

    /// <summary>-executeMethod 진입점(edit 모드). 씬을 열고 auto-bootstrap을 disable한 뒤 EnterPlaymode하고 return한다.</summary>
    public static void RunFromBatch()
    {
        try
        {
            string outPath = null;
            string countsCsv = null;
            int frames = DefaultFrames;
            float warmup = DefaultWarmup;

            string[] args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "-perfOut" && i + 1 < args.Length) outPath = args[i + 1];
                else if (args[i] == "-perfCounts" && i + 1 < args.Length) countsCsv = args[i + 1];
                else if (args[i] == "-perfFrames" && i + 1 < args.Length)
                {
                    if (int.TryParse(args[i + 1], out int fv) && fv > 0) frames = fv;
                }
                else if (args[i] == "-perfWarmup" && i + 1 < args.Length)
                {
                    if (float.TryParse(args[i + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out float wv) && wv >= 0f) warmup = wv;
                }
            }

            if (string.IsNullOrEmpty(outPath))
            {
                outPath = Path.Combine(Directory.GetParent(Application.dataPath).FullName, "perf_out_p95.csv");
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

            // out 파일 header를 새로 쓴다(이후 count별로 append). 파일이 미리 존재하므로 play mode 크래시에도 header가 보존된다.
            string dir = Path.GetDirectoryName(outPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            string header =
                "# CrowdPerfHarnessP95 FULL play-mode frame time (GPU crowd ON + SeparationVisitBudget=48)\n" +
                "# generated: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "\n" +
                "# scene: " + ScenePath + "\n" +
                "# frames/count=" + frames + ", warmupSeconds=" + warmup.ToString("0.###", CultureInfo.InvariantCulture) + ", uncapped(vSync=0,targetFPS=-1)\n" +
                "# render: 1920x1080, camera framed on crowd, GPU-sync via 1x1 readback each measured frame\n" +
                "# full_ms=wall-clock frame period(realtime delta, whole frame); mainthread_ms=ProfilerRecorder 'Main Thread' (unavailable in some batchmode)\n" +
                CsvHeader + "\n";
            File.WriteAllText(outPath, header);

            // 오직 P95 키만 세운다. base의 "CrowdPerfHarness.Active"는 세우지 않으므로 base PlayBoot은 오발되지 않는다.
            SessionState.SetBool(KeyActive, true);
            SessionState.SetString(KeyOut, outPath);
            SessionState.SetString(KeyCounts, string.Join(",", counts));
            SessionState.SetInt(KeyFrames, frames);
            SessionState.SetFloat(KeyWarmup, warmup);

            Debug.Log($"[CrowdPerfP95] RunFromBatch: scene opened, GameSceneController disabled, counts=[{string.Join(",", counts)}], frames={frames}, warmup={warmup.ToString("0.###", CultureInfo.InvariantCulture)}, out={outPath}. Entering play mode.");
            EditorApplication.EnterPlaymode();
        }
        catch (Exception e)
        {
            Debug.LogError("[CrowdPerfP95] RunFromBatch 실패: " + e);
            SessionState.SetBool(KeyActive, false);
            EditorApplication.Exit(3);
        }
    }

    // domain reload 후 play mode boot에서 driver를 재생성한다. 오직 P95 키가 세워졌을 때만 동작한다(base와 완전히 독립).
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void PlayBoot()
    {
        if (!SessionState.GetBool(KeyActive, false))
        {
            return;
        }

        var go = new GameObject("CrowdPerfDriverP95");
        UnityEngine.Object.DontDestroyOnLoad(go);
        go.AddComponent<CrowdPerfDriverP95>();
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
/// play mode에서 실제 측정을 구동하는 MonoBehaviour다(Editor 어셈블리). base의 CrowdPerfDriver와 타입/키가 분리돼 있다.
/// 각 count마다: config 복제(neutralCount + SeparationVisitBudget=48 + useGpuCrowdRenderer=true) → CrowdRoot 프리팹 스폰
/// → warmup(기본 2s) → N프레임 측정(기본 300, fixed-step sim + 실제 씬 렌더 + GPU sync) → 결과 append.
/// 한 count에서 예외가 나도 에러 행을 쓰고 다음 count로 계속한다.
/// </summary>
public sealed class CrowdPerfDriverP95 : MonoBehaviour
{
    private const int RenderWidth = 1920;
    private const int RenderHeight = 1080;
    private const float FixedStep = 0.02f;
    private const int MaxStepsPerFrame = 4;

    private IEnumerator Start()
    {
        yield return Run();
    }

    private IEnumerator Run()
    {
        string outPath = SessionState.GetString(CrowdPerfHarnessP95.KeyOut, "");
        int[] counts = CrowdPerfHarnessP95.ParseCounts(SessionState.GetString(CrowdPerfHarnessP95.KeyCounts, ""));
        int frames = Mathf.Max(1, SessionState.GetInt(CrowdPerfHarnessP95.KeyFrames, CrowdPerfHarnessP95.DefaultFrames));
        float warmup = Mathf.Max(0f, SessionState.GetFloat(CrowdPerfHarnessP95.KeyWarmup, CrowdPerfHarnessP95.DefaultWarmup));

        int exitCode = 0;
        RenderTexture rt = null;
        Texture2D readTex = null;

        GameConfigSO baseConfig = null;
        Transform cityRoot = null;
        Camera cam = null;
        FieldInfo neutralCountField = null;
        FieldInfo gpuFlagField = null;
        CrowdRoot crowdPrefab = null;
        bool setupOk = false;

        // ---- setup phase (yield 없음): 치명적 실패는 여기서 잡아 비-0 종료로 만든다 ----
        try
        {
            // 상한 해제: 실제 compute가 wall-clock에 그대로 반영되도록(측정 루프 이전에 명시적으로 설정).
            // Time.captureFramerate는 건드리지 않고(==0 유지) Time.timeScale도 바꾸지 않는다.
            QualitySettings.vSyncCount = 0;
            Application.targetFrameRate = -1;

            Debug.Log($"[CrowdPerfP95] graphicsDeviceType={SystemInfo.graphicsDeviceType} graphicsDeviceName={SystemInfo.graphicsDeviceName} " +
                      $"shaderLevel={SystemInfo.graphicsShaderLevel} supportsComputeShaders={SystemInfo.supportsComputeShaders} supportsInstancing={SystemInfo.supportsInstancing}");

            // 비활성화된 GameSceneController에서 직렬화 참조를 리플렉션으로 읽는다.
            GameSceneController gsc = UnityEngine.Object.FindFirstObjectByType<GameSceneController>(FindObjectsInactive.Include);
            if (gsc == null)
            {
                throw new InvalidOperationException("play mode에서 GameSceneController를 찾지 못했습니다.");
            }

            baseConfig = (GameConfigSO)GetField(typeof(GameSceneController), "config").GetValue(gsc);
            cityRoot = (Transform)GetField(typeof(GameSceneController), "cityRoot").GetValue(gsc);
            cam = (Camera)GetField(typeof(GameSceneController), "mainCamera").GetValue(gsc);
            if (baseConfig == null || cityRoot == null || cam == null)
            {
                throw new InvalidOperationException($"config({baseConfig}) / cityRoot({cityRoot}) / mainCamera({cam}) 참조를 읽지 못했습니다.");
            }

            cam.gameObject.SetActive(true);
            cam.enabled = true;
            cam.nearClipPlane = 0.1f;
            cam.farClipPlane = 3000f;
            cam.fieldOfView = 50f;

            neutralCountField = GetField(typeof(GameConfigSO), "neutralCount");
            gpuFlagField = GetField(typeof(GameConfigSO), "useGpuCrowdRenderer");

            crowdPrefab = ResourceLoader.LoadPrefab<CrowdRoot>();

            rt = new RenderTexture(RenderWidth, RenderHeight, 24, RenderTextureFormat.ARGB32) { antiAliasing = 1 };
            rt.Create();
            readTex = new Texture2D(1, 1, TextureFormat.RGB24, false);

            setupOk = true;
        }
        catch (Exception e)
        {
            Debug.LogError("[CrowdPerfP95] setup 실패: " + e);
            exitCode = 4;
        }

        // ---- measure phase: 각 count는 자체 예외를 삼키고 계속한다. finally가 항상 정리+Exit를 보장한다. ----
        try
        {
            if (setupOk)
            {
                foreach (int count in counts)
                {
                    yield return MeasureCount(count, baseConfig, cityRoot, cam, neutralCountField, gpuFlagField, crowdPrefab, rt, readTex, outPath, frames, warmup);
                }

                Debug.Log("[CrowdPerfP95] 모든 count 측정 완료.");
            }
        }
        finally
        {
            if (readTex != null) UnityEngine.Object.Destroy(readTex);
            if (rt != null) rt.Release();
            SessionState.SetBool(CrowdPerfHarnessP95.KeyActive, false);
            EditorApplication.Exit(exitCode);
        }
    }

    private IEnumerator MeasureCount(
        int count, GameConfigSO baseConfig, Transform cityRoot, Camera cam,
        FieldInfo neutralCountField, FieldInfo gpuFlagField, CrowdRoot crowdPrefab,
        RenderTexture rt, Texture2D readTex, string outPath, int frames, float warmup)
    {
        GameConfigSO cfg = null;
        CrowdRoot crowd = null;
        Exception ex = null; // coroutine이라 yield를 try/catch로 감쌀 수 없어 예외 플래그로 처리한다.

        // 측정값 홀더(에러 시엔 에러 행만 쓰므로 채워지지 않아도 무해).
        bool gpuActive = false;
        int smrCount = -1;
        int animCount = -1;
        int agents = 0;
        var fullMs = new List<double>(frames);
        var renderMs = new List<double>(frames);
        var mainMs = new List<double>(frames);
        double totalSimMs = 0;
        long totalSteps = 0;
        double toMs = 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        float accumulator = 0f; // warmup→measure로 이어지는 fixed-step accumulator 상태.

        // 1) 스폰 준비 + Initialize/SpawnInitial(yield 없음).
        try
        {
            cfg = UnityEngine.Object.Instantiate(baseConfig);
            cfg.name = baseConfig.name + "_perfP95_n" + count;
            neutralCountField.SetValue(cfg, count);
            SetSepBudget(cfg, 48); // 밀도 cap 강제 ON.

            crowd = UnityEngine.Object.Instantiate(crowdPrefab);
            crowd.gameObject.name = "PerfP95CrowdRoot_n" + count;
            gpuFlagField.SetValue(cfg, true); // GPU 크라우드 경로 강제 ON(in-memory only, 복제 config·원본 asset 불변).

            crowd.Initialize(cfg, cityRoot, cfg.NeutralCount);
            crowd.SpawnInitial();
            crowd.OnMatchStateChanged(MatchState.Playing);
            crowd.SetPlayerHeading(new Vector2(0.85f, 0.53f).normalized, true);
        }
        catch (Exception e)
        {
            ex = e;
        }

        // 2) Warmup: fixed-step sim + RenderInterpolate로 크라우드를 퍼뜨리고 rig 파괴(deferred Destroy)를 소화한다.
        if (ex == null)
        {
            float warmStart = Time.realtimeSinceStartup;
            bool headingFlipped = false;
            while (ex == null && Time.realtimeSinceStartup - warmStart < warmup)
            {
                try
                {
                    if (!headingFlipped && Time.realtimeSinceStartup - warmStart > warmup * 0.5f)
                    {
                        crowd.SetPlayerHeading(new Vector2(-0.4f, 0.9f).normalized, true); // 곡선으로 퍼지게 heading 한 번 틀기.
                        headingFlipped = true;
                    }

                    StepSim(crowd, ref accumulator);
                }
                catch (Exception e)
                {
                    ex = e;
                }

                yield return null;
            }
        }

        // 3) GPU 활성 + rig 파괴 검증 + 카메라 프레이밍(yield 없음).
        if (ex == null)
        {
            try
            {
                CrowdRenderer renderer = crowd.GetComponentInChildren<CrowdRenderer>(true);
                gpuActive = renderer != null && renderer.IsActive;
                smrCount = crowd.GetComponentsInChildren<SkinnedMeshRenderer>(true).Length;
                animCount = crowd.GetComponentsInChildren<Animator>(true).Length;
                agents = crowd.OracleAgentCount;

                // 카메라를 크라우드에 프레임(instanced draw가 frustum 밖으로 컬링되지 않도록).
                FrameCameraOnCrowd(cam, crowd);
            }
            catch (Exception e)
            {
                ex = e;
            }
        }

        // 4) 측정: N프레임. per-frame full 프레임 시간(realtime delta) + render 구간 + sim 구간 + main-thread를 수집.
        if (ex == null)
        {
            ProfilerRecorder recMain = default;
            try { recMain = ProfilerRecorder.StartNew(ProfilerCategory.Internal, "Main Thread"); } catch { }

            double prev = Time.realtimeSinceStartupAsDouble;
            for (int f = 0; f < frames && ex == null; f++)
            {
                try
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

                    // Main Thread(ns): valid할 때만 수집(0 포함해 수집해야 all-zero 판정이 가능).
                    if (recMain.Valid) mainMs.Add(recMain.LastValue / 1e6); // ns → ms
                }
                catch (Exception e)
                {
                    ex = e;
                }

                yield return null;

                if (ex == null)
                {
                    double now = Time.realtimeSinceStartupAsDouble;
                    fullMs.Add((now - prev) * 1000.0);
                    prev = now;
                }
            }

            if (recMain.Valid) recMain.Dispose();
        }

        // 5) 집계 + 기록(yield 없음). 예외가 있었으면 에러 행을 쓰고 다음 count로 계속한다.
        if (ex != null)
        {
            string[] errFields = new string[CrowdPerfHarnessP95.CsvColumnCount];
            for (int i = 0; i < errFields.Length; i++) errFields[i] = "";
            errFields[0] = count.ToString();
            errFields[1] = "ERROR " + Short(ex);
            AppendLine(outPath, string.Join(",", errFields));
            Debug.LogError($"[CrowdPerfP95] count={count} FAILED: {ex}");
        }
        else
        {
            double fullMedian = Percentile(fullMs, 0.50);
            double fullP95 = Percentile(fullMs, 0.95);
            double fullMax = MaxOf(fullMs);
            double renderMedian = Percentile(renderMs, 0.50);
            double simtickMs = totalSteps > 0 ? totalSimMs / totalSteps : 0; // per 0.02s step(base와 동일 계산).
            int measuredFrames = fullMs.Count;
            double ticksPerFrame = measuredFrames > 0 ? (double)totalSteps / measuredFrames : 0;

            string mainMed, mainP95, mainMax;
            double mainMaxVal = MaxOf(mainMs);
            if (mainMs.Count == 0 || mainMaxVal <= 0) // invalid recorder 또는 all-zero(batchmode 흔함).
            {
                mainMed = mainP95 = mainMax = "unavailable";
            }
            else
            {
                mainMed = F(Percentile(mainMs, 0.50));
                mainP95 = F(Percentile(mainMs, 0.95));
                mainMax = F(mainMaxVal);
            }

            string line = string.Join(",", new[]
            {
                count.ToString(), agents.ToString(), gpuActive ? "T" : "F",
                smrCount.ToString(), animCount.ToString(),
                F(fullMedian), F(fullP95), F(fullMax),
                F(renderMedian), F(simtickMs), F(ticksPerFrame),
                mainMed, mainP95, mainMax,
            });
            AppendLine(outPath, line);
            Debug.Log($"[CrowdPerfP95] count={count} agents={agents} full(med/p95/max)={F(fullMedian)}/{F(fullP95)}/{F(fullMax)}ms " +
                      $"render={F(renderMedian)}ms simtick={F(simtickMs)}ms ticks/frame={F(ticksPerFrame)} " +
                      $"main(med/p95/max)={mainMed}/{mainP95}/{mainMax} gpuActive={gpuActive} smr={smrCount} anim={animCount}");
        }

        // 6) teardown.
        if (crowd != null) UnityEngine.Object.Destroy(crowd.gameObject);
        if (cfg != null) UnityEngine.Object.Destroy(cfg);
        yield return null;
        yield return null;
        GC.Collect();
        yield return null;
        yield return null;
    }

    // GameplayRoot.Update의 fixed-step accumulator 루프를 충실히 재현한다(실제 Time.deltaTime 사용). 실행한 sim step 수를 반환한다.
    private int StepSim(CrowdRoot crowd, ref float accumulator)
    {
        accumulator += Time.deltaTime;
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

    // 측정 전용: 복제 config의 SimTuning.SeparationVisitBudget만 덮어쓴다(struct라 box→set→unbox).
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

    private static double MaxOf(List<double> values)
    {
        if (values.Count == 0) return 0;
        double m = values[0];
        for (int i = 1; i < values.Count; i++)
        {
            if (values[i] > m) m = values[i];
        }

        return m;
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

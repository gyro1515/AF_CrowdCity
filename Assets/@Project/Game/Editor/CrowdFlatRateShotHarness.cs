#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// CombatFlatConvertRate=ON 전향 규칙의 feel-check용 스크린샷 harness다(Editor 전용, 런타임 빌드 미포함).
/// GameScene을 edit 모드로 열어 city/카메라/라이팅을 확보한 뒤, GameConfig를 복제(원본 asset 불변)해
/// CombatFlatConvertRate만 ON으로 켜고(리플렉션) 플레이어+라이벌 mixed-team crowd를 SimTick으로 구동하면서
/// 지정한 tick milestone마다 SMR 경로로 PNG를 렌더하고 팀별 인원 수를 로그로 남긴다. 소유자가 전향이
/// "즉시가 아니라 빠른 램프"로 보이는지 눈으로 확인하기 위한 것이며, 결정론/시뮬 로직은 건드리지 않는다.
///
/// 배치 실행 예(그래픽 필요, -nographics 금지):
///   Unity -batchmode -projectPath &lt;proj&gt; -quit \
///     -executeMethod CrowdFlatRateShotHarness.RunFromBatch -flatShotOut &lt;dir&gt; -flatShotNeutral 800 -flatShotTicks "150,300,450,600,800"
/// </summary>
public static class CrowdFlatRateShotHarness
{
    private const string ScenePath = "Assets/@Project/Scenes/GameScene.unity";
    private const float Dt = 0.02f;
    private const int Width = 1280;
    private const int Height = 720;

    public static void RunFromBatch()
    {
        string outDir = null;
        int neutral = 800;
        int[] milestones = { 150, 300, 450, 600, 800 };
        string[] args = Environment.GetCommandLineArgs();
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "-flatShotOut" && i + 1 < args.Length) outDir = args[i + 1];
            else if (args[i] == "-flatShotNeutral" && i + 1 < args.Length) int.TryParse(args[i + 1], out neutral);
            else if (args[i] == "-flatShotTicks" && i + 1 < args.Length) milestones = ParseInts(args[i + 1]);
        }

        if (string.IsNullOrEmpty(outDir))
        {
            outDir = Path.Combine(Directory.GetParent(Application.dataPath).FullName, "flatrate_shots");
        }

        int exit = 0;
        try
        {
            Run(outDir, neutral, milestones);
        }
        catch (Exception e)
        {
            Debug.LogError("[CrowdFlatRateShotHarness] 실패: " + e);
            exit = 3;
        }

        EditorApplication.Exit(exit);
    }

    private static void Run(string outDir, int neutral, int[] milestones)
    {
        Directory.CreateDirectory(outDir);
        Debug.Log($"[CrowdFlatRateShotHarness] 시작 out={outDir} neutral={neutral} ticks=[{string.Join(",", milestones)}]");

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
        Camera cam = so.FindProperty("mainCamera").objectReferenceValue as Camera;
        if (baseConfig == null || cityRoot == null || cam == null)
        {
            throw new InvalidOperationException($"config({baseConfig}) / cityRoot({cityRoot}) / mainCamera({cam}) 참조를 읽지 못했습니다.");
        }

        cam.gameObject.SetActive(true);
        cam.enabled = true;
        cam.nearClipPlane = 0.1f;
        cam.farClipPlane = 3000f;
        cam.fieldOfView = 50f;

        FieldInfo neutralCountField = typeof(GameConfigSO).GetField("neutralCount", BindingFlags.Instance | BindingFlags.NonPublic);
        FieldInfo gpuFlagField = typeof(GameConfigSO).GetField("useGpuCrowdRenderer", BindingFlags.Instance | BindingFlags.NonPublic);
        if (neutralCountField == null || gpuFlagField == null)
        {
            throw new InvalidOperationException("리플렉션 필드(neutralCount / useGpuCrowdRenderer)를 찾지 못했습니다.");
        }

        CrowdRoot crowdPrefab = ResourceLoader.LoadPrefab<CrowdRoot>();

        GameConfigSO cfg = UnityEngine.Object.Instantiate(baseConfig);
        cfg.name = baseConfig.name + "_flatrate";
        neutralCountField.SetValue(cfg, neutral);
        SetFlatRate(cfg, true); // 전투 flat 전향율 ON.

        CrowdRoot crowd = UnityEngine.Object.Instantiate(crowdPrefab);
        crowd.gameObject.name = "FlatRateShotCrowdRoot";
        gpuFlagField.SetValue(cfg, false); // SMR 경로 강제(팀 색상 렌더 신뢰성).

        try
        {
            crowd.Initialize(cfg, cityRoot, cfg.NeutralCount);
            crowd.SpawnInitial();
            crowd.OnMatchStateChanged(MatchState.Playing);
            crowd.SetPlayerHeading(new Vector2(0.85f, 0.53f).normalized, true);
            Physics.SyncTransforms();
            Debug.Log($"[CrowdFlatRateShotHarness] agents={crowd.OracleAgentCount} (SMR 경로, flat 전향율 ON)");

            int prevTick = 0;
            foreach (int milestone in milestones)
            {
                for (int t = prevTick; t < milestone; t++)
                {
                    // 곡선으로 퍼지며 라이벌과 부딪히도록 heading을 주기적으로 틀어준다.
                    if (t == 200) crowd.SetPlayerHeading(new Vector2(-0.4f, 0.9f).normalized, true);
                    else if (t == 450) crowd.SetPlayerHeading(new Vector2(-0.8f, -0.3f).normalized, true);
                    else if (t == 650) crowd.SetPlayerHeading(new Vector2(0.5f, -0.7f).normalized, true);
                    crowd.SimTick(Dt);
                }

                prevTick = milestone;
                TickAnimators(crowd);
                LogTeamCounts(crowd, milestone);

                // 전투 전향은 team(>=0)끼리의 접촉에서만 일어나므로, 카메라를 팀 유닛 무리(중립 제외)에 맞춘다(멜리 클로즈업).
                Bounds tb = ComputeTeamBounds(crowd);
                Vector3 center = tb.center;
                float radius = Mathf.Max(6f, Mathf.Max(tb.extents.x, tb.extents.z));
                Shot(cam, crowd, center, radius, 34f, 35f, 1.35f, 0.12f, Path.Combine(outDir, $"combat_flatrate_t{milestone}.png"));
            }
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(crowd.gameObject);
            UnityEngine.Object.DestroyImmediate(cfg);
        }

        Debug.Log($"[CrowdFlatRateShotHarness] 완료. PNG 저장 위치: {outDir}");
    }

    // 팀별 인원 수를 로그로 남긴다(전향 진행을 수치로 확인). 팀 색: 0=blue player, 1=red, 2=orange, 3=green, -1=neutral.
    private static void LogTeamCounts(CrowdRoot crowd, int tick)
    {
        int n = crowd.OracleAgentCount;
        int t0 = 0, t1 = 0, t2 = 0, t3 = 0, neu = 0;
        for (int i = 0; i < n; i++)
        {
            crowd.OracleReadAgent(i, out int _, out int team, out bool _, out Vector2 _, out float _);
            switch (team)
            {
                case 0: t0++; break;
                case 1: t1++; break;
                case 2: t2++; break;
                case 3: t3++; break;
                default: neu++; break;
            }
        }

        Debug.Log($"[CrowdFlatRateShotHarness] tick={tick} teams: player(blue)={t0} red={t1} orange={t2} green={t3} neutral={neu}");
    }

    private static void TickAnimators(CrowdRoot crowd)
    {
        try
        {
            Animator[] animators = crowd.GetComponentsInChildren<Animator>(true);
            for (int i = 0; i < animators.Length; i++)
            {
                Animator a = animators[i];
                if (a != null && a.enabled && a.runtimeAnimatorController != null)
                {
                    a.Update(1.3f);
                }
            }
        }
        catch (Exception e)
        {
            Debug.LogWarning("[CrowdFlatRateShotHarness] Animator 수동 평가 실패(정적 포즈로 폴백): " + e.Message);
        }
    }

    // 팀 소속(team>=0) 유닛만의 XZ bounds. 전투 전향이 벌어지는 mixed-team 멜리에 카메라를 맞추기 위한 것.
    // 팀 유닛이 아직 없으면 전체 crowd bounds로 폴백한다.
    private static Bounds ComputeTeamBounds(CrowdRoot crowd)
    {
        int n = crowd.OracleAgentCount;
        bool any = false;
        Bounds b = new Bounds(Vector3.zero, Vector3.zero);
        for (int i = 0; i < n; i++)
        {
            crowd.OracleReadAgent(i, out int _, out int team, out bool _, out Vector2 pos, out float _);
            if (team < 0)
            {
                continue;
            }

            Vector3 p = new Vector3(pos.x, 0.5f, pos.y); // Vector2.y == world Z, groundY≈0.5.
            if (!any)
            {
                b = new Bounds(p, Vector3.zero);
                any = true;
            }
            else
            {
                b.Encapsulate(p);
            }
        }

        return any ? b : ComputeCrowdBounds(crowd);
    }

    private static Bounds ComputeCrowdBounds(CrowdRoot crowd)
    {
        Human[] humans = crowd.GetComponentsInChildren<Human>(true);
        if (humans.Length == 0)
        {
            return new Bounds(Vector3.zero, Vector3.one * 80f);
        }

        Bounds b = new Bounds(humans[0].transform.position, Vector3.zero);
        for (int i = 1; i < humans.Length; i++)
        {
            b.Encapsulate(humans[i].transform.position);
        }

        return b;
    }

    private static void Shot(
        Camera cam, CrowdRoot crowd, Vector3 center, float radius,
        float pitchDeg, float yawDeg, float distFactor, float lookUpFactor, string path)
    {
        Vector3 lookAt = center + Vector3.up * (radius * lookUpFactor);
        float dist = radius * distFactor + 6f;
        Quaternion rot = Quaternion.Euler(pitchDeg, yawDeg, 0f);
        Vector3 forward = rot * Vector3.forward;
        cam.transform.position = lookAt - forward * dist;
        cam.transform.rotation = rot;

        RenderTexture rt = new RenderTexture(Width, Height, 24, RenderTextureFormat.ARGB32) { antiAliasing = 1 };
        rt.Create();

        RenderTexture prevTarget = cam.targetTexture;
        RenderTexture prevActive = RenderTexture.active;
        cam.targetTexture = rt;

        crowd.RenderInterpolate(1f); // GPU 경로면 즉시 draw 발행, SMR 경로면 no-op.

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

        RenderTexture.active = rt;
        Texture2D tex = new Texture2D(Width, Height, TextureFormat.RGB24, false);
        tex.ReadPixels(new Rect(0, 0, Width, Height), 0, 0);
        tex.Apply();
        File.WriteAllBytes(path, tex.EncodeToPNG());

        RenderTexture.active = prevActive;
        cam.targetTexture = prevTarget;
        rt.Release();
        UnityEngine.Object.DestroyImmediate(tex);
        UnityEngine.Object.DestroyImmediate(rt);

        Debug.Log($"[CrowdFlatRateShotHarness] 저장: {path} (pitch={pitchDeg} yaw={yawDeg} dist={dist:F1})");
    }

    // feel-check 전용: 복제 config의 SimTuning.CombatFlatConvertRate만 ON으로 덮어쓴다(struct라 box→set→unbox).
    private static void SetFlatRate(GameConfigSO cfg, bool on)
    {
        FieldInfo simField = typeof(GameConfigSO).GetField("sim", BindingFlags.Instance | BindingFlags.NonPublic);
        FieldInfo flatField = typeof(SimTuning).GetField("CombatFlatConvertRate");
        if (simField == null || flatField == null)
        {
            throw new InvalidOperationException("GameConfigSO.sim 또는 SimTuning.CombatFlatConvertRate 필드를 리플렉션으로 찾지 못했습니다.");
        }

        object boxed = simField.GetValue(cfg);
        flatField.SetValue(boxed, on);
        simField.SetValue(cfg, boxed);
    }

    private static int[] ParseInts(string csv)
    {
        string[] parts = csv.Split(',');
        var list = new List<int>();
        for (int i = 0; i < parts.Length; i++)
        {
            if (int.TryParse(parts[i].Trim(), out int v) && v > 0) list.Add(v);
        }

        return list.ToArray();
    }
}
#endif

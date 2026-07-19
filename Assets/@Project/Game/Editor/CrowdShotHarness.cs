#if UNITY_EDITOR
using System;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// GPU 크라우드 렌더러 시각 검증용 스크린샷 harness다(Editor 전용, 런타임 빌드 미포함).
/// GameScene을 edit 모드로 열어 city/Ground/카메라/라이팅을 확보한 뒤, GameConfig를 복제(원본 asset 불변)해
/// neutralCount만 바꿔 CrowdRoot 프리팹을 직접 스폰하고 SimTick을 여러 번 구동해 유닛을 퍼뜨린 뒤,
/// GPU 인스턴스 경로(useGpuCrowdRenderer=true)와 기존 SMR 경로(false)를 같은 seed/frame/카메라로 RenderTexture에
/// 렌더해 PNG로 저장한다. 정상 게임 루프(GameplayRoot)는 구동하지 않으며 결정론에 영향을 주지 않는다.
///
/// 배치 실행 예(그래픽 필요, -nographics 금지):
///   Unity -batchmode -projectPath &lt;proj&gt; -quit \
///     -executeMethod CrowdShotHarness.RunFromBatch -shotOut &lt;dir&gt; -shotNeutral 700 -shotTicks 150
/// </summary>
public static class CrowdShotHarness
{
    private const string ScenePath = "Assets/@Project/Scenes/GameScene.unity";
    private const float Dt = 0.02f;
    private const int Width = 1280;
    private const int Height = 720;

    public static void RunFromBatch()
    {
        string outDir = null;
        int neutral = 700;
        int ticks = 150;
        string[] args = Environment.GetCommandLineArgs();
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "-shotOut" && i + 1 < args.Length) outDir = args[i + 1];
            else if (args[i] == "-shotNeutral" && i + 1 < args.Length) int.TryParse(args[i + 1], out neutral);
            else if (args[i] == "-shotTicks" && i + 1 < args.Length) int.TryParse(args[i + 1], out ticks);
        }

        if (string.IsNullOrEmpty(outDir))
        {
            outDir = Path.Combine(Directory.GetParent(Application.dataPath).FullName, "crowd_shots");
        }

        int exit = 0;
        try
        {
            Run(outDir, neutral, ticks);
        }
        catch (Exception e)
        {
            Debug.LogError("[CrowdShotHarness] 실패: " + e);
            exit = 3;
        }

        EditorApplication.Exit(exit);
    }

    private static void Run(string outDir, int neutral, int ticks)
    {
        Directory.CreateDirectory(outDir);

        Debug.Log($"[CrowdShotHarness] 시작 out={outDir} neutral={neutral} ticks={ticks}");
        Debug.Log($"[CrowdShotHarness] GPU-CAPS graphicsDeviceType={SystemInfo.graphicsDeviceType} " +
                  $"shaderLevel={SystemInfo.graphicsShaderLevel} supportsComputeShaders={SystemInfo.supportsComputeShaders} " +
                  $"maxTexSize={SystemInfo.maxTextureSize}");

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
            throw new InvalidOperationException(
                $"config({baseConfig}) / cityRoot({cityRoot}) / mainCamera({cam}) 참조를 읽지 못했습니다.");
        }

        cam.gameObject.SetActive(true);
        cam.enabled = true;
        cam.nearClipPlane = 0.1f;
        cam.farClipPlane = 3000f;
        cam.fieldOfView = 50f;

        FieldInfo neutralCountField = typeof(GameConfigSO).GetField(
            "neutralCount", BindingFlags.Instance | BindingFlags.NonPublic);
        FieldInfo gpuFlagField = typeof(GameConfigSO).GetField(
            "useGpuCrowdRenderer", BindingFlags.Instance | BindingFlags.NonPublic);
        if (neutralCountField == null || gpuFlagField == null)
        {
            throw new InvalidOperationException("리플렉션 필드(neutralCount / useGpuCrowdRenderer)를 찾지 못했습니다.");
        }

        CrowdRoot crowdPrefab = ResourceLoader.LoadPrefab<CrowdRoot>();

        // 카메라 프레이밍은 walkable region에서 결정적으로 계산하므로 두 조건이 동일하다.
        // 첫(GPU) 크라우드에서 실제 유닛 bounds를 구해 한 번만 계산하고 모든 샷에 재사용한다.
        Vector3 center = Vector3.zero;
        float radius = 40f;
        bool framed = false;

        // ---- 조건 1: GPU-ON (VAT 인스턴스 경로) ----
        {
            GameConfigSO cfg = UnityEngine.Object.Instantiate(baseConfig);
            cfg.name = baseConfig.name + "_gpu";
            neutralCountField.SetValue(cfg, neutral);

            CrowdRoot crowd = UnityEngine.Object.Instantiate(crowdPrefab);
            crowd.gameObject.name = "ShotCrowdRoot_GPU";
            gpuFlagField.SetValue(cfg, true); // in-memory only (복제 config, 원본 asset 불변).

            try
            {
                crowd.Initialize(cfg, cityRoot, cfg.NeutralCount);
                crowd.SpawnInitial(); // 여기서 InitGpuRendererIfEnabled가 GPU 경로를 초기화한다.

                CrowdRenderer renderer = crowd.GetComponentInChildren<CrowdRenderer>(true);
                bool gpuActive = renderer != null && renderer.IsActive;
                Debug.Log($"[CrowdShotHarness] GPU-ON: CrowdRenderer.IsActive={gpuActive} agents={crowd.OracleAgentCount} " +
                          (gpuActive
                              ? "(GPU 인스턴스 경로 활성, SMR 비활성화됨)"
                              : "(GPU 게이트 실패 → SMR 폴백 렌더; gpu_on.png는 SMR과 동일할 것)"));

                DriveSim(crowd, ticks);

                // 프레이밍 전에 _visualRender를 한 번 채운다: GPU+SDF 경로에서 팔로워/중립 transform이 동결되므로(DriveSim은 RenderInterpolate를 부르지 않음)
                // ComputeCrowdBounds가 stale transform 대신 최신 네이티브 렌더 위치로 프레임하게 한다.
                crowd.RenderInterpolate(1f);
                Bounds b = ComputeCrowdBounds(crowd);
                center = b.center;
                radius = Mathf.Max(8f, Mathf.Max(b.extents.x, b.extents.z));
                framed = true;
                Debug.Log($"[CrowdShotHarness] 프레이밍 center={center} radius={radius:F1}");

                // 메인 3/4 뷰 + 상단 뷰 + 클로즈업.
                Shot(cam, crowd, center, radius, 30f, 35f, 2.05f, 0.15f, Path.Combine(outDir, "gpu_on.png"));
                Shot(cam, crowd, center, radius, 68f, 20f, 1.75f, 0.0f, Path.Combine(outDir, "gpu_on_top.png"));
                Shot(cam, crowd, center, radius, 11f, 28f, 0.42f, 0.05f, Path.Combine(outDir, "gpu_on_closeup.png"));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(crowd.gameObject);
                UnityEngine.Object.DestroyImmediate(cfg);
            }
        }

        // ---- 조건 2: SMR-OFF (기존 SkinnedMeshRenderer 레퍼런스) ----
        {
            GameConfigSO cfg = UnityEngine.Object.Instantiate(baseConfig);
            cfg.name = baseConfig.name + "_smr";
            neutralCountField.SetValue(cfg, neutral);

            CrowdRoot crowd = UnityEngine.Object.Instantiate(crowdPrefab);
            crowd.gameObject.name = "ShotCrowdRoot_SMR";
            gpuFlagField.SetValue(cfg, false); // in-memory only: SMR 경로 강제.

            try
            {
                crowd.Initialize(cfg, cityRoot, cfg.NeutralCount);
                crowd.SpawnInitial();
                Debug.Log($"[CrowdShotHarness] SMR-OFF: agents={crowd.OracleAgentCount} (SkinnedMeshRenderer 경로)");

                DriveSim(crowd, ticks);
                TickAnimators(crowd); // edit 모드에서 Animator를 수동 평가해 걷기 포즈를 만든다(레퍼런스 품질).

                if (!framed)
                {
                    Bounds b = ComputeCrowdBounds(crowd);
                    center = b.center;
                    radius = Mathf.Max(8f, Mathf.Max(b.extents.x, b.extents.z));
                }

                Shot(cam, crowd, center, radius, 30f, 35f, 2.05f, 0.15f, Path.Combine(outDir, "smr_off.png"));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(crowd.gameObject);
                UnityEngine.Object.DestroyImmediate(cfg);
            }
        }

        Debug.Log($"[CrowdShotHarness] 완료. PNG 저장 위치: {outDir}");
    }

    // Playing 상태로 전환 후 고정 step으로 SimTick을 구동한다. 플레이어에 heading을 줘 크라우드가 이동/영입하며 퍼지게 한다.
    private static void DriveSim(CrowdRoot crowd, int ticks)
    {
        crowd.OnMatchStateChanged(MatchState.Playing);
        crowd.SetPlayerHeading(new Vector2(0.85f, 0.53f).normalized, true);
        Physics.SyncTransforms();
        for (int i = 0; i < ticks; i++)
        {
            // 중간에 heading을 한 번 틀어 크라우드가 직선으로만 뭉치지 않고 곡선으로 퍼지게 한다.
            if (i == ticks / 2)
            {
                crowd.SetPlayerHeading(new Vector2(-0.4f, 0.9f).normalized, true);
            }

            crowd.SimTick(Dt);
        }
    }

    // edit 모드에서는 Animator가 자동 갱신되지 않으므로, SMR 레퍼런스의 걷기 포즈를 위해 각 Animator를 수동 평가한다.
    // 실패해도(바인딩 미비 등) 정적 바인드 포즈로 폴백하며 레퍼런스로는 여전히 유효하다.
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
                    a.Update(1.3f); // 걷기 루프(~0.7s) 안으로 진입시킨 mid-stride 포즈.
                }
            }
        }
        catch (Exception e)
        {
            Debug.LogWarning("[CrowdShotHarness] Animator 수동 평가 실패(정적 포즈로 폴백): " + e.Message);
        }
    }

    // 크라우드 XZ bounds를 구한다(카메라 프레이밍용). GPU+SDF 경로에서 팔로워/중립 transform이 동결되므로
    // 네이티브 렌더 위치 기반 bounds(RenderInterpolate가 채운 _visualRender)를 우선 쓰고, 미가용(미스폰/렌더 이전)이면
    // 기존 transform 기반 계산으로 폴백한다(groundY 근처 얇은 슬래브).
    private static Bounds ComputeCrowdBounds(CrowdRoot crowd)
    {
        if (crowd.TryGetCrowdRenderBounds(out Bounds nativeBounds))
        {
            return nativeBounds;
        }

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

    // 카메라를 (center, radius) 기준 구면 좌표로 배치하고 GPU 즉시 draw를 발행한 뒤 RT로 렌더해 PNG로 저장한다.
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

        // GPU 인스턴스 draw는 프레임 단위 즉시 모드이므로 렌더 직전에 매번 다시 발행한다(SMR 경로면 no-op).
        crowd.RenderInterpolate(1f);

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
            cam.Render(); // 폴백(SRP에서 경고할 수 있음).
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

        Debug.Log($"[CrowdShotHarness] 저장: {path} (pitch={pitchDeg} yaw={yawDeg} dist={dist:F1})");
    }
}
#endif

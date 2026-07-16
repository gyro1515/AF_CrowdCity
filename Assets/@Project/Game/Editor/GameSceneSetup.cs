using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using TMPro;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

/// <summary>
/// GameScene을 zero-manual-step 상태로 수렴시키는 editor setup이다.
/// FBX clip import, AnimatorController, 팀 material, GameConfig asset, 씬 오브젝트 배선을
/// 모두 load-or-create + converge 방식으로 적용하며 실제로 바뀐 항목이 있을 때만 저장한다.
/// 기대하는 씬 오브젝트가 없으면 조용히 넘어가지 않고 예외를 던진다.
/// </summary>
public static class GameSceneSetup
{
    private sealed class ScenePreflight
    {
        internal GameObject GameArea;
        internal Transform HumanWrapper;
        internal Transform HumanBase;
        internal Transform City;
        internal Camera MainCamera;
        internal CityBuildingsGenerator.ScenePlan Buildings;
        internal GameSceneController Controller;
    }

    private sealed class SceneFileTransaction : IDisposable
    {
        private readonly byte[] _sceneBytes;
        private readonly byte[] _metaBytes;
        private bool _completed;

        internal SceneFileTransaction()
        {
            string scenePath = ToAbsolutePath(ScenePath);
            string metaPath = scenePath + ".meta";
            _sceneBytes = File.ReadAllBytes(scenePath);
            _metaBytes = File.Exists(metaPath) ? File.ReadAllBytes(metaPath) : null;
        }

        internal void Commit()
        {
            _completed = true;
        }

        internal void RestoreAndReload()
        {
            if (_completed)
            {
                return;
            }

            string scenePath = ToAbsolutePath(ScenePath);
            string metaPath = scenePath + ".meta";
            File.WriteAllBytes(scenePath, _sceneBytes);
            if (_metaBytes == null)
            {
                if (File.Exists(metaPath))
                {
                    File.Delete(metaPath);
                }
            }
            else
            {
                File.WriteAllBytes(metaPath, _metaBytes);
            }

            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
            EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            _completed = true;
        }

        public void Dispose()
        {
            RestoreAndReload();
        }
    }

    internal static Action<GameSceneController> AfterLocalSceneSaveForTests;

    internal static void ApplyCityBuildingsForTests()
    {
        ApplyCityBuildings();
    }

    internal static GameSceneController ConvergeControllerAfterCityPreflightForTests()
    {
        Scene scene = SceneManager.GetActiveScene();
        if (scene.path != ScenePath)
        {
            throw new InvalidOperationException($"[GameSceneSetup] test에는 '{ScenePath}'가 필요합니다.");
        }

        CityBuildingsGenerator.GenerationPlan plan = CityBuildingsGenerator.CreateValidatedPlan();
        ScenePreflight preflight = CreateScenePreflight(scene, false, false);
        CityBuildingsGenerator.GeneratedAssets assets = CityBuildingsGenerator.LoadFullyConvergedAssets(plan);
        CityBuildingsGenerator.ValidateFullyConvergedScene(preflight.Buildings, assets);

        GameConfigSO config = AssetDatabase.LoadAssetAtPath<GameConfigSO>(ConfigPath);
        if (config == null)
        {
            throw new InvalidOperationException("[GameSceneSetup] controller converge test asset이 없습니다.");
        }

        List<string> changed = new List<string>();
        List<string> unchanged = new List<string>();
        ConvergeSceneController(
            scene,
            config,
            preflight.MainCamera,
            preflight.City,
            changed,
            unchanged);

        GameObject controllerGo = FindSceneRoot(scene, ControllerGoName);
        return controllerGo != null ? controllerGo.GetComponent<GameSceneController>() : null;
    }

    internal static bool LocalCityPreflightFindsControllerForTests()
    {
        Scene scene = SceneManager.GetActiveScene();
        CityBuildingsGenerator.GenerationPlan plan = CityBuildingsGenerator.CreateValidatedPlan();
        CityBuildingsGenerator.PreflightConvergence(plan);
        ScenePreflight preflight = CreateScenePreflight(scene, false, false);
        PreflightCityColliderTargets(preflight.City);
        return preflight.Controller != null;
    }

    private const string ScenePath = "Assets/@Project/Scenes/GameScene.unity";
    private const string FbxPath = "Assets/@Project/Human/Externals/Human_Base.fbx";
    private const string AnimationsFolder = "Assets/@Project/Human/Animations";
    private const string ControllerPath = AnimationsFolder + "/HumanWalk.controller";
    private const string MaterialsFolder = "Assets/@Project/Human/Materials";
    // Human prefab은 CrowdRoot가 직렬화 필드(_humanPrefab)로 소유한다. 경로가 일치해야 한다.
    private const string HumanPrefabFolder = "Assets/@Project/Human/Prefabs";
    private const string HumanPrefabPath = HumanPrefabFolder + "/Human.prefab";
    // 구 경로(Resources 안). baking 시 신 경로(Prefabs)로 이동하고 잔존 시 정리한다(중복 로드 키/유령 자산 방지).
    private const string LegacyResourcesHumanFolder = "Assets/@Project/Human/Resources/Human";
    private const string LegacyHumanPrefabPath = LegacyResourcesHumanFolder + "/Human.prefab";
    private const string GameFolder = "Assets/@Project/Game";
    private const string ConfigPath = GameFolder + "/GameConfig.asset";
    private const string HudFolder = "Assets/@Project/Hud";
    private const string CrowdCountTextStylePath = HudFolder + "/CrowdCountTextStyle.asset";
    // 정적 도시 벽 SDF의 원본 asset. CrowdRoot 프리팹의 _wallSdfAsset 직렬화 필드가 이 정본을 가리킨다(Resources 사본 없음).
    private const string WallSdfAssetPath = "Assets/@Project/City/Generated/WallSdf.asset";
    private const string WalkName = "HumanWalk";
    private const string UrpLitShaderName = "Universal Render Pipeline/Lit";
    private const string BaseColorProperty = "_BaseColor";
    // feature root 프리팹(로직 전용: 빈 GameObject + 컴포넌트 1개). 런타임이 ResourceLoader.LoadPrefab/LoadUI(클래스 이름)로 소유한다.
    // Prefabs 카테고리(GameplayRoot/InputRoot/CameraRoot/CrowdRoot)는 각 피처의 Resources/Prefabs 하위에,
    // UI 카테고리(HudRoot)는 Resources/UI 하위에 <클래스이름>.prefab로 저작돼 병합 로드 키 "Prefabs/<클래스이름>"/"UI/HudRoot"가 된다.
    // 로직 root 프리팹 경로/이름은 typeof(T).Name에서 파생하므로(ConvergeLogicRootPrefab/VerifyLogicRootPrefab) 로더와 어긋날 수 없다.
    private const string GameRootsFolder = GameFolder + "/Resources/Prefabs";
    private const string CrowdRootsFolder = "Assets/@Project/Crowd/Resources/Prefabs";
    private const string HudRootsFolder = HudFolder + "/Resources/UI";
    // HUD 동적 템플릿(CrowdLabel/RivalMarker)은 MonoBehaviour(HudRoot)가 직렬화로 소비하므로 Resources 밖 Hud/Prefabs에 저작한다.
    private const string HudPrefabsFolder = HudFolder + "/Prefabs";
    private const string CrowdRootPrefabPath = CrowdRootsFolder + "/" + nameof(CrowdRoot) + ".prefab";  // 로드 키 Prefabs/CrowdRoot
    private const string HudRootPrefabPath = HudRootsFolder + "/" + nameof(HudRoot) + ".prefab";        // 로드 키 UI/HudRoot
    private const string CameraRootPrefabPath = GameRootsFolder + "/" + nameof(CameraRoot) + ".prefab"; // 로드 키 Prefabs/CameraRoot
    private const string CrowdLabelPrefabPath = HudPrefabsFolder + "/CrowdLabel.prefab";                // HudRoot._crowdLabelPrefab
    private const string RivalMarkerPrefabPath = HudPrefabsFolder + "/RivalMarker.prefab";              // HudRoot._rivalMarkerPrefab

    // ---- HUD uGUI 프리팹 저작 스펙 (pinned) ----
    // HudRoot.cs의 원래 런타임 BuildCanvas/BuildLeaderboard/CreateLabel/CreateRivalMarker가 쓰던 레이아웃 상수를 그대로 옮긴 것이다.
    // 이 값들이 어긋나면 프리팹 배치가 코드 시절과 달라지므로 GameSceneValidator의 HUD 구조 검사와 함께 시각 1:1을 지킨다.
    private const string HudFontAssetPath = "Assets/TextMesh Pro/Resources/Fonts & Materials/LiberationSans SDF - Fallback.asset"; // HudRoot._fontAsset 배선 대상 벤더 폰트.
    private static readonly Vector2 HudReferenceResolution = new Vector2(1080f, 1920f);
    private const float HudCanvasMatch = 0.5f;
    private const float HudRowWidth = 260f;
    private const float HudRowStride = 70f;
    private const float HudRowHeight = 60f;
    private const float HudSwatchSize = 44f;
    private const int HudLabelFontSize = 64;
    private static readonly Vector2 HudLabelContainerSize = new Vector2(240f, 160f);
    private const float HudMarkerCountInset = 58f;
    private static readonly Vector2 HudMarkerSize = new Vector2(112f, 112f);
    private static readonly Vector2 HudMarkerArrowSize = new Vector2(76f, 76f);
    private static readonly Vector2 HudMarkerCountSize = new Vector2(112f, 54f);
    private static readonly Color HudDimColor = new Color(0f, 0f, 0f, 0.72f);
    private const int HudMaxTeams = 4;

    private const string GameAreaName = "GameArea";
    private const string HumanWrapperName = "Human";
    private const string HumanBaseName = "Human_Base";
    private const string CityName = "City";
    private const string MainCameraName = "Main Camera";
    private const string ControllerGoName = "GameSceneController";

    // [0]=player, [1..3]=rivals, [4]=neutral — GameConfigSO.TeamMaterials 인덱스와 동일하다.
    private static readonly string[] TeamMaterialFileNames =
    {
        "Team_Player.mat",
        "Team_RivalA.mat",
        "Team_RivalB.mat",
        "Team_RivalC.mat",
        "Team_Neutral.mat",
    };

    // 직접 MeshCollider를 가져야 하는 결합 City 자식. Buildings는 generator가 자식 37개의 collider를 소유한다.
    private static readonly string[] ColliderChildNames =
    {
        "StreetProps",
        "Vehicles",
        "Parks",
    };

    /// <summary>
    /// 열려 있는 GameScene과 관련 asset을 목표 상태로 수렴시킨다.
    /// 이미 목표 상태인 항목은 건드리지 않고, 무엇을 바꿨고 무엇이 이미 일치했는지 요약을 남긴다.
    /// </summary>
    /// <exception cref="InvalidOperationException">GameScene이 활성 씬이 아니거나 기대하는 asset/씬 오브젝트가 없으면 발생한다.</exception>
    [MenuItem("AF/CrowdCity/Setup Game Scene")]
    public static void Apply()
    {
        Scene scene = SceneManager.GetActiveScene();
        if (scene.path != ScenePath)
        {
            throw new InvalidOperationException(
                $"[GameSceneSetup] '{ScenePath}'가 열려 있어야 합니다. 현재 활성 씬: '{scene.path}'");
        }

        // Full Setup은 City를 절대 변경하지 않는다. City가 이미 완전 수렴했는지 먼저 읽기 전용 검사한다.
        CityBuildingsGenerator.GenerationPlan buildingPlan;
        CityBuildingsGenerator.GeneratedAssets buildingAssets;
        ScenePreflight preflight;
        try
        {
            buildingPlan = CityBuildingsGenerator.CreateValidatedPlan();
            preflight = CreateScenePreflight(scene, false, false);
            buildingAssets = CityBuildingsGenerator.LoadFullyConvergedAssets(buildingPlan);
            CityBuildingsGenerator.ValidateFullyConvergedScene(preflight.Buildings, buildingAssets);
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException(
                "[GameSceneSetup] City 건물 생성 상태가 완전히 수렴하지 않았습니다. " +
                "다른 asset을 수정하기 전에 'AF/CrowdCity/Setup City Buildings'를 먼저 실행하세요.",
                exception);
        }

        preflight = CreateScenePreflight(scene, false, true);
        PreflightHumanAssets();

        List<string> changed = new List<string>(16);
        List<string> unchanged = new List<string>(16);

        // 1. FBX take clip -> HumanWalk(loop) import 설정.
        ConvergeFbxClipImport(changed, unchanged);

        // 2. clip 재조회(fileID는 rename 시 바뀌므로 절대 캐시하지 않는다) + controller 수렴.
        AnimationClip walkClip = FetchWalkClip();
        AnimatorController animatorController = ConvergeAnimatorController(walkClip, changed, unchanged);

        // 5(선행). material 색상의 원본이 되는 config를 먼저 확보한다.
        GameConfigSO config = LoadOrCreateConfig(changed, unchanged);
        // CrowdCountTextStyle.asset은 HudRoot 프리팹 배선(ConvergeFeatureRootPrefabs) 이전에 존재해야 하므로 여기서 보장만 한다.
        LoadOrCreateCrowdCountTextStyle(changed, unchanged);

        // 3. FBX 원본 material 검증 + 팀 material 5종 수렴.
        Material[] teamMaterials = ConvergeTeamMaterials(config, changed, unchanged);

        // 5(후행). config에 팀 material 배선.
        WireConfigMaterials(config, teamMaterials, changed, unchanged);

        // 6. feature root 프리팹(GameplayRoot/InputRoot/CameraRoot/CrowdRoot/HudRoot) 저작/수렴.
        //    로직 전용 asset 생성이라 씬을 변경하지 않으므로 씬 Undo 그룹 밖에서 처리한다.
        ConvergeFeatureRootPrefabs(changed, unchanged);

        int undoGroup = BeginSceneUndo("Setup CrowdCity Game Scene");
        try
        {
            bool sceneChanged = false;
            sceneChanged |= ConvergeHumanAnimator(preflight.HumanBase, animatorController, changed, unchanged);

            // 4b. 완전히 구성된 씬 템플릿을 Human/Prefabs 하위 prefab asset으로 저장하고(Human.cs/CC baking 포함) 씬 템플릿을 비활성화한다.
            ConvergeHumanPrefab(preflight.HumanWrapper, ref sceneChanged, changed);

            sceneChanged |= ConvergeSceneController(
                scene,
                config,
                preflight.MainCamera,
                preflight.City,
                changed,
                unchanged);

            SaveDirtyAssets(animatorController, config, teamMaterials);
            SaveSceneIfChanged(scene, sceneChanged);
            Undo.CollapseUndoOperations(undoGroup);
        }
        catch
        {
            Undo.RevertAllDownToGroup(undoGroup);
            throw;
        }

        Debug.Log(BuildSummary(changed, unchanged));
    }

    /// <summary>
    /// 기존 Human/Config setup을 다시 저장하지 않고 City 건물 생성 결과와 가림용 material 배선만 수렴시킨다.
    /// </summary>
    [MenuItem("AF/CrowdCity/Setup City Buildings")]
    private static void ApplyCityBuildings()
    {
        Scene scene = SceneManager.GetActiveScene();
        if (SceneManager.sceneCount != 1 || scene.path != ScenePath)
        {
            throw new InvalidOperationException(
                $"[GameSceneSetup] Local City Setup은 additive scene 없이 '{ScenePath}' 하나만 열려 있어야 합니다. " +
                $"현재 scene 수={SceneManager.sceneCount}, 활성 씬='{scene.path}'");
        }

        if (scene.isDirty)
        {
            throw new InvalidOperationException(
                "[GameSceneSetup] City 건물 Setup 전에 GameScene의 기존 변경을 먼저 저장하세요.");
        }

        CityBuildingsGenerator.GenerationPlan buildingPlan = CityBuildingsGenerator.CreateValidatedPlan();
        CityBuildingsGenerator.PreflightConvergence(buildingPlan);
        ScenePreflight preflight = CreateScenePreflight(scene, false, false);
        PreflightCityColliderTargets(preflight.City);
        List<string> changed = new List<string>(16);
        List<string> unchanged = new List<string>(16);

        CityBuildingsGenerator.GeneratedAssetsTransaction assetTransaction = null;
        SceneFileTransaction sceneTransaction = new SceneFileTransaction();
        int undoGroup = BeginSceneUndo("Setup City Buildings");
        try
        {
            assetTransaction = CityBuildingsGenerator.BeginConvergeAssets(buildingPlan, changed, unchanged);
            CityBuildingsGenerator.GeneratedAssets buildingAssets = assetTransaction.Assets;
            bool sceneChanged = CityBuildingsGenerator.ConvergeScene(
                preflight.Buildings, buildingAssets, changed, unchanged);
            sceneChanged |= ConvergeCityColliders(preflight.City, changed, unchanged);

            // 건물 가림 material은 이제 CameraRoot 프리팹에 직렬화 저작된다(GSC 배선 아님).
            // 프리팹 배선은 ConvergeFeatureRootPrefabs(Full Setup / Bake Feature Root Prefabs)가 담당하므로 여기서는 하지 않는다.

            SaveSceneIfChanged(scene, sceneChanged);
            if (AfterLocalSceneSaveForTests != null)
            {
                AfterLocalSceneSaveForTests(preflight.Controller);
            }

            Undo.CollapseUndoOperations(undoGroup);
            assetTransaction.Commit();
            sceneTransaction.Commit();
        }
        catch (Exception failure)
        {
            List<Exception> rollbackFailures = new List<Exception>();
            TryRollback(() => Undo.RevertAllDownToGroup(undoGroup), rollbackFailures);
            if (assetTransaction != null)
            {
                TryRollback(assetTransaction.Dispose, rollbackFailures);
            }
            TryRollback(sceneTransaction.RestoreAndReload, rollbackFailures);

            if (rollbackFailures.Count > 0)
            {
                rollbackFailures.Insert(0, failure);
                throw new AggregateException("[GameSceneSetup] City Setup 및 rollback 중 예외가 발생했습니다.", rollbackFailures);
            }

            throw;
        }

        Debug.Log(BuildSummary(changed, unchanged));
    }

    private static ScenePreflight CreateScenePreflight(
        Scene scene, bool requireExistingController, bool validateHumanSetup)
    {
        GameObject gameArea = RequireSceneRoot(scene, GameAreaName);
        Transform city = RequireChild(gameArea.transform, CityName);
        ScenePreflight result = new ScenePreflight
        {
            GameArea = gameArea,
            City = city,
            MainCamera = RequireMainCamera(scene),
            Buildings = CityBuildingsGenerator.CreateValidatedScenePlan(city),
        };

        if (validateHumanSetup)
        {
            result.HumanWrapper = RequireChild(gameArea.transform, HumanWrapperName);
            result.HumanBase = RequireChild(result.HumanWrapper, HumanBaseName);
            PreflightCityColliderTargets(city);
        }

        GameObject controllerGo = FindSceneRoot(scene, ControllerGoName);
        GameSceneController controller = controllerGo != null
            ? controllerGo.GetComponent<GameSceneController>()
            : null;
        if (requireExistingController && controller == null)
        {
            throw new InvalidOperationException(
                $"[GameSceneSetup] 씬 root '{ControllerGoName}'와 GameSceneController가 필요합니다.");
        }

        if (controller != null)
        {
            ValidateControllerSerializedFields(controller);
            result.Controller = controller;
        }
        else
        {
            ValidateControllerFieldDeclarations();
        }

        return result;
    }

    private static void PreflightHumanAssets()
    {
        ModelImporter importer = AssetImporter.GetAtPath(FbxPath) as ModelImporter;
        if (importer == null)
        {
            throw new InvalidOperationException($"[GameSceneSetup] ModelImporter를 찾지 못했습니다: {FbxPath}");
        }

        ModelImporterClipAnimation[] current = importer.clipAnimations;
        if ((current == null || current.Length == 0 || current[0].name != WalkName || !current[0].loopTime) &&
            (importer.defaultClipAnimations == null || importer.defaultClipAnimations.Length == 0))
        {
            throw new InvalidOperationException($"[GameSceneSetup] FBX에 기본 take clip이 없습니다: {FbxPath}");
        }

        ValidateAssetPathType<AnimatorController>(ControllerPath);
        AnimatorController animatorController = AssetDatabase.LoadAssetAtPath<AnimatorController>(ControllerPath);
        if (animatorController != null && animatorController.layers.Length > 0 &&
            animatorController.layers[0].stateMachine == null)
        {
            throw new InvalidOperationException(
                $"[GameSceneSetup] controller layer 0에 state machine이 없습니다(asset 손상 의심): {ControllerPath}");
        }

        ValidateAssetPathType<GameConfigSO>(ConfigPath);
        ValidateAssetPathType<TMPTextStyleSO>(CrowdCountTextStylePath);
        ValidateAssetPathType<GameObject>(HumanPrefabPath);
        for (int i = 0; i < TeamMaterialFileNames.Length; i++)
        {
            ValidateAssetPathType<Material>(MaterialsFolder + "/" + TeamMaterialFileNames[i]);
        }

        LoadSourceMaterial();

        GameConfigSO config = AssetDatabase.LoadAssetAtPath<GameConfigSO>(ConfigPath);
        bool destroyConfig = false;
        if (config == null)
        {
            config = ScriptableObject.CreateInstance<GameConfigSO>();
            config.hideFlags = HideFlags.HideAndDontSave;
            destroyConfig = true;
        }

        try
        {
            if (config.TeamColors == null || config.TeamColors.Count < 4)
            {
                throw new InvalidOperationException("[GameSceneSetup] GameConfig.TeamColors 길이가 4가 아닙니다.");
            }

            SerializedObject serialized = new SerializedObject(config);
            if (serialized.FindProperty("teamMaterials") == null)
            {
                throw new InvalidOperationException(
                    "[GameSceneSetup] GameConfig에서 직렬화 필드 'teamMaterials'을(를) 찾지 못했습니다.");
            }
        }
        finally
        {
            if (destroyConfig)
            {
                UnityEngine.Object.DestroyImmediate(config);
            }
        }
    }

    private static void PreflightCityColliderTargets(Transform city)
    {
        for (int i = 0; i < ColliderChildNames.Length; i++)
        {
            Transform child = RequireChild(city, ColliderChildNames[i]);
            MeshFilter filter = child.GetComponent<MeshFilter>();
            if (filter == null || filter.sharedMesh == null)
            {
                throw new InvalidOperationException(
                    $"[GameSceneSetup] '{CityName}/{ColliderChildNames[i]}'에 sharedMesh를 가진 MeshFilter가 없습니다.");
            }
        }
    }

    private static void ValidateControllerSerializedFields(GameSceneController controller)
    {
        string[] fieldNames =
        {
            "config", "mainCamera", "cityRoot",
        };
        SerializedObject serialized = new SerializedObject(controller);
        for (int i = 0; i < fieldNames.Length; i++)
        {
            if (serialized.FindProperty(fieldNames[i]) == null)
            {
                throw new InvalidOperationException(
                    $"[GameSceneSetup] GameSceneController에서 직렬화 필드 '{fieldNames[i]}'을(를) 찾지 못했습니다.");
            }
        }
    }

    private static void ValidateControllerFieldDeclarations()
    {
        string[] fieldNames =
        {
            "config", "mainCamera", "cityRoot",
        };
        Type controllerType = typeof(GameSceneController);
        const System.Reflection.BindingFlags flags =
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        for (int i = 0; i < fieldNames.Length; i++)
        {
            if (controllerType.GetField(fieldNames[i], flags) == null)
            {
                throw new InvalidOperationException(
                    $"[GameSceneSetup] GameSceneController field 선언 '{fieldNames[i]}'을(를) 찾지 못했습니다.");
            }
        }
    }

    private static void ValidateAssetPathType<T>(string path)
        where T : UnityEngine.Object
    {
        UnityEngine.Object main = AssetDatabase.LoadMainAssetAtPath(path);
        if (main != null && !(main is T))
        {
            throw new InvalidOperationException(
                $"[GameSceneSetup] asset 경로에 예상 밖 type이 있습니다: {path} ({main.GetType().Name})");
        }
    }

    private static int BeginSceneUndo(string name)
    {
        Undo.IncrementCurrentGroup();
        int group = Undo.GetCurrentGroup();
        Undo.SetCurrentGroupName(name);
        return group;
    }

    private static void TryRollback(Action rollback, List<Exception> failures)
    {
        try
        {
            rollback();
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }
    }

    private static void SaveDirtyAssets(
        AnimatorController animatorController, GameConfigSO config, Material[] teamMaterials)
    {
        AssetDatabase.SaveAssetIfDirty(animatorController);
        AssetDatabase.SaveAssetIfDirty(config);
        for (int i = 0; i < teamMaterials.Length; i++)
        {
            AssetDatabase.SaveAssetIfDirty(teamMaterials[i]);
        }
    }

    private static void SaveSceneIfChanged(Scene scene, bool sceneChanged)
    {
        if (!sceneChanged)
        {
            return;
        }

        EditorSceneManager.MarkSceneDirty(scene);
        if (!EditorSceneManager.SaveScene(scene))
        {
            throw new InvalidOperationException($"[GameSceneSetup] 씬 저장에 실패했습니다: {ScenePath}");
        }
    }

    /// <summary>
    /// batch mode 진입점이다. <see cref="Apply"/>를 실행하고 성공하면 exit code 0,
    /// 예외가 발생하면 로그를 남기고 exit code 1로 editor를 종료한다.
    /// </summary>
    public static void ApplyBatch()
    {
        try
        {
            Apply();
            EditorApplication.Exit(0);
        }
        catch (Exception exception)
        {
            Debug.LogException(exception);
            EditorApplication.Exit(1);
        }
    }

    // ---- 1. FBX clip import ----

    private static void ConvergeFbxClipImport(List<string> changed, List<string> unchanged)
    {
        ModelImporter importer = AssetImporter.GetAtPath(FbxPath) as ModelImporter;
        if (importer == null)
        {
            throw new InvalidOperationException($"[GameSceneSetup] ModelImporter를 찾지 못했습니다: {FbxPath}");
        }

        ModelImporterClipAnimation[] current = importer.clipAnimations;
        if (current != null && current.Length > 0 && current[0].name == WalkName && current[0].loopTime)
        {
            unchanged.Add("FBX clip import(HumanWalk, loop)");
            return;
        }

        // take 이름과 frame 구간은 손으로 적지 않고 defaultClipAnimations에서 복사한다.
        ModelImporterClipAnimation[] defaults = importer.defaultClipAnimations;
        if (defaults == null || defaults.Length == 0)
        {
            throw new InvalidOperationException($"[GameSceneSetup] FBX에 기본 take clip이 없습니다: {FbxPath}");
        }

        defaults[0].name = WalkName;
        defaults[0].loopTime = true;
        importer.clipAnimations = defaults;
        importer.SaveAndReimport();
        changed.Add("FBX clip import -> HumanWalk(loopTime=true) 재설정");
    }

    // ---- 2. clip 재조회 + AnimatorController ----

    private static AnimationClip FetchWalkClip()
    {
        UnityEngine.Object[] representations = AssetDatabase.LoadAllAssetRepresentationsAtPath(FbxPath);
        for (int i = 0; i < representations.Length; i++)
        {
            AnimationClip clip = representations[i] as AnimationClip;
            if (clip != null && clip.name == WalkName)
            {
                return clip;
            }
        }

        throw new InvalidOperationException(
            $"[GameSceneSetup] '{WalkName}' AnimationClip을 FBX sub-asset에서 찾지 못했습니다: {FbxPath}");
    }

    private static AnimatorController ConvergeAnimatorController(
        AnimationClip walkClip, List<string> changed, List<string> unchanged)
    {
        bool changedHere = false;

        AnimatorController controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(ControllerPath);
        if (controller == null)
        {
            EnsureFolder(AnimationsFolder);
            controller = AnimatorController.CreateAnimatorControllerAtPath(ControllerPath);
            changedHere = true;
        }

        if (controller.layers.Length == 0)
        {
            controller.AddLayer("Base Layer");
            changedHere = true;
        }

        while (controller.layers.Length > 1)
        {
            controller.RemoveLayer(controller.layers.Length - 1);
            changedHere = true;
        }

        AnimatorStateMachine stateMachine = controller.layers[0].stateMachine;
        if (stateMachine == null)
        {
            throw new InvalidOperationException(
                $"[GameSceneSetup] controller layer 0에 state machine이 없습니다(asset 손상 의심): {ControllerPath}");
        }

        // HumanWalk 하나만 남기고 나머지 state는 제거한다(append가 아니라 converge).
        AnimatorState walkState = null;
        ChildAnimatorState[] states = stateMachine.states;
        for (int i = 0; i < states.Length; i++)
        {
            AnimatorState state = states[i].state;
            if (walkState == null && state.name == WalkName)
            {
                walkState = state;
                continue;
            }

            stateMachine.RemoveState(state);
            changedHere = true;
        }

        if (walkState == null)
        {
            walkState = stateMachine.AddState(WalkName);
            changedHere = true;
        }

        if (walkState.motion != walkClip)
        {
            walkState.motion = walkClip;
            changedHere = true;
        }

        if (stateMachine.defaultState != walkState)
        {
            stateMachine.defaultState = walkState;
            changedHere = true;
        }

        if (changedHere)
        {
            EditorUtility.SetDirty(controller);
            changed.Add("HumanWalk.controller(단일 layer/state로 수렴)");
        }
        else
        {
            unchanged.Add("HumanWalk.controller");
        }

        return controller;
    }

    // ---- 3. 팀 material ----

    private static Material LoadSourceMaterial()
    {
        GameObject fbxRoot = AssetDatabase.LoadAssetAtPath<GameObject>(FbxPath);
        if (fbxRoot == null)
        {
            throw new InvalidOperationException($"[GameSceneSetup] FBX main asset을 로드하지 못했습니다: {FbxPath}");
        }

        SkinnedMeshRenderer renderer = fbxRoot.GetComponentInChildren<SkinnedMeshRenderer>(true);
        if (renderer == null)
        {
            throw new InvalidOperationException($"[GameSceneSetup] FBX에서 SkinnedMeshRenderer를 찾지 못했습니다: {FbxPath}");
        }

        Material source = renderer.sharedMaterial;
        if (source == null)
        {
            throw new InvalidOperationException($"[GameSceneSetup] FBX SkinnedMeshRenderer에 material이 없습니다: {FbxPath}");
        }

        string shaderName = source.shader == null ? "null" : source.shader.name;
        if (shaderName != UrpLitShaderName)
        {
            throw new InvalidOperationException(
                $"[GameSceneSetup] FBX material shader가 '{UrpLitShaderName}'이 아닙니다: '{shaderName}'");
        }

        if (!source.HasProperty(BaseColorProperty))
        {
            throw new InvalidOperationException(
                $"[GameSceneSetup] FBX material에 '{BaseColorProperty}' property가 없습니다: {source.name}");
        }

        return source;
    }

    private static Material[] ConvergeTeamMaterials(GameConfigSO config, List<string> changed, List<string> unchanged)
    {
        if (config.TeamColors == null || config.TeamColors.Count < 4)
        {
            throw new InvalidOperationException("[GameSceneSetup] GameConfig.TeamColors 길이가 4가 아닙니다.");
        }

        Material source = LoadSourceMaterial();

        Color[] desiredColors =
        {
            config.TeamColors[0],
            config.TeamColors[1],
            config.TeamColors[2],
            config.TeamColors[3],
            config.NeutralColor,
        };

        EnsureFolder(MaterialsFolder);

        Material[] result = new Material[TeamMaterialFileNames.Length];
        int createdCount = 0;
        int updatedCount = 0;
        for (int i = 0; i < TeamMaterialFileNames.Length; i++)
        {
            string path = MaterialsFolder + "/" + TeamMaterialFileNames[i];
            Material material = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (material == null)
            {
                material = new Material(source);
                material.SetColor(BaseColorProperty, desiredColors[i]);
                AssetDatabase.CreateAsset(material, path);
                createdCount++;
            }
            else
            {
                bool materialChanged = false;
                if (material.shader != source.shader)
                {
                    material.shader = source.shader;
                    materialChanged = true;
                }

                if (material.GetColor(BaseColorProperty) != desiredColors[i])
                {
                    material.SetColor(BaseColorProperty, desiredColors[i]);
                    materialChanged = true;
                }

                if (materialChanged)
                {
                    EditorUtility.SetDirty(material);
                    updatedCount++;
                }
            }

            result[i] = material;
        }

        if (createdCount > 0 || updatedCount > 0)
        {
            changed.Add($"팀 material(생성 {createdCount}, 갱신 {updatedCount})");
        }
        else
        {
            unchanged.Add("팀 material 5종");
        }

        return result;
    }

    // ---- 5. GameConfig asset ----

    private static GameConfigSO LoadOrCreateConfig(List<string> changed, List<string> unchanged)
    {
        GameConfigSO config = AssetDatabase.LoadAssetAtPath<GameConfigSO>(ConfigPath);
        if (config != null)
        {
            unchanged.Add("GameConfig.asset");
            return config;
        }

        EnsureFolder(GameFolder);
        config = ScriptableObject.CreateInstance<GameConfigSO>();
        AssetDatabase.CreateAsset(config, ConfigPath);
        changed.Add("GameConfig.asset 생성(기본값)");
        return config;
    }

    private static TMPTextStyleSO LoadOrCreateCrowdCountTextStyle(
        List<string> changed, List<string> unchanged)
    {
        TMPTextStyleSO style = AssetDatabase.LoadAssetAtPath<TMPTextStyleSO>(CrowdCountTextStylePath);
        if (style != null)
        {
            unchanged.Add("CrowdCountTextStyle.asset");
            return style;
        }

        EnsureFolder(HudFolder);
        style = ScriptableObject.CreateInstance<TMPTextStyleSO>();
        AssetDatabase.CreateAsset(style, CrowdCountTextStylePath);
        changed.Add("CrowdCountTextStyle.asset 생성(기본값)");
        return style;
    }

    private static void WireConfigMaterials(
        GameConfigSO config, Material[] teamMaterials, List<string> changed, List<string> unchanged)
    {
        // TeamMaterials는 이제 read-only 프로퍼티이므로 직렬화 필드 'teamMaterials'를 SerializedObject로 배선한다.
        SerializedObject serialized = new SerializedObject(config);
        SerializedProperty property = serialized.FindProperty("teamMaterials");
        if (property == null)
        {
            throw new InvalidOperationException(
                "[GameSceneSetup] GameConfig에서 직렬화 필드 'teamMaterials'을(를) 찾지 못했습니다.");
        }

        if (property.arraySize != teamMaterials.Length)
        {
            property.arraySize = teamMaterials.Length;
        }

        for (int i = 0; i < teamMaterials.Length; i++)
        {
            property.GetArrayElementAtIndex(i).objectReferenceValue = teamMaterials[i];
        }

        if (serialized.hasModifiedProperties)
        {
            serialized.ApplyModifiedPropertiesWithoutUndo();
            changed.Add("GameConfig.TeamMaterials 배선");
        }
        else
        {
            unchanged.Add("GameConfig.TeamMaterials");
        }
    }

    // ---- 4. 씬: Animator + City collider ----

    private static bool ConvergeHumanAnimator(
        Transform humanBase, AnimatorController animatorController, List<string> changed, List<string> unchanged)
    {
        bool changedHere = false;

        // Generic curve 경로(HumanAnimation/mixamorig:…)가 풀리도록 wrapper가 아닌 FBX root 자식에 붙인다.
        Animator animator = humanBase.GetComponent<Animator>();
        if (animator == null)
        {
            animator = Undo.AddComponent<Animator>(humanBase.gameObject);
            changedHere = true;
        }

        Undo.RecordObject(animator, "Configure Human Animator");

        if (animator.runtimeAnimatorController != animatorController)
        {
            animator.runtimeAnimatorController = animatorController;
            changedHere = true;
        }

        if (animator.applyRootMotion)
        {
            animator.applyRootMotion = false;
            changedHere = true;
        }

        if (animator.cullingMode != AnimatorCullingMode.CullUpdateTransforms)
        {
            animator.cullingMode = AnimatorCullingMode.CullUpdateTransforms;
            changedHere = true;
        }

        if (changedHere)
        {
            changed.Add("씬: Human_Base Animator 구성");
        }
        else
        {
            unchanged.Add("씬: Human_Base Animator");
        }

        return changedHere;
    }

    private static bool ConvergeCityColliders(Transform city, List<string> changed, List<string> unchanged)
    {
        bool changedHere = false;

        for (int i = 0; i < ColliderChildNames.Length; i++)
        {
            Transform child = RequireChild(city, ColliderChildNames[i]);

            MeshFilter meshFilter = child.GetComponent<MeshFilter>();
            if (meshFilter == null || meshFilter.sharedMesh == null)
            {
                throw new InvalidOperationException(
                    $"[GameSceneSetup] '{CityName}/{ColliderChildNames[i]}'에 sharedMesh를 가진 MeshFilter가 없습니다.");
            }

            MeshCollider meshCollider = child.GetComponent<MeshCollider>();
            if (meshCollider == null)
            {
                meshCollider = Undo.AddComponent<MeshCollider>(child.gameObject);
                changedHere = true;
            }

            if (meshCollider.sharedMesh != meshFilter.sharedMesh)
            {
                Undo.RecordObject(meshCollider, "Configure City Collider");
                meshCollider.sharedMesh = meshFilter.sharedMesh;
                changedHere = true;
            }
        }

        // 대상 밖 자식(Ground, RoadMarks 등)에 남은 MeshCollider는 제거해 desired state로 수렴시킨다.
        for (int i = 0; i < city.childCount; i++)
        {
            Transform child = city.GetChild(i);
            if (IsColliderTarget(child.name))
            {
                continue;
            }

            MeshCollider stray = child.GetComponent<MeshCollider>();
            if (stray != null)
            {
                Undo.DestroyObjectImmediate(stray);
                changedHere = true;
            }
        }

        if (changedHere)
        {
            changed.Add("씬: City 직접 MeshCollider(StreetProps/Vehicles/Parks만)");
        }
        else
        {
            unchanged.Add("씬: City MeshCollider");
        }

        return changedHere;
    }

    private static bool IsColliderTarget(string childName)
    {
        for (int i = 0; i < ColliderChildNames.Length; i++)
        {
            if (ColliderChildNames[i] == childName)
            {
                return true;
            }
        }

        return false;
    }

    // ---- 4b. Human prefab ----

    // ---- Human prefab root 컴포넌트 baking 스펙 (pinned) ----
    // 리더/팔로워/중립 전원이 enabled CharacterController로 물리충돌을 받는다(사용자 명시 의도). 아래 값은
    // GameSceneValidator의 CC 스펙 검사값과 반드시 일치해야 한다(런타임 수리 금지 — baking + Editor 검증만).
    private const float ControllerRadius = 0.35f;
    private const float ControllerHeight = 1.8f;
    private const float ControllerCenterY = 0.9f;
    private const float ControllerSkinWidth = 0.08f;

    // 구성이 끝난 씬 템플릿(GameArea/Human)을 Human/Prefabs 하위 prefab asset으로 저장하고 씬 템플릿을 비활성화한다.
    // 저장 직전에 (a)Human 컴포넌트와 (b)pinned 스펙 CharacterController(enabled)를 template root에 baking해
    // 런타임 clone이 AddComponent 없이 곧바로 쓰도록 한다. prefab은 매번 같은 경로에 덮어써 수렴시킨다(idempotent).
    private static void ConvergeHumanPrefab(Transform humanWrapper, ref bool sceneChanged, List<string> changed)
    {
        EnsureFolder(HumanPrefabFolder);

        GameObject templateGo = humanWrapper.gameObject;

        // 런타임 AddComponent 제거의 사전조건: Human + CharacterController(pinned)를 template root에 baking한다.
        if (EnsureHumanRootComponents(templateGo, true))
        {
            sceneChanged = true;
        }

        // prefab은 active 상태로 저장한다(런타임 clone이 즉시 활성).
        bool wasActive = templateGo.activeSelf;
        if (!wasActive)
        {
            Undo.RecordObject(templateGo, "Activate Human Prefab Template");
            templateGo.SetActive(true);
        }

        GameObject prefab = PrefabUtility.SaveAsPrefabAsset(templateGo, HumanPrefabPath);
        if (prefab == null)
        {
            throw new InvalidOperationException($"[GameSceneSetup] Human prefab 저장에 실패했습니다: {HumanPrefabPath}");
        }

        // 저장 후 씬 템플릿은 런타임에서 쓰이지 않으므로 비활성화한다(런타임은 prefab을 clone).
        if (templateGo.activeSelf)
        {
            Undo.RecordObject(templateGo, "Deactivate Human Scene Template");
            templateGo.SetActive(false);
        }

        // active 상태가 실제로 바뀐 경우(최초 실행: active -> inactive)에만 씬을 dirty로 표시한다.
        if (wasActive)
        {
            sceneChanged = true;
        }

        // 구 경로(Resources 밖) 잔존 자산을 정리해 Resources 중복 로드 키/유령 자산을 막는다.
        DeleteLegacyHumanPrefabIfPresent(changed);

        changed.Add("Human.prefab 저장/갱신(Human/Prefabs) + Human.cs/CharacterController baking + 씬 템플릿 비활성화");
    }

    /// <summary>
    /// Phase 1 전용: Human.prefab을 Human/Prefabs 하위로 이전하고 root에 Human.cs + enabled CharacterController(pinned 스펙)를
    /// baking한다. City/씬/Config 수렴 없이 prefab asset만 직접 갱신하므로 batchmode에서 독립 실행할 수 있다.
    /// 구 경로 prefab이 있으면 guid를 보존해 이동한 뒤 컴포넌트를 baking하고, 구 경로 잔존물을 정리한다. idempotent.
    /// </summary>
    [MenuItem("AF/CrowdCity/Bake Human Prefab (Resources + CC and Human)")]
    public static void BakeHumanPrefab()
    {
        EnsureFolder(HumanPrefabFolder);

        // 1. 소스 확정: 신 경로에 없고 구 경로에 있으면 guid를 보존해 이동한다.
        if (AssetDatabase.LoadAssetAtPath<GameObject>(HumanPrefabPath) == null)
        {
            if (AssetDatabase.LoadAssetAtPath<GameObject>(LegacyHumanPrefabPath) == null)
            {
                throw new InvalidOperationException(
                    $"[GameSceneSetup] Human prefab을 구/신 경로 어디에서도 찾지 못했습니다: {LegacyHumanPrefabPath} / {HumanPrefabPath}");
            }

            string moveError = AssetDatabase.MoveAsset(LegacyHumanPrefabPath, HumanPrefabPath);
            if (!string.IsNullOrEmpty(moveError))
            {
                throw new InvalidOperationException($"[GameSceneSetup] Human prefab 이동 실패: {moveError}");
            }
        }

        // 2. 신 경로 prefab root에 컴포넌트를 baking한다(변경이 있을 때만 저장).
        GameObject root = PrefabUtility.LoadPrefabContents(HumanPrefabPath);
        try
        {
            if (EnsureHumanRootComponents(root, false))
            {
                PrefabUtility.SaveAsPrefabAsset(root, HumanPrefabPath);
            }
        }
        finally
        {
            PrefabUtility.UnloadPrefabContents(root);
        }

        // 3. 구 경로 잔존물 정리(중복 로드 키 방지).
        List<string> changed = new List<string>(1);
        DeleteLegacyHumanPrefabIfPresent(changed);

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();

        // 4. 결과 확인(실패 시 예외로 명확히 알린다).
        GameObject verify = AssetDatabase.LoadAssetAtPath<GameObject>(HumanPrefabPath);
        Human humanComponent = verify != null ? verify.GetComponent<Human>() : null;
        CharacterController cc = verify != null ? verify.GetComponent<CharacterController>() : null;
        if (verify == null || humanComponent == null || cc == null)
        {
            throw new InvalidOperationException(
                $"[GameSceneSetup] baking 검증 실패: prefab={verify != null}, Human={humanComponent != null}, CC={cc != null}");
        }

        Debug.Log(
            $"[BakeHumanPrefab] PASS at {HumanPrefabPath} — Human=1, CharacterController(enabled={cc.enabled}, " +
            $"radius={cc.radius}, height={cc.height}, center={cc.center}, skinWidth={cc.skinWidth}). " +
            $"legacy cleaned={changed.Count > 0}");
    }

    /// <summary>
    /// batch mode 진입점. <see cref="BakeHumanPrefab"/>를 실행하고 성공 시 exit 0, 예외 시 로그 후 exit 1로 종료한다.
    /// </summary>
    public static void BakeHumanPrefabBatch()
    {
        try
        {
            BakeHumanPrefab();
            EditorApplication.Exit(0);
        }
        catch (Exception exception)
        {
            Debug.LogException(exception);
            EditorApplication.Exit(1);
        }
    }

    // prefab/씬 템플릿 root에 Human 컴포넌트와 pinned 스펙 CharacterController(enabled)를 보장한다.
    // 이미 목표 상태면 아무것도 바꾸지 않는다(idempotent). 무언가 바꿨으면 true.
    // useUndo=true면 씬 편집으로 간주해 Undo 그룹에 등록한다(Apply 롤백과 정합). prefab contents 편집엔 false.
    private static bool EnsureHumanRootComponents(GameObject root, bool useUndo)
    {
        bool changed = false;

        if (root.GetComponent<Human>() == null)
        {
            if (useUndo)
            {
                Undo.AddComponent<Human>(root);
            }
            else
            {
                root.AddComponent<Human>();
            }

            changed = true;
        }

        CharacterController controller = root.GetComponent<CharacterController>();
        if (controller == null)
        {
            controller = useUndo
                ? Undo.AddComponent<CharacterController>(root)
                : root.AddComponent<CharacterController>();
            changed = true;
        }

        if (useUndo)
        {
            Undo.RecordObject(controller, "Configure Human CharacterController");
        }

        changed |= ConfigureController(controller);
        return changed;
    }

    // CharacterController 스펙을 pinned 값으로 수렴한다(enabled 포함). 바뀐 게 있으면 true. 저작 시점에만 호출한다.
    private static bool ConfigureController(CharacterController controller)
    {
        bool changed = false;

        if (!controller.enabled)
        {
            controller.enabled = true;
            changed = true;
        }

        if (!Mathf.Approximately(controller.radius, ControllerRadius))
        {
            controller.radius = ControllerRadius;
            changed = true;
        }

        if (!Mathf.Approximately(controller.height, ControllerHeight))
        {
            controller.height = ControllerHeight;
            changed = true;
        }

        Vector3 center = new Vector3(0f, ControllerCenterY, 0f);
        if (controller.center != center)
        {
            controller.center = center;
            changed = true;
        }

        if (!Mathf.Approximately(controller.skinWidth, ControllerSkinWidth))
        {
            controller.skinWidth = ControllerSkinWidth;
            changed = true;
        }

        return changed;
    }

    private static void DeleteLegacyHumanPrefabIfPresent(List<string> changed)
    {
        if (AssetDatabase.LoadAssetAtPath<GameObject>(LegacyHumanPrefabPath) == null)
        {
            return;
        }

        if (AssetDatabase.DeleteAsset(LegacyHumanPrefabPath))
        {
            changed.Add("구 Human.prefab(Resources/Human) 삭제");
        }
    }

    // ---- 4c. feature root 프리팹 (로직 전용: 빈 GameObject + 컴포넌트 1개) ----

    // GameplayRoot/InputRoot/CameraRoot/CrowdRoot는 로직 전용(빈 GO + 컴포넌트 1개) 프리팹으로 load-or-create 수렴한다.
    // 런타임(GameSceneController/GameplayRoot)이 ResourceLoader.LoadPrefab/LoadUI + Instantiate + Init(deps)로 조립한다.
    // HudRoot는 Canvas/CanvasScaler + 정적 uGUI 트리(타이머·순위표·시작힌트·결과오버레이)를 사전 저작해야 하므로
    // 로직 전용 수렴이 아니라 전용 저작 경로(ConvergeHudPrefabs)를 쓰고 동적 라벨/마커 템플릿 프리팹도 함께 수렴한다.
    private static void ConvergeFeatureRootPrefabs(List<string> changed, List<string> unchanged)
    {
        ConvergeLogicRootPrefab<GameplayRoot>(GameRootsFolder, changed, unchanged);
        ConvergeLogicRootPrefab<InputRoot>(GameRootsFolder, changed, unchanged);
        ConvergeLogicRootPrefab<CameraRoot>(GameRootsFolder, changed, unchanged);
        ConvergeLogicRootPrefab<CrowdRoot>(CrowdRootsFolder, changed, unchanged);
        ConvergeHudPrefabs(changed, unchanged);

        // 단일 소비자 asset을 소비 feature root 프리팹의 직렬화 필드로 배선한다(Init/직접 런타임 로드 체인 제거).
        // 배선 대상 asset/프리팹은 이 프리팹 수렴 뒤에 존재해야 하므로 위 프리팹 수렴 뒤에 실행한다.
        WireCameraRootOccludedMaterial(changed, unchanged);
        WireHudRootCrowdCountTextStyle(changed, unchanged);
        WireCrowdRootHumanPrefab(changed, unchanged);
        WireCrowdRootWallSdf(changed, unchanged);
        WireHudRootCrowdLabelPrefab(changed, unchanged);
        WireHudRootRivalMarkerPrefab(changed, unchanged);
        WireHudRootFont(changed, unchanged);
    }

    // CrowdRoot 프리팹의 _humanPrefab 직렬화 필드에 Human 프리팹(Human/Prefabs/Human.prefab)을 배선한다.
    private static void WireCrowdRootHumanPrefab(List<string> changed, List<string> unchanged)
    {
        GameObject human = AssetDatabase.LoadAssetAtPath<GameObject>(HumanPrefabPath);
        if (human == null)
        {
            throw new InvalidOperationException(
                $"[GameSceneSetup] CrowdRoot 프리팹 배선 대상 Human 프리팹이 없습니다: {HumanPrefabPath}. " +
                "먼저 'AF/CrowdCity/Bake Human Prefab'을 실행하세요.");
        }

        ConvergePrefabSerializedRef<CrowdRoot>(
            CrowdRootPrefabPath, "_humanPrefab", human, "CrowdRoot", changed, unchanged);
    }

    // CrowdRoot 프리팹의 _wallSdfAsset 직렬화 필드에 정본 WallSdf asset(City/Generated/WallSdf.asset)을 배선한다.
    private static void WireCrowdRootWallSdf(List<string> changed, List<string> unchanged)
    {
        WallSdfAsset wallSdf = AssetDatabase.LoadAssetAtPath<WallSdfAsset>(WallSdfAssetPath);
        if (wallSdf == null)
        {
            throw new InvalidOperationException(
                $"[GameSceneSetup] CrowdRoot 프리팹 배선 대상 WallSdf asset이 없습니다: {WallSdfAssetPath}. " +
                "먼저 'AF/CrowdCity/Bake Wall SDF'를 실행하세요.");
        }

        ConvergePrefabSerializedRef<CrowdRoot>(
            CrowdRootPrefabPath, "_wallSdfAsset", wallSdf, "CrowdRoot", changed, unchanged);
    }

    // HudRoot 프리팹의 _crowdLabelPrefab 직렬화 필드에 CrowdLabel 템플릿 프리팹(Hud/Prefabs)을 배선한다.
    private static void WireHudRootCrowdLabelPrefab(List<string> changed, List<string> unchanged)
    {
        GameObject crowdLabel = AssetDatabase.LoadAssetAtPath<GameObject>(CrowdLabelPrefabPath);
        if (crowdLabel == null)
        {
            throw new InvalidOperationException(
                $"[GameSceneSetup] HudRoot 프리팹 배선 대상 CrowdLabel 프리팹이 없습니다: {CrowdLabelPrefabPath}.");
        }

        ConvergePrefabSerializedRef<HudRoot>(
            HudRootPrefabPath, "_crowdLabelPrefab", crowdLabel, "HudRoot", changed, unchanged);
    }

    // HudRoot 프리팹의 _rivalMarkerPrefab 직렬화 필드에 RivalMarker 템플릿 프리팹(Hud/Prefabs)을 배선한다.
    private static void WireHudRootRivalMarkerPrefab(List<string> changed, List<string> unchanged)
    {
        GameObject rivalMarker = AssetDatabase.LoadAssetAtPath<GameObject>(RivalMarkerPrefabPath);
        if (rivalMarker == null)
        {
            throw new InvalidOperationException(
                $"[GameSceneSetup] HudRoot 프리팹 배선 대상 RivalMarker 프리팹이 없습니다: {RivalMarkerPrefabPath}.");
        }

        ConvergePrefabSerializedRef<HudRoot>(
            HudRootPrefabPath, "_rivalMarkerPrefab", rivalMarker, "HudRoot", changed, unchanged);
    }

    // HudRoot 프리팹의 _fontAsset 직렬화 필드에 벤더 TMP 폰트(이동하지 않고 GUID 참조)를 배선한다.
    private static void WireHudRootFont(List<string> changed, List<string> unchanged)
    {
        TMP_FontAsset font = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(HudFontAssetPath);
        if (font == null)
        {
            throw new InvalidOperationException(
                $"[GameSceneSetup] HudRoot 프리팹 배선 대상 TMP 폰트가 없습니다: {HudFontAssetPath}.");
        }

        ConvergePrefabSerializedRef<HudRoot>(
            HudRootPrefabPath, "_fontAsset", font, "HudRoot", changed, unchanged);
    }

    // CameraRoot 프리팹의 buildingOccludedMaterial 직렬화 필드에 City 생성물 material(City_Occluded.mat)을 배선한다.
    private static void WireCameraRootOccludedMaterial(List<string> changed, List<string> unchanged)
    {
        Material occluded = AssetDatabase.LoadAssetAtPath<Material>(CityBuildingsGenerator.OccludedMaterialPath);
        if (occluded == null)
        {
            throw new InvalidOperationException(
                $"[GameSceneSetup] CameraRoot 프리팹 배선 대상 material이 없습니다: {CityBuildingsGenerator.OccludedMaterialPath}. " +
                "먼저 'AF/CrowdCity/Setup City Buildings'를 실행하세요.");
        }

        ConvergePrefabSerializedRef<CameraRoot>(
            CameraRootPrefabPath, "buildingOccludedMaterial", occluded, "CameraRoot", changed, unchanged);
    }

    // HudRoot 프리팹의 _crowdCountTextStyle 직렬화 필드에 CrowdCountTextStyle.asset을 배선한다.
    private static void WireHudRootCrowdCountTextStyle(List<string> changed, List<string> unchanged)
    {
        TMPTextStyleSO style = AssetDatabase.LoadAssetAtPath<TMPTextStyleSO>(CrowdCountTextStylePath);
        if (style == null)
        {
            throw new InvalidOperationException(
                $"[GameSceneSetup] HudRoot 프리팹 배선 대상 스타일 SO가 없습니다: {CrowdCountTextStylePath}.");
        }

        ConvergePrefabSerializedRef<HudRoot>(
            HudRootPrefabPath, "_crowdCountTextStyle", style, "HudRoot", changed, unchanged);
    }

    // 프리팹 asset의 컴포넌트 T에서 objectReference 직렬화 필드 하나를 목표 값으로 수렴한다.
    // 이미 목표 값이면 프리팹을 다시 저장하지 않는다(idempotent). LoadPrefabContents로 격리 편집해 diff를 해당 필드로 한정한다.
    private static void ConvergePrefabSerializedRef<T>(
        string prefabPath,
        string fieldName,
        UnityEngine.Object value,
        string label,
        List<string> changed,
        List<string> unchanged)
        where T : Component
    {
        GameObject contents = PrefabUtility.LoadPrefabContents(prefabPath);
        try
        {
            T component = contents.GetComponent<T>();
            if (component == null)
            {
                throw new InvalidOperationException(
                    $"[GameSceneSetup] 프리팹 '{prefabPath}' root에 {typeof(T).Name} 컴포넌트가 없습니다.");
            }

            SerializedObject serialized = new SerializedObject(component);
            SerializedProperty property = serialized.FindProperty(fieldName);
            if (property == null)
            {
                throw new InvalidOperationException(
                    $"[GameSceneSetup] {typeof(T).Name}에서 직렬화 필드 '{fieldName}'을(를) 찾지 못했습니다.");
            }

            if (property.objectReferenceValue == value)
            {
                unchanged.Add($"{label} 프리팹 '{fieldName}' 배선");
                return;
            }

            property.objectReferenceValue = value;
            serialized.ApplyModifiedPropertiesWithoutUndo();
            PrefabUtility.SaveAsPrefabAsset(contents, prefabPath, out bool saveSuccess);
            if (!saveSuccess)
            {
                throw new InvalidOperationException(
                    $"[GameSceneSetup] 프리팹 저장에 실패했습니다: {prefabPath} ('{fieldName}' 배선).");
            }

            changed.Add($"{label} 프리팹 '{fieldName}' 배선");
        }
        finally
        {
            PrefabUtility.UnloadPrefabContents(contents);
        }
    }

    // HUD 프리팹 3종(HudRoot Canvas 트리 + CrowdLabel/RivalMarker 동적 템플릿)을 load-or-create 수렴한다.
    // HudRoot는 UI 카테고리(Resources/UI), 동적 템플릿은 Resources 밖 Hud/Prefabs에 저작하며, 레이아웃은 HudRoot.cs의 옛 Build* 상수를 1:1로 옮긴 것이다.
    private static void ConvergeHudPrefabs(List<string> changed, List<string> unchanged)
    {
        EnsureFolder(HudPrefabsFolder);

        TMP_FontAsset font = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(HudFontAssetPath);
        if (font == null)
        {
            throw new InvalidOperationException(
                "[GameSceneSetup] HUD TMP 폰트 에셋을 찾지 못했습니다: " + HudFontAssetPath);
        }

        ConvergeHudRootPrefab(font, changed, unchanged);
        ConvergeCrowdLabelPrefab(font, changed, unchanged);
        ConvergeRivalMarkerPrefab(font, changed, unchanged);
    }

    // HudRoot.prefab: root(RectTransform + Canvas ScreenSpaceOverlay + CanvasScaler 1080x1920 match 0.5 + HudRoot) 아래
    // 정적 uGUI 트리(리더라벨 레이어/타이머/순위표 4행/마커 레이어/시작힌트/결과오버레이)를 저작하고 HudRoot 뷰 참조를 배선한다.
    // 이미 Canvas가 저작된 프리팹이면 건드리지 않는다(idempotent).
    private static void ConvergeHudRootPrefab(TMP_FontAsset font, List<string> changed, List<string> unchanged)
    {
        EnsureFolder(HudRootsFolder);

        GameObject existing = AssetDatabase.LoadAssetAtPath<GameObject>(HudRootPrefabPath);
        if (existing != null && existing.GetComponent<HudRoot>() != null && existing.GetComponent<Canvas>() != null)
        {
            unchanged.Add("HUD 프리팹: HudRoot (Canvas 저작 완료)");
            return;
        }

        GameObject root = new GameObject("HudRoot", typeof(RectTransform));
        try
        {
            Canvas canvas = root.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;

            CanvasScaler scaler = root.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = HudReferenceResolution;
            scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
            scaler.matchWidthOrHeight = HudCanvasMatch;

            HudRoot hud = root.AddComponent<HudRoot>();

            Transform canvasTransform = root.transform;

            // 자식 순서 = 옛 BuildCanvas 순서(그리기 순서 보존): LeaderLabels -> Timer -> Leaderboard -> RivalMarkers -> StartHint -> ResultOverlay.
            RectTransform leaderLabelLayer = CreateHudLayer(canvasTransform, "LeaderLabels");

            TextMeshProUGUI timer = CreateHudText(
                canvasTransform, font, "Timer", 72, TextAlignmentOptions.Center, Color.white, FontStyles.Bold);
            SetHudRect(timer.rectTransform,
                new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -36f), new Vector2(500f, 100f));

            GameObject[] rowGos;
            Image[] rowSwatches;
            TextMeshProUGUI[] rowCounts;
            BuildHudLeaderboard(canvasTransform, font, out rowGos, out rowSwatches, out rowCounts);

            RectTransform markerLayer = CreateHudLayer(canvasTransform, "RivalMarkers");

            TextMeshProUGUI hint = CreateHudText(
                canvasTransform, font, "StartHint", 56, TextAlignmentOptions.Center, Color.white, FontStyles.Bold);
            hint.text = "DRAG TO START";
            SetHudRect(hint.rectTransform,
                new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0f, -360f), new Vector2(900f, 90f));

            GameObject resultOverlayGo;
            TextMeshProUGUI resultTitle;
            TextMeshProUGUI resultStandings;
            BuildHudResultOverlay(canvasTransform, font, out resultOverlayGo, out resultTitle, out resultStandings);

            // 뷰 참조 배선(자기 자식 뷰 참조 — SO 소비자 불변성과 무관). 필드명이 어긋나면 SetHudRef가 예외를 던진다.
            SerializedObject serialized = new SerializedObject(hud);
            SetHudRef(serialized, "_timerText", timer);
            SetHudRef(serialized, "_hintGo", hint.gameObject);
            SetHudRef(serialized, "_resultOverlayGo", resultOverlayGo);
            SetHudRef(serialized, "_resultTitleText", resultTitle);
            SetHudRef(serialized, "_resultStandingsText", resultStandings);
            SetHudRef(serialized, "_leaderLabelLayerRect", leaderLabelLayer);
            SetHudRef(serialized, "_markerLayerRect", markerLayer);
            SetHudRefArray(serialized, "_rowGos", rowGos);
            SetHudRefArray(serialized, "_rowSwatches", rowSwatches);
            SetHudRefArray(serialized, "_rowCounts", rowCounts);
            serialized.ApplyModifiedPropertiesWithoutUndo();

            GameObject saved = PrefabUtility.SaveAsPrefabAsset(root, HudRootPrefabPath);
            if (saved == null)
            {
                throw new InvalidOperationException("[GameSceneSetup] HudRoot 프리팹 저장에 실패했습니다: " + HudRootPrefabPath);
            }
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(root);
        }

        changed.Add("HUD 프리팹 저작/갱신: HudRoot (Canvas+정적 uGUI)");
    }

    // CrowdLabel.prefab: 리더 머리 위 라벨 템플릿. root TMP(Bold/Bottom/richText, fontSize 64, container 240x160)만 저작하고
    // 팀 아웃라인 material과 텍스트는 HudRoot.CreateLabel이 런타임 주입한다.
    private static void ConvergeCrowdLabelPrefab(TMP_FontAsset font, List<string> changed, List<string> unchanged)
    {
        GameObject existing = AssetDatabase.LoadAssetAtPath<GameObject>(CrowdLabelPrefabPath);
        if (existing != null && existing.GetComponent<TextMeshProUGUI>() != null)
        {
            unchanged.Add("HUD 프리팹: CrowdLabel");
            return;
        }

        GameObject go = new GameObject("CrowdLabel", typeof(RectTransform));
        try
        {
            SetHudRect((RectTransform)go.transform,
                new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0f), Vector2.zero, HudLabelContainerSize);

            TextMeshProUGUI label = go.AddComponent<TextMeshProUGUI>();
            label.font = font;
            label.fontSize = HudLabelFontSize;
            label.fontStyle = FontStyles.Bold;
            label.alignment = TextAlignmentOptions.Bottom;
            label.richText = true;
            label.color = Color.white;
            label.raycastTarget = false;
            label.textWrappingMode = TextWrappingModes.NoWrap;
            label.overflowMode = TextOverflowModes.Overflow;
            label.text = "1";

            GameObject saved = PrefabUtility.SaveAsPrefabAsset(go, CrowdLabelPrefabPath);
            if (saved == null)
            {
                throw new InvalidOperationException("[GameSceneSetup] CrowdLabel 프리팹 저장에 실패했습니다: " + CrowdLabelPrefabPath);
            }
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(go);
        }

        changed.Add("HUD 프리팹 저작/갱신: CrowdLabel");
    }

    // RivalMarker.prefab: 화면 밖 방향/인원 마커 템플릿. root(RectTransform 112x112) + Arrow(▲ 76x76) + Count(112x54, y -58)를 저작하고
    // 팀 색(화살표)과 카운트는 HudRoot.CreateRivalMarker/UpdateRivalMarkers가 런타임 주입한다.
    private static void ConvergeRivalMarkerPrefab(TMP_FontAsset font, List<string> changed, List<string> unchanged)
    {
        GameObject existing = AssetDatabase.LoadAssetAtPath<GameObject>(RivalMarkerPrefabPath);
        if (existing != null
            && existing.GetComponent<RectTransform>() != null
            && existing.transform.Find("Arrow") != null
            && existing.transform.Find("Count") != null)
        {
            unchanged.Add("HUD 프리팹: RivalMarker");
            return;
        }

        GameObject markerGo = new GameObject("RivalMarker", typeof(RectTransform));
        try
        {
            SetHudRect((RectTransform)markerGo.transform,
                new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, HudMarkerSize);

            TextMeshProUGUI arrow = CreateHudText(
                markerGo.transform, font, "Arrow", 72, TextAlignmentOptions.Center, Color.white, FontStyles.Bold);
            arrow.text = "▲";
            SetHudRect(arrow.rectTransform,
                new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, HudMarkerArrowSize);

            TextMeshProUGUI count = CreateHudText(
                markerGo.transform, font, "Count", 42, TextAlignmentOptions.Center, Color.white, FontStyles.Bold);
            count.text = "1";
            SetHudRect(count.rectTransform,
                new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.down * HudMarkerCountInset, HudMarkerCountSize);

            GameObject saved = PrefabUtility.SaveAsPrefabAsset(markerGo, RivalMarkerPrefabPath);
            if (saved == null)
            {
                throw new InvalidOperationException("[GameSceneSetup] RivalMarker 프리팹 저장에 실패했습니다: " + RivalMarkerPrefabPath);
            }
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(markerGo);
        }

        changed.Add("HUD 프리팹 저작/갱신: RivalMarker");
    }

    // 로직 전용 root 프리팹 하나를 수렴한다. 이미 컴포넌트 T가 부착된 prefab이 있으면 건드리지 않고, 없거나 손상되면
    // 임시 GameObject(빈 GO + T)를 만들어 SaveAsPrefabAsset으로 덮어쓴 뒤 임시 GO를 즉시 DestroyImmediate한다.
    private static void ConvergeLogicRootPrefab<T>(
        string folder, List<string> changed, List<string> unchanged)
        where T : Component
    {
        EnsureFolder(folder);

        // 프리팹 파일명/root 이름은 typeof(T).Name에서 파생한다(Prefabs 카테고리 로더 키 "Prefabs/<클래스이름>"와 어긋날 수 없다; UI 카테고리 HudRoot는 ConvergeHudPrefabs가 저작).
        string rootName = typeof(T).Name;
        string prefabPath = folder + "/" + rootName + ".prefab";

        GameObject existing = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
        if (existing != null && existing.GetComponent<T>() != null)
        {
            unchanged.Add($"feature root 프리팹: {rootName}");
            return;
        }

        GameObject temp = new GameObject(rootName);
        try
        {
            temp.AddComponent<T>();
            GameObject saved = PrefabUtility.SaveAsPrefabAsset(temp, prefabPath);
            if (saved == null)
            {
                throw new InvalidOperationException($"[GameSceneSetup] feature root 프리팹 저장에 실패했습니다: {prefabPath}");
            }
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(temp);
        }

        changed.Add($"feature root 프리팹 저장/갱신: {rootName}");
    }

    /// <summary>
    /// Phase 2 전용: feature root 프리팹 5종(GameplayRoot/InputRoot/CameraRoot/CrowdRoot/HudRoot)을 각 피처 Resources 하위로
    /// load-or-create 수렴한다. City/씬/Config 수렴 없이 prefab asset만 갱신하므로 batchmode에서 독립 실행할 수 있다. idempotent.
    /// </summary>
    [MenuItem("AF/CrowdCity/Bake Feature Root Prefabs (Resources)")]
    public static void BakeFeatureRootPrefabs()
    {
        List<string> changed = new List<string>(8);
        List<string> unchanged = new List<string>(8);
        ConvergeFeatureRootPrefabs(changed, unchanged);

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();

        // 결과 확인(실패 시 예외로 명확히 알린다). 각 prefab에 해당 컴포넌트가 부착됐는지 검사한다.
        VerifyLogicRootPrefab<GameplayRoot>(GameRootsFolder);
        VerifyLogicRootPrefab<InputRoot>(GameRootsFolder);
        VerifyLogicRootPrefab<CameraRoot>(GameRootsFolder);
        VerifyLogicRootPrefab<CrowdRoot>(CrowdRootsFolder);
        VerifyLogicRootPrefab<HudRoot>(HudRootsFolder);

        // 새로 저작된 직렬화 참조가 저장된 프리팹에서 실제로 해석되는지(컴포넌트 존재만이 아니라) fail-fast 검증한다.
        VerifyPrefabSerializedRef<CameraRoot>(
            CameraRootPrefabPath, "buildingOccludedMaterial", CityBuildingsGenerator.OccludedMaterialPath);
        VerifyPrefabSerializedRef<HudRoot>(
            HudRootPrefabPath, "_crowdCountTextStyle", CrowdCountTextStylePath);
        VerifyPrefabSerializedRef<CrowdRoot>(CrowdRootPrefabPath, "_humanPrefab", HumanPrefabPath);
        VerifyPrefabSerializedRef<CrowdRoot>(CrowdRootPrefabPath, "_wallSdfAsset", WallSdfAssetPath);
        VerifyPrefabSerializedRef<HudRoot>(HudRootPrefabPath, "_crowdLabelPrefab", CrowdLabelPrefabPath);
        VerifyPrefabSerializedRef<HudRoot>(HudRootPrefabPath, "_rivalMarkerPrefab", RivalMarkerPrefabPath);
        VerifyPrefabSerializedRef<HudRoot>(HudRootPrefabPath, "_fontAsset", HudFontAssetPath);

        Debug.Log(
            "[BakeFeatureRootPrefabs] PASS — GameplayRoot/InputRoot/CameraRoot/CrowdRoot/HudRoot 프리팹 저작 완료. " +
            BuildSummary(changed, unchanged));
    }

    /// <summary>
    /// batch mode 진입점. <see cref="BakeFeatureRootPrefabs"/>를 실행하고 성공 시 exit 0, 예외 시 로그 후 exit 1로 종료한다.
    /// </summary>
    public static void BakeFeatureRootPrefabsBatch()
    {
        try
        {
            BakeFeatureRootPrefabs();
            EditorApplication.Exit(0);
        }
        catch (Exception exception)
        {
            Debug.LogException(exception);
            EditorApplication.Exit(1);
        }
    }

    private static void VerifyLogicRootPrefab<T>(string folder)
        where T : Component
    {
        // 검증 경로도 typeof(T).Name에서 파생해 baker/loader와 동일 키를 쓴다.
        string prefabPath = folder + "/" + typeof(T).Name + ".prefab";
        GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
        if (prefab == null || prefab.GetComponent<T>() == null)
        {
            throw new InvalidOperationException(
                $"[GameSceneSetup] feature root 프리팹 baking 검증 실패: path={prefabPath}, prefab={prefab != null}, {typeof(T).Name}={(prefab != null && prefab.GetComponent<T>() != null)}");
        }
    }

    // 저장된 프리팹 asset을 다시 로드해 objectReference 직렬화 필드가 기대 asset 경로로 해석되는지 검증한다(baking 성공 오탐 방지).
    private static void VerifyPrefabSerializedRef<T>(string prefabPath, string fieldName, string expectedAssetPath)
        where T : Component
    {
        GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
        T component = prefab != null ? prefab.GetComponent<T>() : null;
        if (component == null)
        {
            throw new InvalidOperationException(
                $"[GameSceneSetup] 프리팹 ref 검증 실패: '{prefabPath}'에서 {typeof(T).Name}을(를) 로드하지 못했습니다.");
        }

        SerializedObject serialized = new SerializedObject(component);
        SerializedProperty property = serialized.FindProperty(fieldName);
        UnityEngine.Object value = property != null ? property.objectReferenceValue : null;
        string actualPath = value != null ? AssetDatabase.GetAssetPath(value) : null;
        if (actualPath != expectedAssetPath)
        {
            throw new InvalidOperationException(
                $"[GameSceneSetup] 프리팹 ref 검증 실패: {typeof(T).Name}.'{fieldName}'이(가) '{expectedAssetPath}'로 해석되지 않습니다(actual='{actualPath ?? "null"}').");
        }
    }

    // ---- HUD uGUI 저작 헬퍼 (옛 HudRoot.BuildCanvas/CreateText/SetRect의 1:1 이식) ----

    // full-stretch RectTransform 레이어(리더 라벨/마커 컨테이너). 옛 BuildLeaderLabelLayer/BuildMarkerLayer와 동일.
    private static RectTransform CreateHudLayer(Transform parent, string name)
    {
        GameObject go = new GameObject(name, typeof(RectTransform));
        go.transform.SetParent(parent, false);

        RectTransform rect = (RectTransform)go.transform;
        rect.anchorMin = Vector2.zero;
        rect.anchorMax = Vector2.one;
        rect.offsetMin = Vector2.zero;
        rect.offsetMax = Vector2.zero;
        return rect;
    }

    // 옛 HudRoot.CreateText와 동일한 TMP 속성(폰트/크기/정렬/색/스타일 + raycast off, NoWrap, Overflow)을 저작한다.
    private static TextMeshProUGUI CreateHudText(
        Transform parent,
        TMP_FontAsset font,
        string name,
        int fontSize,
        TextAlignmentOptions alignment,
        Color color,
        FontStyles style)
    {
        GameObject go = new GameObject(name, typeof(RectTransform));
        go.transform.SetParent(parent, false);

        TextMeshProUGUI text = go.AddComponent<TextMeshProUGUI>();
        text.font = font;
        text.fontSize = fontSize;
        text.alignment = alignment;
        text.color = color;
        text.fontStyle = style;
        text.raycastTarget = false;
        text.textWrappingMode = TextWrappingModes.NoWrap;
        text.overflowMode = TextOverflowModes.Overflow;
        return text;
    }

    // 옛 HudRoot.SetRect와 동일(anchorMin=anchorMax=anchor, pivot, anchoredPosition, sizeDelta).
    private static void SetHudRect(RectTransform rect, Vector2 anchor, Vector2 pivot, Vector2 anchoredPosition, Vector2 size)
    {
        rect.anchorMin = anchor;
        rect.anchorMax = anchor;
        rect.pivot = pivot;
        rect.anchoredPosition = anchoredPosition;
        rect.sizeDelta = size;
    }

    // 옛 HudRoot.BuildLeaderboard와 동일: 우상단 컨테이너 아래 Row0..3(각 Swatch Image + Count TMP).
    private static void BuildHudLeaderboard(
        Transform canvasTransform,
        TMP_FontAsset font,
        out GameObject[] rowGos,
        out Image[] rowSwatches,
        out TextMeshProUGUI[] rowCounts)
    {
        GameObject leaderboardGo = new GameObject("Leaderboard", typeof(RectTransform));
        leaderboardGo.transform.SetParent(canvasTransform, false);
        SetHudRect((RectTransform)leaderboardGo.transform,
            new Vector2(1f, 1f), new Vector2(1f, 1f), new Vector2(-30f, -30f),
            new Vector2(HudRowWidth, HudRowStride * HudMaxTeams));

        rowGos = new GameObject[HudMaxTeams];
        rowSwatches = new Image[HudMaxTeams];
        rowCounts = new TextMeshProUGUI[HudMaxTeams];

        for (int i = 0; i < HudMaxTeams; i++)
        {
            GameObject rowGo = new GameObject("Row" + i, typeof(RectTransform));
            rowGo.transform.SetParent(leaderboardGo.transform, false);
            SetHudRect((RectTransform)rowGo.transform,
                new Vector2(1f, 1f), new Vector2(1f, 1f), new Vector2(0f, -i * HudRowStride),
                new Vector2(HudRowWidth, HudRowHeight));

            GameObject swatchGo = new GameObject("Swatch", typeof(RectTransform));
            swatchGo.transform.SetParent(rowGo.transform, false);
            Image swatch = swatchGo.AddComponent<Image>();
            swatch.raycastTarget = false;
            SetHudRect((RectTransform)swatchGo.transform,
                new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(0f, 0f),
                new Vector2(HudSwatchSize, HudSwatchSize));

            TextMeshProUGUI countText = CreateHudText(
                rowGo.transform, font, "Count", 44, TextAlignmentOptions.Right, Color.white, FontStyles.Bold);
            RectTransform countRect = countText.rectTransform;
            countRect.anchorMin = new Vector2(0f, 0f);
            countRect.anchorMax = new Vector2(1f, 1f);
            countRect.offsetMin = new Vector2(HudSwatchSize + 12f, 0f);
            countRect.offsetMax = new Vector2(0f, 0f);
            countText.text = "-";

            rowGos[i] = rowGo;
            rowSwatches[i] = swatch;
            rowCounts[i] = countText;
        }
    }

    // 옛 HudRoot.BuildResultOverlay와 동일: dim Image(full stretch) + Title/Standings/RestartHint TMP. 기본 비활성.
    private static void BuildHudResultOverlay(
        Transform canvasTransform,
        TMP_FontAsset font,
        out GameObject resultOverlayGo,
        out TextMeshProUGUI resultTitle,
        out TextMeshProUGUI resultStandings)
    {
        resultOverlayGo = new GameObject("ResultOverlay", typeof(RectTransform));
        resultOverlayGo.transform.SetParent(canvasTransform, false);

        Image dim = resultOverlayGo.AddComponent<Image>();
        dim.color = HudDimColor;
        dim.raycastTarget = false;

        RectTransform overlayRect = (RectTransform)resultOverlayGo.transform;
        overlayRect.anchorMin = Vector2.zero;
        overlayRect.anchorMax = Vector2.one;
        overlayRect.offsetMin = Vector2.zero;
        overlayRect.offsetMax = Vector2.zero;

        Transform overlayTransform = resultOverlayGo.transform;

        resultTitle = CreateHudText(
            overlayTransform, font, "Title", 120, TextAlignmentOptions.Center, Color.white, FontStyles.Bold);
        SetHudRect(resultTitle.rectTransform,
            new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0f, 420f), new Vector2(900f, 160f));

        resultStandings = CreateHudText(
            overlayTransform, font, "Standings", 56, TextAlignmentOptions.Center, Color.white, FontStyles.Normal);
        SetHudRect(resultStandings.rectTransform,
            new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0f, 40f), new Vector2(900f, 520f));

        TextMeshProUGUI restart = CreateHudText(
            overlayTransform, font, "RestartHint", 48, TextAlignmentOptions.Center, Color.white, FontStyles.Bold);
        restart.text = "R / TAP TO RESTART";
        SetHudRect(restart.rectTransform,
            new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0f, -420f), new Vector2(900f, 90f));

        resultOverlayGo.SetActive(false);
    }

    private static void SetHudRef(SerializedObject serialized, string propertyName, UnityEngine.Object value)
    {
        SerializedProperty property = serialized.FindProperty(propertyName);
        if (property == null)
        {
            throw new InvalidOperationException(
                $"[GameSceneSetup] HudRoot에서 직렬화 필드 '{propertyName}'을(를) 찾지 못했습니다.");
        }

        property.objectReferenceValue = value;
    }

    private static void SetHudRefArray(SerializedObject serialized, string propertyName, UnityEngine.Object[] values)
    {
        SerializedProperty property = serialized.FindProperty(propertyName);
        if (property == null)
        {
            throw new InvalidOperationException(
                $"[GameSceneSetup] HudRoot에서 직렬화 배열 필드 '{propertyName}'을(를) 찾지 못했습니다.");
        }

        property.arraySize = values.Length;
        for (int i = 0; i < values.Length; i++)
        {
            property.GetArrayElementAtIndex(i).objectReferenceValue = values[i];
        }
    }

    // ---- 6. GameSceneController ----

    private static bool ConvergeSceneController(
        Scene scene,
        GameConfigSO config,
        Camera mainCamera,
        Transform city,
        List<string> changed,
        List<string> unchanged)
    {
        bool changedHere = false;

        GameObject controllerGo = FindSceneRoot(scene, ControllerGoName);
        if (controllerGo == null)
        {
            // 활성 씬이 GameScene임을 이미 검증했으므로 새 GO는 GameScene의 root로 들어간다.
            controllerGo = new GameObject(ControllerGoName);
            Undo.RegisterCreatedObjectUndo(controllerGo, "Create GameSceneController");
            changedHere = true;
        }

        GameSceneController controller = controllerGo.GetComponent<GameSceneController>();
        if (controller == null)
        {
            controller = Undo.AddComponent<GameSceneController>(controllerGo);
            changedHere = true;
        }

        // 필드 이름 기반 배선. 이름이 어긋나면(계약 위반) SetObjectReference가 예외를 던진다.
        // crowdCountTextStyle/buildingOccludedMaterial은 GSC가 아니라 각 root 프리팹에 직렬화 저작된다.
        SerializedObject serialized = new SerializedObject(controller);
        changedHere |= SetObjectReference(serialized, "config", config);
        changedHere |= SetObjectReference(serialized, "mainCamera", mainCamera);
        changedHere |= SetObjectReference(serialized, "cityRoot", city);
        if (serialized.hasModifiedProperties)
        {
            serialized.ApplyModifiedProperties();
        }

        if (changedHere)
        {
            changed.Add("씬: GameSceneController 생성/배선");
        }
        else
        {
            unchanged.Add("씬: GameSceneController 배선");
        }

        return changedHere;
    }

    private static bool SetObjectReference(SerializedObject serialized, string propertyName, UnityEngine.Object value)
    {
        SerializedProperty property = serialized.FindProperty(propertyName);
        if (property == null)
        {
            throw new InvalidOperationException(
                $"[GameSceneSetup] GameSceneController에서 직렬화 필드 '{propertyName}'을(를) 찾지 못했습니다.");
        }

        if (property.objectReferenceValue == value)
        {
            return false;
        }

        property.objectReferenceValue = value;
        return true;
    }

    // ---- 공용 헬퍼 ----

    private static GameObject FindSceneRoot(Scene scene, string rootName)
    {
        GameObject[] roots = scene.GetRootGameObjects();
        for (int i = 0; i < roots.Length; i++)
        {
            if (roots[i].name == rootName)
            {
                return roots[i];
            }
        }

        return null;
    }

    private static GameObject RequireSceneRoot(Scene scene, string rootName)
    {
        GameObject root = FindSceneRoot(scene, rootName);
        if (root == null)
        {
            throw new InvalidOperationException($"[GameSceneSetup] 씬 root '{rootName}'을(를) 찾지 못했습니다.");
        }

        return root;
    }

    private static Transform RequireChild(Transform parent, string childName)
    {
        Transform child = parent.Find(childName);
        if (child == null)
        {
            throw new InvalidOperationException(
                $"[GameSceneSetup] '{parent.name}/{childName}' 오브젝트를 씬에서 찾지 못했습니다.");
        }

        return child;
    }

    private static Camera RequireMainCamera(Scene scene)
    {
        GameObject cameraGo = RequireSceneRoot(scene, MainCameraName);
        Camera camera = cameraGo.GetComponent<Camera>();
        if (camera == null)
        {
            throw new InvalidOperationException($"[GameSceneSetup] '{MainCameraName}'에 Camera component가 없습니다.");
        }

        return camera;
    }

    private static void EnsureFolder(string folderPath)
    {
        if (AssetDatabase.IsValidFolder(folderPath))
        {
            return;
        }

        int separatorIndex = folderPath.LastIndexOf('/');
        string parentPath = folderPath.Substring(0, separatorIndex);
        string leafName = folderPath.Substring(separatorIndex + 1);
        EnsureFolder(parentPath);
        AssetDatabase.CreateFolder(parentPath, leafName);
    }

    private static string ToAbsolutePath(string assetPath)
    {
        return Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), assetPath));
    }

    private static string BuildSummary(List<string> changed, List<string> unchanged)
    {
        StringBuilder builder = new StringBuilder(512);
        builder.Append("[GameSceneSetup] 완료 — 변경 ").Append(changed.Count)
            .Append("건, 이미 일치 ").Append(unchanged.Count).AppendLine("건");

        builder.Append("변경: ");
        AppendJoined(builder, changed);
        builder.AppendLine();

        builder.Append("이미 일치: ");
        AppendJoined(builder, unchanged);

        return builder.ToString();
    }

    private static void AppendJoined(StringBuilder builder, List<string> items)
    {
        if (items.Count == 0)
        {
            builder.Append("없음");
            return;
        }

        for (int i = 0; i < items.Count; i++)
        {
            if (i > 0)
            {
                builder.Append("; ");
            }

            builder.Append(items[i]);
        }
    }
}

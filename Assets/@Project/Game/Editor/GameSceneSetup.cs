using System;
using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// GameScene을 zero-manual-step 상태로 수렴시키는 editor setup이다.
/// FBX clip import, AnimatorController, 팀 material, GameConfig asset, 씬 오브젝트 배선을
/// 모두 load-or-create + converge 방식으로 적용하며 실제로 바뀐 항목이 있을 때만 저장한다.
/// 기대하는 씬 오브젝트가 없으면 조용히 넘어가지 않고 예외를 던진다.
/// </summary>
public static class GameSceneSetup
{
    private const string ScenePath = "Assets/@Project/Scenes/GameScene.unity";
    private const string FbxPath = "Assets/@Project/Human/Externals/Human_Base.fbx";
    private const string AnimationsFolder = "Assets/@Project/Human/Animations";
    private const string ControllerPath = AnimationsFolder + "/HumanWalk.controller";
    private const string MaterialsFolder = "Assets/@Project/Human/Materials";
    private const string PrefabsFolder = "Assets/@Project/Human/Prefabs";
    private const string HumanPrefabPath = PrefabsFolder + "/Human.prefab";
    private const string GameFolder = "Assets/@Project/Game";
    private const string ConfigPath = GameFolder + "/GameConfig.asset";
    private const string WalkName = "HumanWalk";
    private const string UrpLitShaderName = "Universal Render Pipeline/Lit";
    private const string BaseColorProperty = "_BaseColor";
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

    // MeshCollider를 가져야 하는 City 자식. 이 밖의 자식(Ground, RoadMarks)은 collider가 없어야 한다.
    private static readonly string[] ColliderChildNames =
    {
        "Buildings",
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

        List<string> changed = new List<string>(16);
        List<string> unchanged = new List<string>(16);

        // 1. FBX take clip -> HumanWalk(loop) import 설정.
        ConvergeFbxClipImport(changed, unchanged);

        // 2. clip 재조회(fileID는 rename 시 바뀌므로 절대 캐시하지 않는다) + controller 수렴.
        AnimationClip walkClip = FetchWalkClip();
        AnimatorController animatorController = ConvergeAnimatorController(walkClip, changed, unchanged);

        // 5(선행). material 색상의 원본이 되는 config를 먼저 확보한다.
        GameConfigSO config = LoadOrCreateConfig(changed, unchanged);

        // 3. FBX 원본 material 검증 + 팀 material 5종 수렴.
        Material[] teamMaterials = ConvergeTeamMaterials(config, changed, unchanged);

        // 5(후행). config에 팀 material 배선.
        WireConfigMaterials(config, teamMaterials, changed, unchanged);

        // 4 + 6. 씬 오브젝트 수렴(없으면 예외).
        GameObject gameArea = RequireSceneRoot(scene, GameAreaName);
        Transform humanWrapper = RequireChild(gameArea.transform, HumanWrapperName);
        Transform humanBase = RequireChild(humanWrapper, HumanBaseName);
        Transform city = RequireChild(gameArea.transform, CityName);
        Camera mainCamera = RequireMainCamera(scene);

        bool sceneChanged = false;
        sceneChanged |= ConvergeHumanAnimator(humanBase, animatorController, changed, unchanged);
        sceneChanged |= ConvergeCityColliders(city, changed, unchanged);

        // 4b. 완전히 구성된 씬 템플릿(Animator/controller/material 반영)을 prefab asset으로 저장하고
        //     씬 템플릿을 비활성화한다. 런타임은 이 prefab을 clone한다(씬 템플릿 의존 제거).
        GameObject humanPrefab = ConvergeHumanPrefab(humanWrapper, ref sceneChanged, changed);

        sceneChanged |= ConvergeSceneController(scene, config, humanPrefab, mainCamera, city, changed, unchanged);

        // 7. 바뀐 것이 있을 때만 저장한다.
        if (sceneChanged)
        {
            EditorSceneManager.MarkSceneDirty(scene);
            if (!EditorSceneManager.SaveScene(scene))
            {
                throw new InvalidOperationException($"[GameSceneSetup] 씬 저장에 실패했습니다: {ScenePath}");
            }
        }

        if (changed.Count > 0)
        {
            AssetDatabase.SaveAssets();
        }

        Debug.Log(BuildSummary(changed, unchanged));
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
            animator = humanBase.gameObject.AddComponent<Animator>();
            changedHere = true;
        }

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
                meshCollider = child.gameObject.AddComponent<MeshCollider>();
                changedHere = true;
            }

            if (meshCollider.sharedMesh != meshFilter.sharedMesh)
            {
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
                UnityEngine.Object.DestroyImmediate(stray);
                changedHere = true;
            }
        }

        if (changedHere)
        {
            changed.Add("씬: City MeshCollider(Buildings/StreetProps/Vehicles/Parks만)");
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

    // 구성이 끝난 씬 템플릿(GameArea/Human)을 prefab asset으로 저장하고 씬 템플릿을 비활성화한다.
    // prefab은 매번 같은 경로에 덮어써 수렴시킨다(idempotent). 저장된 prefab은 active 상태여야
    // 런타임 clone이 즉시 활성화되므로 저장 직전에 active를 보장한다.
    private static GameObject ConvergeHumanPrefab(Transform humanWrapper, ref bool sceneChanged, List<string> changed)
    {
        EnsureFolder(PrefabsFolder);

        GameObject templateGo = humanWrapper.gameObject;

        // prefab은 active 상태로 저장한다(런타임 clone이 즉시 활성).
        bool wasActive = templateGo.activeSelf;
        if (!wasActive)
        {
            templateGo.SetActive(true);
        }

        GameObject prefab = PrefabUtility.SaveAsPrefabAsset(templateGo, HumanPrefabPath);
        if (prefab == null)
        {
            throw new InvalidOperationException($"[GameSceneSetup] Human prefab 저장에 실패했습니다: {HumanPrefabPath}");
        }

        // 저장 후 씬 템플릿은 런타임에서 쓰이지 않으므로 비활성화한다(런타임은 prefab을 clone).
        templateGo.SetActive(false);

        // active 상태가 실제로 바뀐 경우(최초 실행: active -> inactive)에만 씬을 dirty로 표시한다.
        if (wasActive)
        {
            sceneChanged = true;
        }

        changed.Add("Human.prefab 저장/갱신 + 씬 템플릿 비활성화");
        return prefab;
    }

    // ---- 6. GameSceneController ----

    private static bool ConvergeSceneController(
        Scene scene,
        GameConfigSO config,
        GameObject humanPrefab,
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
            changedHere = true;
        }

        GameSceneController controller = controllerGo.GetComponent<GameSceneController>();
        if (controller == null)
        {
            controller = controllerGo.AddComponent<GameSceneController>();
            changedHere = true;
        }

        // 필드 이름 기반 배선. 이름이 어긋나면(계약 위반) SetObjectReference가 예외를 던진다.
        SerializedObject serialized = new SerializedObject(controller);
        changedHere |= SetObjectReference(serialized, "config", config);
        changedHere |= SetObjectReference(serialized, "humanPrefab", humanPrefab);
        changedHere |= SetObjectReference(serialized, "mainCamera", mainCamera);
        changedHere |= SetObjectReference(serialized, "cityRoot", city);
        if (serialized.hasModifiedProperties)
        {
            serialized.ApplyModifiedPropertiesWithoutUndo();
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

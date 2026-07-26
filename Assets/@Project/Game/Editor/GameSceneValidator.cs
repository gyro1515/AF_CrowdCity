using System.Collections.Generic;
using TMPro;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

/// <summary>
/// GameScene이 셋업(GameSceneSetup) 이후 플레이 가능한 상태인지 검증하는 Editor 전용 검증기다.
/// 첫 실패에서 중단하지 않고 모든 실패 사유를 수집한 뒤 한 번에 보고한다.
/// 씬/에셋을 절대 수정하지 않는다(읽기 전용 검증).
/// </summary>
public static class GameSceneValidator
{
    private const string ScenePath = "Assets/@Project/Scenes/GameScene.unity";
    private const string ConfigAssetPath = "Assets/@Project/Game/GameConfig.asset";
    private const string CrowdCountTextStyleAssetPath = "Assets/@Project/Hud/CrowdCountTextStyle.asset";
    // 런타임은 CrowdRoot가 직렬화 필드(_humanPrefab)로 이 prefab을 소유한다. 경로가 일치해야 한다.
    private const string HumanPrefabPath = "Assets/@Project/Human/Prefabs/Human.prefab";
    // HUD 프리팹 3종. HudRoot는 UI 카테고리(로드 키 "UI/HudRoot")에서 로드하고, 동적 템플릿(CrowdLabel/RivalMarker)은
    // HudRoot가 직렬화로 소비하므로 Resources 밖 Hud/Prefabs에 있다. GameSceneSetup.ConvergeHudPrefabs가 저작한다.
    private const string HudRootPrefabPath = "Assets/@Project/Hud/Resources/UI/HudRoot.prefab";
    private const string CrowdLabelPrefabPath = "Assets/@Project/Hud/Prefabs/CrowdLabel.prefab";
    private const string RivalMarkerPrefabPath = "Assets/@Project/Hud/Prefabs/RivalMarker.prefab";
    private static readonly Vector2 HudReferenceResolution = new Vector2(1080f, 1920f);
    private const float HudCanvasMatch = 0.5f;
    private const int HudMaxTeams = 4;
    private const string UrpLitShaderName = "Universal Render Pipeline/Lit";
    private const string BaseColorProperty = "_BaseColor";
    private const float ColorTolerance = 0.004f; // 약 1/255 — float 직렬화 오차 허용치.
    private const int MaxUnresolvedPathExamples = 5;

    // Human prefab root에 baking된 CharacterController의 pinned 스펙. GameSceneSetup의 baking 값과 반드시 일치해야 한다.
    // 리더/팔로워/중립 전원이 enabled CC로 물리충돌을 받는다(사용자 명시 의도).
    private const float ControllerRadius = 0.35f;
    private const float ControllerHeight = 1.8f;
    private const float ControllerCenterY = 0.9f;
    private const float ControllerSkinWidth = 0.08f;
    private const float ControllerSpecTolerance = 0.001f;

    private static readonly string[] ColliderRequiredChildren = { "Buildings", "StreetProps", "Vehicles", "Parks" };
    private static readonly string[] ColliderForbiddenChildren = { "Ground", "RoadMarks" };

    /// <summary>
    /// 메뉴에서 검증을 실행한다. 결과는 Console의 [Validator] PASS/FAIL 로그로 확인한다.
    /// </summary>
    [MenuItem("AF/CrowdCity/Validate Game Scene")]
    public static void ValidateMenu()
    {
        Validate();
    }

    /// <summary>
    /// 열린 씬과 GameConfig 에셋을 검증한다.
    /// 검사 항목: GameSceneController 존재와 6개 직렬화 참조, Human.prefab 구조(SkinnedMeshRenderer,
    /// Human_Base의 Animator, 루프 clip, 모든 curve 경로가 Animator 하위에서 해석되는지), City 하위 MeshCollider
    /// 배치, GameConfig의 팀 material(shader/_BaseColor 색상 일치)과 수치 범위.
    /// </summary>
    /// <returns>모든 검사를 통과하면 true, 하나라도 실패하면 false를 반환한다.</returns>
    public static bool Validate()
    {
        List<string> failures = new List<string>(32);

        ValidateController(failures);
        ValidateHumanPrefab(failures);
        ValidateHudPrefabs(failures);
        ValidateGeneratedBuildings(failures);
        ValidateCityColliders(failures);
        ValidateCrowdCountTextStyleAsset(failures);
        ValidateConfigAsset(failures);

        // 병합 Resources 네임스페이스의 중복 로드 키 검증을 파이프라인 게이트에 포함한다(상세는 [ResPathValidator] 로그).
        if (!ResourcePathValidator.Validate())
        {
            failures.Add("Resources 중복 로드 키 존재([ResPathValidator] 로그 참조)");
        }

        if (failures.Count == 0)
        {
            Debug.Log("[Validator] PASS");
            return true;
        }

        Debug.LogError("[Validator] FAIL: " + string.Join("; ", failures));
        return false;
    }

    /// <summary>
    /// batch mode 파이프라인용 진입점이다. 검증 통과 시 exit code 0, 실패 또는 예외 시 1로 Editor를 종료한다.
    /// </summary>
    public static void ValidateBatch()
    {
        bool passed = false;
        try
        {
            // batch에서는 활성 씬에 의존하지 않고 GameScene을 직접 연다(파이프라인 게이트 자체 완결).
            EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            passed = Validate();
        }
        finally
        {
            EditorApplication.Exit(passed ? 0 : 1);
        }
    }

    private static void ValidateController(List<string> failures)
    {
        GameSceneController controller =
            Object.FindFirstObjectByType<GameSceneController>(FindObjectsInactive.Include);
        if (controller == null)
        {
            failures.Add("열린 씬에 GameSceneController가 없음");
            return;
        }

        // 필드가 private이므로 GameSceneSetup과 같은 방식(SerializedObject, 이름 기반)으로 읽는다.
        // crowdCountTextStyle/buildingOccludedMaterial은 GSC가 아니라 각 root 프리팹에 직렬화 저작되므로
        // 씬-독립 프리팹-로컬 검사(ResourcePathValidator)가 담당한다. 여기서는 GSC 소유 참조만 검사한다.
        SerializedObject serialized = new SerializedObject(controller);
        Object configRef = GetObjectReference(serialized, "config", failures);
        GetObjectReference(serialized, "mainCamera", failures);
        Object cityRef = GetObjectReference(serialized, "cityRoot", failures);

        // humanPrefab은 CrowdRoot 프리팹의 직렬화 필드로 소유하며(ResourcePathValidator가 배선 검사) GSC 참조가 아니다. 여기서 검사하지 않는다.

        Transform sceneCity = FindSceneTransform("GameArea", "City");
        if (cityRef != null && sceneCity != null && cityRef != sceneCity)
        {
            failures.Add("GameSceneController.cityRoot가 GameArea/City가 아님");
        }

        GameConfigSO configAsset = AssetDatabase.LoadAssetAtPath<GameConfigSO>(ConfigAssetPath);
        if (configRef != null && configAsset != null && configRef != configAsset)
        {
            failures.Add($"GameSceneController.config가 {ConfigAssetPath} 에셋이 아님");
        }
    }

    private static Object GetObjectReference(SerializedObject serialized, string fieldName, List<string> failures)
    {
        SerializedProperty property = serialized.FindProperty(fieldName);
        if (property == null)
        {
            failures.Add($"GameSceneController에 직렬화 필드 '{fieldName}'가 없음");
            return null;
        }

        if (property.objectReferenceValue == null)
        {
            failures.Add($"GameSceneController.{fieldName} 참조가 비어 있음");
            return null;
        }

        return property.objectReferenceValue;
    }

    private static void ValidateHumanPrefab(List<string> failures)
    {
        // 런타임은 씬 템플릿이 아니라 이 prefab asset을 clone하므로 prefab을 검증한다.
        // asset root의 transform 계층은 읽기 전용으로 탐색 가능하므로 별도 로드/언로드가 필요 없다.
        GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(HumanPrefabPath);
        if (prefab == null)
        {
            failures.Add($"Human prefab이 없음({HumanPrefabPath})");
            return;
        }

        // prefab root에 Human 컴포넌트가 baking되어 있어야 한다(런타임 AddComponent 제거의 전제).
        if (prefab.GetComponent<Human>() == null)
        {
            failures.Add("Human.prefab root에 Human 컴포넌트가 baking되어 있지 않음");
        }

        // prefab root에 enabled CharacterController가 pinned 스펙으로 baking되어 있어야 한다(리더/팔로워/중립 전원 물리충돌 = 사용자 의도).
        CharacterController controller = prefab.GetComponent<CharacterController>();
        if (controller == null)
        {
            failures.Add("Human.prefab root에 CharacterController가 baking되어 있지 않음");
        }
        else
        {
            if (!controller.enabled)
            {
                failures.Add("Human.prefab CharacterController가 enabled=false임(전원 물리충돌 규약 위반)");
            }

            if (!Mathf.Approximately(controller.radius, ControllerRadius) ||
                !Mathf.Approximately(controller.height, ControllerHeight) ||
                Mathf.Abs(controller.center.x) > ControllerSpecTolerance ||
                Mathf.Abs(controller.center.y - ControllerCenterY) > ControllerSpecTolerance ||
                Mathf.Abs(controller.center.z) > ControllerSpecTolerance ||
                !Mathf.Approximately(controller.skinWidth, ControllerSkinWidth))
            {
                failures.Add(
                    $"Human.prefab CharacterController 스펙 불일치(radius={controller.radius}, height={controller.height}, " +
                    $"center={controller.center}, skinWidth={controller.skinWidth}; " +
                    $"기대 r={ControllerRadius}, h={ControllerHeight}, c=(0,{ControllerCenterY},0), skin={ControllerSkinWidth})");
            }
        }

        Transform humanBase = prefab.transform.Find("Human_Base");
        if (humanBase == null)
        {
            failures.Add("Human.prefab 아래에 Human_Base 자식이 없음");
            return;
        }

        if (humanBase.GetComponentInChildren<SkinnedMeshRenderer>(true) == null)
        {
            failures.Add("Human_Base 하위에 SkinnedMeshRenderer가 없음");
        }

        // Animator는 curve 경로(HumanAnimation/mixamorig:…)가 해석되도록 반드시 Human_Base 자신에 있어야 한다.
        Animator animator = humanBase.GetComponent<Animator>();
        if (animator == null)
        {
            failures.Add("Human_Base에 Animator가 없음(wrapper가 아닌 Human_Base 자신에 있어야 함)");
            return;
        }

        if (animator.runtimeAnimatorController == null)
        {
            failures.Add("Human_Base Animator에 controller가 없음");
            return;
        }

        AnimatorController animatorController = animator.runtimeAnimatorController as AnimatorController;
        if (animatorController == null)
        {
            failures.Add($"Animator controller '{animator.runtimeAnimatorController.name}'가 AnimatorController 타입이 아님");
            return;
        }

        if (animatorController.layers.Length == 0)
        {
            failures.Add($"AnimatorController '{animatorController.name}'에 layer가 없음");
            return;
        }

        AnimatorState defaultState = animatorController.layers[0].stateMachine.defaultState;
        if (defaultState == null)
        {
            failures.Add($"AnimatorController '{animatorController.name}' layer 0에 default state가 없음");
            return;
        }

        AnimationClip clip = defaultState.motion as AnimationClip;
        if (clip == null)
        {
            failures.Add($"default state '{defaultState.name}'의 motion이 AnimationClip이 아님");
            return;
        }

        if (!AnimationUtility.GetAnimationClipSettings(clip).loopTime)
        {
            failures.Add($"clip '{clip.name}'의 loopTime이 꺼져 있음");
        }

        ValidateCurvePaths(clip, animator.transform, failures);
    }

    private static void ValidateCurvePaths(AnimationClip clip, Transform animatorTransform, List<string> failures)
    {
        // 경로가 하나라도 해석되지 않으면 Animator 위치가 잘못됐거나 hierarchy가 clip과 어긋난 것이다.
        EditorCurveBinding[] bindings = AnimationUtility.GetCurveBindings(clip);
        HashSet<string> visitedPaths = new HashSet<string>();
        List<string> unresolvedPaths = new List<string>(MaxUnresolvedPathExamples);
        int unresolvedCount = 0;

        for (int i = 0; i < bindings.Length; i++)
        {
            string path = bindings[i].path;
            if (string.IsNullOrEmpty(path))
            {
                continue; // 빈 경로는 Animator 자신을 가리키므로 항상 해석된다.
            }

            if (!visitedPaths.Add(path))
            {
                continue;
            }

            if (animatorTransform.Find(path) == null)
            {
                unresolvedCount++;
                if (unresolvedPaths.Count < MaxUnresolvedPathExamples)
                {
                    unresolvedPaths.Add(path);
                }
            }
        }

        if (unresolvedCount > 0)
        {
            failures.Add(
                $"clip '{clip.name}'의 curve 경로 {unresolvedCount}개가 Animator({animatorTransform.name}) 하위에서 해석되지 않음" +
                $" (예: {string.Join(", ", unresolvedPaths)})");
        }
    }

    private static void ValidateCityColliders(List<string> failures)
    {
        Transform city = FindSceneTransform("GameArea", "City");
        if (city == null)
        {
            failures.Add("씬에 GameArea/City가 없음");
            return;
        }

        for (int i = 0; i < ColliderRequiredChildren.Length; i++)
        {
            string childName = ColliderRequiredChildren[i];
            Transform child = city.Find(childName);
            if (child == null)
            {
                failures.Add($"City 자식 '{childName}'이 없음");
                continue;
            }

            if (child.GetComponentsInChildren<MeshCollider>(true).Length == 0)
            {
                failures.Add($"City/{childName}에 MeshCollider가 없음");
            }
        }

        for (int i = 0; i < ColliderForbiddenChildren.Length; i++)
        {
            string childName = ColliderForbiddenChildren[i];
            Transform child = city.Find(childName);
            if (child == null)
            {
                failures.Add($"City 자식 '{childName}'이 없음");
                continue;
            }

            int colliderCount = child.GetComponentsInChildren<MeshCollider>(true).Length;
            if (colliderCount > 0)
            {
                failures.Add($"City/{childName}에 MeshCollider가 있으면 안 됨({colliderCount}개 발견)");
            }
        }
    }

    // HUD 프리팹 3종(GameSceneSetup.ConvergeHudPrefabs 저작)의 구조·배선을 읽기 전용으로 검증한다.
    // HudRoot: Canvas 트리(ScreenSpaceOverlay + CanvasScaler 1080x1920 match 0.5) + HudRoot 뷰 참조 전부 배선 + ResultOverlay 비활성.
    // CrowdLabel/RivalMarker: 동적 템플릿의 최소 구조(root TMP / Arrow·Count TMP 자식).
    private static void ValidateHudPrefabs(List<string> failures)
    {
        GameObject hudRootPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(HudRootPrefabPath);
        if (hudRootPrefab == null)
        {
            failures.Add($"HudRoot 프리팹이 없음: {HudRootPrefabPath}");
        }
        else
        {
            HudRoot hud = hudRootPrefab.GetComponent<HudRoot>();
            if (hud == null)
            {
                failures.Add("HudRoot 프리팹 root에 HudRoot 컴포넌트가 없음");
            }

            Canvas canvas = hudRootPrefab.GetComponent<Canvas>();
            if (canvas == null)
            {
                failures.Add("HudRoot 프리팹 root에 Canvas가 없음");
            }
            else if (canvas.renderMode != RenderMode.ScreenSpaceOverlay)
            {
                failures.Add($"HudRoot Canvas renderMode가 ScreenSpaceOverlay가 아님({canvas.renderMode})");
            }

            CanvasScaler scaler = hudRootPrefab.GetComponent<CanvasScaler>();
            if (scaler == null)
            {
                failures.Add("HudRoot 프리팹 root에 CanvasScaler가 없음");
            }
            else
            {
                if (scaler.uiScaleMode != CanvasScaler.ScaleMode.ScaleWithScreenSize)
                {
                    failures.Add("HudRoot CanvasScaler uiScaleMode가 ScaleWithScreenSize가 아님");
                }

                if (scaler.referenceResolution != HudReferenceResolution)
                {
                    failures.Add($"HudRoot CanvasScaler referenceResolution가 {HudReferenceResolution}이(가) 아님({scaler.referenceResolution})");
                }

                if (!Mathf.Approximately(scaler.matchWidthOrHeight, HudCanvasMatch))
                {
                    failures.Add($"HudRoot CanvasScaler matchWidthOrHeight가 {HudCanvasMatch}이(가) 아님({scaler.matchWidthOrHeight})");
                }
            }

            if (hud != null)
            {
                SerializedObject serialized = new SerializedObject(hud);
                RequireHudRef(serialized, "_timerText", failures);
                RequireHudRef(serialized, "_hintGo", failures);
                RequireHudRef(serialized, "_resultOverlayGo", failures);
                RequireHudRef(serialized, "_resultTitleText", failures);
                RequireHudRef(serialized, "_resultStandingsText", failures);
                RequireHudRef(serialized, "_leaderLabelLayerRect", failures);
                RequireHudRef(serialized, "_markerLayerRect", failures);
                RequireHudRefArray(serialized, "_rowGos", HudMaxTeams, failures);
                RequireHudRefArray(serialized, "_rowSwatches", HudMaxTeams, failures);
                RequireHudRefArray(serialized, "_rowCounts", HudMaxTeams, failures);
            }

            Transform resultOverlay = hudRootPrefab.transform.Find("ResultOverlay");
            if (resultOverlay == null)
            {
                failures.Add("HudRoot 프리팹에 ResultOverlay 자식이 없음");
            }
            else if (resultOverlay.gameObject.activeSelf)
            {
                failures.Add("HudRoot ResultOverlay가 기본 활성 상태(비활성으로 저작돼야 함)");
            }
        }

        GameObject crowdLabelPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(CrowdLabelPrefabPath);
        if (crowdLabelPrefab == null)
        {
            failures.Add($"CrowdLabel 프리팹이 없음: {CrowdLabelPrefabPath}");
        }
        else if (crowdLabelPrefab.GetComponent<TextMeshProUGUI>() == null)
        {
            failures.Add("CrowdLabel 프리팹 root에 TextMeshProUGUI가 없음");
        }

        GameObject rivalMarkerPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(RivalMarkerPrefabPath);
        if (rivalMarkerPrefab == null)
        {
            failures.Add($"RivalMarker 프리팹이 없음: {RivalMarkerPrefabPath}");
        }
        else
        {
            if (rivalMarkerPrefab.GetComponent<RectTransform>() == null)
            {
                failures.Add("RivalMarker 프리팹 root에 RectTransform이 없음");
            }

            RequireMarkerChildText(rivalMarkerPrefab, "Arrow", failures);
            RequireMarkerChildText(rivalMarkerPrefab, "Count", failures);
        }
    }

    private static void RequireHudRef(SerializedObject serialized, string propertyName, List<string> failures)
    {
        SerializedProperty property = serialized.FindProperty(propertyName);
        if (property == null)
        {
            failures.Add($"HudRoot 직렬화 필드 '{propertyName}'을(를) 찾지 못함");
            return;
        }

        if (property.objectReferenceValue == null)
        {
            failures.Add($"HudRoot 직렬화 필드 '{propertyName}'이(가) 프리팹에서 미배선(null)");
        }
    }

    private static void RequireHudRefArray(
        SerializedObject serialized, string propertyName, int expectedLength, List<string> failures)
    {
        SerializedProperty property = serialized.FindProperty(propertyName);
        if (property == null)
        {
            failures.Add($"HudRoot 직렬화 배열 필드 '{propertyName}'을(를) 찾지 못함");
            return;
        }

        if (!property.isArray)
        {
            failures.Add($"HudRoot 직렬화 필드 '{propertyName}'이(가) 배열이 아님");
            return;
        }

        if (property.arraySize != expectedLength)
        {
            failures.Add($"HudRoot 직렬화 배열 '{propertyName}' 길이가 {expectedLength}이(가) 아님({property.arraySize})");
            return;
        }

        for (int i = 0; i < expectedLength; i++)
        {
            if (property.GetArrayElementAtIndex(i).objectReferenceValue == null)
            {
                failures.Add($"HudRoot 직렬화 배열 '{propertyName}'[{i}]이(가) 미배선(null)");
            }
        }
    }

    private static void RequireMarkerChildText(GameObject markerPrefab, string childName, List<string> failures)
    {
        Transform child = markerPrefab.transform.Find(childName);
        if (child == null)
        {
            failures.Add($"RivalMarker 프리팹에 '{childName}' 자식이 없음");
            return;
        }

        if (child.GetComponent<TextMeshProUGUI>() == null)
        {
            failures.Add($"RivalMarker 프리팹 '{childName}'에 TextMeshProUGUI가 없음");
        }
    }

    private static void ValidateGeneratedBuildings(List<string> failures)
    {
        CityBuildingsGenerator.GenerationPlan plan;
        try
        {
            plan = CityBuildingsGenerator.CreateValidatedPlan();
        }
        catch (System.Exception exception)
        {
            failures.Add("City Buildings source signature/귀속 검증 실패: " + exception.Message);
            return;
        }

        Transform city = FindSceneTransform("GameArea", "City");
        Transform buildings = city == null ? null : city.Find(CityBuildingsGenerator.BuildingsName);
        if (buildings == null)
        {
            failures.Add("씬에 GameArea/City/Buildings가 없음");
            return;
        }

        if (buildings.GetComponent<MeshFilter>() != null ||
            buildings.GetComponent<MeshRenderer>() != null ||
            buildings.GetComponent<MeshCollider>() != null)
        {
            failures.Add("City/Buildings root에 결합 MeshFilter/MeshRenderer/MeshCollider가 남아 있음");
        }

        if (buildings.childCount != CityBuildingsGenerator.ExpectedBuildingCount)
        {
            failures.Add(
                $"City/Buildings 개별 건물 수가 {buildings.childCount}개임(기대 {CityBuildingsGenerator.ExpectedBuildingCount})");
            return;
        }

        int totalVertices = 0;
        int totalTriangles = 0;
        for (int i = 0; i < buildings.childCount; i++)
        {
            Transform child = buildings.GetChild(i);
            string expectedName = CityBuildingsGenerator.GetBuildingAssetName(i);
            if (child.name != expectedName ||
                child.localPosition != Vector3.zero ||
                child.localRotation != Quaternion.identity ||
                child.localScale != Vector3.one)
            {
                failures.Add($"City/Buildings/{expectedName} 이름/순서/local transform 불일치");
                continue;
            }

            string prefabPath = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(child.gameObject);
            if (prefabPath != CityBuildingsGenerator.GetPrefabAssetPath(i))
            {
                failures.Add($"{expectedName}이 기대 generated prefab instance가 아님");
            }

            MeshFilter filter = child.GetComponent<MeshFilter>();
            MeshRenderer renderer = child.GetComponent<MeshRenderer>();
            MeshCollider collider = child.GetComponent<MeshCollider>();
            if (filter == null || filter.sharedMesh == null || renderer == null || collider == null)
            {
                failures.Add($"{expectedName}에 MeshFilter/MeshRenderer/MeshCollider가 모두 있지 않음");
                continue;
            }

            Mesh mesh = filter.sharedMesh;
            if (AssetDatabase.GetAssetPath(mesh) != CityBuildingsGenerator.GetMeshAssetPath(i) ||
                collider.sharedMesh != mesh)
            {
                failures.Add($"{expectedName}의 render/collider mesh 참조가 generated mesh와 다름");
            }

            Material[] materials = renderer.sharedMaterials;
            if (materials.Length != 1 || materials[0] != plan.SourceMaterial)
            {
                failures.Add($"{expectedName}이 원본 CityAtlas sharedMaterial 하나를 사용하지 않음");
            }

            if (renderer.shadowCastingMode != UnityEngine.Rendering.ShadowCastingMode.On)
            {
                failures.Add($"{expectedName}의 Shadow Casting Mode가 On이 아님");
            }

            if (mesh.normals.Length != mesh.vertexCount ||
                mesh.tangents.Length != mesh.vertexCount ||
                mesh.uv.Length != mesh.vertexCount ||
                mesh.subMeshCount != 1 ||
                mesh.GetTopology(0) != MeshTopology.Triangles)
            {
                failures.Add($"{expectedName}의 vertex channel/submesh 구조가 불완전함");
            }

            totalVertices += mesh.vertexCount;
            totalTriangles += (int)mesh.GetIndexCount(0) / 3;
        }

        if (totalVertices != CityBuildingsGenerator.ExpectedSourceVertexCount ||
            totalTriangles != CityBuildingsGenerator.ExpectedSourceTriangleCount)
        {
            failures.Add(
                $"개별 건물 geometry 합계 불일치: vertices={totalVertices}, triangles={totalTriangles}");
        }
    }

    private static void ValidateCrowdCountTextStyleAsset(List<string> failures)
    {
        Object mainAsset = AssetDatabase.LoadMainAssetAtPath(CrowdCountTextStyleAssetPath);
        if (mainAsset == null)
        {
            failures.Add($"CrowdCountTextStyle 에셋이 없음({CrowdCountTextStyleAssetPath})");
            return;
        }

        TMPTextStyleSO style = mainAsset as TMPTextStyleSO;
        if (style == null)
        {
            failures.Add(
                $"CrowdCountTextStyle 에셋 type이 TMPTextStyleSO가 아님({mainAsset.GetType().Name})");
            return;
        }

        Check(
            style.OutlineWidth >= 0f && style.OutlineWidth <= 1f,
            $"CrowdCountTextStyle.OutlineWidth({style.OutlineWidth})는 0..1이어야 함",
            failures);
    }

    private static void ValidateConfigAsset(List<string> failures)
    {
        GameConfigSO config = AssetDatabase.LoadAssetAtPath<GameConfigSO>(ConfigAssetPath);
        if (config == null)
        {
            failures.Add($"GameConfig 에셋이 없음({ConfigAssetPath})");
            return;
        }

        bool colorsValid = config.TeamColors != null && config.TeamColors.Count == 4;
        if (!colorsValid)
        {
            failures.Add("GameConfig.TeamColors 길이가 4가 아님");
        }

        ValidateTeamMaterials(config, colorsValid, failures);
        ValidateConfigRanges(config, failures);
    }

    private static void ValidateTeamMaterials(GameConfigSO config, bool colorsValid, List<string> failures)
    {
        if (config.TeamMaterials == null || config.TeamMaterials.Count != 5)
        {
            failures.Add("GameConfig.TeamMaterials 길이가 5가 아님");
            return;
        }

        for (int i = 0; i < config.TeamMaterials.Count; i++)
        {
            Material material = config.TeamMaterials[i];
            if (material == null)
            {
                failures.Add($"GameConfig.TeamMaterials[{i}]가 비어 있음");
                continue;
            }

            if (material.shader == null || material.shader.name != UrpLitShaderName)
            {
                failures.Add($"TeamMaterials[{i}] '{material.name}'의 shader가 '{UrpLitShaderName}'이 아님");
                continue;
            }

            if (!material.HasProperty(BaseColorProperty))
            {
                failures.Add($"TeamMaterials[{i}] '{material.name}'에 {BaseColorProperty} 속성이 없음");
                continue;
            }

            // [0]=플레이어, [1..3]=라이벌은 TeamColors, [4]=중립은 NeutralColor와 일치해야 한다.
            if (i == 4)
            {
                CheckMaterialColor(material, config.NeutralColor, i, failures);
            }
            else if (colorsValid)
            {
                CheckMaterialColor(material, config.TeamColors[i], i, failures);
            }
        }
    }

    private static void CheckMaterialColor(Material material, Color expected, int index, List<string> failures)
    {
        Color actual = material.GetColor(BaseColorProperty);
        bool matches =
            Mathf.Abs(actual.r - expected.r) <= ColorTolerance &&
            Mathf.Abs(actual.g - expected.g) <= ColorTolerance &&
            Mathf.Abs(actual.b - expected.b) <= ColorTolerance &&
            Mathf.Abs(actual.a - expected.a) <= ColorTolerance;
        if (!matches)
        {
            failures.Add(
                $"TeamMaterials[{index}] '{material.name}'의 {BaseColorProperty}({actual})가 설정 색상({expected})과 다름");
        }
    }

    private static void ValidateConfigRanges(GameConfigSO config, List<string> failures)
    {
        // GameConfigSO.OnValidate의 clamp 규칙을 그대로 검사 기준으로 삼는다.
        Check(config.MatchSeconds >= 1f, $"MatchSeconds({config.MatchSeconds})는 1 이상이어야 함", failures);
        Check(config.RivalCount >= 1 && config.RivalCount <= 3, $"RivalCount({config.RivalCount})는 1..3이어야 함", failures);
        Check(config.NeutralCount >= 0, $"NeutralCount({config.NeutralCount})는 0 이상이어야 함", failures);

        Check(config.LeaderSpeed > 0f, $"LeaderSpeed({config.LeaderSpeed})는 0보다 커야 함", failures);
        Check(config.FollowerMaxSpeed > 0f, $"FollowerMaxSpeed({config.FollowerMaxSpeed})는 0보다 커야 함", failures);
        Check(config.TurnRateDegPerSec > 0f, $"TurnRateDegPerSec({config.TurnRateDegPerSec})는 0보다 커야 함", failures);
        Check(config.SlotSpacing > 0f, $"SlotSpacing({config.SlotSpacing})은 0보다 커야 함", failures);
        Check(config.SeparationRadius > 0f, $"SeparationRadius({config.SeparationRadius})는 0보다 커야 함", failures);
        Check(config.SeparationPush >= 0f, $"SeparationPush({config.SeparationPush})는 0 이상이어야 함", failures);

        Check(config.Sim.RecruitRadius > 0f, $"Sim.RecruitRadius({config.Sim.RecruitRadius})는 0보다 커야 함", failures);
        Check(config.Sim.CombatRadius > 0f, $"Sim.CombatRadius({config.Sim.CombatRadius})는 0보다 커야 함", failures);
        Check(config.Sim.ConvertPerSecond >= 0f, $"Sim.ConvertPerSecond({config.Sim.ConvertPerSecond})는 0 이상이어야 함", failures);
        Check(config.Sim.ConvertPerSecondPerMember >= 0f, $"Sim.ConvertPerSecondPerMember({config.Sim.ConvertPerSecondPerMember})는 0 이상이어야 함", failures);
        Check(config.Sim.PairNormalizer >= 1, $"Sim.PairNormalizer({config.Sim.PairNormalizer})는 1 이상이어야 함", failures);

        Check(config.AiDecideInterval > 0f, $"AiDecideInterval({config.AiDecideInterval})은 0보다 커야 함", failures);
        Check(config.FleeSizeRatio > 0f, $"FleeSizeRatio({config.FleeSizeRatio})는 0보다 커야 함", failures);
        Check(config.HuntSizeRatio > 0f, $"HuntSizeRatio({config.HuntSizeRatio})는 0보다 커야 함", failures);
        Check(config.HuntMinCount >= 1, $"HuntMinCount({config.HuntMinCount})는 1 이상이어야 함", failures);
        Check(config.AiVisionRadius > 0f, $"AiVisionRadius({config.AiVisionRadius})는 0보다 커야 함", failures);
        Check(config.WallProbeDistance > 0f, $"WallProbeDistance({config.WallProbeDistance})는 0보다 커야 함", failures);

        Check(config.NeutralWanderSpeed >= 0f, $"NeutralWanderSpeed({config.NeutralWanderSpeed})는 0 이상이어야 함", failures);
        Check(config.WanderRepickMinSeconds > 0f, $"WanderRepickMinSeconds({config.WanderRepickMinSeconds})는 0보다 커야 함", failures);
        Check(
            config.WanderRepickMaxSeconds >= config.WanderRepickMinSeconds,
            $"WanderRepickMaxSeconds({config.WanderRepickMaxSeconds})는 WanderRepickMinSeconds({config.WanderRepickMinSeconds}) 이상이어야 함",
            failures);

        Check(config.CamPitchDeg > 0f && config.CamPitchDeg < 90f, $"CamPitchDeg({config.CamPitchDeg})는 0~90 사이여야 함", failures);
        Check(config.CamBaseDistance > 0f, $"CamBaseDistance({config.CamBaseDistance})는 0보다 커야 함", failures);
        Check(config.CamDistancePerSqrtCount >= 0f, $"CamDistancePerSqrtCount({config.CamDistancePerSqrtCount})는 0 이상이어야 함", failures);
        Check(
            config.CamMaxDistance >= config.CamBaseDistance,
            $"CamMaxDistance({config.CamMaxDistance})는 CamBaseDistance({config.CamBaseDistance}) 이상이어야 함",
            failures);
        Check(config.CamFollowSmoothTime > 0f, $"CamFollowSmoothTime({config.CamFollowSmoothTime})은 0보다 커야 함", failures);
    }

    private static void Check(bool condition, string reason, List<string> failures)
    {
        if (!condition)
        {
            failures.Add(reason);
        }
    }

    private static Transform FindSceneTransform(string rootName, string childPath)
    {
        // 비활성 오브젝트도 찾아야 하므로 root 목록에서 직접 내려간다.
        GameObject[] roots = SceneManager.GetActiveScene().GetRootGameObjects();
        for (int i = 0; i < roots.Length; i++)
        {
            if (roots[i].name == rootName)
            {
                return roots[i].transform.Find(childPath);
            }
        }

        return null;
    }
}

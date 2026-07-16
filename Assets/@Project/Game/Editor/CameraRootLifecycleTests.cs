using System;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

public sealed class CameraRootLifecycleTests
{
    private const int BuildingCount = 37;
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;

    [Test]
    public void ReinitializeAndShutdown_RestoreMaterialsAndResetSessionState()
    {
        GameObject rootGo = new GameObject("CameraRoot_Test");
        CameraRoot root = rootGo.AddComponent<CameraRoot>();
        GameObject cameraOneGo = new GameObject("CameraOne");
        Camera cameraOne = cameraOneGo.AddComponent<Camera>();
        GameObject cameraTwoGo = new GameObject("CameraTwo");
        Camera cameraTwo = cameraTwoGo.AddComponent<Camera>();
        GameObject target = new GameObject("Target");
        GameConfigSO config = ScriptableObject.CreateInstance<GameConfigSO>();
        Material originalOne = new Material(Shader.Find("Universal Render Pipeline/Lit"));
        Material originalTwo = new Material(Shader.Find("Universal Render Pipeline/Lit"));
        Material occluded = new Material(Shader.Find("Universal Render Pipeline/Lit"));
        occluded.SetColor("_BaseColor", new Color(0.8f, 0.7f, 0.6f, 0.25f));
        occluded.SetFloat("_Surface", 1f);
        occluded.SetFloat("_Blend", 0f);
        occluded.SetFloat("_ZWrite", 0f);
        occluded.SetFloat("_AlphaClip", 0f);
        occluded.renderQueue = 3000;
        occluded.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
        occluded.SetShaderPassEnabled("ShadowCaster", false);
        string sourceMaterialJson = EditorJsonUtility.ToJson(occluded);
        GameObject cityOne = CreateCity("CityOne", originalOne);
        GameObject cityTwo = CreateCity("CityTwo", originalTwo);

        try
        {
            // buildingOccludedMaterial은 이제 CameraRoot 프리팹의 직렬화 필드다. AddComponent 경로에서는 프리팹 직렬화가
            // 없으므로 Initialize 이전에 명시적으로 주입한다(재초기화 사이에도 유지되므로 한 번만 설정한다).
            SetPrivate(root, "buildingOccludedMaterial", occluded);
            root.Initialize(cameraOne, config, cityOne.transform);
            Material runtimeOne = GetPrivate<Material>(root, "_runtimeOccludedMaterial");
            AssertRuntimeMaterial(runtimeOne, occluded);
            Assert.That(EditorJsonUtility.ToJson(occluded), Is.EqualTo(sourceMaterialJson));

            root.SetTarget(target.transform);
            SetPrivate(root, "_latched", true);
            SetPrivate(root, "_latchRequested", true);
            SetPrivate(root, "_playerCount", 99);
            SetPrivate(root, "_followVelocity", Vector3.one);
            SetFirstBuildingOccluded(root, true);
            Renderer oldRenderer = cityOne.transform.Find("Buildings/Building_00").GetComponent<Renderer>();
            Assert.That(oldRenderer.sharedMaterial, Is.SameAs(runtimeOne));

            root.Initialize(cameraTwo, config, cityTwo.transform);
            Material runtimeTwo = GetPrivate<Material>(root, "_runtimeOccludedMaterial");

            Assert.That(oldRenderer.sharedMaterial, Is.SameAs(originalOne));
            Assert.That(runtimeOne == null, Is.True);
            AssertRuntimeMaterial(runtimeTwo, occluded);
            Assert.That(runtimeTwo, Is.Not.SameAs(runtimeOne));
            Assert.That(EditorJsonUtility.ToJson(occluded), Is.EqualTo(sourceMaterialJson));
            Assert.That(GetPrivate<Camera>(root, "_camera"), Is.SameAs(cameraTwo));
            Assert.That(GetPrivate<Transform>(root, "_target"), Is.Null);
            Assert.That(GetPrivate<bool>(root, "_latched"), Is.False);
            Assert.That(GetPrivate<bool>(root, "_latchRequested"), Is.False);
            Assert.That(GetPrivate<int>(root, "_playerCount"), Is.EqualTo(1));
            Assert.That(GetPrivate<Vector3>(root, "_followVelocity"), Is.EqualTo(Vector3.zero));
            Assert.That(GetCollectionCount(root, "_buildingByCollider"), Is.EqualTo(BuildingCount));
            Assert.That(GetCollectionCount(root, "_activeOccluders"), Is.Zero);

            SetFirstBuildingOccluded(root, true);
            Renderer newRenderer = cityTwo.transform.Find("Buildings/Building_00").GetComponent<Renderer>();
            Assert.That(newRenderer.sharedMaterial, Is.SameAs(runtimeTwo));

            root.Shutdown();

            Assert.That(newRenderer.sharedMaterial, Is.SameAs(originalTwo));
            Assert.That(runtimeTwo == null, Is.True);
            Assert.That(GetPrivate<Material>(root, "_runtimeOccludedMaterial"), Is.Null);
            Assert.That(EditorJsonUtility.ToJson(occluded), Is.EqualTo(sourceMaterialJson));
            Assert.That(GetPrivate<Camera>(root, "_camera"), Is.Null);
            Assert.That(GetPrivate<GameConfigSO>(root, "_config"), Is.Null);
            Assert.That(GetPrivate<Transform>(root, "_target"), Is.Null);
            Assert.That(GetPrivate<object>(root, "_buildingByCollider"), Is.Null);
            Assert.That(GetPrivate<object>(root, "_buildingStates"), Is.Null);
            Assert.That(GetPrivate<object>(root, "_activeOccluders"), Is.Null);
            Assert.That(GetPrivate<bool>(root, "_subscribed"), Is.False);
            Assert.That(GetPrivate<bool>(root, "_latched"), Is.False);
            Assert.That(GetPrivate<bool>(root, "_latchRequested"), Is.False);
            Assert.That(GetPrivate<int>(root, "_playerCount"), Is.EqualTo(1));
            Assert.That(GetPrivate<Vector3>(root, "_followVelocity"), Is.EqualTo(Vector3.zero));
        }
        finally
        {
            root.Shutdown();
            UnityEngine.Object.DestroyImmediate(cityOne);
            UnityEngine.Object.DestroyImmediate(cityTwo);
            UnityEngine.Object.DestroyImmediate(target);
            UnityEngine.Object.DestroyImmediate(cameraOneGo);
            UnityEngine.Object.DestroyImmediate(cameraTwoGo);
            UnityEngine.Object.DestroyImmediate(rootGo);
            UnityEngine.Object.DestroyImmediate(config);
            UnityEngine.Object.DestroyImmediate(originalOne);
            UnityEngine.Object.DestroyImmediate(originalTwo);
            UnityEngine.Object.DestroyImmediate(occluded);
        }
    }

    [Test]
    public void FeatureRootPrefabs_CarryMovedSerializedRefs()
    {
        // Batch B: 단일 소비자 asset이 GSC Init 체인 대신 소비 feature root 프리팹에 직렬화 저작됐는지 검증한다(읽기 전용).
        AssertPrefabRefPath(
            "Assets/@Project/Game/Resources/Prefabs/CameraRoot.prefab",
            typeof(CameraRoot),
            "buildingOccludedMaterial",
            "Assets/@Project/City/Materials/City_Occluded.mat");
        AssertPrefabRefPath(
            "Assets/@Project/Hud/Resources/UI/HudRoot.prefab",
            typeof(HudRoot),
            "_crowdCountTextStyle",
            "Assets/@Project/Hud/CrowdCountTextStyle.asset");
    }

    private static void AssertPrefabRefPath(
        string prefabPath, Type componentType, string fieldName, string expectedAssetPath)
    {
        GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
        Assert.That(prefab, Is.Not.Null, prefabPath);
        Component component = prefab.GetComponent(componentType);
        Assert.That(component, Is.Not.Null, componentType.Name);
        SerializedObject serialized = new SerializedObject(component);
        SerializedProperty property = serialized.FindProperty(fieldName);
        Assert.That(property, Is.Not.Null, fieldName);
        Assert.That(property.objectReferenceValue, Is.Not.Null, $"{componentType.Name}.{fieldName} 미배선");
        Assert.That(AssetDatabase.GetAssetPath(property.objectReferenceValue), Is.EqualTo(expectedAssetPath));
    }

    private static void AssertRuntimeMaterial(Material runtimeMaterial, Material source)
    {
        Assert.That(runtimeMaterial, Is.Not.Null);
        Assert.That(runtimeMaterial, Is.Not.SameAs(source));
        Assert.That(runtimeMaterial.shader, Is.SameAs(source.shader));
        Assert.That(runtimeMaterial.GetColor("_BaseColor"), Is.EqualTo(source.GetColor("_BaseColor")));
        Assert.That(runtimeMaterial.GetFloat("_Surface"), Is.EqualTo(source.GetFloat("_Surface")));
        Assert.That(runtimeMaterial.GetFloat("_Blend"), Is.EqualTo(source.GetFloat("_Blend")));
        Assert.That(runtimeMaterial.GetFloat("_ZWrite"), Is.EqualTo(source.GetFloat("_ZWrite")));
        Assert.That(runtimeMaterial.GetFloat("_AlphaClip"), Is.EqualTo(source.GetFloat("_AlphaClip")));
        Assert.That(runtimeMaterial.renderQueue, Is.EqualTo(source.renderQueue));
        Assert.That(runtimeMaterial.IsKeywordEnabled("_SURFACE_TYPE_TRANSPARENT"), Is.True);
        Assert.That(runtimeMaterial.GetShaderPassEnabled("ShadowCaster"), Is.True);
        Assert.That(source.GetShaderPassEnabled("ShadowCaster"), Is.False);
        Assert.That(runtimeMaterial.hideFlags & HideFlags.DontSave, Is.EqualTo(HideFlags.DontSave));
    }

    private static GameObject CreateCity(string name, Material originalMaterial)
    {
        GameObject city = new GameObject(name);
        GameObject buildings = new GameObject("Buildings");
        buildings.transform.SetParent(city.transform, false);
        for (int i = 0; i < BuildingCount; i++)
        {
            GameObject building = new GameObject("Building_" + i.ToString("D2"));
            building.transform.SetParent(buildings.transform, false);
            MeshRenderer renderer = building.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = originalMaterial;
            building.AddComponent<BoxCollider>();
        }

        return city;
    }

    private static void SetFirstBuildingOccluded(CameraRoot root, bool occluded)
    {
        Array states = GetPrivate<Array>(root, "_buildingStates");
        object state = states.GetValue(0);
        MethodInfo setOccluded = state.GetType().GetMethod("SetOccluded", PrivateInstance);
        setOccluded.Invoke(state, new object[] { occluded });

        object active = GetPrivate<object>(root, "_activeOccluders");
        MethodInfo add = active.GetType().GetMethod("Add", BindingFlags.Instance | BindingFlags.Public);
        add.Invoke(active, new[] { state });
    }

    private static int GetCollectionCount(CameraRoot root, string fieldName)
    {
        object collection = GetPrivate<object>(root, fieldName);
        return (int)collection.GetType().GetProperty("Count").GetValue(collection);
    }

    private static T GetPrivate<T>(CameraRoot root, string fieldName)
    {
        FieldInfo field = typeof(CameraRoot).GetField(fieldName, PrivateInstance);
        Assert.That(field, Is.Not.Null, fieldName);
        return (T)field.GetValue(root);
    }

    private static void SetPrivate<T>(CameraRoot root, string fieldName, T value)
    {
        FieldInfo field = typeof(CameraRoot).GetField(fieldName, PrivateInstance);
        Assert.That(field, Is.Not.Null, fieldName);
        field.SetValue(root, value);
    }
}

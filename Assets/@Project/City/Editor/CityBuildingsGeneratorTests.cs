using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

public sealed class CityBuildingsGeneratorTests
{
    private const string GameScenePath = "Assets/@Project/Scenes/GameScene.unity";
    private const string CrowdCountTextStylePath = "Assets/@Project/Hud/CrowdCountTextStyle.asset";

    [Test]
    public void CurrentSource_ProducesExpectedUniqueBuildingPlan()
    {
        CityBuildingsGenerator.GenerationPlan plan = CityBuildingsGenerator.CreateValidatedPlan();

        Assert.That(plan.SourceVertexCount, Is.EqualTo(CityBuildingsGenerator.ExpectedSourceVertexCount));
        Assert.That(plan.SourceTriangleCount, Is.EqualTo(CityBuildingsGenerator.ExpectedSourceTriangleCount));
        Assert.That(plan.ConnectedComponentCount, Is.EqualTo(CityBuildingsGenerator.ExpectedConnectedComponentCount));
        Assert.That(plan.Groups, Has.Length.EqualTo(CityBuildingsGenerator.ExpectedBuildingCount));

        int totalVertices = 0;
        int totalTriangles = 0;
        for (int i = 0; i < plan.Groups.Length; i++)
        {
            CityBuildingsGenerator.BuildingGroup group = plan.Groups[i];
            Assert.That(group.Index, Is.EqualTo(i));
            Assert.That(group.Normals, Has.Length.EqualTo(group.Vertices.Length));
            Assert.That(group.Tangents, Has.Length.EqualTo(group.Vertices.Length));
            Assert.That(group.Uvs, Has.Length.EqualTo(group.Vertices.Length));
            Assert.That(group.Triangles.Length % 3, Is.Zero);
            totalVertices += group.Vertices.Length;
            totalTriangles += group.Triangles.Length / 3;
        }

        Assert.That(totalVertices, Is.EqualTo(CityBuildingsGenerator.ExpectedSourceVertexCount));
        Assert.That(totalTriangles, Is.EqualTo(CityBuildingsGenerator.ExpectedSourceTriangleCount));
    }

    [Test]
    public void GeneratedAssets_PreservePlanChannelsWindingAndPrefabStructure()
    {
        CityBuildingsGenerator.GenerationPlan plan = CityBuildingsGenerator.CreateValidatedPlan();

        for (int i = 0; i < plan.Groups.Length; i++)
        {
            CityBuildingsGenerator.BuildingGroup group = plan.Groups[i];
            Mesh mesh = AssetDatabase.LoadAssetAtPath<Mesh>(CityBuildingsGenerator.GetMeshAssetPath(i));
            Assert.That(mesh, Is.Not.Null, CityBuildingsGenerator.GetMeshAssetPath(i));
            CollectionAssert.AreEqual(group.Vertices, mesh.vertices);
            CollectionAssert.AreEqual(group.Normals, mesh.normals);
            CollectionAssert.AreEqual(group.Tangents, mesh.tangents);
            CollectionAssert.AreEqual(group.Uvs, mesh.uv);
            CollectionAssert.AreEqual(group.Triangles, mesh.GetIndices(0));

            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(
                CityBuildingsGenerator.GetPrefabAssetPath(i));
            Assert.That(prefab, Is.Not.Null, CityBuildingsGenerator.GetPrefabAssetPath(i));
            Assert.That(prefab.transform.childCount, Is.Zero);

            MeshFilter filter = prefab.GetComponent<MeshFilter>();
            MeshRenderer renderer = prefab.GetComponent<MeshRenderer>();
            MeshCollider collider = prefab.GetComponent<MeshCollider>();
            Assert.That(filter, Is.Not.Null);
            Assert.That(renderer, Is.Not.Null);
            Assert.That(collider, Is.Not.Null);
            Assert.That(filter.sharedMesh, Is.SameAs(mesh));
            Assert.That(collider.sharedMesh, Is.SameAs(mesh));
            Assert.That(renderer.sharedMaterial, Is.SameAs(plan.SourceMaterial));
            Assert.That(renderer.shadowCastingMode, Is.EqualTo(ShadowCastingMode.On));
        }

        Material occluded = AssetDatabase.LoadAssetAtPath<Material>(
            CityBuildingsGenerator.OccludedMaterialPath);
        Assert.That(occluded, Is.Not.Null);
        Assert.That(occluded.shader.name, Is.EqualTo("Universal Render Pipeline/Lit"));
        Assert.That(occluded.GetFloat("_Surface"), Is.EqualTo(1f));
        Assert.That(occluded.GetFloat("_Blend"), Is.EqualTo(0f));
        Assert.That(occluded.GetFloat("_ZWrite"), Is.EqualTo(0f));
        Assert.That(occluded.GetFloat("_AlphaClip"), Is.EqualTo(0f));
        Assert.That(occluded.GetColor("_BaseColor").a, Is.EqualTo(0.25f).Within(0.0001f));
        Assert.That(occluded.renderQueue, Is.EqualTo(3000));
        Assert.That(occluded.IsKeywordEnabled("_SURFACE_TYPE_TRANSPARENT"), Is.True);
        Assert.That(occluded.IsKeywordEnabled("_ALPHATEST_ON"), Is.False);
        Assert.That(occluded.FindPass("ShadowCaster"), Is.GreaterThanOrEqualTo(0));
        Assert.That(occluded.GetShaderPassEnabled("ShadowCaster"), Is.False);

        Assert.That(plan.SourceMaterial.GetFloat("_Surface"), Is.EqualTo(0f));
        Assert.That(plan.SourceMaterial.GetColor("_BaseColor").a, Is.EqualTo(1f));
        Assert.That(plan.SourceMaterial.GetShaderPassEnabled("ShadowCaster"), Is.True);
    }

    [Test]
    public void Preflight_InvalidShaderSourceAndPlan_DoNotWriteGeneratedAssets()
    {
        Dictionary<string, byte[]> before = CaptureGeneratedFiles();
        CityBuildingsGenerator.GenerationPlan invalidShaderPlan = CityBuildingsGenerator.CreateValidatedPlan();
        Material invalidMaterial = new Material(Shader.Find("Unlit/Color"));
        invalidShaderPlan.SourceMaterial = invalidMaterial;
        try
        {
            Assert.Throws<InvalidOperationException>(
                () => CityBuildingsGenerator.PreflightConvergence(invalidShaderPlan));
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(invalidMaterial);
        }

        AssertGeneratedFilesEqual(before);

        CityBuildingsGenerator.GenerationPlan invalidSourcePlan = CityBuildingsGenerator.CreateValidatedPlan();
        Mesh invalidSource = UnityEngine.Object.Instantiate(invalidSourcePlan.SourceMesh);
        invalidSource.SetUVs(4, new Vector2[invalidSource.vertexCount]);
        invalidSourcePlan.SourceMesh = invalidSource;
        try
        {
            Assert.Throws<InvalidOperationException>(
                () => CityBuildingsGenerator.PreflightConvergence(invalidSourcePlan));
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(invalidSource);
        }

        AssertGeneratedFilesEqual(before);

        CityBuildingsGenerator.GenerationPlan invalidGeneratedPlan = CityBuildingsGenerator.CreateValidatedPlan();
        invalidGeneratedPlan.Groups[0].Uvs = Array.Empty<Vector2>();
        Assert.Throws<InvalidOperationException>(
            () => CityBuildingsGenerator.PreflightConvergence(invalidGeneratedPlan));
        AssertGeneratedFilesEqual(before);
    }

    [Test]
    public void ScenePreflight_ForeignNamedChildren_FailsWithoutDeletingOrWriting()
    {
        AssertRejectedHierarchyIsUntouched(buildings =>
        {
            for (int i = 0; i < CityBuildingsGenerator.ExpectedBuildingCount; i++)
            {
                GameObject foreign = new GameObject(CityBuildingsGenerator.GetBuildingAssetName(i));
                foreign.transform.SetParent(buildings, false);
            }
        });
    }

    [Test]
    public void ScenePreflight_DuplicatePrefabChild_FailsWithoutDeletingOrWriting()
    {
        AssertRejectedHierarchyIsUntouched(buildings =>
        {
            AddGeneratedPrefabChildren(buildings);
            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(
                CityBuildingsGenerator.GetPrefabAssetPath(0));
            GameObject duplicate = (GameObject)PrefabUtility.InstantiatePrefab(prefab, buildings);
            duplicate.name = CityBuildingsGenerator.GetBuildingAssetName(0);
        });
    }

    [Test]
    public void ScenePreflight_OverriddenPrefabChild_FailsWithoutDeletingOrWriting()
    {
        AssertRejectedHierarchyIsUntouched(buildings =>
        {
            AddGeneratedPrefabChildren(buildings);
            buildings.GetChild(0).localPosition = Vector3.right;
        });
    }

    [Test]
    public void ScenePreflight_ForeignCombinedMesh_FailsWithoutDeletingOrWriting()
    {
        AssertRejectedCombinedIdentityIsUntouched(true);
    }

    [Test]
    public void ScenePreflight_ForeignCombinedMaterial_FailsWithoutDeletingOrWriting()
    {
        AssertRejectedCombinedIdentityIsUntouched(false);
    }

    [Test]
    public void GeneratedAssetTransaction_DisposeRestoresExactFiles()
    {
        Dictionary<string, byte[]> before = CaptureGeneratedFiles();
        CityBuildingsGenerator.GenerationPlan plan = CityBuildingsGenerator.CreateValidatedPlan();
        plan.Groups[0].Normals[0] = -plan.Groups[0].Normals[0];
        List<string> changed = new List<string>();
        List<string> unchanged = new List<string>();

        CityBuildingsGenerator.GeneratedAssetsTransaction transaction =
            CityBuildingsGenerator.BeginConvergeAssets(plan, changed, unchanged);
        try
        {
            Assert.That(changed, Is.Not.Empty);
        }
        finally
        {
            transaction.Dispose();
        }

        AssertGeneratedFilesEqual(before);
    }

    [Test]
    public void SceneConvergence_UndoRollbackRestoresCombinedHierarchy()
    {
        Scene previewScene = EditorSceneManager.NewPreviewScene();
        GetSourceBuildings(out Mesh combinedMesh, out Material combinedMaterial);
        try
        {
            GameObject city = new GameObject("City");
            SceneManager.MoveGameObjectToScene(city, previewScene);
            GameObject buildingsGo = new GameObject(CityBuildingsGenerator.BuildingsName);
            buildingsGo.transform.SetParent(city.transform, false);
            MeshFilter filter = buildingsGo.AddComponent<MeshFilter>();
            filter.sharedMesh = combinedMesh;
            buildingsGo.AddComponent<MeshRenderer>().sharedMaterial = combinedMaterial;
            buildingsGo.AddComponent<MeshCollider>().sharedMesh = combinedMesh;

            CityBuildingsGenerator.ScenePlan scenePlan =
                CityBuildingsGenerator.CreateValidatedScenePlan(city.transform);
            CityBuildingsGenerator.GeneratedAssets assets = LoadGeneratedAssets();
            List<string> changed = new List<string>();
            List<string> unchanged = new List<string>();

            Undo.IncrementCurrentGroup();
            int undoGroup = Undo.GetCurrentGroup();
            try
            {
                Assert.That(
                    CityBuildingsGenerator.ConvergeScene(scenePlan, assets, changed, unchanged),
                    Is.True);
                Assert.That(buildingsGo.transform.childCount,
                    Is.EqualTo(CityBuildingsGenerator.ExpectedBuildingCount));
                Assert.That(buildingsGo.GetComponent<MeshFilter>(), Is.Null);
                Assert.That(buildingsGo.GetComponent<MeshRenderer>(), Is.Null);
                Assert.That(buildingsGo.GetComponent<MeshCollider>(), Is.Null);
            }
            finally
            {
                Undo.RevertAllDownToGroup(undoGroup);
            }

            Assert.That(buildingsGo.transform.childCount, Is.Zero);
            Assert.That(buildingsGo.GetComponent<MeshFilter>(), Is.Not.Null);
            Assert.That(buildingsGo.GetComponent<MeshRenderer>(), Is.Not.Null);
            Assert.That(buildingsGo.GetComponent<MeshCollider>(), Is.Not.Null);
        }
        finally
        {
            EditorSceneManager.ClosePreviewScene(previewScene);
        }
    }

    [Test]
    public void LocalSetup_AfterSceneSaveFailure_RestoresSceneAndGeneratedFiles()
    {
        EditorSceneManager.OpenScene(GameScenePath, OpenSceneMode.Single);
        Assert.That(SceneManager.GetActiveScene().isDirty, Is.False);
        byte[] sceneBefore = File.ReadAllBytes(Path.GetFullPath(GameScenePath));
        byte[] sceneMetaBefore = File.ReadAllBytes(Path.GetFullPath(GameScenePath + ".meta"));
        Dictionary<string, byte[]> generatedBefore = CaptureGeneratedFiles();

        GameSceneSetup.AfterLocalSceneSaveForTests = controller =>
        {
            SerializedObject serialized = new SerializedObject(controller);
            serialized.FindProperty("buildingOccludedMaterial").objectReferenceValue = null;
            serialized.ApplyModifiedProperties();
            Assert.That(EditorSceneManager.SaveScene(SceneManager.GetActiveScene()), Is.True);
            throw new InvalidOperationException("Injected failure after scene save");
        };

        try
        {
            InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
                () => GameSceneSetup.ApplyCityBuildingsForTests());
            Assert.That(exception.Message, Does.Contain("Injected failure"));
        }
        finally
        {
            GameSceneSetup.AfterLocalSceneSaveForTests = null;
        }

        CollectionAssert.AreEqual(sceneBefore, File.ReadAllBytes(Path.GetFullPath(GameScenePath)));
        CollectionAssert.AreEqual(sceneMetaBefore, File.ReadAllBytes(Path.GetFullPath(GameScenePath + ".meta")));
        AssertGeneratedFilesEqual(generatedBefore);

        GameObject controllerGo = GameObject.Find("GameSceneController");
        SerializedObject restored = new SerializedObject(controllerGo.GetComponent<GameSceneController>());
        UnityEngine.Object material = restored.FindProperty("buildingOccludedMaterial").objectReferenceValue;
        Assert.That(AssetDatabase.GetAssetPath(material), Is.EqualTo(CityBuildingsGenerator.OccludedMaterialPath));
        Assert.That(SceneManager.GetActiveScene().isDirty, Is.False);
    }

    [TestCase("controller")]
    [TestCase("cityRoot")]
    [TestCase("mainCamera")]
    [TestCase("material")]
    [TestCase("crowdCountTextStyle")]
    public void FullControllerConvergence_RecoversWithoutCityPreflightDeadlock(string missingReference)
    {
        EditorSceneManager.OpenScene(GameScenePath, OpenSceneMode.Single);
        Undo.IncrementCurrentGroup();
        int undoGroup = Undo.GetCurrentGroup();
        try
        {
            GameObject controllerGo = GameObject.Find("GameSceneController");
            GameSceneController originalController = controllerGo.GetComponent<GameSceneController>();
            if (missingReference == "controller")
            {
                Undo.DestroyObjectImmediate(controllerGo);
                Assert.That(GameSceneSetup.LocalCityPreflightFindsControllerForTests(), Is.False);
            }
            else
            {
                SerializedObject serialized = new SerializedObject(originalController);
                string propertyName = missingReference == "material"
                    ? "buildingOccludedMaterial"
                    : missingReference;
                serialized.FindProperty(propertyName).objectReferenceValue = null;
                serialized.ApplyModifiedProperties();
            }

            GameSceneController converged = GameSceneSetup.ConvergeControllerAfterCityPreflightForTests();
            Assert.That(converged, Is.Not.Null);
            SerializedObject result = new SerializedObject(converged);
            Assert.That(result.FindProperty("cityRoot").objectReferenceValue,
                Is.SameAs(GameObject.Find("GameArea/City").transform));
            Assert.That(result.FindProperty("mainCamera").objectReferenceValue,
                Is.SameAs(GameObject.Find("Main Camera").GetComponent<Camera>()));
            Assert.That(AssetDatabase.GetAssetPath(
                    result.FindProperty("buildingOccludedMaterial").objectReferenceValue),
                Is.EqualTo(CityBuildingsGenerator.OccludedMaterialPath));
            Assert.That(AssetDatabase.GetAssetPath(
                    result.FindProperty("crowdCountTextStyle").objectReferenceValue),
                Is.EqualTo(CrowdCountTextStylePath));
        }
        finally
        {
            Undo.RevertAllDownToGroup(undoGroup);
            EditorSceneManager.OpenScene(GameScenePath, OpenSceneMode.Single);
        }
    }

    [Test]
    public void LocalSetup_WithAdditiveScene_FailsBeforeAnyWrite()
    {
        Scene gameScene = EditorSceneManager.OpenScene(GameScenePath, OpenSceneMode.Single);
        byte[] sceneBefore = File.ReadAllBytes(Path.GetFullPath(GameScenePath));
        Dictionary<string, byte[]> generatedBefore = CaptureGeneratedFiles();
        Scene additive = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
        SceneManager.SetActiveScene(gameScene);
        try
        {
            InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
                () => GameSceneSetup.ApplyCityBuildingsForTests());
            Assert.That(exception.Message, Does.Contain("additive scene 없이"));
            CollectionAssert.AreEqual(sceneBefore, File.ReadAllBytes(Path.GetFullPath(GameScenePath)));
            AssertGeneratedFilesEqual(generatedBefore);
        }
        finally
        {
            EditorSceneManager.CloseScene(additive, true);
            SceneManager.SetActiveScene(gameScene);
        }
    }

    private static void AssertRejectedCombinedIdentityIsUntouched(bool replaceMesh)
    {
        Dictionary<string, byte[]> before = CaptureGeneratedFiles();
        GetSourceBuildings(out Mesh sourceMesh, out Material sourceMaterial);
        Mesh foreignMesh = replaceMesh ? new Mesh() : null;
        Material foreignMaterial = !replaceMesh ? new Material(sourceMaterial) : null;
        Scene previewScene = EditorSceneManager.NewPreviewScene();
        try
        {
            GameObject city = new GameObject("City");
            SceneManager.MoveGameObjectToScene(city, previewScene);
            GameObject buildingsGo = new GameObject(CityBuildingsGenerator.BuildingsName);
            buildingsGo.transform.SetParent(city.transform, false);
            MeshFilter filter = buildingsGo.AddComponent<MeshFilter>();
            filter.sharedMesh = replaceMesh ? foreignMesh : sourceMesh;
            MeshRenderer renderer = buildingsGo.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = replaceMesh ? sourceMaterial : foreignMaterial;
            MeshCollider collider = buildingsGo.AddComponent<MeshCollider>();
            collider.sharedMesh = sourceMesh;

            Assert.Throws<InvalidOperationException>(
                () => CityBuildingsGenerator.CreateValidatedScenePlan(city.transform));
            Assert.That(buildingsGo.transform.childCount, Is.Zero);
            Assert.That(buildingsGo.GetComponent<MeshFilter>(), Is.SameAs(filter));
            Assert.That(buildingsGo.GetComponent<MeshRenderer>(), Is.SameAs(renderer));
            Assert.That(buildingsGo.GetComponent<MeshCollider>(), Is.SameAs(collider));
            AssertGeneratedFilesEqual(before);
        }
        finally
        {
            EditorSceneManager.ClosePreviewScene(previewScene);
            if (foreignMesh != null)
            {
                UnityEngine.Object.DestroyImmediate(foreignMesh);
            }
            if (foreignMaterial != null)
            {
                UnityEngine.Object.DestroyImmediate(foreignMaterial);
            }
        }
    }

    private static void AssertRejectedHierarchyIsUntouched(Action<Transform> arrange)
    {
        Dictionary<string, byte[]> before = CaptureGeneratedFiles();
        Scene previewScene = EditorSceneManager.NewPreviewScene();
        try
        {
            GameObject city = new GameObject("City");
            SceneManager.MoveGameObjectToScene(city, previewScene);
            GameObject buildingsGo = new GameObject(CityBuildingsGenerator.BuildingsName);
            buildingsGo.transform.SetParent(city.transform, false);
            arrange(buildingsGo.transform);

            int childCount = buildingsGo.transform.childCount;
            GameObject[] originalChildren = new GameObject[childCount];
            for (int i = 0; i < childCount; i++)
            {
                originalChildren[i] = buildingsGo.transform.GetChild(i).gameObject;
            }

            Assert.Throws<InvalidOperationException>(
                () => CityBuildingsGenerator.CreateValidatedScenePlan(city.transform));
            Assert.That(buildingsGo.transform.childCount, Is.EqualTo(childCount));
            for (int i = 0; i < childCount; i++)
            {
                Assert.That(buildingsGo.transform.GetChild(i).gameObject, Is.SameAs(originalChildren[i]));
            }

            AssertGeneratedFilesEqual(before);
        }
        finally
        {
            EditorSceneManager.ClosePreviewScene(previewScene);
        }
    }

    private static void AddGeneratedPrefabChildren(Transform buildings)
    {
        for (int i = 0; i < CityBuildingsGenerator.ExpectedBuildingCount; i++)
        {
            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(
                CityBuildingsGenerator.GetPrefabAssetPath(i));
            GameObject instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab, buildings);
            instance.name = CityBuildingsGenerator.GetBuildingAssetName(i);
            instance.transform.SetLocalPositionAndRotation(Vector3.zero, Quaternion.identity);
            instance.transform.localScale = Vector3.one;
            instance.transform.SetSiblingIndex(i);
        }
    }

    private static CityBuildingsGenerator.GeneratedAssets LoadGeneratedAssets()
    {
        Mesh[] meshes = new Mesh[CityBuildingsGenerator.ExpectedBuildingCount];
        GameObject[] prefabs = new GameObject[CityBuildingsGenerator.ExpectedBuildingCount];
        for (int i = 0; i < CityBuildingsGenerator.ExpectedBuildingCount; i++)
        {
            meshes[i] = AssetDatabase.LoadAssetAtPath<Mesh>(CityBuildingsGenerator.GetMeshAssetPath(i));
            prefabs[i] = AssetDatabase.LoadAssetAtPath<GameObject>(CityBuildingsGenerator.GetPrefabAssetPath(i));
        }

        return new CityBuildingsGenerator.GeneratedAssets
        {
            Meshes = meshes,
            Prefabs = prefabs,
            OccludedMaterial = AssetDatabase.LoadAssetAtPath<Material>(
                CityBuildingsGenerator.OccludedMaterialPath),
        };
    }

    private static void GetSourceBuildings(out Mesh mesh, out Material material)
    {
        GameObject source = AssetDatabase.LoadAssetAtPath<GameObject>(CityBuildingsGenerator.SourceFbxPath);
        Transform buildings = source.transform.Find(CityBuildingsGenerator.BuildingsName);
        mesh = buildings.GetComponent<MeshFilter>().sharedMesh;
        material = buildings.GetComponent<MeshRenderer>().sharedMaterial;
    }

    private static Dictionary<string, byte[]> CaptureGeneratedFiles()
    {
        Dictionary<string, byte[]> result = new Dictionary<string, byte[]>();
        for (int i = 0; i < CityBuildingsGenerator.ExpectedBuildingCount; i++)
        {
            CaptureFile(CityBuildingsGenerator.GetMeshAssetPath(i), result);
            CaptureFile(CityBuildingsGenerator.GetPrefabAssetPath(i), result);
        }

        CaptureFile(CityBuildingsGenerator.OccludedMaterialPath, result);
        return result;
    }

    private static void CaptureFile(string assetPath, Dictionary<string, byte[]> result)
    {
        string absolutePath = Path.GetFullPath(assetPath);
        result.Add(assetPath, File.Exists(absolutePath) ? File.ReadAllBytes(absolutePath) : null);
        string metaPath = absolutePath + ".meta";
        result.Add(assetPath + ".meta", File.Exists(metaPath) ? File.ReadAllBytes(metaPath) : null);
    }

    private static void AssertGeneratedFilesEqual(Dictionary<string, byte[]> expected)
    {
        foreach (KeyValuePair<string, byte[]> pair in expected)
        {
            string absolutePath = Path.GetFullPath(pair.Key);
            byte[] actual = File.Exists(absolutePath) ? File.ReadAllBytes(absolutePath) : null;
            CollectionAssert.AreEqual(pair.Value, actual, pair.Key);
        }
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Rendering.Universal.ShaderGUI;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// 현재 City.fbx의 결합 Buildings mesh를 37개 논리 건물 asset/prefab으로 결정적으로 분리한다.
/// 런타임에는 포함되지 않으며 원본 FBX는 읽기만 한다.
/// </summary>
internal static class CityBuildingsGenerator
{
    internal const string SourceFbxPath = "Assets/@Project/City/Externals/City.fbx";
    internal const string BuildingsName = "Buildings";
    internal const string GeneratedRootFolder = "Assets/@Project/City/Generated";
    internal const string GeneratedMeshFolder = GeneratedRootFolder + "/BuildingMeshes";
    internal const string GeneratedPrefabFolder = "Assets/@Project/City/Prefabs/GeneratedBuildings";
    internal const string OccludedMaterialPath = "Assets/@Project/City/Materials/City_Occluded.mat";
    internal const int ExpectedBuildingCount = 37;
    internal const int ExpectedSourceVertexCount = 12240;
    internal const int ExpectedSourceTriangleCount = 6120;
    internal const int ExpectedConnectedComponentCount = 2420;

    private const string BuildingAssetPrefix = "Building_";
    private const float GeometryEpsilon = 0.0001f;
    private const float MaxAssignmentGap = 0.05f;
    private const float MinAssignmentMargin = 0.05f;
    private const float OccludedAlpha = 0.25f;

    internal sealed class GenerationPlan
    {
        internal Mesh SourceMesh;
        internal MeshRenderer SourceRenderer;
        internal Material SourceMaterial;
        internal BuildingGroup[] Groups;
        internal int ConnectedComponentCount;
        internal int SourceVertexCount;
        internal int SourceTriangleCount;
    }

    internal sealed class BuildingGroup
    {
        internal int Index;
        internal Vector3 SeedCenter;
        internal Vector3[] Vertices;
        internal Vector3[] Normals;
        internal Vector4[] Tangents;
        internal Vector2[] Uvs;
        internal int[] Triangles;
        internal Bounds Bounds;
    }

    internal sealed class GeneratedAssets
    {
        internal Material OccludedMaterial;
        internal Mesh[] Meshes;
        internal GameObject[] Prefabs;
    }

    internal sealed class ScenePlan
    {
        internal Transform Buildings;
        internal bool HasGeneratedChildren;
        internal bool HasCombinedComponents;
    }

    internal sealed class GeneratedAssetsTransaction : IDisposable
    {
        private AssetFileSnapshot _snapshot;
        private bool _completed;

        internal GeneratedAssetsTransaction(GeneratedAssets assets, AssetFileSnapshot snapshot)
        {
            Assets = assets;
            _snapshot = snapshot;
        }

        internal GeneratedAssets Assets { get; }

        internal void Commit()
        {
            _completed = true;
            _snapshot = null;
        }

        public void Dispose()
        {
            if (_completed || _snapshot == null)
            {
                return;
            }

            _snapshot.Restore();
            _snapshot = null;
            _completed = true;
        }
    }

    internal sealed class AssetFileSnapshot
    {
        private readonly Dictionary<string, byte[]> _files;
        private readonly Dictionary<string, bool> _folders;

        private AssetFileSnapshot(Dictionary<string, byte[]> files, Dictionary<string, bool> folders)
        {
            _files = files;
            _folders = folders;
        }

        internal static AssetFileSnapshot Capture()
        {
            Dictionary<string, byte[]> files = new Dictionary<string, byte[]>();
            foreach (string assetPath in GetManagedAssetPaths())
            {
                CaptureFile(assetPath, files);
                CaptureFile(assetPath + ".meta", files);
            }

            Dictionary<string, bool> folders = new Dictionary<string, bool>();
            string[] managedFolders = GetManagedFolders();
            for (int i = 0; i < managedFolders.Length; i++)
            {
                string folder = managedFolders[i];
                folders.Add(folder, Directory.Exists(ToAbsolutePath(folder)));
                CaptureFile(folder + ".meta", files);
            }

            return new AssetFileSnapshot(files, folders);
        }

        internal void Restore()
        {
            AssetDatabase.DisallowAutoRefresh();
            try
            {
                foreach (KeyValuePair<string, byte[]> pair in _files)
                {
                    string absolutePath = ToAbsolutePath(pair.Key);
                    if (pair.Value == null)
                    {
                        if (File.Exists(absolutePath))
                        {
                            File.Delete(absolutePath);
                        }
                    }
                    else
                    {
                        string directory = Path.GetDirectoryName(absolutePath);
                        if (!Directory.Exists(directory))
                        {
                            Directory.CreateDirectory(directory);
                        }

                        File.WriteAllBytes(absolutePath, pair.Value);
                    }
                }

                string[] managedFolders = GetManagedFolders();
                for (int i = managedFolders.Length - 1; i >= 0; i--)
                {
                    string folder = managedFolders[i];
                    if (!_folders[folder])
                    {
                        string absolutePath = ToAbsolutePath(folder);
                        if (Directory.Exists(absolutePath))
                        {
                            Directory.Delete(absolutePath, true);
                        }

                        string metaPath = absolutePath + ".meta";
                        if (File.Exists(metaPath))
                        {
                            File.Delete(metaPath);
                        }
                    }
                }
            }
            finally
            {
                AssetDatabase.AllowAutoRefresh();
                AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
            }
        }

        private static void CaptureFile(string assetPath, Dictionary<string, byte[]> files)
        {
            string absolutePath = ToAbsolutePath(assetPath);
            files[assetPath] = File.Exists(absolutePath) ? File.ReadAllBytes(absolutePath) : null;
        }
    }

    private sealed class ComponentData
    {
        internal readonly List<int> TriangleOrdinals = new List<int>();
        internal Bounds Bounds;
        internal bool HasBounds;
        internal int GroupIndex = -1;

        internal void Encapsulate(Vector3 point)
        {
            if (!HasBounds)
            {
                Bounds = new Bounds(point, Vector3.zero);
                HasBounds = true;
                return;
            }

            Bounds.Encapsulate(point);
        }
    }

    private sealed class UnionFind
    {
        private readonly int[] _parent;
        private readonly byte[] _rank;

        internal UnionFind(int count)
        {
            _parent = new int[count];
            _rank = new byte[count];
            for (int i = 0; i < count; i++)
            {
                _parent[i] = i;
            }
        }

        internal int Find(int value)
        {
            int root = value;
            while (_parent[root] != root)
            {
                root = _parent[root];
            }

            while (_parent[value] != value)
            {
                int next = _parent[value];
                _parent[value] = root;
                value = next;
            }

            return root;
        }

        internal void Union(int a, int b)
        {
            int rootA = Find(a);
            int rootB = Find(b);
            if (rootA == rootB)
            {
                return;
            }

            if (_rank[rootA] < _rank[rootB])
            {
                _parent[rootA] = rootB;
            }
            else
            {
                _parent[rootB] = rootA;
                if (_rank[rootA] == _rank[rootB])
                {
                    _rank[rootA]++;
                }
            }
        }
    }

    /// <summary>
    /// 원본을 읽고 signature/귀속 불변식을 모두 검사한다. asset이나 scene은 쓰지 않는다.
    /// </summary>
    internal static GenerationPlan CreateValidatedPlan()
    {
        GameObject sourceRoot = AssetDatabase.LoadAssetAtPath<GameObject>(SourceFbxPath);
        if (sourceRoot == null)
        {
            throw new InvalidOperationException($"[CityBuildingsGenerator] 원본 FBX를 찾지 못했습니다: {SourceFbxPath}");
        }

        Transform buildings = sourceRoot.transform.Find(BuildingsName);
        if (buildings == null)
        {
            throw new InvalidOperationException($"[CityBuildingsGenerator] 원본 FBX에 '{BuildingsName}' 자식이 없습니다.");
        }

        MeshFilter sourceFilter = buildings.GetComponent<MeshFilter>();
        MeshRenderer sourceRenderer = buildings.GetComponent<MeshRenderer>();
        if (sourceFilter == null || sourceFilter.sharedMesh == null || sourceRenderer == null)
        {
            throw new InvalidOperationException(
                "[CityBuildingsGenerator] 원본 Buildings에 MeshFilter/sharedMesh/MeshRenderer가 모두 필요합니다.");
        }

        Mesh sourceMesh = sourceFilter.sharedMesh;
        ValidateSourceSignature(sourceMesh, sourceRenderer);

        Vector3[] sourceVertices = sourceMesh.vertices;
        Vector3[] sourceNormals = sourceMesh.normals;
        Vector4[] sourceTangents = sourceMesh.tangents;
        Vector2[] sourceUvs = sourceMesh.uv;
        int[] sourceTriangles = sourceMesh.GetIndices(0);

        UnionFind connectivity = new UnionFind(sourceVertices.Length);
        for (int i = 0; i < sourceTriangles.Length; i += 3)
        {
            connectivity.Union(sourceTriangles[i], sourceTriangles[i + 1]);
            connectivity.Union(sourceTriangles[i], sourceTriangles[i + 2]);
        }

        // FBX import가 hard normal/atlas seam에서 정점을 나누므로, 연결 판정에만 같은 위치를 합친다.
        // 출력 vertex data는 절대 weld하지 않는다.
        Dictionary<Vector3, int> firstVertexAtPosition = new Dictionary<Vector3, int>(sourceVertices.Length);
        for (int i = 0; i < sourceVertices.Length; i++)
        {
            int first;
            if (firstVertexAtPosition.TryGetValue(sourceVertices[i], out first))
            {
                connectivity.Union(i, first);
            }
            else
            {
                firstVertexAtPosition.Add(sourceVertices[i], i);
            }
        }

        Dictionary<int, ComponentData> componentByRoot = new Dictionary<int, ComponentData>();
        for (int triangleOrdinal = 0; triangleOrdinal < sourceTriangles.Length / 3; triangleOrdinal++)
        {
            int firstIndex = sourceTriangles[triangleOrdinal * 3];
            int root = connectivity.Find(firstIndex);
            ComponentData component;
            if (!componentByRoot.TryGetValue(root, out component))
            {
                component = new ComponentData();
                componentByRoot.Add(root, component);
            }

            component.TriangleOrdinals.Add(triangleOrdinal);
            int indexOffset = triangleOrdinal * 3;
            component.Encapsulate(sourceVertices[sourceTriangles[indexOffset]]);
            component.Encapsulate(sourceVertices[sourceTriangles[indexOffset + 1]]);
            component.Encapsulate(sourceVertices[sourceTriangles[indexOffset + 2]]);
        }

        if (componentByRoot.Count != ExpectedConnectedComponentCount)
        {
            throw new InvalidOperationException(
                $"[CityBuildingsGenerator] 연결 성분 signature 불일치: {componentByRoot.Count}, 기대 {ExpectedConnectedComponentCount}");
        }

        float meshMinimumY = sourceMesh.bounds.min.y;
        List<ComponentData> seedComponents = componentByRoot.Values
            .Where(component =>
                component.Bounds.size.x > GeometryEpsilon &&
                component.Bounds.size.y > GeometryEpsilon &&
                component.Bounds.size.z > GeometryEpsilon &&
                Mathf.Abs(component.Bounds.min.y - meshMinimumY) <= GeometryEpsilon)
            .OrderBy(component => component.Bounds.center.z)
            .ThenBy(component => component.Bounds.center.x)
            .ThenBy(component => component.Bounds.center.y)
            .ToList();

        if (seedComponents.Count != ExpectedBuildingCount)
        {
            throw new InvalidOperationException(
                $"[CityBuildingsGenerator] mesh 최저 Y에 닿는 건물 seed 수 불일치: {seedComponents.Count}, 기대 {ExpectedBuildingCount}");
        }

        ValidateSeedSeparation(seedComponents);
        AssignComponents(componentByRoot.Values, seedComponents);

        int[] groupByTriangle = Enumerable.Repeat(-1, ExpectedSourceTriangleCount).ToArray();
        foreach (ComponentData component in componentByRoot.Values)
        {
            if (component.GroupIndex < 0)
            {
                throw new InvalidOperationException("[CityBuildingsGenerator] 귀속되지 않은 연결 성분이 있습니다.");
            }

            for (int i = 0; i < component.TriangleOrdinals.Count; i++)
            {
                int triangleOrdinal = component.TriangleOrdinals[i];
                if (groupByTriangle[triangleOrdinal] >= 0)
                {
                    throw new InvalidOperationException(
                        $"[CityBuildingsGenerator] triangle {triangleOrdinal}이 둘 이상의 건물에 귀속됐습니다.");
                }

                groupByTriangle[triangleOrdinal] = component.GroupIndex;
            }
        }

        if (groupByTriangle.Any(group => group < 0))
        {
            throw new InvalidOperationException("[CityBuildingsGenerator] 귀속되지 않은 triangle이 있습니다.");
        }

        BuildingGroup[] groups = BuildGroups(
            seedComponents, groupByTriangle, sourceVertices, sourceNormals, sourceTangents, sourceUvs, sourceTriangles);

        return new GenerationPlan
        {
            SourceMesh = sourceMesh,
            SourceRenderer = sourceRenderer,
            SourceMaterial = sourceRenderer.sharedMaterial,
            Groups = groups,
            ConnectedComponentCount = componentByRoot.Count,
            SourceVertexCount = sourceVertices.Length,
            SourceTriangleCount = sourceTriangles.Length / 3,
        };
    }

    /// <summary>
    /// 생성 plan, shader/property, 출력 경로와 기존 asset type을 검사한다. asset이나 scene은 쓰지 않는다.
    /// </summary>
    internal static void PreflightConvergence(GenerationPlan plan)
    {
        if (plan == null)
        {
            throw new ArgumentNullException(nameof(plan));
        }

        if (plan.SourceMesh == null || plan.SourceRenderer == null || plan.SourceMaterial == null)
        {
            throw new InvalidOperationException("[CityBuildingsGenerator] 생성 plan의 source 참조가 비어 있습니다.");
        }

        ValidateSourceSignature(plan.SourceMesh, plan.SourceRenderer);
        ValidateOccludedMaterialSource(plan.SourceMaterial);
        ValidateGenerationPlan(plan);
        ValidateOutputDestinations();
        ValidateDesiredOutputsCanBeBuilt(plan);
    }

    /// <summary>
    /// 현재 City/Buildings가 원본 결합 상태이거나 정확한 generator prefab instance 상태인지 검사한다.
    /// 이름만 같은 foreign/duplicate/override 자식은 쓰기 전에 실패한다.
    /// </summary>
    internal static ScenePlan CreateValidatedScenePlan(Transform city)
    {
        if (city == null)
        {
            throw new ArgumentNullException(nameof(city));
        }

        Transform buildings = city.Find(BuildingsName);
        if (buildings == null)
        {
            throw new InvalidOperationException($"[CityBuildingsGenerator] scene City/{BuildingsName}을 찾지 못했습니다.");
        }

        ValidateBuildingsRootComponents(buildings);

        bool hasCombinedComponents =
            buildings.GetComponent<MeshRenderer>() != null ||
            buildings.GetComponent<MeshFilter>() != null ||
            buildings.GetComponent<MeshCollider>() != null;

        if (buildings.childCount == 0)
        {
            ValidateCombinedSourceIdentity(buildings);

            return new ScenePlan
            {
                Buildings = buildings,
                HasGeneratedChildren = false,
                HasCombinedComponents = true,
            };
        }

        ValidateGeneratedSceneChildren(buildings, null);
        return new ScenePlan
        {
            Buildings = buildings,
            HasGeneratedChildren = true,
            HasCombinedComponents = hasCombinedComponents,
        };
    }

    /// <summary>
    /// 생성 asset과 scene hierarchy가 현재 source plan에 완전히 수렴했는지 읽기 전용으로 검사한다.
    /// </summary>
    internal static GeneratedAssets LoadFullyConvergedAssets(GenerationPlan plan)
    {
        PreflightConvergence(plan);

        Mesh[] meshes = new Mesh[ExpectedBuildingCount];
        GameObject[] prefabs = new GameObject[ExpectedBuildingCount];
        for (int i = 0; i < ExpectedBuildingCount; i++)
        {
            meshes[i] = AssetDatabase.LoadAssetAtPath<Mesh>(GetMeshAssetPath(i));
            if (meshes[i] == null || !MeshMatches(meshes[i], plan.Groups[i]))
            {
                throw new InvalidOperationException(
                    $"[CityBuildingsGenerator] 생성 mesh가 현재 source plan과 다릅니다: {GetMeshAssetPath(i)}");
            }

            prefabs[i] = AssetDatabase.LoadAssetAtPath<GameObject>(GetPrefabAssetPath(i));
            if (prefabs[i] == null || !PrefabMatches(prefabs[i], i, meshes[i], plan))
            {
                throw new InvalidOperationException(
                    $"[CityBuildingsGenerator] 생성 prefab이 현재 source plan과 다릅니다: {GetPrefabAssetPath(i)}");
            }
        }

        Material occluded = AssetDatabase.LoadAssetAtPath<Material>(OccludedMaterialPath);
        Material desired = CreateDesiredOccludedMaterial(plan.SourceMaterial);
        try
        {
            if (occluded == null || !MaterialMatches(occluded, desired))
            {
                throw new InvalidOperationException(
                    $"[CityBuildingsGenerator] 투명 material이 현재 source plan과 다릅니다: {OccludedMaterialPath}");
            }
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(desired);
        }

        return new GeneratedAssets
        {
            Meshes = meshes,
            Prefabs = prefabs,
            OccludedMaterial = occluded,
        };
    }

    internal static void ValidateFullyConvergedScene(ScenePlan scenePlan, GeneratedAssets assets)
    {
        if (scenePlan == null || !scenePlan.HasGeneratedChildren || scenePlan.HasCombinedComponents)
        {
            throw new InvalidOperationException(
                "[CityBuildingsGenerator] City/Buildings가 개별 건물 37개 상태로 완전히 수렴하지 않았습니다.");
        }

        ValidateScenePlanStillMatches(scenePlan, assets.Prefabs);
    }

    internal static GeneratedAssetsTransaction BeginConvergeAssets(
        GenerationPlan plan, List<string> changed, List<string> unchanged)
    {
        PreflightConvergence(plan);

        AssetFileSnapshot snapshot = AssetFileSnapshot.Capture();
        try
        {
            EnsureFolder(GeneratedMeshFolder);
            EnsureFolder(GeneratedPrefabFolder);
            EnsureFolder("Assets/@Project/City/Materials");

            int meshChanges = 0;
            Mesh[] meshes = new Mesh[plan.Groups.Length];
            for (int i = 0; i < plan.Groups.Length; i++)
            {
                bool meshChanged;
                meshes[i] = ConvergeMeshAsset(plan.Groups[i], out meshChanged);
                if (meshChanged)
                {
                    meshChanges++;
                }
            }

            Material desiredMaterial = CreateDesiredOccludedMaterial(plan.SourceMaterial);
            Material occludedMaterial;
            bool materialChanged;
            try
            {
                occludedMaterial = ConvergeOccludedMaterial(desiredMaterial, out materialChanged);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(desiredMaterial);
            }

            int prefabChanges = 0;
            GameObject[] prefabs = new GameObject[plan.Groups.Length];
            for (int i = 0; i < plan.Groups.Length; i++)
            {
                bool prefabChanged;
                prefabs[i] = ConvergeBuildingPrefab(i, meshes[i], plan, out prefabChanged);
                if (prefabChanged)
                {
                    prefabChanges++;
                }
            }

            for (int i = 0; i < meshes.Length; i++)
            {
                AssetDatabase.SaveAssetIfDirty(meshes[i]);
                AssetDatabase.SaveAssetIfDirty(prefabs[i]);
            }
            AssetDatabase.SaveAssetIfDirty(occludedMaterial);

            if (meshChanges > 0 || prefabChanges > 0 || materialChanged)
            {
                changed.Add(
                    $"City 개별 건물 asset(mesh {meshChanges}, prefab {prefabChanges}, 투명 material {(materialChanged ? 1 : 0)})");
            }
            else
            {
                unchanged.Add("City 개별 건물 asset 37종 + 투명 material");
            }

            GeneratedAssets assets = new GeneratedAssets
            {
                OccludedMaterial = occludedMaterial,
                Meshes = meshes,
                Prefabs = prefabs,
            };
            return new GeneratedAssetsTransaction(assets, snapshot);
        }
        catch
        {
            snapshot.Restore();
            throw;
        }
    }

    internal static bool ConvergeScene(
        ScenePlan scenePlan, GeneratedAssets assets, List<string> changed, List<string> unchanged)
    {
        if (scenePlan == null || scenePlan.Buildings == null)
        {
            throw new ArgumentNullException(nameof(scenePlan));
        }

        if (assets == null || assets.Prefabs == null || assets.Prefabs.Length != ExpectedBuildingCount)
        {
            throw new InvalidOperationException("[CityBuildingsGenerator] 생성 prefab 37종이 준비되지 않았습니다.");
        }

        Transform buildings = scenePlan.Buildings;
        ValidateScenePlanStillMatches(scenePlan, assets.Prefabs);

        bool changedHere = false;
        GameObject preparedRoot = null;
        if (!scenePlan.HasGeneratedChildren)
        {
            preparedRoot = BuildPreparedHierarchy(assets.Prefabs);
        }

        try
        {
            MeshRenderer combinedRenderer = buildings.GetComponent<MeshRenderer>();
            MeshFilter combinedFilter = buildings.GetComponent<MeshFilter>();
            MeshCollider combinedCollider = buildings.GetComponent<MeshCollider>();
            if (combinedRenderer != null)
            {
                Undo.DestroyObjectImmediate(combinedRenderer);
                changedHere = true;
            }

            if (combinedFilter != null)
            {
                Undo.DestroyObjectImmediate(combinedFilter);
                changedHere = true;
            }

            if (combinedCollider != null)
            {
                Undo.DestroyObjectImmediate(combinedCollider);
                changedHere = true;
            }

            if (preparedRoot != null)
            {
                for (int i = 0; i < assets.Prefabs.Length; i++)
                {
                    Transform child = preparedRoot.transform.GetChild(0);
                    Undo.RegisterCreatedObjectUndo(child.gameObject, "Create generated city building");
                    Undo.SetTransformParent(child, buildings, "Place generated city building");
                    child.SetSiblingIndex(i);
                }

                changedHere = true;
            }
        }
        finally
        {
            if (preparedRoot != null)
            {
                UnityEngine.Object.DestroyImmediate(preparedRoot);
            }
        }

        ValidateGeneratedSceneChildren(buildings, assets.Prefabs);

        if (changedHere)
        {
            changed.Add("씬: City/Buildings 결합 renderer/collider -> 개별 건물 37개");
        }
        else
        {
            unchanged.Add("씬: City/Buildings 개별 건물 37개");
        }

        return changedHere;
    }

    internal static string GetBuildingAssetName(int index)
    {
        return BuildingAssetPrefix + index.ToString("D2");
    }

    internal static string GetMeshAssetPath(int index)
    {
        return GeneratedMeshFolder + "/" + GetBuildingAssetName(index) + ".asset";
    }

    internal static string GetPrefabAssetPath(int index)
    {
        return GeneratedPrefabFolder + "/" + GetBuildingAssetName(index) + ".prefab";
    }

    private static void ValidateSourceSignature(Mesh mesh, MeshRenderer renderer)
    {
        if (mesh.vertexCount != ExpectedSourceVertexCount)
        {
            throw new InvalidOperationException(
                $"[CityBuildingsGenerator] source vertex 수 불일치: {mesh.vertexCount}, 기대 {ExpectedSourceVertexCount}");
        }

        if (mesh.subMeshCount != 1 || mesh.GetTopology(0) != MeshTopology.Triangles)
        {
            throw new InvalidOperationException("[CityBuildingsGenerator] source는 triangle 단일 submesh여야 합니다.");
        }

        int triangleCount = (int)mesh.GetIndexCount(0) / 3;
        if (triangleCount != ExpectedSourceTriangleCount)
        {
            throw new InvalidOperationException(
                $"[CityBuildingsGenerator] source triangle 수 불일치: {triangleCount}, 기대 {ExpectedSourceTriangleCount}");
        }

        bool requiredChannels =
            HasVertexAttribute(mesh, VertexAttribute.Position, VertexAttributeFormat.Float32, 3) &&
            HasVertexAttribute(mesh, VertexAttribute.Normal, VertexAttributeFormat.Float32, 3) &&
            HasVertexAttribute(mesh, VertexAttribute.Tangent, VertexAttributeFormat.Float32, 4) &&
            HasVertexAttribute(mesh, VertexAttribute.TexCoord0, VertexAttributeFormat.Float32, 2) &&
            mesh.normals.Length == mesh.vertexCount &&
            mesh.tangents.Length == mesh.vertexCount &&
            mesh.uv.Length == mesh.vertexCount;
        bool unexpectedChannels =
            mesh.HasVertexAttribute(VertexAttribute.Color) ||
            mesh.HasVertexAttribute(VertexAttribute.TexCoord1) ||
            mesh.HasVertexAttribute(VertexAttribute.TexCoord2) ||
            mesh.HasVertexAttribute(VertexAttribute.TexCoord3) ||
            mesh.HasVertexAttribute(VertexAttribute.TexCoord4) ||
            mesh.HasVertexAttribute(VertexAttribute.TexCoord5) ||
            mesh.HasVertexAttribute(VertexAttribute.TexCoord6) ||
            mesh.HasVertexAttribute(VertexAttribute.TexCoord7) ||
            mesh.HasVertexAttribute(VertexAttribute.BlendIndices) ||
            mesh.HasVertexAttribute(VertexAttribute.BlendWeight);
        if (!requiredChannels || unexpectedChannels)
        {
            throw new InvalidOperationException(
                "[CityBuildingsGenerator] source vertex channel signature가 Position/Normal/Tangent/UV0과 다릅니다.");
        }

        Material[] materials = renderer.sharedMaterials;
        if (materials.Length != 1 || materials[0] == null)
        {
            throw new InvalidOperationException("[CityBuildingsGenerator] source Buildings는 material 하나를 사용해야 합니다.");
        }
    }

    private static bool HasVertexAttribute(
        Mesh mesh, VertexAttribute attribute, VertexAttributeFormat format, int dimension)
    {
        return mesh.HasVertexAttribute(attribute) &&
               mesh.GetVertexAttributeFormat(attribute) == format &&
               mesh.GetVertexAttributeDimension(attribute) == dimension;
    }

    private static void ValidateGenerationPlan(GenerationPlan plan)
    {
        if (plan.Groups == null || plan.Groups.Length != ExpectedBuildingCount ||
            plan.SourceVertexCount != ExpectedSourceVertexCount ||
            plan.SourceTriangleCount != ExpectedSourceTriangleCount ||
            plan.ConnectedComponentCount != ExpectedConnectedComponentCount ||
            plan.SourceRenderer.sharedMaterial != plan.SourceMaterial)
        {
            throw new InvalidOperationException("[CityBuildingsGenerator] 생성 plan header가 source signature와 다릅니다.");
        }

        int totalVertices = 0;
        int totalTriangles = 0;
        for (int i = 0; i < plan.Groups.Length; i++)
        {
            BuildingGroup group = plan.Groups[i];
            if (group == null || group.Index != i || group.Vertices == null || group.Normals == null ||
                group.Tangents == null || group.Uvs == null || group.Triangles == null ||
                group.Normals.Length != group.Vertices.Length ||
                group.Tangents.Length != group.Vertices.Length ||
                group.Uvs.Length != group.Vertices.Length ||
                group.Triangles.Length == 0 || group.Triangles.Length % 3 != 0)
            {
                throw new InvalidOperationException($"[CityBuildingsGenerator] 생성 plan group {i}의 channel 구성이 잘못됐습니다.");
            }

            Bounds calculatedBounds = new Bounds(group.Vertices[0], Vector3.zero);
            for (int v = 0; v < group.Vertices.Length; v++)
            {
                calculatedBounds.Encapsulate(group.Vertices[v]);
            }

            if (calculatedBounds != group.Bounds)
            {
                throw new InvalidOperationException($"[CityBuildingsGenerator] 생성 plan group {i}의 bounds가 vertex 배열과 다릅니다.");
            }

            for (int t = 0; t < group.Triangles.Length; t++)
            {
                if (group.Triangles[t] < 0 || group.Triangles[t] >= group.Vertices.Length)
                {
                    throw new InvalidOperationException(
                        $"[CityBuildingsGenerator] 생성 plan group {i}의 triangle index가 범위를 벗어났습니다.");
                }
            }

            totalVertices += group.Vertices.Length;
            totalTriangles += group.Triangles.Length / 3;
        }

        if (totalVertices != ExpectedSourceVertexCount || totalTriangles != ExpectedSourceTriangleCount)
        {
            throw new InvalidOperationException(
                $"[CityBuildingsGenerator] 생성 plan 보존 수 불일치: vertices={totalVertices}, triangles={totalTriangles}");
        }
    }

    private static void ValidateDesiredOutputsCanBeBuilt(GenerationPlan plan)
    {
        Material desiredMaterial = CreateDesiredOccludedMaterial(plan.SourceMaterial);
        try
        {
            if (!MaterialMatches(desiredMaterial, desiredMaterial))
            {
                throw new InvalidOperationException("[CityBuildingsGenerator] 투명 material desired state를 만들 수 없습니다.");
            }

            for (int i = 0; i < plan.Groups.Length; i++)
            {
                Mesh mesh = new Mesh();
                GameObject prefabRoot = new GameObject(GetBuildingAssetName(i));
                prefabRoot.hideFlags = HideFlags.HideAndDontSave;
                try
                {
                    ApplyMeshData(mesh, plan.Groups[i]);
                    if (!MeshMatches(mesh, plan.Groups[i]))
                    {
                        throw new InvalidOperationException(
                            $"[CityBuildingsGenerator] 건물 {i} desired mesh 검증에 실패했습니다.");
                    }

                    ConvergePrefabRoot(prefabRoot, mesh, plan);
                    if (!PrefabMatches(prefabRoot, i, mesh, plan))
                    {
                        throw new InvalidOperationException(
                            $"[CityBuildingsGenerator] 건물 {i} desired prefab 검증에 실패했습니다.");
                    }
                }
                finally
                {
                    UnityEngine.Object.DestroyImmediate(prefabRoot);
                    UnityEngine.Object.DestroyImmediate(mesh);
                }
            }
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(desiredMaterial);
        }
    }

    private static void ValidateOutputDestinations()
    {
        string[] folders = GetManagedFolders();
        for (int i = 0; i < folders.Length; i++)
        {
            string absolutePath = ToAbsolutePath(folders[i]);
            if (File.Exists(absolutePath))
            {
                throw new InvalidOperationException(
                    $"[CityBuildingsGenerator] 생성 folder 경로를 file이 점유하고 있습니다: {folders[i]}");
            }
        }

        HashSet<string> expectedMeshes = new HashSet<string>(StringComparer.Ordinal);
        HashSet<string> expectedPrefabs = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < ExpectedBuildingCount; i++)
        {
            string meshPath = GetMeshAssetPath(i);
            string prefabPath = GetPrefabAssetPath(i);
            expectedMeshes.Add(meshPath);
            expectedPrefabs.Add(prefabPath);
            ValidateAssetPathType<Mesh>(meshPath);
            ValidateAssetPathType<GameObject>(prefabPath);
        }

        ValidateAssetPathType<Material>(OccludedMaterialPath);
        ValidateOwnedFolderContents(GeneratedMeshFolder, expectedMeshes);
        ValidateOwnedFolderContents(GeneratedPrefabFolder, expectedPrefabs);
    }

    private static void ValidateAssetPathType<T>(string assetPath)
        where T : UnityEngine.Object
    {
        UnityEngine.Object existing = AssetDatabase.LoadMainAssetAtPath(assetPath);
        if (existing != null && !(existing is T))
        {
            throw new InvalidOperationException(
                $"[CityBuildingsGenerator] 생성 경로에 다른 asset type이 있습니다: {assetPath} ({existing.GetType().Name})");
        }

        string absolutePath = ToAbsolutePath(assetPath);
        if (existing == null && File.Exists(absolutePath))
        {
            throw new InvalidOperationException(
                $"[CityBuildingsGenerator] AssetDatabase에서 읽을 수 없는 file이 생성 경로를 점유합니다: {assetPath}");
        }

        if (File.Exists(absolutePath) && (File.GetAttributes(absolutePath) & FileAttributes.ReadOnly) != 0)
        {
            throw new InvalidOperationException($"[CityBuildingsGenerator] 생성 asset이 read-only입니다: {assetPath}");
        }
    }

    private static void ValidateOwnedFolderContents(string folderPath, HashSet<string> expectedAssetPaths)
    {
        string absoluteFolder = ToAbsolutePath(folderPath);
        if (!Directory.Exists(absoluteFolder))
        {
            return;
        }

        string[] directories = Directory.GetDirectories(absoluteFolder);
        if (directories.Length > 0)
        {
            throw new InvalidOperationException(
                $"[CityBuildingsGenerator] generator 전용 folder에 예상 밖 하위 folder가 있습니다: {folderPath}");
        }

        string[] files = Directory.GetFiles(absoluteFolder);
        for (int i = 0; i < files.Length; i++)
        {
            if (files[i].EndsWith(".meta", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string assetPath = files[i].Replace('\\', '/');
            string projectRoot = Directory.GetCurrentDirectory().Replace('\\', '/').TrimEnd('/') + "/";
            if (assetPath.StartsWith(projectRoot, StringComparison.Ordinal))
            {
                assetPath = assetPath.Substring(projectRoot.Length);
            }

            if (!expectedAssetPaths.Contains(assetPath))
            {
                throw new InvalidOperationException(
                    $"[CityBuildingsGenerator] generator 전용 folder에 사용자/foreign asset이 있어 중단합니다: {assetPath}");
            }
        }
    }

    private static void ValidateBuildingsRootComponents(Transform buildings)
    {
        Component[] components = buildings.GetComponents<Component>();
        for (int i = 0; i < components.Length; i++)
        {
            Component component = components[i];
            if (!(component is Transform) && !(component is MeshFilter) &&
                !(component is MeshRenderer) && !(component is MeshCollider))
            {
                throw new InvalidOperationException(
                    $"[CityBuildingsGenerator] City/Buildings의 사용자 소유 component를 보존하기 위해 중단합니다: {component.GetType().Name}");
            }
        }
    }

    private static void ValidateCombinedSourceIdentity(Transform buildings)
    {
        GameObject sourceRoot = AssetDatabase.LoadAssetAtPath<GameObject>(SourceFbxPath);
        Transform sourceBuildings = sourceRoot != null ? sourceRoot.transform.Find(BuildingsName) : null;
        MeshFilter sourceFilter = sourceBuildings != null ? sourceBuildings.GetComponent<MeshFilter>() : null;
        MeshRenderer sourceRenderer = sourceBuildings != null ? sourceBuildings.GetComponent<MeshRenderer>() : null;
        if (sourceFilter == null || sourceFilter.sharedMesh == null || sourceRenderer == null)
        {
            throw new InvalidOperationException(
                "[CityBuildingsGenerator] 원본 City.fbx Buildings identity를 확인할 수 없습니다.");
        }

        MeshFilter filter = buildings.GetComponent<MeshFilter>();
        MeshRenderer renderer = buildings.GetComponent<MeshRenderer>();
        MeshCollider collider = buildings.GetComponent<MeshCollider>();
        Material[] sourceMaterials = sourceRenderer.sharedMaterials;
        Material[] sceneMaterials = renderer != null ? renderer.sharedMaterials : null;
        bool materialsMatch = sceneMaterials != null && sceneMaterials.Length == sourceMaterials.Length;
        if (materialsMatch)
        {
            for (int i = 0; i < sourceMaterials.Length; i++)
            {
                if (sceneMaterials[i] != sourceMaterials[i])
                {
                    materialsMatch = false;
                    break;
                }
            }
        }

        if (filter == null || filter.sharedMesh != sourceFilter.sharedMesh || renderer == null ||
            !materialsMatch || (collider != null &&
                (collider.sharedMesh != sourceFilter.sharedMesh || collider.sharedMaterial != null ||
                 collider.convex || collider.isTrigger)))
        {
            throw new InvalidOperationException(
                "[CityBuildingsGenerator] 자식 없는 City/Buildings의 mesh/material/collider가 " +
                "원본 City.fbx Buildings identity와 다릅니다. 사용자 교체 데이터를 삭제하지 않고 중단합니다.");
        }
    }

    private static void ValidateGeneratedSceneChildren(Transform buildings, GameObject[] expectedPrefabs)
    {
        if (buildings.childCount != ExpectedBuildingCount)
        {
            throw new InvalidOperationException(
                $"[CityBuildingsGenerator] City/Buildings 자식이 {buildings.childCount}개입니다. " +
                "generator-owned hierarchy가 아니므로 삭제하지 않고 중단합니다.");
        }

        HashSet<GameObject> instances = new HashSet<GameObject>();
        for (int i = 0; i < ExpectedBuildingCount; i++)
        {
            Transform child = buildings.GetChild(i);
            string expectedName = GetBuildingAssetName(i);
            string expectedPath = GetPrefabAssetPath(i);
            GameObject expectedPrefab = expectedPrefabs != null
                ? expectedPrefabs[i]
                : AssetDatabase.LoadAssetAtPath<GameObject>(expectedPath);
            GameObject source = PrefabUtility.GetCorrespondingObjectFromSource(child.gameObject);

            bool valid = expectedPrefab != null && child.name == expectedName &&
                         source == expectedPrefab && AssetDatabase.GetAssetPath(source) == expectedPath &&
                         PrefabUtility.GetOutermostPrefabInstanceRoot(child.gameObject) == child.gameObject &&
                         PrefabUtility.GetPrefabInstanceStatus(child.gameObject) == PrefabInstanceStatus.Connected &&
                         !PrefabUtility.HasPrefabInstanceAnyOverrides(child.gameObject, false) &&
                         child.localPosition == Vector3.zero &&
                         child.localRotation == Quaternion.identity &&
                         child.localScale == Vector3.one &&
                         instances.Add(child.gameObject);
            if (!valid)
            {
                throw new InvalidOperationException(
                    $"[CityBuildingsGenerator] City/Buildings 자식 {i}('{child.name}')이 " +
                    $"정확한 generator prefab instance({expectedPath})가 아니거나 override/duplicate 상태입니다. 삭제하지 않고 중단합니다.");
            }
        }
    }

    private static void ValidateScenePlanStillMatches(ScenePlan scenePlan, GameObject[] prefabs)
    {
        Transform buildings = scenePlan.Buildings;
        ValidateBuildingsRootComponents(buildings);
        if (scenePlan.HasGeneratedChildren)
        {
            ValidateGeneratedSceneChildren(buildings, prefabs);
            return;
        }

        if (buildings.childCount != 0 || buildings.GetComponent<MeshFilter>() == null ||
            buildings.GetComponent<MeshFilter>().sharedMesh == null || buildings.GetComponent<MeshRenderer>() == null)
        {
            throw new InvalidOperationException(
                "[CityBuildingsGenerator] preflight 이후 City/Buildings 결합 hierarchy가 변경됐습니다.");
        }
    }

    private static GameObject BuildPreparedHierarchy(GameObject[] prefabs)
    {
        GameObject preparedRoot = new GameObject("__CityBuildings_Prepared");
        preparedRoot.hideFlags = HideFlags.HideAndDontSave;
        try
        {
            for (int i = 0; i < prefabs.Length; i++)
            {
                GameObject instance = (GameObject)PrefabUtility.InstantiatePrefab(prefabs[i], preparedRoot.transform);
                if (instance == null)
                {
                    throw new InvalidOperationException(
                        $"[CityBuildingsGenerator] 임시 hierarchy에 prefab을 배치하지 못했습니다: {GetPrefabAssetPath(i)}");
                }

                instance.name = GetBuildingAssetName(i);
                instance.transform.SetLocalPositionAndRotation(Vector3.zero, Quaternion.identity);
                instance.transform.localScale = Vector3.one;
                instance.transform.SetSiblingIndex(i);
            }

            ValidateGeneratedSceneChildren(preparedRoot.transform, prefabs);
            return preparedRoot;
        }
        catch
        {
            UnityEngine.Object.DestroyImmediate(preparedRoot);
            throw;
        }
    }

    private static void ValidateSeedSeparation(List<ComponentData> seeds)
    {
        for (int i = 0; i < seeds.Count; i++)
        {
            for (int j = i + 1; j < seeds.Count; j++)
            {
                float gap = FootprintGap(seeds[i].Bounds, seeds[j].Bounds);
                if (gap <= MaxAssignmentGap)
                {
                    throw new InvalidOperationException(
                        $"[CityBuildingsGenerator] 건물 seed {i}/{j} footprint가 너무 가깝습니다: {gap:F4}m");
                }
            }
        }
    }

    private static void AssignComponents(
        IEnumerable<ComponentData> components, List<ComponentData> seeds)
    {
        foreach (ComponentData component in components)
        {
            int bestIndex = -1;
            float bestGap = float.PositiveInfinity;
            float secondGap = float.PositiveInfinity;
            int overlappingSeeds = 0;

            for (int i = 0; i < seeds.Count; i++)
            {
                float gap = FootprintGap(component.Bounds, seeds[i].Bounds);
                if (gap <= GeometryEpsilon)
                {
                    overlappingSeeds++;
                }

                if (gap < bestGap)
                {
                    secondGap = bestGap;
                    bestGap = gap;
                    bestIndex = i;
                }
                else if (gap < secondGap)
                {
                    secondGap = gap;
                }
            }

            if (bestIndex < 0 || bestGap > MaxAssignmentGap)
            {
                throw new InvalidOperationException(
                    $"[CityBuildingsGenerator] 연결 성분을 건물에 귀속할 수 없습니다. 최근접 거리={bestGap:F4}m");
            }

            if (overlappingSeeds > 1 || secondGap - bestGap <= MinAssignmentMargin)
            {
                throw new InvalidOperationException(
                    $"[CityBuildingsGenerator] 연결 성분 귀속이 모호합니다. best={bestGap:F4}, second={secondGap:F4}");
            }

            component.GroupIndex = bestIndex;
        }
    }

    private static BuildingGroup[] BuildGroups(
        List<ComponentData> seeds,
        int[] groupByTriangle,
        Vector3[] sourceVertices,
        Vector3[] sourceNormals,
        Vector4[] sourceTangents,
        Vector2[] sourceUvs,
        int[] sourceTriangles)
    {
        BuildingGroup[] groups = new BuildingGroup[ExpectedBuildingCount];
        int[] vertexOwner = Enumerable.Repeat(-1, sourceVertices.Length).ToArray();
        int assignedTriangleCount = 0;

        for (int groupIndex = 0; groupIndex < groups.Length; groupIndex++)
        {
            Dictionary<int, int> localIndexBySource = new Dictionary<int, int>();
            List<int> sourceVertexIndices = new List<int>();
            List<int> remappedTriangles = new List<int>();

            for (int triangleOrdinal = 0; triangleOrdinal < groupByTriangle.Length; triangleOrdinal++)
            {
                if (groupByTriangle[triangleOrdinal] != groupIndex)
                {
                    continue;
                }

                assignedTriangleCount++;
                int sourceOffset = triangleOrdinal * 3;
                for (int corner = 0; corner < 3; corner++)
                {
                    int sourceVertexIndex = sourceTriangles[sourceOffset + corner];
                    int localIndex;
                    if (!localIndexBySource.TryGetValue(sourceVertexIndex, out localIndex))
                    {
                        if (vertexOwner[sourceVertexIndex] >= 0 && vertexOwner[sourceVertexIndex] != groupIndex)
                        {
                            throw new InvalidOperationException(
                                $"[CityBuildingsGenerator] source vertex {sourceVertexIndex}가 여러 건물에 공유됩니다.");
                        }

                        vertexOwner[sourceVertexIndex] = groupIndex;
                        localIndex = sourceVertexIndices.Count;
                        localIndexBySource.Add(sourceVertexIndex, localIndex);
                        sourceVertexIndices.Add(sourceVertexIndex);
                    }

                    remappedTriangles.Add(localIndex);
                }
            }

            if (remappedTriangles.Count == 0)
            {
                throw new InvalidOperationException($"[CityBuildingsGenerator] 건물 group {groupIndex}에 triangle이 없습니다.");
            }

            Vector3[] vertices = new Vector3[sourceVertexIndices.Count];
            Vector3[] normals = new Vector3[sourceVertexIndices.Count];
            Vector4[] tangents = new Vector4[sourceVertexIndices.Count];
            Vector2[] uvs = new Vector2[sourceVertexIndices.Count];
            Bounds bounds = new Bounds(sourceVertices[sourceVertexIndices[0]], Vector3.zero);
            for (int i = 0; i < sourceVertexIndices.Count; i++)
            {
                int sourceIndex = sourceVertexIndices[i];
                vertices[i] = sourceVertices[sourceIndex];
                normals[i] = sourceNormals[sourceIndex];
                tangents[i] = sourceTangents[sourceIndex];
                uvs[i] = sourceUvs[sourceIndex];
                bounds.Encapsulate(vertices[i]);
            }

            groups[groupIndex] = new BuildingGroup
            {
                Index = groupIndex,
                SeedCenter = seeds[groupIndex].Bounds.center,
                Vertices = vertices,
                Normals = normals,
                Tangents = tangents,
                Uvs = uvs,
                Triangles = remappedTriangles.ToArray(),
                Bounds = bounds,
            };
        }

        if (assignedTriangleCount != ExpectedSourceTriangleCount || vertexOwner.Any(owner => owner < 0))
        {
            int assignedVertexCount = vertexOwner.Count(owner => owner >= 0);
            throw new InvalidOperationException(
                $"[CityBuildingsGenerator] 분할 보존 불변식 실패: triangles={assignedTriangleCount}/{ExpectedSourceTriangleCount}, " +
                $"vertices={assignedVertexCount}/{ExpectedSourceVertexCount}");
        }

        return groups;
    }

    private static float FootprintGap(Bounds a, Bounds b)
    {
        float dx = Mathf.Max(0f, Mathf.Max(b.min.x - a.max.x, a.min.x - b.max.x));
        float dz = Mathf.Max(0f, Mathf.Max(b.min.z - a.max.z, a.min.z - b.max.z));
        return Mathf.Sqrt(dx * dx + dz * dz);
    }

    private static Mesh ConvergeMeshAsset(BuildingGroup group, out bool changed)
    {
        string path = GetMeshAssetPath(group.Index);
        Mesh mesh = AssetDatabase.LoadAssetAtPath<Mesh>(path);
        if (mesh == null)
        {
            mesh = new Mesh();
            ApplyMeshData(mesh, group);
            AssetDatabase.CreateAsset(mesh, path);
            changed = true;
            return mesh;
        }

        if (MeshMatches(mesh, group))
        {
            changed = false;
            return mesh;
        }

        ApplyMeshData(mesh, group);
        EditorUtility.SetDirty(mesh);
        changed = true;
        return mesh;
    }

    private static void ApplyMeshData(Mesh mesh, BuildingGroup group)
    {
        mesh.Clear();
        mesh.name = GetBuildingAssetName(group.Index);
        mesh.indexFormat = group.Vertices.Length > ushort.MaxValue ? IndexFormat.UInt32 : IndexFormat.UInt16;
        mesh.SetVertices(group.Vertices);
        mesh.SetNormals(group.Normals);
        mesh.SetTangents(group.Tangents);
        mesh.SetUVs(0, group.Uvs);
        mesh.SetIndices(group.Triangles, MeshTopology.Triangles, 0, false);
        mesh.bounds = group.Bounds;
    }

    private static bool MeshMatches(Mesh mesh, BuildingGroup group)
    {
        if (mesh.name != GetBuildingAssetName(group.Index) ||
            mesh.vertexCount != group.Vertices.Length ||
            mesh.subMeshCount != 1 ||
            mesh.GetTopology(0) != MeshTopology.Triangles ||
            mesh.bounds != group.Bounds ||
            mesh.indexFormat != (group.Vertices.Length > ushort.MaxValue ? IndexFormat.UInt32 : IndexFormat.UInt16) ||
            mesh.GetVertexAttributes().Length != 4 ||
            !HasVertexAttribute(mesh, VertexAttribute.Position, VertexAttributeFormat.Float32, 3) ||
            !HasVertexAttribute(mesh, VertexAttribute.Normal, VertexAttributeFormat.Float32, 3) ||
            !HasVertexAttribute(mesh, VertexAttribute.Tangent, VertexAttributeFormat.Float32, 4) ||
            !HasVertexAttribute(mesh, VertexAttribute.TexCoord0, VertexAttributeFormat.Float32, 2))
        {
            return false;
        }

        return ArraysEqual(mesh.vertices, group.Vertices) &&
               ArraysEqual(mesh.normals, group.Normals) &&
               ArraysEqual(mesh.tangents, group.Tangents) &&
               ArraysEqual(mesh.uv, group.Uvs) &&
               ArraysEqual(mesh.GetIndices(0), group.Triangles);
    }

    private static void ValidateOccludedMaterialSource(Material source)
    {
        if (source == null || source.shader == null || source.shader.name != "Universal Render Pipeline/Lit")
        {
            throw new InvalidOperationException(
                "[CityBuildingsGenerator] CityAtlas material은 Universal Render Pipeline/Lit이어야 합니다.");
        }

        string[] requiredProperties = { "_BaseColor", "_BaseMap", "_Surface", "_Blend", "_ZWrite" };
        for (int i = 0; i < requiredProperties.Length; i++)
        {
            if (!source.HasProperty(requiredProperties[i]))
            {
                throw new InvalidOperationException(
                    $"[CityBuildingsGenerator] CityAtlas material에 '{requiredProperties[i]}' property가 없습니다.");
            }
        }
    }

    private static Material CreateDesiredOccludedMaterial(Material source)
    {
        ValidateOccludedMaterialSource(source);

        Material desired = new Material(source) { name = "City_Occluded" };
        Color baseColor = desired.GetColor("_BaseColor");
        baseColor.a = OccludedAlpha;
        desired.SetColor("_BaseColor", baseColor);
        desired.SetFloat("_Surface", (float)BaseShaderGUI.SurfaceType.Transparent);
        desired.SetFloat("_Blend", (float)BaseShaderGUI.BlendMode.Alpha);
        BaseShaderGUI.SetMaterialKeywords(desired, LitGUI.SetMaterialKeywords);
        return desired;
    }

    private static Material ConvergeOccludedMaterial(Material desired, out bool changed)
    {
        Material material = AssetDatabase.LoadAssetAtPath<Material>(OccludedMaterialPath);
        if (material == null)
        {
            material = new Material(desired) { name = "City_Occluded" };
            AssetDatabase.CreateAsset(material, OccludedMaterialPath);
            changed = true;
            return material;
        }

        if (MaterialMatches(material, desired))
        {
            changed = false;
            return material;
        }

        EditorUtility.CopySerialized(desired, material);
        material.name = "City_Occluded";
        EditorUtility.SetDirty(material);
        changed = true;
        return material;
    }

    private static bool MaterialMatches(Material actual, Material desired)
    {
        return actual != null && desired != null && actual.shader == desired.shader &&
               EditorJsonUtility.ToJson(actual) == EditorJsonUtility.ToJson(desired);
    }

    private static GameObject ConvergeBuildingPrefab(
        int index, Mesh mesh, GenerationPlan plan, out bool changed)
    {
        string path = GetPrefabAssetPath(index);
        GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
        if (prefab != null && PrefabMatches(prefab, index, mesh, plan))
        {
            changed = false;
            return prefab;
        }

        if (prefab == null)
        {
            GameObject temporary = new GameObject(GetBuildingAssetName(index));
            try
            {
                ConvergePrefabRoot(temporary, mesh, plan);
                prefab = PrefabUtility.SaveAsPrefabAsset(temporary, path);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(temporary);
            }
        }
        else
        {
            GameObject contents = PrefabUtility.LoadPrefabContents(path);
            try
            {
                ConvergePrefabRoot(contents, mesh, plan);
                prefab = PrefabUtility.SaveAsPrefabAsset(contents, path);
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(contents);
            }
        }

        if (prefab == null)
        {
            throw new InvalidOperationException($"[CityBuildingsGenerator] prefab 저장 실패: {path}");
        }

        changed = true;
        return prefab;
    }

    private static void ConvergePrefabRoot(GameObject root, Mesh mesh, GenerationPlan plan)
    {
        root.name = mesh.name;
        root.layer = 0;
        root.tag = "Untagged";
        root.SetActive(true);
        root.transform.SetLocalPositionAndRotation(Vector3.zero, Quaternion.identity);
        root.transform.localScale = Vector3.one;

        for (int i = root.transform.childCount - 1; i >= 0; i--)
        {
            UnityEngine.Object.DestroyImmediate(root.transform.GetChild(i).gameObject);
        }

        Component[] components = root.GetComponents<Component>();
        for (int i = 0; i < components.Length; i++)
        {
            Component component = components[i];
            if (!(component is Transform) &&
                !(component is MeshFilter) &&
                !(component is MeshRenderer) &&
                !(component is MeshCollider))
            {
                UnityEngine.Object.DestroyImmediate(component);
            }
        }

        MeshFilter filter = root.GetComponent<MeshFilter>();
        if (filter == null)
        {
            filter = root.AddComponent<MeshFilter>();
        }
        filter.sharedMesh = mesh;

        MeshRenderer renderer = root.GetComponent<MeshRenderer>();
        if (renderer == null)
        {
            renderer = root.AddComponent<MeshRenderer>();
        }
        EditorUtility.CopySerialized(plan.SourceRenderer, renderer);
        renderer.sharedMaterial = plan.SourceMaterial;

        MeshCollider collider = root.GetComponent<MeshCollider>();
        if (collider == null)
        {
            collider = root.AddComponent<MeshCollider>();
        }
        collider.sharedMesh = mesh;
        collider.sharedMaterial = null;
        collider.convex = false;
        collider.isTrigger = false;
    }

    private static bool PrefabMatches(GameObject prefab, int index, Mesh mesh, GenerationPlan plan)
    {
        if (prefab.name != GetBuildingAssetName(index) ||
            prefab.transform.childCount != 0 ||
            prefab.transform.localPosition != Vector3.zero ||
            prefab.transform.localRotation != Quaternion.identity ||
            prefab.transform.localScale != Vector3.one ||
            prefab.layer != 0 ||
            prefab.tag != "Untagged")
        {
            return false;
        }

        Component[] components = prefab.GetComponents<Component>();
        if (components.Length != 4)
        {
            return false;
        }

        MeshFilter filter = prefab.GetComponent<MeshFilter>();
        MeshRenderer renderer = prefab.GetComponent<MeshRenderer>();
        MeshCollider collider = prefab.GetComponent<MeshCollider>();
        if (filter == null || renderer == null || collider == null ||
            filter.sharedMesh != mesh || collider.sharedMesh != mesh ||
            collider.sharedMaterial != null || collider.convex || collider.isTrigger)
        {
            return false;
        }

        Material[] materials = renderer.sharedMaterials;
        return materials.Length == 1 &&
               materials[0] == plan.SourceMaterial &&
               renderer.shadowCastingMode == plan.SourceRenderer.shadowCastingMode &&
               renderer.receiveShadows == plan.SourceRenderer.receiveShadows &&
               renderer.lightProbeUsage == plan.SourceRenderer.lightProbeUsage &&
               renderer.reflectionProbeUsage == plan.SourceRenderer.reflectionProbeUsage &&
               renderer.motionVectorGenerationMode == plan.SourceRenderer.motionVectorGenerationMode &&
               renderer.allowOcclusionWhenDynamic == plan.SourceRenderer.allowOcclusionWhenDynamic &&
               renderer.renderingLayerMask == plan.SourceRenderer.renderingLayerMask &&
               renderer.rendererPriority == plan.SourceRenderer.rendererPriority &&
               renderer.sortingLayerID == plan.SourceRenderer.sortingLayerID &&
               renderer.sortingOrder == plan.SourceRenderer.sortingOrder;
    }

    private static bool ArraysEqual<T>(T[] a, T[] b)
        where T : IEquatable<T>
    {
        if (a == null || b == null || a.Length != b.Length)
        {
            return false;
        }

        for (int i = 0; i < a.Length; i++)
        {
            if (!a[i].Equals(b[i]))
            {
                return false;
            }
        }

        return true;
    }

    private static IEnumerable<string> GetManagedAssetPaths()
    {
        for (int i = 0; i < ExpectedBuildingCount; i++)
        {
            yield return GetMeshAssetPath(i);
            yield return GetPrefabAssetPath(i);
        }

        yield return OccludedMaterialPath;
    }

    private static string[] GetManagedFolders()
    {
        return new[]
        {
            GeneratedRootFolder,
            GeneratedMeshFolder,
            "Assets/@Project/City/Prefabs",
            GeneratedPrefabFolder,
            "Assets/@Project/City/Materials",
        };
    }

    private static string ToAbsolutePath(string assetPath)
    {
        return Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), assetPath));
    }

    private static void EnsureFolder(string folderPath)
    {
        if (AssetDatabase.IsValidFolder(folderPath))
        {
            return;
        }

        int separatorIndex = folderPath.LastIndexOf('/');
        if (separatorIndex <= 0)
        {
            throw new InvalidOperationException($"[CityBuildingsGenerator] 잘못된 folder 경로: {folderPath}");
        }

        string parentPath = folderPath.Substring(0, separatorIndex);
        string leafName = folderPath.Substring(separatorIndex + 1);
        EnsureFolder(parentPath);
        AssetDatabase.CreateFolder(parentPath, leafName);
    }
}

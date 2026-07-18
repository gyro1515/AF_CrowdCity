using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;

/// <summary>
/// 여러 씬-독립 계약을 검사하는 Editor 전용 검증기다(씬/에셋을 절대 수정하지 않는 읽기 전용).
/// (1) 병합 Resources 네임스페이스에서 같은 로드 키(예: "Prefabs/GameplayRoot")를 가리키는 asset이 둘 이상 존재하는지.
///     여러 Resources 폴더가 하나의 런타임 네임스페이스로 병합되므로 로드 키가 겹치면 런타임 로드가 어느 asset을
///     반환할지 불확정이 된다(구 경로 잔존/오배치의 백스톱).
/// (2) feature root 프리팹의 존재·파일명·root 단일 컴포넌트·타입 계약. Prefabs 그룹(GameplayRoot/InputRoot/CameraRoot/CrowdRoot,
///     로드 키 "Prefabs/&lt;이름&gt;")과 UI 그룹(HudRoot, 로드 키 "UI/HudRoot")을 각각 검사한다.
/// (3) 구 카테고리(Resources 바로 아래 Roots 폴더) 잔존 asset 금지(카테고리 개편 후 잔존물 백스톱).
/// (4) 단일 소비자 asset이 소비 feature root 프리팹의 직렬화 필드로 배선됐는지.
/// (5) source-policy: ResourceLoader.cs 외 프로젝트 소스가 런타임 Resources 로드를 호출하지 않는지.
///
/// 로드 키 = 경로에서 마지막 "/Resources/" 이후 부분, 확장자 제거, forward slash, 소문자.
/// 중복 실패는 "충돌 asset 중 하나 이상이 이 프로젝트(Assets/@Project) 소유일 때"만 보고한다.
/// 제3자 패키지(TMP 등) 내부의 자체 병합은 우리 관심사가 아니므로 무시한다.
/// </summary>
public static class ResourcePathValidator
{
    private const string ResourcesMarker = "/Resources/";
    private const string ProjectRootPrefix = "Assets/@Project/";
    private const string PrefabsLoadKeyPrefix = "Prefabs/";
    private const string UiLoadKeyPrefix = "UI/";
    private const string LegacyRootsFolderName = "Roots"; // 구 카테고리 폴더명(이제 Prefabs/UI로 개편). Resources 바로 아래에 잔존하면 안 된다.
    // source-policy 예외는 파일명이 아니라 이 정본 경로에만 부여한다(다른 위치의 동명 ResourceLoader.cs가 우회하지 못하게).
    private const string ResourceLoaderCanonicalPath = "Assets/@Project/Manager/ResourceLoader/Scripts/ResourceLoader.cs";

    // 단일 소비자 asset이 소비 feature root 프리팹에 직렬화 저작됐는지(씬 불필요, AssetDatabase만 사용) 검증할 대상 경로.
    private const string CameraRootPrefabPath = "Assets/@Project/Game/Resources/Prefabs/CameraRoot.prefab";
    private const string CrowdRootPrefabPath = "Assets/@Project/Crowd/Resources/Prefabs/CrowdRoot.prefab";
    private const string HudRootPrefabPath = "Assets/@Project/Hud/Resources/UI/HudRoot.prefab";
    private const string OccludedMaterialPath = "Assets/@Project/City/Materials/City_Occluded.mat";
    private const string CrowdCountTextStylePath = "Assets/@Project/Hud/CrowdCountTextStyle.asset";
    private const string HumanPrefabPath = "Assets/@Project/Human/Prefabs/Human.prefab";
    private const string WallSdfAssetPath = "Assets/@Project/City/Generated/WallSdf.asset";
    private const string CrowdLabelPrefabPath = "Assets/@Project/Hud/Prefabs/CrowdLabel.prefab";
    private const string RivalMarkerPrefabPath = "Assets/@Project/Hud/Prefabs/RivalMarker.prefab";
    // GPU-anim Stage 1 Chunk B: CrowdRoot 프리팹 자식 CrowdRenderer의 VAT 직렬화 자산(NON-Resources).
    private const string VatMeshPath = "Assets/@Project/Human/VAT/HumanWalkVatMesh.asset";
    private const string VatMaterialPath = "Assets/@Project/Human/VAT/HumanVat.mat";
    private const string VatPositionTexPath = "Assets/@Project/Human/VAT/HumanWalkVatPosition.asset";
    private const string VatNormalTexPath = "Assets/@Project/Human/VAT/HumanWalkVatNormal.asset";
    private const string FontAssetPath = "Assets/TextMesh Pro/Resources/Fonts & Materials/LiberationSans SDF - Fallback.asset";

    // Prefabs 카테고리 로직 root. 런타임(ResourceLoader.LoadPrefab&lt;T&gt;)이 클래스 이름으로 "Prefabs/&lt;이름&gt;"을 로드한다.
    private static readonly Type[] PrefabRootTypes =
    {
        typeof(GameplayRoot),
        typeof(InputRoot),
        typeof(CameraRoot),
        typeof(CrowdRoot),
    };

    // UI 카테고리 root. 런타임(ResourceLoader.LoadUI&lt;T&gt;)이 클래스 이름으로 "UI/&lt;이름&gt;"을 로드한다.
    private static readonly Type[] UiRootTypes =
    {
        typeof(HudRoot),
    };

    /// <summary>
    /// 메뉴에서 검증을 실행한다. 결과는 Console의 [ResPathValidator] PASS/FAIL 로그로 확인한다.
    /// </summary>
    [MenuItem("AF/CrowdCity/Validate Resource Paths")]
    public static void ValidateMenu()
    {
        Validate();
    }

    /// <summary>
    /// 병합 Resources 중복 로드 키, feature root 프리팹의 씬-독립 계약, 구 카테고리(Roots) 잔존, 프리팹 직렬화 배선,
    /// 그리고 ResourceLoader.cs 외 Resources 로드 금지(source-policy)를 검증한다.
    /// </summary>
    /// <returns>모든 검사를 통과하면 true, 하나라도 실패하면 false를 반환한다.</returns>
    public static bool Validate()
    {
        Dictionary<string, List<string>> keyToPaths = BuildResourceLoadKeyMap();

        List<string> failures = new List<string>();
        CollectDuplicateLoadKeys(keyToPaths, failures);

        // Component 파생 타입을 simple-name(소문자) -> 타입 목록으로 한 번 인덱싱해 두 그룹의 전역 유일성 검사에서 공유한다.
        Dictionary<string, List<Type>> componentTypesByName = BuildComponentTypesByName();
        ValidateRootPrefabGroup(PrefabRootTypes, PrefabsLoadKeyPrefix, "Prefabs", keyToPaths, componentTypesByName, failures);
        ValidateRootPrefabGroup(UiRootTypes, UiLoadKeyPrefix, "UI", keyToPaths, componentTypesByName, failures);

        CollectRemainingLegacyRootsAssets(failures);
        ValidateMovedSerializedRefs(failures);
        CheckCrowdRootSdfSolverEnabled(failures);
        ValidateCrowdRendererRefs(failures);
        ValidateResourcesLoadPolicy(failures);

        if (failures.Count == 0)
        {
            Debug.Log("[ResPathValidator] PASS");
            return true;
        }

        Debug.LogError("[ResPathValidator] FAIL: " + string.Join("; ", failures));
        return false;
    }

    // 모든 asset의 Resources 로드 키(소문자) -> 경로 목록 맵을 만든다. 중복 검사와 root 조회가 함께 쓴다.
    private static Dictionary<string, List<string>> BuildResourceLoadKeyMap()
    {
        Dictionary<string, List<string>> keyToPaths = new Dictionary<string, List<string>>();
        string[] allPaths = AssetDatabase.GetAllAssetPaths();
        for (int i = 0; i < allPaths.Length; i++)
        {
            string key = TryGetResourceLoadKey(allPaths[i]);
            if (key == null)
            {
                continue;
            }

            if (!keyToPaths.TryGetValue(key, out List<string> paths))
            {
                paths = new List<string>(2);
                keyToPaths[key] = paths;
            }

            paths.Add(allPaths[i]);
        }

        return keyToPaths;
    }

    // 같은 로드 키에 2개 이상 asset이 있고 그중 하나 이상이 이 프로젝트 소유면 실패로 수집한다.
    private static void CollectDuplicateLoadKeys(Dictionary<string, List<string>> keyToPaths, List<string> failures)
    {
        foreach (KeyValuePair<string, List<string>> pair in keyToPaths)
        {
            if (pair.Value.Count > 1 && InvolvesProjectAsset(pair.Value))
            {
                failures.Add($"중복 로드 키 '{pair.Key}': {string.Join(", ", pair.Value)}");
            }
        }
    }

    // 한 카테고리 그룹의 feature root 프리팹 계약을 검증한다(AssetDatabase만 사용):
    // (1) 로드 키 "<prefix><클래스이름>"에 프리팹 존재, (2) 파일명 == 클래스 이름(대소문자 무시),
    // (3) root GameObject에 그 정확한 타입 컴포넌트가 root(자식 아님)에 정확히 1개,
    // (4) 타입이 concrete·non-nested·non-generic,
    // (5) root simple-name이 로드된 모든 어셈블리의 다른 Component 파생 타입과 겹치지 않음(전역 유일, 대소문자 무시).
    private static void ValidateRootPrefabGroup(
        Type[] types,
        string loadKeyPrefix,
        string folderLabel,
        Dictionary<string, List<string>> keyToPaths,
        Dictionary<string, List<Type>> componentTypesByName,
        List<string> failures)
    {
        for (int i = 0; i < types.Length; i++)
        {
            Type type = types[i];
            string name = type.Name;

            // (5) root simple-name이 로드된 모든 어셈블리의 다른 Component 파생 타입과 겹치면 안 된다(네임스페이스/어셈블리 무관).
            if (componentTypesByName.TryGetValue(name.ToLowerInvariant(), out List<Type> sameName))
            {
                for (int j = 0; j < sameName.Count; j++)
                {
                    if (sameName[j] != type)
                    {
                        failures.Add(
                            $"root 클래스 simple-name '{name}'이 전역 유일하지 않음(충돌 타입: {sameName[j].FullName})");
                    }
                }
            }

            if (!type.IsClass || type.IsAbstract || type.IsGenericType || type.IsNested)
            {
                failures.Add($"root 타입 '{name}'이 concrete·non-nested·non-generic 계약을 위반함");
            }

            string expectedKey = (loadKeyPrefix + name).ToLowerInvariant();
            if (!keyToPaths.TryGetValue(expectedKey, out List<string> paths) || paths.Count == 0)
            {
                failures.Add(
                    $"root 프리팹이 로드 키 '{loadKeyPrefix}{name}'에 없음(기대 경로 '<Feature>/Resources/{folderLabel}/{name}.prefab')");
                continue;
            }

            string prefabPath = SelectProjectAsset(paths);

            if (!string.Equals(Path.GetFileNameWithoutExtension(prefabPath), name, StringComparison.OrdinalIgnoreCase))
            {
                failures.Add($"root 프리팹 파일명이 클래스 이름과 다름: '{prefabPath}'(기대 '{name}.prefab')");
            }

            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            if (prefab == null)
            {
                failures.Add($"root 프리팹을 GameObject로 로드하지 못함: '{prefabPath}'");
                continue;
            }

            Component[] components = prefab.GetComponentsInChildren(type, true);
            if (components.Length != 1)
            {
                failures.Add($"root 프리팹 '{name}'에 {name} 컴포넌트가 정확히 1개가 아님({components.Length}개)");
                continue;
            }

            Component component = components[0];
            if (component.gameObject != prefab)
            {
                failures.Add($"root 프리팹 '{name}'의 {name} 컴포넌트가 root가 아니라 child에 있음");
            }

            if (component.GetType() != type)
            {
                failures.Add(
                    $"root 프리팹 '{name}'의 컴포넌트 타입이 정확히 {name}이(가) 아님({component.GetType().Name})");
            }
        }
    }

    // 카테고리 개편 후 Resources 바로 아래 구 Roots 폴더에 프로젝트 소유 asset이 남아 있으면 실패로 수집한다(폴더 자체는 무시).
    private static void CollectRemainingLegacyRootsAssets(List<string> failures)
    {
        string[] allPaths = AssetDatabase.GetAllAssetPaths();
        for (int i = 0; i < allPaths.Length; i++)
        {
            string path = allPaths[i];
            if (!path.StartsWith(ProjectRootPrefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (AssetDatabase.IsValidFolder(path))
            {
                continue;
            }

            // Resources 바로 아래 첫 세그먼트가 구 카테고리 폴더명(Roots)이면 잔존물이다(연속 리터럴 없이 세그먼트로 판정).
            int markerIndex = path.IndexOf(ResourcesMarker, StringComparison.OrdinalIgnoreCase);
            if (markerIndex < 0)
            {
                continue;
            }

            string afterResources = path.Substring(markerIndex + ResourcesMarker.Length);
            int slashIndex = afterResources.IndexOf('/');
            string firstSegment = slashIndex >= 0 ? afterResources.Substring(0, slashIndex) : afterResources;
            if (string.Equals(firstSegment, LegacyRootsFolderName, StringComparison.OrdinalIgnoreCase))
            {
                failures.Add($"구 카테고리(Resources 하위 {LegacyRootsFolderName}) asset이 남아 있음: {path}");
            }
        }
    }

    // 단일 소비자 asset의 프리팹-로컬 배선(씬 불필요)을 검증한다.
    // CameraRoot: buildingOccludedMaterial == City_Occluded.mat, HudRoot: _crowdCountTextStyle == CrowdCountTextStyle.asset,
    // CrowdRoot: _humanPrefab == Human/Prefabs/Human.prefab, _wallSdfAsset == City/Generated/WallSdf.asset,
    // HudRoot: _crowdLabelPrefab/_rivalMarkerPrefab == Hud/Prefabs/*, _fontAsset == 벤더 TMP 폰트(경로 고정).
    private static void ValidateMovedSerializedRefs(List<string> failures)
    {
        CheckPrefabSerializedRef<CameraRoot>(
            CameraRootPrefabPath, "buildingOccludedMaterial", OccludedMaterialPath, failures);
        CheckPrefabSerializedRef<HudRoot>(
            HudRootPrefabPath, "_crowdCountTextStyle", CrowdCountTextStylePath, failures);
        CheckPrefabSerializedRef<CrowdRoot>(
            CrowdRootPrefabPath, "_humanPrefab", HumanPrefabPath, failures);
        CheckPrefabSerializedRef<CrowdRoot>(
            CrowdRootPrefabPath, "_wallSdfAsset", WallSdfAssetPath, failures);
        CheckPrefabSerializedRef<HudRoot>(
            HudRootPrefabPath, "_crowdLabelPrefab", CrowdLabelPrefabPath, failures);
        CheckPrefabSerializedRef<HudRoot>(
            HudRootPrefabPath, "_rivalMarkerPrefab", RivalMarkerPrefabPath, failures);
        CheckPrefabSerializedRef<HudRoot>(
            HudRootPrefabPath, "_fontAsset", FontAssetPath, failures);
    }

    // 회귀 가드: Crowd/Resources/Prefabs/CrowdRoot.prefab의 _useSdfSolver가 1(SDF-ON, commit 55c8d19의 의도)인지 확인한다.
    // 런타임 기본값은 false이고 프리팹 재생성 경로가 이 값을 세우지 않으므로, 재생성 시 SDF가 조용히 꺼지지 않도록 여기서 강제한다.
    private static void CheckCrowdRootSdfSolverEnabled(List<string> failures)
    {
        SerializedProperty property =
            FindPrefabSerializedProperty<CrowdRoot>(CrowdRootPrefabPath, "_useSdfSolver", failures);
        if (property == null)
        {
            return;
        }

        if (property.propertyType != SerializedPropertyType.Boolean)
        {
            failures.Add("CrowdRoot 프리팹 '_useSdfSolver'가 bool 필드가 아님");
            return;
        }

        if (!property.boolValue)
        {
            failures.Add(
                "CrowdRoot 프리팹 '_useSdfSolver'가 1(SDF-ON, commit 55c8d19 의도)이 아님 — " +
                "프리팹 재생성이 SDF solver를 조용히 껐을 수 있습니다.");
        }
    }

    // GPU-anim Stage 1 Chunk B: CrowdRoot 프리팹의 자식 CrowdRenderer가 저작돼 있으면 그 VAT refs와 CrowdRoot._crowdRenderer
    // 배선을 검사한다(present-then-strict). 자식이 없으면(GPU 렌더 경로 미저작 = opt-in) 통과한다 — 기본 OFF 스위치라
    // 미저작 상태도 유효하다. FindPrefabSerializedProperty<T>는 root 전용이라 자식 컴포넌트는 GetComponentInChildren로 해석한다.
    private static void ValidateCrowdRendererRefs(List<string> failures)
    {
        GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(CrowdRootPrefabPath);
        if (prefab == null)
        {
            failures.Add($"프리팹을 로드하지 못함: {CrowdRootPrefabPath}");
            return;
        }

        CrowdRenderer renderer = prefab.GetComponentInChildren<CrowdRenderer>(true);
        if (renderer == null)
        {
            return; // GPU 렌더 경로 미저작(opt-in): 통과.
        }

        SerializedObject rendererSo = new SerializedObject(renderer);
        CheckChildSerializedRef(rendererSo, "_vatMesh", VatMeshPath, failures);
        CheckChildSerializedRef(rendererSo, "_vatMaterial", VatMaterialPath, failures);
        CheckChildSerializedRef(rendererSo, "_positionVat", VatPositionTexPath, failures);
        CheckChildSerializedRef(rendererSo, "_normalVat", VatNormalTexPath, failures);

        CrowdRoot crowdRoot = prefab.GetComponent<CrowdRoot>();
        SerializedProperty crowdRendererRef =
            crowdRoot != null ? new SerializedObject(crowdRoot).FindProperty("_crowdRenderer") : null;
        if (crowdRendererRef == null || crowdRendererRef.objectReferenceValue == null)
        {
            failures.Add("CrowdRoot 프리팹 '_crowdRenderer'가 미배선(CrowdRenderer 자식이 있는데 참조가 비어 있음)");
        }
        else if (crowdRendererRef.objectReferenceValue != renderer)
        {
            failures.Add("CrowdRoot 프리팹 '_crowdRenderer'가 CrowdRenderer 자식을 가리키지 않음");
        }
    }

    // 자식 컴포넌트의 objectReference 직렬화 필드가 비어 있지 않고 기대 asset 경로를 가리키는지 검사한다.
    private static void CheckChildSerializedRef(
        SerializedObject serialized, string fieldName, string expectedAssetPath, List<string> failures)
    {
        SerializedProperty property = serialized.FindProperty(fieldName);
        if (property == null)
        {
            failures.Add($"CrowdRenderer 직렬화 필드 '{fieldName}'을(를) 찾지 못함");
            return;
        }

        UnityEngine.Object value = property.objectReferenceValue;
        if (value == null)
        {
            failures.Add($"CrowdRenderer '{fieldName}'이(가) 미배선(null)");
            return;
        }

        string actualPath = AssetDatabase.GetAssetPath(value);
        if (actualPath != expectedAssetPath)
        {
            failures.Add($"CrowdRenderer '{fieldName}'이(가) {expectedAssetPath}이(가) 아님(actual={actualPath})");
        }
    }

    // 프리팹 root 컴포넌트 T의 objectReference 직렬화 필드가 비어 있지 않고 기대 asset 경로를 가리키는지 검사한다.
    private static void CheckPrefabSerializedRef<T>(
        string prefabPath, string fieldName, string expectedAssetPath, List<string> failures)
        where T : Component
    {
        SerializedProperty property = FindPrefabSerializedProperty<T>(prefabPath, fieldName, failures);
        if (property == null)
        {
            return;
        }

        UnityEngine.Object value = property.objectReferenceValue;
        if (value == null)
        {
            failures.Add($"{typeof(T).Name} 프리팹 '{fieldName}'이(가) 미배선(null)");
            return;
        }

        string actualPath = AssetDatabase.GetAssetPath(value);
        if (actualPath != expectedAssetPath)
        {
            failures.Add(
                $"{typeof(T).Name} 프리팹 '{fieldName}'이(가) {expectedAssetPath}이(가) 아님(actual={actualPath})");
        }
    }

    // 프리팹 root 컴포넌트 T의 직렬화 프로퍼티를 찾는다. 실패 시 failures에 사유를 남기고 null을 반환한다.
    private static SerializedProperty FindPrefabSerializedProperty<T>(
        string prefabPath, string fieldName, List<string> failures)
        where T : Component
    {
        GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
        if (prefab == null)
        {
            failures.Add($"프리팹을 로드하지 못함: {prefabPath}");
            return null;
        }

        T component = prefab.GetComponent<T>();
        if (component == null)
        {
            failures.Add($"프리팹 '{prefabPath}' root에 {typeof(T).Name} 컴포넌트가 없음");
            return null;
        }

        SerializedObject serialized = new SerializedObject(component);
        SerializedProperty property = serialized.FindProperty(fieldName);
        if (property == null)
        {
            failures.Add($"{typeof(T).Name} 직렬화 필드 '{fieldName}'을(를) 찾지 못함");
            return null;
        }

        return property;
    }

    // source-policy: ResourceLoader.cs만 런타임 Resources 로드를 호출할 수 있다. 프로젝트 소스(*.cs)를 스캔해 그 외 파일에서
    // 실행 코드의 런타임 Resources 로드 호출을 찾으면 실패로 수집한다. 주석/문자열 리터럴은 제거하고 실행 코드만 본다.
    private static void ValidateResourcesLoadPolicy(List<string> failures)
    {
        string rootAbsolute = Path.Combine(Application.dataPath, "@Project");
        if (!Directory.Exists(rootAbsolute))
        {
            failures.Add($"source-policy 스캔 루트가 없음: {rootAbsolute}");
            return;
        }

        string[] files = Directory.GetFiles(rootAbsolute, "*.cs", SearchOption.AllDirectories);
        for (int i = 0; i < files.Length; i++)
        {
            string file = files[i];
            // 예외는 파일명이 아니라 정본 상대 경로에만 부여한다(동명 우회 방지).
            if (string.Equals(ToAssetPath(file), ResourceLoaderCanonicalPath, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string code = StripCommentsAndStrings(File.ReadAllText(file));
            // 호출 형태만 매칭한다: 앞에 식별자 워드문자가 없어야 하고(부분일치 방지: 예 MyResources 프로퍼티의 Loaded 접근 제외),
            // Resources 정적 클래스의 Load 계열(Load / Load&lt; / LoadAll( / LoadAsync()처럼 뒤에 '&lt;' 또는 '(' 호출 토큰이 와야 한다.
            if (Regex.IsMatch(code, @"(?<!\w)Resources\s*\.\s*Load\w*\s*[<(]"))
            {
                failures.Add($"ResourceLoader.cs 외 파일에서 런타임 Resources 로드 실행 코드 발견: {ToAssetPath(file)}");
            }
        }
    }

    // C# 소스에서 주석(//, /* */, ///)과 리터럴 텍스트를 제거해 실행 코드만 남긴다.
    // 보간 문자열($"...", $@"...", @$"...")은 리터럴 텍스트만 제거하고 { ... } 보간 홀 안의 코드는 보존해
    // 보간 홀 안에 들어간 Resources 런타임 로드 호출도 스캔 대상에 남긴다(홀 코드는 보존되므로).
    private static string StripCommentsAndStrings(string s)
    {
        StringBuilder sb = new StringBuilder(s.Length);
        int i = 0;
        int n = s.Length;
        while (i < n)
        {
            char c = s[i];

            if (c == '/' && i + 1 < n && s[i + 1] == '/')
            {
                i += 2;
                while (i < n && s[i] != '\n')
                {
                    i++;
                }

                continue;
            }

            if (c == '/' && i + 1 < n && s[i + 1] == '*')
            {
                i += 2;
                while (i + 1 < n && !(s[i] == '*' && s[i + 1] == '/'))
                {
                    i++;
                }

                i = Math.Min(i + 2, n);
                continue;
            }

            if (TryGetStringStart(s, i, out bool interpolated, out bool verbatim, out int quotePos))
            {
                i = interpolated
                    ? ScanInterpolated(s, quotePos + 1, verbatim, sb)
                    : SkipPlainString(s, quotePos + 1, verbatim);
                continue;
            }

            if (c == '\'')
            {
                i = SkipCharLiteral(s, i + 1);
                continue;
            }

            sb.Append(c);
            i++;
        }

        return sb.ToString();
    }

    // i 위치가 문자열/문자 리터럴의 시작(", @", $", $@", @$")인지 판정한다. 시작이면 여는 큰따옴표 위치와 종류를 돌려준다.
    private static bool TryGetStringStart(string s, int i, out bool interpolated, out bool verbatim, out int quotePos)
    {
        interpolated = false;
        verbatim = false;
        quotePos = -1;
        int n = s.Length;
        char c = s[i];

        if (c == '"')
        {
            quotePos = i;
            return true;
        }

        if (c == '@' && i + 1 < n && s[i + 1] == '"')
        {
            verbatim = true;
            quotePos = i + 1;
            return true;
        }

        if (c == '$' && i + 1 < n && s[i + 1] == '"')
        {
            interpolated = true;
            quotePos = i + 1;
            return true;
        }

        if (c == '$' && i + 2 < n && s[i + 1] == '@' && s[i + 2] == '"')
        {
            interpolated = true;
            verbatim = true;
            quotePos = i + 2;
            return true;
        }

        if (c == '@' && i + 2 < n && s[i + 1] == '$' && s[i + 2] == '"')
        {
            interpolated = true;
            verbatim = true;
            quotePos = i + 2;
            return true;
        }

        return false;
    }

    // 비보간/보간이 아닌 문자열의 내용을 건너뛴다(리터럴은 버린다). i는 여는 큰따옴표 다음. 닫는 큰따옴표 다음 인덱스를 돌려준다.
    private static int SkipPlainString(string s, int i, bool verbatim)
    {
        int n = s.Length;
        while (i < n)
        {
            char c = s[i];
            if (verbatim)
            {
                if (c == '"')
                {
                    if (i + 1 < n && s[i + 1] == '"')
                    {
                        i += 2;
                        continue;
                    }

                    return i + 1;
                }

                i++;
            }
            else
            {
                if (c == '\\')
                {
                    i += 2;
                    continue;
                }

                if (c == '"')
                {
                    return i + 1;
                }

                i++;
            }
        }

        return i;
    }

    // 문자 리터럴('...')을 건너뛴다. i는 여는 작은따옴표 다음. 닫는 작은따옴표 다음 인덱스를 돌려준다.
    private static int SkipCharLiteral(string s, int i)
    {
        int n = s.Length;
        while (i < n)
        {
            char c = s[i];
            if (c == '\\')
            {
                i += 2;
                continue;
            }

            if (c == '\'')
            {
                return i + 1;
            }

            i++;
        }

        return i;
    }

    // 보간 문자열을 스캔해 리터럴 텍스트는 버리고 { ... } 홀 안의 코드는 (주석/문자열을 벗겨) sb에 보존한다.
    // i는 여는 큰따옴표 다음. 닫는 큰따옴표 다음 인덱스를 돌려준다.
    private static int ScanInterpolated(string s, int i, bool verbatim, StringBuilder sb)
    {
        int n = s.Length;
        while (i < n)
        {
            char c = s[i];

            if (!verbatim && c == '\\')
            {
                i += 2; // \" \\ 등 이스케이프 리터럴.
                continue;
            }

            if (c == '"')
            {
                if (verbatim && i + 1 < n && s[i + 1] == '"')
                {
                    i += 2;
                    continue;
                }

                return i + 1;
            }

            if (c == '{')
            {
                if (i + 1 < n && s[i + 1] == '{')
                {
                    i += 2; // {{ 리터럴 중괄호.
                    continue;
                }

                int holeStart = i + 1;
                int holeEnd = FindHoleEnd(s, holeStart);
                if (holeEnd > holeStart)
                {
                    sb.Append(StripCommentsAndStrings(s.Substring(holeStart, holeEnd - holeStart)));
                    sb.Append(' ');
                }

                i = holeEnd < n ? holeEnd + 1 : n;
                continue;
            }

            if (c == '}')
            {
                if (i + 1 < n && s[i + 1] == '}')
                {
                    i += 2; // }} 리터럴 중괄호.
                    continue;
                }

                i++;
                continue;
            }

            i++; // 리터럴 텍스트는 버린다.
        }

        return i;
    }

    // 보간 홀의 여는 '{' 다음(i)에서 시작해, 중첩 중괄호/문자열/주석을 존중하며 짝이 맞는 '}'의 인덱스를 돌려준다.
    private static int FindHoleEnd(string s, int i)
    {
        int n = s.Length;
        int depth = 1;
        while (i < n)
        {
            char c = s[i];

            if (c == '/' && i + 1 < n && s[i + 1] == '/')
            {
                i += 2;
                while (i < n && s[i] != '\n')
                {
                    i++;
                }

                continue;
            }

            if (c == '/' && i + 1 < n && s[i + 1] == '*')
            {
                i += 2;
                while (i + 1 < n && !(s[i] == '*' && s[i + 1] == '/'))
                {
                    i++;
                }

                i = Math.Min(i + 2, n);
                continue;
            }

            if (TryGetStringStart(s, i, out bool interpolated, out bool verbatim, out int quotePos))
            {
                i = interpolated
                    ? SkipInterpolated(s, quotePos + 1, verbatim)
                    : SkipPlainString(s, quotePos + 1, verbatim);
                continue;
            }

            if (c == '\'')
            {
                i = SkipCharLiteral(s, i + 1);
                continue;
            }

            if (c == '{')
            {
                depth++;
                i++;
                continue;
            }

            if (c == '}')
            {
                depth--;
                if (depth == 0)
                {
                    return i;
                }

                i++;
                continue;
            }

            i++;
        }

        return i;
    }

    // 보간 문자열을 (아무것도 보존하지 않고) 건너뛴다. 홀 안의 중괄호까지 올바로 넘기기 위한 헬퍼다(FindHoleEnd에서 사용).
    private static int SkipInterpolated(string s, int i, bool verbatim)
    {
        int n = s.Length;
        while (i < n)
        {
            char c = s[i];

            if (!verbatim && c == '\\')
            {
                i += 2;
                continue;
            }

            if (c == '"')
            {
                if (verbatim && i + 1 < n && s[i + 1] == '"')
                {
                    i += 2;
                    continue;
                }

                return i + 1;
            }

            if (c == '{')
            {
                if (i + 1 < n && s[i + 1] == '{')
                {
                    i += 2;
                    continue;
                }

                int holeEnd = FindHoleEnd(s, i + 1);
                i = holeEnd < n ? holeEnd + 1 : n;
                continue;
            }

            if (c == '}')
            {
                if (i + 1 < n && s[i + 1] == '}')
                {
                    i += 2;
                    continue;
                }

                i++;
                continue;
            }

            i++;
        }

        return i;
    }

    // 절대 파일 경로를 Assets/ 상대 asset 경로(forward slash)로 변환한다(보고 메시지용).
    private static string ToAssetPath(string absolutePath)
    {
        string normalized = absolutePath.Replace('\\', '/');
        string dataPath = Application.dataPath.Replace('\\', '/');
        if (normalized.StartsWith(dataPath, StringComparison.OrdinalIgnoreCase))
        {
            return "Assets" + normalized.Substring(dataPath.Length);
        }

        return normalized;
    }

    // 로드된 모든 어셈블리의 Component 파생 타입을 simple-name(소문자) -> 타입 목록으로 인덱싱한다(root 전역 유일성 검사용).
    private static Dictionary<string, List<Type>> BuildComponentTypesByName()
    {
        Dictionary<string, List<Type>> map = new Dictionary<string, List<Type>>();
        foreach (Type type in TypeCache.GetTypesDerivedFrom<Component>())
        {
            string key = type.Name.ToLowerInvariant();
            if (!map.TryGetValue(key, out List<Type> list))
            {
                list = new List<Type>(1);
                map[key] = list;
            }

            list.Add(type);
        }

        return map;
    }

    // 로드 키가 여러 경로를 가리키면(중복 검사가 별도로 보고) 이 프로젝트 소유 경로를 우선 선택한다.
    private static string SelectProjectAsset(List<string> paths)
    {
        for (int i = 0; i < paths.Count; i++)
        {
            if (paths[i].StartsWith(ProjectRootPrefix, StringComparison.OrdinalIgnoreCase))
            {
                return paths[i];
            }
        }

        return paths[0];
    }

    /// <summary>
    /// batch mode 파이프라인용 진입점이다. 통과 시 exit code 0, 실패 또는 예외 시 1로 Editor를 종료한다.
    /// </summary>
    public static void ValidateBatch()
    {
        bool passed = false;
        try
        {
            passed = Validate();
        }
        finally
        {
            EditorApplication.Exit(passed ? 0 : 1);
        }
    }

    // asset 경로의 런타임 Resources 로드 키를 계산한다. Resources asset이 아니거나 폴더면 null.
    private static string TryGetResourceLoadKey(string assetPath)
    {
        if (string.IsNullOrEmpty(assetPath))
        {
            return null;
        }

        // 폴더는 로드 키가 없다(개별 asset만 검사한다).
        if (AssetDatabase.IsValidFolder(assetPath))
        {
            return null;
        }

        int markerIndex = assetPath.LastIndexOf(ResourcesMarker, StringComparison.OrdinalIgnoreCase);
        if (markerIndex < 0)
        {
            return null;
        }

        string relative = assetPath.Substring(markerIndex + ResourcesMarker.Length);
        int dotIndex = relative.LastIndexOf('.');
        if (dotIndex >= 0)
        {
            relative = relative.Substring(0, dotIndex);
        }

        return relative.Replace('\\', '/').ToLowerInvariant();
    }

    // 충돌 경로 중 하나라도 이 프로젝트 소유(Assets/@Project)면 우리 문제로 보고한다.
    private static bool InvolvesProjectAsset(List<string> paths)
    {
        for (int i = 0; i < paths.Count; i++)
        {
            if (paths[i].StartsWith(ProjectRootPrefix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}

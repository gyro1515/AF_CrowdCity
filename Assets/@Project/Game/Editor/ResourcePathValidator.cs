using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

/// <summary>
/// 두 가지 씬-독립 계약을 검사하는 Editor 전용 검증기다(씬/에셋을 절대 수정하지 않는 읽기 전용).
/// (1) 병합 Resources 네임스페이스에서 같은 로드 키(예: "Roots/GameplayRoot")를 가리키는 asset이 둘 이상 존재하는지.
///     여러 Resources 폴더가 하나의 런타임 네임스페이스로 병합되므로 로드 키가 겹치면 Resources.Load가 어느 asset을
///     반환할지 불확정이 된다(구 경로 잔존/오배치의 백스톱).
/// (2) 5개 로직 root 프리팹(ResourceLoader.LoadRoot&lt;T&gt; 대상)의 존재·파일명·root 단일 컴포넌트·타입 계약.
///
/// 로드 키 = 경로에서 마지막 "/Resources/" 이후 부분, 확장자 제거, forward slash, 소문자.
/// 중복 실패는 "충돌 asset 중 하나 이상이 이 프로젝트(Assets/@Project) 소유일 때"만 보고한다.
/// 제3자 패키지(TMP 등) 내부의 자체 병합은 우리 관심사가 아니므로 무시한다.
/// </summary>
public static class ResourcePathValidator
{
    private const string ResourcesMarker = "/Resources/";
    private const string ProjectRootPrefix = "Assets/@Project/";
    private const string RootLoadKeyPrefix = "Roots/";

    // 5개 로직 root. 런타임(ResourceLoader.LoadRoot&lt;T&gt;)이 클래스 이름으로 "Roots/&lt;이름&gt;"을 로드하므로
    // 프리팹 존재/이름/구조/타입 계약을 여기(씬 불필요, AssetDatabase만 사용)에서 검증해 저렴한 batch 게이트가 덮게 한다.
    private static readonly Type[] LogicRootTypes =
    {
        typeof(GameplayRoot),
        typeof(InputRoot),
        typeof(CameraRoot),
        typeof(CrowdRoot),
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
    /// (1) 병합 Resources 네임스페이스의 중복 로드 키(2개 이상)와 (2) 5개 로직 root 프리팹의 씬-독립 계약을 검증한다.
    /// </summary>
    /// <returns>모든 검사를 통과하면 true, 하나라도 실패하면 false를 반환한다.</returns>
    public static bool Validate()
    {
        Dictionary<string, List<string>> keyToPaths = BuildResourceLoadKeyMap();

        List<string> failures = new List<string>();
        CollectDuplicateLoadKeys(keyToPaths, failures);
        ValidateRootPrefabs(keyToPaths, failures);

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

    // 5개 로직 root 프리팹의 씬-독립 계약을 검증한다(AssetDatabase만 사용):
    // (1) 로드 키 "Roots/<클래스이름>"에 프리팹 존재, (2) 파일명 == 클래스 이름(대소문자 무시),
    // (3) root GameObject에 그 정확한 타입 컴포넌트가 root(자식 아님)에 정확히 1개,
    // (4) 타입이 concrete·non-nested·non-generic,
    // (5) root simple-name이 로드된 모든 어셈블리의 다른 Component 파생 타입과 겹치지 않음(전역 유일, 대소문자 무시).
    private static void ValidateRootPrefabs(Dictionary<string, List<string>> keyToPaths, List<string> failures)
    {
        // 병합 로드 키가 소문자이므로 대소문자를 무시하고 Component 파생 타입 전체를 simple-name -> 타입 목록으로 인덱싱한다.
        Dictionary<string, List<Type>> componentTypesByName = BuildComponentTypesByName();

        for (int i = 0; i < LogicRootTypes.Length; i++)
        {
            Type type = LogicRootTypes[i];
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

            string expectedKey = (RootLoadKeyPrefix + name).ToLowerInvariant();
            if (!keyToPaths.TryGetValue(expectedKey, out List<string> paths) || paths.Count == 0)
            {
                failures.Add(
                    $"root 프리팹이 로드 키 'Roots/{name}'에 없음(기대 경로 '<Feature>/Resources/Roots/{name}.prefab')");
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

    // asset 경로의 Resources.Load 로드 키를 계산한다. Resources asset이 아니거나 폴더면 null.
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

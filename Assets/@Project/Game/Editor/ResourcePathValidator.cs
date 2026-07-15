using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

/// <summary>
/// 병합 Resources 네임스페이스에서 같은 로드 키(예: "Game/GameplayRoot")를 가리키는 asset이 둘 이상 존재하는지
/// 검사하는 Editor 전용 검증기다. 여러 Resources 폴더가 하나의 런타임 네임스페이스로 병합되므로 로드 키가 겹치면
/// Resources.Load가 어느 asset을 반환할지 불확정이 된다(구 경로 잔존/오배치의 백스톱).
/// 씬/에셋을 절대 수정하지 않는다(읽기 전용 검증).
///
/// 로드 키 = 경로에서 마지막 "/Resources/" 이후 부분, 확장자 제거, forward slash, 소문자.
/// 실패는 "충돌 asset 중 하나 이상이 이 프로젝트(Assets/@Project) 소유일 때"만 보고한다.
/// 제3자 패키지(TMP 등) 내부의 자체 병합은 우리 관심사가 아니므로 무시한다.
/// </summary>
public static class ResourcePathValidator
{
    private const string ResourcesMarker = "/Resources/";
    private const string ProjectRootPrefix = "Assets/@Project/";

    /// <summary>
    /// 메뉴에서 검증을 실행한다. 결과는 Console의 [ResPathValidator] PASS/FAIL 로그로 확인한다.
    /// </summary>
    [MenuItem("AF/CrowdCity/Validate Resource Paths")]
    public static void ValidateMenu()
    {
        Validate();
    }

    /// <summary>
    /// 모든 Resources asset의 로드 키를 계산해 중복(2개 이상) 키를 수집한다.
    /// </summary>
    /// <returns>중복이 없으면 true, 하나라도 있으면 false를 반환한다.</returns>
    public static bool Validate()
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

        List<string> failures = new List<string>();
        foreach (KeyValuePair<string, List<string>> pair in keyToPaths)
        {
            if (pair.Value.Count > 1 && InvolvesProjectAsset(pair.Value))
            {
                failures.Add($"중복 로드 키 '{pair.Key}': {string.Join(", ", pair.Value)}");
            }
        }

        if (failures.Count == 0)
        {
            Debug.Log("[ResPathValidator] PASS");
            return true;
        }

        Debug.LogError("[ResPathValidator] FAIL: " + string.Join("; ", failures));
        return false;
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

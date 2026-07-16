using System;
using UnityEngine;

/// <summary>
/// 프리팹/UI/ScriptableObject를 카테고리 폴더 규칙으로 로드하는 중앙 무상태 로더다.
/// 캐시/수명 소유가 없다 — 반환된 프리팹 컴포넌트를 Instantiate하고 그 인스턴스 수명을 책임지는 것은 호출부다.
/// 카테고리별 로드 키: 프리팹="Prefabs/"+typeof(T).Name, UI="UI/"+typeof(T).Name, SO="SO/"+name.
/// 여러 피처의 Resources 하위가 하나의 병합 네임스페이스로 합쳐지므로 카테고리 폴더로 키 충돌을 막는다.
/// 컴포넌트는 클래스 이름으로 직접 로드하므로 로더와 프리팹 저작이 어긋날 수 없다.
/// 이 클래스만이 Resources.Load를 호출하는 유일한 지점이다(source-policy 게이트가 강제).
/// </summary>
public static class ResourceLoader
{
    private const string PrefabsFolder = "Prefabs/";
    private const string UiFolder = "UI/";
    private const string SoFolder = "SO/";

    /// <summary>
    /// 프리팹을 클래스 이름으로("Prefabs/&lt;이름&gt;") 로드해 root의 T 컴포넌트를 반환한다. Instantiate는 호출부가 수행한다.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// 프리팹을 Resources에서 찾지 못하거나 프리팹 루트에 T 컴포넌트가 없으면 발생한다.
    /// </exception>
    public static T LoadPrefab<T>()
        where T : Component
    {
        return LoadComponentPrefab<T>(PrefabsFolder);
    }

    /// <summary>
    /// UI 프리팹을 클래스 이름으로("UI/&lt;이름&gt;") 로드해 root의 T 컴포넌트를 반환한다. Instantiate는 호출부가 수행한다.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// 프리팹을 Resources에서 찾지 못하거나 프리팹 루트에 T 컴포넌트가 없으면 발생한다.
    /// </exception>
    public static T LoadUI<T>()
        where T : Component
    {
        return LoadComponentPrefab<T>(UiFolder);
    }

    /// <summary>
    /// ScriptableObject를 이름으로("SO/&lt;name&gt;") 로드해 반환한다. 순수 C# 소비자용(컴포넌트가 아닌 자산).
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="name"/>이 null/빈 문자열이면 발생한다.</exception>
    /// <exception cref="InvalidOperationException">자산을 Resources에서 찾지 못하면 발생한다.</exception>
    public static T LoadSO<T>(string name)
        where T : ScriptableObject
    {
        if (string.IsNullOrEmpty(name))
        {
            throw new ArgumentException("[ResourceLoader] LoadSO name이 비어 있습니다.", nameof(name));
        }

        string key = SoFolder + name;
        T asset = Resources.Load<T>(key);
        if (asset == null)
        {
            throw new InvalidOperationException(
                $"[ResourceLoader] SO를 Resources에서 로드하지 못했습니다: '{key}'. " +
                $"자산이 '<Feature>/Resources/{key}.asset'에 있어야 합니다.");
        }

        return asset;
    }

    // 프리팹을 Resources.Load<GameObject> + GetComponent<T> 규약으로 로드한다(Resources.Load<T> 아님).
    private static T LoadComponentPrefab<T>(string folder)
        where T : Component
    {
        string key = folder + typeof(T).Name;

        GameObject prefab = Resources.Load<GameObject>(key);
        if (prefab == null)
        {
            throw new InvalidOperationException(
                $"[ResourceLoader] 프리팹을 Resources에서 로드하지 못했습니다: '{key}'. " +
                $"프리팹이 '<Feature>/Resources/{key}.prefab'에 있어야 합니다(GameSceneSetup으로 baking하세요).");
        }

        T component = prefab.GetComponent<T>();
        if (component == null)
        {
            throw new InvalidOperationException(
                $"[ResourceLoader] 프리팹 '{key}'의 루트에 {typeof(T).Name} 컴포넌트가 없습니다(component not on prefab root).");
        }

        return component;
    }
}

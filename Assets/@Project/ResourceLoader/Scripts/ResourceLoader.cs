using System;
using UnityEngine;

/// <summary>
/// 로직 root 프리팹을 컴포넌트 클래스 이름으로 로드하는 중앙 무상태 로더다.
/// 캐시/수명 소유가 없다 — 반환된 프리팹 컴포넌트를 Instantiate하고 그 인스턴스 수명을 책임지는 것은 호출부다.
/// 로드 키 = "Roots/" + typeof(T).Name (여러 피처의 Resources/Roots 하위가 하나의 병합 네임스페이스로 합쳐진다).
/// 문자열 키를 홀더에 두지 않고 클래스 이름으로 직접 로드하므로 로더와 프리팹 저작이 어긋날 수 없다.
/// </summary>
public static class ResourceLoader
{
    private const string RootsFolder = "Roots/";

    /// <summary>
    /// 로직 root 프리팹을 클래스 이름으로 로드해 root의 T 컴포넌트를 반환한다. Instantiate는 호출부가 수행한다.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// 프리팹을 Resources에서 찾지 못하거나 프리팹 루트에 T 컴포넌트가 없으면 발생한다.
    /// </exception>
    public static T LoadRoot<T>()
        where T : Component
    {
        string key = RootsFolder + typeof(T).Name;

        GameObject prefab = Resources.Load<GameObject>(key);
        if (prefab == null)
        {
            throw new InvalidOperationException(
                $"[ResourceLoader] root 프리팹을 Resources에서 로드하지 못했습니다: '{key}'. " +
                $"프리팹이 '<Feature>/Resources/{key}.prefab'에 있어야 합니다(GameSceneSetup으로 baking하세요).");
        }

        T component = prefab.GetComponent<T>();
        if (component == null)
        {
            throw new InvalidOperationException(
                $"[ResourceLoader] root 프리팹 '{key}'의 루트에 {typeof(T).Name} 컴포넌트가 없습니다(root component not on prefab root).");
        }

        return component;
    }
}

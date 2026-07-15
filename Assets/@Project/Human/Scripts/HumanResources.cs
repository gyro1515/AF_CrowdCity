/// <summary>
/// Human 피처가 소유하는 런타임 로드 경로 상수 홀더다.
/// Resources.Load 소비자(CrowdRoot)와 Editor 셋업이 같은 값을 참조해 경로가 어긋나지 않게 한다.
/// 경로 스킴: "&lt;피처세그먼트&gt;/&lt;자산명&gt;" — 병합 Resources 네임스페이스 충돌을 막기 위해 피처 폴더명을 세그먼트로 쓴다.
/// </summary>
public static class HumanResources
{
    // Assets/@Project/Human/Resources/Human/Human.prefab 를 가리킨다(확장자/최상위 Resources 접두 생략).
    public const string HumanPrefab = "Human/Human";
}

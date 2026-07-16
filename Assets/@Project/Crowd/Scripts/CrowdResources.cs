/// <summary>
/// Crowd 피처가 소유하는 런타임 로드 경로 상수 홀더다.
/// Resources.Load 소비자(GameplayRoot)와 Editor 셋업(GameSceneSetup)이 같은 값을 참조해 경로가 어긋나지 않게 한다.
/// 경로 스킴: "&lt;피처세그먼트&gt;/&lt;자산명&gt;" — 병합 Resources 네임스페이스 충돌을 막기 위해 피처 폴더명을 세그먼트로 쓴다.
/// </summary>
public static class CrowdResources
{
    // Assets/@Project/Crowd/Resources/Crowd/CrowdRoot.prefab 를 가리킨다(확장자/최상위 Resources 접두 생략).
    public const string CrowdRoot = "Crowd/CrowdRoot";

    // Assets/@Project/Crowd/Resources/Crowd/WallSdf.asset(정적 도시 벽 SDF) 를 가리킨다(확장자/최상위 Resources 접두 생략).
    // 베이커의 편집용 원본은 City/Generated/WallSdf.asset이며, 이 런타임 로드용 사본은 같은 .bytes payload를 공유한다.
    public const string WallSdf = "Crowd/WallSdf";
}

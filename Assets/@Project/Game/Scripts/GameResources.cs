/// <summary>
/// Game 피처가 소유하는 런타임 로드 경로 상수 홀더다.
/// Resources.Load 소비자(GameSceneController/GameplayRoot)와 Editor 셋업(GameSceneSetup)이 같은 값을 참조해
/// 경로가 어긋나지 않게 한다.
/// 경로 스킴: "&lt;피처세그먼트&gt;/&lt;자산명&gt;" — 병합 Resources 네임스페이스 충돌을 막기 위해 피처 폴더명을 세그먼트로 쓴다.
/// GameplayRoot/InputRoot/CameraRoot는 Game 피처 소유이므로 "Game/" 세그먼트를 공유하고 자산명으로 구분한다.
/// </summary>
public static class GameResources
{
    // Assets/@Project/Game/Resources/Game/GameplayRoot.prefab 를 가리킨다(확장자/최상위 Resources 접두 생략).
    public const string GameplayRoot = "Game/GameplayRoot";

    // Assets/@Project/Game/Resources/Game/InputRoot.prefab 를 가리킨다.
    public const string InputRoot = "Game/InputRoot";

    // Assets/@Project/Game/Resources/Game/CameraRoot.prefab 를 가리킨다.
    public const string CameraRoot = "Game/CameraRoot";
}

/// <summary>
/// Hud 피처가 소유하는 런타임 로드 경로 상수 홀더다.
/// Resources.Load 소비자(GameplayRoot/HudRoot)와 Editor 셋업(GameSceneSetup)이 같은 값을 참조해 경로가 어긋나지 않게 한다.
/// 경로 스킴: "&lt;피처세그먼트&gt;/&lt;자산명&gt;" — 병합 Resources 네임스페이스 충돌을 막기 위해 피처 폴더명을 세그먼트로 쓴다.
/// </summary>
public static class HudResources
{
    // 동적 리더 라벨 템플릿(팀당 1). HudRoot가 Instantiate 후 팀 아웃라인 material/텍스트만 주입한다.
    public const string CrowdLabel = "Hud/CrowdLabel";

    // 동적 라이벌 방향/인원 마커 템플릿(라이벌 팀당 1). HudRoot가 Instantiate 후 팀 색/카운트만 주입한다.
    public const string RivalMarker = "Hud/RivalMarker";
}

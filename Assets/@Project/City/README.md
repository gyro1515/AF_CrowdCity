# City

`City.fbx`는 원본 소스이며 수정하지 않는다. `CityBuildingsGenerator`가 FBX의 결합 `Buildings` mesh를
Editor에서만 분석해 37개 개별 mesh/prefab으로 생성하고, `GameSceneSetup`이 기존
`GameArea/City/Buildings` root와 transform을 유지한 채 그 prefab instance들을 자식으로 배치한다.

생성기는 source vertex/triangle/channel signature, mesh 최저 Y에서 얻은 37개 seed,
모든 연결 성분의 단일 footprint 귀속을 asset 쓰기 전에 검증한다. 생성 mesh는 원본 position,
normal, tangent, UV0와 triangle winding을 그대로 복사하며 vertex weld나 normal/tangent 재계산을 하지 않는다.
기존 scene 자식은 이름이 아니라 대응 prefab source와 override 유무까지 확인한다. 생성 asset은 file/meta
snapshot transaction으로, scene 변경은 완성된 임시 hierarchy와 Undo transaction으로 처리해 실패 시 원복한다.
`Setup Game Scene`은 City 상태를 읽기 전용으로 검사하며, City asset/scene 수렴은 `Setup City Buildings`만 수행한다.
Local City Setup은 열린 scene이 단 하나인 GameScene일 때만 실행한다. GameSceneController가 없어도 건물
수렴은 완료하며, controller 생성과 cityRoot/mainCamera/material 전체 배선은 이후 Full Setup이 담당한다.

런타임 차폐 상태는 `CameraRoot`가 소유한다. `GameplayRoot`가 scene의 City 참조와 전용
`City_Occluded.mat`을 직접 전달하며 EventBus/QueryBus는 사용하지 않는다. `CameraRoot`는 전달받은
asset을 변경하지 않고 runtime material을 한 번 복제해 `ShadowCaster` pass를 활성화한다. 카메라 pose
계산 뒤 플레이어-카메라 구간을 검사하고, 가린 건물 prefab의 `sharedMaterials`만 이 clone으로 전환한다.
target 교체/소실과 시야 해제에는 원본 material로 복구하며, 재초기화, Shutdown, OnDestroy에는 원복 후
소유한 runtime material을 파괴한다.

## Decision Log

1. 대상 기능 소유자: 분할 asset은 City Editor generator, 런타임 차폐 상태와 material clone은 CameraRoot
2. 새 파일 위치: `Assets/@Project/City/Editor/{CityBuildingsGenerator.cs,CityBuildingsGeneratorTests.cs}`, `Assets/@Project/City/Generated/BuildingMeshes`, `Assets/@Project/City/Materials/City_Occluded.mat`, `Assets/@Project/City/Prefabs/GeneratedBuildings`, `Assets/@Project/City/README.md`, `Assets/@Project/Game/Editor/CameraRootLifecycleTests.cs`
3. 상태 위치: 원본 투명 material은 asset, 현재 차폐 집합과 ShadowCaster runtime Material은 CameraRoot field
4. 통신 방식: GameSceneController -> GameplayRoot -> CameraRoot 직접 초기화 전달
5. 영향 파일: `Assets/@Project/City` generator/tests/generated meshes/prefabs/material/README, `Assets/@Project/Scenes/GameScene.unity`, `Assets/@Project/Game/Editor/{GameSceneSetup.cs,GameSceneValidator.cs,CameraRootLifecycleTests.cs}`, `Assets/@Project/Game/Scripts/{CameraRoot.cs,GameSceneController.cs,GameplayRoot.cs}`, `Docs/CrowdCity/INTERFACES.md`(그 문서는 이후 폐기 — 현행 좌표는 `Docs/PROJECT_MAP.md`)

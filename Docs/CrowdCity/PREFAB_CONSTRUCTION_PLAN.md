# CrowdCity — 프리팹 기반 구성 패턴 적용 계획

> **상태:** 적용됨 (구현 완료, 2026-07-16). 최초 작성 2026-07-15.
> Codex + Claude 시니어 다중 라운드 교차검증 완료. 아래 계획은 프리팹 리팩터로 코드에 반영됐다: Human 프리팹에 `Human.cs`+enabled CC baking, feature root 5종(GameplayRoot/InputRoot/CameraRoot/CrowdRoot/HudRoot) + Human 1종 + HUD leaf 2종(CrowdLabel/RivalMarker) = 총 8종 프리팹화, `Resources.Load`+`Instantiate`+`Init(deps)` 조립, 피처 소유 경로 상수, 중복경로(`ResourcePathValidator`)·CC 스펙(`GameSceneValidator`) Editor 검증기, HudRoot uGUI 프리팹화. 구현과 달라진 원안 전제는 각 절에 **[정정]** 표기.

## 0. 목표와 배경

**리팩터 전 상태(기준선)**
- 런타임 오브젝트 트리를 거의 전부 코드로 조립 (`new GameObject` + `AddComponent`).
- 씬에는 `GameSceneController` 하나만 미리 배치, `Start()`에서 GameplayRoot→Crowd/Hud/Camera/Input, HUD 캔버스/라벨까지 코드 생성.
- 프로젝트 전체에서 유일한 런타임 `Instantiate(prefab)`는 `CrowdRoot.SpawnClone`의 `Human.prefab`. 그마저도 클론에 `Human.cs` + `CharacterController`를 런타임 `AddComponent`.

**리팩터 후 상태(적용됨, 2026-07-16)**
- 씬에는 부트스트랩 `GameSceneController` + 환경 오브젝트(Main Camera, GameArea/City, 비활성 GameArea/Human 저작 템플릿)만 남는다.
- 기능/스폰 대상은 스크립트 사전부착 프리팹 8종(`Human/Human`, `Game/{GameplayRoot,InputRoot,CameraRoot}`, `Crowd/CrowdRoot`, `Hud/{HudRoot,CrowdLabel,RivalMarker}`)으로 저작되고 `Resources.Load<GameObject>("<피처세그먼트>/<자산>")`+`Instantiate`+`Init(deps)`로 조립된다. HUD Canvas/타이머/순위표/결과오버레이/라벨·마커까지 프리팹 저작.
- `Human.prefab` 루트에 `Human.cs`와 `enabled` CharacterController가 baked(런타임 `AddComponent` 제거, `GetComponent`만 사용). `CrowdRoot`가 `Resources.Load(HumanResources.HumanPrefab)`로 스폰 대상을 직접 소유(`humanPrefab` 주입 체인 제거).
- 경로 상수: `HumanResources`/`CrowdResources`/`HudResources`(비-root 로드 키). root 프리팹은 per-root 상수 없이 중앙 `ResourceLoader.LoadRoot<T>()`가 `typeof(T).Name`에서 `Roots/<클래스이름>` 키를 파생한다. 중복 상대경로는 `ResourcePathValidator`(Editor), CC 스펙·`Human.cs` baking은 `GameSceneValidator`가 검증.

**목표**
- 기능/스폰 대상을 **스크립트가 사전 부착된 프리팹**으로 저작 → 런타임에 **동적 로드 + Instantiate + C# 이벤트/Init 바인딩**.
- 참조 모델: `Playable_Cat_Merge_Cafe`의 구성 레시피. 단 그 프로젝트의 `[SerializeField]` 직접 참조 대신 **동적 로딩**, leaf의 싱글턴/전역버스 접근 등 플레이어블-속도 지름길은 **배제**.

**검증 결론(핵심):** 트리 구조 + 의존성 흐름만 보면 이 계획 = Cat Merge의 *의도된* 패턴을 동적화한 것과 **일치(MATCH)**. 차이는 (a) 생성 방식(동적 vs 씬배치), (b) 추가된 pure-C# 결정론 커널 — 둘 다 트리 형태·흐름을 바꾸지 않음.

## 1. Decision Log (CLAUDE.md §1)

```
1. Target feature owner: 전 기능 루트 + Human (구성/생성 방식 전반)
2. New file location (적용됨): 피처별 자기완결 Resources 경로 — `Human/Resources/Human/Human.prefab`, `Game/Resources/Game/{GameplayRoot,InputRoot,CameraRoot}.prefab`, `Crowd/Resources/Crowd/CrowdRoot.prefab`, `Hud/Resources/Hud/{HudRoot,CrowdLabel,RivalMarker}.prefab`; 경로 상수 `{Human,Crowd,Game,Hud}/Scripts/{Human,Crowd,Game,Hud}Resources.cs`; 검증기 `Game/Editor/ResourcePathValidator.cs`; 규칙 문서 CLAUDE.md §11.5 + Docs/CrowdCity/. (계획 §3의 `UI`/`SO` 하위 폴더는 필요 전까지 생성하지 않음 — CLAUDE.md §0.2. 세그먼트는 "소유 피처 폴더명" 규약으로 Human=`Human/Human`.)
3. State location: SO(밸런스/카탈로그), RuntimeModel/GameSession(런타임) — 변경 없음
4. Communication method: 부모→자식 직접호출 + Init 주입 / 자식→부모 C# event / 원거리 사실만 feature root가 EventBus 1회 publish (기존 기본값 유지)
5. Affected files (적용됨): 코드 — GameSceneController, GameplayRoot, CrowdRoot, HudRoot, `{Human,Crowd,Game,Hud}Resources.cs`, Editor(GameSceneSetup/GameSceneValidator/ResourcePathValidator), 프리팹 8종; 문서 — CLAUDE.md §11.5, Docs/CrowdCity/DESIGN.md §3, INTERFACES.md G1·G3·C1, 본 문서.
```

## 2. 구성(생성) 방식 규칙
1. 씬에는 **부트스트랩 1개(`GameSceneController`) + 지정 환경 오브젝트(Main Camera, City)** 만 배치. 나머지는 프리팹에서 생성.
2. `new GameObject + AddComponent`로 기능 트리 조립 금지(HUD 캔버스/라벨 등 uGUI 포함).
3. 프리팹에 스크립트 **사전 부착**. 클론에 `AddComponent<기능스크립트>` 금지. (현재 Human 흐름이 위반 예 → 수정 대상)
4. 로드는 `Resources.Load<T>(path)` (MVP; 추후 Addressables).
5. 생성 소유권 체인: SceneController→GameplayRoot 프리팹, GameplayRoot→자식 루트 프리팹, 각 FeatureRoot→자기 스폰 대상. 생성자가 teardown 소유.
6. 모든 기능 루트 프리팹화(로직 전용 루트 포함 — 사용자 결정). 단 3~4장 가드레일 준수.

## 3. 로딩 & 폴더 구조 (Resources)
피처별 자기완결. 각 피처가 Scripts/Editor/이미지/사운드 + 자기 `Resources/{Prefabs,UI,SO}` 보유.

```
Assets/@Project/<Feature>/
├── Scripts/  Editor/  (이미지, 사운드 ...)
└── Resources/
    ├── Prefabs/
    ├── UI/
    └── SO/
```

- **병합 네임스페이스 주의(필수):** Unity가 모든 `Resources/`를 하나로 병합 → 로드 경로에 피처명이 안 들어감. **경로에 피처 세그먼트**(예: `Resources.Load<Human>("Crowd/Human")`)로 전역 유일성 확보. **중복 상대경로 Editor 검증기 필수**(Unity는 경고만 하고 임의 해석; 대소문자 무시; 확장자 생략; forward slash).
- 경로 문자열은 **피처 소유의 상수**로 관리(전역 창고 금지).
- 비용 각주: `Resources/` 자산은 항상 빌드에 포함 + 시작 인덱스 비용. 대형/원격/독립 릴리즈 필요 시 Addressables 이전.

## 4. 생명주기 / 바인딩 규칙 (Init-only)
- 프리팹은 **활성** 저작. 소유자가 Instantiate → `Init(deps)` 동기 호출로 주입.
- `Awake`/`OnEnable`은 **의존성-free + 외부로 조용히**. 금지(동기 호출 코루틴 prefix·등록 콜백 포함): 주입 의존성 읽기 / 프리팹 자산 밖 참조(부모·형제·타 피처·`Find*`) / 외부 이벤트·콜백·UnityEvent 발행 / 매니저·EventBus·QueryBus·spatial·tick 등록 / 의존성 읽는 코루틴 시작 / 주입 데이터로 구동(`CharacterController.Move`·주입 위치/힘). **허용:** 자기·자식 컴포넌트 캐싱, 상수 초기화 등 자기완결.
- **첫 외부 publish는 배선 완료 후 소유자 주도의 별도 단계.** 구독 순서는 안전장치가 아님.
- `Init`: **동기 + 정확히 1회**. 재활성/풀링 도입 시 재획득마다 재-Init, `OnEnable`에 재초기화 의존 금지.
- **Init throw 시:** 소유자가 방금 만든 클론 `Destroy` + 결정론 buffer 등록 금지. `Init`을 `buffer.Add`보다 먼저 호출.
- **Baked 물리 전제:** baked 콜라이더/CC는 Instantiate 즉시 live → Instantiate–Init 사이에 물리/Update/overlap 쿼리 없어야 함. 프레임 경계·코루틴 yield 넘어 스폰 금지(그 경우 inactive/CC-disabled).
- **inactive Instantiate = 예외 탈출구**(위 조건에 걸리는 경우만).
- 씬 저작 루트(`GameSceneController`)는 `Awake`에서 직렬화된 authored ref를 null 검증용으로 읽어도 됨(주입 런타임 의존성 아님).
- 문구: "Start는 다음 프레임"이 아니라 "동기 Instantiate→Init 완료 후, 첫 Update 전 Start 단계".

## 5. leaf 상호작용 & 통신 규칙 (기존 기본값 유지 + 승격 경로 명시)
- leaf는 소유자에게 **C# 이벤트/콜백**으로 알린다(= 권장 기본, 이것이 "leaf가 이벤트를 쏘는 것"). leaf는 전역 EventBus/QueryBus/싱글턴 접근 **금지**.
- 승격 경로: **leaf → 소유자 → (원거리 사실만) feature ROOT가 1회 EventBus publish.** 릴레이 체인 아님.
- despawn/풀 반환 시 델리게이트 해제.
- 이 프로젝트: 상호작용(전투·리쿠르트·리더 제거)은 **pure-C# 커널**(`AgentBuffer`/`CombatResolver`/`RecruitResolver`)이 계산, `CrowdRoot`가 커밋·publish. **Human은 상호작용 이벤트를 안 쏨**(이벤트 표면 ≈ 0).

## 6. CharacterController / 결정론 특례 (구현 반영 2026-07-16)
- **[정정] CC는 리더 전용이 아니다.** 원안은 "CC는 리더에게만 부착"을 전제로 프리팹 분리(옵션 a) 또는 "disabled baking + 리더만 enable"(옵션 b)을 제시했으나, **실제 채택·구현은 리더·팔로워·중립 전원이 `enabled` CharacterController로 city collider와 물리충돌하는 것이 사용자의 의도된 설계**다. 따라서 **단일 공유 `Human.prefab` 루트에 `enabled=true` CharacterController를 baked**했다(runtime `AddComponent` 제거, `GetComponent`만 사용). 프리팹 분리도, disabled 게이팅도 하지 않는다.
- **[정정] 이동도 전원 CC.Move.** 리더는 heading, 팔로워는 골든앵글 슬롯+분리, 중립은 wander를 모두 `CharacterController.Move`로 적용해 건물 collider와 충돌한다(원안 "팔로워/중립은 transform 조향만"은 stale). 유닛 CC 캡슐끼리는 전용 유닛 레이어 + `Physics.IgnoreLayerCollision`으로 서로의 이동을 막지 않는다(도시 벽만 막음).
- CC 스펙(`radius=0.35`, `height=1.8`, `center=(0,0.9,0)`, `skinWidth=0.08`)은 결정론 sim의 pinned 상수 → 프리팹에 baked하되 값은 **`GameSceneValidator`가 상수(`ControllerRadius/Height/CenterY/SkinWidth`)와 대조 + `enabled==true` 확인**(런타임 수리 금지). `Human.cs` baking 여부도 함께 검사.
- **[정정] 탈락 시 CC를 파괴하지 않는다.** ex-리더는 baked CC를 그대로 유지하고 `Human.SetLeader(false)`로 그림자 캐스팅만 끈 뒤 최종 생존 팀 팔로워로 편입되거나 중립화된다(buffer의 `IsLeader`가 최종 권한). 원안·DESIGN §2/§4·INTERFACES C1의 "ex-리더 CharacterController를 Destroy" 서술은 stale.
- 배치 `CheckSphere`는 모두 Instantiate **이전**에 수행(현행 유지) — 4장 baked-물리 전제 충족(Instantiate–Init 사이 물리/overlap 없음).
- **순수 C# sim 커널(`Crowd/Core`)과 `SimTuning`은 이번 리팩터에서 변경하지 않았다**(구성/로딩 전용). 커널은 프리팹 규칙에서 제외(테스트 가능성 보호).

## 7. 문서 반영 범위 & ④ CLAUDE.md 트림
- 프로젝트 특화 구성/로딩 규칙: **CLAUDE.md** + `Docs/CrowdCity/DESIGN.md §3` + `INTERFACES.md G2–G4`.
- 재사용 문서(`TEAM_ARCHITECTURE_GUIDE.md`, `AI_WORKING_RULES.md`)에는 **프로젝트 특화 규칙을 넣지 않음**. §11 일반 Resources 주의는 유지.
- ④ 트림(별도 작업): 상단 "Behavioral Guidelines" 블록 **축약**(삭제 아님 — 팀 회귀 방지, line 80의 "impossible scenarios" 인용·"Project-Specific Safety Precedence" 보존). **두 파일 통합 안 함.** Growth/Advanced 섹션 포인터화(안전 계약 라인 유지). §108/119 EventBus 문구 정리(precedence로 이미 해소 — 버그 아님).

## 8. 교차검증 결과 요약
| 검증 대상 | 판정 |
|---|---|
| 구성 방식 전반 (Unity/아키텍처/Codex) | AGREE-WITH-CHANGES → 보완 흡수 |
| Init-only 생명주기 (3자) | SOUND-WITH-CAVEATS |
| leaf 상호작용 규칙 (3자) | SOUND-WITH-NUANCE (직관 정당, 용어만 분리) |
| 트리+의존성흐름 vs Cat Merge (지름길 제외) | MATCH |

## 9. 적용 결과 (완료, 2026-07-16)
- **문서:** CLAUDE.md §11.5 "Prefab Construction Rules" 신설 + 상단 Behavioral Guidelines 축약(삭제 아님; "No error handling for impossible scenarios" 인용·"Project-Specific Safety Precedence" 블록 보존) / DESIGN.md §3 소유권·조립 체인·프리팹 로드 반영 / INTERFACES.md G1·G3·C1(humanPrefab 제거, Human 프리팹 Resources 경로, baked enabled CC 명시) / 본 문서 상태·§0·§1·§6·§9 갱신.
- **코드:** `Human.prefab`에 `Human.cs` + enabled CC baking(전원 물리충돌), feature root 5종(GameplayRoot/InputRoot/CameraRoot/CrowdRoot/HudRoot) + Human 1종 + HUD leaf 2종(CrowdLabel/RivalMarker) = 총 8종 프리팹화 + `Resources.Load` 전환, `GameplayRoot`/`GameSceneController`/`CrowdRoot` 부트스트랩·시그니처 조정(humanPrefab 주입 체인 제거), 피처별 `Resources/<세그먼트>/` 재배치 + 경로 상수 + `ResourcePathValidator`(중복경로) + `GameSceneValidator` CC/Human.cs 검사.
- 신규 Resources 경로 Decision Log는 §1에 반영.
- **정정 완료(문서 stale 정정 패스, 2026-07-16):** 에디터 파이프라인 세부(DESIGN §5, INTERFACES E1/E2)를 신규 프리팹 수렴 스텝(Human prefab baking 4b, feature-root/HUD prefab 수렴 4c)·Resources 경로(`Assets/@Project/Human/Resources/Human/Human.prefab`)·`Human.cs`+enabled CC baking·`ResourcePathValidator`·HUD 프리팹 저작/검증으로 갱신했다. DESIGN §2/§4의 "탈락 시 CC Destroy"와 §8 "팔로워/중립 벽 충돌 없음(리더만)"·"팔로워 벽 클리핑 = MVP 타협" 서술은 전원 baked enabled CC + `CharacterController.Move` + city collider 벽 충돌로 정정했다(§6 [정정] 일치). STATUS.md의 `humanPrefab` 배선 서술도 필드 제거·`Resources.Load` 소유로 갱신했다. **여전히 범위 밖:** NeutralCount 등 커널 상수 서술 불일치는 이번 구성/로딩 리팩터 및 문서 정정 범위 밖.

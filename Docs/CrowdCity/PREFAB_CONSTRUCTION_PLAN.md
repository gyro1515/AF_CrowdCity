# CrowdCity — 프리팹 기반 구성 패턴 적용 계획

> **상태:** 미적용 (문서·코드 아직 변경하지 않음). 작성 2026-07-15.
> Codex + Claude 시니어 다중 라운드 교차검증 완료. 실제 적용은 추후.

## 0. 목표와 배경

**현재 상태**
- 런타임 오브젝트 트리를 거의 전부 코드로 조립 (`new GameObject` + `AddComponent`).
- 씬에는 `GameSceneController` 하나만 미리 배치, `Start()`에서 GameplayRoot→Crowd/Hud/Camera/Input, HUD 캔버스/라벨까지 코드 생성.
- 프로젝트 전체에서 유일한 런타임 `Instantiate(prefab)`는 `CrowdRoot.SpawnClone`의 `Human.prefab`. 그마저도 클론에 `Human.cs` + `CharacterController`를 런타임 `AddComponent`.

**목표**
- 기능/스폰 대상을 **스크립트가 사전 부착된 프리팹**으로 저작 → 런타임에 **동적 로드 + Instantiate + C# 이벤트/Init 바인딩**.
- 참조 모델: `Playable_Cat_Merge_Cafe`의 구성 레시피. 단 그 프로젝트의 `[SerializeField]` 직접 참조 대신 **동적 로딩**, leaf의 싱글턴/전역버스 접근 등 플레이어블-속도 지름길은 **배제**.

**검증 결론(핵심):** 트리 구조 + 의존성 흐름만 보면 이 계획 = Cat Merge의 *의도된* 패턴을 동적화한 것과 **일치(MATCH)**. 차이는 (a) 생성 방식(동적 vs 씬배치), (b) 추가된 pure-C# 결정론 커널 — 둘 다 트리 형태·흐름을 바꾸지 않음.

## 1. Decision Log (CLAUDE.md §1)

```
1. Target feature owner: 전 기능 루트 + Human (구성/생성 방식 전반)
2. New file location: 각 피처 폴더의 Resources/{Prefabs,UI,SO}; 규칙 문서는 CLAUDE.md + Docs/CrowdCity/
3. State location: SO(밸런스/카탈로그), RuntimeModel/GameSession(런타임) — 변경 없음
4. Communication method: 부모→자식 직접호출 + Init 주입 / 자식→부모 C# event / 원거리 사실만 feature root가 EventBus 1회 publish (기존 기본값 유지)
5. Affected files: CLAUDE.md, Docs/CrowdCity/DESIGN.md(§3), Docs/CrowdCity/INTERFACES.md(G2-G4); 코드는 추후 리팩터 (GameSceneController, GameplayRoot, CrowdRoot, HudRoot, Human, 각 루트 스크립트, Human.prefab)
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

## 6. CharacterController / 결정론 특례
- CC는 현재 **리더에게만** 부착(`CrowdRoot.cs:304/307/329/349`). 활성 CC를 공유 `Human.prefab`에 baking하면 중립·팔로워까지 활성화 → **리더/비리더 프리팹 분리** 또는 **disabled CC baking + 소유자가 리더만 `enabled=true`**.
- CC 스펙(radius/height/center/skinWidth)은 결정론 sim의 pinned 상수 → 프리팹에 부착하되 값은 **Editor 검증기로 상수 일치 검사**(런타임 수리 금지).
- 배치 `CheckSphere`는 모두 Instantiate **이전**에 수행(현행 유지) — 4장 baked-물리 전제 충족.
- **순수 C# sim 커널은 프리팹 규칙에서 제외**(테스트 가능성 보호).

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

## 9. 적용 시 남은 작업 (추후)
- **문서 편집:** CLAUDE.md 구성 규칙 신설 + ④ 트림(분리) / DESIGN.md §3 / INTERFACES.md G2–G4.
- **코드 리팩터(별도, 범위 큼):** `Human.prefab`에 `Human.cs` baking + CC 리더 처리, 각 루트 프리팹화 + `Resources.Load` 전환, `GameplayRoot`/`GameSceneController` 부트스트랩 조정, `Resources/` 폴더 재배치 + 경로 상수 + 중복경로 Editor 검증기.
- 각 신규 Resources 경로마다 Decision Log 갱신.

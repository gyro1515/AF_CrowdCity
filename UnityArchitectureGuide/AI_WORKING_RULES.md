# Unity AI Working Rules

이 문서는 Unity 프로젝트에서 AI가 코드를 만들거나 수정할 때 따라야 하는 작업 규칙이다.

새 프로젝트에서 AI 에이전트가 자동으로 읽게 하려면 이 파일을 루트의 `AGENTS.md` 또는 해당 도구가 읽는 지침 파일로 복사한다.
이 파일은 `TEAM_ARCHITECTURE_GUIDE.md`의 필수 안전 규칙만 압축한 AI용 subset이다.

## 0. 기본 태도

- 복잡한 프레임워크를 만들지 않는다. 소유권, 상태 위치, 통신 방식, 수명 책임을 명확히 한다.
- 기존 프로젝트 컨벤션이 있으면 이 문서보다 먼저 확인한다.
- 불확실하면 추측하지 말고, 무엇이 불확실한지 말한 뒤 질문한다.
- 변경은 작게 한다. 요청과 직접 관련 없는 리팩터링, 정리, 이름 변경은 하지 않는다.
- 검증 가능한 성공 기준을 먼저 세우고, 가능한 범위에서 확인한 뒤 끝낸다.

## 0.1 기존 프로젝트 적용 원칙

이미 진행 중인 프로젝트에 이 문서를 적용할 때는 전면 개조하지 않는다.

- 현재 폴더 루트가 `Assets/_Project`인지 `Assets/Game`인지 먼저 확인하고, 기존 루트와 네이밍을 따른다.
- 기존 구조가 문서와 다르더라도 요청 범위 밖의 구조 변경은 하지 않는다.
- 새 기능, 새 파일, 격리 가능한 변경부터 이 기준을 적용한다.
- 기존 static hub, Manager, SaveData 직접 접근, 중앙 이벤트 구조를 발견하면 새 코드에서 확장하지 않는다.
- 기존 반례를 고치려면 별도 리팩터링 작업으로 분리하고, 영향 범위와 검증 방법을 먼저 제시한다.
- 위치가 애매하면 `rg --files`, 주변 폴더 구조, 기존 네임스페이스, asmdef를 확인한 뒤 결정한다.
- 같은 프로젝트 안에 경로 패턴이 둘 이상이면 임의로 고르지 말고 사용자에게 기준을 묻는다.

## 0.2 도입 단계

처음부터 모든 구조를 만들지 않는다. 현재 프로젝트 단계에 필요한 규칙만 적용한다.

### MVP 단계: 처음부터 필수

- 기능 소유자를 명확히 한다.
- 기본 통신 방식은 직접 호출, C# event/callback, 상위 바인딩이다.
- SO에 런타임 상태를 저장하지 않는다.
- 구독한 생명주기와 같은 생명주기에서 해제한다.
- 비동기 작업에는 취소와 예외 처리 기준을 둔다.
- Editor 코드는 runtime 코드와 분리한다.
- 보상, 결제, 재화, 랭킹처럼 민감한 값은 클라이언트만 믿지 않는다.
- EventBus 구현체를 미리 둘 수는 있지만, 기본 통신 방식으로 사용하지 않는다.
- MVP에서 EventBus를 쓰는 예외는 2개 이상 독립 경계 객체가 같은 "이미 일어난 사실"을 관찰해야 할 때로 제한한다.

### Growth 단계: 기능 간 연결이 늘어나면 도입

- 여러 기능 또는 경계 객체가 같은 "이미 일어난 사실"을 관찰하기 시작하면 EventBus를 도입한다. 단일 receiver이거나 상위 바인딩으로 충분하면 EventBus를 쓰지 않는다.
- 형제 기능 연결이 많아지고 즉시 조회/상태 변경 진입점이 필요해지면 QueryBus, Command를 도입한다.
- 동적 로딩/해제 책임이 여러 곳에 흩어지면 AssetProvider를 둔다.
- 여러 기능에서 같은 Runtime 값을 읽기 시작하면 읽기 전용 Runtime 인터페이스를 만든다.
- 컴파일 시간이나 참조 경계 문제가 생기면 주요 영역별 asmdef를 나눈다.

### Advanced 단계: 라이브 운영/대규모 협업에서 도입

- RuntimeRegistry는 세션 runtime endpoint가 많아질 때만 둔다.
- 기능별 asmdef는 기능 독립성과 팀 규모가 커졌을 때 도입한다.
- Remote Addressables, Remote Config rollout, 보상 서버 검증은 라이브 운영 기능이 들어가는 순간 필수 계약으로 다룬다.

## 1. 작업 전 Decision Log

새 public API, 새 상태 저장소, 새 이벤트/쿼리/세이브키/에셋키, 새 asmdef, 리소스 lifetime 변경이 생기면 먼저 아래 5줄을 정리한다.

```txt
1. 대상 기능 소유자:
2. 새 파일 위치:
3. 상태 위치: SO / RuntimeModel / SaveData / Server
4. 통신 방식: 직접 호출 / C# event/callback / 상위 바인딩 / EventBus / QueryBus / Command
5. 영향 파일:
```

Decision Log는 실제 커밋 메시지가 아니라 작업 전 판단 메모다. 코드에 남기라는 요청이 없으면 최종 보고에 포함하고, 팀이 별도 위치를 정했다면 기능 README 또는 `Docs/.../DECISIONS.md`에 남긴다.

판단이 애매하면 구현을 시작하지 말고 질문한다.

## 2. 소유권 규칙

- 씬은 조립하고, 기능은 소유한다.
- `SceneController`는 생성 순서와 연결만 담당한다.
- `GameplayRoot`는 여러 기능 루트의 연결과 기능 간 바인딩을 담당한다.
- `GameSession`은 Ready/Playing/Pause/GameOver 같은 세션 흐름을 담당한다.
- `FeatureRoot`는 자기 기능 내부의 생성, 해제, 규칙, RuntimeModel을 소유한다.
- 부모는 자식을 직접 호출할 수 있다.
- 자식은 부모를 직접 알지 않는다. 자식이 부모에게 알릴 때는 C# event/callback을 사용한다.
- 형제 기능끼리는 구현체를 직접 참조하지 않는다.

## 3. 상태 위치 규칙

- 원본 데이터, 밸런스, 에셋 참조, 카탈로그는 `ScriptableObject`에 둔다.
- 현재 플레이 중 변하는 상태는 `RuntimeModel` 또는 `GameSession`에 둔다.
- 앱을 껐다 켜도 유지해야 하는 값은 `SaveData`에 둔다.
- 결제, 재화, 보상, 랭킹, 치트에 민감한 값은 클라이언트가 최종 권위자가 되면 안 된다. 라이브 경제 기능에서는 `Server` 검증을 필수 계약으로 본다.
- Runtime 중 SO 값을 현재 상태 저장소처럼 수정하지 않는다.
- SO 런타임 캐시가 필요하면 원본 데이터와 플레이 상태를 분리한다.

## 4. 통신 방식 규칙

- 부모가 자식을 제어한다: 직접 호출.
- 자식이 부모에게 알린다: C# event/callback.
- 형제 기능을 연결한다: 상위 루트가 바인딩.
- 먼 기능에 이미 일어난 사실을 알린다: EventBus.
- 먼 기능의 현재 값을 즉시 읽는다: QueryBus 또는 읽기 전용 Runtime 인터페이스.
- 상태를 변경한다: 상태 소유자의 public API 또는 Command.
- EventBus/QueryBus는 말단 View, Tile, Button, Enemy에서 직접 사용하지 않는다.
- EventBus/QueryBus 사용자는 기능 루트, 세션, 튜토리얼, HUD, Analytics 같은 경계 객체로 제한한다.

여기서 "먼 기능"은 hierarchy 거리가 아니라 소유권 거리다. 부모/자식 관계가 아니고 서로 구현체를 직접 참조하면 안 되는 기능이면 먼 기능으로 본다.

## 5. EventBus 규칙

EventBus는 기본적으로 "이미 일어난 사실"만 전달한다.
보상, 결제, 재화, 저장, 게임 규칙 변경 요청은 EventBus가 아니라 상태 소유자 API 또는 Command로 보낸다.

좋은 예:

```txt
BoardItemMerged
CustomerOrderCompleted
RewardClaimSucceeded
RewardClaimFailed
```

나쁜 예:

```txt
GetGoldRequest -> GetGoldResponse
SetInventoryItem
RewardClaimRequested
EveryFrameEnemyPositionChanged
```

Payload 규칙:

- ID, 좌표, enum, 숫자, immutable snapshot 위주로 담는다.
- `GameObject`, `MonoBehaviour`, mutable collection, 내부 구현체를 넣지 않는다.
- 매 프레임 대량 데이터는 EventBus로 보내지 않는다.
- 구독은 반드시 해제한다.

이벤트 타입 위치:

- EventBus 폴더에는 `Publish`, `Subscribe`, `IEventBus` 같은 인프라만 둔다.
- `readonly struct` 이벤트 payload는 기본적으로 사건이 발생한 기능 폴더의 `Events`에 둔다.
- 다른 기능도 구독하는 공개 이벤트는 발생 기능의 `Contracts/Events`에 둔다.
- `Shared.Contracts/Events`는 앱 초기화, 세션 변경, 공통 재화 변경처럼 owner가 없는 공통 계약에만 쓴다.
- 이벤트 위치는 "누가 듣는가"가 아니라 "어느 도메인에서 발생한 사건인가"로 결정한다.
- 모든 이벤트를 `EventBus/Events`, `Shared/Events`, `GameEvents.cs`에 몰아넣지 않는다.

Dispatch 규칙:

- MVP-light EventBus는 Unity main thread 동기 dispatch를 기본으로 한다.
- subscriber 실행 순서에 게임 규칙이 의존하면 안 된다.
- subscriber 안에서 다시 `Publish`해야 한다면 순환 가능성과 실행 순서를 Decision Log에 남긴다.
- 이벤트 A가 B를 발행하고 B가 다시 A를 발행하는 순환 구조는 만들지 않는다.
- dispatch 중 구독/해제가 일어나도 안전한 구현이 아니라면, publish 전에 subscriber 목록을 snapshot으로 복사한다.

## 6. QueryBus 규칙

QueryBus는 즉시 필요한 값을 읽기 위한 창구다.

- 최소 API는 `Register<T>(T provider) : IDisposable`, `TryGet<T>(out T provider)` 정도로 작게 유지한다.
- QueryBus에는 구현체가 아니라 mutator가 없는 읽기 전용 인터페이스만 등록한다.
- QueryBus로 쓰기 명령을 보내지 않는다.
- Query 결과를 오래 캐싱하면 무효화 규칙을 같이 둔다.
- Provider 등록은 기능 루트나 상위 루트에서만 한다.
- Provider 해제는 등록한 생명주기와 같은 생명주기에서 한다.
- 같은 타입 중복 등록은 에러로 처리하거나 명시적인 교체 정책을 둔다.
- 개별 타일, 버튼, 적, 셀 같은 말단 객체는 Provider를 등록하지 않는다.
- 말단 객체나 매 프레임 hot path에서 QueryBus를 직접 호출하지 않는다.
- QueryBus가 Service Locator처럼 아무 기능이나 꺼내 쓰는 통로가 되지 않게 한다.

## 7. Command / API 규칙

Command/API는 상태 변경의 명시적 진입점이다.

- 상태를 소유한 기능 또는 도메인 서비스가 Command/API를 소유한다.
- 보상, 결제, 재화, 저장, 서버 요청은 EventBus가 아니라 Command/API로 처리한다.
- 호출자는 성공, 실패, 취소 결과를 알 수 있어야 한다.
- 비동기 Command는 `CancellationToken`을 받는다.
- 보상, 결제, 재화 Command는 중복 호출 방지 기준을 둔다.
- 라이브 경제 Command는 idempotency key, 서버 검증, 보상 ledger 필요 여부를 검토한다.
- 모든 쓰기 요청을 하나의 전역 `CommandBus`로 몰아넣지 않는다.
- 상태 변경 후 다른 기능이 알아야 하는 사실만 EventBus로 알린다.

## 8. 구독과 해제

구독은 구독한 생명주기와 같은 생명주기에서 해제한다.

```txt
Awake      -> OnDestroy
OnEnable   -> OnDisable
Initialize -> Dispose / Clear
Open       -> Close
OnSpawn    -> OnDespawn
```

- `OnEnable`에서 구독했다면 `OnDisable`에서 해제한다.
- `Initialize`에서 구독했다면 `Dispose`에서 해제한다.
- `OnSpawn`에서 구독했다면 `OnDespawn`에서 해제한다.
- `Initialize`가 두 번 호출될 수 있으면 기존 구독을 먼저 해제하거나 중복 초기화를 막는다.
- `OnDestroy`는 최종 안전망으로만 둔다.
- 풀링 객체는 풀에 돌아갈 때 이전 런타임 상태와 구독을 정리한다.

## 9. UniTask / Async 규칙

- 비동기 작업은 가능하면 `CancellationToken`을 받는다.
- MonoBehaviour 기반 작업은 `destroyCancellationToken` 또는 `GetCancellationTokenOnDestroy()`를 사용한다.
- 반복 async loop는 반드시 취소 조건을 가진다.
- 결과나 실패가 의미 있는 작업은 `await`한다.
- `.Forget()`은 실패해도 흐름에 영향을 주지 않는 작업에만 허용한다.
- `.Forget()`을 쓸 때는 작업 내부에서 예외를 처리하거나 exception handler를 둔다.

## 10. Resources / Addressables 규칙

- 동적 생성은 기능 루트 또는 해당 기능의 Factory가 소유한다.
- 생성한 쪽이 해제, Pool 반환, Addressables Release까지 책임진다.
- `Resources.Load("string/path")`를 여러 기능에 흩뿌리지 않는다.
- Resources path, Addressables key는 상수, CatalogSO, AssetId 같은 검색 가능한 위치에서 관리한다.
- Addressables handle은 인스턴스 lifetime보다 먼저 Release하지 않는다.
- `Addressables.InstantiateAsync`를 사용했다면 대응되는 Release 정책을 둔다.
- `LoadAssetAsync` 후 직접 `Instantiate`했다면 로드 handle 소유자가 인스턴스 lifetime 동안 살아 있어야 한다.

## 11. asmdef / Editor 규칙

- Editor 코드는 runtime 코드와 분리한다.
- `*.Editor.asmdef`는 Editor 플랫폼 전용으로 둔다.
- Editor assembly는 runtime assembly를 참조할 수 있다.
- Runtime assembly는 Editor assembly나 `UnityEditor`를 참조하면 안 된다.
- ScriptableObject 타입 코드가 런타임에서 필요하면 runtime assembly에 둔다.
- Custom inspector, importer, validator, build tool은 Editor assembly에 둔다.
- 기능별 asmdef는 필요할 때 도입한다. 너무 작은 단위까지 처음부터 쪼개지 않는다.
- `Feature.Contracts` assembly를 만들 때는 공개 이벤트, 읽기 전용 Runtime 인터페이스, 공용 ID만 둔다.
- `Feature.Contracts`는 해당 기능의 Runtime 구현체나 EventBus 구현체를 참조하지 않는다.
- 다른 기능은 `Feature.Runtime`이 아니라 필요한 경우 `Feature.Contracts`만 참조한다.
- `Shared.Contracts`는 owner가 없는 공통 계약에만 사용한다.
- 순환 참조를 해결하려고 아무 타입이나 Shared로 올리지 않는다.

## 12. 보안 / 라이브 운영 규칙

- 클라이언트 SaveData를 최종 진실로 믿지 않는다.
- 결제, 광고 보상, 이벤트 보상은 중복 지급과 위변조 가능성을 고려한다.
- IAP/광고/서버 보상/라이브 재화가 들어가면 영수증 검증, 광고 SSV, idempotency key, 보상 ledger, replay 방지, 서버 시간 기준을 검토한다.
- 외부 URL, deep link, push payload, 서버 응답은 검증 후 사용한다.
- Remote Config 값은 fallback/default와 범위 검증을 가진다.
- Remote Config, SaveData, Addressables 데이터는 schema version, min app version, fallback, migration 필요 여부를 검토한다.
- 게임 규칙, 가격, 이벤트, Remote Addressables catalog를 원격으로 바꾸면 kill switch, rollout, rollback 기준을 둔다.
- Analytics/Crash 로그에 개인정보, 토큰, 원문 계정 ID를 남기지 않는다.
- 개발용 cheat/debug menu가 프로덕션 빌드에 노출되지 않게 한다.

## 13. 금지 Anti-patterns

기본적으로 금지한다. 예외가 필요하면 기능 소유자, 이유, 영향 범위, 제거 조건을 Decision Log에 남긴다.

- 기존 프로젝트를 요청 범위 밖에서 전면 개조하기.
- God Manager 만들기.
- 기능 구현체끼리 직접 참조하기.
- 자식이 부모 또는 SceneController를 직접 참조하기.
- UI가 도메인 상태를 직접 수정하기.
- 현재 세션 상태를 전역 singleton/static에 두기.
- static event를 만들고 clear 책임을 정하지 않기.
- Shared 폴더를 미래형 공통 코드 창고로 쓰기.
- SO에 현재 플레이 상태 저장하기.
- SaveData를 RuntimeModel처럼 매 순간 직접 읽고 쓰기.
- EventBus로 요청-응답 RPC 흉내내기.
- QueryBus로 쓰기 명령 보내기.
- 말단 객체가 EventBus/QueryBus에 직접 붙기.
- 이벤트 payload에 내부 구현체나 mutable collection 넣기.
- EventBus 폴더에 도메인 이벤트 payload 모아두기.
- 소비자 기준으로 이벤트 payload 위치 정하기.
- Shared 폴더를 모든 이벤트 모음집으로 쓰기.
- 모든 쓰기 요청을 전역 CommandBus로 몰아넣기.
- QueryBus를 Service Locator처럼 사용하기.
- 말단 객체나 hot path에서 QueryBus를 반복 호출하기.
- 매 프레임 대량 데이터를 EventBus로 흘리기.
- 구독 해제 없이 event/EventBus를 구독하기.
- 예외 처리 없는 `.Forget()` 사용하기.
- Addressables handle 소유자를 잃어버리기.
- Runtime assembly에서 `UnityEditor` 참조하기.
- public API 변경과 내부 리팩터링을 한 작업에 섞기.

## 14. 작업 후 보고

작업 완료 보고에는 다음을 포함한다.

- 변경한 파일.
- 소유권, 상태 위치, 통신 방식 판단.
- 검증한 내용.
- 남은 리스크 또는 수동 확인이 필요한 부분.
- 기존 프로젝트 컨벤션과 문서 기준이 충돌한 경우, 어떤 쪽을 우선했는지.

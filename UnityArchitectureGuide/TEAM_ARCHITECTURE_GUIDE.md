# Unity 협업/확장 아키텍처 가이드

이 문서는 Unity 신규 프로젝트에서 사람과 AI가 같은 기준으로 구조를 만들고 수정하기 위한 팀 문서다.

목표는 복잡한 프레임워크를 만드는 것이 아니라, 씬, 기능, 런타임 상태, 이벤트, 리소스의 소유권과 계약을 명확히 해서 협업과 컨텐츠 확장을 견디는 구조를 만드는 것이다.

이 문서의 경로 예시는 신규 프로젝트 기준인 `Assets/Game`을 사용한다. 기존 프로젝트에 적용할 때는 루트명만 프로젝트 컨벤션에 맞게 바꾼다.

## 0. 핵심 기준

1. 씬은 조립하고, 기능은 소유한다.
2. 부모는 자식을 직접 호출하고, 자식은 부모를 직접 알지 않는다.
3. 기능끼리는 구현체를 직접 참조하지 않는다.
4. 플레이 중 변하는 상태는 ScriptableObject에 두지 않는다.
5. public API, 이벤트, Runtime 인터페이스, Asset key, Save key는 계약이다.
6. 공유화는 반복 이후에 한다.
7. 의존성을 없애려고 하지 말고, 의존성의 모양을 관리한다.

## 0.1 단계별 도입 기준

이 문서는 전체 지도를 보여주지만, 처음부터 모든 구조를 만들라는 뜻은 아니다. 구조는 프로젝트의 복잡도와 팀 규모가 실제로 커질 때 단계적으로 도입한다.

### MVP 단계: 처음부터 지킨다

작은 플레이어블, 프로토타입, 신규 프로젝트 초반에도 지킨다.

- 기능 소유자를 명확히 한다.
- SceneController는 조립과 초기화까지만 담당한다.
- FeatureRoot는 자기 기능의 생성, 해제, 규칙, 런타임 상태를 소유한다.
- 부모는 자식을 직접 호출하고, 자식은 C# event/callback으로 알린다.
- EventBus를 기본 통신 방식으로 쓰지 않는다. 먼저 직접 호출, C# event/callback, 상위 바인딩으로 충분한지 확인한다.
- 단, 하나의 fact를 HUD, Tutorial, Analytics, Sound처럼 2개 이상 독립 경계 객체가 관찰해야 한다면 session-scoped MVP-light EventBus를 제한적으로 사용할 수 있다.
- ScriptableObject에 현재 플레이 상태를 저장하지 않는다.
- 구독한 생명주기와 같은 생명주기에서 해제한다.
- 비동기 작업에는 취소와 예외 처리 기준을 둔다.
- Editor 코드는 runtime 코드와 분리한다.
- 결제, 보상, 재화, 랭킹처럼 민감한 값은 클라이언트만 믿지 않는다.

### Growth 단계: 연결이 늘어나면 도입한다

기능이 늘고, 형제 기능 간 연결이 복잡해지고, 여러 사람이 같은 영역을 만지기 시작하면 도입한다.

- EventBus: 여러 기능 또는 경계 객체가 같은 "이미 일어난 사실"을 관찰해야 하고, 상위 바인딩이 복잡해질 때 도입한다. 단일 receiver에는 쓰지 않는다.
- QueryBus: 먼 기능의 현재 값을 읽어야 하지만 구현체 직접 참조를 피해야 할 때.
- Command/API: 보상, 결제, 저장, 재화 변경처럼 검증과 부작용이 있는 상태 변경의 진입점이 필요할 때.
- AssetProvider: Resources/Addressables 호출과 Release 책임이 여러 곳에 흩어질 때.
- 주요 영역별 asmdef: 컴파일 시간이나 참조 경계 문제가 실제로 생길 때.

### Advanced 단계: 대규모 협업/라이브 운영에서 도입한다

팀 규모, 라이브 운영, 원격 컨텐츠, 패키지화 요구가 생기면 도입한다.

- RuntimeRegistry: 세션 안에서 공개 Runtime endpoint가 많아질 때.
- 기능별 asmdef: 기능 독립성과 직접 참조 차단이 중요해질 때.
- Remote Addressables 운영 정책: catalog rollout, rollback, kill switch가 필요할 때.
- 서버 권위 보상/경제 구조: IAP, 광고 보상, 라이브 재화, 랭킹을 운영할 때.

## 1. 역할 기준

이 문서의 `Root`, `Session`, `RuntimeModel`, `Service` 같은 이름은 역할을 설명하기 위한 말이다. 실제 클래스명은 프로젝트 컨벤션에 맞춘다.

| 역할 | 책임 | 하지 말아야 할 일 |
| --- | --- | --- |
| GlobalRoot | 앱 전체 서비스 소유 | 현재 스테이지/세션 상태 보관 |
| GlobalService | 사운드, 저장, 로딩, 분석, 네트워크 제공 | 보드, 손님, 전투 같은 현재 플레이 상태 직접 변경 |
| SceneController | 씬 조립, 의존성 연결, 초기화 순서 관리 | 개별 게임 규칙 처리 |
| GameSession | Ready/Playing/Pause/GameOver/Result 같은 세션 흐름 관리 | 기능 내부 구현체 직접 제어 |
| GameplayRoot | 여러 기능 루트 묶음 관리, 기능 간 바인딩 | 말단 View/Model 직접 제어 |
| FeatureRoot | 한 기능의 생성, 해제, 규칙, RuntimeModel 소유 | 다른 기능 구현체 직접 참조 |
| RuntimeModel | 플레이 중 변하는 상태 보관 | Unity 오브젝트 생명주기 직접 소유 |
| RuntimeRegistry | 세션에서 공개할 읽기 전용 Runtime endpoint 등록/해제 | 말단 객체 등록, 상태 변경 API 노출 |
| Command/API | 검증과 부작용이 있는 상태 변경의 명시적 진입점 | 단순 알림 이벤트처럼 남발 |
| ScriptableObject | 원본 데이터, 밸런스, 에셋 참조 | 현재 플레이 상태 저장 |

추가 용어:

- `Provider`: QueryBus나 AssetProvider처럼 값을 제공하는 등록 함수 또는 객체.
- `상위 바인딩`: 두 형제 기능을 서로 직접 참조시키지 않고 GameplayRoot나 SceneController가 연결하는 방식.
- `말단 객체`: 버튼, 타일, 셀, 이펙트, 개별 적처럼 기능 루트 아래에서 동작하는 작은 객체.
- `Read-only Runtime`: 외부에는 조회 메서드와 읽기 전용 프로퍼티만 노출하고, 쓰기 메서드는 숨긴 인터페이스.

## 2. 권장 전체 구조

아래 구조는 Growth 이후의 권장 지도다. MVP 단계에서 `EventBus`, `QueryBus`, `RuntimeRegistry`, 모든 기능별 Root를 한 번에 만들 필요는 없다.

```txt
AppRoot
└─ GlobalRoot
   └─ GlobalServices
      ├─ SceneLoadService
      ├─ SoundService
      ├─ SaveService
      ├─ AssetProvider
      ├─ LocalizationService
      └─ AnalyticsService

GameScene
└─ GameSceneController
   ├─ GameSession
   ├─ GameEventBus
   ├─ GameQueryBus
   ├─ GameRuntimeRegistry
   ├─ GameSceneConfigSO
   └─ GameplayRoot
      ├─ InputRoot
      ├─ BoardRoot
      ├─ CustomerRoot
      ├─ HudRoot
      ├─ TutorialRoot
      └─ CameraRoot
```

이름은 예시다. 중요한 것은 소유권, 생명주기, 외부 계약이 명확한지다.

## 3. 폴더와 asmdef 기준

컨텐츠 단위로 폴더를 만들고 그 안에 런타임 코드, Editor 코드, SO 에셋, 프리팹을 모으는 방식을 기본으로 한다.
MVP 단계에서는 Editor/runtime 분리를 먼저 지키고, 기능별 asmdef는 필요해질 때 나눈다.

```txt
Assets/Game/Content/SeasonPass/
  Contracts/
    Events/
      SeasonPassRewardClaimSucceededEvent.cs
    Runtime/
      ISeasonPassRuntime.cs
    Game.Content.SeasonPass.Contracts.asmdef
  Scripts/
    Core/
      SeasonPassManager.cs
      SeasonPassRuntime.cs
      SeasonPassSO.cs
    UI/
      UISeasonPass.cs
    Game.Content.SeasonPass.asmdef
  Editor/
    SeasonPassImporter.cs
    SeasonPassValidator.cs
    Game.Content.SeasonPass.Editor.asmdef
  ScriptableObjects/
  Prefabs/
  Art/
```

핵심 게임플레이 기능은 같은 모양으로 `Core` 아래에 둘 수 있다.

```txt
Assets/Game/Core/Board/
  Contracts/
    Events/
      BoardItemMergedEvent.cs
    Runtime/
      IBoardRuntime.cs
    Game.Core.Board.Contracts.asmdef
  Scripts/
    BoardRoot.cs
    BoardRuntime.cs
    Game.Core.Board.asmdef
  Editor/
    BoardValidator.cs
    Game.Core.Board.Editor.asmdef
  README.md
```

이 문서에서 `SeasonPass`는 컨텐츠 기능 예시이고, `Board`는 핵심 게임플레이 기능 예시다. 둘은 루트가 다른 것이 아니라 도메인 분류가 다르다.

기준:

- Editor 코드는 runtime 코드와 분리한다.
- `*.Editor.asmdef`는 Editor 플랫폼 전용으로 둔다.
- Editor assembly는 자기 runtime assembly를 참조할 수 있다.
- Runtime assembly는 Editor assembly나 `UnityEditor`를 참조하지 않는다.
- 런타임에서 읽는 SO 타입 코드는 runtime assembly에 둔다.
- importer, validator, custom inspector, build tool은 Editor assembly에 둔다.
- `Contracts` 폴더에는 다른 기능이 참조해도 되는 공개 이벤트, 읽기 전용 Runtime 인터페이스, 공용 ID만 둔다.
- 기능 내부에서만 쓰는 타입은 `Contracts`로 올리지 않는다.
- MVP에서 외부 공개 계약이 없다면 `Contracts` 폴더를 만들지 않아도 된다. 기능 내부 이벤트는 `Scripts/Events`로 시작하고, 다른 기능이 구독하기 시작하면 `Contracts/Events`로 올린다.
- 처음부터 모든 컨텐츠를 기능별 asmdef로 쪼개지는 않는다.

초기 프로젝트는 다음 정도로 시작할 수 있다.

```txt
Game.Shared.Contracts.asmdef
Game.Shared.EventBus.asmdef
Game.Shared.asmdef
Game.Core.asmdef
Game.Content.asmdef
Game.Content.Editor.asmdef
```

컨텐츠가 커지거나 독립성이 중요해지면 다음처럼 분리한다.

```txt
Game.Content.SeasonPass.Contracts.asmdef
Game.Content.SeasonPass.asmdef
Game.Content.SeasonPass.Editor.asmdef
Game.Content.LavaRush.Contracts.asmdef
Game.Content.LavaRush.asmdef
Game.Content.LavaRush.Editor.asmdef
```

분리 기준:

- 여러 명이 동시에 자주 수정한다.
- 컴파일 시간이 체감될 정도로 늘었다.
- 다른 컨텐츠가 직접 참조하면 안 되는 경계가 필요하다.
- Editor importer/validator가 많다.
- 패키지화, DLC, 이벤트 모듈처럼 독립성이 중요하다.

참조 방향 예시:

| Assembly | 참조 가능 | 참조 금지 |
| --- | --- | --- |
| Game.Shared.Contracts | Unity 런타임 기본 타입, owner 없는 공통 ID/이벤트/읽기 계약 | 특정 기능 구현체, EventBus 구현체 |
| Game.Shared.EventBus | EventBus 인터페이스/구현 | 게임 도메인 이벤트 payload |
| Game.Core | Shared.Contracts, Shared.EventBus, 필요한 공통 런타임 | Content.* 구현체 |
| Game.Core.Feature.Contracts | Shared.Contracts, 필요한 공용 ID | Feature Runtime 구현체, EventBus 구현체 |
| Game.Core.Feature | 자기 Feature.Contracts, Shared.Contracts, Shared.EventBus, 필요한 공통 런타임 | 다른 Feature Runtime 구현체 |
| Game.Content | Shared.Contracts, Shared.EventBus, Core | Editor assembly |
| Game.Content.Feature.Contracts | Shared.Contracts, 필요한 공용 ID | Feature Runtime 구현체, EventBus 구현체 |
| Game.Content.Feature | 자기 Feature.Contracts, Shared.Contracts, Shared.EventBus, Core | 다른 Feature Runtime 구현체 |
| Game.Content.Feature.Editor | 자기 runtime assembly, UnityEditor | Player runtime에서 필요한 코드 |

`Feature.Contracts` assembly는 공개 이벤트, 읽기 전용 Runtime 인터페이스, 공용 ID만 담는다. 다른 기능은 구현체가 필요하지 않다면 `Feature.Runtime`이 아니라 `Feature.Contracts`만 참조한다.

순환 참조가 생기면 아무 타입이나 Shared로 올리지 말고, 참조 방향을 다시 설계하거나 같은 assembly에 둘지 검토한다.

## 4. 상태 위치 기준

| 데이터 성격 | 위치 |
| --- | --- |
| 에디터에서 설정하고 여러 세션에서 재사용 | ScriptableObject |
| 플레이 중 생성되고 세션 종료 시 사라짐 | RuntimeModel / GameSession |
| 앱을 껐다 켜도 유지해야 함 | SaveData |
| 결제, 재화, 보상, 랭킹처럼 조작에 민감함 | Server authority 또는 서버 검증 가능 구조 |
| 런타임 조회를 빠르게 하기 위한 재생성 가능 캐시 | read-only lookup cache 또는 별도 Runtime cache |

ScriptableObject는 원본 데이터다. 런타임 상태 저장소가 아니다.

SO에 두기 좋은 것:

- 밸런스 값.
- 스테이지 설정.
- 캐릭터, 아이템, 스킬, 손님 정의.
- 프리팹 참조.
- Addressables key 또는 Resources path.
- 에셋 카탈로그.
- 재생성 가능한 읽기 전용 lookup cache.

SO에 두면 안 되는 것:

- 현재 점수.
- 현재 선택 대상.
- 현재 생성된 오브젝트 목록.
- 현재 게임 오버 여부.
- 현재 튜토리얼 진행 상태.
- 현재 로딩된 인스턴스 참조.
- 플레이 중 변하는 보드/전투/상점/인벤토리 상태.

## 5. 통신 방식 선택 기준

| 필요한 것 | 선택 |
| --- | --- |
| 부모가 자식을 제어한다 | 직접 메서드 호출 |
| 자식이 부모에게 알린다 | C# event/callback |
| 형제 기능끼리 연결이 필요하다 | GameplayRoot/SceneController가 바인딩 |
| 먼 기능에게 사건을 알린다 | EventBus |
| 먼 기능의 현재 값을 읽는다 | QueryBus |
| 상태를 변경한다 | 상태 소유자 API 또는 Command |
| 매 프레임 대량 데이터가 흐른다 | EventBus 금지, 같은 기능 내부 직접 참조 또는 read-only Runtime/Snapshot 고려 |

"먼 기능"은 hierarchy 거리가 아니라 소유권 거리다. 부모/자식 관계가 아니고 서로 구현체를 직접 참조하면 안 되는 기능이면 먼 기능으로 본다.

초보자용 한 줄 규칙:

```txt
EventBus = 사실 알림
QueryBus = 읽기 질문
Command/API = 상태 변경
```

### 5.1 Command/API 기준

Command/API는 검증, 저장, 서버 통신, 재화 변경, 보상 지급처럼 부작용이 있는 상태 변경의 명시적 진입점이다.

EventBus로 보내지 말아야 하는 것:

- 보상 수령 요청.
- 결제 처리 요청.
- 재화 증감.
- SaveData 변경.
- 서버 검증이 필요한 이벤트 보상.

권장 예:

```txt
ClaimRewardCommand
PurchaseProductCommand
GrantAdRewardCommand
ChangeCurrencyCommand
```

Command는 반드시 성공/실패/취소 결과를 호출자가 알 수 있어야 한다. 결과 사실은 필요하면 EventBus로 알릴 수 있다.

Command/API 기준:

- 상태를 소유한 기능 또는 도메인 서비스가 Command/API를 소유한다.
- 모든 쓰기 요청을 하나의 전역 `CommandBus`로 몰아넣지 않는다.
- 비동기 Command는 `CancellationToken`을 받는다.
- 호출자는 성공, 실패, 취소 결과를 구분할 수 있어야 한다.
- 보상, 결제, 재화 Command는 중복 클릭/중복 호출 방지 기준을 둔다.
- IAP, 광고 보상, 서버 보상, 라이브 재화 Command는 idempotency key, 서버 검증, 보상 ledger 필요 여부를 검토한다.
- 상태 변경 후 다른 기능이 알아야 하는 사실만 EventBus로 알린다.

예:

```csharp
public readonly struct CommandResult
{
    public readonly bool Succeeded;
    public readonly string ErrorCode;
}

public interface IClaimRewardCommand
{
    UniTask<CommandResult> ExecuteAsync(string rewardId, CancellationToken ct);
}
```

## 6. EventBus 기준

EventBus는 직접 참조하면 안 되는 기능 사이에 "이미 일어난 사실"을 전달한다. 상태 변경 요청, 보상 요청, 결제 요청은 EventBus가 아니라 상태 소유자 API 또는 Command로 보낸다.
EventBus가 있다는 것은 EventBus를 쓰라는 뜻이 아니다. 먼저 직접 호출, C# event/callback, 상위 바인딩으로 충분한지 확인한다.

좋은 사용:

- 보드 기능이 아이템 병합 완료를 알린다.
- 손님 기능이 주문 완료 또는 실패를 알린다.
- 보상 시스템이 보상 지급 성공/실패를 알린다.
- 튜토리얼, HUD, Analytics가 여러 기능의 사건을 관찰한다.

피해야 할 사용:

- 부모와 자식 사이의 단순 호출.
- 같은 기능 내부에서 기능 루트가 직접 중개할 수 있는 일.
- 호출 즉시 결과가 필요한 일.
- 검증, 저장, 결제, 보상 지급처럼 성공/실패가 중요한 상태 변경 요청.
- 매 프레임 대량으로 발생하는 데이터 전달.

### 6.1 MVP-light EventBus 최소 계약

MVP에서 EventBus를 조기 도입한다면 작은 도구로 제한한다.

- GameSession 또는 GameplayRoot가 소유하는 session-scoped 객체로 둔다.
- 전역 static EventBus로 만들지 않는다.
- API는 `Publish<T>()`, `Subscribe<T>(Action<T>) : IDisposable` 정도로 작게 유지한다.
- replay, priority, sticky event, async queue, request/response, wildcard subscribe는 MVP 범위에서 제외한다.
- Unity main thread에서 동기 dispatch를 기본으로 한다.
- subscriber 실행 순서에 게임 규칙이 의존하면 안 된다.
- subscriber 예외는 event type과 subscriber 정보를 포함해 로그한다. 한 subscriber 실패가 다른 subscriber 호출을 막지 않게 하는 정책을 기본으로 한다.
- subscriber 안에서 다시 `Publish`해야 한다면 순환 가능성과 실행 순서를 Decision Log에 남긴다.
- 이벤트 A가 B를 발행하고 B가 다시 A를 발행하는 순환 구조는 만들지 않는다.
- dispatch 중 구독/해제가 일어나도 안전한 구현이 아니라면 publish 전에 subscriber 목록을 snapshot으로 복사한다.
- 이벤트 이름은 `BoardItemMergedEvent`, `RewardClaimSucceededEvent`처럼 과거형 fact로 짓는다.
- payload는 ID, enum, 숫자, 좌표, immutable snapshot만 담는다.
- `GameObject`, `MonoBehaviour`, mutable collection, 내부 구현체를 넣지 않는다.
- 구독자는 FeatureRoot, GameSession, HUD, Tutorial, Analytics, Sound 같은 경계 객체로 제한한다.
- View, Tile, Button, Cell 같은 말단 객체는 직접 구독하지 않는다.
- 새 이벤트를 추가할 때는 Decision Log에 이벤트명, 발행자, 구독자 후보, payload, 사용 이유를 남긴다.
- 개발 중에는 이벤트별 subscriber count 또는 publish log를 확인할 수 있게 한다.

### 6.2 요청-응답 RPC 흉내 금지

나쁜 예:

```csharp
_events.Publish(new GetGoldRequest(requestId));
_events.Subscribe<GetGoldResponse>(OnGoldResponse);
```

이 구조는 이벤트를 함수 호출처럼 쓰는 것이다. 응답자가 없는지, 둘 이상인지, 순서가 어떤지, timeout은 어떻게 할지 모두 불명확해진다.

즉시 답이 필요하면 QueryBus나 명시 API를 사용한다.

```csharp
int gold = _queries.Get<IUserWalletRuntime>().Gold;
bool canBuy = _shop.CanBuy(itemId);
```

### 6.3 Payload 규칙

나쁜 예:

```csharp
public sealed class BoardItemMergedEvent
{
    public BoardItemBehaviour Item;
    public List<BoardCellBehaviour> Cells;
}
```

이벤트를 받은 쪽이 보드 내부 구현을 알게 되고, 전달된 오브젝트가 이미 Destroy되었거나 풀에 반납되었을 수 있다. mutable collection은 외부에서 바뀔 수도 있다.

좋은 예:

```csharp
public readonly struct BoardItemMergedEvent
{
    public readonly int ItemId;
    public readonly int Level;
    public readonly Vector2Int Cell;
}
```

Payload에는 받는 쪽이 알아야 할 사실만 담는다. ID, 좌표, enum, 숫자, immutable snapshot을 우선한다.

### 6.4 이벤트 타입 위치

EventBus 구현체와 이벤트 payload 타입은 같은 것이 아니다.
EventBus는 공용 인프라이고, `readonly struct` 이벤트 payload는 기능 간 계약이다.

권장 위치:

```txt
EventBus 구현체
-> Assets/Game/Shared/EventBus/
-> Publish, Subscribe, IEventBus 같은 인프라만 둔다.

기능 내부 이벤트
-> Assets/Game/Core/Board/Scripts/Events/BoardLocalSomethingEvent.cs

다른 기능도 구독하는 공개 이벤트
-> Assets/Game/Core/Board/Contracts/Events/BoardItemMergedEvent.cs

진짜 공통 소유 이벤트
-> Assets/Game/Shared/Contracts/Events/
```

기준은 "누가 듣는가"가 아니라 "어느 도메인에서 발생한 사건인가"다.
`BoardItemMergedEvent`는 HUD, Quest, Reward, Analytics가 듣더라도 Board 기능이 소유한다.
구독자는 Board 구현체가 아니라 Board의 공개 계약만 참조해야 한다.

asmdef를 쓰는 경우:

```txt
Game.Core.Board
-> Game.Core.Board.Contracts
-> Game.Shared.EventBus

Game.Core.Hud
-> Game.Core.Board.Contracts
-> Game.Shared.EventBus

Game.Core.Quest
-> Game.Core.Board.Contracts
-> Game.Shared.EventBus

Game.Content.SeasonPass
-> Game.Core.Board.Contracts
-> Game.Shared.EventBus
```

`Game.Shared.Contracts`는 owner가 없는 공통 계약에만 쓴다. 앱 초기화, 세션 변경, 공통 재화 변경처럼 여러 기능의 공통 언어일 때만 올리고, 모든 이벤트를 모으는 폴더로 쓰지 않는다.

이름은 팀에서 한 가지 스타일로 통일한다. 초보자와 검색성을 우선하면 `BoardItemMergedEvent`, `RewardClaimSucceededEvent`처럼 `[도메인][사건][결과]Event`를 권장한다.

## 7. QueryBus 기준

QueryBus는 먼 기능이 값을 즉시 읽어야 할 때 사용한다.
QueryBus에는 구현체가 아니라 mutator가 없는 읽기 전용 인터페이스만 등록한다.

최소 API는 작게 유지한다.

```csharp
IDisposable Register<T>(T provider);
bool TryGet<T>(out T provider);
```

좋은 예:

```csharp
public interface IBoardRuntime
{
    bool HasItem(int itemId, int level);
}
```

등록은 소유자인 `BoardRoot` 또는 상위 Root가 한다.

```csharp
private IDisposable _boardRuntimeRegistration;

public void Initialize(GameQueryBus queries)
{
    _boardRuntimeRegistration?.Dispose();
    _boardRuntimeRegistration = queries.Register<IBoardRuntime>(_runtime);
}

public void Dispose()
{
    _boardRuntimeRegistration?.Dispose();
    _boardRuntimeRegistration = null;
}
```

조회는 필요한 경계 객체가 한다.

```csharp
private bool CanServe(OrderRequest order)
{
    if (!_queries.TryGet<IBoardRuntime>(out var board))
        return false;

    return board.HasItem(order.RequiredItemId, order.RequiredLevel);
}
```

금지:

```csharp
_queries.Get<IInventoryRuntime>().AddItem(itemId);
```

Query는 질문이다. 쓰기 명령을 보내면 상태 소유자가 검증, 저장, 이벤트 발행, UI 갱신을 통제하지 못한다.
위 예시처럼 QueryBus를 통해 `AddItem`, `SetGold`, `ClaimReward` 같은 쓰기 메서드가 보이면 설계 실패로 본다.

규칙:

- Provider 등록은 기능 루트나 상위 루트에서만 한다.
- Provider 해제는 등록한 생명주기와 같은 생명주기에서 한다.
- Provider가 중복 등록되면 에러로 처리하거나 명시적으로 교체 정책을 둔다.
- Provider가 없으면 `TryGet` 실패를 정상 흐름으로 처리한다.
- 말단 View, Tile, Button, Enemy는 QueryBus에 직접 붙지 않는다.
- 말단 객체나 매 프레임 hot path에서 QueryBus를 직접 호출하지 않는다.
- QueryBus로 쓰기 작업을 하지 않는다.
- Query 결과를 오래 캐싱할 때는 무효화 규칙을 같이 둔다.

## 8. 말단 객체가 Bus에 붙으면 안 되는 이유

나쁜 예:

```csharp
public sealed class TileView : MonoBehaviour
{
    private void OnEnable()
    {
        _events.Subscribe<GamePaused>(OnPaused);
    }
}
```

타일이 100개면 구독도 100개가 된다. 풀링되면 중복 구독 위험도 커진다. 버튼, 타일, 적, 셀 같은 말단 객체가 전역 Bus를 알기 시작하면 전역에 연결된 작은 코드 조각이 폭발한다.

좋은 구조:

```txt
BoardRoot가 EventBus 구독
BoardRoot가 TileView들을 직접 제어
TileView는 BoardRoot에게 C# event/callback으로만 알림
```

말단 객체는 자기 부모 또는 자기 기능 루트의 소유 안에 머물러야 한다.

## 9. 구독과 해제

구독은 구독한 생명주기와 같은 생명주기에서 해제한다.

```txt
Awake      -> OnDestroy
OnEnable   -> OnDisable
Initialize -> Dispose / Clear
Open       -> Close
OnSpawn    -> OnDespawn
```

예:

```csharp
private void OnEnable()
{
    _button.onClick.AddListener(OnClick);
}

private void OnDisable()
{
    _button.onClick.RemoveListener(OnClick);
}
```

명시 초기화 구조:

```csharp
private IDisposable _sub;

public void Initialize(GameEventBus events)
{
    Dispose();
    _sub = events.Subscribe<BoardItemMergedEvent>(OnItemMerged);
}

public void Dispose()
{
    _sub?.Dispose();
    _sub = null;
}

private void OnDestroy()
{
    Dispose();
}
```

`OnDestroy`는 최종 안전망이다. 팝업, 타일, 적, 셀은 Destroy되지 않고 비활성화되거나 풀에 돌아갈 수 있으므로 `OnDestroy`만 믿으면 안 된다.

## 10. UniTask / Forget

`.Forget()`은 이 비동기 작업을 기다리지 않겠다는 뜻이다.

나쁜 예:

```csharp
private void OnClick()
{
    ClaimRewardAsync().Forget();
}
```

보상 지급, 결제, 저장, 씬 로딩처럼 실패가 의미 있는 작업은 기다리거나 예외를 처리해야 한다.

좋은 예:

```csharp
private async UniTask OnClickAsync(CancellationToken ct)
{
    try
    {
        await ClaimRewardAsync(ct);
    }
    catch (Exception ex)
    {
        Debug.LogException(ex);
        ShowErrorPopup();
    }
}
```

정말 기다릴 수 없는 작업이면 작업 내부에서 예외를 처리하거나 exception handler를 둔다.

```csharp
ClaimRewardAsync(ct).Forget(ex =>
{
    Debug.LogException(ex);
});
```

Unity Button처럼 `void` 이벤트에 연결해야 한다면 wrapper에서 중복 클릭을 막고 예외를 기록한다.

```csharp
private bool _isClaiming;

public void OnClickClaim()
{
    if (_isClaiming) return;
    ClaimRewardButtonAsync(destroyCancellationToken).Forget(Debug.LogException);
}

private async UniTask ClaimRewardButtonAsync(CancellationToken ct)
{
    _isClaiming = true;
    try
    {
        await _claimRewardCommand.ExecuteAsync(ct);
    }
    finally
    {
        _isClaiming = false;
    }
}
```

## 11. 동적 생성과 리소스 로딩

동적 생성은 기능 루트 또는 해당 기능의 Factory가 소유한다.

- 어떤 기능이 생성했는지 명확해야 한다.
- 생성한 기능이 해제, Pool 반환, Addressables Release까지 책임진다.
- 동적 생성된 객체가 다른 기능에 직접 등록되면 안 된다.
- 다른 기능이 알아야 할 정보는 RuntimeModel, EventBus, QueryBus로 전달한다.

Addressables 기준:

- `Addressables.InstantiateAsync`를 사용했다면 대응되는 Release 정책을 둔다.
- `LoadAssetAsync` 후 직접 `Instantiate`했다면 로드 handle이 인스턴스 lifetime 동안 살아 있어야 한다.
- 로드 handle을 즉시 Release하고 인스턴스만 남기는 예시는 피한다.

Resources 기준:

- 작은 프로젝트 또는 소량 로컬 에셋에는 허용할 수 있다.
- 허용 예: 작은 아이콘, 로컬 전용 임시 프리팹, 에디터 검증용 소량 리소스.
- 피해야 할 예: 캐릭터/맵/대형 프리팹 묶음, 라이브 교체가 필요한 이벤트 에셋, 여러 기능에서 문자열로 반복 로드하는 리소스.
- 문자열 경로를 여기저기 흩뿌리지 않는다.
- AssetId, CatalogSO, 상수 클래스로 경로를 관리한다.
- 큰 에셋을 무분별하게 Resources에 넣지 않는다.

## 12. 보안 / 라이브 운영

클라이언트는 편집 가능한 실행 환경이다. 보안에 민감한 값은 클라이언트만 신뢰하지 않는다.

- 클라이언트 SaveData를 결제, 재화, 보상 검증의 최종 권위자로 취급하지 않는다.
- 결제, 광고 보상, 이벤트 보상은 중복 지급과 위변조 가능성을 고려한다.
- 서버 보상 API는 중복 호출되어도 안전한지 확인한다.
- IAP가 있으면 영수증 검증을 설계에 포함한다.
- 광고 보상이 있으면 가능하면 서버 측 검증(SSV) 또는 중복 지급 방지 장치를 둔다.
- 서버 보상은 idempotency key, 보상 ledger, replay 방지, 서버 시간 기준을 검토한다.
- 외부 URL, deep link, push payload, 서버 응답은 검증 후 사용한다.
- Remote Config 값은 fallback/default와 범위 검증을 가진다.
- Remote Config, SaveData, Addressables 데이터는 schema version, min app version, fallback, migration 필요 여부를 검토한다.
- Remote Config로 게임 규칙, 가격, 이벤트, 라이브 경제 값을 바꾸면 kill switch와 rollout/rollback 기준을 둔다.
- Analytics/Crash 로그에 개인정보, 토큰, 원문 계정 ID를 남기지 않는다.
- 개발용 cheat/debug menu가 프로덕션 빌드에 노출되지 않게 한다.
- Remote Addressables catalog 업데이트에는 롤백 정책이 필요하다.

## 13. Anti-patterns

아래 패턴은 기본적으로 금지한다. 예외가 필요하면 기능 소유자, 이유, 영향 범위, 제거 조건을 Decision Log에 남긴다.

### Ownership / Dependency

- God Manager 만들기: 하나의 Manager가 세션, UI, 저장, 리소스, 이벤트, 게임 규칙을 모두 아는 구조.
- 기능 구현체끼리 직접 참조하기: `Board -> Customer`, `HUD -> BoardController` 같은 참조.
- 자식이 부모를 직접 참조하기: `ChildView -> SceneController`, `Tile -> BoardRoot`.
- UI가 도메인 상태를 직접 수정하기.
- 현재 세션 상태를 전역 singleton/static에 두기.
- static event를 만들고 clear 책임을 정하지 않기.
- Shared 폴더를 미래형 공통 코드 창고로 쓰기.

### State / Data

- ScriptableObject에 현재 플레이 상태 저장하기.
- 런타임 중 SO 값을 직접 변경하고 원본 데이터처럼 재사용하기.
- SaveData를 RuntimeModel처럼 매 순간 직접 읽고 쓰기.
- 클라이언트 SaveData를 결제, 재화, 보상 검증의 권위자로 취급하기.
- Remote Config 값을 검증이나 fallback 없이 바로 게임 규칙에 사용하기.
- 서버 응답, push payload, deep link 값을 신뢰하고 바로 실행하기.

### EventBus / QueryBus

- EventBus로 요청-응답 RPC 흉내내기.
- QueryBus로 쓰기 명령 보내기.
- 말단 View, Tile, Button, Enemy가 EventBus/QueryBus에 직접 붙기.
- 이벤트 payload에 `GameObject`, `MonoBehaviour`, mutable collection, 내부 구현체를 넣기.
- EventBus 폴더에 도메인 이벤트 payload를 모아두기.
- 소비자 기준으로 이벤트 payload 위치를 정하기.
- `Shared.Contracts`를 모든 이벤트 모음집으로 쓰기.
- 모든 쓰기 요청을 전역 `CommandBus`로 몰아넣기.
- QueryBus를 Service Locator처럼 사용하기.
- 말단 객체나 hot path에서 QueryBus를 반복 호출하기.
- 매 프레임 대량 데이터를 EventBus로 흘리기.
- 구독 해제 없이 event/EventBus를 구독하기.
- `OnEnable`마다 구독하고 `OnDisable`에서 해제하지 않기.

### Resources / Addressables

- Addressables 로드 핸들을 인스턴스 lifetime보다 먼저 Release하기.
- `Addressables.LoadAssetAsync` 후 직접 `Instantiate`하고 handle 소유자를 잃어버리기.
- `Resources.Load("string/path")`를 여러 기능에 흩뿌리기.
- 큰 에셋을 Resources 폴더에 무분별하게 넣기.
- Addressables instance를 `Destroy`만 하고 Release 정책을 정하지 않기.
- 로딩 방식은 Provider를 쓰면서 해제는 호출자가 제각각 처리하기.
- Asset key, Resources path를 상수/카탈로그 없이 문자열로 반복하기.

### Async / Lifecycle

- CancellationToken 없는 UniTask 반복 루프 만들기.
- 예외 처리 없는 `.Forget()` 사용하기.
- 초기화 순서를 `Awake`, `Start` 우연에 맡기기.
- 풀링 객체에서 이전 런타임 상태를 리셋하지 않기.
- 이벤트 구독 해제를 `OnDestroy`에만 기대하기.
- 생성한 기능과 해제하는 기능이 다른데 소유권 문서가 없는 구조.
- 씬 언로드, 세션 리셋, 앱 종료에서 cleanup 경로가 서로 다른 구조.

### asmdef / Folder Boundary

- Runtime assembly에서 `UnityEditor` 참조하기.
- Editor 코드와 Runtime 코드를 같은 asmdef에 섞기.
- 기능별 asmdef가 서로 직접 참조하게 만들기.
- 순환 참조를 해결하려고 아무 타입이나 Shared로 올리기.
- 너무 작은 폴더마다 asmdef를 만들어 참조 관리 비용만 늘리기.
- public API 변경과 내부 리팩터링을 한 작업에 섞기.

### Security / Live Ops

- 결제 성공, 광고 보상, 이벤트 보상을 클라이언트 플래그만으로 확정하기.
- 서버 보상 API를 중복 호출해도 안전한지 검증하지 않기.
- 외부 URL/deep link를 allowlist 없이 열거나 파싱하기.
- Analytics/Crash 로그에 개인정보, 토큰, 원문 계정 ID를 남기기.
- 개발용 cheat/debug menu가 프로덕션 빌드에 노출되기.
- Remote Addressables catalog 업데이트의 롤백 정책이 없는 상태로 라이브 운영하기.
- 난독화만으로 보안이 해결된다고 가정하기.

### AI / Collaboration

- AI가 기능 소유자 확인 없이 새 구조를 만들기.
- 반복 근거 없이 새 Shared abstraction 만들기.
- 기존 컨벤션을 무시하고 별도 스타일을 도입하기.
- Decision Log 없이 public API, 이벤트, Save key, Asset key를 바꾸기.
- 테스트/검증 방법이 없는 상태로 구조 변경을 완료 처리하기.

## 14. 작업 체크리스트

작업 전:

- 이 변경의 기능 소유자는 누구인가?
- 새 Runtime 상태는 SO, RuntimeModel, SaveData, Server 중 어디에 속하는가?
- 다른 기능 구현체와 직접 참조가 생기지 않는가?
- EventBus가 필요한가, QueryBus가 필요한가, 상위 바인딩으로 충분한가?
- 새 public API가 계약으로 남아도 되는가?
- 동적 생성 객체의 해제 책임은 어디에 있는가?
- Resources 또는 Addressables key가 검색 가능한 위치에서 관리되는가?
- asmdef 참조 방향이 깨지지 않는가?

작업 후:

- C# event 구독 해제가 있는가?
- EventBus 구독 해제가 있는가?
- CancellationToken 취소가 있는가?
- Pool 반환 또는 Addressables Release가 있는가?
- RuntimeRegistry에 말단 객체가 직접 등록되지 않았는가?
- SO에 플레이 중 상태를 저장하지 않았는가?
- 새 이벤트 payload가 구체 MonoBehaviour나 내부 구현체를 노출하지 않는가?
- 새 이벤트 payload 위치가 발생 도메인의 `Events` 또는 `Contracts/Events`인가?
- QueryBus가 쓰기 명령처럼 사용되지 않았는가?
- QueryBus Provider 등록과 해제 생명주기가 같은가?
- Command/API가 성공, 실패, 취소 결과와 중복 호출 방지 기준을 가지는가?
- 필요한 기능 README 또는 문서가 갱신되었는가?

## 15. 작업 전 Decision Log

사람과 AI 모두 새 public API, 이벤트, Save key, Asset key, asmdef, 리소스 lifetime 변경이 있으면 다음 5줄을 먼저 정리한다.

기본 위치는 해당 기능의 `README.md` 하단이다. 기능 README가 없거나 여러 기능에 걸친 결정이면 `Docs/UnityArchitectureGuide/DECISIONS.md` 또는 PR 설명에 남긴다.

```txt
1. 대상 기능 소유자:
2. 새 파일 위치:
3. 상태 위치: SO / RuntimeModel / SaveData / Server
4. 통신 방식: 직접 호출 / C# event/callback / 상위 바인딩 / EventBus / QueryBus / Command
5. 영향 파일:
```

예시:

```txt
1. 대상 기능 소유자: BoardRoot
2. 새 파일 위치: Assets/Game/Core/Board/Scripts
3. 상태 위치: BoardRuntime
4. 통신 방식: InputView -> BoardRoot는 C# event/callback, Board -> HUD는 BoardItemMergedEvent EventBus
5. 영향 파일: BoardRoot, BoardRuntime, BoardItemMergedEvent, Board README
```

## 16. Unity 공식 문서 참고

- `Editor` 폴더는 에디터 확장 스크립트용이며 Player 빌드 런타임에서 사용할 수 없다. Editor 코드용 asmdef도 대안이다.
- `Resources` 폴더는 런타임 로딩용이지만 Player 빌드 크기를 늘리고, 필요 없는 에셋을 정리하지 않으면 성능에 악영향을 줄 수 있다.
- asmdef는 의존성 관리, 재컴파일 범위 축소, 디버깅에 도움이 된다.
- asmdef는 플랫폼별 컴파일 대상을 설정할 수 있으므로 Editor 전용 assembly를 만들 수 있다.
- Unity는 asmdef 순환 참조를 허용하지 않는다. 순환 참조가 생기면 리팩터링하거나 같은 assembly로 옮겨야 한다.
- `OnDisable`은 비활성화, Destroy, 씬 unload, domain reload 때 호출된다.
- `OnDestroy`는 Destroy, 씬 unload, 앱 종료/Play Mode 종료 때 호출되지만, 모바일 OS가 앱을 죽이는 경우 호출되지 못할 수 있다.

참고 링크:

- https://docs.unity3d.com/Manual/SpecialFolders.html
- https://docs.unity3d.com/Manual/assembly-definition-files.html
- https://docs.unity3d.com/Manual/class-AssemblyDefinitionImporter.html
- https://docs.unity3d.com/Manual/assembly-definitions-referencing.html
- https://docs.unity3d.com/ScriptReference/MonoBehaviour.OnDisable.html
- https://docs.unity3d.com/ScriptReference/MonoBehaviour.OnDestroy.html
- https://docs.unity3d.com/Manual/execution-order.html

## 17. 최종 기준

좋은 구조는 복잡한 구조가 아니라 소유권이 분명한 구조다.

- 전역 서비스는 GlobalRoot가 소유한다.
- 씬 조립은 SceneController가 소유한다.
- 플레이 흐름은 GameSession이 소유한다.
- 기능 내부는 해당 FeatureRoot가 소유한다.
- 런타임 상태는 RuntimeModel이 소유한다.
- 원본 데이터는 ScriptableObject가 소유한다.
- 먼 기능 간 사건은 EventBus가 전달한다.
- 먼 기능 간 즉시 질의는 QueryBus가 처리한다.
- 동적 생성과 해제는 생성한 기능 또는 Factory가 책임진다.

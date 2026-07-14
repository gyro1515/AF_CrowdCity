# Crowd City MVP — 작업 현황 (핸드오프)

> 이 문서는 작업 중단 시점의 스냅샷입니다. 회사 컴퓨터에서 이어서 작업하기 위한 요약입니다.
> 설계 근거는 같은 폴더의 `DESIGN.md`(합의 v4), 공개 API 계약은 `INTERFACES.md`가 최종 기준입니다.

## 1. 개요

Voodoo "Crowd City"(`io.voodoo.crowdcity`) MVP 클론을 `Assets/@Project/Scenes/GameScene.unity`에
프로젝트 원칙(CLAUDE.md/AGENTS.md)과 기존 EventBus(EventManager) 패턴으로 구현했다.

- Unity 6000.3.9f1, URP 17.3.0, Input System 1.18.0 전용, Linear color space, Android 우선.
- 결정론 시뮬레이션: 고정 스텝(0.02s, 프레임당 최대 4스텝), 스냅샷-순수 전투 해석.
- 씬 오브젝트는 프리팹으로 생성(Human.prefab). 게임 루프에 Destroy가 없어 오브젝트 풀링은 도입하지 않음(요청 조건대로).

## 2. 아키텍처 요약 (CLAUDE.md 준수)

- **소유권**: `GameSceneController`(조립) → `GameplayRoot`(피처 연결/바인딩) → `GameSession`(세션 흐름)
  + 피처 루트(`CrowdRoot`, `InputRoot`, `CameraRoot`, `HudRoot`). 자식→부모는 C# 이벤트, 형제는 부모에서 바인딩.
- **상태 위치**: 소스/밸런스는 `GameConfigSO`(불변, 소비자는 읽기전용), 플레이 상태는 `CrowdModel`/`GameSession`(RuntimeModel).
- **통신**: EventBus(static EventManager)는 "이미 일어난 사실" 2종만 사용
  — `CrowdCountChangedEvent`, `CrowdEliminatedEvent`. 구독자는 boundary(Session/Hud/Camera)로 한정, 모두 수명주기 짝으로 해제.
- **결정론 커널**(pure C#, `Assets/@Project/Crowd/Core/`): AgentBuffer, SimTuning, SpatialGrid,
  RecruitResolver, CombatResolver(스냅샷-순수), MatchRules, FollowerSteering. NUnit EditMode 테스트로 검증.

## 3. 완료된 작업

- [x] 결정론 커널 + 크라우드/게임/HUD 피처 + 에디터 셋업/검증 스크립트 전체 구현.
- [x] 프리팹 기반 생성(`GameSceneSetup`이 씬 템플릿에서 `Human.prefab` 생성, `GameSceneController.humanPrefab` 배선).
- [x] `GameConfigSO` SO 불변성: 모든 값 `[SerializeField] private` + get-only 프로퍼티,
      배열은 캐시된 `ReadOnlyCollection<T>`로 노출(다운캐스트 변조 불가).
- [x] EventManager는 static 버전을 정본으로 유지(사용자 결정: "static 유지, 현 상태 그대로"). 편집 금지 인프라.
- [x] 리더/중립/**팔로워** 위치를 walkable region으로 clamp — 맵 이탈/오프맵 recruit·combat 버그 해소.
- [x] 검증 게이트 통과: 컴파일 0 에러/0 경고, **EditMode 28 pass / 0 fail**, 씬 셋업+검증, play-smoke(OUTSIDE=0).

## 4. 교차검증 합의 결과 (완료)

시니어 Unity 프로그래머 관점의 CODEX ↔ Claude 교차검증을 합의까지 반복.

| 리뷰 | 판정 |
|---|---|
| Claude 최종 리뷰 | **APPROVE** (미해결 BLOCKER/MAJOR 0) |
| CODEX 최종 리뷰 | REVISE — MAJOR 1(팔로워 맵-clamp) + MINOR 10 |
| 수정 반영 후 CODEX 재리뷰 | **APPROVE** — 3개 수정 모두 RESOLVED |

**합의 확정: 양측 미해결 BLOCKER/MAJOR 0건.**

### 반영한 수정
1. **(MAJOR)** `CrowdRoot.SteerFollowersAndNeutrals` — 팔로워 커밋 위치를 `_regionMin/Max X/Z`로 clamp.
   transform → `MirrorPositionsToBuffer` → `_buffer.Pos` 경로라 clamp 값이 시뮬 권위 위치로 전파됨(런타임 프로브 OUTSIDE=0 확인).
2. `RecruitResolver.Resolve` — `_candidates.Capacity`를 첫 tick에 buffer capacity로 1회 확장(핫패스 재할당 제거, 결정론 무영향).
3. `CrowdRoot` — `CombatResolver.Resolve` 직전 중복 `_combatOutcome.Clear()` 제거(Resolve가 진입 시 clear).

## 5. 남은 작업 / 수용·보류한 MINOR (근거)

아래는 CODEX/Claude가 제기했으나 MVP 범위에서 **의도적으로 보류**한 항목. 필요 시 후속 태스크로 처리.

- CrowdModel 병렬 리스트 캡슐화(read-only view) — 유일 mutator가 소유자(CrowdRoot), 외부 참조 없음. 이론적 위험.
- CombatResolver 팀ID / MatchRules 배열 길이 방어 검증 — 내부 호출자만 존재, 도달 불가 시나리오(§Simplicity First).
- SpatialGrid 극단 반경 / Ground 최소 크기 가드 — 고정 config·고정 도시라 실사용 도달 불가.
- 에디터 위생(collider 재귀 정리, Validator 카메라 동일성/`prefab.activeSelf`, 불필요 prefab 재기록) — 에디터 전용 도구.
- RivalAiDriver 경계 인지 항목 — 중립 밀도 벡터로 자기보정(플레이상 문제 미관측).
- HudRoot per-event `ToString()` 할당 — 프레젠테이션 경로, 양측 MVP 허용.
- EventManager `GetInvocationList()` 에디터 경로 할당 — **off-limits 정본 인프라, 편집 금지**.

## 6. 회사 컴퓨터에서 이어서 하기

이 작업은 브랜치 `crowdcity-mvp`에 올라가 있다.

```bash
git fetch origin
git checkout crowdcity-mvp   # 또는: git pull origin crowdcity-mvp
```

검증 재현(unity-cli 커넥터):

```
unity-cli editor refresh --compile
unity-cli console --type error,warning
unity-cli test --mode EditMode          # 28 pass 기대
# play-smoke: exec로 Application.runInBackground=true 설정(도메인 리로드 후 재설정 필요) → editor play --wait → screenshot --view game → editor stop
```

씬을 처음부터 셋업하려면 에디터 메뉴의 **Game Scene Setup** 실행 후 **Game Scene Validator**로 확인.

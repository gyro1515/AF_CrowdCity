# Crowd Sim CPU 최적화 — 실행 핸드오프 (콜드스타트용)

---

## 🔄 최신 상태 업데이트 (2026-07-19) — 크라우드 성능 세션 결과 + 남은 작업

> 이 절이 현재 상태의 정본이다(2026-07-19 성능 세션 이후). 아래 2026-07-17 마커/게이트/계획은 배경·상세 참조용으로 남겨둔다.

브랜치: `feat/crowd-sdf-perf` (다른 PC에서 `git fetch && git checkout feat/crowd-sdf-perf && git pull`로 재개)

### 완료 (커밋·푸시됨)
- Burst 핫잡 활성(FloatMode.Strict): 10k SimTick 22.95→14.04ms (−38.8%)
- GPU 크라우드 렌더러 토글 → GameConfigSO, 기본 ON (Animator/SMR 제거, 미지원 기기 자동 폴백)
- 밀도필드 separation: 추가했다가 제거(사용 불가 — 튕김)
- 설정 기본값: useGpuCrowdRenderer=ON, CombatFlatConvertRate=ON, SeparationVisitBudget=0(캡 되돌림 — 조밀군집 떨림 유발), neutralCount=3000(에디터 60fps), ConvertPerSecond=100
- DevHudRoot: 개발용 시작 인원 선택 + FPS 오버레이 (게이트: Debug.isDebugBuild && !batchmode) — 빌드에서 인원별 실측
- 청크(멀티프레임) 스포닝 — 고인원 스폰 프리즈 완화
- 리더-방사형 separation A/B 토글(SeparationMode, 기본 OFF=Pairwise 바이트동일) — 동작하나 줄무늬, 폴리시 필요
- 측정/스크린샷 에디터 하네스, CLAUDE.md Codex `< /dev/null` 규칙

### 핵심 현황
- 10k CPU 시뮬 해결: 14.86ms/tick, 20ms(50Hz) 예산 아래.
- 에디터 60fps 인원 상한 ≈ 3400 = 렌더-바운드(디메이션 안 된 2964버텍스 메시 × N), 시뮬 아님. 측정 하네스는 GPU 강제동기라 보수적 → 실제 빌드는 더 높을 가능성.
- 인원 상향 핵심 레버 = 디메이션 렌더 메시(아트).
- separation 결정성 기준선: OFF/Pairwise 오라클 n=2000 s12345 md5 = 2b3aad7f786004f1185a067bc1b31f4a.

### 남은 작업 (우선순위)
1. [사용자] Development 빌드 + DevHudRoot FPS로 인원별 실 프레임 측정 → 진짜 파이프라인 60fps 인원 + 시뮬/렌더 병목 판정.
2. [아트] 디메이션 렌더 메시(~200~500 vert) — 높은 온스크린 인원의 핵심 경로, GPU 렌더러 모바일 출하 게이트.
3. [시뮬-병목 될 때까지 연기] 리더-방사형 폴리시: (a) 버킷→정확한 셀 점유수 수정(Codex: 해시충돌이 occ 부풀려 오탐 → 줄무늬), (b) T/gain/tangential 튜닝.
4. [빌드 실측 후] 네이티브 위치 권위 — 틱마다 10k 트랜스폼 왕복(Restore+Mirror) 제거 = 남은 최대 직렬 시뮬 이득(결정성 재기준선 필요).
5. Scope B: §4 밀스톤 Burst 골든 baseline(확장 스냅샷 필드, 수치 허용오차, 플레이어 AOT 빌드).
6. 수동 확인: DevHudRoot 버튼/FPS 동작, 청크 스폰 후 튐 없음, GPU 경로 스폰중 pop-in 허용 여부(점진표시로 수정 가능).
7. 하네스 정리(Shutdown 후 DestroyImmediate — 경미).

### 워크플로우
- 반복: 최적화 → neutralCount 상향 → 반복. 목표 = 에디터 안정 60fps. ConvertPerSecond=100은 의도된 튜닝.
- 교차검증: Codex(gpt-5.6-sol ultra) `< /dev/null` 동기 ↔ Claude, 합의까지.

---

## 🔄 다른 PC 재개 마커 (2026-07-17 업데이트)
- **기준 커밋(baseline HEAD) = 이 브랜치(`feat/crowd-sdf-perf`)의 최신 push된 커밋** — `origin/feat/crowd-sdf-perf` 에 push 완료(이전 마커의 `fb14a41`에서 진행됨). 다른 PC에서는 `git pull` (branch `feat/crowd-sdf-perf`)로 전부 수신됨. 로컬 미커밋/stash 없음 → 유실 없음.
- **레이(`Physics.Raycast`) = 벽 판정 용도지만 layermask 버그가 있었고, 이번 세션에 수정 완료.** 두 곳뿐: `Assets/@Project/Crowd/Scripts/RivalAiDriver.cs`(`ApplyWallAvoidance`/`ProbeClearance`, 벽 회피)와 `Assets/@Project/Crowd/Scripts/CrowdRoot.cs:1099`(`RepickWanderHeading`, wander 방향 벽 판정). 용도는 둘 다 벽 판정이지만 **layermask 없이(`Physics.DefaultRaycastLayers`) 쏘고 있어 Unit(crowd) 콜라이더를 벽으로 오판하던 실제 버그였음** — 이전 마커는 레이의 *용도*만 확인하고 layermask를 보지 않아 "이미 정리됨"으로 잘못 판단했다. mask에서 Unit 레이어 제외로 이번 세션에 **FIXED**.
- **크라우드-크라우드(에이전트 간) 판정은 레이가 아니라 `SpatialGrid.QueryCircle` 경로** (separation=`CrowdRoot.cs:929`, recruit=`RecruitResolver.cs:74`, combat=`CombatResolver.cs:271/461`). → 다만 위 벽 레이 2곳은 layermask 누락으로 **crowd(Unit)를 실제로 맞고 있었음**(오판) — 이번 세션에 mask에서 Unit 제외로 **수정 완료**. 단 이 오판은 correctness 문제일 뿐 프레임 시간(Update self)의 주원인은 아래 진단대로 QueryCircle·SDF다.
- **다음 작업(사용자 의도) = Burst 컴파일러 + Job 시스템 = 계획서 §3 `M-sim-2`(=M2).** 3분할: M2-a(Native SoA) → M2-b(Burst canonical) → M2-c(IJobParallelFor).
- **단, 계획/게이트상 M2 선행 조건:** `M-sim-0` 실측(아직 미실행, 베이스라인 산출물 미커밋) → `M-sim-1`(grid 쿼리 밀도 캡핑). 사용자 의도(바로 Burst)와 계획 순서(측정 먼저)가 갈리는 지점 — 재개 시 확정 필요.
- ⚠ **메모리(로컬 `~/.claude`)는 PC 간 동기화 안 됨.** 이 문서(git 추적)가 PC 간 유일한 인수인계 소스.

### 📌 진단 갱신 (2026-07-17, 프로파일러 근거 `Docs/Photo/PRO.PNG`, `PRO2.PNG`)
- **프레임 핫스팟 = `GameplayRoot.Update()` self 14.86ms(32.4%)**, 66ms(15FPS)까지 스파이크. 실제 `PhysX.Simulate`는 1.53ms뿐 → 물리 솔버가 아니라 **크라우드 틱 내부 연산**이 원인.
- **원인:** `MaxStepsPerFrame=4`(GameplayRoot.cs:13)로 SimTick이 프레임당 ~3회 실행 × 매 틱 [4개 `SpatialGrid.QueryCircle` 이웃질의(separation=`CrowdRoot.cs:929` / recruit=`RecruitResolver.cs:74` / combat=`CombatResolver.cs:271,461`) + 에이전트별 SDF `WallSolver.Resolve`(`CrowdRoot.cs:1125`)]. 커스텀 `CrowdSimProfiler`가 Unity ProfilerMarker를 안 써서 하위 단계가 전부 `GameplayRoot.Update` self로 뭉쳐 보임. → **M-sim-1(쿼리 밀도 캡핑)·M-sim-2(Burst)가 노리는 지점.** 기존 M-sim-0 베이스라인("dominant = SDF move-solve")과 일치.
- **별개 correctness 버그 — 이번 세션 수정 완료:** 벽 감지 레이 2곳(`CrowdRoot.RepickWanderHeading` @~1099, `RivalAiDriver.ProbeClearance` @~199)이 layermask 없이 `Physics.DefaultRaycastLayers`로 쏴 Unit(crowd) 콜라이더를 벽으로 오판. `Physics.IgnoreLayerCollision(Unit,Unit)`은 raycast에 무효라 유닛 물리충돌을 꺼도 레이는 crowd를 맞음. → mask에서 Unit 레이어 제외(`Physics.DefaultRaycastLayers & ~(1<<unitLayer)`, 신규 직렬화 필드 없이 코드로 계산)로 수정. **오판 제거일 뿐 14.86ms와는 무관** (프레임 시간은 위 쿼리·SDF가 원인).

> **이 문서의 용도**: 별도 세션(대화 컨텍스트 없음)이 이 문서 하나로 crowd sim CPU 최적화 작업을 **바로 시작**할 수 있게 하는 진입점이다. 상세 설계는 [`SIM_OPT_PLAN.md`](SIM_OPT_PLAN.md)에 있다. 이 문서는 "어떻게 부팅하고 무엇부터 하는가"만 담는다.
> **선행 조건**: 사용자의 별도 구조 리팩토링이 **완료된 뒤** 시작한다. 리팩토링은 `Crowd/Core` + `CrowdRoot`를 전부 건드리므로, 이 계획은 라인이 아니라 **책임 단위로 rebase**한다.

---

## ⚠️ 시작 전 게이트 (P0 — 통과 못 하면 착수 금지)
1. **리팩토링 완료 확인**: 이 계획은 사용자의 `Crowd/Core` + `CrowdRoot` 리팩토링 **완료 후** 시작한다. 완료 여부는 **문서로 판별 불가 → 사용자에게 명시 확인**받거나 사용자가 지정한 "완료 커밋/브랜치"로 판정한다. 착수 시 baseline 고정 기록: `git branch --show-current`, `git rev-parse HEAD`. (이 계획 작성 시점엔 리팩토링이 진행 중이었다.)
2. **정본 우선순위**: **코드 > 이 HANDOFF/PLAN > DESIGN/INTERFACES/STATUS.** `DESIGN.md`/`INTERFACES.md`/`STATUS.md`는 **Phase C 이전 스냅샷**이라 SDF/`UseSdfSolver`/`WallField`/`OracleAgentCount`가 누락돼 현재 상태를 오도할 수 있다 — 현 상태 근거로 쓰지 말 것. 계약 확인은 **현재 코드가 유일 진실**.
3. **산출물은 tracked 커밋**: `codex_burst_*.txt`가 untracked로 방치된 전례가 있다. M-sim-0 CSV·오라클 baseline·측정 manifest는 반드시 기준 브랜치에 커밋(clean/clone 시 소멸 방지).
4. **의도적 계약 변경 목록 유지**: 밀도 캡(거동), RNG 스트림(시드 재현), baked CC 제거, SDF probe 등은 의도된 변경 → 별도 목록으로 추적하고 DESIGN/INTERFACES를 그에 맞춰 갱신.

---

## 0. 한 문단 컨텍스트 (세션 독립)
AF_CrowdCity의 crowd 시뮬레이션은 확정된 CPU 병목이 `GameplayRoot.Update → CrowdRoot.SimTick`이다(사용자 Profiler 실측: Update ~30ms @2000, Ryzen 5600X 에디터). 라이브 프리팹은 `_useSdfSolver:1`(SDF ON, `CC.Move`는 폴백 전용)이고 `gpuSkinning` ON이라 **데스크톱 병목은 렌더가 아니라 sim(script)**이다. 유력 주범(코드 근거 가설, **미실측**): combat/recruit의 `SpatialGrid` 이웃 쿼리가 밀도에 초선형 + victim 정렬 O(v²). 목표 = SimTick CPU 비용 대폭↓, 수만까지 확장 가능한 sim 구조, 거동·결정성 보존. **렌더/애니/GameObject 스택은 이 작업 대상 아님**(모바일 만단위 렌더 재설계는 별도 트랙).

## 1. 먼저 읽을 것 (순서대로)
1. [`SIM_OPT_PLAN.md`](SIM_OPT_PLAN.md) — **설계 of record**. Decision Log, end-state SoA 구조, 마일스톤 M-sim-0~3, 결정성 계획, 밀도 캡핑 알고리즘, 검증 방법론, 리스크/rebase 노트. 이 핸드오프와 충돌하면 PLAN이 우선.
2. `CLAUDE.md`(repo root) — 필수 안전 규칙 + **메인/서브에이전트 운영 모델**(아래 §3).
3. `Docs/CrowdCity/DESIGN.md`, `INTERFACES.md`, `STATUS.md` — crowd 계약 **참고용**. ⚠️ 이들은 **Phase C 이전**이라 뒤처져 있다(STATUS.md는 브랜치명·"MVP 완료"까지 stale). 현재 상태 근거로 삼지 말 것 — 위 시작 게이트 §2대로 **코드가 정본**.
4. 메모리(있으면): `crowd-scaleup-architecture.md`(이 작업의 상위 결정), `phasec-ccmove-bottleneck.md`(SDF/WallSolver 배경), `agents-verify-as-unity-senior.md`, `codex-cross-verify-command.md`.

## 2. 대상 코드 (리팩토링으로 이동/개명됐을 수 있음 — 현재 트리에서 재확인)
- 커널(순수 C#): `Assets/@Project/Crowd/Core/` — `AgentBuffer`, `SpatialGrid`, `CombatResolver`, `RecruitResolver`, `WallField`, `WallSolver`, `SimTuning`.
- 오케스트레이션: `Assets/@Project/Crowd/Scripts/` — `CrowdRoot`(SimTick), `CrowdModel`, `RivalAiDriver`, `CrowdSimProfiler`.
- 하네스(Editor): `Assets/@Project/Game/Editor/` — `CrowdProfileHarness`(성능), `CrowdOracleHarness`(결정성 오라클).
- 루프: `Assets/@Project/Game/Scripts/GameplayRoot.cs`(FixedStep 0.02s=50Hz, 프레임당 최대 4스텝).
- 브랜치: `feat/crowd-sdf-perf`. 감사 추적: 워킹트리의 `codex_burst_*.txt`(설계 라운드 산출물).

## 3. 운영 모델 (CLAUDE.md 준수 — 반드시 지킬 것)
- **메인 에이전트 = 매니저만**. 조사/파일읽기/분석/구현/편집/테스트/diff 리뷰는 **전부 서브에이전트에 위임**. 메인은 목표·범위·성공기준 정의, 위임, 판정, 최종보고만.
- **한 파일 = 한 오너**. 두 서브에이전트가 같은 파일 동시 편집 금지. 비자명 작업은 구현자와 검증자를 다른 서브에이전트로.
- **교차검증**: 설계·검증은 **Claude 서브에이전트 + Codex**로 독립 수행 후 **합의점 도달까지** 반복(사용자 표준 방식).
  - Claude 서브에이전트: opus 4.8, xhigh, "유니티 시니어 게임 프로그래머" 관점.
  - Codex(읽기전용) 호출 템플릿:
    ```
    codex exec -m gpt-5.6-sol -c model_reasoning_effort=ultra -c service_tier=fast --skip-git-repo-check --sandbox read-only "<PROMPT>" < /dev/null
    ```
    파일 쓰기 필요 시 `--sandbox workspace-write`. stdin은 `< /dev/null`로 닫아 hang 방지. 긴 프롬프트는 파일에 쓰고 `"$(cat promptfile)"`.
- 서브에이전트 보고 형식: 정확히 `Conclusion / Changed files / Verification commands and results / Risks or blockers` 네 제목만. 프로세스 로그·소스 덤프 금지.
- **새 세션의 최초 행동 = 문서/코드 조사를 서브에이전트에 위임**(메인이 직접 읽으면 매니저 전용 규칙 위반). 메인은 확인된 결론만 받아 Step 0 구성.
- **교차검증 reviewer는 항상 read-only**, 동일 HEAD SHA·frozen 작업트리·동일 요구사항/Decision Log/검증결과를 입력받는다. (Codex는 파일쓰기 필요 시에만 `workspace-write`; 설계·검증 리뷰는 read-only.)
- **합의 종료 조건 = 미해결 BLOCKER/MAJOR 0건.** reviewer 의견이 끝내 충돌하면 임의 봉합 말고 **사용자가 최종 결정**. 참조: 메모리 `autonomous-decisions-via-cross-verification`.
- **셸 주의**: 주 셸 = PowerShell(win32). codex 템플릿의 `< /dev/null` 등 POSIX 문법은 **Bash 툴**로 실행. 필수 CLI/인증/지정 모델(codex `gpt-5.6-sol`, Claude `opus`)이 없으면 임의 대체 말고 **중단→사용자 확인**.
- **성능 목표 수치를 지금 지어내지 말 것** — M-sim-0 실측 + 디바이스 budget 후 확정. 현재 문서의 모든 수치는 외삽.

---

## 4. 리팩토링 세션에게 (rebase가 매끄럽도록 남겨줄 것)
리팩토링 중 아래를 **깨지 않거나, 바뀌면 명확히 기록**해 두면 sim-opt rebase가 쉬워진다:
1. **SimTick 단계 순서 계약** 유지: restore → heading → leader move → follower/neutral steer → mirror → grid rebuild → recruit → combat → commit → publish. (병렬화의 "이웃은 직전 tick 스냅샷" 전제가 mirror/rebuild가 steer 뒤라는 순서에 의존.)
2. **AgentId 고유성 + population slot 안정성**(삽입순 오름차순 index 불변) 유지 — 결정성 canonical order의 근간.
3. **`SpatialGrid.QueryCircle` 경계**(누가 호출하는지: combat per non-neutral+per leader, recruit per neutral, separation per follower)를 명확히 — 밀도 캡핑이 이 지점을 대체한다. 시그니처가 바뀌면 기록.
4. **`CrowdSimProfiler`의 Seg 열거** 유지(하네스가 의존). 이름/의미 바뀌면 기록.
5. **오라클 스냅샷 계약**(CrowdOracleHarness가 읽는 위치/team/event) 유지 — 재기준선 비교의 기준.
6. `AgentBuffer`가 여전히 SoA인지 / 이미 NativeArray로 갔는지 결과 상태를 STATUS.md에 한 줄 기록(M-sim-2 스코프가 달라짐).
7. 모든 `System.Random`, `Physics.Raycast`, `Physics.CheckSphere`, `CharacterController` **최종 위치**를 기록 — M-sim-3 대상.

## 5. 실행 세션에게 (리팩토링 후 — 바로 이 순서로)

### Step 0 — rebase 재매핑 + coverage (구현 전 필수)
> 여기서 "rebase"는 설계를 **현재 책임 구조에 의미적으로 재매핑**한다는 뜻이며, `git rebase`/checkout/브랜치 변경 권한이 아니다.

서브에이전트에 위임하여 현재 트리에서 산출:
- **책임 매핑표**: (tick driver / sim owner·권위 상태 / buffer·grid·query / profiler·harness / config source / SDF·CC / prefab·setup·validator / asmdef·package) 각각 `기존 책임 → 현재 owner·file·type·API·lifecycle`. 기존 이름(`CrowdRoot`,`_useSdfSolver`,`OracleAgentCount`,`RunFromBatch`,`QueryCircle`,`SpatialGrid`,`SimTuning`)이 유지/개명/이동됐는지 확정. **리팩토링이 프리팹 직렬화+Init 주입으로 갔다면 M-sim-0의 `AddComponent<CrowdRoot>` 하네스 방식 자체가 무효일 수 있으니 반드시 확인.**
- **milestone coverage**: M-sim-0~3 각각을 `미구현/부분/완료/설계충돌`로 판정 — 리팩토링이 일부를 이미 흡수했으면 **실제 delta만** 구현.
- §4 핸드셰이크 항목이 STATUS.md에 기록돼 있지 않으면(현재 미기록 상태) 코드에서 전수 도출. PLAN §7 rebase 체크리스트와 대조.
- PLAN 설계가 현재 코드와 어긋나면 그 부분만 교차검증(Claude+Codex)으로 재합의 후 진행.

### Step 1 — M-sim-0: 실측 (첫 관문, 코드변경 최소)
**이게 나머지 전부의 판단 기준점이다. 반드시 먼저.**
- 위임 내용: `CrowdProfileHarness`의 scale 하드코딩을 CLI 인자화 + **2000/5000**(가능하면 10K) 추가. `agents`가 neutral 제외하는 버그 수정(실제 `OracleAgentCount` 기록). work counter 추가(grid entries, separation/recruit/combat visits, exact-radius qualifying, touching/unique pair, victim/comparison). **live prefab 경로 또는 명시적 `_useSdfSolver=true` + SDF load 성공 + `CC.Move` 호출 0을 assert**(현 하네스는 `AddComponent<CrowdRoot>`라 SDF ON을 우회 → 이 수정 없이 낸 프로파일은 기준선 불인정).
- 실행: `-batchmode -nographics -executeMethod CrowdProfileHarness.RunFromBatch ...`(에디터 GUI 불필요). 동일 seed/tick 독립 ≥3회로 **A/A 노이즈 밴드** 확립.
- **구동 수단**: win32 헤드리스 배치 = `Unity.exe -batchmode -nographics -projectPath <proj> -quit -executeMethod CrowdProfileHarness.RunFromBatch -profileOut <out> -profileLabel <label>`. Unity 에디터 실행 파일 경로(또는 unity-cli 커넥터)와 정확한 Unity 버전은 **사용자에게 확인**.
- **M0 위생**: 계측·하네스 외 게임 규칙/쿼리 알고리즘/tuning 값/production scene·prefab/오라클 baseline을 **바꾸지 말 것**(순수 측정).
- **counter run ≠ timing run**: work counter가 timing을 교란하므로 성능 게이트에는 **counter-off** 결과를 쓰고, counter는 별도 run에서 집계.
- 산출 baseline CSV/오라클/manifest는 **tracked 커밋**(시작 게이트 §3).
- **게이트/산출**: median/p95 + work counter가 **주범 sub-stage(grid/recruit/combat/steering)를 일관 지목**. 가설("combat/recruit 밀도 초선형")을 실측으로 확정/반증. 순위가 실행마다 뒤집히면 marker 세분화 후 재측정(M-sim-1 진행 금지). **여기서 나온 실측으로 이후 성능 게이트 목표치를 확정**.

### Step 2 — M-sim-1: 밀도 캡핑 (초선형 항 근본 제거)
- PLAN §5 알고리즘대로: grid는 **전 agent 저장(cap 없음) + query 방문량만 lane별 budget cap**. victim O(v²) 정렬 제거. 캡 선택은 `(distSq, AgentId)` 총순서 top-k. 캡 파라미터는 `SimTuning` SO(불변 소비), 값은 M-sim-0 histogram 후 확정.
- **밀도 캡 = 게임플레이 변경이므로 성능 리팩토링과 분리 커밋** + 거동 A/B("no perceptible difference": recruit 지연/claimant/strength/conversion·elimination/separation). cap 미발동 fixture는 legacy byte-identical, cap 발동 fixture만 A/B 승인 후 오라클 갱신.
- 성공조건 = **초선형 work term 제거**(상수배 아님). Burst 미적용.

### Step 3 — M-sim-2: NativeArray SoA + Burst + IJobParallelFor (3분할 필수)
- **M2-a** native storage(serial): managed 배열 → `CrowdSimState`/`NativeAgentSoA`(Persistent), Native 위치 권위, Transform mirror 제거. 게이트: M-sim-1 오라클과 byte 비교.
- **M2-b** Burst canonical(serial): float2/math 통일, `WallFieldView` + Burst SDF, `System.Random` → per-agent/team `uint4` 해시 스트림(`DeterministicRng`), canonical reduction. 게이트: RNG/math 변경으로 byte 자동보장 안 됨 → 거동 A/B 승인 후 `M2-canonical` baseline.
- **M2-c** parallel: per-agent 단계만 IJobParallelFor(자기 슬롯·자기 RNG만). 게이트: **모든 worker/batch permutation이 `M2-canonical`과 byte-identical**, 전체 SimTick(schedule+complete) 측정, leak 0, managed alloc 0.
- 결정성 필수: `[BurstCompile(FloatMode=Strict, FloatPrecision=Standard)]`, FastMath 금지. 새 asmdef 만들지 말 것.

### Step 4 — M-sim-3: 잔존 Physics 정리
- neutral wander Raycast + `RivalAiDriver` 클리어런스 Raycast → SDF segment-clearance query. baked `CharacterController` 제거(SDF가 이동 대체). spawn `CheckSphere`는 init-only라 유지. 게이트: probe/이동 A/B + CC 0 + runtime Raycast 0, 거동 승인 후 M3 baseline.

## 6. 성공 기준 (요약)
- **성능**: 단계별 개선 paired 95% CI가 A/A 노이즈 밴드 밖(개선 방향), 무관 sub-stage는 등가. M1=work bound hard gate, M2=전체 SimTick hard gate. 절대 ms/% 는 M-sim-0 후 확정.
- **결정성**: same-seed 2× byte-identical + worker/batch permutation byte-identical + 오라클 다단 재기준선(rebase/M1/M2-b/M3 경계). cross-ISA 불필요(싱글플레이).
- **거동**: critical fixture 100% 보존(recruit tie-break, strict-larger 승리, escort immunity, radius밖 false contact 0 등). 밀도캡/SDF의 거동 변화는 "no perceptible difference" 게이트 승인.
- **무할당/수명**: SimTick managed alloc 0(`GC.GetAllocatedBytesForCurrentThread` 1000tick delta 0), Native leak 0, Initialize→ticks→Shutdown 반복 안전.

## 7. 금기 / 주의
- 성능 목표 수치 지어내기 금지(M-sim-0 후 확정).
- **가설 반증 시 정지**: M-sim-0가 combat/recruit를 일관된 주범으로 확정하지 못하면 M-sim-1 시작 금지 → PLAN 재검토·재승인.
- **용어 구분**: M-sim-0의 "CC.Move 0" = **호출 횟수 0**, M-sim-3의 "CC 0" = **prefab/scene/runtime 컴포넌트 수 0**.
- **"렌더 스택 불변"의 정확한 범위**: 메시/Animator/스키닝/머티리얼 등 **시각 표현은 불변**. 단 M2의 Transform mirror 제거·M3의 baked CC 제거는 예외(위치 권위가 Native로 이동, CC는 이동에 미사용) — 시각 결과는 동일해야 함.
- M-sim-2를 한 커밋에 몰아서 하지 말 것(3분할 — divergence 원인 분리 불가).
- 밀도 캡을 순수 성능 리팩토링과 같은 커밋에 섞지 말 것(게임플레이 변경).
- 렌더/애니/GameObject/Human 프리팹 재설계는 이 작업 범위 밖(모바일 트랙).
- Burst 산출은 Mono 오라클과 bit-identical 아님 → 재기준선 전제(기존 baseline 직접 비교 금지).
- 새 asmdef·새 Bus·새 전역 registry 만들지 말 것(CLAUDE.md §11, §13).

## 8. 파일 인덱스
- 이 핸드오프: `Docs/CrowdCity/SIM_OPT_HANDOFF.md`
- 상세 설계: `Docs/CrowdCity/SIM_OPT_PLAN.md`
- 규칙: `CLAUDE.md`(root)
- crowd 계약: `Docs/CrowdCity/{DESIGN,INTERFACES,STATUS}.md`
- 설계 라운드 산출물(감사): 워킹트리 `codex_burst_prompt{,2..7}.txt` / `codex_burst_out{,2..7}.txt` — ⚠️ **현재 untracked라 clean/clone 시 소멸**. 감사에 필요하면 tracked 경로로 커밋.

# Crowd Sim CPU 최적화 설계 (합의안)

> 상태: **설계 확정 · 구현 대기**. Claude(opus 4.8) + Codex(gpt-5.6-sol) 유니티 시니어 관점 교차검증 1라운드 합의(2026-07-17).
> 전제: 현재 구조 리팩토링이 `Crowd/Core` + `CrowdRoot`를 전부 건드리므로, **리팩토링 완료 후 책임 단위로 rebase 적용**한다. 라인 번호는 근거 인용용이며 못박지 않는다.
> 대상: **데스크톱 sim-bound**(확정 병목 = `GameplayRoot.Update → CrowdRoot.SimTick`, 사용자 Profiler 실측). 렌더/애니/GameObject 스택은 **불변**(모바일 만단위 렌더 재설계는 별도 트랙).

## 확정 사실
- 라이브 프리팹 `_useSdfSolver:1`(SDF ON, `CC.Move`는 폴백 전용). Human에 per-frame Update 없음. `gpuSkinning` ON.
- 유력 주범(코드 근거 가설, **미실측**): combat/recruit의 `SpatialGrid` 이웃 쿼리가 밀도에 **초선형**(`QueryCircle` per non-neutral+per leader / per neutral), victim 정렬 **O(v²)**. 800→2000 고정맵 = 개체 2.5× + 밀도 2.5× ≈ 6×.
- Crowd/Core 순수 C#·이미 SoA(managed 배열). WallSolver/SDF는 NativeArray·float2/math. Core asmdef 이미 Burst/Collections/Mathematics 참조, `allowUnsafeCode=false`. 결정성 = 로컬 same-build만(싱글플레이, cross-ISA 불필요).

## 1. Decision Log (CLAUDE.md §1)
1. **소유자**: Crowd feature — `CrowdRoot`(오케스트레이션·Native 컨테이너 소유·job schedule/Complete·Dispose·commit·presentation 동기화), `Crowd/Core`(순수 커널).
2. **파일 위치**: `Crowd/Core`에 `CrowdSimState.cs`/`NativeAgentSoA.cs`/`CrowdSimJobs.cs`/`DeterministicRng.cs` 신설(기존 flat 관례 유지, **새 asmdef 만들지 않음**). SimTick 스케줄/commit은 `CrowdRoot`.
3. **상태 위치**: `GameConfigSO`/`SimTuning` = 불변 source(캡 파라미터 포함). `CrowdSimState`의 `Allocator.Persistent` NativeArray = 세션 RuntimeModel(권위 상태). `CrowdModel`/`Human` = presentation cache. SaveData/Server 없음.
4. **통신**: `GameplayRoot → CrowdRoot.SimTick` 직접 호출 유지. Job은 Crowd 내부 구현. 상태 변경은 CrowdRoot canonical commit으로만. commit 후 기존 `CrowdCountChanged`/`Eliminated` fact 발행만 유지(신규 Bus/API 없음).
5. **영향 파일**: AgentBuffer·SpatialGrid·CombatResolver·RecruitResolver·WallField/WallSolver·SimTuning, CrowdRoot·CrowdModel·RivalAiDriver, CrowdSimProfiler·오라클, CrowdProfileHarness/CrowdOracleHarness·tests, GameConfigSO/asset, scene setup/validator, Human/CrowdRoot prefab(CC 제거만), DESIGN/INTERFACES/STATUS 문서.

## 2. Target end-state
**소유·수명**: `CrowdRoot` 내부에 `CrowdSimState : IDisposable`. `Initialize`에서 고정 population/capacity로 `Allocator.Persistent` 1회 할당. tick 중 resize/`Temp`/`TempJob`/`NativeList` 증설 금지. `_simFence`(마지막 JobHandle) 추적, `Shutdown`은 `Complete()` 후 idempotent Dispose(초기화 실패/정상/OnDestroy 동일 경로). **Native 위치가 sim 권위**, Transform은 presentation.

**NativeArray SoA**: AgentId/Team/IsLeader(byte)/Scale, 위치 **더블버퍼**(`PositionCurrent`/`PositionNext` swap → 이전/현재 동시 보존), Velocity, WanderHeading/Timer, per-agent RNG(`uint4`). team 크기: LeaderIndex/TeamHeading/LeaderYaw/MemberCount/Published/Eliminated/team RNG. team-pair 크기: combat accumulator/포화 contact. canonical: `AgentIndexByIdOrder`. recruit/combat/victim 결과 = **고정폭 배열(atomic append 금지)**. telemetry 고정 슬롯. (제거 개념 없으면 speculative `Alive` 배열 금지.)

**Grid**: 모든 agent 보존(저장 cap 없음) + lane 분리(`0=neutral`, `1+teamId=team`), cell 내부 stable AgentId 순. 구성 = ①IJobParallelFor cell/lane key ②단일 Burst IJob count→prefix→scatter(canonical) ③전향 후 O(N) re-lane. `NativeParallelMultiHashMap` **지양**(열거 순서 비결정).

**tick 파이프라인**: pending 소비/team AI → leader next-pos → (∥)follower steering·neutral wander·SDF move → position swap → (∥)cell key → **grid finalize(직렬 Burst)** → (∥)recruit query·combat contact discovery → **recruit/combat/leader resolve(직렬 Burst canonical)** → team commit·re-lane → CrowdModel/Human sync·fact publish.
- **∥ IJobParallelFor**(per-agent 고정 출력 슬롯, 이웃은 직전 tick 스냅샷 읽기): steering/wander/SDF move, cell key, recruit 후보, combat contact, telemetry.
- **직렬(canonical)**: pending, team/rival AI+leader 이동(≤4팀), grid prefix/scatter, team-pair reduction, victim 선택, leader 제거, commit, presentation sync, event. (직렬도 단일 Burst IJob 가능 — 중요한 건 실행 순서가 하나로 고정.)
- `WallFieldView`(blittable read-only: `[ReadOnly] NativeArray<float> Samples` + 메타 + Burst-callable Phi/Gradient/Resolve)를 M-sim-2에서 도입(live SDF job 호환).

## 3. 마일스톤 (각 변경 스코프 + 검증 게이트)
### M-sim-0 — 실측(선행, 코드변경 최소)
- 하네스 Scales 하드코딩(100/300/500/800)을 CLI 인자화 + **2000/5000**(가능하면 10K) 추가. `agents`가 neutral 제외하는 버그 수정 → 실제 `OracleAgentCount` 기록. work counter 추가(grid entries, separation/recruit/combat visits, exact-radius qualifying, touching/unique pair, victim/comparison). **반드시 live prefab 또는 명시적 `_useSdfSolver=true` + SDF load 성공 + CC.Move 호출 0** (현 하네스는 `AddComponent<CrowdRoot>`라 SDF ON 우회 → 이 수정 전 프로파일은 기준선 불인정).
- **게이트**: 동일 머신/설정/seed로 독립 프로세스 ≥3회, **A/A 노이즈 밴드** 확립. Total·grid·recruit·combat·steering median/p95 + work counter가 같은 sub-stage를 일관 지목. 순위가 뒤집히면 marker 세분화 후 재측정(M-sim-1 진행 금지). Development Player의 `GameplayRoot.Update→SimTick` 주범과 일치 확인. **성능 목표치는 이 실측 + 디바이스 budget 후 확정**(현재 전부 외삽).

### M-sim-1 — grid 쿼리 밀도 캡핑(초선형 항 근본 제거)
- `SpatialGrid`를 "모든 agent 보존 + query **방문량**만 cap"(BoundedLaneGrid)으로. recruit/combat = team별 독립 visit budget `B`, separation = own-team budget. **leader(≤4) exact query와 rival(≤3) 0.4s AI query는 cap 안 함**(전체 O(N)이라 의미불변).
- victim insertion sort 제거: instant 경로 = deterministic radix(또는 순서만 결정→commit 순서무관이면 제거), rate-limit = bounded max-heap prefix. 캡은 `(distSq, AgentId)` 총순서 top-k(무엇을 버리나가 순서 의존이면 안 됨).
- 캡 파라미터 = 기존 `GameConfigSO`의 직렬화 tuning 계약에 추가(불변 소비). ⚠️ `SimTuning`은 **별도 독립 ScriptableObject가 아니라 GameConfigSO 내부의 직렬화 값/스냅샷**이다(INTERFACES 계약) — 새 SO 신설 금지. 현재 형태는 Step 0에서 확인. 값은 M-sim-0 histogram 전 확정 금지.
- **게이트**: visit ≤ `QueryCount×LaneCount×B` counter 증명, dense에서 work/N이 밀도와 무증가, cap 미발동 fixture는 legacy와 동일, **cap 발동 fixture는 byte-identical 불요 → 거동 A/B("no perceptible difference": recruit 지연/claimant/strength deficit/conversion·elimination/separation 편차) 승인 후에만 오라클 갱신**. 대상 resolver 시간 개선이 A/A 밴드 밖 + 전체 SimTick 무악화. **성공조건 = 초선형 work term 제거(상수배 아님)**. Burst 미적용.

### M-sim-2 — NativeArray SoA + Burst + IJobParallelFor (상수배, 3분할)
- **M2-a** Native storage(serial): managed AgentBuffer/흩어진 배열 → `NativeAgentSoA`/`CrowdSimState`, Transform mirror 제거·Native position authority. **게이트**: M-sim-1 오라클과 byte 비교(divergence 시 storage 외 연산순서 변경 조사).
- **M2-b** Burst canonical(serial): Vector2/Mathf→float2/math, `WallFieldView`+Burst SDF, `System.Random`→per-agent/team stream, canonical reduction 확정. **게이트**: RNG/math 변경으로 M1 byte 자동보장 안 됨 → 거동 A/B 승인 후 `M2-canonical` baseline 생성.
- **M2-c** parallel: 위 ∥ 단계를 IJobParallelFor로. job은 자기 슬롯·자기 RNG만. shared append/float atomic/worker-merge 순서의존 금지. **게이트**: 모든 worker/batch permutation이 `M2-canonical`과 byte-identical, schedule+complete 포함 전체 SimTick 측정, 작은 N job overhead와 2000/5000 개선 동시 보고, Persistent lifecycle/leak0/managed alloc0.

### M-sim-3 — 잔존 Physics 정리
- neutral wander Raycast + `RivalAiDriver.ProbeClearance`(정면/좌/우 Raycast) → 고정-step SDF segment-clearance query(샘플 간격 ≤0.5×cell, 방향순서·refinement 고정). SDF 누락 시 조용한 CC fallback 금지(초기화 실패 처리). baked `CharacterController` + controller 배열/취득·CC.Move fallback·Unit-layer self-collision·CCMove profile 구간·"CC 필수" validator 계약 제거. Human/scene setup "CC 없음"으로. **spawn `CheckSphere`는 init-only라 유지**(완전 physics-zero 요구 시 후속 SDF 대체).
- **게이트**: SDF bake source가 기존 Raycast/CC 대상 정적 collider 포함 validator, probe A/B(accept·heading·first-obstacle), 이동 A/B(관통깊이·tunneling·clearance·slide·stuck·경로편차), CC 0 확인, runtime Raycast 0. 거동 승인 후 M3 baseline 재기준화. 최종 Player에서 `GameplayRoot.Update`/SimTick/catch-up/frame p95·p99 재측정.

## 4. 결정성 계획
- `[BurstCompile(FloatMode=FloatMode.Strict, FloatPrecision=FloatPrecision.Standard)]`, **FastMath 금지**. cross-ISA 계약 없어 `Deterministic` 강제 안 함. float sum은 stable AgentId/team-pair 순 직렬 reduction(atomic float/worker순서 reduction 금지).
- **RNG**: 공유 `System.Random` job 전달 금지. `DeterministicRng`(uint4 xoshiro계열 + 고정 bit→float, 상위 24bit×2⁻²⁴). 초기화 = mix(`globalSeed`, `stableAgentId`|`teamId`, `streamTag`), 0상태→고정 non-zero. 스트림: spawn=직렬 SpawnRng, wander=per-agent, rival/team tie-break=per-team. 캡 rotating cursor는 RNG 소비 없이 `(queryAgentId,cell,lane,tick)` pure hash. (현 checkout에 CrowdRoot RNG + RivalAiDriver RNG 둘 다 존재 → rebase 후 전수 재확인, 양쪽 스트림 계약으로 흡수.)
- **canonical order**: agent=AgentId, grid=cellKey→lane→AgentId, query tie=distSq→AgentId, recruit=neutral AgentId 순, combat edge=`(min,maxAgentId)` canonicalize→sort→unique, team-pair=`(winner,loserTeamId)`, victim=`(assignedDistSq, victimAgentId)`, leader=leader/team ID 순. commit/sync/event 동일 순서.
- **오라클 재기준선**: rebase 직후 serial baseline(same-seed 2× byte) → M0 유지 → M1(cap 미발동 유지, 발동 A/B 승인 1회 갱신) → M2-a(M1 비교) → M2-b(승인 후 `M2-canonical`) → M2-c(전 permutation = M2-canonical) → M3(승인 후 별도). snapshot = position raw bits/team/leader/velocity/heading/timer/agent·team RNG state/combat accumulator/elimination/event transcript. manifest = commit·platform·package versions·SDF hash·cap값·seed/count/ticks. **테스트와 baseline 자동 동시 덮어쓰기 금지**.
- **worker/batch permutation 테스트**: worker ∈ {0,1,default,max}, batch ∈ {1,16,64,127,>N}, 반복·순서변경. 각 tick Complete 후 canonical snapshot byte 비교. `JobWorkerCount`는 try/finally 원복.

## 5. 밀도 캡핑 알고리즘 (full storage + bounded lane query)
셀당 저장 상한 없음(자르면 exact leader/AI·오라클 깨지고 영구 누락). 각 bounded query: ①broad radius cell offset stencil(초기화 시) ②cell AABB 최소거리 + cell key로 canonical cell 순서 ③필요 team/neutral lane만 ④lane별 독립 visit budget `B` ⑤active cell range round-robin(한 dense cell 예산 독점 방지) ⑥cell range 시작점 = `(queryId,cell,lane,tick)` hash 회전(기아 완화) ⑦self/team/radius filter 이전에 budget 소비(strict bound) ⑧radius 통과분만 fixed max-heap `(distSq,AgentId)` ⑨canonical key 정렬 반환.
- **의미 훼손 최소 근거**: recruit는 "최근접 1명"만 → k가 최근접 포함하면 사실상 무손실. combat touchCount는 `clamp01(touch/PairNormalizer)`로 포화 → 고밀도서 대부분 1.0, 캡 영향 작음. leader는 `ownLocal<=1`(홀로 여부) 핵심이라 소수 카운트로 정확, enemy 최다팀 tie=낮은 팀id.

## 6. 검증 방법론
- 하네스: full-root 2000/5000(+10K), M2후 presentation-free kernel harness 10K/20K+, **live prefab/SDF ON/CC.Move 0 assertion**, 실제 agent 수, 고정 dt=0.02, 독립 ≥3회, A/B는 `A-B-B-A`, median·p95·paired ratio·95%CI·환경(CPU/Burst/worker/build hash) 기록.
- 알고리즘 게이트: `CandidatesVisited ≤ QueryCount×LaneCount×B`, work/N 밀도 무증가, victim v² 제거, cap-off=reference 동일, cap-hit·skipped telemetry.
- 거동 A/B: critical fixture 100% 보존(recruit tie-break, strict-larger 승리, equal no-conversion, pair accumulator/emit 순서, escort immunity, killer count/tie, radius밖 false contact 0, instant+rate-limit, permutation). dense 통계 A/B(recruit 지연/claimant·team mismatch/contact deficit/conversion 수/elimination/team-count 시계열·winner/separation·position·velocity p50·p95·max 편차) → 임계치는 실측+영상 비교 후 승인.
- 결정성: 전 scale/seed same-seed 2× 비교(첫 조합만 X), 최초 divergence tick·필드 diff 출력, worker/batch permutation 전수, event transcript+RNG state 포함.
- GC/수명: warmup 후 `GC.GetAllocatedBytesForCurrentThread()` 1000 tick delta 0 + Player `ProfilerRecorder` GC counter 300 frame+ 연속 0 교차확인(현 `GC.GetTotalMemory` 프록시는 참고). Initialize→ticks→Shutdown 반복(handle Complete·`IsCreated==false`·중복 Shutdown 안전·leak 0).
- 최종 성능: 단계별 개선 paired 95%CI가 A/A 밴드 밖(개선방향), 무관 sub-stage는 등가 밴드 내, M1=work bound hard gate, M2=전체 SimTick hard gate, 최종 Player=디바이스 budget. **절대 ms/fps/% 현재 미확정**.

## 7. 리스크 + rebase 노트
- 밀도 캡 = 게임플레이 변경 → 거동 게이트 + 보수적 기본값 + **성능 리팩토링과 분리 커밋**.
- Burst 코드젠 ≠ Mono → 재기준선 필수(기존 baseline 직접비교 불가). M2에서 Native/Burst/parallel을 한 번에 바꾸면 divergence 원인 분리 불가 → 3분할 필수.
- RNG 재설계가 기존 시드 재현 깸 → 의도된 재기준선, 문서화.
- full grid 메모리 O(N), combat slots O(N×team×K) → 10K/20K 실측.
- `allowUnsafeCode` true 전환 시에도 WallField식 안전 인덱싱 유지 권장. 새 asmdef 금지, `Unity.Jobs` 참조 필요 여부는 컴파일 게이트.
- **확인 불가**: 30ms의 실제 sub-stage 분해 — M-sim-0가 첫 관문. SDF가 dynamic collider/기존 CC·Raycast 대상 전체 표현하는지 validator 전 미확인.
- **rebase**(리팩토링이 Crowd/Core+CrowdRoot 전부): 라인 아닌 책임 단위. rebase 후 재확인 = SimTick 호출·tick 순서 계약(restore→heading→move→steer→mirror→rebuild→recruit→combat→commit→publish; 병렬 스냅샷 전제가 이 순서 의존), pre/post-move grid 경계, AgentId 고유성·slot 안정, team≤4, 전향 후 re-lane 시점, SDF/CC 상태, 모든 System.Random/Raycast/CheckSphere/CC 위치, QueryCircle 시그니처, CrowdSimProfiler Seg 열거.
- CLAUDE.md: §11.5 프리팹/렌더 불변(충돌 없음), baked CC 제거만 §11.5 "baked CC" 일부 완화 → 문서화. §0.2 Job 도입은 sim 병목 실측으로 정당화. §12 무관(싱글).

## 우선순위 (합의 핵심)
1. **알고리즘(M-sim-1, 밀도 캡핑) > Burst/멀티스레드(M-sim-2, 상수배)** — 초선형 항이 근본. M-sim-0 실측으로 가설(combat/recruit 밀도) 확정이 첫 관문.
2. sim만 대상 — 렌더/애니 스택 불변(모바일 만단위 T3는 별도 트랙).

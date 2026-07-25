# SIM_OPT 만단위(10k~50k) 다음 단계 계획 (데이터 기반)

- 작성: 2026-07-20
- 상태: **계획만(미구현).** 나중에 작업 예정.
- 전제: P3(네이티브 위치 권위)까지 완료된 코드 기준. 관련 로드맵은 [`SIM_OPT_PLAN.md`](SIM_OPT_PLAN.md) §2 / M-sim-1·2, 현황은 [`WORK_STATE.md`](../WORK_STATE.md).

---

## 0. 선행 완료 (이번 브랜치 `feat/crowd-sdf-perf`)

P2 → Stage A → S2 → S3 → S4a → S4b1 → S4b2 (7커밋). 전부 오라클 `md5=4F79282EB20A79023B45F2EB2DE5271B` 바이트 동일 검증(시뮬 결과 불변, 메커니즘만 변경) + 렌더 변경은 shot 하네스 시각 검증. Codex↔Claude 교차검증 합의로 단계별 진행.
- **Stage A**: 뉴트럴 SDF `WallSolver.Resolve` Burst 잡화 → SimTick@3000 16.3→10.4ms (당장의 가장 큰 이득).
- **S2~S4b2**: 팔로워/뉴트럴 transform 왕복 완전 제거. `buffer.Pos + _visualRender + _visualYaw`가 완전 권위, 리더(≤4)만 라이브 트랜스폼.

---

## 1. 측정 데이터 (이 계획의 근거)

`CrowdProfileHarness` headless(edit-mode, `-nographics`), SDF ON, seed=12345, warmup 200 / timing 1000틱, median.

재현:
```
Unity -batchmode -nographics -quit -projectPath <proj> -executeMethod CrowdProfileHarness.RunFromBatch \
  -profileScales 5000,10000 -profileCounters -profileOut <out.txt>
```

### SimTick 세그먼트 분해

| 세그먼트 | 5k ms (%) | 10k ms (%) | 잡화 상태 |
|---|---|---|---|
| **FollowerSteer** | 1.29 (21%) | **11.83 (57%)** | 이미 잡 + 직렬 prepass/presentation |
| **NeutralMove** | 3.38 (55%) | **5.64 (27%)** | 이미 잡 + 직렬 prepass/presentation |
| Recruit | 1.06 (17%) | 1.60 (7.7%) | 직렬 managed, 비-Burst |
| Combat | 0.30 (4.8%) | 1.39 (6.7%) | 직렬 managed, 비-Burst |
| GridRebuild | 0.09 (1.5%) | 0.21 (**1.0%**) | 직렬 managed, 비-Burst |
| LeaderMove | 0.03 (0.5%) | 0.02 (**0.09%**) | ≤4명 (teamCount) |
| Restore+Mirror | ~0.02 (0.3%) | 0.04 (0.2%) | P3로 대부분 제거 |
| SimTick 합계(median) | 5.87 | **20.67** | |

카운터(별도 run): Combat `candidate_visits` 5k 0.89M → 10k **76.8M/tick** (초선형, `combat_touching`가 10k에서 발생), victim_comparisons_per_victim ≈ 31 (O(v²) 정렬). Recruit `candidate_visits` ~35k/tick(밀집 쿼리).

### 결론 (전제 수정)
- **리더 잡화 = 무의미**(0.09%, ≤4명 → 스케줄 오버헤드 > 일). 두 리뷰어도 확인.
- **그리드-리빌드 단독 잡화 = 이득 미미**(1%). 단, 병렬 쿼리의 *토대*로서 카운팅-소트 전환은 리졸버 잡화의 전제.
- **리졸버 전체(Recruit+Combat) = 15%.** Combat은 초선형이라 50k·격전에서 급증.
- **진짜 병목 = 이동(84%)** — 이미 잡화됐지만 세그먼트가 `.Complete()` 대기(병렬 잡 wall-clock) + **직렬 prepass/presentation**을 모두 포함.

주의: 이 프로파일은 **headless(SMR 경로, gpuActive=false)**라 GPU 빌드가 건너뛰는 transform 위치 쓰기까지 포함 → GPU 빌드의 presentation은 더 쌈. 상대 순위(이동 ≫ 리졸버)는 유효.

---

## 2. 측정 완료 — 직렬 vs 잡 분리 → **T1b 확정** (2026-07-26, 측정 트리 `fdf909d` · 증거 커밋 `ff60d39`)

> ⚠️ **이 절의 "T1b 확정" 판정은 현재 재결정 중이다** — 아래 본문은 측정 당시 기록 그대로다. 이 측정이 헤드리스(SMR 경로)라 `*Present`가 T1a로 제거된 쓰기를 포함하는 문제이며, 선행 조건과 유보 사항은 [§4 항목 3](#4-권장-순서)과 [`WORK_STATE.md`](../WORK_STATE.md) "운영 계약" 절에 있다. **재측정 전까지 이 절의 판정을 근거로 T1b에 착수하지 말 것.**

**정정.** 이 절의 초판이 제안했던 `FollowerSteer − CCMove − .Complete대기` 산식은 **성립하지 않는다.** `Seg.CCMove`는 다섯 지점(리더 `ApplyHorizontalMove`, 팔로워 SDF 이동 잡 대기, 팔로워 CC 폴백, 중립 SDF 이동 잡 대기, 중립 CC 폴백)을 합산하는 단일 버킷이라 10k의 `CCMove` 2.78ms에는 팔로워·중립 대기가 섞여 있고, `*JobWait`은 `CCMove`와 inclusive 중첩이라 이중 차감이 된다. 대신 `1485848`에서 분기별 Seg 7개(`Follower/Neutral × Prepass/JobWait/Present` + `FollowerGridSnapshot`)를 추가해 직접 계측했다.

**측정:** headless SDF ON, 5000/10000, 동일 인자 3런. 환경·verbatim 커맨드라인·부하 통제·판정 규칙·판독 주의는 [`Perf/MANIFEST.md`](Perf/MANIFEST.md), 원시 출력은 [`Perf/simopt10k_step1_r{1,2,3}.txt`](Perf/).

- **T1b 대상(직렬)** = `FollowerPrepass`+`FollowerGridSnapshot`+`FollowerPresent`+`NeutralPrepass`+`NeutralPresent`
- **T2 대상(잡 벽시계)** = `FollowerJobWait`+`NeutralJobWait`
- **spread** = 두 합 각각의 (max−min)을 더한 노이즈 대역. 차이가 spread를 못 넘으면 판정 불가.

| 스케일 | T1b 대상 | T2 대상 | Total | 차이 / spread |
|---|---|---|---|---|
| 5000 | **4.39 (68.3%)** | 0.52 (8.1%) | 6.43 | 3.87 / 0.079 = **49배** |
| 10000 | **14.47 (70.4%)** | 2.78 (13.5%) | 20.56 | 11.69 / 0.197 = **59배** |

**판정: T1b.** 차이가 런 간 노이즈 대역의 49~59배 → 결정적. 10k 직렬 내역은 `FollowerPresent` 8.87(43.1%) > `NeutralPresent` 4.71(22.9%) ≫ `NeutralPrepass` 0.70 > `FollowerPrepass` 0.18 > `FollowerGridSnapshot` 0.02 — **두 Present 루프만으로 SimTick의 66%**다. 반면 **T2의 상한**은 두 잡 대기를 0으로 만들어도 10k 13.5% / 5k 8.1%다. (5k→10k Total 3.20배 = 에이전트 2배 대비 초선형이고, 그 증가분도 팔로워 계열 6~10배가 끌고 간다.)

**상한 주의 + 손익분기.** 헤드리스는 SMR 경로라 `*Present`가 GPU 빌드에서는 게이팅되는 `transform.rotation`·`Animator.speed` 쓰기(T1a)까지 포함한다 → **`*Present`는 상한**이다. 그래도 판정은 뒤집히지 않는다: 5k는 `*Present`가 공짜여도 prepass만으로 0.70 > T2 0.52고, 10k는 `*Present` 비용의 **86.1% 초과**가 SMR 전용이어야 뒤집힌다. 두 루프 모두 `Quaternion.Euler(0f, X, 0f).eulerAngles.y`(`CrowdRoot.cs:1430`, `:1440`, `:1631`)를 게이트 **밖에서 무조건** 실행하고 이는 에이전트·틱당 managed→native→managed 왕복이라 GPU 빌드에서도 남으므로, 86.1%는 비현실적이다.

**측정 공백(2건).** ①하네스가 팔로워/중립 모집단 수를 출력하지 않아 위 스케일링 비대칭의 원인(모집단 구성 이동)은 추론이며 미측정. ②`RenderInterpolate`의 per-agent transform 쓰기(`CrowdRoot.cs:747`, `:755`)는 하네스가 호출하지 않아 이 측정 범위 밖이다.

---

## 3. 계획 (Tier = 데이터 우선순위)

각 단계는 **P3에서 검증된 방식**을 따른다: 계획 Codex↔Claude 교차검증 → 구현/검증 서브에이전트 분리 → **오라클 `md5` 바이트 동일(시뮬 불변) 게이트** + (렌더 변경 시) shot 하네스 시각 검증 → 커밋/푸시. 결정성 계약: `FloatMode.Strict`, no FastMath, 안정 AgentId 순 직렬 reduction(atomic-float 금지), canonical order.

### Tier 1 — 이동 84% 공략 (최고 가치)

**T1a. GPU 경로 죽은 `transform.rotation` 쓰기 제거** *(저위험·즉효, 먼저 권장)*
- 현재 `Human.SetHeadingAndSpeed`가 매 틱 전 에이전트 `transform.rotation = Quaternion.Euler(0,h,0)`. GPU 경로에선 yaw가 `_visualYaw`에서 오므로 **죽은 쓰기**(SMR 폴백만 필요).
- `(!_gpuRenderActive)`로 게이팅 → 10k×native rotation write/tick 제거. **S4b2 위치-쓰기 게이팅 패턴 그대로 재사용.**
- 검증: 오라클 byte-identical(sim 불변) + shot(GPU/ SMR 양쪽 facing 정상). 리스크 낮음.
- 주의: headless 프로파일(SMR)에선 안 보임 → **GPU 빌드/실측에서만 이득**. DevHud/실기기 또는 gpuActive 강제 프로파일로 확인.

**T1b. 팔로워/뉴트럴 직렬 prepass + presentation 잡화** *(§2 측정 후 착수)*
- 현재 per-agent managed 직렬(위치 캡처는 S4a로 PrevPos화됐지만, presentation의 `SetHeadingAndSpeed`·`_visualYaw` `Quaternion.Euler`·`_visualPrev/_visualCur/_visualRender` 쓰기가 메인스레드 O(N)).
- 로드맵 M-sim-2c의 full-SoA IJobParallelFor 종단 상태로: heading/speed/yaw/visual을 잡 출력 슬롯으로. `Quaternion.Euler`는 잡 내 `math`로 대체(단, 렌더 yaw는 기존과 bit 동일하게 — 정규화 값 주의, S4b1 교훈).
- 검증: 오라클 byte-identical + shot. 결정성: per-agent 고정 출력 슬롯, 이웃은 prev-tick 스냅샷.

### Tier 2 — 스티어링 잡 wall-clock 축소
**T2. 분리(separation) 이웃 스캔 밀도 캡 (로드맵 M-sim-1 BoundedLaneGrid)**
- Recruit/Combat/Separation 쿼리에 per-team visit budget. 밀집 시 초선형 스캔 상한.
- **주의(이미 되돌린 이력):** `SeparationVisitBudget` 캡은 밀집 클러스터 지터로 revert됨. 재도입 시 **정책 수정 필수**: bucket 해시충돌 점유수 대신 **정확 셀 점유수**로 판정(false positive 줄임) + T/gain/tangential 튜닝. 결정성: `(distSq,AgentId)` total-order top-k, 앤티-스타베이션은 `(queryAgentId,cell,lane,tick)` 순수 해시(RNG 미소모).
- 리스크 중(시각/거동 변화) → shot + 거동 눈검증 필수. **T1b 측정 결과 잡 wall-clock이 지배적일 때만 우선.**

### Tier 3 — 리졸버 15% (50k·격전에서 중요)
**T3a. 그리드 카운팅-소트 전환** (로드맵 §2)
- 현재: full-rebuild LIFO 링크드리스트 해시(managed). → **①IJobParallelFor cell/lane key → ②단일 Burst IJob count→prefix→scatter(canonical) → ③변환 후 O(N) re-lane.** 안정 `cellKey→lane→AgentId` 배치. `NativeParallelMultiHashMap` 금지(열거 순서 비결정).
- 자체 1%지만 리졸버/분리 병렬 쿼리의 전제.
- 검증: 오라클 byte-identical(쿼리 소비자는 이미 `(distSq,AgentId)` 순서 무관).

**T3b. Recruit/Combat 잡화**
- 병렬 후보 발견(per-agent) + **직렬 Burst canonical reduction**(팀-쌍, victim 선택, 리더 소거, commit). Combat 초선형 → 50k 관건.
- **victim O(v²) 삽입정렬 제거**(로드맵): instant=결정적 radix/order-free, rate-limited=bounded max-heap prefix.
- 결정성: combat edge `(min,maxAgentId)` canonicalize→sort→unique, victim `(assignedDistSq,victimAgentId)`, 팀-쌍 `(winner,loserTeamId)`. RNG는 per-agent/team `uint4` 해시 스트림(공유 System.Random 잡 반입 금지).

### 제외
- 리더(≤4, 0.09%), 그리드-리빌드 단독 이득.

---

## 4. 권장 순서
1. **[완료] §2 분리-측정** — `1485848`에서 Seg 7개 추가 → `ff60d39`에서 3런 측정 기록(측정 트리는 `fdf909d`) → **T1b 확정**(§2).
2. **[완료] T1a** (죽은 rotation 쓰기 제거, `fdf909d`). **GPU 경로 실측 완료(`e0f81e4`)** — play-mode `CrowdPerfHarnessP95`, 10k, A/B/B/A, `gpuActive=T`: SimTick 틱당 **−12.83ms(−55.5%)**, 동일-arm spread의 8.0배 → 개선 확정. 단 그 CSV 열은 `RenderInterpolate` 1회를 포함하므로 **순수 SimTick 이득은 [−20.76, −12.83]ms/틱 범위로만 묶인다**(헤드라인은 보수적 끝을 쓴다). 증거·판독 주의: [`Perf/MANIFEST.md`](Perf/MANIFEST.md) §7.
3. **[잠정 다음 — 판정 재결정 중, 재측정 대기] T1b** (팔로워/뉴트럴 직렬 prepass + presentation 잡화). 잠정 최우선 타깃은 두 `*Present` 루프(10k SimTick의 66%). **단 §2의 "T1b 확정"은 더 이상 확정이 아니다** — §2는 헤드리스(SMR 경로) 측정이라 `*Present`가 T1a(`fdf909d`)로 제거된 쓰기를 포함하고, T1a 실측치가 §2에 기록된 손익분기(`Present` 비용의 86.1%)를 넘는다. **착수 전 선행 조건: `CrowdProfileHarness`를 `-nographics` 없이 돌려 `_gpuRenderActive == true` 상태로 세그먼트를 재측정할 것.** 근거·수치·유보 조건(서로 다른 하네스/모드 비교라 반증이 아니라 해소할 긴장이라는 점)은 [`WORK_STATE.md`](../WORK_STATE.md) "운영 계약" 절과 [`CHANGELOG.md`](CHANGELOG.md) §2.2에 있다 — 여기서 반복하지 않는다.
4. 그다음 **T3a → T3b** (50k 목표 시). **T2는 후순위** — §2 판정상 상한이 10k 13.5%다.
5. 각 단계: 계획 교차검증 → 구현/검증 분리 → 오라클 byte-identical + shot → 커밋.

## 5. 모바일 최약기기 1만 "하한" 조건 (Codex R5 판단 — 외삽, 미측정)

> 출처: `git show fb14a41:codex_burst_out5.txt`. 5년 window(2021~2026) 최약 AOS/iOS 기준 1만 floor 판정 = **(B) 조건부 현실적**. 실기 측정이 아니라 코드·ProjectSettings 근거 + 기기 스펙 외삽이므로 아래 수치를 성능 목표로 못박지 말 것(`WORK_STATE.md` §7 "성능 목표 수치 지어내기 금지").

- **렌더 경로 이중화 필수**: `Graphics.RenderMeshIndirect`는 compute/SSBO를 요구해 **GLES3.1+**에서만 성립한다. 본 프로젝트는 MinSdk 25 · Vulkan 우선 + GLES3 폴백이고 **GLES3.1을 강제하지 않아** GLES3.0-only 기기가 window에 들어온다 → `DrawMeshInstanced` + `MaterialPropertyBlock` **2차 인스턴싱 경로**가 필요하다. 현재 `CrowdRenderer.cs:105`(`graphicsShaderLevel < 45 || !supportsComputeShaders`)와 `:114`는 **SMR 경로로 내려가는 게이트일 뿐** 이 폴백을 제공하지 않는다.
- **하나라도 빼면 1만 하한이 깨지는 항목**: ①동시 가시 hard cap ②frustum/거리 컬링 ③고정 50Hz 심(`GameplayRoot`)에서 **분리된** multi-rate behavior LOD ④밀도 캡(§3 분리 이웃 스캔 캡) ⑤per-agent RNG ⑥최약기기 장시간 thermal soak 수용 게이트.
- **"1만"의 정의**: 논리적 **활성** population + 동시 가시 hard cap. 1만 전원 동시 가시로 해석하면 최약기기에서 판정이 **(C) 비현실적**으로 뒤집힌다.
- **메모리는 제약 아님**: SoA 1만 ≈ 1MB(5만 ≈ 5MB). binding은 렌더 제출·GPU fill·지속 발열.
- 렌더-스택 티어별 상한(R4 — **§3의 T1a/T2/T3a와 다른 축**, 전부 외삽·미측정): T0 300~700 / T1(sim만) 300~900 = 자릿수 불변 / T2 3천~8천 / T3(GO 제거+indirect) 1만~2만 = **1만 첫 도달** / T4(+VAT·LOD) 총 활성 2만~4만 · 동시 가시 1만~2만.
- VAT/BRG/multi-LOD는 품질·헤드룸(선택)이지 하한 필수는 아니다. "보장" 선언은 Mali-G52/Adreno 610급 포함 device matrix의 실기 thermal soak 통과 후에만.

## 6. 참고
- 세그먼트 계측: `CrowdSimProfiler.Seg.*` (Enabled=false 기본, 결정성 중립). DERIVED_KernelResolvers = Grid+Recruit+Combat.
- CC 잔여 정리(정지된 agent CharacterController 제거)는 M-sim-3. 현재 SDF+GPU에서 무해(Unit 레이어 쿼리 없음)라 수용 중 — 50k에서 PhysX broadphase 비용이면 그때 제거.
- `float2/Mathf→math` 전환, `DeterministicRng(uint4)`는 M-sim-2b (아직 미완).

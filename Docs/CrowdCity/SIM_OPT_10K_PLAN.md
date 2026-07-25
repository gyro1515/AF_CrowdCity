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

> **GPU 경로 실측 후(§2):** 순위는 유효하지만 **격차가 크게 좁혀졌다** — 10k에서 이동(`FollowerSteer`+`NeutralMove`) 84% → **64.5%**, 리졸버(`Recruit`+`Combat`) 15% → **32.3%**. 위 표의 백분율은 **SMR 경로 값**이므로 인용할 때 모드를 함께 적을 것.

---

## 2. 측정 완료 — 직렬 vs 잡 분리 → **10k 판정 불가 / 5k T1b** (2026-07-26, 측정 트리 `fdf909d`·`f9c1cdc` · 증거 커밋 `ff60d39`·`e0f81e4`·본 커밋)

**결론부터.** 이 질문은 **닫혔다.** 두 번 쟀고 답이 스케일에 따라 갈린다.

- **5k: T1b가 이긴다** — GPU 경로에서도 직렬 1.74 vs 잡 대기 0.54, 차이가 런 간 노이즈 대역의 3.17배.
- **10k: 판정 불가(inconclusive)** — GPU 경로에서 직렬 3.15 vs 잡 대기 3.01, 두 합이 사실상 **동률**이다(차이/spread = 0.12배). 런을 6개로 늘려도 유의하지 않다.
- **"그러면 T2를 해라"도 따라 나오지 않는다.** 판정 불가는 "T2가 이겼다"가 아니라 "이 지표로는 우열을 못 가린다"다. 오히려 이 데이터의 변동은 병렬 구간에 편중돼 **T2를 크게 보이게 하는 방향**인데도 T2가 앞서지 못했다.

**왜 판정이 바뀌었나.** 첫 측정(`ff60d39`)은 **헤드리스 = SMR 경로**였고, 그래서 `*Present` 수치가 **T1a(`fdf909d`)가 제거한 바로 그 `transform.rotation`·`Animator.speed` 쓰기를 포함**하고 있었다. 그 데이터 위에서 "T1b" 판정은 **옳았다** — 계산도, 규칙도, 적용도 틀리지 않았다. 다만 그 판정이 근거로 삼은 비용의 대부분이 **T1a가 이미 지운 것**이었다. T1a가 들어간 GPU 경로에서 같은 세그먼트를 다시 재면 그 몫이 빠지고, 남은 직렬과 잡 대기가 동률이 된다. 즉 **첫 판정을 뒤집은 것이 아니라, 그 판정이 서 있던 바닥이 T1a로 사라졌다.**

**정정(유효).** 이 절의 초판이 제안했던 `FollowerSteer − CCMove − .Complete대기` 산식은 **성립하지 않는다.** `Seg.CCMove`는 다섯 지점(리더 `ApplyHorizontalMove`, 팔로워 SDF 이동 잡 대기, 팔로워 CC 폴백, 중립 SDF 이동 잡 대기, 중립 CC 폴백)을 합산하는 단일 버킷이라 10k의 `CCMove`에는 팔로워·중립 대기가 섞여 있고, `*JobWait`은 `CCMove`와 inclusive 중첩이라 이중 차감이 된다. 대신 `1485848`에서 분기별 Seg 7개(`Follower/Neutral × Prepass/JobWait/Present` + `FollowerGridSnapshot`)를 추가해 직접 계측했다.

**판정 규칙(두 측정 공통, 측정 전 고정).**

- **T1b 대상(직렬)** = `FollowerPrepass`+`FollowerGridSnapshot`+`FollowerPresent`+`NeutralPrepass`+`NeutralPresent`
- **T2 대상(잡 벽시계)** = `FollowerJobWait`+`NeutralJobWait`
- **spread** = 두 합 각각의 (max−min)을 더한 노이즈 대역. 차이가 spread를 못 넘으면 판정 불가.

**측정 2건.** 둘 다 `CrowdProfileHarness`, SDF ON, seed=12345, 5000/10000, 동일 인자 3런. 차이는 `-nographics` 하나뿐이다. 환경·verbatim 커맨드라인·GPU 활성 증명·부하 통제·판독 주의는 [`Perf/MANIFEST.md`](Perf/MANIFEST.md) §1~§6(SMR)·§8(GPU).

| 스케일 | 모드 | T1b 대상(직렬) | T2 대상(잡 대기) | Total | 차이 / spread | 판정 |
|---|---|---|---|---|---|---|
| 5000 | SMR (헤드리스) | 4.39 (68.3%) | 0.52 (8.1%) | 6.43 | 3.87 / 0.079 = **49배** | T1b |
| 10000 | SMR (헤드리스) | 14.47 (70.4%) | 2.78 (13.5%) | 20.56 | 11.69 / 0.197 = **59배** | T1b |
| 5000 | **GPU** | **1.74 (45.4%)** | 0.54 (14.2%) | 3.84 | 1.197 / 0.377 = **3.17배** | **T1b** |
| 10000 | **GPU** | 3.15 (32.9%) | 3.01 (31.5%) | **9.56** | 0.137 / 1.129 = **0.12배** | **판정 불가** |

원시 출력: [`Perf/simopt10k_step1_r{1,2,3}.txt`](Perf/) (SMR) · [`Perf/simopt10k_gpupath_r{1,2,3}.txt`](Perf/) (GPU). **후자의 파일 헤더는 자기를 `edit-mode headless 측정`이라고 잘못 소개한다** — 하네스가 하드코딩한 문구다(MANIFEST §8 상단 경고).

**T1a가 지운 몫(10k 중앙값).** `*Present` 합 13.58 → **2.22 ms(−83.6%)**, Total 20.56 → **9.56 ms(−53.5%)**. 반면 `FollowerJobWait`·`CCMove`는 GPU 경로에서 오히려 8% 높다 — 게이트가 두 Present 루프에만 들어갔다는 사실과 정확히 일치한다. 계획이 측정 전에 못 박아 둔 손익분기(10k에서 `*Present` 비용의 **86.1% 초과**가 SMR 전용이어야 판정이 뒤집힌다)와 대조하면 실측 감소는 **83.6%** — 손익분기보다 2.5%p 낮아 "뒤집히지는 않지만 동률"이라는 결과와 정합한다. 당시 "86.1%는 비현실적"이라고 본 근거(`Quaternion.Euler(...).eulerAngles.y`가 게이트 밖에 남는다 — `CrowdRoot.cs:1430`, `:1440`, `:1631`)는 여전히 옳고, 그 잔여가 GPU 경로 `*Present` 2.22 ms의 일부다.

### 2.1 전제가 바뀌었다 — 다음 타깃은 10k 세그먼트 점유율에서 나오지 않는다

**이 계획서 전체가 "10k가 문제다"라는 전제로 쓰였다.** 그 전제는 이제 성립하지 않는다.

T1a **하나만으로** GPU 경로 10k가 **9.56 ms/tick**(mean, edit-mode `CrowdProfileHarness`)이 됐다. 50Hz 고정 스텝(`GameplayRoot` `FixedStep = 0.02`)의 틱 주기가 20 ms이므로 이는 주기의 **약 48%**이고, p95(11.16 ms)로도 **약 56%**다. — 이 20 ms는 **루프 케이던스라는 구조적 사실**이며 성능 목표치나 수용 임계가 아니다(§7 "성능 목표 수치 지어내기 금지"). 그러나 "10k 틱이 주기를 넘긴다"는 이 계획의 출발점은 데스크톱 에디터 기준으로 **더 이상 관측되지 않는다**(SMR 경로 20.56 ms는 넘겼다).

**따라서 다음 타깃은 20k~50k에서 무엇이 먼저 깨지는가로부터 다시 도출해야 한다.** 10k 세그먼트 점유율로 고르면 안 된다 — 10k에서 두 후보가 동률이고, 동률인 두 값의 순위는 노이즈가 정한다.

**다음 타깃은 여기서 고르지 않는다. 열려 있다.** 필요한 것은 더 정밀한 10k 측정이 아니라 **다른 판별자**다. 후보 축(어느 것도 아직 측정되지 않았다): 스케일 지수(어느 세그먼트가 초선형인가 — §1의 `Combat` `candidate_visits` 0.89M→76.8M, `FollowerPresent` 10.04배가 그 축의 단서다), 워커 코어 수가 적은 실기기에서의 병렬 구간 거동, §5의 렌더 티어 상한. 이 축들은 §3의 Tier 순서와 다른 답을 낼 수 있다.

**측정 공백(3건).** ①하네스가 팔로워/중립 모집단 수를 출력하지 않아 스케일링 비대칭의 원인(모집단 구성 이동)은 추론이며 미측정. ②`RenderInterpolate`의 per-agent transform 쓰기(`CrowdRoot.cs:747`, `:755`)는 하네스가 호출하지 않아 두 측정 모두의 범위 밖이다. ③GPU 경로 측정도 **edit mode**라 지연 `Destroy`가 거부돼 rig가 비활성 상태로 살아 있다 — 즉 재어진 것은 T1a의 6개 게이트뿐이고, 출하 빌드가 "본 계층 부재"로 추가로 얻는 절감은 미측정이다. 그래서 위 −83.6%는 출하 빌드 절감의 **상한**이다(MANIFEST §8.8-a).

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
3. **[완료] GPU 경로 재측정** — `CrowdProfileHarness`를 `-nographics` 없이 3런(측정 트리 `f9c1cdc`). 결과: **10k 판정 불가 / 5k T1b**(§2). 증거: [`Perf/MANIFEST.md`](Perf/MANIFEST.md) §8.
4. **[열려 있음 — 다음 타깃 미확정]** §2.1이 정본이다. 정리하면:
   - **T1b**(팔로워/뉴트럴 직렬 prepass + presentation 잡화)는 **5k에서는 여전히 유효한 후보**(3.17배)이지만, **10k에서는 "직렬 점유율이 크다"가 더 이상 착수 근거가 못 된다** — T2와 동률이다. GPU 경로 10k 직렬 3.15 ms 중 두 `*Present`가 2.22 ms로 여전히 최대 항목이라는 사실 자체는 유지된다(SimTick의 23.2%).
   - **T2**(분리 이웃 스캔 밀도 캡)도 승자가 아니다. 단 **"상한이 10k 13.5%"라는 이전 후순위 근거는 무효다** — 그건 SMR 경로 수치이고, GPU 경로에서는 잡 대기가 Total의 **31.5%**다. T2의 상한은 이전 서술보다 **두 배 이상 크다.** 그래도 §2 규칙으로는 T1b를 이기지 못했고, [§3의 revert 이력](#tier-2--스티어링-잡-wall-clock-축소)(밀집 클러스터 지터)이 그대로 남아 있다.
   - **T3a → T3b**는 50k를 목표로 할 때의 경로이며 이번 측정으로 순위가 바뀌지 않았다(§1의 `Combat` 초선형 근거는 10k 세그먼트 점유율이 아니라 스케일 지수에서 온다).
   - **착수 전 필요한 것은 더 정밀한 10k 측정이 아니라 다른 판별자다**(§2.1). 세그먼트 점유율로 고르지 말 것.
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

# SIM_OPT 만단위(10k~50k) 다음 단계 계획 (데이터 기반)

- 작성: 2026-07-20
- 상태: **계획만(미구현).** 나중에 작업 예정.
- 전제: P3(네이티브 위치 권위)까지 완료된 코드 기준. 관련 로드맵은 [`SIM_OPT_PLAN.md`](SIM_OPT_PLAN.md) §2 / M-sim-1·2, 현황은 [`SIM_OPT_HANDOFF.md`](SIM_OPT_HANDOFF.md).

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

## 2. 미확정 (구현 전 측정 필요)

**FollowerSteer 11.83ms의 직렬 vs 잡-wall-clock 비율 미분리.**
- 직렬(prepass 위치캡처 + presentation: `SetHeadingAndSpeed`·`Quaternion.Euler`·visual 배열 쓰기)이 크면 → **T1b(직렬 잡화)**가 유효.
- 잡 wall-clock(SDF resolve + separation, 이미 병렬, 일 자체가 큼)이 크면 → **T2(일 축소 = 밀도 캡)**가 필요.
- **액션:** prepass/presentation 구간과 `.Complete()` 대기를 분리 계측(임시 Seg 추가 또는 Stopwatch)해 T1b vs T2 우선순위 확정. CCMove(SDF resolve) 세그가 이미 10k 2.76ms로 잡혀 있으니, `FollowerSteer − CCMove − .Complete대기`가 직렬 몫의 근사.

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
1. **§2 분리-측정** (T1b vs T2 우선순위 확정).
2. **T1a** (죽은 rotation 쓰기 제거) — 저위험·GPU 빌드 즉효, 먼저.
3. 측정 결과에 따라 **T1b 또는 T2**.
4. 그다음 **T3a → T3b** (50k 목표 시).
5. 각 단계: 계획 교차검증 → 구현/검증 분리 → 오라클 byte-identical + shot → 커밋.

## 5. 참고
- 세그먼트 계측: `CrowdSimProfiler.Seg.*` (Enabled=false 기본, 결정성 중립). DERIVED_KernelResolvers = Grid+Recruit+Combat.
- CC 잔여 정리(정지된 agent CharacterController 제거)는 M-sim-3. 현재 SDF+GPU에서 무해(Unit 레이어 쿼리 없음)라 수용 중 — 50k에서 PhysX broadphase 비용이면 그때 제거.
- `float2/Mathf→math` 전환, `DeterministicRng(uint4)`는 M-sim-2b (아직 미완).

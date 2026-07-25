# SIM_OPT 10k — step1 "직렬 vs 잡 분리" 측정 MANIFEST

- 측정일: 2026-07-26
- 대상 질문: [`../SIM_OPT_10K_PLAN.md`](../SIM_OPT_10K_PLAN.md) §2 — 다음 단계가 **T1b**(직렬 제시 패스 잡화)인가 **T2**(잡 일 축소)인가
- 원시 출력: 이 디렉터리의 `simopt10k_step1_r1.txt` / `r2.txt` / `r3.txt`
- 이 문서의 역할: **증거 기록**(환경·커맨드라인·부하 통제·판정 규칙·판독 주의). 판정과 그 후속 계획은 `SIM_OPT_10K_PLAN.md` §2에 있다.

---

## 1. 측정 환경

| 항목 | 값 |
|---|---|
| 커밋 | `fdf909d` (브랜치 `feat/crowd-sim-10k`) |
| Unity | `6000.3.9f1` (revision `7a9955a4f2fa`) |
| 호스트 CPU | AMD Ryzen 5 5600X 6-Core Processor — 6코어 / 12논리프로세서 |
| OS | Windows 10 Pro 64bit (10.0.19045), 물리 메모리 32712 MB (런 로그 헤더) |
| 하네스 | `CrowdProfileHarness` (edit-mode headless, `-nographics`) |
| 씬/시드 | `Assets/@Project/Scenes/GameScene.unity`, seed=12345, rivalCount=3, groundY=0.5 |
| 솔버 | SDF ON 강제 + `IsSdfActive` 단언 + `CcMoveFallbacks == 0` 단언 (baseline 인정 조건) |
| dt | 0.02 |
| 스케일 | neutralCount 5000 / 10000 (agents 5004 / 10004) |
| 스케일당 틱 | sdfVerify 20 + warmup 200 + timing 1000 + gc 500 |
| 런 수 | 3 (r1 / r2 / r3) — 인자 동일, `-profileLabel`·`-profileOut`·`-logFile`만 다름 |

`counterTicks=300`은 하네스 헤더에 인쇄되지만 **이번 측정에서 실행되지 않았다** — `-profileCounters` 미전달(§2 참조).

---

## 2. 커맨드라인 (verbatim)

r1 로그의 `COMMAND LINE ARGUMENTS` 원문이다. r2/r3는 `rN` 세 곳만 다르다.

```
C:\Program Files\Unity\Hub\Editor\6000.3.9f1\Editor\Unity.exe
-batchmode
-nographics
-quit
-projectPath
D:\UNITY\UNITY_PROJECT\ActionFitPro\AF_CrowdCity
-executeMethod
CrowdProfileHarness.RunFromBatch
-profileScales
5000,10000
-profileLabel
step1_serialsplit_r1
-profileOut
D:\UNITY\UNITY_PROJECT\ActionFitPro\AF_CrowdCity\Docs\CrowdCity\Perf\simopt10k_step1_r1.txt
-logFile
<session scratchpad>\step1_profile_r1.log
```

- **`-profileCounters`는 의도적으로 전달하지 않았다.** work counter는 timing을 교란하므로 하네스가 counter를 별도 pass로 분리해 둔다(출력 헤더 11행). 이번 질문은 타이밍 분리이므로 counter pass는 불필요하고, 켰다면 오히려 판정 대상 수치를 오염시킨다. 확인: 세 로그 모두 `profileCounters` 출현 0회, 세 출력 파일 모두 `## COUNTERS` 섹션 없음.
- `-logFile` 경로는 세션 scratchpad(휘발성)라 저장소에 없다. 위 커맨드라인이 재현 기록이다.

---

## 3. 부하 통제

프로파일 직전 호스트 전체 CPU 사용률을 읽고, **30% 초과면 폐기 후 재측정**하는 규칙으로 돌렸다.

| 런 | 직전 ambient CPU | 판정 |
|---|---|---|
| r1 | 8.7% | 채택 |
| r2 | **19.4%** | 채택 |
| r3 | 8.3% | 채택 |

r2만 r1/r3의 약 2.3배 부하에서 돌았다. 그러나 **잡 대기가 계통적으로 부풀지 않았다**:

- `FollowerJobWait`@10k — r1 2.6809 / **r2 2.6650** / r3 2.6410. r2는 최댓값이 아니라 3개 중 중간값이다.
- `FollowerJobWait`@5k — r1 0.4223 / r2 0.4224 / **r3 0.4377**. 최저 부하 런(r3, 8.3%)이 오히려 최댓값이다.

즉 이 부하 범위(≤19.4%)에서는 병렬 구간 오염이 관측되지 않는다. 대조: 다른 프로세스가 코어를 점유한 상태에서 폐기했던 이전 시도에서는 **병렬 구간 +66% vs 직렬 구간 +8~13%**로 벌어졌다(커밋 `fdf909d` 메시지에 기록). 30% 임계는 그 관측에서 왔다.

---

## 4. 판정 규칙 (측정 전 고정)

> 이 문서는 성능 **목표치·수용 임계**를 정의하지 않는다(`../SIM_OPT_HANDOFF.md` §7 "성능 목표 수치 지어내기 금지"). 아래는 T1b/T2 **우선순위 판정 규칙**일 뿐이다.

1. **T1b 대상 합(직렬)** = `FollowerPrepass` + `FollowerGridSnapshot` + `FollowerPresent` + `NeutralPrepass` + `NeutralPresent`
   — 전부 메인스레드 직렬이며 T1b가 잡으로 옮기려는 대상이다.
2. **T2 대상 합(잡 벽시계)** = `FollowerJobWait` + `NeutralJobWait`
   — 이미 병렬이며 T2(밀도 캡)가 줄이려는 대상이다.
3. 런마다 위 두 합을 구하고, 3런의 **중앙값**끼리 비교한다.
4. **런 간 spread** = (직렬 합 max − min) + (잡 대기 합 max − min) — 두 합계 각각의 최악 변동을 더한 보수적 노이즈 대역.
5. **두 합의 차이가 spread를 넘지 못하면 판정 불가(inconclusive)** → 재측정하거나 런 수를 늘린다. 차이가 spread를 크게 넘을 때만 큰 쪽을 다음 단계로 채택한다.

### 적용 결과

런별 합(ms/tick, 세그먼트 mean):

| 스케일 | 항목 | r1 | r2 | r3 | 중앙값 |
|---|---|---|---|---|---|
| 5000 | 직렬 합 | 4.3902 | 4.3492 | 4.4117 | **4.3902** |
| 5000 | 잡 대기 합 | 0.5217 | 0.5233 | 0.5378 | **0.5233** |
| 5000 | Total | 6.4318 | 6.3890 | 6.4747 | **6.4318** |
| 10000 | 직렬 합 | 14.4744 | 14.6107 | 14.4585 | **14.4744** |
| 10000 | 잡 대기 합 | 2.8001 | 2.7847 | 2.7556 | **2.7847** |
| 10000 | Total | 20.5569 | 20.6581 | 20.4962 | **20.5569** |

| 스케일 | 직렬(T1b) | 잡 대기(T2) | 차이 | spread | 차이/spread |
|---|---|---|---|---|---|
| 5000 | 4.39 (68.3%) | 0.52 (8.1%) | 3.87 | 0.079 | **49배** |
| 10000 | 14.47 (70.4%) | 2.78 (13.5%) | 11.69 | 0.197 | **59배** |

규칙 5의 inconclusive 조건에 걸리지 않는다 → **T1b 채택**. 상세 판정과 후속 계획은 [`../SIM_OPT_10K_PLAN.md`](../SIM_OPT_10K_PLAN.md) §2.

---

## 5. 판독 시 주의 (세그먼트 의미론)

원시 파일의 SEGMENTS 표는 **구간이 상호 배타가 아니다**. 아래를 지키지 않으면 수치를 잘못 읽는다.

- **`*JobWait`은 `CCMove`와 inclusive 중첩이다.** `Begin(CCMove) → Begin(*JobWait) → Complete() → End(*JobWait) → End(CCMove)` 순으로 엄격히 중첩되어 있다(`1485848`). **합산 금지, 이중 차감 금지.**
- **`CCMove`는 다섯 지점을 합산하는 단일 버킷이다** — `CrowdRoot.cs:1185`(리더 `ApplyHorizontalMove`) / `:1390`(팔로워 SDF 이동 잡 대기) / `:1479`(팔로워 CC 폴백) / `:1608`(중립 SDF 이동 잡 대기) / `:1657`(중립 CC 폴백). 따라서 `CCMove`를 특정 분기의 몫으로 간주하는 산식은 성립하지 않는다.
- **SDF ON에서 `CCMove` ≠ `CharacterController.Move`.** 실제 CC.Move 호출 수는 0이고(하네스가 단언), 이 구간은 `WallSolver.Resolve`(SDF 이동 해소)의 **병렬 벽시계**다.
- **하위 세그 합 ≠ 부모.** `FollowerSteer`의 하위 4개 합은 부모의 99.66~99.95%, `NeutralMove`의 하위 3개는 99.97~99.98%다. 잔차(부모 대비 ≤0.34%)는 `SteeringForceJob` 구조체 배선 + `Schedule`(`CrowdRoot.cs:1320-1389`)로, 어떤 하위 세그에도 속하지 않는다.
- **`FollowerJobWait`은 순수 move 잡 비용이 아니다.** `forceHandle`을 의존성으로 스케줄한 잡의 `.Complete()`를 재므로 `SteeringForceJob`의 잔여 실행분을 흡수한다.
- **`*Present`는 상한(upper bound)이다.** 헤드리스는 SMR 경로(`_gpuRenderActive == false`)라 `*Present`가 GPU 빌드에서는 게이팅되는 `transform.rotation` + `Animator.speed` 쓰기(T1a, `fdf909d`)까지 포함한다. GPU 빌드의 실제 `*Present`는 이보다 싸다. 단 `Quaternion.Euler(0f, X, 0f).eulerAngles.y`(`CrowdRoot.cs:1430`, `:1440`, `:1631`)는 그 게이트 **밖**에 있어 GPU 빌드에서도 남는다.
- **SEGMENTS 표는 mean ms/tick이고 SUMMARY의 `simtick_median_ms`와 다른 통계다.** §4의 판정은 세그먼트 mean을 런별로 합산한 뒤 런 간 중앙값을 취한 값이다.
- **`!sdfActive` CC 폴백 분기는 미계측이다.** 하네스가 SDF ON + `CcMoveFallbacks == 0`을 단언하므로 이번 측정에서 실행되지 않았다.
- `alloc_*`는 `GC.GetTotalMemory(false)` 틱 델타 프록시(하한 지표)이지 정확한 할당량이 아니다.

---

## 6. 알려진 측정 공백

- **팔로워/중립 모집단 수 미출력.** 하네스는 내부적으로 추적하지만 출력 파일에 쓰지 않는다. 그 결과 5k→10k 스케일링 비대칭(팔로워 계열 `FollowerPrepass` 5.95배 / `FollowerJobWait` 6.31배 / `FollowerPresent` 10.04배 vs 중립 계열 `NeutralPrepass` 1.06배 / `NeutralJobWait` 1.19배 / `NeutralPresent` 1.69배)의 원인을 **모집단 구성 이동으로 추론**할 뿐 측정하지 못했다. `FollowerGridSnapshot`만 2.49배로 예외지만 절대값이 0.008→0.019ms라 무시 가능하다.
- **`RenderInterpolate`는 이 측정 밖이다.** 하네스는 `SimTick`만 돌리고 `RenderInterpolate`를 호출하지 않으므로, 거기의 per-agent transform 쓰기(`CrowdRoot.cs:747`, `:755`)는 위 어떤 수치에도 포함되지 않는다.
- **GPU 경로 실측 없음.** T1a(`fdf909d`)의 이득은 설계상 이 헤드리스 프로파일에 나타나지 않는다. 별도 GPU 빌드 측정이 필요하다.

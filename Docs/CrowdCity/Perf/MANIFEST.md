# SIM_OPT 10k 측정 MANIFEST

수록 측정: **§1~§6** = step1 "직렬 vs 잡 분리"(headless `CrowdProfileHarness`) · **§7** = T1a GPU 경로 A/B(play-mode `CrowdPerfHarnessP95`). 아래 §1~§6은 전자에 대한 기록이다.

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

그 폐기 시도의 오염 부하는 **이 세션(2026-07-26)에서 실측했다** — 배경 프로세스 활동 **3.16코어**(12논리프로세서 평균 총 CPU **31.7%**)이고, 그중 MapleStory 단일 프로세스가 **1코어의 106.7%**를 점유했다. 사용자가 그 프로세스를 종료한 뒤에는 **1.28코어**(평균 **15.9%**)로 떨어졌다. 즉 "코어 점유"는 정성적 추정이 아니라 6물리코어 중 3코어분이 실제로 다른 일을 하고 있던 상태이고, 이는 잡 워커가 쓸 코어를 직접 잠식하므로 병렬 구간만 계통적으로 부푼 관측과 일치한다.

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
- **GPU 경로 실측 없음.** T1a(`fdf909d`)의 이득은 설계상 이 헤드리스 프로파일에 나타나지 않는다. 별도 GPU 빌드 측정이 필요하다. → **§7에서 메웠다**(play-mode GPU 경로 A/B).

---

## 7. T1a GPU 경로 A/B 측정 (play-mode `CrowdPerfHarnessP95`)

- 측정일: 2026-07-26
- 대상 질문: T1a(`fdf909d` — "GPU 경로의 죽은 `transform.rotation` 쓰기 제거 — 팔로워/뉴트럴 6개 호출부 게이팅")가 **GPU 경로에서 실제로 이득이 있는가**. §6 마지막 항목이 남긴 공백을 메운다.
- 원시 출력: 이 디렉터리의 `t1a_p95_before_r1.csv` / `t1a_p95_before_r2.csv` / `t1a_p95_after_r1.csv` / `t1a_p95_after_r2.csv`
- **왜 별도 측정인가:** T1a가 제거한 호출은 `_gpuRenderActive == true`일 때만 죽은 코드다. §1~§6의 헤드리스 프로파일은 SMR 경로(`_gpuRenderActive == false`)라 그 호출이 **살아 있고**, 설계상 이득이 나타날 수 없다.

### 7.1 측정 환경

| 항목 | 값 |
|---|---|
| AFTER 커밋 | `fdf909d` (T1a). 워킹트리는 `ff60d39`이고 `CrowdRoot.cs`는 `fdf909d`와 동일 |
| BEFORE 커밋 | `dcb3bfb` — `fdf909d`의 **부모**. `git diff dcb3bfb fdf909d` = `CrowdRoot.cs` 1파일, 게이트 6개 추가 + 로컬 `Human human = …` 2개 삭제뿐 |
| Unity | `6000.3.9f1` (revision `7a9955a4f2fa`, build revision 8034645) |
| 호스트 CPU | AMD Ryzen 5 5600X — 6코어 / 12논리프로세서 |
| 호스트 GPU | NVIDIA GeForce RTX 4080 (ID=0x2704), **Direct3D 11.0 [level 11.1]**, shaderLevel=50 (4개 런 로그 전부 동일) |
| OS | Windows 10 Pro 64bit (10.0.19045), 물리 메모리 32712 MB |
| 하네스 | `CrowdPerfHarnessP95` — **play-mode**, 렌더 ON, 측정 프레임마다 1×1 readback으로 GPU 동기 |
| 렌더 | 1920×1080, 카메라를 크라우드에 프레이밍, uncapped (`vSync=0`, `targetFrameRate=-1`) |
| 씬 | `Assets/@Project/Scenes/GameScene.unity`, `GameSceneController` 비활성 |
| 스케일 | `-perfCounts 10000` (agents 10004) |
| 프레임 / 워밍업 | 300 프레임 / 2.0초 — **하네스 기본값 유지** |
| sim cadence | `FixedStep = 0.02`, `MaxStepsPerFrame = 4`, 실제 `Time.deltaTime` 누산 (`CrowdPerfHarnessP95.cs:164-165`, `:459`) |
| 런 수 | **arm당 2런** (BEFORE r1/r2, AFTER r1/r2) |

`SeparationVisitBudget = 48`은 하네스가 CSV 1행 헤더에 인쇄한다(양 arm 동일).

### 7.2 커맨드라인 (verbatim)

after_r1 로그의 `COMMAND LINE ARGUMENTS` 원문이다. 나머지 3런은 `-perfOut`/`-logFile`의 파일명만 다르다.

```
C:\Program Files\Unity\Hub\Editor\6000.3.9f1\Editor\Unity.exe
-batchmode
-projectPath
D:\UNITY\UNITY_PROJECT\ActionFitPro\AF_CrowdCity
-executeMethod
CrowdPerfHarnessP95.RunFromBatch
-perfOut
D:\UNITY\UNITY_PROJECT\ActionFitPro\AF_CrowdCity\Docs\CrowdCity\Perf\t1a_p95_after_r1.csv
-perfCounts
10000
-logFile
<session scratchpad>\p95_after_r1.log
```

- **`-nographics`를 주지 않았다.** 하네스가 실제로 렌더하고 측정 프레임마다 GPU 동기를 하므로 이 부재는 필수 조건이다. 확인: 4개 로그 모두 `-nographics` 출현 0회, `graphicsDeviceType=Direct3D11` 인쇄.
- **`-quit`도 주지 않았다.** 하네스는 play mode에 진입한 뒤 스스로 `EditorApplication.Exit`로 내려간다. `-quit`을 주면 play mode 진입 전에 에디터가 종료된다. 확인: 4개 로그 모두 `-quit` 출현 0회.
- `-logFile` 경로는 세션 scratchpad(휘발성)라 저장소에 없다. 위 커맨드라인이 재현 기록이다.
- 4개 런 전부 종료 코드 0, 로그의 예외/`LogError` 0건, `[CrowdPerfP95] 모든 count 측정 완료.` 출력.

### 7.3 실행 순서: A/B/B/A

CSV `# generated` 헤더의 시각 순서는 **after_r1(02:48:20) → before_r1(02:49:36) → before_r2(02:51:45) → after_r2(02:53:30)**이다.

`AABB`/`ABAB`가 아니라 `ABBA`로 돌린 이유: 세션이 진행되며 호스트 상태가 한 방향으로 변하는 **단조 드리프트**(캐시·클럭 상태, 배경 부하 증감)가 있어도 두 arm의 평균이 시간 중심을 공유하므로 드리프트의 1차 성분이 짝지어 상쇄된다. `AABB`는 드리프트가 그대로 arm 차이로 흘러든다.

BEFORE arm 만드는 법: `git checkout dcb3bfb -- Assets/@Project/Crowd/Scripts/CrowdRoot.cs` → 측정 → `ff60d39`로 복원. 다른 파일은 건드리지 않았다.

**트리 버전을 런마다 증명했다** — 게이트 문자열 `if (!_gpuRenderActive)`의 정확 일치 개수: `ff60d39` = **7**, `dcb3bfb` = **1**. 재현: `git grep -c -F 'if (!_gpuRenderActive)' <rev> -- '*CrowdRoot.cs'`.

### 7.4 부하 통제

§3과 같은 규칙 — 런 직전 호스트 전체 CPU를 1초 간격 5회 샘플링하고 **30% 초과면 미룬다**.

| 런 | 직전 ambient CPU | 비고 |
|---|---|---|
| after_r1 | 8.9% | |
| before_r1 | **19.14%** | |
| before_r2 | 9.36% | |
| after_r2 | 13.08% | 첫 읽기가 **25.26%**여서 재확인한 뒤 이 값으로 진행 |

arm 평균 ambient는 AFTER 10.99% / BEFORE 14.25%로 **부하가 높은 쪽이 BEFORE**다. 남은 비대칭은 BEFORE를 불리하게 만드는 방향이므로 §7.6 결론(개선)의 부호를 뒤집지 못하고, **효과 크기를 얼마간 과대평가할 위험**만 남긴다. 그 위험의 크기는 이 측정으로 정량화하지 못했다.

### 7.5 검증 게이트 (4/4 통과)

아래를 전부 만족해야 채택한다. 하나라도 어기면 그 런은 다른 코드 경로를 잰 것이다.

| 조건 | 의미 | before_r1 | before_r2 | after_r1 | after_r2 |
|---|---|---|---|---|---|
| `gpuActive` = `T` | GPU 인스턴싱 경로 활성 — T1a 게이트가 발동하는 조건 자체 | T | T | T | T |
| `smr` = 0 | 살아 있는 `SkinnedMeshRenderer` 0 | 0 | 0 | 0 | 0 |
| `anim` = 0 | 살아 있는 `Animator` 0 | 0 | 0 | 0 | 0 |
| `agents` = 10004 | 모집단 동일 | 10004 | 10004 | 10004 | 10004 |

`gpuActive=T` + `anim=0`이 T1a의 전제를 확정한다: 제거된 호출이 **정말 죽은 코드**인 상태에서 쟀다.

### 7.6 판정 규칙과 결과

> 이 문서는 성능 **목표치·수용 임계**를 정의하지 않는다(`../SIM_OPT_HANDOFF.md` §7 "성능 목표 수치 지어내기 금지"). 아래는 "이 변경이 개선인가"의 판정 규칙일 뿐이다.

규칙(§4와 동형):

1. 지표별로 arm 내 2런의 **평균**을 구하고 Δ = AFTER 평균 − BEFORE 평균으로 둔다.
2. **동일-arm spread** = 두 arm의 (max − min) 중 **큰 쪽**. 같은 코드에서 같은 지표가 흔들린 폭이므로 보수적 노이즈 대역이다.
3. **|Δ|가 spread를 넘지 못하면 판정 불가(inconclusive)** → 근거로 쓰지 않는다.

결과 (단위 ms, `ticksPerFrame`만 무차원):

| 지표 | B r1 | B r2 | B 평균 | A r1 | A r2 | A 평균 | Δ | Δ% | spread | \|Δ\|/spread | 판정 |
|---|---|---|---|---|---|---|---|---|---|---|---|
| `simtick_median_ms` | 23.916 | 22.310 | **23.113** | 10.416 | 10.158 | **10.287** | **−12.826** | −55.5% | 1.606 | **7.99배** | **채택 — 헤드라인** |
| `full_median_ms` | 113.196 | 117.456 | 115.326 | 16.286 | 19.493 | 17.890 | −97.436 | −84.5% | 4.260 | 22.87배 | 채택(단 7.7-a 증폭 주의) |
| `full_p95_ms` | 150.562 | 144.579 | 147.571 | 22.825 | 30.497 | 26.661 | −120.910 | −81.9% | 7.672 | 15.76배 | 채택(동) |
| `mainthread_median_ms` | 112.913 | 117.566 | 115.240 | 16.395 | 19.432 | 17.914 | −97.326 | −84.5% | 4.653 | 20.92배 | 채택(동) |
| `mainthread_p95_ms` | 149.259 | 145.353 | 147.306 | 21.633 | 30.597 | 26.115 | −121.191 | −82.3% | 8.964 | 13.52배 | 채택(동) |
| `ticksPerFrame` | 3.997 | 3.990 | 3.9935 | 0.777 | 1.047 | 0.912 | −3.082 | −77.2% | 0.270 | 11.41배 | 채택 — 7.7-a의 캡 포화 증거 |
| `mainthread_max_ms` | 254.163 | 216.559 | 235.361 | 42.877 | 90.924 | 66.901 | −168.460 | −71.6% | 48.047 | 3.51배 | 보조 참고만 |
| `render_median_ms` | 15.744 | 27.183 | 21.464 | 5.703 | 7.455 | 6.579 | −14.884 | −69.3% | 11.439 | 1.30배 | **판정 불가** |
| `full_max_ms` | 526.876 | 393.134 | 460.005 | 366.971 | 438.740 | 402.856 | −57.149 | −12.4% | 133.742 | 0.43배 | **판정 불가** |

- **헤드라인은 `simtick_median_ms` −12.826 ms/틱 (23.113 → 10.287, −55.5%)이다.** 최대 동일-arm spread(1.606 ms, BEFORE arm)의 **7.99배**라 규칙 3에 걸리지 않는다 → **개선 확정**.
- `full_max_ms`는 규칙 3에 정면으로 걸린다(0.43배) → **근거 아님**.
- `render_median_ms`(1.30배)도 근거로 쓰지 않았다. spread를 넘기긴 했지만 arm당 2런의 range는 참 분산의 하한이라 1.3배 여유는 노이즈와 분리되지 않는다. **이건 사후 판단이며 사전 고정 임계가 아니다**(임계를 지어내지 않는다). BEFORE arm의 `render_median`이 15.744 ↔ 27.183으로 1.73배 흔들린 것 자체가 이 지표가 이 하네스에서 불안정하다는 증거다.
- `mainthread_max_ms`(3.51배)는 300프레임 중 단일 극단값이라 보조 참고로만 남긴다.

### 7.7 판독 시 주의

**(a) `full_median` −97 ms는 T1a 비용의 선형 판독이 아니다 — 캡 증폭.**
`MaxStepsPerFrame × FixedStep = 4 × 0.02s = 80 ms`인데 BEFORE의 프레임 주기가 115.326 ms로 이 캡을 넘는다. 따라서 catch-up 누산기가 매 프레임 **4틱 상한에 포화**한다 — `ticksPerFrame` BEFORE **3.9935**(≈4) vs AFTER **0.912**. 프레임당 sim 구간 비용(= `ticksPerFrame × simtick`, (c)대로 `RenderInterpolate` 1회를 포함하는 `StepSim` 전체)으로 환산하면:

- BEFORE 3.9935 × 23.113 = **92.302 ms**
- AFTER 0.912 × 10.287 = **9.382 ms**
- 차이 82.920 + `render_median` 차이 14.884 = **97.805 ms** vs 측정된 `full_median` 격차 **97.436 ms** (잔차 −0.368 ms = 격차의 0.4%).

즉 −97 ms는 "틱당 이득 × 포화된 틱 수"의 결과다. **정직한 헤드라인은 틱당 −12.83 ms**이고, −97 ms는 "BEFORE가 이미 캡 포화 상태였을 때 프레임이 얼마나 좋아졌나"로만 읽어야 한다. 모델 항에 `render_median`(판정 불가 지표)이 들어가는 것도 이 산식을 증거가 아니라 **설명**으로만 쓰는 이유다.

**(b) 기전 — 지워진 것은 transform 쓰기 하나가 아니다.**
틱당 이득 12.826 ms를 10004 에이전트로 나누면 **호출당 약 1.28 µs**다. 맨 `transform.rotation` 쓰기 하나보다 훨씬 크다. 원인은 `Human.SetHeadingAndSpeed`(`Assets/@Project/Human/Scripts/Human.cs:87`)가 회전 외에 `if (_animator != null)`도 평가한다는 점이다 — GPU 경로에서 `_animator`는 **이미 파괴된 `UnityEngine.Object`**이므로 이 비교는 C# 참조 null 체크가 아니라 native alive-check ICall이다. 10k 에이전트면 **틱당 1만 회**다. T1a가 제거한 것은 "죽은 쓰기"가 아니라 "죽은 쓰기 + 죽은 객체 생존 확인 왕복"이다.

**(c) CSV 컬럼 `simtick_median_ms`는 median도 아니고 `SimTick` 전용도 아니다.**
두 가지가 겹쳐 있다.

- **median이 아니다.** `CrowdPerfHarnessP95.cs:413`이 `totalSimMs / totalSteps`, 즉 **0.02초 스텝당 산술 평균**을 계산한다. 컬럼 이름이 실제 통계와 어긋나 있다(같은 행의 `full_median_ms`/`render_median_ms`는 실제로 `Percentile(…, 0.50)`이다 — `:409`, `:412`).
- **`SimTick` 전용이 아니다.** `totalSimMs`에 더해지는 구간(`:364-367`)은 `StepSim()` **전체**이고, `StepSim`은 fixed-step 루프의 `SimTick` 호출들 **뒤에 `RenderInterpolate(alpha)`를 프레임당 1회** 호출한다(`:472`, GPU 인스턴스 draw 발행 지점). 하네스 주석(`:363`)도 이 구간을 "fixed-step accumulator + RenderInterpolate"로 명시한다. 따라서

  `simtick = SimTick + RenderInterpolate / (프레임당 스텝 수)`

  이다. `render_median_ms`는 별개 구간(`RenderSceneAndSync`, `:371-374`)이라 (a)의 모델에서 이중 계상은 없다 — 두 구간이 프레임을 분할한다.

이 절의 모든 `simtick` 수치는 "스텝당 평균, `RenderInterpolate` 1회 분할 상환 포함"으로 읽어야 한다. 그 상환분이 arm마다 다르다는 점은 (d)에서 다룬다.

**(d) `RenderInterpolate` 분할 상환이 arm마다 다르며, 그 방향은 헤드라인을 *과소*평가한다.**
(c)에 따라 `simtick = SimTick + RI/스텝수`인데 프레임당 스텝 수가 arm마다 크게 다르다(BEFORE 3.9935 vs AFTER 0.912). `RenderInterpolate`는 T1a가 건드리지 않은 코드이므로 프레임당 비용을 양 arm 공통 `RI`로 두면:

- BEFORE: `simtick_B = SimTick_B + RI × (1/3.9935) = SimTick_B + 0.250·RI`
- AFTER: `simtick_A = SimTick_A + RI × (1/0.912) = SimTick_A + 1.096·RI`
- 따라서 `Δsimtick = ΔSimTick + 0.846·RI`, 즉 **`ΔSimTick = −12.826 − 0.846·RI`**

`RI > 0`이므로 순수 `SimTick`의 틱당 개선은 **−12.826 ms보다 더 크다**. 헤드라인 −12.826과 (b)의 호출당 1.28 µs는 이 상환 때문에 **보수적(과소) 추정**이며, 이 방향은 §7.6의 결론 부호를 위협하지 않는다.

`RI`의 절대값은 하네스가 따로 출력하지 않지만 **상한은 CSV만으로 나온다**: AFTER의 프레임당 `StepSim` 비용이 0.912 × 10.287 = 9.382 ms이고 이것이 `0.912·SimTick_A + RI`이므로 `SimTick_A ≥ 0`에서 `RI ≤ 9.382 ms`다. 대입하면

`ΔSimTick ∈ [−20.76, −12.83] ms/틱`

기록용 헤드라인은 이 구간의 **보수적 끝인 −12.83 ms/틱**을 쓴다. 구간을 좁히려면 `RenderInterpolate`를 별도 세그로 계측해야 한다 — 남은 공백이다.

**(e) 범위 — 데스크톱 Editor/Mono 수치이며 플레이어 빌드/모바일 수치가 아니다.**
Editor + Mono 스크립팅 백엔드, Ryzen 5 5600X(6C/12T) + RTX 4080, D3D11에서 쟀다. **부호는 무조건 유지된다** — 죽은 작업을 없앤 변경이라 느려질 경로가 없다. 그러나 크기는 그대로 옮겨지지 않는다:

- IL2CPP에서는 호출당 상수(1.28 µs)가 **줄어들 것**으로 본다 — managed 호출 + ICall 왕복이 Mono보다 싸다.
- 반대로 모바일에서는 프레임 예산이 더 빡빡해 **4틱 캡 포화가 더 잘 일어난다** → (a)의 증폭이 더 크게 나타날 여지가 있다.
- 두 방향 모두 이 측정으로 정량화하지 못했다. **플레이어 빌드/실기 재측정이 남은 공백이다.**

**(f) 남은 부하 비대칭.** §7.4대로 BEFORE arm의 평균 ambient CPU가 더 높다(14.25% vs 10.99%). 결론 부호에는 영향이 없으나 효과 크기를 얼마간 과대평가할 수 있고, 그 정도는 정량화되지 않았다.

**(g) §6의 `RenderInterpolate` 공백은 여기서 닫힌다(값은 아니고 범위만).** 헤드리스 프로파일은 `SimTick`만 돌려 `RenderInterpolate`의 per-agent transform 쓰기(`CrowdRoot.cs:747`, `:755`)를 아예 제외했다. 이 play-mode 측정은 프레임마다 그것을 호출하므로 `full_*`/`mainthread_*`/`simtick_*` 전부에 그 비용이 들어 있다. 다만 (c)·(d)대로 **분리해 출력되지는 않는다.**

# WORK_STATE — 진행 중 작업 상태 (저장소 전역, 콜드스타트용)

> **검증 기준 커밋: `03b57a9`** — 이 문서는 이 커밋 시점에 사실임이 확인됐다(verified true as of this commit). `git rev-parse --short HEAD`가 이 해시와 다르면 그 사이 커밋들을 읽기 전까지 이 문서를 신뢰하지 않는다 — 절차는 `CLAUDE.md`/`AGENTS.md`의 "Session start" 규칙.

> **이 문서가 답하는 질문: "지금 무엇이 진행 중이고, 무엇을 깨면 안 되는가?"** 독자는 AI다. 저장소 전역 문서이며 **영역별 절**로 나누어진다. 구조("어디에 있는가")는 `Docs/PROJECT_MAP.md`가, 설명·근거("왜 이렇게 만들었는가")는 영역별 사람용 가이드가 담당한다 — 세 문서의 경계는 `CLAUDE.md` §1.1이 정본이다(둘 다 작성됐다 — `4cd9260`이 `PROJECT_MAP.md`를, `a28aee3`이 CrowdCity 가이드 [`CROWD_GUIDE.md`](CrowdCity/CROWD_GUIDE.md)를 신설했다).
> **아래 본문은 전부 CrowdCity 영역 절이다.** 2026-07-26에 `Docs/CrowdCity/SIM_OPT_HANDOFF.md`에서 개명·이동했고(`ce53e44`), 그 패스는 이름·경로·자기참조만 고쳤다. 영역별 절 재편은 다음 패스 몫이다.

---

## 🔴 운영 계약 (2026-07-26) — **이 영역 작업 전 반드시 읽을 것**

> 이 절이 **현재 상태·불변식·게이트·함정의 정본**이다. 상태가 바뀌면 커밋이 없더라도 이 절을 갱신한다.
> 변경 **이력**(무엇을 왜 바꿨는지, 기각한 대안)은 [`CHANGELOG.md`](CrowdCity/CHANGELOG.md)에 있다. 사실은 한 곳에만 둔다 — 이력은 CHANGELOG, 계약은 이 절.

브랜치: **`feat/crowd-sim-10k`** (다른 PC에서 `git fetch && git checkout feat/crowd-sim-10k && git pull`로 재개)

### 현재 상태 / 다음 단계

계측·에디트 모드 rig 수정·T1a가 완료되고 실측까지 끝났다. 실측이 끝난 것은 셋 — ①§2 "직렬 vs 잡 대기" 분리(**SMR 경로**, 헤드리스 `CrowdProfileHarness`), ②T1a의 GPU 경로 이득(플레이 모드 `CrowdPerfHarnessP95`), ③같은 분리를 **GPU 경로에서 재측정**(edit-mode `CrowdProfileHarness`, `-nographics` 없이).

| 항목 | 커밋 | 상태 |
|---|---|---|
| 분리 계측 — 프로파일러 Seg 7개 | `1485848` | 완료, 오라클 바이트 동일 |
| 에디트 모드 rig 유령 수정 | `dcb3bfb` | 완료, 스크린샷 검증 |
| T1a — GPU 경로 죽은 `transform.rotation` 쓰기 제거 | `fdf909d` | 완료, 정확성 + 성능 실측 |
| §2 측정 증거 + 계획 정정 | `ff60d39` | 완료(문서만) |
| T1a GPU 경로 A/B 실측 증거 | `e0f81e4` | 완료(문서만) |
| GPU 경로 세그먼트 재측정 + 판정 정정 | `47cbb79` | 완료(문서만), 측정 트리 `f9c1cdc` |

**재결정은 끝났다.** `CrowdProfileHarness`를 `-nographics` 없이 3런 돌려(`_gpuRenderActive == true` 증명 3중 — `Perf/MANIFEST.md` §8.3) 같은 세그먼트를 다시 쟀다. 결과:

- **10k: 판정 불가(inconclusive).** 직렬 3.15 (32.9%) vs 잡 대기 3.01 (31.5%), 차이/spread = **0.12배**. 6런으로 늘려도 paired t = 1.82(df=5, 임계 2.571)로 유의하지 않다.
- **5k: T1b 유지.** 1.74 vs 0.54, 3.17배.
- **"그러면 T2"도 아니다.** 판정 불가는 우열을 못 가린다는 뜻이다. 게다가 이 데이터셋의 변동은 병렬 구간에 편중돼(잡 대기 변동폭이 직렬의 1.9배) **T2를 크게 보이게 하는 방향**인데도 T2가 앞서지 못했다.
- 첫 판정("T1b 확정")은 **그 데이터 위에서는 옳았다.** 다만 그 근거의 대부분이 T1a가 이미 지운 쓰기였다 — 헤드리스는 SMR 경로이므로(`CrowdRenderer.Init`이 null graphics device에서 `false` 반환 — `CrowdRenderer.cs:98`~`:102`) `*Present`가 그 쓰기를 포함했다. GPU 경로에서 그 몫이 빠지자 남은 두 합이 동률이 됐다: `*Present` 합 13.58 → **2.22 ms(−83.6%)**.
- 근거·환경·판독 주의는 `Perf/MANIFEST.md` §8, 판정 본문은 `SIM_OPT_10K_PLAN.md` §2. 여기서 반복하지 않는다.

**🔻 전제가 바뀌었다 — 다음 타깃은 10k에서 나오지 않는다.**

`SIM_OPT_10K_PLAN.md`는 **"10k가 문제다"라는 전제로 쓰인 문서**다. 그 전제가 깨졌다. **T1a 하나만으로** GPU 경로 10k가 **9.56 ms/tick**(mean; p95 11.16)이 됐고, 50Hz 고정 스텝(`GameplayRoot` `FixedStep = 0.02`)의 틱 주기 20 ms 대비 **약 48%**(p95 약 56%)다. 이 20 ms는 **루프 케이던스라는 구조적 사실**이며 성능 목표치·수용 임계가 아니다(§7).

- **다음 타깃은 20k~50k에서 무엇이 먼저 깨지는가로부터 재도출해야 한다.** 10k 세그먼트 점유율로 고르면 안 된다 — 그 지점에서 두 후보가 동률이고, 동률인 두 값의 순위는 노이즈가 정한다.
- **타깃은 아직 정하지 않았다. 열려 있다.** 필요한 것은 더 정밀한 10k 측정이 아니라 **다른 판별자**다(스케일 지수 / 워커 코어가 적은 실기기의 병렬 구간 거동 / 렌더 티어 상한). 후보 축은 `SIM_OPT_10K_PLAN.md` §2.1에 열거해 뒀고 **어느 것도 아직 측정되지 않았다.**
- **`T2는 후순위 — 상한 13.5%`라는 이전 근거는 무효다.** 그건 SMR 경로 값이고 GPU 경로에서는 잡 대기가 Total의 **31.5%**다. T2가 이겼다는 뜻이 아니라, 그 후순위 논거를 다시 쓰면 안 된다는 뜻이다.

### 깨면 안 되는 불변식

| 불변식 | 위치 | 이유 |
|---|---|---|
| `_visualYaw[…] =` 쓰기 **7곳 전부 무조건** | `CrowdRoot.cs:1205, 1430, 1440, 1526, 1536, 1631, 1673` | GPU 경로의 yaw 입력. 게이팅하면 크라우드 방향이 스폰 값에 얼어붙는다 |
| `_visualSpeed01[…] =` 쓰기 **5곳 전부 무조건** | `CrowdRoot.cs:1204, 1427, 1523, 1630, 1672` | GPU 애니메이션 위상(`_phase01`) 적분 입력 |
| 리더 `SetHeadingAndSpeed` **게이팅 금지** | `CrowdRoot.cs:1206` | S4b2 계약이 리더 트랜스폼을 라이브로 유지. 리더 ≤4명(SimTick의 0.09%)이라 이득도 없다 |
| 정지 시 yaw 유지 읽기 2곳 유지 | `CrowdRoot.cs:1439`, `:1535` | `float headingDeg = _visualYaw[index];` |
| `*JobWait`은 `CCMove`의 **inclusive 중첩** | `CrowdRoot.cs:1390`~`:1394`, `:1608`~`:1612` | 합산 금지, 이중 차감 금지 |
| 렌더 경로 게이트는 **픽셀 허용오차**, PNG MD5 아님 | 아래 게이트 절 | 스크린샷 하네스 출력이 바이트 재현되지 않는다 |

### 검증 게이트

- **오라클 바이트 동일 게이트(시뮬 변경).** `CrowdOracleHarness`, n=2000 seed=12345 1000틱, 스냅샷 md5 **`4F79282EB20A79023B45F2EB2DE5271B`**. 시뮬 결과가 불변이어야 하는 변경은 이 값이 바이트 동일해야 통과다(events/summary CSV도 함께 비교).
- **새 머신 사전 조건 — 오라클 md5가 이 머신에서 재현되는지 먼저 확인한다.** 이 머신에서 위 게이트를 한 번도 돌린 적이 없다면, **어떤 바이트 동일 결과에도 의지하기 전에** 게이트를 그대로 한 번 돌려 md5를 대조한다: `Unity.exe -batchmode -nographics -projectPath <proj> -quit -executeMethod CrowdOracleHarness.RunFromBatch -oracleOut <dir> -oracleTicks 1000 -oracleScales 2000 -oracleSeeds 12345` → 생성된 `phaseC_oracle_snapshot_n2000_s12345.bin`의 md5가 `4F79282EB20A79023B45F2EB2DE5271B`이어야 한다. 재현되지 않으면 하위의 바이트 동일 게이트 전부가 무효다 — "통과했지만 의미 없는" 점검 위에서 계속 진행하지 말고 **작업을 중단한다**.
- **렌더 게이트(렌더 경로 변경).** **차이 ≤100 픽셀 AND 최대 채널 델타 ≤8** + 육안 확인. **PNG MD5 금지** — `CrowdShotHarness` 출력은 바이트 재현되지 않는다(동일 인자 재실행에서 이미지당 921,600픽셀 중 3~16픽셀 차이, 최대 델타 ≤4, 손대지 않은 `smr_off.png` 포함). 실제 변경과의 분리도: 35,587~123,161픽셀 / 최대 델타 171~184(픽셀 수 약 2,200배, 델타 약 43배).
- **TMP 에셋 부수 효과.** 헤드리스 하네스를 돌릴 때마다 `Assets/TextMesh Pro/Resources/Fonts & Materials/LiberationSans SDF - Fallback.asset`이 더럽혀진다(약 10 insertions / 911 deletions). 커밋 전 되돌릴 것. **`git add -A` 금지** — 항상 경로를 명시한다.
- **타이밍 측정은 조용한 머신에서.** 런 직전 호스트 전체 CPU를 읽고 **약 30% 초과면 미룬다.** 포그라운드 게임이 코어 하나를 점유했을 때 **병렬 구간 +66%** vs **직렬 구간 +8~13%** 로 갈렸다 — 배경 부하는 병렬 구간에 집중된다.
- **성능 목표 수치 지어내기 금지**(아래 §7). 게이트는 "런 간 spread를 넘는 개선"으로 서술하고, spread 안의 델타는 **판정 불가(inconclusive)** 로 보고한다.
- **하네스가 config를 덮어쓴다 — 어느 수치가 출하 설정의 수치인지 먼저 가릴 것.**
  - **`CrowdPerfHarness` / `CrowdPerfHarnessP95`는 복제 config에 `SeparationVisitBudget = 48`을 무조건 강제한다**(`CrowdPerfHarness.cs:235`, `CrowdPerfHarnessP95.cs:290`). 출하 `Assets/@Project/Game/GameConfig.asset`은 **0**이다(`79db659`가 0→48, `4adb502`가 조밀 군집 떨림 때문에 48→0으로 되돌림 — 하네스의 하드코딩 48만 남았다). 0=이웃 전수 방문, 48=48번째 매칭 후보에서 스캔 중단(`SteeringForceJob.cs:100`, `:144`)이므로 **두 하네스는 조밀 구간에서 출하본보다 적게 일한다** → 그 절대 수치는 **게임이 도는 시뮬의 수치가 아니다**. 반면 **같은 하네스의 두 arm을 비교하는 A/B는 유효하다** — 양 arm이 같은 override를 공유하므로 델타는 살고, 절대값만 죽는다. 두 하네스의 `useGpuCrowdRenderer = true` 강제(`:239`, `:294`)는 **분기가 아니다** — 출하 asset이 이미 `1`이라 중복 고정일 뿐이다.
  - **`CrowdProfileHarness` / `CrowdOracleHarness`는 강제하지 않는다.** `-profileSepBudget`/`-oracleSepBudget`을 준 런에서만 덮어쓰고 기본은 config 값 그대로다(`CrowdProfileHarness.cs:78`·`:192`, `CrowdOracleHarness.cs:49`·`:193`). 두 하네스의 `UseSdfSolver = true`(`:201`, `:215`)도 프리팹 직렬화 값(`CrowdRoot.prefab` `_useSdfSolver: 1`)과 같아 분기가 아니다. 결과적으로 `Perf/MANIFEST.md` **§1~§6·§8은 출하 예산(0)으로**, **§7만 48로** 측정됐다.

### 함정

- **GPU 경로에서 팔로워/뉴트럴의 `transform.rotation`은 영구히 stale**이다(이미 stale이던 위치에 회전이 합류 — 기존 주석 `CrowdRoot.cs:740`). 앞으로 팔로워·뉴트럴의 `transform.rotation`이나 `.forward`를 읽는 기능을 추가하면 조용히 스폰 시점 값을 받는다.
- **`fdf909d`(T1a)는 `dcb3bfb`와 결합되어 있다.** 되돌릴 때는 함께 되돌려야 한다 — `dcb3bfb` 이전이면 살아남은 edit-mode 유령의 facing이 얼어붙는 실제 시각 회귀가 된다.
- **Unity는 에디트 모드에서 지연 `Object.Destroy`를 거부한다.** 모든 에디트 모드 하네스(`CrowdShotHarness`, `CrowdProfileHarness`, `CrowdOracleHarness`)가 런타임 코드는 파괴했다고 믿는 객체를 계속 본다. `GetComponentsInChildren<SkinnedMeshRenderer>(true)`는 비활성 객체까지 세므로 에디트 모드에서 `smr == 0` 단언은 여전히 실패한다. 플레이 모드 성능 하네스는 프레임마다 yield하므로 무관하다.
- **`CrowdPerfHarnessP95`의 CSV 열 `simtick_median_ms`는 중앙값도 아니고 `SimTick` 전용도 아니다.** ①`CrowdPerfHarnessP95.cs:413`이 `totalSimMs / totalSteps`(0.02초 스텝당 산술 평균)를 계산한다 — 같은 행의 `full_median`·`render_median`은 실제 `Percentile(…, 0.50)`(`:409`, `:412`). ②계측 구간(`:364-367`)이 `StepSim()` 전체를 감싸고 `StepSim`이 `RenderInterpolate(alpha)`를 프레임당 1회 호출한다(`:472`) → `simtick = SimTick + RenderInterpolate / 프레임당 스텝수`. 상환 제수가 arm마다 달라 **순수 `SimTick`으로 arm 간 비교가 안 된다**. `RenderInterpolate`는 별도 계측이 없어 순수 `SimTick`은 **범위만 잡히고 측정되지 않는다**(T1a: `ΔSimTick ∈ [−20.76, −12.83]` ms/틱). 유도는 [`CHANGELOG.md`](CrowdCity/CHANGELOG.md) §6.9.1.

---

## 🔄 이전 상태 업데이트 (2026-07-19) — 크라우드 성능 세션 결과 + 남은 작업

> 위 운영 계약 절이 현재 상태의 정본이다. 이 절과 아래 2026-07-17 마커/게이트/계획은 배경·상세 참조용으로 남겨둔다.

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

> **이 절의 모든 성능 수치는 "어떤 모드/하네스에서 잰 값인지"를 함께 적는다.** 이 프로젝트에는 같은 세그먼트의 두 데이터셋이 있고(SMR 경로 vs GPU 경로) 값이 두 배 이상 다르다. 모드를 떼면 반드시 오독된다.

- **10k SimTick (2026-07-26, edit-mode `CrowdProfileHarness`, SDF ON, seed=12345, 세그먼트 mean의 3런 중앙값):**
  - **GPU 경로**(`-nographics` 없이, `_gpuRenderActive == true`) — **9.56 ms/tick**, p95 11.16
  - **SMR 경로**(`-nographics`, `_gpuRenderActive == false`) — **20.56 ms/tick**, p95 22.67
  - 50Hz 고정 스텝의 틱 주기는 20 ms다(`GameplayRoot` `FixedStep = 0.02`) — **루프 케이던스라는 구조적 사실이며 성능 목표치·수용 임계가 아니다**(§7). GPU 경로는 그 주기의 약 48%(p95 약 56%), SMR 경로는 넘긴다.
  - 원시 출력·환경·판독 주의: `Docs/CrowdCity/Perf/MANIFEST.md` §1~§6(SMR) / §8(GPU).
- ~~10k CPU 시뮬 해결: 14.86ms/tick, 20ms(50Hz) 예산 아래.~~ **[정정 — 이 수치는 어느 모드에도 해당하지 않는다.]** `14.86ms`의 출처를 추적한 결과 **10k SimTick 측정값이 아니다.** `14.86`이 등장하는 커밋은 히스토리 전체에 3개뿐이고 가장 이른 것이 `5caae09`(2026-07-17)인데, 거기서는 프로파일러 스크린샷(`Docs/Photo/PRO.PNG`, `PRO2.PNG`) 근거로 **`GameplayRoot.Update()` self 시간 14.86 ms = 프레임의 32.4%** 를 가리킨다(그 커밋 메시지도 "프레임의 14.86ms(GameplayRoot.Update self)"라고 쓴다). 이후 `433b5fe`(2026-07-19)가 같은 숫자를 **"10k … ms/tick"** 으로 옮겨 적었고, **바로 그 커밋이 같은 절에서 실제 10k SimTick 측정값을 `22.95→14.04ms`로 따로 기록한다.** 즉 `Update self`(프레임당, `MaxStepsPerFrame` 때문에 틱 여러 개가 접힌 값)를 per-tick으로 재라벨한 것이다. **단서 하나는 확인하지 못했다** — 그 스크린샷의 실제 에이전트 수가 문서에 기록돼 있지 않아 "다른 스케일에서 잰 값"이라는 부분은 **추론**이다(같은 문서 §0은 사용자 Profiler로 `Update ~30ms @2000`을 인용한다). 확실한 것은 **집계 축이 다르다는 것**(프레임 self vs per-tick)이고, 그것만으로도 이 수치는 위 표의 어느 항목도 아니다.
- 에디터 60fps 인원 상한 ≈ 3400 = 렌더-바운드(디메이션 안 된 2964버텍스 메시 × N), 시뮬 아님 — **2026-07-19 에디터 플레이 모드 성능 하네스(GPU 강제동기)**. 하네스가 GPU 강제동기라 보수적 → 실제 빌드는 더 높을 가능성. ⚠️ 이 항목의 정확한 하네스·커밋은 기록돼 있지 않다(모드만 확인됨).
- 인원 상향 핵심 레버 = 디메이션 렌더 메시(아트) — 위 렌더-바운드 판정의 귀결이며 자체 측정값 없음.
- ~~separation 결정성 기준선: OFF/Pairwise 오라클 n=2000 s12345 md5 = 2b3aad7f786004f1185a067bc1b31f4a.~~ **[stale — Burst 이전 값]** 현재 유효한 기준선은 **`4F79282EB20A79023B45F2EB2DE5271B`**(같은 n=2000 s12345 1000틱). Burst 산출은 Mono 오라클과 bit-identical하지 않아 Stage A에서 재기준선을 잡았다(§7 "Burst 산출은 … 기존 baseline 직접 비교 금지"). 위 게이트 절 참조.

### 남은 작업 (우선순위)
1. [사용자] Development 빌드 + DevHudRoot FPS로 인원별 실 프레임 측정 → 진짜 파이프라인 60fps 인원 + 시뮬/렌더 병목 판정.
2. [아트] 디메이션 렌더 메시(~200~500 vert) — 높은 온스크린 인원의 핵심 경로, GPU 렌더러 모바일 출하 게이트.
3. [시뮬-병목 될 때까지 연기] 리더-방사형 폴리시: (a) 버킷→정확한 셀 점유수 수정(Codex: 해시충돌이 occ 부풀려 오탐 → 줄무늬), (b) T/gain/tangential 튜닝.
4. ~~[빌드 실측 후] 네이티브 위치 권위 — 틱마다 10k 트랜스폼 왕복(Restore+Mirror) 제거 = 남은 최대 직렬 시뮬 이득(결정성 재기준선 필요).~~ **[완료 — P3]** `ff27407`(S3: 팔로워/뉴트럴이 `buffer.Pos` 직접 저작) → `d124de3`(S4a: `Restore`를 `if (!sdfActive || IsLeader[i])`로 게이팅, 프리패스·wander raycast를 `PrevPos`로) → `b564fb6`(S4b1: 네이티브 `_visualYaw`) → `20cab38`(S4b2: GPU 경로 팔로워/뉴트럴 transform 쓰기 중단). SDF 경로에서 `MirrorPositionsToBuffer`는 이제 **live 리더(≤4)만** 미러링한다(`CrowdRoot.cs:1741`). 재기준선 불필요했다 — 전 단계가 오라클 `4F79282E…`에 바이트 동일(메커니즘만 변경, 시뮬 결과 불변).
5. Scope B: §4 밀스톤 Burst 골든 baseline(확장 스냅샷 필드, 수치 허용오차, 플레이어 AOT 빌드).
6. 수동 확인: DevHudRoot 버튼/FPS 동작, 청크 스폰 후 튐 없음, GPU 경로 스폰중 pop-in 허용 여부(점진표시로 수정 가능).
7. 하네스 정리(Shutdown 후 DestroyImmediate — 경미).

### 워크플로우
- 반복: 최적화 → neutralCount 상향 → 반복. 목표 = 에디터 안정 60fps. ConvertPerSecond=100은 의도된 튜닝.
- 교차검증: Codex(gpt-5.6-sol ultra) `< /dev/null` 동기 ↔ Claude, 합의까지.

---

## 🔄 다른 PC 재개 마커 (2026-07-17 업데이트)
- **기준 커밋(baseline HEAD) = `5caae09`**(브랜치 `feat/crowd-sdf-perf`) — `origin/feat/crowd-sdf-perf` 에 push 완료(이전 마커의 `fb14a41`에서 진행됨). 다른 PC에서는 `git pull` (branch `feat/crowd-sdf-perf`)로 전부 수신됨. 로컬 미커밋/stash 없음 → 유실 없음.
- **레이(`Physics.Raycast`) = 벽 판정 용도지만 layermask 버그가 있었고, 이번 세션에 수정 완료.** 두 곳뿐: `Assets/@Project/Crowd/Scripts/RivalAiDriver.cs`(`ApplyWallAvoidance`/`ProbeClearance`, 벽 회피)와 `Assets/@Project/Crowd/Scripts/CrowdRoot.cs:1099`(`RepickWanderHeading`, wander 방향 벽 판정). 용도는 둘 다 벽 판정이지만 **layermask 없이(`Physics.DefaultRaycastLayers`) 쏘고 있어 Unit(crowd) 콜라이더를 벽으로 오판하던 실제 버그였음** — 이전 마커는 레이의 *용도*만 확인하고 layermask를 보지 않아 "이미 정리됨"으로 잘못 판단했다. mask에서 Unit 레이어 제외로 이번 세션에 **FIXED**.
- **크라우드-크라우드(에이전트 간) 판정은 레이가 아니라 `SpatialGrid.QueryCircle` 경로** (separation=`CrowdRoot.cs:929`, recruit=`RecruitResolver.cs:74`, combat=`CombatResolver.cs:271/461`). → 다만 위 벽 레이 2곳은 layermask 누락으로 **crowd(Unit)를 실제로 맞고 있었음**(오판) — 이번 세션에 mask에서 Unit 제외로 **수정 완료**. 단 이 오판은 correctness 문제일 뿐 프레임 시간(Update self)의 주원인은 아래 진단대로 QueryCircle·SDF다.
- **다음 작업(사용자 의도) = Burst 컴파일러 + Job 시스템 = 계획서 §3 `M-sim-2`(=M2).** 3분할: M2-a(Native SoA) → M2-b(Burst canonical) → M2-c(IJobParallelFor).
- **단, 계획/게이트상 M2 선행 조건:** `M-sim-0` 실측(아직 미실행, 베이스라인 산출물 미커밋) → `M-sim-1`(grid 쿼리 밀도 캡핑). 사용자 의도(바로 Burst)와 계획 순서(측정 먼저)가 갈리는 지점 — 재개 시 확정 필요.
- ⚠ **메모리(로컬 `~/.claude`)는 PC 간 동기화 안 됨.** 이 문서(git 추적)가 PC 간 유일한 인수인계 소스.

### 📌 진단 갱신 (2026-07-17, 프로파일러 근거 `Docs/Photo/PRO.PNG`, `PRO2.PNG`)
- **프레임 핫스팟 = `GameplayRoot.Update()` self 14.86ms(32.4%)**, 66ms(15FPS)까지 스파이크. 실제 `PhysX.Simulate`는 1.53ms뿐 → 물리 솔버가 아니라 **크라우드 틱 내부 연산**이 원인.
- **원인:** `MaxStepsPerFrame=4`(GameplayRoot.cs:13)로 SimTick이 프레임당 ~3회 실행 × 매 틱 [4개 `SpatialGrid.QueryCircle` 이웃질의(separation=`CrowdRoot.cs:929` / recruit=`RecruitResolver.cs:74` / combat=`CombatResolver.cs:271,461`) + 에이전트별 SDF `WallSolver.Resolve`(`CrowdRoot.cs:1125`)]. 커스텀 `CrowdSimProfiler`가 Unity ProfilerMarker를 안 써서 하위 단계가 전부 `GameplayRoot.Update` self로 뭉쳐 보임. → **M-sim-1(쿼리 밀도 캡핑)·M-sim-2(Burst)가 노리는 지점.** 기존 M-sim-0 베이스라인("dominant = SDF move-solve")과 일치.
- **별개 correctness 버그 — 이번 세션 수정 완료:** 벽 감지 레이 2곳(`CrowdRoot.RepickWanderHeading` @~1099, `RivalAiDriver.ProbeClearance` @~199)이 layermask 없이 `Physics.DefaultRaycastLayers`로 쏴 Unit(crowd) 콜라이더를 벽으로 오판. `Physics.IgnoreLayerCollision(Unit,Unit)`은 raycast에 무효라 유닛 물리충돌을 꺼도 레이는 crowd를 맞음. → mask에서 Unit 레이어 제외(`Physics.DefaultRaycastLayers & ~(1<<unitLayer)`, 신규 직렬화 필드 없이 코드로 계산)로 수정. **오판 제거일 뿐 14.86ms와는 무관** (프레임 시간은 위 쿼리·SDF가 원인).

> **이 문서의 용도**: 별도 세션(대화 컨텍스트 없음)이 이 문서의 CrowdCity 절 하나로 crowd sim CPU 최적화 작업을 **바로 시작**할 수 있게 하는 진입점이다. 상세 설계는 [`SIM_OPT_PLAN.md`](CrowdCity/SIM_OPT_PLAN.md)에 있다. 이 문서는 "어떻게 부팅하고 무엇부터 하는가"만 담는다.
> **선행 조건**: 사용자의 별도 구조 리팩토링이 **완료된 뒤** 시작한다. 리팩토링은 `Crowd/Core` + `CrowdRoot`를 전부 건드리므로, 이 계획은 라인이 아니라 **책임 단위로 rebase**한다.

---

## ⚠️ 시작 전 게이트 (P0 — 통과 못 하면 착수 금지)
1. **리팩토링 완료 확인**: 이 계획은 사용자의 `Crowd/Core` + `CrowdRoot` 리팩토링 **완료 후** 시작한다. 완료 여부는 **문서로 판별 불가 → 사용자에게 명시 확인**받거나 사용자가 지정한 "완료 커밋/브랜치"로 판정한다. 착수 시 baseline 고정 기록: `git branch --show-current`, `git rev-parse HEAD`. (이 계획 작성 시점엔 리팩토링이 진행 중이었다.)
2. **정본 우선순위**: **코드 > 이 WORK_STATE/PLAN > DESIGN/INTERFACES/STATUS.** `DESIGN.md`/`INTERFACES.md`/`STATUS.md`는 **Phase C 이전 스냅샷**이라 SDF/`UseSdfSolver`/`WallField`/`OracleAgentCount`가 누락돼 현재 상태를 오도할 수 있다 — 현 상태 근거로 쓰지 말 것. 계약 확인은 **현재 코드가 유일 진실**.
3. **산출물은 tracked 커밋**: 설계 라운드 트랜스크립트(`codex_*.txt`)가 untracked로 방치된 전례가 있다(이후 `fb14a41`에 커밋 → 결론 이관 후 워킹트리에서 제거, 원본은 `git show fb14a41:<파일>`로 복구). M-sim-0 CSV·오라클 baseline·측정 manifest는 반드시 기준 브랜치에 커밋(clean/clone 시 소멸 방지).
4. **의도적 계약 변경 목록 유지**: 밀도 캡(거동), RNG 스트림(시드 재현), baked CC 제거, SDF probe 등은 의도된 변경 → 별도 목록으로 추적하고 DESIGN/INTERFACES를 그에 맞춰 갱신.

---

## 0. 한 문단 컨텍스트 (세션 독립)
AF_CrowdCity의 crowd 시뮬레이션은 확정된 CPU 병목이 `GameplayRoot.Update → CrowdRoot.SimTick`이다(사용자 Profiler 실측: Update ~30ms @2000, Ryzen 5600X 에디터). 라이브 프리팹은 `_useSdfSolver:1`(SDF ON, `CC.Move`는 폴백 전용)이고 `gpuSkinning` ON이라 **데스크톱 병목은 렌더가 아니라 sim(script)**이다. 유력 주범(코드 근거 가설, **미실측**): combat/recruit의 `SpatialGrid` 이웃 쿼리가 밀도에 초선형 + victim 정렬 O(v²). 목표 = SimTick CPU 비용 대폭↓, 수만까지 확장 가능한 sim 구조, 거동·결정성 보존. **렌더/애니/GameObject 스택은 이 작업 대상 아님**(모바일 만단위 렌더 재설계는 별도 트랙).

## 1. 먼저 읽을 것 (순서대로)
1. [`SIM_OPT_PLAN.md`](CrowdCity/SIM_OPT_PLAN.md) — **설계 of record**. Decision Log, end-state SoA 구조, 마일스톤 M-sim-0~3, 결정성 계획, 밀도 캡핑 알고리즘, 검증 방법론, 리스크/rebase 노트. 이 문서와 충돌하면 PLAN이 우선.
2. `CLAUDE.md`(repo root) — 필수 안전 규칙 + **메인/서브에이전트 운영 모델**(아래 §3).
3. `Docs/CrowdCity/DESIGN.md`, `INTERFACES.md`, `STATUS.md` — crowd 계약 **참고용**. ⚠️ 이들은 **Phase C 이전**이라 뒤처져 있다(STATUS.md는 브랜치명·"MVP 완료"까지 stale). 현재 상태 근거로 삼지 말 것 — 위 시작 게이트 §2대로 **코드가 정본**.
4. 메모리(있으면): `crowd-scaleup-architecture.md`(이 작업의 상위 결정), `phasec-ccmove-bottleneck.md`(SDF/WallSolver 배경), `agents-verify-as-unity-senior.md`, `codex-cross-verify-command.md`.

## 2. 대상 코드 (리팩토링으로 이동/개명됐을 수 있음 — 현재 트리에서 재확인)
- 커널(순수 C#): `Assets/@Project/Crowd/Core/` — `AgentBuffer`, `SpatialGrid`, `CombatResolver`, `RecruitResolver`, `WallField`, `WallSolver`, `SimTuning`.
- 오케스트레이션: `Assets/@Project/Crowd/Scripts/` — `CrowdRoot`(SimTick), `CrowdModel`, `RivalAiDriver`, `CrowdSimProfiler`.
- 하네스(Editor): `Assets/@Project/Game/Editor/` — `CrowdProfileHarness`(성능), `CrowdOracleHarness`(결정성 오라클).
- 루프: `Assets/@Project/Game/Scripts/GameplayRoot.cs`(FixedStep 0.02s=50Hz, 프레임당 최대 4스텝).
- 브랜치: **`feat/crowd-sim-10k`**(이전 `feat/crowd-sdf-perf`에서 이어짐 — 그 브랜치도 여전히 존재하나 현재 작업 대상이 아니다). 감사 추적: 설계 라운드 산출물(`codex_burst_*.txt`)은 워킹트리에 없다 — `git show fb14a41:<파일>`로 히스토리에서 복구(§8 참조).

## 3. 운영 모델 (CLAUDE.md 준수 — 반드시 지킬 것)
- **메인 에이전트 = 매니저만**. 조사/파일읽기/분석/구현/편집/테스트/diff 리뷰는 **전부 서브에이전트에 위임**. 메인은 목표·범위·성공기준 정의, 위임, 판정, 최종보고만.
- **한 파일 = 한 오너**. 두 서브에이전트가 같은 파일 동시 편집 금지. 비자명 작업은 구현자와 검증자를 다른 서브에이전트로.
- **교차검증**: 설계·검증은 **Claude 서브에이전트 + Codex**로 독립 수행 후 **합의점 도달까지** 반복(사용자 표준 방식).
  - Claude 서브에이전트: Opus 5, xhigh, "유니티 시니어 게임 프로그래머" 관점.
  - Codex(읽기전용) 호출 템플릿:
    ```
    codex exec -m gpt-5.6-sol -c model_reasoning_effort=ultra -c service_tier=fast --skip-git-repo-check --sandbox read-only "<PROMPT>" < /dev/null
    ```
    파일 쓰기 필요 시 `--sandbox workspace-write`. stdin은 `< /dev/null`로 닫아 hang 방지. 긴 프롬프트는 파일에 쓰고 `"$(cat promptfile)"`.
- 서브에이전트 보고 형식: 정확히 `Conclusion / Changed files / Verification commands and results / Risks or blockers` 네 제목만. 프로세스 로그·소스 덤프 금지.
- **새 세션의 최초 행동 = 문서/코드 조사를 서브에이전트에 위임**(메인이 직접 읽으면 매니저 전용 규칙 위반). 메인은 확인된 결론만 받아 Step 0 구성.
- **교차검증 reviewer는 항상 read-only**, 동일 HEAD SHA·frozen 작업트리·동일 요구사항/Decision Log/검증결과를 입력받는다. (Codex는 파일쓰기 필요 시에만 `workspace-write`; 설계·검증 리뷰는 read-only.)
- **합의 종료 조건 = 미해결 BLOCKER/MAJOR 0건.** reviewer 의견이 끝내 충돌하면 임의 봉합 말고 **사용자가 최종 결정**. 참조: 메모리 `autonomous-decisions-via-cross-verification`.
- **셸 주의**: 주 셸 = PowerShell(win32). codex 템플릿의 `< /dev/null` 등 POSIX 문법은 **Bash 툴**로 실행. 필수 CLI/인증/지정 모델(codex `gpt-5.6-sol`, Claude `Opus 5`)이 없으면 임의 대체 말고 **중단→사용자 확인**.
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
- 이 문서: `Docs/WORK_STATE.md`
- 구조 지도("어디에 있는가" — 영문, 포인터 전용): `Docs/PROJECT_MAP.md`
- CrowdCity 사람용 가이드("왜 이렇게 만들었는가" — 한국어, 사람 전용): `Docs/CrowdCity/CROWD_GUIDE.md`
- 상세 설계: `Docs/CrowdCity/SIM_OPT_PLAN.md`
- 규칙: `CLAUDE.md`(root)
- crowd 계약: `Docs/CrowdCity/{DESIGN,INTERFACES,STATUS}.md`
- 설계 라운드 산출물(감사): `codex_burst_prompt{,2..7}.txt` / `codex_burst_out{,2..7}.txt` + `codex_{ccmove,design,explain}_*.txt` — **워킹트리에서 제거됨**(결론은 이 문서·`SIM_OPT_PLAN.md`·`SIM_OPT_10K_PLAN.md` §5로 이관 완료). 원본은 히스토리에 남아 `git show fb14a41:<파일>`로 언제든 복구.

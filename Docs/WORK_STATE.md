# WORK_STATE — 진행 중 작업 상태 (저장소 전역, 콜드스타트용)

> **검증 기준 커밋: `33e49ab`** — 이 문서는 이 커밋 시점에 사실임이 확인됐다(verified true as of this commit). `git rev-parse --short HEAD`가 이 해시와 다르면 그 사이 커밋들을 읽기 전까지 이 문서를 신뢰하지 않는다 — 절차는 `CLAUDE.md`/`AGENTS.md`의 "Session start" 규칙.

> **이 문서가 답하는 질문: "지금 무엇이 진행 중이고, 무엇을 깨면 안 되는가?"** 독자는 AI다. 저장소 전역 문서이며 **영역별 절**로 나누어진다. 구조("어디에 있는가")는 `Docs/PROJECT_MAP.md`가, 설명·근거("왜 이렇게 만들었는가")는 영역별 사람용 가이드가 담당한다 — 세 문서의 경계는 `CLAUDE.md` §1.1이 정본이다(둘 다 작성됐다 — `4cd9260`이 `PROJECT_MAP.md`를, `a28aee3`이 CrowdCity 가이드 [`CROWD_GUIDE.md`](CrowdCity/CROWD_GUIDE.md)를 신설했다).
> **아래 본문은 전부 CrowdCity 영역 절이다.** 2026-07-26에 `Docs/CrowdCity/SIM_OPT_HANDOFF.md`에서 개명·이동했고(`ce53e44`), 그 패스는 이름·경로·자기참조만 고쳤다. 영역별 절 재편은 아직 하지 않았다(미착수).

---

## 🔴 운영 계약 (2026-07-26) — **이 영역 작업 전 반드시 읽을 것**

> 이 절이 **현재 상태·불변식·게이트·함정의 정본**이다. 상태가 바뀌면 커밋이 없더라도 이 절을 갱신한다.
> 변경 **이력**(무엇을 왜 바꿨는지, 기각한 대안)은 **git 히스토리**에 있다(`Docs/CrowdCity/CHANGELOG.md`는 삭제됐다 — CLAUDE.md §1.1 Rule 1 "이력은 네 번째 문서가 아니다". 원본은 히스토리에 남아 `git log --diff-filter=D -- Docs/CrowdCity/CHANGELOG.md`로 커밋을 찾아 복구할 수 있다). 서술이 필요한 이력은 사람용 가이드 [`CROWD_GUIDE.md`](CrowdCity/CROWD_GUIDE.md)의 산문이 담고, 측정 이력은 [`Perf/MANIFEST.md`](CrowdCity/Perf/MANIFEST.md)가 담는다. 사실은 한 곳에만 둔다 — 계약은 이 절.

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

- **10k: 판정 불가(inconclusive).** 직렬 3.15 (32.9%) vs 잡 대기 3.01 (31.5%), 차이/spread = **0.12배**. 6런으로 늘려도 paired t = 1.82(df=5, 임계 2.571)로 유의하지 않다 — 단 보조 3런(r4/r5/r6)은 **저장소에 없다**(`Perf/MANIFEST.md` §8.5의 ⚠️ 경고). 커밋된 3런만으로도 판정은 같다.
- **5k: T1b 유지.** 직렬 1.74 vs 잡 대기 0.54, **차이/spread 3.17배**(두 값의 비 3.22가 아니다 — `Perf/MANIFEST.md` §8.5).
- **"그러면 T2"도 아니다.** 판정 불가는 우열을 못 가린다는 뜻이다. 게다가 이 데이터셋의 변동은 병렬 구간에 편중돼(잡 대기 변동폭이 직렬의 1.9배) **T2를 크게 보이게 하는 방향**인데도 T2가 앞서지 못했다.
- 첫 판정("T1b 확정")은 **그 데이터 위에서는 옳았다.** 다만 그 근거의 대부분이 T1a가 이미 지운 쓰기였다 — 헤드리스는 SMR 경로이므로(`CrowdRenderer.Init`이 null graphics device에서 `false` 반환 — `CrowdRenderer.cs:98`~`:102`) `*Present`가 그 쓰기를 포함했다. GPU 경로에서 그 몫이 빠지자 남은 두 합이 동률이 됐다: `*Present` 합 13.58 → **2.22 ms(−83.6%)**.
- 근거·환경·판독 주의·판정 본문은 전부 [`Perf/MANIFEST.md`](CrowdCity/Perf/MANIFEST.md) §8(특히 §8.5 판정, §8.7 손익분기 대조, §8.8 판독 주의)에 있다. 여기서 반복하지 않는다.

**🔻 전제가 바뀌었다 — 다음 타깃은 10k에서 나오지 않는다.**

이 브랜치의 최적화 계획은 **"10k가 문제다"라는 전제 위에 세워져 있었다**(그 전제로 쓰인 `SIM_OPT_10K_PLAN.md`는 삭제됐고, 살릴 사양은 §9로 옮겼다). 그 전제가 깨졌다. **T1a 하나만으로** GPU 경로 10k가 **9.56 ms/tick**(mean; p95 11.16)이 됐고, 50Hz 고정 스텝(`GameplayRoot.FixedStepSeconds = 0.02f` — `GameplayRoot.cs:12`)의 틱 주기 20 ms 대비 **약 48%**(p95 약 56%)다. 이 20 ms는 **루프 케이던스라는 구조적 사실**이며 성능 목표치·수용 임계가 아니다(§7).

- **다음 타깃은 20k~50k에서 무엇이 먼저 깨지는가로부터 재도출해야 한다.** 10k 세그먼트 점유율로 고르면 안 된다 — 그 지점에서 두 후보가 동률이고, 동률인 두 값의 순위는 노이즈가 정한다.
- **타깃은 아직 정하지 않았다. 열려 있다.** 필요한 것은 더 정밀한 10k 측정이 아니라 **다른 판별자**다(스케일 지수 / 워커 코어가 적은 실기기의 병렬 구간 거동 / 렌더 티어 상한). **세 축 어느 것도 아직 측정되지 않았다.** 세그먼트 점유율로 고르면 안 된다.
- **`T2는 후순위 — 상한 13.5%`라는 이전 근거는 무효다.** 그건 SMR 경로 값이고 GPU 경로에서는 잡 대기가 Total의 **31.5%**다. T2가 이겼다는 뜻이 아니라, 그 후순위 논거를 다시 쓰면 안 된다는 뜻이다.

**🔻 문서 전수 감사(2026-07-26) — 이 문서 몫의 수정은 본문에 반영됐다.**

정본 3문서 + `Perf/MANIFEST.md`를 코드·git·원시 데이터와 전수 대조했다. 결과 원문은 [`Docs/Audit/2026-07-26/`](Audit/2026-07-26/) — `PROJECT_MAP` **12** · `WORK_STATE` **49** · `CROWD_GUIDE` **38** · `MANIFEST` **14**, 합계 **113건**(`WORK_STATE.audit.md`의 헤드라인은 48로 적었으나 표는 49행이다 — 표가 정본). 그 디렉터리는 임시 작업 목록이지 네 번째 문서가 아니며(`CLAUDE.md` §1.1), 수정 패스를 끝내는 커밋이 삭제한다.

**이 문서 49건 중 41건**(FALSE·UNVERIFIABLE·MODE-MISSING·IMPRECISE·OFF-BY-N·RULE4)**은 아래 본문에 반영됐다.** 죽은 좌표, 이미 랜딩된 작업을 미완으로 적은 서술, 모드 누락, 저장소 근거 없는 수치는 본문이 정정본이다. 감사 보고서는 단일 에이전트 산출물이라 신뢰 입력이 아니므로 항목마다 1차 소스(`.cs`/`.asset`/`Perf/` 원시 파일/`git`)로 재검증했고, 재검증에서 반증된 지적은 반영하지 않았다.

**남은 8건은 BOUNDARY**(다른 문서 소관 내용이 이 문서에 중복 거주하는 건들)였고, **경계 이관 패스에서 4건이 처리됐다** — §2 대상 코드 색인 → `PROJECT_MAP.md` §3·§4·§5·§14 포인터로 대체, §3 운영 모델 → `CLAUDE.md` 포인터로 대체(`CLAUDE.md`에 없는 4항목만 이관 대기로 명시 잔류), §8 파일 인덱스 → `PROJECT_MAP.md` §16 포인터로 대체, §9.1 "의미 훼손이 작다고 본 근거" → `CROWD_GUIDE.md` §8로 이동.

**나머지 4건은 의도적으로 이 문서에 남겼다**(목적지가 없거나 옮기면 사실이 사라지는 건들):
- 2026-07-19 "완료" 목록과 2026-07-17 마커 절 — 지정 목적지가 "git 히스토리"라 이관이 아니라 **삭제**이고, 두 절 모두 살아 있는 함정(Burst 22.95→14.04 "재인용 금지" 경고, 로컬 메모리 미동기 경고)을 안고 있다.
- `14.86` 커밋 계보 서술 — 지정 목적지 `CROWD_GUIDE.md` §15는 이미 그 **교훈**을 담고 있으나(오래된 문서가 확신 있게 틀린다), 여기 남은 것은 살아 있는 정정의 **증거**이고 사람용 가이드는 `CLAUDE.md` §1.1 Rule 2에 따라 AI가 사실 근거로 읽을 수 없다.
- §9.6 렌더 티어 표 — 지정 목적지 `CROWD_GUIDE.md`는 자기 §0·§16에서 수치 표와 지어낸 목표치를 금지하고 §10에서 이 판정의 소유를 명시적으로 거부한다. 옮기면 목적지 문서의 자체 계약을 깬다.

### 깨면 안 되는 불변식

| 불변식 | 위치 | 이유 |
|---|---|---|
| `_visualYaw[…] =` 쓰기 **7곳 전부 `_gpuRenderActive` 게이트 밖** | `CrowdRoot.cs:1205, 1430, 1440, 1526, 1536, 1631, 1673` | GPU 경로의 yaw 입력. 게이팅하면 크라우드 방향이 스폰 값에 얼어붙는다. 7곳은 배타 분기(`speed>0.001f` if/else 두 쌍 + `sdfActive` 분기)라 한 실행이 전부를 지나지는 않는다 — 요점은 **어느 것도 렌더 플래그 뒤에 두지 말라**는 것 |
| `_visualSpeed01[…] =` 쓰기 **5곳 전부 `_gpuRenderActive` 게이트 밖** | `CrowdRoot.cs:1204, 1427, 1523, 1630, 1672` | GPU 애니메이션 위상(`_phase01`) 적분 입력. 배타성은 `sdfActive` 분기에서만 온다(`:1427`/`:1523` 한 쌍, `:1630`/`:1672` 한 쌍) — 위 yaw 행의 `speed>0.001f` if/else와는 무관하다 |
| 리더 `SetHeadingAndSpeed` **게이팅 금지** | `CrowdRoot.cs:1206` | S4b2 계약이 리더 트랜스폼을 라이브로 유지. 리더 ≤4명(`LeaderMove` = Total의 **0.09%** — **SMR 경로 10k**. GPU 경로 10k는 0.21%)이라 이득도 없다 |
| 정지 시 yaw 유지 읽기 2곳 유지 | `CrowdRoot.cs:1439`, `:1535` | `float headingDeg = _visualYaw[index];` |
| `*JobWait`은 `CCMove`의 **inclusive 중첩** | `CrowdRoot.cs:1390`~`:1394`, `:1608`~`:1612` | 합산 금지, 이중 차감 금지 |
| 세 Burst 잡 전부 `[BurstCompile(FloatMode = FloatMode.Strict, FloatPrecision = FloatPrecision.Standard)]` | `SteeringForceJob.cs:16`, `FollowerSdfMoveJob.cs:20`, `NeutralSdfMoveJob.cs:21` | 결정성 계약. FastMath나 기본 FloatMode는 재결합·근사를 허용해 오라클 바이트 동일 게이트를 깬다(사양 서술은 §9.2) |
| 전투의 방향·예산은 **틱 시작 인원수(`_startCounts`)로만** 계산 | 스냅샷 `CombatResolver.cs:239-241`·`:261` → 소비 `:353`, `:387`, `:416`, `:547`. 쌍 열거는 고정 중첩 루프 `:336-338`, `:412-414` | **쌍 열거 순서 순열 불변성이 이 규칙에서만 나온다.** 열거 루프가 고정이라 순열을 만들 자리가 없고, 전용 테스트가 실제로 흔드는 것은 agent 삽입 순서뿐이다(`CombatResolverTests.cs:302`) → 중간에 조정된 인원수를 쓰면 **테스트는 그대로 통과하면서 이 불변성만 조용히 사라진다**(근거 서술은 `CrowdCity/CROWD_GUIDE.md` §9) |
| 렌더 경로 게이트는 **픽셀 허용오차**, PNG MD5 아님 | 아래 게이트 절 | 스크린샷 하네스 출력이 바이트 재현되지 않는다 |
| EventBus는 **이벤트 타입 2개**(`CrowdCountChangedEvent`, `CrowdEliminatedEvent`)뿐 | 페이로드 `Crowd/Contracts/Events/CrowdEvents.cs:6`, `:34` | static `EventManager` 사용의 정식 예외가 "정확히 2개 · 경계 객체만"을 영향 범위로 걸고 승인됐다. 타입을 늘리면 그 예외 근거가 무효가 된다(근거 서술은 `CrowdCity/CROWD_GUIDE.md` §11) |
| 발행자는 **`CrowdRoot` 하나**, 구독자는 **경계 객체만** | 발행 `CrowdRoot.cs:333-334` 취득 → `PublishTickEvents` 내부에서만 발행 · 구독 `GameSession.cs:61-62`, `HudRoot.cs:168-169`, `CameraRoot.cs:184-185`(+ 에디터 하네스 `CrowdOracleHarness.cs:231-232`) | 리프(`Human`·라벨·마커)를 버스에 붙이면 CLAUDE.md §13 금지 패턴. 모든 `Subscribe`는 같은 lifecycle에 `Unsubscribe` 짝이 있어야 한다 |

### 검증 게이트

- **오라클 바이트 동일 게이트(시뮬 변경).** `CrowdOracleHarness`, n=2000 seed=12345 1000틱, 스냅샷 md5 **`4F79282EB20A79023B45F2EB2DE5271B`**. 시뮬 결과가 불변이어야 하는 변경은 이 값이 바이트 동일해야 통과다(events/summary CSV도 함께 비교. `phaseC_oracle_determinism.txt`는 `DateTime.Now`를 담으므로 비교 대상이 아니다 — `CrowdOracleHarness.cs:383`). ⚠️ **이 md5는 저장소에 뒷받침 산출물이 없다** — 값이 이 문서 안에만 있고(이 줄 · 아래 신머신 절차 · "핵심 현황"의 재기준선 줄 · "남은 작업" 8번 — 열거는 예시이며 **개수를 근거로 쓰지 말 것**, `grep -in 4F79282`로 재확인한다) `.bin`이나 체크섬 파일이 `Perf/`·`Assets/` 어디에도 커밋돼 있지 않다. 게이트는 유지하되 **저장소만으로는 확립도 반증도 안 되는 값**으로 취급하고, 아래 절차를 한 번 돌려 산출물을 커밋하기 전까지 "검증된 값"으로 인용하지 않는다(남은 작업 8번).
- **새 머신 사전 조건 — 오라클 md5가 이 머신에서 재현되는지 먼저 확인한다.** 이 머신에서 위 게이트를 한 번도 돌린 적이 없다면, **어떤 바이트 동일 결과에도 의지하기 전에** 게이트를 그대로 한 번 돌려 md5를 대조한다: `Unity.exe -batchmode -nographics -projectPath <proj> -quit -executeMethod CrowdOracleHarness.RunFromBatch -oracleOut <dir> -oracleTicks 1000 -oracleScales 2000 -oracleSeeds 12345` → 생성된 `phaseC_oracle_snapshot_n2000_s12345.bin`의 md5가 `4F79282EB20A79023B45F2EB2DE5271B`이어야 한다. 재현되지 않으면 하위의 바이트 동일 게이트 전부가 무효다 — "통과했지만 의미 없는" 점검 위에서 계속 진행하지 말고 **작업을 중단한다**.
- **렌더 게이트(렌더 경로 변경).** **차이 ≤100 픽셀 AND 최대 채널 델타 ≤8** + 육안 확인. **PNG MD5 금지** — `CrowdShotHarness` 출력은 바이트 재현되지 않는다(동일 인자 재실행에서 이미지당 921,600픽셀 중 3~16픽셀 차이, 최대 델타 ≤4, 손대지 않은 `smr_off.png` 포함). 실제 변경과의 분리도: 35,587~123,161픽셀 / 최대 델타 171~184(픽셀 수 약 2,200배, 델타 약 43배). ⚠️ 이 픽셀 수치들은 **산문 기록뿐이고 원시 산출물이 커밋된 적 없다** — 임계(≤100픽셀 / 델타 ≤8)는 그대로 지키되 수치 자체는 재현 불가로 읽는다. 소스로 확인되는 것은 프레임 크기 921,600 = 1280×720(`CrowdShotHarness.cs:25-26`)뿐이다. 또 `CrowdFlatRateShotHarness`도 같은 1280×720 PNG를 쓰지만(`CrowdFlatRateShotHarness.cs:27-28`) **그쪽 노이즈 플로어는 확립된 적이 없다**.
- **TMP 에셋 부수 효과.** 헤드리스 하네스를 돌릴 때마다 `Assets/TextMesh Pro/Resources/Fonts & Materials/LiberationSans SDF - Fallback.asset`이 더럽혀진다(약 10 insertions / 911 deletions — diff 규모는 산문 기록이며 저장소로 확인되지 않는다. 부수 효과 자체와 대처는 유효하다). 커밋 전 되돌릴 것. **`git add -A` 금지** — 항상 경로를 명시한다.
- **타이밍 측정은 조용한 머신에서.** 런 직전 호스트 전체 CPU를 읽고 **약 30% 초과면 미룬다.** 포그라운드 게임이 코어 하나를 점유했을 때 **병렬 구간 +66%** vs **직렬 구간 +8~13%** 로 갈렸다 — 배경 부하는 병렬 구간에 집중된다. 그 관측은 **폐기된 미커밋 런**이고, 하네스·모드는 **플레이 모드 `CrowdPerfHarnessP95`의 GPU 경로 before/after 쌍**이다(출처 `fdf909d` 커밋 메시지 — `Perf/MANIFEST.md` §3이 30% 임계의 근거로 인용).
- **성능 목표 수치 지어내기 금지**(아래 §7). 게이트는 "런 간 spread를 넘는 개선"으로 서술하고, spread 안의 델타는 **판정 불가(inconclusive)** 로 보고한다.
- **하네스가 config를 덮어쓴다 — 어느 수치가 출하 설정의 수치인지 먼저 가릴 것.**
  - **`CrowdPerfHarness` / `CrowdPerfHarnessP95`는 복제 config에 `SeparationVisitBudget = 48`을 무조건 강제한다**(`CrowdPerfHarness.cs:235`, `CrowdPerfHarnessP95.cs:290`). 출하 `Assets/@Project/Game/GameConfig.asset`은 **0**이다(`79db659`가 0→48, `4adb502`가 조밀 군집 떨림 때문에 48→0으로 되돌림 — 하네스의 하드코딩 48만 남았다). 0=이웃 전수 방문, 48=48번째 매칭 후보에서 스캔 중단(`SteeringForceJob.cs:100`, `:144`)이므로 **두 하네스는 조밀 구간에서 출하본보다 적게 일한다** → 그 절대 수치는 **게임이 도는 시뮬의 수치가 아니다**. 반면 **같은 하네스의 두 arm을 비교하는 A/B는 유효하다** — 양 arm이 같은 override를 공유하므로 델타는 살고, 절대값만 죽는다. 두 하네스의 `useGpuCrowdRenderer = true` 강제(`:239`, `:294`)는 **분기가 아니다** — 출하 asset이 이미 `1`이라 중복 고정일 뿐이다.
  - **`CrowdProfileHarness` / `CrowdOracleHarness`는 강제하지 않는다.** `-profileSepBudget`/`-oracleSepBudget`을 준 런에서만 덮어쓰고 기본은 config 값 그대로다(`CrowdProfileHarness.cs:78`·`:192`, `CrowdOracleHarness.cs:49`·`:193`). 두 하네스의 `UseSdfSolver = true`(`:201`, `:215`)도 프리팹 직렬화 값(`CrowdRoot.prefab` `_useSdfSolver: 1`)과 같아 분기가 아니다. 결과적으로 `Perf/MANIFEST.md` **§1~§6·§8은 출하 예산(0)으로**, **§7만 48로** 측정됐다.

### 함정

- **GPU 경로에서 팔로워/뉴트럴의 `transform.rotation`은 영구히 stale**이다(이미 stale이던 위치에 회전이 합류 — 기존 주석 `CrowdRoot.cs:740`). 앞으로 팔로워·뉴트럴의 `transform.rotation`이나 `.forward`를 읽는 기능을 추가하면 조용히 스폰 시점 값을 받는다.
- **`fdf909d`(T1a)는 `dcb3bfb`와 결합되어 있다.** 되돌릴 때는 함께 되돌려야 한다 — `dcb3bfb` 이전이면 살아남은 edit-mode 유령의 facing이 얼어붙는 실제 시각 회귀가 된다.
- **Unity는 에디트 모드에서 지연 `Object.Destroy`를 거부한다.** 모든 에디트 모드 하네스(`CrowdShotHarness`, `CrowdProfileHarness`, `CrowdOracleHarness`)가 런타임 코드는 파괴했다고 믿는 객체를 계속 본다. `GetComponentsInChildren<SkinnedMeshRenderer>(true)`는 비활성 객체까지 세므로 에디트 모드에서 `smr == 0` 단언은 여전히 실패한다. 플레이 모드 성능 하네스는 프레임마다 yield하므로 무관하다.
- **`CrowdPerfHarnessP95`의 CSV 열 `simtick_median_ms`는 중앙값도 아니고 `SimTick` 전용도 아니다.** ①`CrowdPerfHarnessP95.cs:413`이 `totalSimMs / totalSteps`(0.02초 스텝당 산술 평균)를 계산한다 — 같은 행의 `full_median`·`render_median`은 실제 `Percentile(…, 0.50)`(`:409`, `:412`). ②계측 구간(`:364-367`)이 `StepSim()` 전체를 감싸고 `StepSim`이 `RenderInterpolate(alpha)`를 프레임당 1회 호출한다(`:472`) → `simtick = SimTick + RenderInterpolate / 프레임당 스텝수`. 상환 제수가 arm마다 달라 **순수 `SimTick`으로 arm 간 비교가 안 된다**. `RenderInterpolate`는 별도 계측이 없어 순수 `SimTick`은 **범위만 잡히고 측정되지 않는다**(T1a: `ΔSimTick ∈ [−20.76, −12.83]` ms/틱). 유도는 [`Perf/MANIFEST.md`](CrowdCity/Perf/MANIFEST.md) §7.7 (c)·(d).

---

## 🔄 이전 상태 업데이트 (2026-07-19) — 크라우드 성능 세션 결과 + 남은 작업

> 위 운영 계약 절이 현재 상태의 정본이다. 이 절과 아래 2026-07-17 마커/게이트/계획은 배경·상세 참조용으로 남겨둔다.

### 완료 (커밋·푸시됨)
- Burst 핫잡 활성(FloatMode.Strict): 10k SimTick 22.95→14.04ms (−38.8%) — ⚠️ **하네스·모드·원시 산출물이 전부 없다.** `433b5fe`가 처음 적었고 `Perf/` 어느 파일·`MANIFEST.md`에도 대응 데이터가 없다. 커밋된 두 데이터셋(SMR 20.56 / GPU 9.56)은 T1a 이후 코드라 직접 대조 대상이 아니고, 그래서 이 두 끝점이 어느 모드의 값인지 역추적할 방법도 없다 → **이 델타를 근거로 재인용하지 말 것**(Burst 활성 자체는 코드로 확인된다)
- GPU 크라우드 렌더러 토글 → GameConfigSO, 기본 ON (Animator/SMR 제거, 미지원 기기 자동 폴백)
- 밀도필드 separation: 추가했다가 제거(사용 불가 — 튕김)
- 설정 기본값: useGpuCrowdRenderer=ON, CombatFlatConvertRate=ON, SeparationVisitBudget=0(캡 되돌림 — 조밀군집 떨림 유발), neutralCount=3000(에디터 60fps라는 근거는 아래 "핵심 현황"에서 철회됐다), ConvertPerSecond=100
- DevHudRoot: 개발용 시작 인원 선택 + FPS 오버레이 (게이트: Debug.isDebugBuild && !batchmode) — 빌드에서 인원별 실측
- 청크(멀티프레임) 스포닝 — 고인원 스폰 프리즈 완화
- 리더-방사형 separation A/B 토글(SeparationMode, 기본 OFF=Pairwise 바이트동일) — 동작하나 줄무늬, 폴리시 필요
- 측정/스크린샷 에디터 하네스, CLAUDE.md Codex `< /dev/null` 규칙

### 핵심 현황

> **이 절의 모든 성능 수치는 "어떤 모드/하네스에서 잰 값인지"를 함께 적는다.** 이 프로젝트에는 같은 세그먼트의 두 데이터셋이 있고(SMR 경로 vs GPU 경로) 값이 두 배 이상 다르다. 모드를 떼면 반드시 오독된다.

- **10k SimTick (2026-07-26, edit-mode `CrowdProfileHarness`, SDF ON, seed=12345, 3런):**
  - **GPU 경로**(`-nographics` 없이, `_gpuRenderActive == true`) — **9.56 ms/tick**, p95 11.16
  - **SMR 경로**(`-nographics`, `_gpuRenderActive == false`) — **20.56 ms/tick**, p95 22.67
  - **두 열의 집계 축이 다르다.** ms/tick은 세그먼트 `Total` **mean**의 3런 중앙값이고, p95는 SUMMARY 열 **`simtick_p95_ms`** 의 3런 중앙값이다(세그먼트 mean이 아니다 — `Perf/MANIFEST.md` §5가 두 통계를 섞지 말라고 경고한다). p95 두 값은 MANIFEST에 없고 원시 파일에만 있다(`Perf/simopt10k_gpupath_r{1,2,3}.txt` / `Perf/simopt10k_step1_r{1,2,3}.txt`).
  - 50Hz 고정 스텝의 틱 주기는 20 ms다(`GameplayRoot.FixedStepSeconds = 0.02f` — `GameplayRoot.cs:12`) — **루프 케이던스라는 구조적 사실이며 성능 목표치·수용 임계가 아니다**(§7). GPU 경로는 그 주기의 약 48%(p95 약 56%), SMR 경로는 넘긴다.
  - 원시 출력·환경·판독 주의: `Docs/CrowdCity/Perf/MANIFEST.md` §1~§6(SMR) / §8(GPU).
- ~~10k CPU 시뮬 해결: 14.86ms/tick, 20ms(50Hz) 예산 아래.~~ **[정정 — 이 수치는 어느 모드에도 해당하지 않는다.]** `14.86ms`의 출처를 추적한 결과 **10k SimTick 측정값이 아니다.** `14.86`이 등장하는 **가장 이른** 커밋은 `5caae09`(2026-07-17)인데(등장 커밋 **개수**는 근거로 쓰지 말 것 — 측정 원시 데이터에 `5000,CCMove,0.5679,14.8674` 같은 우연 일치가 섞이고 문서를 인용하는 커밋마다 늘어난다), 거기서는 프로파일러 스크린샷(`Docs/Photo/PRO.PNG`, `PRO2.PNG`) 근거로 **`GameplayRoot.Update()` self 시간 14.86 ms = 프레임의 32.4%** 를 가리킨다(그 커밋 메시지도 "프레임의 14.86ms(GameplayRoot.Update self)"라고 쓴다). 이후 `433b5fe`(2026-07-19)가 같은 숫자를 **"10k … ms/tick"** 으로 옮겨 적었고, **바로 그 커밋이 같은 절에서 실제 10k SimTick 측정값을 `22.95→14.04ms`로 따로 기록한다.** 즉 `Update self`(프레임당, `MaxStepsPerFrame` 때문에 틱 여러 개가 접힌 값)를 per-tick으로 재라벨한 것이다. **단서 하나는 확인하지 못했다** — 그 스크린샷의 실제 에이전트 수가 문서에 기록돼 있지 않아 "다른 스케일에서 잰 값"이라는 부분은 **추론**이다(같은 문서 §0은 사용자 Profiler로 `Update ~30ms @2000`을 인용한다). 확실한 것은 **집계 축이 다르다는 것**(프레임 self vs per-tick)이고, 그것만으로도 이 수치는 위 표의 어느 항목도 아니다.
- ~~에디터 60fps 인원 상한 ≈ 3400 = 렌더-바운드(디메이션 안 된 2964버텍스 메시 × N), 시뮬 아님 — 2026-07-19 에디터 플레이 모드 성능 하네스(GPU 강제동기). 하네스가 GPU 강제동기라 보수적 → 실제 빌드는 더 높을 가능성.~~ **[철회 — 저장소 근거가 0이고, 저장소 안의 프로파일이 반박한다.]** 항목은 남겨 둔다(누구도 이 숫자를 다시 도출하지 않도록).
  - **`3400`에는 뒷받침 산출물이 하나도 없다.** 원시 출력·CSV·로그가 `Docs/CrowdCity/Perf/` 어디에도 없고, 이 숫자를 처음 적은 커밋(`433b5fe`, 2026-07-19)도 어떤 하네스로 어느 커밋에서 쟀는지 적지 않았다.
  - **`Docs/Photo/PRO3.PNG`(커밋 `4043475`, 2026-07-20 01:05)가 반박한다.** 그 커밋의 출하 설정(`neutralCount: 3000`, `useGpuCrowdRenderer: 1`, `SeparationVisitBudget: 0`) = 3004 에이전트에서 프레임 CPU **37.85 ms ≈ 26 fps**이고, `PostLateUpdate.UpdateAllRenderers`는 **0.00 ms**에 Animator·스키닝 행 자체가 없다(GPU 인스턴싱 경로). 주장된 상한보다 12% **낮은** 인원에서 26 fps가 나오므로 그 상한이 60 fps 지점일 수는 없다.
  - **"시뮬 아님"을 "시뮬 바운드"로 뒤집지도 말 것.** 비용은 `GameplayRoot.Update()`(self **19.69 ms = 52.0%**) 안에 있는데 그 구간은 시뮬과 **렌더 준비**(`RenderInterpolate`, O(N) 트랜스폼 준비, GPU 버퍼 업로드)를 함께 담는다. 측정된 것은 `PostLateUpdate.UpdateAllRenderers`가 0.00 ms라는 사실뿐이다 — 그것은 Unity 내장 `Renderer`/SMR 계층이 ≈0이라는 뜻이지 **메인스레드 렌더 비용이 ≈0이라는 뜻이 아니다.** GPU 크라우드의 버퍼 업로드(`CrowdRenderer.cs:179`·`:204`)와 draw 제출(`:188`·`:212`)은 `RenderInterpolate`(`CrowdRoot.cs:765` → `RenderGpuCrowd` → `:930`) 안, 즉 지금 논의 중인 그 19.69 ms **안**에서 돈다. 그 안에서 시뮬 대 렌더 준비의 배분은 **미측정**이다. `Update` self를 프레임당 틱 수로 나눠 per-tick 비용을 만드는 산식도 성립하지 않는다(같은 이유).
  - **하네스 편향은 부분 설명일 뿐 화해가 아니다.** 이 주장의 서술·날짜는 2026-07-19에 커밋된 perf 하네스 계열(`3bcddf1`)과 맞고 그 계열은 전부 `SeparationVisitBudget = 48`을 강제하지만(출하 0 — 위 게이트 절), 0-대-48 대응 측정이 없고 이 정도 격차를 설명하지 못한다. GPU 강제동기는 결과를 **나쁘게** 만들므로 하네스가 더 **좋은** 수치를 낸 이유는 설명하지 못한다. ⇒ **두 판독은 새 대응 측정 없이는 화해되지 않는다** — 둘 다 참으로 만들려 하지 말 것.
  - **PRO3도 정본으로 올리지 않는다.** ①3004는 화면에 인쇄된 값이 아니라 `4043475`의 `neutralCount: 3000`과 `Loading.IsObjectAvailable` 호출 **6008 = 2 × 3004**에서 **추론**한 값이다. ②GPU 열이 `--ms`(미포착)이고 `PlayerLoop`가 프레임의 82%뿐이어서 약 6.8 ms가 미계상이다 — 플레이 모드 프로파일링의 통상 원인은 `EditorLoop`지만 스크린샷 한 장으로 `Gfx.WaitForPresent*` GPU 스톨을 배제할 수는 없다. ③`4043475`는 P2(`20985dd`)·P3 시리즈(`1ca40d6`~`20cab38`)·T1a(`fdf909d`) 전체보다 앞서므로 현재 코드도 아니다.
  - **격차를 메우는 측정은 아래 "남은 작업" 1번**(Development 빌드 + DevHudRoot FPS로 인원별 실측)이다. 목표 수치는 지어내지 않는다(§7).
- ~~인원 상향 핵심 레버 = 디메이션 렌더 메시(아트) — 위 렌더-바운드 판정의 귀결이며 자체 측정값 없음.~~ **[함께 철회 — 유일한 근거였던 위 렌더-바운드 판정이 철회됐다.]** 디메이션 메시는 여전히 "남은 작업" 2번의 과제지만, **인원 상향의 핵심 레버라는 판정에는 근거가 없다**. 되살리려면 위 1번 측정이 먼저다.
- ~~separation 결정성 기준선: OFF/Pairwise 오라클 n=2000 s12345 md5 = 2b3aad7f786004f1185a067bc1b31f4a.~~ **[stale — Burst 이전 값]** 현재 유효한 기준선은 **`4F79282EB20A79023B45F2EB2DE5271B`**(같은 n=2000 s12345 1000틱). Burst 산출은 Mono 오라클과 bit-identical하지 않아 Stage A에서 재기준선을 잡았다(§7 "Burst 산출은 … 기존 baseline 직접 비교 금지"). 위 게이트 절 참조 — 그 값의 **저장소 근거 부재** 표시도 거기에 있다.

### 남은 작업 (우선순위)
1. [사용자] Development 빌드 + DevHudRoot FPS로 인원별 실 프레임 측정 → 진짜 파이프라인 60fps 인원 + 시뮬/렌더 병목 판정.
2. [아트] 디메이션 렌더 메시(~200~500 vert) — GPU 렌더러 모바일 출하 게이트. **인원 상향의 핵심 레버라는 판정은 위 철회 항목에서 철회됐다** — 되살리려면 "남은 작업" 1번 측정이 먼저다.
3. [시뮬-병목 될 때까지 연기] 리더-방사형 폴리시: (a) 버킷→정확한 셀 점유수 수정(Codex: 해시충돌이 occ 부풀려 오탐 → 줄무늬), (b) T/gain/tangential 튜닝.
4. ~~[빌드 실측 후] 네이티브 위치 권위 — 틱마다 10k 트랜스폼 왕복(Restore+Mirror) 제거 = 남은 최대 직렬 시뮬 이득(결정성 재기준선 필요).~~ **[완료 — P3]** `ff27407`(S3: 팔로워/뉴트럴이 `buffer.Pos` 직접 저작) → `d124de3`(S4a: `Restore`를 `if (!sdfActive || IsLeader[i])`로 게이팅, 프리패스·wander raycast를 `PrevPos`로) → `b564fb6`(S4b1: 네이티브 `_visualYaw`) → `20cab38`(S4b2: GPU 경로 팔로워/뉴트럴 transform 쓰기 중단). SDF 경로에서 `MirrorPositionsToBuffer`는 이제 **live 리더(≤4)만** 미러링한다(`CrowdRoot.cs:1741`). 재기준선 불필요했다 — 전 단계가 오라클 `4F79282E…`에 바이트 동일(메커니즘만 변경, 시뮬 결과 불변).
5. Scope B: §4 밀스톤 Burst 골든 baseline(확장 스냅샷 필드, 수치 허용오차, 플레이어 AOT 빌드).
6. 수동 확인: DevHudRoot 버튼/FPS 동작, 청크 스폰 후 튐 없음, GPU 경로 스폰중 pop-in 허용 여부(점진표시로 수정 가능).
7. 하네스 정리(Shutdown 후 DestroyImmediate — 경미).
8. [사용자] **오라클 md5 게이트 값의 산출물 커밋** — 게이트 절의 `4F79282EB20A79023B45F2EB2DE5271B`이 이 문서에만 있어 저장소로 확립되지 않는다. 게이트 절의 신머신 절차를 한 번 돌려 `phaseC_oracle_snapshot_n2000_s12345.bin`의 md5를 tracked 파일로 남긴다(Unity 실행이 필요해 문서 패스로는 처리할 수 없다).

### 의도적으로 보류한 MINOR (MVP 교차검증에서 제기 → 수용·보류, `c9e61a4`에서 코드 재확인)

아래는 MVP 시절 CODEX/Claude 리뷰가 제기했으나 **의도적으로 보류**한 항목이며, 전부 **현재 코드에 그대로 남아 있다**. 보류 사유가 아직 유효하므로 "미완 작업"이 아니라 "받아들인 상태"로 읽는다. 필요해지면 후속 태스크로 처리한다.

| 항목 | 현재 위치(재확인) | 보류 사유 |
|---|---|---|
| `CrowdModel` 병렬 리스트 캡슐화(read-only view 미제공) | `CrowdModel.cs:56`, `:59` — `public List<Human> Followers { get; }` / `public List<int> FollowerAgentIndices { get; }`. get-only 자동속성이라 **참조 교체는 막혀 있고**, 컬렉션 자체가 여전히 가변 `List<>`다 | 유일 mutator가 소유자(`CrowdRoot`), 외부 참조 없음. 이론적 위험 |
| `CombatResolver` 팀ID / `MatchRules` 배열 길이 방어 검증 | `CombatResolver.cs:258-262`(하한만 검사) · `MatchRules.cs:54-56`(길이 정합성 미검사) | 내부 호출자만 존재 — **도달 불가 시나리오**(CLAUDE.md "No error handling for impossible scenarios") |
| `SpatialGrid` 극단 반경 / Ground 최소 크기 가드 | `SpatialGrid.cs:190`(`radius < 0f`만 early-out, 상한 clamp 없음) · `CrowdRoot.cs:989-992`(2m shrink 후 영역 반전 미검사) | 고정 config·고정 도시라 실사용 도달 불가 |
| 에디터 위생 3건 | collider 정리 비재귀 `GameSceneSetup.cs:1012-1026` · Validator 카메라 동일성 미검사 `GameSceneValidator.cs:123` · 프리팹 자산에 `activeSelf` 검사 `GameSceneValidator.cs:417` | 에디터 전용 도구 |
| `RivalAiDriver` 경계 인지 없음 | `RivalAiDriver.cs` 전체에 region/맵 경계 항이 없다(도주/추격/중립 밀도 + 벽 raycast만) | 중립 밀도 벡터로 자기보정, 플레이상 문제 미관측 |
| `HudRoot` per-event `ToString()` 할당 | `HudRoot.cs:780`(`SetCrowdCount`) ← 버스 핸들러 `:339` | 프레젠테이션 경로, 양측 MVP 허용 |
| `EventManager` `GetInvocationList()` 에디터 경로 할당 | `EventManager.cs:196` (`#if UNITY_EDITOR` 분기) | **off-limits 정본 인프라, 편집 금지** |

**해소된 것 1건:** "불필요한 prefab 재기록"은 닫혔다 — 모든 저작 경로가 load-or-create / 변경 시에만 저장 가드를 갖는다(`GameSceneSetup.cs:1141-1144`, `:1438-1446`, `:1486-1491`, `:1567-1572`, `:1664-1669`).

### 워크플로우
- 반복: 최적화 → neutralCount 상향 → 반복. 목표 = 에디터 안정 60fps. ConvertPerSecond=100은 의도된 튜닝.
- 교차검증: Codex(gpt-5.6-sol ultra) `< /dev/null` 동기 ↔ Claude, 합의까지.

---

## 🔄 다른 PC 재개 마커 (2026-07-17 업데이트)
- **기준 커밋(baseline HEAD) = `5caae09`**(브랜치 `feat/crowd-sdf-perf`) — `origin/feat/crowd-sdf-perf` 에 push 완료(이전 마커의 `fb14a41`에서 진행됨). 다른 PC에서는 `git pull` (branch `feat/crowd-sdf-perf`)로 전부 수신됨. 로컬 미커밋/stash 없음 → 유실 없음.
- **레이(`Physics.Raycast`) = 벽 판정 용도지만 layermask 버그가 있었고, `5caae09`에서 수정 완료.** 두 곳뿐: `Assets/@Project/Crowd/Scripts/RivalAiDriver.cs:205`(`ProbeClearance` — 선언 `:200`, 호출부 `ApplyWallAvoidance`, 벽 회피)와 `Assets/@Project/Crowd/Scripts/CrowdRoot.cs:1697`(`RepickWanderHeading` — 선언 `:1685`, wander 방향 벽 판정). 용도는 둘 다 벽 판정이지만 **layermask 없이(`Physics.DefaultRaycastLayers`) 쏘고 있어 Unit(crowd) 콜라이더를 벽으로 오판하던 실제 버그였음** — 그 이전 마커는 레이의 *용도*만 확인하고 layermask를 보지 않아 "이미 정리됨"으로 잘못 판단했다. mask에서 Unit 레이어 제외로 `5caae09`에서 **FIXED**(현재 마스크 계산 `CrowdRoot.cs:240`·`:250`).
- **크라우드-크라우드(에이전트 간) 판정은 레이가 아니라 `SpatialGrid.QueryCircle` 경로.** 현재 트리의 호출부는 **4곳**(recruit=`RecruitResolver.cs:75`, combat=`CombatResolver.cs:287`·`:487`, AI 중립 밀도=`RivalAiDriver.cs:129`)이고, **separation은 더 이상 `QueryCircle` 호출이 아니다** — Burst 잡 안에서 같은 열거를 인라인 재현한다(`SteeringForceJob.cs:104-155`, 무캡 열거 주석 `:91` / 캡 절단 `:143-148`). 잡이 읽는 grid 네이티브 스냅샷은 `CrowdRoot.cs:1317`이 만든다. → 다만 위 벽 레이 2곳은 layermask 누락으로 **crowd(Unit)를 실제로 맞고 있었음**(오판) — `5caae09`에서 mask에서 Unit 제외로 **수정 완료**. 단 이 오판은 correctness 문제이고, 프레임 시간(`Update` self)의 **귀속은 미측정이다** — 그 self는 `SimTick`과 `RenderInterpolate`를 함께 담는다(핵심 현황 절의 철회 항목).
- **다음 작업(사용자 의도) = Burst 컴파일러 + Job 시스템 = 계획서 §3 `M-sim-2`(=M2).** 3분할: M2-a(Native SoA) → M2-b(Burst canonical) → M2-c(IJobParallelFor).
- **단, 계획/게이트상 M2 선행 조건이었다:** `M-sim-0` 실측 → `M-sim-1`(grid 쿼리 밀도 캡핑). **`M-sim-0`은 그 뒤 실행됐다** — 계측 인프라는 `5c05a27`, 헤드리스 5000/10000 프로파일 3런이 `Perf/simopt10k_step1_r{1,2,3}.txt`로 커밋됐다(`ff60d39`). 'M-sim-0' 라벨의 baseline CSV는 없고 산출물 라벨이 `step1_serialsplit_*`이라 이름으로 찾으면 안 보인다. Burst/잡 경로(M2)도 이미 랜딩됐다(`Crowd/Core`의 `SteeringForceJob`·`FollowerSdfMoveJob`·`NeutralSdfMoveJob`). 사용자 의도(바로 Burst)와 계획 순서(측정 먼저)가 갈리던 지점은 **둘 다 랜딩되어 해소됐다** — 이 줄은 2026-07-17 시점 서술이다.
- ⚠ **메모리(로컬 `~/.claude`)는 PC 간 동기화 안 됨.** 이 문서(git 추적)가 PC 간 유일한 인수인계 소스.

### 📌 진단 갱신 (2026-07-17, 프로파일러 근거 `Docs/Photo/PRO.PNG`, `PRO2.PNG`)
- **프레임 핫스팟 = `GameplayRoot.Update()` self 14.86ms(32.4%)**, 66ms(15FPS)까지 스파이크. 실제 `Physics.Simulate`(=`PxScene.simulate`)는 1.53ms뿐이고 `PhysX.*` 자식 행은 전부 ≤0.47ms → 배제되는 것은 **물리 솔버**뿐이다. 남은 `Update` self 안에 `SimTick`과 `RenderInterpolate`가 함께 들어 있어 그 둘 사이의 귀속은 이 스크린샷으로 갈리지 않는다(핵심 현황 절의 철회 항목).
- **후보 지점(귀속 아님):** `MaxStepsPerFrame=4`(`GameplayRoot.cs:13`)는 프레임당 틱 수의 **상한**이고 실제 실행 횟수는 기록된 적이 없다. 한 틱 안의 무거운 항은 [이웃 질의 5개 경로 — `SpatialGrid.QueryCircle` 호출부 4곳(recruit=`RecruitResolver.cs:75` / combat=`CombatResolver.cs:287`·`:487` / AI 중립 밀도=`RivalAiDriver.cs:129`) + separation은 Burst 잡 인라인 열거(`SteeringForceJob.cs:104-155`) + 에이전트별 SDF `WallSolver.Resolve`(직렬 경로 `CrowdRoot.cs:1723`, 핫패스는 Burst로 이동 — `FollowerSdfMoveJob.cs:70`·`NeutralSdfMoveJob.cs:67`)]. 커스텀 `CrowdSimProfiler`가 Unity ProfilerMarker를 안 써서 하위 단계가 전부 `GameplayRoot.Update` self로 뭉쳐 보이고, `RenderInterpolate`는 그 계측기의 대상 자체가 아니면서 같은 self에 들어간다. → **M-sim-1(쿼리 밀도 캡핑)·M-sim-2(Burst)가 노리는 지점.** **"M-sim-0가 SDF move-solve 지배를 확인했다"는 근거로 쓰지 말 것** — 그 베이스라인은 커밋된 산출물이 없고, 커밋된 가장 가까운 데이터(`Perf/simopt10k_step1_r{1,2,3}.txt`, 10k)는 직렬 `*Present` 합이 Total의 약 66%, `CCMove`가 약 13.5%다. 게다가 SDF ON에서 `CCMove`는 SDF 이동 해소의 **병렬 잡 대기 벽시계**이지 그 CPU work가 아니고, 이 파일들은 Burst 잡화 **이후** 측정이라 잡화 이전 주장을 확인도 반증도 하지 못한다.
- **별개 correctness 버그 — `5caae09`에서 수정 완료:** 벽 감지 레이 2곳(`CrowdRoot.RepickWanderHeading` = `CrowdRoot.cs:1697`, `RivalAiDriver.ProbeClearance` = `RivalAiDriver.cs:205`)이 layermask 없이 `Physics.DefaultRaycastLayers`로 쏴 Unit(crowd) 콜라이더를 벽으로 오판. `Physics.IgnoreLayerCollision(Unit,Unit)`은 raycast에 무효라 유닛 물리충돌을 꺼도 레이는 crowd를 맞음. → mask에서 Unit 레이어 제외(`Physics.DefaultRaycastLayers & ~(1<<unitLayer)`, 신규 직렬화 필드 없이 코드로 계산)로 수정. **오판 제거일 뿐 14.86ms와는 무관하다**(그 프레임 시간의 귀속 자체가 미측정이다 — 핵심 현황 절).

> **이 문서의 용도**: 별도 세션(대화 컨텍스트 없음)이 이 문서의 CrowdCity 절 하나로 crowd sim CPU 최적화 작업을 **바로 시작**할 수 있게 하는 진입점이다. 미착수 설계 사양은 **이 문서 §9**가 정본이다(별도 PLAN 문서 `SIM_OPT_PLAN.md`/`SIM_OPT_10K_PLAN.md`는 삭제됐다).
> **선행 조건**: 사용자의 별도 구조 리팩토링이 **완료된 뒤** 시작한다. 리팩토링은 `Crowd/Core` + `CrowdRoot`를 전부 건드리므로, 이 계획은 라인이 아니라 **책임 단위로 rebase**한다.

---

## ⚠️ 시작 전 게이트 (P0 — 통과 못 하면 착수 금지)
1. **리팩토링 완료 확인**: 이 계획은 사용자의 `Crowd/Core` + `CrowdRoot` 리팩토링 **완료 후** 시작한다. 완료 여부는 **문서로 판별 불가 → 사용자에게 명시 확인**받거나 사용자가 지정한 "완료 커밋/브랜치"로 판정한다. 착수 시 baseline 고정 기록: `git branch --show-current`, `git rev-parse HEAD`. (이 게이트가 처음 적힌 `fb14a41` 시점엔 리팩토링이 진행 중이었다.)
2. **정본 우선순위**: **코드 > 이 문서(WORK_STATE) > 그 외.** 계약 확인은 **현재 코드가 유일 진실**이며, 이 문서의 §9 사양이 코드와 어긋나면 코드가 옳다. (Phase C 이전 스냅샷이던 `DESIGN.md`/`INTERFACES.md`/`STATUS.md`는 삭제됐다 — 오도 위험이 실제 이유였고, 살릴 내용은 이 문서와 `PROJECT_MAP.md`·`CROWD_GUIDE.md`로 이관 완료.)
3. **산출물은 tracked 커밋**: 설계 라운드 트랜스크립트(`codex_*.txt`)가 untracked로 방치된 전례가 있다(이후 `fb14a41`에 커밋 → 결론 이관 후 워킹트리에서 제거, 원본은 `git show fb14a41:<파일>`로 복구). M-sim-0 CSV·오라클 baseline·측정 manifest는 반드시 기준 브랜치에 커밋(clean/clone 시 소멸 방지).
4. **의도적 계약 변경 목록 유지**: 밀도 캡(거동), RNG 스트림(시드 재현), baked CC 제거, SDF probe 등은 의도된 변경 → 별도 목록으로 추적하고 DESIGN/INTERFACES를 그에 맞춰 갱신.

---

## 0. 한 문단 컨텍스트 (세션 독립)
AF_CrowdCity의 CPU 프레임 핫스팟은 `GameplayRoot.Update`다(사용자 Profiler 실측: Update self ~30ms @2000, Ryzen 5600X 에디터). 라이브 프리팹은 `_useSdfSolver:1`(SDF ON, `CC.Move`는 폴백 전용)이고 `gpuSkinning` ON이다. **다만 그 self 안에서 sim 대 렌더 준비의 배분은 미측정이다** — 같은 함수가 `CrowdRoot.SimTick`(프레임당 여러 틱)과 `RenderInterpolate`(O(N) 렌더 위치 준비 + GPU 경로에서는 버퍼 업로드·draw 제출까지)를 함께 담는다. "렌더가 아니라 sim"도 그 반대도 이 데이터로는 말할 수 없다(핵심 현황 절의 철회 항목 참조). 유력 주범(코드 근거 가설, **미실측**): combat/recruit의 `SpatialGrid` 이웃 쿼리가 밀도에 초선형 + victim 정렬 O(v²). 목표 = SimTick CPU 비용 대폭↓, 수만까지 확장 가능한 sim 구조, 거동·결정성 보존. **렌더/애니/GameObject 스택은 이 작업 대상 아님**(모바일 만단위 렌더 재설계는 별도 트랙).

## 1. 먼저 읽을 것 (순서대로)
1. **이 문서 §9(미착수 설계 사양)** — 밀도 캡핑 알고리즘, canonical order, `DeterministicRng`, T2/T3a/T3b, 모바일 하한 조건. 아래 §5 Step 2~4가 이 사양을 참조한다.
2. `CLAUDE.md`(repo root) — 필수 안전 규칙 + **메인/서브에이전트 운영 모델**(아래 §3).
3. `Docs/PROJECT_MAP.md` — 구조 색인("어디에 있는가"). 커널 타입·진입점·프리팹 로딩·이벤트 버스 좌표가 여기 있다.
4. 메모리(있으면): `crowd-scaleup-architecture.md`(이 작업의 상위 결정), `phasec-ccmove-bottleneck.md`(SDF/WallSolver 배경), `agents-verify-as-unity-senior.md`, `codex-cross-verify-command.md`.

## 2. 대상 코드 (리팩토링으로 이동/개명됐을 수 있음 — 현재 트리에서 재확인)
- **커널·오케스트레이션·하네스·고정 스텝 루프의 파일·타입·진입점 좌표는 `Docs/PROJECT_MAP.md`가 정본이다** — 커널(`Crowd/Core`) §4, Crowd 오케스트레이션(`Crowd/Scripts`) §5, 검증 하네스(`Game/Editor`) §14, 루프와 케이던스(`GameplayRoot`) §3. 여기서 같은 색인을 두 번 두지 않는다(구조는 이 문서의 소관이 아니다 — `CLAUDE.md` §1.1 Rule 1).
- 브랜치: **`feat/crowd-sim-10k`**(이전 `feat/crowd-sdf-perf`에서 이어짐 — 그 브랜치도 여전히 존재하나 작업은 `feat/crowd-sim-10k`에서만 한다). 감사 추적: 설계 라운드 산출물(`codex_burst_*.txt`)은 워킹트리에 없다 — `git show fb14a41:<파일>`로 히스토리에서 복구(§8 참조).

## 3. 운영 모델

- **정본은 `CLAUDE.md` / `AGENTS.md`다.** 메인=매니저 전용 위임 모델, 서브에이전트 보고 4제목 형식, 한 파일=한 오너, Codex↔Claude 교차검증 절차와 합의 요구, 두 에이전트의 모델·effort·`codex exec` 기본 호출형(`< /dev/null` 포함) — 전부 그 파일의 "Main/Subagent Operating Model" · "Cross-Verification" · "Agent settings" 절에 있다. 워크플로우 계약은 이 문서가 소유하지 않는다(`CLAUDE.md` §1.1 Rule 1·Rule 3). 여기서 재기술하지 않는다.
- **아래 4건은 `CLAUDE.md`에 아직 없는 항목이다** — `CLAUDE.md`/`AGENTS.md`는 이 문서의 소관이 아니므로 그 미러 쌍의 오너가 같은 커밋에서 이관해야 한다. 이관될 때까지는 여기가 유일한 기록이다.
  - Codex 호출 플래그: 설계·검증 리뷰는 `--skip-git-repo-check --sandbox read-only`, 파일 쓰기가 필요할 때만 `--sandbox workspace-write`. 긴 프롬프트는 파일에 써서 `"$(cat promptfile)"`로 넘긴다.
  - **교차검증 reviewer는 항상 read-only**이고, 동일 HEAD SHA·frozen 작업트리·동일 요구사항/Decision Log/검증결과를 입력으로 받는다.
  - **합의 종료 조건 = 미해결 BLOCKER/MAJOR 0건.** reviewer 의견이 끝내 충돌하면 임의 봉합하지 말 것(사용자 부재 시의 처리는 `CLAUDE.md`의 자율 결정 규칙).
  - **셸**: 주 셸 = PowerShell(win32)이므로 `< /dev/null` 등 POSIX 문법은 **Bash 툴**로 실행한다. 필수 CLI·인증·지정 모델이 없으면 임의 대체 말고 **중단→사용자 확인**.
- **성능 목표 수치를 지금 지어내지 말 것** — M-sim-0 실측 + 디바이스 budget 후 확정. 현재 문서의 모든 수치는 외삽.

---

## 4. 리팩토링 세션에게 (rebase가 매끄럽도록 남겨줄 것)
리팩토링 중 아래를 **깨지 않거나, 바뀌면 명확히 기록**해 두면 sim-opt rebase가 쉬워진다:
1. **SimTick 단계 순서 계약** 유지: **`PrevPos` 스냅샷** → restore → heading → leader move → follower/neutral steer → mirror → grid rebuild → recruit → combat → commit → publish. (①`PrevPos` 복사(`CrowdRoot.cs:680`)는 번호 없는 선행 단계이며 restore(`:690`)보다 **앞**이다 — 이동 단계가 `buffer.Pos`를 직접 저작하므로 "직전 tick 위치"를 읽는 코드(라이벌 AI, wander raycast 원점, 잡의 자기 위치)가 이 스냅샷을 봐야 오염되지 않는다. ②병렬화의 "이웃은 직전 tick 스냅샷" 전제는 mirror/rebuild가 steer 뒤라는 순서에 의존.)
2. **AgentId 고유성 + population slot 안정성**(삽입순 오름차순 index 불변) 유지 — 결정성 canonical order의 근간.
3. **`SpatialGrid.QueryCircle` 경계**(누가 호출하는지: combat per non-neutral + per leader `CombatResolver.cs:287`·`:487`, recruit per neutral `RecruitResolver.cs:75`, AI 중립 밀도 `RivalAiDriver.cs:129`. **separation은 호출자가 아니다** — `SteeringForceJob.cs:104-155`가 같은 열거를 잡 안에서 인라인 재현한다)를 명확히 — 밀도 캡핑이 이 지점을 대체한다. 시그니처가 바뀌면 기록.
4. **`CrowdSimProfiler`의 Seg 열거** 유지(하네스가 의존). 이름/의미 바뀌면 기록.
5. **오라클 스냅샷 계약**(CrowdOracleHarness가 읽는 위치/team/event) 유지 — 재기준선 비교의 기준.
6. `AgentBuffer`가 여전히 SoA인지 / 이미 NativeArray로 갔는지 결과 상태를 이 문서에 한 줄 기록(M-sim-2 스코프가 달라짐). — **해소됨**: `Docs/PROJECT_MAP.md` §4가 `AgentBuffer`를 "SoA, NativeArray"로 기록하고 있다.
7. 모든 `System.Random`, `Physics.Raycast`, `Physics.CheckSphere`, `CharacterController` **최종 위치**를 기록 — M-sim-3 대상.

## 5. 실행 세션에게 (리팩토링 후 — 바로 이 순서로)

### Step 0 — rebase 재매핑 + coverage (구현 전 필수)
> 여기서 "rebase"는 설계를 **현재 책임 구조에 의미적으로 재매핑**한다는 뜻이며, `git rebase`/checkout/브랜치 변경 권한이 아니다.

서브에이전트에 위임하여 현재 트리에서 산출:
- **책임 매핑표**: (tick driver / sim owner·권위 상태 / buffer·grid·query / profiler·harness / config source / SDF·CC / prefab·setup·validator / asmdef·package) 각각 `기존 책임 → 현재 owner·file·type·API·lifecycle`. 기존 이름(`CrowdRoot`,`_useSdfSolver`,`OracleAgentCount`,`RunFromBatch`,`QueryCircle`,`SpatialGrid`,`SimTuning`)이 유지/개명/이동됐는지 확정. **리팩토링이 프리팹 직렬화+Init 주입으로 갔다면 M-sim-0의 `AddComponent<CrowdRoot>` 하네스 방식 자체가 무효일 수 있으니 반드시 확인.** — **해소됨:** 하네스는 라이브 프리팹을 로드한다(`CrowdProfileHarness.cs:157`).
- **milestone coverage**: M-sim-0~3 각각을 `미구현/부분/완료/설계충돌`로 판정 — 리팩토링이 일부를 이미 흡수했으면 **실제 delta만** 구현.
- §4 핸드셰이크 항목이 이 문서·`PROJECT_MAP.md`에 기록돼 있지 않으면 코드에서 전수 도출.
- §9 사양이 현재 코드와 어긋나면 그 부분만 교차검증(Claude+Codex)으로 재합의 후 진행.

### Step 1 — M-sim-0: 실측 (첫 관문, 코드변경 최소)
**이 단계는 이미 실행됐다 — 아래 위임 4항목은 `5c05a27`에 랜딩됐고, 헤드리스 5000/10000 프로파일 3런이 커밋돼 있다(`ff60d39` → `Perf/simopt10k_step1_r{1,2,3}.txt`). 재실행하지 말고 이력으로 읽을 것.** 남은 공백은 **2000 스케일 런이 커밋돼 있지 않다**는 것뿐이다(커밋된 파일의 SUMMARY 행은 5000/10000 둘).
- **위임 4항목 — 전부 완료(좌표는 현재 트리):** ① scale 하드코딩 → CLI 인자화 = `-profileScales`(`CrowdProfileHarness.cs:58`). ② `agents`가 중립을 빼던 버그 → 실제 `OracleAgentCount` 기록(`:216`). ③ work counter(grid entries, separation/recruit/combat visits, exact-radius qualifying, touching/unique pair, victim/comparison) → `Crowd/Core/CrowdSimCounters.cs` + 하네스 출력(예: `victim_comparisons_per_victim` `:439`), timing과 분리된 counter 패스. ④ SDF ON 보장 → **라이브 프리팹 로드**(`:157` `ResourceLoader.LoadPrefab<CrowdRoot>()`) + `UseSdfSolver = true`(`:201`) + `IsSdfActive` hard-fail(`:205-210`) + `CC.Move` 폴백 0 hard-fail(`:231-237`).
- **`AddComponent<CrowdRoot>`는 트리에 존재하지 않는다.** 두 하네스의 경고 주석으로만 남아 있다(`CrowdProfileHarness.cs:156`, `CrowdOracleHarness.cs:209`) — "현 하네스는 `AddComponent`라 SDF ON을 우회한다"는 옛 서술을 근거로 커밋된 baseline을 불신하지 말 것.
- 실행: `-batchmode -nographics -executeMethod CrowdProfileHarness.RunFromBatch ...`(에디터 GUI 불필요). 동일 seed/tick 독립 ≥3회로 **A/A 노이즈 밴드** 확립.
- **구동 수단**: win32 헤드리스 배치 = `Unity.exe -batchmode -nographics -projectPath <proj> -quit -executeMethod CrowdProfileHarness.RunFromBatch -profileOut <out> -profileLabel <label>`. Unity 에디터 실행 파일 경로(또는 unity-cli 커넥터)와 정확한 Unity 버전은 **사용자에게 확인**.
- **M0 위생**: 계측·하네스 외 게임 규칙/쿼리 알고리즘/tuning 값/production scene·prefab/오라클 baseline을 **바꾸지 말 것**(순수 측정).
- **counter run ≠ timing run**: work counter가 timing을 교란하므로 성능 게이트에는 **counter-off** 결과를 쓰고, counter는 별도 run에서 집계.
- 산출 baseline CSV/오라클/manifest는 **tracked 커밋**(시작 게이트 §3).
- **게이트/산출**: median/p95 + work counter가 **주범 sub-stage(grid/recruit/combat/steering)를 일관 지목**. 가설("combat/recruit 밀도 초선형")을 실측으로 확정/반증. 순위가 실행마다 뒤집히면 marker 세분화 후 재측정(M-sim-1 진행 금지). **여기서 나온 실측으로 이후 성능 게이트 목표치를 확정**.

### Step 2 — M-sim-1: 밀도 캡핑 (초선형 항 근본 제거)
- **§9.1 알고리즘대로**: grid는 **전 agent 저장(cap 없음) + query 방문량만 lane별 budget cap**. victim O(v²) 정렬 제거. 캡 선택은 `(distSq, AgentId)` 총순서 top-k. 캡 파라미터는 `SimTuning` SO(불변 소비), 값은 M-sim-0 histogram 후 확정.
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
- **가설 반증 시 정지**: M-sim-0가 combat/recruit를 일관된 주범으로 확정하지 못하면 M-sim-1 시작 금지 → §9.1 재검토·재승인.
- **용어 구분**: M-sim-0의 "CC.Move 0" = **호출 횟수 0**, M-sim-3의 "CC 0" = **prefab/scene/runtime 컴포넌트 수 0**.
- **"렌더 스택 불변"의 정확한 범위**: 메시/Animator/스키닝/머티리얼 등 **시각 표현은 불변**. 단 M2의 Transform mirror 제거·M3의 baked CC 제거는 예외(위치 권위가 Native로 이동, CC는 이동에 미사용) — 시각 결과는 동일해야 함.
- M-sim-2를 한 커밋에 몰아서 하지 말 것(3분할 — divergence 원인 분리 불가).
- 밀도 캡을 순수 성능 리팩토링과 같은 커밋에 섞지 말 것(게임플레이 변경).
- 렌더/애니/GameObject/Human 프리팹 재설계는 이 작업 범위 밖(모바일 트랙).
- Burst 산출은 Mono 오라클과 bit-identical 아님 → 재기준선 전제(기존 baseline 직접 비교 금지).
- 새 asmdef·새 Bus·새 전역 registry 만들지 말 것(CLAUDE.md §11, §13).

## 8. 파일 인덱스

**추적 중인 문서 목록의 정본은 `Docs/PROJECT_MAP.md` §16이다** — 어떤 문서가 무엇을 담당하는지, 그리고 그 표가 exhaustive라는 선언까지 거기 있다. 여기서 같은 색인을 두 번 두지 않는다. **그 표에 없는 `Docs/` 하위 문서를 인용하는 서술을 만나면 그 서술이 낡은 것이다.** 다만 이 문서가 직접 인용하는 캡처의 용도만 여기 적어 둔다 — `Docs/Photo/PRO.PNG`·`PRO2.PNG`는 2026-07-17 진단 근거, `PRO3.PNG`는 위 "핵심 현황"의 철회 근거다.

**삭제된 문서(히스토리에서 복구 가능 — `git log --diff-filter=D -- <경로>`):**

| 삭제된 문서 | 살릴 내용이 간 곳 |
|---|---|
| `Docs/CrowdCity/SIM_OPT_PLAN.md` | 밀도 캡 9단계 사양·canonical order 표·`DeterministicRng` → 이 문서 **§9.1~§9.3** |
| `Docs/CrowdCity/SIM_OPT_10K_PLAN.md` | T2 정책 수정·T3a/T3b·모바일 1만 하한 → 이 문서 **§9.4~§9.6** |
| `Docs/CrowdCity/CHANGELOG.md` | 이력은 git. 측정 유도는 `Perf/MANIFEST.md` §7.7·§8.7, 서술은 `CROWD_GUIDE.md` |
| `Docs/CrowdCity/DESIGN.md` | 근거 4건 → `CROWD_GUIDE.md` §11·§17(이미 존재). 나머지는 대부분 거짓이라 폐기 |
| `Docs/CrowdCity/INTERFACES.md` | 고유 내용 없음(모든 시그니처가 드리프트). 현행 좌표는 `PROJECT_MAP.md` |
| `Docs/CrowdCity/STATUS.md` | 소유권 체인·커널 목록·버스 계약 → `PROJECT_MAP.md` §2·§4·§10 + 이 문서 불변식 표. §5 MINOR triage → 이 문서 §"의도적으로 보류한 MINOR" |
| `Docs/CrowdCity/PREFAB_CONSTRUCTION_PLAN.md` | §6 CC 3항목 → `CROWD_GUIDE.md` §4(정정 포함, 이미 존재) |
| `Docs/CrowdCity/GameLogicExplainer.html` | 가이드에 없던 5건 → `CROWD_GUIDE.md` §1·§2·§5·§7. 알려진 오류 4건은 폐기 |
| `Docs/CrowdCity/Sessions/2026-07-15_first.md` | MVP 세션 노트. 승계할 사실 없음 |
| `UnityArchitectureGuide/AI_WORKING_RULES.md` | `CLAUDE.md`의 진부분집합이었고, 자기를 root `AGENTS.md`로 복사하라는 지시(line 5)가 미러 계약을 깨는 위험이었다 |
| `phaseC_stage1_validator.txt` (root) | `WallFieldValidator`가 매 실행 재생성하고 읽는 곳이 없다. 해시 정본은 `City/Generated/WallSdf.asset`. `.gitignore`에 추가 |

- 설계 라운드 산출물(감사): `codex_burst_prompt{,2..7}.txt` / `codex_burst_out{,2..7}.txt` + `codex_{ccmove,design,explain}_*.txt` — **워킹트리에서 제거됨**(결론은 이 문서 §9로 이관 완료). 원본은 히스토리에 남아 `git show fb14a41:<파일>`로 언제든 복구.

---

## 9. 미착수 설계 사양 (정본)

> **이 절이 아직 구현되지 않은 설계 사양의 정본이다.** 각 항목은 착수 시점에 코드와 대조하고 시작한다 — 사양이 코드보다 뒤처졌으면 사양이 아니라 코드가 옳다(§P0 게이트 2 "정본 우선순위").
> 각 항목 머리에 **현재 트리 상태**를 적어 뒀다. "미구현"은 `c9e61a4` 시점에 확인된 값이다.

### 9.1 밀도 캡 — full storage + bounded lane query (9단계 사양)

**현재 트리 상태: 부분 구현. 착수 지점은 `SpatialGrid.QueryCircleCapped`가 아니다.** 그 메서드(`SpatialGrid.cs:274`)는 **프로덕션 호출자가 0**이다 — 유일한 호출자가 `Crowd/Tests/Editor/DensityCapTests.cs`다. 실제로 도는 캡은 팔로워 분리 Burst 잡 안의 **중복 인라인 사본**이고(`SteeringForceJob.cs:100` `bool capped = SepBudget > 0;`, 열거 `:104-155`, 절단 `:144-148`), 그 잡은 팔로워 조향 경로에서 매 tick 스케줄된다(`CrowdRoot.cs:1357`). **라이브 경로를 움직이는 것은 잡 안의 인라인 사본뿐이다** — `SpatialGrid.QueryCircleCapped`만 고치면 런타임은 그대로다(프로덕션 호출자 0). 반대로 인라인 사본만 고쳐도 라이브 분리 경로는 실제로 바뀌므로 **결정성 재기준선이 필요하다.** 두 사본은 `DensityCapTests`의 등가 검증이 의미를 유지하도록 함께 고친다.

두 사본 모두 **단순 방문수 절단**만 한다 — budget번째 매칭 후보까지 처리하고 그다음을 방문하지 않는다. 아래 사양의 lane 분리·라운드로빈·해시 회전·max-heap은 없다. 단 ⑦(필터 이전 예산 소비)는 **둘 다 이미 충족한다** — 예산 소비가 radius 테스트보다 앞서고(`SpatialGrid.cs:322` vs `:326`, `SteeringForceJob.cs:113-116` vs `:120`) 잡 쪽은 self/team 테스트(`:124`)보다도 앞선다. 출하 `GameConfig.asset`의 `SeparationVisitBudget`은 **0(비활성)** 이다(`79db659`가 0→48, `4adb502`가 조밀 군집 떨림 때문에 48→0으로 되돌림) → 라이브 빌드에서는 잡 사본의 캡도 발화하지 않는다.

**저장 상한은 두지 않는다.** 셀에 담기는 agent를 자르면 exact leader/AI 질의와 오라클이 깨지고 **영구 누락**이 생긴다. 자르는 것은 **저장이 아니라 질의 방문량**이다.

각 bounded query는 다음 9단계를 순서대로 밟는다.

1. broad radius의 cell offset stencil을 **초기화 시** 계산해 둔다.
2. cell AABB 최소거리 + cell key로 **canonical cell 순서**를 정한다.
3. 필요한 team/neutral **lane만** 방문한다(`0=neutral`, `1+teamId=team`).
4. lane별 **독립 visit budget `B`**.
5. active cell range를 **round-robin**으로 돈다(한 dense cell이 예산을 독점하지 못하게).
6. cell range 시작점을 `(queryId, cell, lane, tick)` **해시로 회전**시킨다(기아 완화).
7. self/team/radius 필터 **이전에** budget을 소비한다(strict bound — 필터 후 소비하면 상한이 성립하지 않는다).
8. radius를 통과한 것만 고정 크기 **max-heap `(distSq, AgentId)`** 에 적재한다.
9. canonical key로 정렬해 반환한다.

**의미 훼손이 작다고 본 근거**(recruit 최근접 1명 · combat touchCount 포화 · leader 국소 카운트)는 **설명이지 사양이 아니므로** 사람용 가이드 `CrowdCity/CROWD_GUIDE.md` §8이 담는다. 여기 남는 것은 그 논거가 아니라 아래 게이트다.

**게이트:** `CandidatesVisited ≤ QueryCount × LaneCount × B` counter 증명 + dense에서 work/N이 밀도에 무증가 + cap 미발동 fixture는 legacy 바이트 동일 + **cap 발동 fixture는 바이트 동일 불요 → 거동 A/B("no perceptible difference": recruit 지연/claimant/strength deficit/conversion·elimination/separation 편차) 승인 후에만 오라클 갱신.** 성공조건은 **초선형 work term 제거**(상수배 아님). 밀도 캡은 **게임플레이 변경**이므로 순수 성능 리팩터와 **분리 커밋**한다.

### 9.2 canonical order 표 (결정성 계약)

| 대상 | 순서 |
|---|---|
| agent | `AgentId` |
| grid | `cellKey` → `lane` → `AgentId` |
| query tie | `distSq` → `AgentId` |
| recruit | neutral `AgentId` 오름차순 |
| combat edge | `(min, max AgentId)`로 canonicalize → sort → unique |
| team-pair | `(winner, loser TeamId)` |
| victim | `(assignedDistSq, victimAgentId)` |
| leader | leader / team Id 순 |
| commit · presentation sync · event | 위와 **같은 순서** |

- float 합은 stable `AgentId`/team-pair 순 **직렬 reduction**으로 만든다. **atomic float 금지, worker 완료 순서 reduction 금지.**
- `[BurstCompile(FloatMode = FloatMode.Strict, FloatPrecision = FloatPrecision.Standard)]`, **FastMath 금지**. cross-ISA 계약이 없어(싱글플레이) `Deterministic` 강제는 하지 않는다.
- worker/batch permutation 테스트: worker ∈ {0, 1, default, max}, batch ∈ {1, 16, 64, 127, >N}, 반복·순서 변경. 각 tick `Complete` 후 canonical 스냅샷 바이트 비교. `JobWorkerCount`는 `try/finally`로 원복.
- **현재 트리 상태:** victim 순서는 이미 이 계약대로 구현돼 있다(`CombatResolver.SortVictimsByDistanceThenId` — `(assignedDistSq, AgentId)` 삽입정렬). 사양은 유지하고 **구현 방식만** §9.5의 T3b에서 교체한다.

### 9.3 `DeterministicRng` (uint4) 사양 — 미구현 (M-sim-2b)

**현재 트리 상태: 미구현.** `git grep DeterministicRng -- Assets/` 무결과. 현행은 `System.Random` **인스턴스 4개**다 — `CrowdRoot._rng` 1개(`CrowdRoot.cs:332`, seed = `config.Seed`) + 팀별 `RivalAiDriver._random` 3개(`RivalAiDriver.cs:43`, seed = `config.Seed + t`, 생성 루프 `CrowdRoot.cs:297-301`이 `_teamCount - 1`개 = `1 + RivalCount - 1` = 3). 잡은 RNG를 보유하지 않는다(직렬 프리패스에서 소모).

- 공유 `System.Random`을 **잡에 넘기지 않는다.**
- `DeterministicRng` = `uint4` xoshiro 계열 상태 + **고정 bit→float 변환**(상위 24bit × 2⁻²⁴).
- 초기화 = `mix(globalSeed, stableAgentId | teamId, streamTag)`. 0 상태는 고정 non-zero로 치환.
- 스트림 분리: spawn = 직렬 `SpawnRng`, wander = per-agent, rival/team tie-break = per-team.
- 밀도 캡(§9.1 ⑥)의 rotating cursor는 **RNG를 소모하지 않는다** — `(queryAgentId, cell, lane, tick)` 순수 해시로 만든다.
- RNG 교체는 기존 시드 재현을 깬다 → **의도된 재기준선**이며, 오라클 md5를 새로 잡아야 한다.

### 9.4 T2(분리 이웃 스캔 밀도 캡) 재도입 시 필수 정책 수정

되돌린 이력이 있다(§9.1의 `4adb502`). 재도입하려면 아래를 함께 고쳐야 한다.

- **판정 기준을 버킷 해시 충돌 점유수가 아니라 정확한 셀 점유수로 바꾼다.** 해시 충돌이 occupancy를 부풀려 오탐을 만들고, 그 오탐이 조밀 클러스터의 줄무늬·지터로 나타났다.
- `T`/gain/tangential 튜닝을 동반한다.
- 결정성: `(distSq, AgentId)` 전순서 top-k, 안티-스타베이션은 `(queryAgentId, cell, lane, tick)` 순수 해시(RNG 미소모).
- 리스크 중(시각·거동 변화) → shot 하네스 + 육안 검증 필수. 렌더 게이트는 픽셀 허용오차(위 게이트 절).

### 9.5 T3a / T3b — 리졸버 잡화 사양 (미구현)

**T3a. 그리드 카운팅-소트 전환.** 현재는 full-rebuild LIFO 링크드리스트 해시(managed)다. 전환 형태:
① IJobParallelFor로 cell/lane key 산출 → ② **단일 Burst IJob**으로 count → prefix → scatter(canonical) → ③ 전향 후 O(N) re-lane. 안정 `cellKey → lane → AgentId` 배치.
- **`NativeParallelMultiHashMap` 금지.** 열거 순서가 비결정이라 §9.2 canonical order를 깬다. (현재 트리에 사용처 없음 — `c9e61a4`에서 확인.)
- 자체 이득은 작다 — `GridRebuild`는 10k에서 Total의 **1.03~1.05%**(SMR 경로, `Perf/simopt10k_step1_r{1,2,3}.txt`) / **2.31~2.40%**(GPU 경로, `Perf/simopt10k_gpupath_r{1,2,3}.txt`)이고, grid를 native로 복사하는 `FollowerGridSnapshot`이 GPU 경로에서 별도로 0.17~0.22%다. 그래도 **리졸버·분리 병렬 쿼리의 전제**다.
- 게이트: 오라클 바이트 동일(쿼리 소비자는 이미 `(distSq, AgentId)` 순서에 무관).

**T3b. Recruit/Combat 잡화.** 병렬 후보 발견(per-agent) + **직렬 Burst canonical reduction**(팀-쌍, victim 선택, 리더 소거, commit).
- **victim O(v²) 삽입정렬 제거**: instant 경로 = 결정적 radix 또는 order-free, rate-limited = bounded max-heap prefix. **현재 트리 상태: 미착수** — `CombatResolver.SortVictimsByDistanceThenId`가 여전히 삽입정렬이다. 카운터 `victim_comparisons_per_victim`(`CrowdProfileHarness.cs:439`)로 **≈31**이 관측됐으나 ⚠️ **그 카운터 출력은 커밋된 적이 없고**(원문은 `768130c`가 추가한 `Docs/CrowdCity/SIM_OPT_10K_PLAN.md` — `0733deb`이 삭제했다. `git show 768130c:Docs/CrowdCity/SIM_OPT_10K_PLAN.md`로 복구) **스케일·모드도 기록돼 있지 않다** — 재측정 없이 이 수치를 게이트 근거로 쓰지 말 것.
- 결정성 계약은 §9.2 표 그대로. RNG는 §9.3의 per-agent/team `uint4` 해시 스트림(공유 `System.Random` 잡 반입 금지).
- Combat은 초선형으로 보이며(`candidate_visits` 5k 0.89M → 10k 76.8M/tick) **50k에서 관건**이 된다. ⚠️ 이 두 수치도 **커밋된 카운터 출력이 없고**(원문은 위 항목과 같은 `768130c`의 `SIM_OPT_10K_PLAN.md` — 복구 명령은 위 항목) 하네스·모드는 기록이 아니라 추론이다. 게다가 원문은 그 급증을 **`combat_touching`이 10k에서야 발생하는 레짐 변화**로 귀속시켰는데 이관에서 그 한정어가 탈락했다 — 2배 인구 대 86배를 **매끄러운 초선형 곡선으로 읽지 말 것**.

### 9.6 모바일 최약기기 1만 "하한" 조건 (외삽 — 실측 아님)

> 출처: Codex R5 판단, `git show fb14a41:codex_burst_out5.txt`. 5년 window(2021~2026) 최약 AOS/iOS 기준 1만 floor 판정 = **(B) 조건부 현실적**. 실기 측정이 아니라 코드·ProjectSettings 근거 + 기기 스펙 외삽이므로 **아래 수치를 성능 목표로 못박지 말 것**(§7 "성능 목표 수치 지어내기 금지").

**렌더 경로 이중화가 필수다.** VAT 인스턴싱 경로는 vertex 스테이지의 StructuredBuffer(= compute/SSBO)를 요구하므로 **GLES3.1+ 에서만 성립**한다. 이 프로젝트의 설정은:

- `ProjectSettings/ProjectSettings.asset:179` — `AndroidMinSdkVersion: 25`
- 같은 파일 `:536` — Android graphics API = `150000000b000000` = **Vulkan 우선 + GLES3 폴백**
- 같은 파일 `:541` — `openGLRequireES31: 0` → **GLES3.1을 강제하지 않는다**

⇒ **GLES3.0-only 기기가 window 안에 들어온다.** 그런데 `CrowdRenderer.cs:105`(`graphicsShaderLevel < 45 || !supportsComputeShaders`)와 `:114`(`!supportsInstancing || maxComputeBufferInputsVertex <= 0`)는 **SMR 경로로 내려가는 게이트일 뿐**, 2차 인스턴싱 경로를 제공하지 않는다. 1만 하한을 주장하려면 `DrawMeshInstanced` + `MaterialPropertyBlock` **2차 경로**가 필요하다.

> **원문 정정.** Codex R5는 이 요구를 `Graphics.RenderMeshIndirect`에 걸었으나, 실제 호출은 `Graphics.RenderMeshPrimitives`다(`CrowdRenderer.cs:188`, 리더 그림자 `:212`). **API 이름만 다르고 compute/SSBO를 요구한다는 제약은 동일**하므로 위 결론은 그대로 유효하다.

**하나라도 빼면 1만 하한이 깨지는 6개 항목:** ①동시 가시 hard cap ②frustum/거리 컬링 ③고정 50Hz 심(`GameplayRoot`)에서 **분리된** multi-rate behavior LOD ④밀도 캡(§9.1) ⑤per-agent RNG(§9.3) ⑥최약기기 장시간 thermal soak 수용 게이트.

**"1만"의 정의:** 논리적 **활성** population + 동시 가시 hard cap. 1만 전원 동시 가시로 해석하면 판정이 **(C) 비현실적**으로 뒤집힌다.

**메모리는 제약이 아니다**(단 원문 R5의 "SoA 1만 ≈ 1MB"는 현재 트리보다 작다). 현재 트리의 per-agent 네이티브 SoA는 `AgentBuffer` **21 B**(`Id` 4 + `Team` 4 + `IsLeader` 1 + `Pos` 8 + `Scale` 4) + `CrowdSimState`의 per-agent 17배열 **108 B** = **129 B/agent** ⇒ 1만 ≈ **1.3 MB**, 5만 ≈ **6.5 MB**. 여기에 grid 해시 테이블(`SpatialGrid.ComputeTableSize` = 용량×2 이상의 2의 거듭제곱 → `GridBucketHead` + `BucketTeamCount`×teamCount)과 관리형 미러·`InstanceData`가 더해지므로 실제 총량은 이보다 크다. 결론은 그대로다 — binding은 렌더 제출·GPU fill·지속 발열이다(**이 결론은 이 절의 모바일 최약기기 외삽에 한정되며, 데스크톱/에디터 `GameplayRoot.Update`의 sim 대 렌더 준비 배분에 대해서는 아무것도 말하지 않는다 — 그쪽은 여전히 미측정이다**: 핵심 현황 절의 철회 항목).

**렌더-스택 티어별 상한**(R4 — §9.5의 T3a 등 sim 축과 **다른 축**이고, 전부 외삽·미측정):

| 티어 | 내용 | 동시 가시 상한 |
|---|---|---|
| T0 | 현행 | 300~700 |
| T1 | sim만 최적화 | 300~900 (자릿수 불변) |
| T2 | — | 3천~8천 |
| T3 | GameObject 제거 + indirect | **1만~2만 (1만 첫 도달)** |
| T4 | + VAT · LOD | 총 활성 2만~4만 · 동시 가시 1만~2만 |

VAT/BRG/multi-LOD는 품질·헤드룸(선택)이지 하한 필수는 아니다. **"보장" 선언은 Mali-G52 / Adreno 610급을 포함한 device matrix에서 실기 thermal soak를 통과한 뒤에만** 한다.

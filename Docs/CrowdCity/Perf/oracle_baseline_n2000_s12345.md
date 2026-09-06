# 오라클 바이트 동일 게이트 — 산출물 체크섬 기록 (n=2000, seed=12345, 1000틱)

이 문서의 역할: [`../../WORK_STATE.md`](../../WORK_STATE.md) "검증 게이트" 절의 **오라클 바이트 동일 게이트** 값이 그 문서 안에만 존재해 저장소로 확립도 반증도 되지 않던 상태를 닫는다. 게이트에 적힌 신머신 절차를 **2회** 그대로 실행하고 산출물 md5를 tracked 파일로 남긴다.

- 최초 측정: 2026-07-26, 커밋 `4ee8466` — §1~§4
- 재확인: 2026-07-27, 커밋 `862856c` — §5. 출하 config가 바뀐 뒤(`UseDynamicConvertRate` `0`→`1`) 같은 값이 그대로 다시 나왔다. **재기준선이 아니라 재확인이다.**
- 결과: `4F79282EB20A79023B45F2EB2DE5271B`이 **두 커밋·두 출하 config에 걸쳐 4회 독립 실행으로 재현됨**
- ⚠️ 이 게이트가 덮지 못하는 구간은 §6 — 1000틱 창 안에 전향·제거가 0건이다

---

## 1. 측정 환경

| 항목 | 값 |
|---|---|
| 커밋 | `4ee8466` (`4ee84663dc162861e0ed4b0adabd39d4d1973e2b`, 브랜치 `feat/crowd-sim-10k`) |
| 워킹 트리 | 실행 직전 clean (`git status --porcelain` 무출력) |
| Unity | `6000.3.9f1` (revision `7a9955a4f2fa`, build revision 8034645) |
| OS | Windows 10 Pro 64bit (10.0.19045), 물리 메모리 32712 MB (런 로그 헤더) |
| 하네스 | `CrowdOracleHarness.RunFromBatch` (edit-mode headless, `-nographics`) |
| 씬 | `Assets/@Project/Scenes/GameScene.unity` (`CrowdOracleHarness.cs:31`) |
| dt | 0.02 (`CrowdOracleHarness.cs:32`, 하네스 상수 — CLI 인자 아님) |
| Burst | `EnableBurstCompilation=True`, `Synchronous=True` (런 로그 `BURST-ACTIVE:` 행 — `CrowdOracleHarness.cs:85`) |
| work counter | OFF (`-oracleCounters` 미전달 — 런 로그 `work counters OFF`) |
| config override | 없음 — 런 로그가 `sepBudget=config-default flatRate=False separationMode=config-default`를 인쇄(`CrowdOracleHarness.cs:106-109`) |
| 에이전트 수 | 2004 (summary CSV `agentCount` 열 = neutralCount 2000 + 리더 4) |

`CrowdOracleHarness`는 config를 무조건 덮어쓰지 않는다 — `-oracleSepBudget`/`-oracleFlatRate`/`-oracleSeparationMode`를 준 런에서만 복제본을 override한다(기본값 `CrowdOracleHarness.cs:49`, `:50`, `:51` → 적용 가드 `:193`, `:198`, `:203`). 위 표대로 셋 다 미전달이므로 이 게이트는 **출하 config 값으로** 돈다. 판독 주의는 [`../../WORK_STATE.md`](../../WORK_STATE.md) "하네스가 config를 덮어쓴다" 항목 참조.

---

## 2. 커맨드라인 (verbatim)

`<OUT>`만 런마다 다르고(run1/run2 서로 다른 디렉터리), `-logFile` 경로도 함께 달랐다. 나머지 인자는 동일하다.

`<repo>`는 저장소 루트다(원본 로그의 측정 머신 절대 경로를 공개 시 치환했다).

```
C:\Program Files\Unity\Hub\Editor\6000.3.9f1\Editor\Unity.exe ^
  -batchmode -nographics ^
  -projectPath <repo> ^
  -quit ^
  -logFile <LOG> ^
  -executeMethod CrowdOracleHarness.RunFromBatch ^
  -oracleOut <OUT> ^
  -oracleTicks 1000 ^
  -oracleScales 2000 ^
  -oracleSeeds 12345
```

인자 출처(전부 `CrowdOracleHarness.RunFromBatch`의 파서에서 유도):

| 인자 | 파싱 위치 | 기본값 | 비고 |
|---|---|---|---|
| `-oracleOut <dir>` | `CrowdOracleHarness.cs:56` | `<proj>/../phaseC_oracle` (`:69`) | 산출물 디렉터리 |
| `-oracleTicks 1000` | `:57` | 1000 (`:45`) | 기본값과 동일하지만 게이트 절차대로 명시 전달 |
| `-oracleScales 2000` | `:58` | `100,300,500,800` (`:46`) | scale = `GameConfigSO.neutralCount` override (`:191`) |
| `-oracleSeeds 12345` | `:59` | config seed (`:47`, `:140`) | `GameConfigSO.seed` override (`:192`) |
| (미전달) `-oracleNoVerify` | `:60` | verify=ON (`:48`) | ON이므로 하네스가 **같은 combo를 임시 파일로 1회 더** 돌려 내부 결정성까지 확인(`:162-166`, `VerifyDeterminism` `:366`) |
| (미전달) `-oracleCounters` | `:61` | OFF (`:38`) | |
| (미전달) `-oracleSepBudget` | `:62` | -1 = config 값 유지 (`:49`) | |
| (미전달) `-oracleFlatRate` | `:63` | OFF (`:50`) | |
| (미전달) `-oracleSeparationMode` | `:64` | -1 = config 값 유지 = Pairwise (`:51`) | |

두 런 모두 프로세스 종료 코드 **0**(`EditorApplication.Exit(exitCode)` — `CrowdOracleHarness.cs:101`, 실패 시 7 `:92`).

---

## 3. 산출물 md5

`-oracleOut` 디렉터리에 생성되는 4개 파일 중 3개가 게이트 비교 대상이다. `phaseC_oracle_determinism.txt`는 `DateTime.Now`를 담아 비교 대상이 아니다(`CrowdOracleHarness.cs:383`).

| 파일 | 크기 (bytes) | md5 (run1) | md5 (run2) |
|---|---|---|---|
| `phaseC_oracle_snapshot_n2000_s12345.bin` | 18076104 | `4F79282EB20A79023B45F2EB2DE5271B` | `4F79282EB20A79023B45F2EB2DE5271B` |
| `phaseC_oracle_summary.csv` | 78359 | `BADD215F42816C99B50BF70C2E1909F8` | `BADD215F42816C99B50BF70C2E1909F8` |
| `phaseC_oracle_events.csv` | 15992 | `ECA5A0F2DB11B54653E3DB634093FCF8` | `ECA5A0F2DB11B54653E3DB634093FCF8` |

기계 대조용(`md5sum -c` 형식, 소문자):

```
4f79282eb20a79023b45f2eb2de5271b  phaseC_oracle_snapshot_n2000_s12345.bin
badd215f42816c99b50bf70c2e1909f8  phaseC_oracle_summary.csv
eca5a0f2db11b54653e3db634093fcf8  phaseC_oracle_events.csv
```

- **스냅샷 `.bin`은 이 저장소에 커밋하지 않는다** — 18,076,104 bytes(약 17.2 MB)로 tracked 산출물로 두기에 크다. 이 문서의 체크섬이 그 자리를 대신한다. summary/events CSV도 같은 이유로 넣지 않되(각 런에서 즉시 재생성되며 위 md5로 대조된다) 크기는 작으므로, 필요해지면 별도 판단으로 추가한다.
- 하네스 내부 결정성 확인도 두 런 모두 PASS: `phaseC_oracle_determinism.txt`의 `result:` 행이 `PASS`(같은 seed 2회 실행 바이너리 동일 — `CrowdOracleHarness.cs:379-385`). 즉 실제로는 **런당 2회씩, 총 4회** 시뮬이 돌아 전부 같은 바이트를 냈다.

---

## 4. 판정

- run1 md5 == run2 md5 → 이 머신에서 **재현된다**.
- 두 값 == `WORK_STATE.md` 게이트 절의 `4F79282EB20A79023B45F2EB2DE5271B` → **일치**. 게이트가 참조하는 값에 이제 저장소 근거가 있다.
- §1~§3은 `4ee8466` 시점의 측정이다. 그 뒤 `862856c`에서 같은 절차를 다시 돌린 결과가 §5이며, 세 md5는 모두 그대로다. 이후 시뮬 결과가 불변이어야 하는 변경은 위 §2 커맨드라인을 그대로 돌려 §3의 세 md5와 대조한다 — 다만 **무엇이 그 대조에 잡히고 무엇이 안 잡히는지는 §6을 먼저 읽고 판단한다.**

---

## 5. 재확인 — `862856c` (2026-07-27)

### 5.1 왜 다시 돌렸나

`862856c`가 출하 asset의 `UseDynamicConvertRate`를 `0`에서 `1`로 뒤집었다(`GameConfig.asset:47`). 이것이 §3의 md5를 무효화한다는 **전제로 재기준선 작업이 발주되었다.** 실제로 돌려 보니 무효화되지 않았다 — 그래서 이 절은 새 기준값이 아니라 기존 값에 대한 **재확인 기록**이다.

### 5.2 실행 조건

`4ee8466` 때와 달라진 것은 **커밋과 그에 딸린 출하 config뿐**이다. 하네스 인자는 §2 커맨드라인 그대로다(n=2000, seed=12345, 1000틱, override 인자 없음).

| 항목 | 값 |
|---|---|
| 커밋 | `862856c` (브랜치 `feat/crowd-sim-10k`) |
| 출하 config 차이 | `UseDynamicConvertRate: 1`. `4ee8466`에는 이 필드가 **asset에 아예 없었고**(따라서 `GameConfigSO.cs:125` 초기화값 `false`로 동작), `ab5b6b6`이 `0`으로 추가한 뒤 `862856c`이 `1`로 바꿨다 |
| 실행 횟수 | 2회 (독립 실행, `<OUT>`·`-logFile`만 상이) |
| work counter | OFF (런 로그 `work counters OFF`) |
| Burst | 런 로그 `BURST-ACTIVE … Synchronous=True` |
| config override | 없음 — 런 로그가 `sepBudget=config-default`를 인쇄 |
| 종료 코드 | 두 런 모두 **0** |
| 컴파일 오류 | 두 런 모두 `error CS` **0건** |
| 내부 결정성(verify ON) | 두 런 모두 **PASS** |

### 5.3 결과 — 세 파일 전부 바이트 단위로 동일

| 파일 | 크기 (bytes) | md5 (`862856c` 2회) | §3(`4ee8466`) 값과 |
|---|---|---|---|
| `phaseC_oracle_snapshot_n2000_s12345.bin` | 18076104 | `4F79282EB20A79023B45F2EB2DE5271B` | 일치 |
| `phaseC_oracle_summary.csv` | 78359 | `BADD215F42816C99B50BF70C2E1909F8` | 일치 |
| `phaseC_oracle_events.csv` | 15992 | `ECA5A0F2DB11B54653E3DB634093FCF8` | 일치 |

### 5.4 판정

- **재기준선 불필요.** 게이트 값은 `4F79282EB20A79023B45F2EB2DE5271B` 그대로이며, `862856c`는 그 값을 바꾸지 않고 착지했다.
- **이 재확인은 값의 출처를 강화한다.** 이제 이 md5는 **두 커밋(`4ee8466`, `862856c`)·두 출하 config·4회 독립 실행**을 근거로 갖는다. verify가 두 런 모두 ON이었으므로 하네스가 각 런 안에서 같은 조합을 한 번 더 돌렸고, 따라서 실제 시뮬레이션 수는 재확인분만 4회, 최초 측정분과 합쳐 **총 8회**다.
- **왜 안 바뀌었는지는 토글이 죽어 있어서가 아니다.** 배선은 살아 있다(`GameConfig.asset:47` → `GameConfigSO.cs:125` → `CombatResolver.cs:238` → `:364`), 그리고 `RateLimitConversion`이 `1`이므로 "rate limit이 꺼져 있으면 무효" 면제 조항도 걸리지 않는다. 실제 이유는 **고정 시나리오의 커버리지**이며 §6이 그것이다.

---

## 6. 이 게이트가 덮는 구간 — 전향과 제거는 1000틱 창 밖이다

> 절 제목을 "전투"가 아니라 "전향과 제거"라고 쓴 것은 의도적이다. 창 안에 **전투 접촉**이 있었는지 없었는지는 이 산출물들이 말해 주지 않는다(6.3 참조). 측정된 것은 전향·제거 건수뿐이다.

### 6.1 측정된 것

- **창 안에 전향 0건, 제거 0건.** summary CSV에 팀 인원수가 감소하는 행이 하나도 없고, events CSV는 `CountChanged` **440행**뿐으로 `Eliminated` 행이 **0개**다.
- **같은 n/seed로 5000틱 진단을 돌리면 최초로 관측되는 팀 인원수 감소가 틱 1092**다. 게이트가 멈추는 1000틱보다 **92틱 뒤**이며, dt=0.02이므로 시뮬레이션 시간으로 **1.84초** 뒤다. 틱 5000까지 가면 판이 `0,0,0,1499`로 정리되고 제거는 3건이다.

### 6.2 따라서 (경계 그 자체)

**전향 *속도*만 바꾸는 변경은, 그 변경이 최초 팀 인원수 감소를 1000틱 창 안으로 끌어오지 않는 한 이 게이트를 바이트 동일로 통과한다.** `862856c`가 정확히 그 경우다 — 속도를 바꿨는데 세 산출물이 그대로였다.

("최초 전향"이 아니라 **"최초 팀 인원수 감소"** 라고 쓴 이유: 틱 1092에서 실제로 관측된 것은 인원수가 줄어든 사건이고, 그것이 전향인지 제거인지는 이 진단이 구분하지 않았다. 측정된 양으로만 진술한다.)

### 6.3 여기서 더 나가지 말 것

- ❌ **"게이트가 전투 변경에 눈이 멀었다"로 일반화하면 틀린다.** 어떤 변경이 관측 가능한 인원수 감소(전향이든 제거든)를 창 안으로 끌어오면 산출물이 움직이고, 그러면 게이트가 잡는다. 위 6.2의 경계가 측정된 전부다.
- ❌ **반대로 "전투를 앞당기는 변경이면 반드시 잡힌다"도 보장이 아니다.** 잡히는 조건은 "전투가 빨라졌다"가 아니라 **창 안에서 스냅샷이 실제로 달라졌다**는 것이다. 접촉 반경을 넓혀도 그 결과가 예산 누적에만 머물고 1000틱 안에 아무것도 방출하지 않으면, 전투 판정은 바뀌었는데 산출물은 그대로일 수 있다 — 전투 접촉 자체는 스냅샷에 기록되지 않기 때문이다(다음 항목). 보장되는 것은 한 방향뿐이다: **방출이 창 안으로 들어오면 잡힌다.**
- ❌ **"창 안에 팀 간 접촉이 0건"이라고 쓰면 안 된다.** 이 산출물들은 그것을 뒷받침하지 않는다 — 리더끼리만 스친 접촉은 예산을 쌓기만 하고 방출하지 않으므로 summary에도 events에도 행을 남기지 않는다. 측정된 것은 **전향과 제거가 0건**이라는 좁은 사실과 **최초 관측 팀 인원수 감소가 틱 1092**라는 사실뿐이다.

### 6.4 선택지 (채택된 결정이 아님)

`-oracleTicks`를 **약 1100 이상**으로 올리면 이 게이트에 전투 커버리지가 생긴다. 다만 그것은 **게이트 설계 변경**이고 자체 재기준선 비용(새 틱 수에 대한 새 md5 3개, 그리고 그만큼 길어지는 런 시간)이 따른다. 여기 적는 것은 **선택지이지 내려진 결정이 아니며**, 채택하려면 별도 판단이 필요하다.

---

## 7. 부수 효과 (커밋 전 되돌릴 것)

§1~§3의 두 런 각각 직후 `git status --porcelain`이 정확히 한 줄을 냈다:

```
 M "Assets/TextMesh Pro/Resources/Fonts & Materials/LiberationSans SDF - Fallback.asset"
```

`git checkout -- "<그 경로>"`로 되돌렸고 그 뒤 `git status --porcelain`은 무출력이다. 이것은 [`../../WORK_STATE.md`](../../WORK_STATE.md) "TMP 에셋 부수 효과" 항목이 예고한 그대로이며, 이 런으로 **재확인**됐다. `git add -A` 금지 이유가 바로 이것이다.

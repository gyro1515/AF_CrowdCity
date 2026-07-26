# 오라클 바이트 동일 게이트 — 산출물 체크섬 기록 (n=2000, seed=12345, 1000틱)

이 문서의 역할: [`../../WORK_STATE.md`](../../WORK_STATE.md) "검증 게이트" 절의 **오라클 바이트 동일 게이트** 값이 그 문서 안에만 존재해 저장소로 확립도 반증도 되지 않던 상태를 닫는다. 게이트에 적힌 신머신 절차를 **2회** 그대로 실행하고 산출물 md5를 tracked 파일로 남긴다.

- 측정일: 2026-07-26
- 결과: 문서에 적힌 `4F79282EB20A79023B45F2EB2DE5271B`이 **재현됨**(2회 실행 상호 동일, 문서 값과 일치)

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

```
C:\Program Files\Unity\Hub\Editor\6000.3.9f1\Editor\Unity.exe ^
  -batchmode -nographics ^
  -projectPath D:\UNITY\UNITY_PROJECT\ActionFitPro\AF_CrowdCity ^
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
- 이 문서는 `4ee8466` 시점의 값이다. 이후 시뮬 결과가 불변이어야 하는 변경은 위 §2 커맨드라인을 그대로 돌려 §3의 세 md5와 대조한다.

---

## 5. 부수 효과 (커밋 전 되돌릴 것)

두 런 각각 직후 `git status --porcelain`이 정확히 한 줄을 냈다:

```
 M "Assets/TextMesh Pro/Resources/Fonts & Materials/LiberationSans SDF - Fallback.asset"
```

`git checkout -- "<그 경로>"`로 되돌렸고 그 뒤 `git status --porcelain`은 무출력이다. 이것은 [`../../WORK_STATE.md`](../../WORK_STATE.md) "TMP 에셋 부수 효과" 항목이 예고한 그대로이며, 이 런으로 **재확인**됐다. `git add -A` 금지 이유가 바로 이것이다.

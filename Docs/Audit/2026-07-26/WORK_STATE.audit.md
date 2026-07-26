<!-- RECOVERED VERBATIM from session transcript
     source: 7be25ba5-e341-4238-8642-5da87b3a7962.jsonl line 1589 (task-notification <result> block)
     task-id: a4fa06d4fb3351eed | agent: "Verify WORK_STATE exhaustively" | audited doc: Docs/WORK_STATE.md
     Body below is byte-identical to the transcript's <result> payload (HTML entities
     &lt; &gt; &amp; are as stored by the transcript). Nothing added, removed, or reworded. -->

# Conclusion

`Docs/WORK_STATE.md` 전수 검증 결과. **파일 변경 없음(읽기 전용).**

## 결함 표

### FALSE — 사실이 아닌 서술

| 줄 | 주장 | 실제 | 심각도 |
|---|---|---|---|
| 103 | "`14.86`이 등장하는 커밋은 히스토리 전체에 **3개**뿐" | **6개**: `0733deb, a28aee3, 47cbb79, 98ec4b3, 433b5fe, 5caae09`. (`47cbb79`의 히트는 원시데이터 `5000,CCMove,0.5679,14.8674` — 맨 카운트 주장이 왜 취약한지 보여주는 예) 나머지 서술(5caae09 최초, 433b5fe 재라벨, 같은 커밋의 22.95→14.04 별기)은 전부 HOLD | FALSE |
| 104 | "에디터 60fps 인원 상한 ≈ **3400** = 렌더-바운드, 시뮬 아님" | "3400"은 추적 트리 전체에서 이 줄 **1회**뿐. 출처 `433b5fe:+36`, 하네스·커밋·원시 없음. **저장소 내 반증 존재**: `Docs/Photo/PRO3.PNG`(어느 문서도 인용 안 함)이 `CPU 37.85ms(~26fps)`, `GameplayRoot.Update self 19.69ms=52.0%`, `Loading.IsObjectAvailable calls 6008`(→3004 에이전트). 3004에서 이미 26fps이므로 60fps 상한이 3400일 수 없고, PRO3은 **시뮬 지배**라 "렌더-바운드·시뮬 아님"도 반증 | FALSE |
| 142, 149 | separation = `CrowdRoot.cs:929` | `CrowdRoot.cs`에 `grid.QueryCircle` 호출이 **하나도 없다**. 929는 `RenderGpuCrowd` 내부 빈 줄. separation 이웃 열거는 Burst 잡 `SteeringForceJob.cs:91`(무캡)/`:143`(캡)이고, 네이티브 스냅샷은 `CrowdRoot.cs:1315` | FALSE |
| 141, 150 | wander 벽 레이 = `CrowdRoot.cs:1099` | 1099는 닫는 중괄호. 실제 = **`CrowdRoot.cs:1697`** `if (!Physics.Raycast(origin, dir, WanderRayDistance, _wallProbeMask))` (`RepickWanderHeading` 선언 `:1685`) | FALSE |
| 149 | 에이전트별 SDF `WallSolver.Resolve` = `CrowdRoot.cs:1125` | 1125는 `ComputeNeutralScale`의 닫는 중괄호. 실제 = **`CrowdRoot.cs:1723`**. 게다가 핫패스는 Burst로 이동 — `FollowerSdfMoveJob.cs:70`, `NeutralSdfMoveJob.cs:67` | FALSE |
| 40, 101, 178 | "`GameplayRoot` **`FixedStep`** = 0.02" | 식별자는 **`FixedStepSeconds`**(`GameplayRoot.cs:12`). `FixedStep`이라는 리터럴은 `CrowdPerfHarnessP95.cs:164`에만 존재. 값 0.02는 HOLD | FALSE(식별자) |
| 144 | "`M-sim-0` 실측(**아직 미실행**, 베이스라인 산출물 미커밋)" | 실행됐다 — 인프라 `5c05a27`, Step 1의 4개 수정 전부 랜딩, 헤드리스 2000/5000/10000 측정이 `Perf/simopt10k_step1_r{1,2,3}.txt`로 커밋. "산출물 미커밋"만 부분 참(M-sim-0 라벨 CSV는 실제로 없음) | FALSE |
| 223 | "현 하네스는 `AddComponent&lt;CrowdRoot&gt;`라 SDF ON을 우회 → 이 수정 없이 낸 프로파일은 기준선 불인정" | `AddComponent&lt;CrowdRoot&gt;`는 `Assets/` 어디에도 없다(경고 주석으로만). 하네스는 라이브 프리팹 로드(`CrowdProfileHarness.cs:157` `ResourceLoader.LoadPrefab&lt;CrowdRoot&gt;()`), SDF 강제(`:201`), `!IsSdfActive` hard-fail(`:205-210`), `CC.Move` 폴백 hard-fail(`:231-237`). scale은 `-profileScales`(`:58`)로 CLI화, `agents`는 이미 `OracleAgentCount`(`:216`). **나열된 4개 To-Do 전부 완료** — 지시대로 따르면 랜딩된 작업을 재실행하거나 유효한 baseline을 불신하게 된다 | FALSE |
| 349 | "현행은 `CrowdRoot._rng`와 `RivalAiDriver`의 `System.Random` **두 스트림**" | **4개 인스턴스**. `CrowdRoot.cs:332` ×1 + `CrowdRoot.cs:297-301`이 `_teamCount-1`(=rivalCount 3)개 `RivalAiDriver`를 `config.Seed + t`로 생성 → `RivalAiDriver.cs:43` ×3. 잡은 RNG를 보유하지 않음(직렬 프리패스에서 소모) | FALSE(개수) |
| 305 | §9.1 "`SpatialGrid.QueryCircleCapped`(`SpatialGrid.cs:274`)가 존재하지만 단순 방문수 절단만" | 메서드 서술은 정확(4개 부재 항목 전부 확인). 그러나 **프로덕션 호출자가 0** — 유일한 호출자는 `Crowd/Tests/Editor/DensityCapTests.cs`(7개 `[Test]`). 실제 캡은 `SteeringForceJob.cs:104-155`의 **중복 인라인 사본**이고 `CrowdRoot.cs:1357`에서 무조건 스케줄. 착수자를 잘못된 지점으로 보낸다 | FALSE(불완전) |
| 398 | "SoA 1만 ≈ 1MB(5만 ≈ 5MB)" | **~169 B/agent** → **1.7MB@10k / 8.5MB@50k** (~1.7배 차이). `AgentBuffer` 21 + `CrowdSimState` 17배열 108 + 테이블 비례 40. 관리 미러·`InstanceData`(stride 28) 포함 시 더 큼. 절의 결론("메모리는 제약 아님")은 불변 | FALSE(수치) |
| 372 | T3a "자체 이득은 **1%** 수준" | `GridRebuild` %Total@10k: SMR arm 1.03% / **GPU arm(최신) 2.31~2.40%**. 게다가 T3a가 통째로 삭제할 `FollowerGridSnapshot`(managed→native memcpy 세그먼트)을 계산에서 누락 | FALSE |
| 33 | "5k: **1.74 vs 0.54, 3.17배**" | `MANIFEST.md:423`의 3.17은 **차이/spread**(1.1967/0.3770=3.174). 1.74/0.54 = **3.22**. 32행은 "차이/spread" 라벨을 유지하는데 33행은 떼서, 독자가 계산할 수 있는 값과 어긋난다 | FALSE(라벨) |

### UNVERIFIABLE — 저장소로 검증 불가

| 줄 | 주장 | 상태 | 심각도 |
|---|---|---|---|
| **61, 62, 106** | 오라클 게이트 md5 **`4F79282EB20A79023B45F2EB2DE5271B`** | **이 문서 3개 지점에만 존재.** `Docs/CrowdCity/Perf/**` 0회(MANIFEST는 md5를 아예 언급 안 함), `Assets/` 0회, `.bin`/체크섬 산출물 미커밋. 히스토리상으로도 삭제된 산문 문서에만 있었다. **문서에서 가장 하중이 큰 게이트 값이 저장소로 반증 불가** — 62행의 신머신 재현 절차가 현재 이 값을 확립하는 유일한 수단 | UNVERIFIABLE |
| 63 | 렌더 게이트 픽셀 수치 35,587 / 123,161 / 171 / 184 | 산문 전용. `git log -S` → 삭제된 `Docs/CrowdCity/CHANGELOG.md`뿐이고, **그 문서 자체가 출처를 "세션 scratchpad 로그… 저장소만으로는 재현되지 않습니다"라고 인정**했다. 921,600만 소스 근거(`CrowdShotHarness.cs:25-26` = 1280×720) | UNVERIFIABLE |
| 32 | "6런으로 늘려도 paired t = **1.82**(df=5)" | 임계값 2.571은 정확(t₀.₀₂₅,₅=2.5706). 그러나 **r4/r5/r6이 저장소에 없다** — `MANIFEST.md:431`이 "⚠️ r4/r5/r6은 저장소에 없다"고 명시하는데 **WORK_STATE는 그 단서를 떼고 단정** | UNVERIFIABLE |
| 376 | `victim_comparisons_per_victim ≈ 31` | 카운터는 코드에 실재(`CrowdProfileHarness.cs:439`)하나 **카운터 출력이 한 번도 커밋된 적 없음**. 출처는 삭제된 `768130c`. **스케일·모드 둘 다 미기재** | UNVERIFIABLE |
| 378 | `candidate_visits` 5k 0.89M → 10k 76.8M/tick | 동일하게 카운터 출력 없음. 더 나쁜 점: **이관 시 원문의 한정어가 탈락**. 원문(`768130c`)은 `combat_touching`이 10k에서만 발생하는 **레짐 변화**로 귀속시켰는데, 그게 빠져서 2배 인구에 86.3배(≈2^6.4, 맞는 지수 없음)가 매끄러운 초선형 곡선처럼 읽힌다. "SMR 경로"도 기록이 아니라 추론 | UNVERIFIABLE |
| 85 | "Burst 핫잡: 10k SimTick **22.95→14.04ms (−38.8%)**" | 산술은 정확(38.824%). 그러나 MANIFEST·Perf 전 파일에 부재, `git log -S`는 자기 인용 2건뿐, **모드 미기재**, 두 끝점 모두 커밋된 두 데이터셋(SMR 20.56 / GPU 9.56) 어디에도 맞지 않아 모드 역추적조차 불가. **문서 최악 사례** — 헤드라인 이득이 하네스·모드·원시 전부 없음 | UNVERIFIABLE + MODE-MISSING |
| 64 | TMP "약 10 insertions / 911 deletions" | 워킹트리 clean, 이 파일의 유일한 커밋은 `8e2036c`의 2,735줄 추가. 크기는 개연적이나 검증 불가. 지시("되돌릴 것 / `git add -A` 금지") 자체는 유효 | UNVERIFIABLE |

### MODE-MISSING — 프로젝트 자체 규칙(96행) 위반

| 줄 | 도출 | 심각도 |
|---|---|---|
| 52 | "리더 ≤4명(**SimTick의 0.09%**)" — 0.09%는 **SMR·10k만** 참. 원시: SMR10k 0.0875% / **GPU10k 0.2085%(2.4배)** / SMR5k 0.455%. 즉 4개 셀에서 **0.09~0.46%로 5배 변동**하는 값을 불변식 표에 스케일·모드 없이 박음 | MODE-MISSING |
| 99, 104 | "세그먼트 mean의 3런 중앙값" 헤더가 p95(11.16/22.67)까지 덮는데, 이 둘은 SUMMARY 블록 `simtick_p95_ms`의 중앙값 — **세그먼트 mean이 아니다**. `MANIFEST.md:134`가 정확히 이 혼동을 경고. 또 두 p95 값은 MANIFEST에 부재(원시 파일에만) | MODE-MISSING |
| 65 | "병렬 +66% vs 직렬 +8~13%" — 하네스·모드 미기재(MANIFEST §3에서 복구 가능). 해당 런은 폐기되어 미커밋 | MODE-MISSING(경) |

### IMPRECISE

| 줄 | 주장 | 실제 |
|---|---|---|
| 3 | 검증 스탬프 `9f23df4` | **형식상 정확**(문서 최종 수정 `0733deb`의 부모). 그러나 **CLAUDE.md의 carve-out 점검이 발화한다**: `git diff --stat 9f23df4..HEAD -- Assets/`가 비어 있지 않음(`City/README.md`, `CrowdRoot.cs`, `RivalAiDriver.cs`). 셋 다 `0733deb` 자신이 만든 **주석 전용 1:1 치환**이므로 인용 좌표는 하나도 안 움직였다. → **메커니즘 결함**: 문서 갱신 커밋이 `Assets/`도 건드리면 통과하는 스탬프를 만들 수 없고, 실제로는 최신인 문서를 콜드스타트가 불신하게 된다 |
| 50 | "`_visualYaw` 쓰기 **7곳 전부 무조건**" | 1430/1440과 1526/1536은 같은 `if(speed&gt;0.001f)/else`의 두 팔, 1631/1673은 배타적 `sdfActive` 분기 → **어떤 실행도 7곳을 다 지나지 않는다**. 실질 주장(어느 것도 `_gpuRenderActive` 게이트 뒤에 없음)은 HOLD |
| 123 | "`public List&lt;Human&gt; Followers`" | 실제 `public List&lt;Human&gt; Followers { get; }` (get-only 자동속성). 요지(가변 컬렉션 타입)는 HOLD |
| 148 | "`PhysX.Simulate`는 1.53ms뿐" | PRO2.PNG의 행은 `Physics.Simulate`/`PxScene.simulate`. 실제 `PhysX.*` 자식들은 전부 ≤0.47ms |
| 61 | "events/summary CSV도 함께 비교" | 하네스는 `phaseC_oracle_determinism.txt`도 쓰고 그 안에 `DateTime.Now`가 들어간다(`CrowdOracleHarness.cs:380,:383`) → **바이트 비교에서 제외해야 하는데 명시 없음** |
| 63 | 렌더 게이트가 `CrowdShotHarness`만 지명 | `CrowdFlatRateShotHarness.cs`도 1280×720 PNG를 쓴다(`:140`). 그쪽 노이즈 플로어는 확립된 적 없음 |
| 142, 149 | `QueryCircle` lane 4개 | **5개** — `RivalAiDriver.cs:129`(AI 중립 밀도)가 목록에 없다 |
| 305 | §9.1 9단계 중 ⑦("필터 이전 budget 소비")를 부재로 서술 | 두 라이브 구현 모두 **이미 부분 충족** — radius 테스트와 호출자의 self/team 테스트 **이전에** 소비(`SpatialGrid.cs:315-322`, `SteeringForceJob.cs:111-124`) |

### OFF-BY-N

| 줄 | 주장 | 정확한 줄 |
|---|---|---|
| 142, 149 | recruit `RecruitResolver.cs:74` | **`:75`** (74는 `CrowdSimCounters.SetSource`) |
| 142, 149 | combat `CombatResolver.cs:271` | **`:287`** |
| 142, 149 | combat `CombatResolver.cs:461` | **`:487`** (461은 `accumulators[pairIndex] -= emitCount;`) |
| 150 | `RivalAiDriver.ProbeClearance` @~199 | `:200` (물결표 근사라 실질 HOLD) |

### RULE4 — 조용히 재타깃되는 참조

| 줄 | 인용 |
|---|---|
| 6 | "영역별 절 재편은 **다음 패스** 몫이다" |
| 141 | "layermask 버그가 있었고, **이번 세션**에 수정 완료" / "**이번 세션**에 **FIXED**" |
| 142 | "**이번 세션**에 mask에서 Unit 제외로 **수정 완료**" |
| 150 | "별개 correctness 버그 — **이번 세션** 수정 완료" |
| 179 | "그 브랜치도 여전히 존재하나 **현재 작업 대상**이 아니다" |
| 158 | "(**이 계획 작성 시점**엔 리팩토링이 진행 중이었다)" |

### BOUNDARY — 다른 문서 소관

| 줄 | 인용 | 소관 |
|---|---|---|
| 174-179 | "커널(순수 C#): `Assets/@Project/Crowd/Core/` — `AgentBuffer`, `SpatialGrid`, `CombatResolver`, `RecruitResolver`, `WallField`, `WallSolver`, `SimTuning`." (§2 전체가 구조 색인) | `PROJECT_MAP.md` §3·§4·§5·§14 — **이미 전부 존재** |
| 181-196 | "**메인 에이전트 = 매니저만**. 조사/파일읽기/분석/구현/편집/테스트/diff 리뷰는 **전부 서브에이전트에 위임**." + Codex 호출 템플릿 전문 | `CLAUDE.md`(Operating Model / Cross-Verification). 워크플로우 계약의 두 번째 집 = §1.1 Rule 1 위반 |
| 84-92 | "- Burst 핫잡 활성(FloatMode.Strict): 10k SimTick 22.95→14.04ms (−38.8%)" (§완료 목록 전체) | git 히스토리. §1.1 Rule 1: "Change history is not a fourth document" |
| 103 | 14.86 출처 추적 서술(어느 커밋이 무엇을 적었는지) | 정정 사실은 여기 남되, 3-커밋 계보 서술은 `CROWD_GUIDE.md` §15 "오래된 문서가 확신 있게 틀린다"·§16 |
| 321-324 | "- recruit는 \"최근접 1명\"만 쓰므로, k가 최근접을 포함하면 사실상 무손실." | `CROWD_GUIDE.md`(근거) |
| 400-410 | 렌더 티어 표 + "VAT/BRG/multi-LOD는 품질·헤드룸(선택)이지 하한 필수는 아니다" (전부 외삽·미측정) | `CROWD_GUIDE.md` |
| 262-294 | §8 파일 인덱스 | `PROJECT_MAP.md` §16이 문자 그대로 "Doc map". 동일 색인 두 집 (내용 자체는 정확) |
| 139-150 | 2026-07-17 마커 절 전체 | 82행이 "배경 참조용"으로 격하했으나 여전히 현재시제 사실로 서술 → 이 절이 FALSE 좌표 6건의 진원지 |

## 견고한 절 (결함 0)

- **깨면 안 되는 불변식 (46-57)** — 7행 전부, 좌표 하나하나 정확. `CrowdSimProfiler.cs:21`이 `*JobWait` inclusive 중첩을 독립 확증. EventBus 2개 타입도 여전히 참(트리 전체 `.Publish(` = `CrowdRoot.cs:1953/1964/1976`, 전부 `PublishTickEvents` 내부; 4개 Subscribe 전부 동일 lifecycle Unsubscribe 짝 보유).
- **함정 (71-76)** — 5좌표 전부 정확. `CrowdPerfHarnessP95` 5줄 유도(`:409/:412/:413/:364-367/:472`) 완벽.
- **하네스 override 주의 (67-69)** — 좌표 24/24 정확, off-by-N 0건. 출하 config 값(budget 0, GPU 1), 프리팹 `_useSdfSolver: 1`, `SteeringForceJob` 캡 의미론 전부 확인.
- **의도적으로 보류한 MINOR (117-131)** — 15좌표 전부 정확, 해소 1건의 5개 저장 가드도 전부 확인.
- **남은 작업 4번 (112)** — `MirrorPositionsToBuffer`(`:1741`)가 실제로 라이브 리더만 미러링. 4개 커밋 전부 해석 일치.
- **§9.6 코드/설정 근거 (386-392)** — `ProjectSettings.asset:179/:536/:541` 전부 정확, `150000000b000000` 디코드(21=Vulkan, 11=GLES3) 정확, **"원문 정정"이 맞다** — 실호출은 `Graphics.RenderMeshPrimitives`(`:188`, `:212`)이고 `RenderMeshIndirect`는 트리 전체 부재, SSBO 요구도 확증(`SetBuffer("_Instances")` → `HumanVat.shader:47 StructuredBuffer` vertex 단계 읽기).
- **커밋 해시 25/25** 전부 resolve + HEAD 조상 + 주장한 내용 일치. `f9c1cdc`를 §8 측정 트리로 든 것도 `MANIFEST.md:335`가 독립 기록(+`git diff fdf909d f9c1cdc -- Assets/` 빈 출력 증명).
- **참조 감사 dangling 0건** — MANIFEST §1~§6/§7/§7.7(c)(d)/§8/§8.3/§8.5/§8.7/§8.8, CROWD_GUIDE §11(`:445-455`에 EventManager 예외 기록 실재)·§17 전부 존재. `PROJECT_MAP.md:69`가 `AgentBuffer`를 "(SoA, NativeArray)"로 기록(207행 주장 HOLD). §8 인덱스 10행이 `git ls-files`와 정확히 일치, 삭제문서 11행이 `0733deb`의 11개 삭제와 정확히 일치, `phaseC_stage1_validator.txt`도 실제로 `.gitignore`에 추가됨.
- **산술 재계산 전부 통과**: 0.12배(0.1213) · 1.9배(1.919) · −83.6%(83.632%) · 47.79%/55.78% · 13.546% · 38.824% · 0.846/RI≤9.382/−20.763 · 32.46% · 2,224배/42.75배(보수적 하한).

## 판정

**콜드스타트 세션에 그대로 넘기기 안전한가 — 78행까지는 예, 그 아래는 아니다.**

- **1~76행(운영 계약)은 안전하다.** 불변식·함정·하네스 주의는 좌표 단위로 무결하고, 게이트 절차도 실행 가능하다. 단 두 개의 별표: ①오라클 md5가 저장소 근거 0 — 62행 재현 절차가 유일한 확립 수단이므로 신머신에서는 **반드시** 먼저 돌려야 한다(문서가 이미 그렇게 지시하고 있다는 점은 다행). ②52행 0.09%는 SMR·10k 전용 값.
- **80행 이후는 위험하다.** 실질 피해 경로가 셋: (1) §1(169행)이 §9를 정본으로 지목 → §9.1은 착수자를 프로덕션 호출자가 0인 `QueryCircleCapped`로 보내고 실제 캡(`SteeringForceJob.cs:104-155`)을 숨긴다. (2) §5 Step 1(223행)이 이미 완료된 4개 수정을 미완으로 서술 → 랜딩된 작업 재실행 또는 유효 baseline 불신. (3) 2026-07-17 마커가 6개 좌표를 죽은 줄로 가리키는데 82행의 "배경 참조용" 격하가 §5까지 덮는지 모호하다(§9는 명시적으로 정본이라 격하 밖).
- **문서 최대 구조 문제는 정본 md5·핵심 성능 수치가 산문에만 존재한다는 것**이다. 게이트 값이 저장소에서 반증 불가하면 "통과했지만 의미 없는" 검증 — 이 문서 62행이 스스로 경고하는 바로 그 상태다.

**점검 대비 결함**: 확인한 개별 주장 **약 156건**(커밋 25 · `file:line` 좌표 ~78 · 수치 21 · 절 참조 9 · §9 상태 16 · 게이트 파라미터/플래그/파일명 ~10) 중 **결함 48건** — FALSE 13 · UNVERIFIABLE 7 · MODE-MISSING 3 · IMPRECISE 8 · OFF-BY-N 4 · RULE4 6 · BOUNDARY 8(중복 계상 없음). 결함의 **약 70%가 80행 아래**에 몰려 있다.

# Changed files

없음 (읽기 전용).

# Verification commands and results

```
git rev-parse --short HEAD                        → d136350
25개 인용 해시 × git cat-file -e + merge-base --is-ancestor  → 25/25 ANCESTOR, 주제 일치
git diff --stat 9f23df4..HEAD -- Assets/          → 3 files, 4 insertions/4 deletions (전부 주석)
git log --oneline 9f23df4..HEAD -- Docs/WORK_STATE.md → 0733deb (부모가 9f23df4)
git log -S 14.86 --oneline --all                  → 6 commits (문서 주장 3)
git ls-files Docs/                                → 17 paths (§8 인덱스와 일치)
grep -rn "4F79282E" (대소문자 무관, 저장소 전체)   → Docs/WORK_STATE.md:61,62,106 만
grep -c "victim\|candidate_visits" Docs/CrowdCity/Perf/*  → 0
git log --all --diff-filter=A -- Docs/CrowdCity/Perf/  → ff60d39, e0f81e4, 47cbb79 (11 파일)
```

스팟체크 권장 3곳: `CrowdRoot.cs:929`(빈 줄, `QueryCircle` 없음) · `CrowdProfileHarness.cs:157`(`ResourceLoader.LoadPrefab&lt;CrowdRoot&gt;()`) · `SpatialGrid.cs:274` 호출자(`Crowd/Tests/Editor/DensityCapTests.cs`만).

# Risks or blockers

1. **오라클 md5를 확정도 반증도 못 했다** — Unity 실행 금지 제약 + 저장소에 스냅샷/체크섬 산출물 없음. 62행 절차 실행만이 유일한 해결.
2. **paired t=1.82, 픽셀 4수치, TMP 10/911, `candidate_visits`, `victim_comparisons`는 원시 데이터 자체가 저장소에 없어** 재계산 불가. FALSE가 아니라 반증 불가로만 분류했다.
3. **PRO.PNG/PRO2.PNG는 이미지 판독**으로 검증했다(14.86 self / 32.4% / 66ms(15FPS) 축 / `Physics.Simulate` 1.53ms 전부 확인). 다만 **두 캡처 어디에도 에이전트 수가 보이지 않아** 103행의 "스케일 미기록" 단서는 정직하다 — 그러나 "다른 스케일에서 잰 값"이라는 부분은 여전히 추론이다.
4. **82행의 격하 범위가 모호하다**("이 절과 아래 2026-07-17 마커/게이트/계획"). §5가 격하 대상인지 아닌지에 따라 223행 FALSE의 심각도가 달라지는데, 문서로 판별 불가.
5. `Docs/Photo/PRO3.PNG`는 추적되지만 **어느 문서도 인용하지 않는다** — 하필 그것이 104행 ≈3400 주장을 반증하는 저장소 내 최강 증거다.

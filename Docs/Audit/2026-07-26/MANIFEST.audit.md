<!-- RECOVERED VERBATIM from session transcript
     source: 7be25ba5-e341-4238-8642-5da87b3a7962.jsonl line 1583 (task-notification <result> block)
     task-id: ad4d156721d5fa375 | agent: "Verify MANIFEST against raw evidence" | audited doc: Docs/CrowdCity/Perf/MANIFEST.md
     Body below is byte-identical to the transcript's <result> payload (HTML entities
     &lt; &gt; &amp; are as stored by the transcript). Nothing added, removed, or reworded. -->

All recomputation and source verification complete. Report follows.

## Conclusion

**Every measurement number in MANIFEST reproduces exactly.** I recomputed ~224 figures from the six raw profile files and four CSVs and found **zero arithmetic errors**. All defects found are interpretive/provenance ones.

### Arithmetic convention (asked explicitly)

MANIFEST's stated convention is **median-of-sums** (§5: "세그먼트 mean을 런별로 합산한 뒤 런 간 중앙값"), and §4/§8.5/§8.7 apply it consistently. I recomputed both conventions: **no verdict changes** under either (step1 5k 49.20x vs 48.99x; 10k 59.43x vs 59.46x; GPU 5k 3.174x vs 3.186x; GPU 10k 0.1213x vs 0.1215x). The one place the two mix is §8.6 (defect 5).

### Defect table

| # | Section / line | Claim | Recomputed truth | Severity |
|---|---|---|---|---|
| 1 | §8.8-b (L486) | `Human.SetTeamMaterial` "호출부는 팀 전환 4곳(`CrowdRoot.cs:1790, :1813, :1929, :1938`)라 **`Recruit`/`Combat`에 실린다**"; therefore device builds' `Recruit`/`Combat` are cheaper | **False.** All four sites are inside `CommitOutcomes()` (`CrowdRoot.cs:1780`) and its helpers `RouteToSurvivor`(:1925)/`Neutralize`(:1935), called only from `CommitOutcomes` (:1865/:1869/:1881/:1885). `CommitOutcomes()` is invoked at **:712, wrapped by `Seg.CommitPublish`** (:711/:713). `Recruit`(:705-707)/`Combat`(:708-710) wrap only `_recruitResolver.Resolve`/`_combatResolver.Resolve` — pure `Crowd/Core` kernels that never touch `Human`. So the cost lands in `CommitPublish` = **0.008 ms/tick @10k**, and the derived "실기기의 Recruit/Combat은 조금 더 싸다" does not follow | **WRONG CLAIM** |
| 2 | §7.6 table, `render_median_ms` row | 판정 = **판정 불가** | Rule 3 as recorded: inconclusive iff \|Δ\| ≤ spread. \|Δ\|=14.8845, spread=11.439 → **ratio 1.301 &gt; 1, rule 3 does not trigger**. The correct label under its own rule is "규칙 통과, 사후 판단으로 미채택". The prose bullet under the table admits this ("spread를 넘기긴 했지만… 사후 판단"), so the defect is the table label contradicting the stated rule | **WRONG VERDICT** (self-disclosed) |
| 3 | §5, sub-segment residual bullet | "잔차는 `SteeringForceJob` 구조체 배선 + `Schedule`(`CrowdRoot.cs:1320-1389`)로" — stated for **both** `FollowerSteer` and `NeutralMove` | Line range is right for `FollowerSteer`, but that span also contains the **`FollowerSdfMoveJob` struct wiring (:1363-1388)**, and `NeutralMove`'s residual comes from a completely different region — **`NeutralSdfMoveJob` wiring (:1582-1607)**, which `SteeringForceJob` code never enters | **MISSING CAVEAT** (misattribution) |
| 4 | §5 ("잔차 ≤0.34%") vs §8.8 preamble ("§5의 세그먼트 의미론은 이 데이터셋에도 **그대로** 적용된다") | residual ≤0.34% | Holds for step1 only (max 0.337%). GPU dataset: `FollowerSteer` residual **0.626 / 0.634 / 0.835%** @5k and 0.143/0.131/0.189% @10k; `NeutralMove` 0.051–0.093%. A reader carrying §5's bound into §8 as invited is wrong by 2.5x | **INCONSISTENT** |
| 5 | §8.6 table | `*Present` 합 SMR = **13.5750**, components 8.8657 + 4.7140 | 8.8657 + 4.7140 = **13.5797**. 13.5750 is median-of-sums (r1), components are per-segment medians (r2 for NeutralPresent). Two conventions in one table, unflagged; Δ 0.0047 ms (0.03%). Same at 5k: 3.6895 (MoS) vs 3.6729 (SoM), Δ 0.45%. §8.7's breakeven correctly uses 13.5750 throughout, so no downstream error | **INCONSISTENT** (convention) |
| 6 | §8.3 proof table + reproduce block | "정확히 **15008건**… 재현: `grep -c 'Destroy may not be called from edit mode' &lt;logfile&gt;`"; also the device-line row and the `[CrowdRenderer]` 0-count row | 15008 = 5004 + 10004 arithmetically ✓, but **no log is tracked** — `-logFile` went to volatile scratchpad (§8.2 says so, §8.3 does not). The single most load-bearing interpretive fact about the six raw files — *which mode each ran in* — is unverifiable from tracked artifacts; the harness prints no `_gpuRenderActive` line (MANIFEST admits this) and header line 7 actively contradicts it. Same applies to §2/§7.2/§8.2's "확인: N개 로그 모두 …" and "종료 코드 0 / LogError 0건" | **UNREPRODUCIBLE** (presented with a reproduce command) |
| 7 | §7.3 (L207) | "**트리 버전을 런마다 증명했다** … 재현: `git grep -c -F 'if (!_gpuRenderActive)' &lt;rev&gt;`" | The `git grep` reproduces ✓ (`ff60d39`=7, `dcb3bfb`=1, also `f9c1cdc`=7, HEAD=7) but it proves what the **committed revisions** contain, **not** what the working tree contained during each of the four runs. The per-run proof rests on session state that is gone | **UNREPRODUCIBLE** (as presented) |
| 8 | Raw files (all six), header line 1 | MANIFEST §8 warns only that **header line 7** lies, and only for the gpupath trio | Header **line 1** is also a hard-coded literal (`CrowdProfileHarness.cs:393` `"# Phase C 단계 0 baseline profile"`) and mislabels **both** datasets (step1 = SIM_OPT serial-vs-job split; gpupath = GPU re-measurement). The step1 trio carries **no self-description warning at all** in MANIFEST | **MISSING CAVEAT** |
| 9 | Raw files (all six), SUMMARY columns | `moved_agents=3`, `leader_disp_m=0` in every row at both scales — never mentioned anywhere in MANIFEST | These are the harness's own movement self-check (`CrowdProfileHarness.cs:297-306`, comment "edit 모드 CC.Move 동작 확인용") reporting that **3 of 5004/10004 agents moved and the player leader did not move at all** over 1720 ticks. Cause: the profile harness never calls `SetPlayerHeading`, so `moving = t != PlayerTeam \|\| _playerHasHeading` is false for the player (`CrowdRoot.cs:1180`), and in SDF mode follower/neutral transforms are written only by `RenderInterpolate` (:747/:755), which this harness never calls. Benign, but a reader can only read it as "the sim did nothing" | **MISSING CAVEAT** |
| 10 | §8.7 (L473) | "−12.83 ms/tick … 10k `*Present` 합계 13.58 ms의 **약 94%**", explained away as amortization only (§7.7 c/d) | 12.826/13.575 = 94.48% ✓, but the ratio spans **two configurations**: numerator is play-mode, `SeparationVisitBudget=48`, player driven with a mid-warmup heading flip (`CrowdPerfHarnessP95.cs:290/:299/:317`); denominator is edit-mode, budget **0**, player stationary. §7.7-h explicitly lists that same −12.83 among the absolutes invalidated by the budget override. §8.7 downgrades 94% to "상한 쪽 판독" but never says it is cross-configuration | **SCOPE / MISSING CAVEAT** |
| 11 | §3 (L86) | "배경 프로세스 활동 **3.16코어**(12논리프로세서 평균 총 CPU **31.7%**)" … "**1.28코어**(평균 **15.9%**)" | Cannot both be right: 3.16/12 = **26.3%** (not 31.7%); 1.28/12 = **10.7%** (not 15.9%). Ratios of the mismatch differ (1.20x vs 1.49x), so it is not one consistent unit error. Unverifiable either way (live host reading); cosmetic to every verdict | **WRONG NUMBER** |
| 12 | §8.4 (L404) | "세 런의 부하는 §3(8.3~19.4%)과 같은 대역이지만 **§3보다 좁지 않고 더 촘촘하다**(10.4~16.9%)" | Self-contradictory, and the data says narrower: §3 band 11.1 pp, §8.4 band 6.5 pp | **INCONSISTENT** (wording) |
| 13 | Head warning (L10) / §7.1 (L173) | "`SeparationVisitBudget = 48`은 하네스가 CSV 1행 헤더에 **인쇄한다**" / "§7의 원시 CSV 1행만 `SeparationVisitBudget=48`을 인쇄한다" | That CSV line is a **hard-coded literal** (`CrowdPerfHarnessP95.cs:96`), not a runtime readout of the effective value — the same class of self-description hazard §8 warns about. The real evidence is `:290` (which MANIFEST does cite) | **MISSING CAVEAT** (minor) |
| 14 | §8.7 (L457) | "`SIM_OPT_10K_PLAN.md` §2 — 이후 삭제, 히스토리에서 복구 가능" | Claim is **true and I verified it**: the 86.1% breakeven + "86.1%는 비현실적" is in §2 of that file at `ff60d39` (committed 02:44:49), i.e. genuinely **before** the §8 runs (03:55–03:59) and before the §7 CSVs (02:48). But no rev is named; recovery requires knowing `0733deb` deleted it (violates CLAUDE.md §1.1 Rule 4 "cite by literal identifier") | **UNREPRODUCIBLE** (pointer only) |

### Verdicts — each recomputed against its own recorded rule

- **§4 headless verdict (T1b)** — follows. 5k: diff 3.8669 / spread 0.0786 = **49.20x**; 10k: diff 11.6897 / spread 0.1967 = **59.43x**. Rule 5 not triggered. Forward pointer to §8.5 present (L120). ✓
- **§8.5 GPU-path verdict** — follows. 10k: diff 0.1369 / spread 1.1285 = **0.1213x → 판정 불가** ✓; 5k: 1.1967 / 0.3770 = **3.174x → T1b** ✓. Sign flip only in r3 ✓, variance concentrated in the parallel sums (0.7419 vs 0.3866 = 1.92x) ✓ — and that bias favours T2, which still did not win, exactly as §8.8-e argues. ✓
- **§7.6 T1a A/B verdict** — follows for 8 of 9 rows; `render_median_ms` label is defect 2. Headline `simtick_median_ms` −12.826 ms (23.113→10.287, −55.49%), spread 1.606, **7.986x** ✓.

### Reading caveats (item 3) — all four verified against source, not prose

| Caveat | Verdict |
|---|---|
| `*JobWait` inclusive inside `CCMove` | **CORRECT and complete.** `CrowdRoot.cs:1390-1394` (follower) and `:1608-1612` (neutral) are strictly `Begin(CCMove)→Begin(JobWait)→Complete()→End(JobWait)→End(CCMove)`. `CrowdSimProfiler.cs:21` says the same |
| `CCMove` aggregates five sites | **CORRECT, line-exact.** `:1185` leader, `:1390` follower SDF wait, `:1479` follower CC fallback, `:1608` neutral SDF wait, `:1657` neutral CC fallback — grep returns exactly those five |
| Sub-segments ≠ parent, residual right | **Numbers correct** (99.663–99.948% / 99.973–99.978%); **attribution wrong** (defects 3, 4) |
| CSV `simtick_median_ms` neither median nor SimTick-only | **CORRECT.** `:413` `totalSimMs/totalSteps` (arithmetic mean per 0.02 s step) vs real `Percentile(…,0.50)` at `:409`/`:412`; timed span `:364-367` wraps all of `StepSim`, which calls `RenderInterpolate(alpha)` once per frame at `:472`. Also correct by omission: `CrowdProfileHarness`'s own `simtick_median_ms` **is** a true median of SimTick-only (`:275` `Percentile(perTick,0.50)` around `crowd.SimTick(Dt)` alone) — MANIFEST never conflates the two |
| Both perf harnesses force budget 48, shipped asset is 0 | **CORRECT.** `CrowdPerfHarness.cs:235`, `CrowdPerfHarnessP95.cs:290` = `SetSepBudget(cfg, 48)`; `GameConfig.asset:45` = `SeparationVisitBudget: 0` at `fdf909d`/`ff60d39`/`f9c1cdc`/HEAD. History: `79db659` introduced the field at 48 (field was **absent** before — effective 0, so "0→48" is materially right if not literally an edit), `4adb502` set 48→0, no later change. Semantics correct: `SteeringForceJob.cs:100` `capped = SepBudget &gt; 0`, `:144` `matched &gt;= SepBudget → stop` → budget 0 = full enumeration, harness does **less** work in dense regions ✓. `useGpuCrowdRenderer=true` forcing (`:239`/`:294`) is indeed redundant — asset already `1` ✓ |

### Provenance (item 4)

All 7 cited commits resolve, all are ancestors of HEAD `d136350`, and all say what MANIFEST claims. All flags exist in the parsers (`-profileOut/-profileLabel/-profileScales/-profileCounters` at `CrowdProfileHarness.cs:56-59`; `-perfOut/-perfCounts` at `CrowdPerfHarnessP95.cs:61-62`; `-oracleSepBudget` at `CrowdOracleHarness.cs:62`), both `RunFromBatch` entry points exist, and each command does write the named file. `git diff dcb3bfb fdf909d` is exactly 1 file / 6 gates added / 2 dead `Human human = …` locals removed, as claimed. `git diff fdf909d ff60d39 -- CrowdRoot.cs` and `git diff fdf909d f9c1cdc -- Assets/` are both empty, as claimed. The commit timeline is consistent with every raw-file timestamp (step1 02:26–02:31 under `fdf909d`; CSVs 02:48–02:53 under `ff60d39` committed 02:44:49; gpupath 03:55–03:59 under `f9c1cdc` committed 03:44:47). `Human.prefab` = **45** Transforms ✓. `fdf909d`'s message does record "+66% parallel vs +8~13% serial" ✓.

### Verdict on reliability

**The recorded measurements can be relied on as they stand.** Every number reproduces to the last printed digit, every verdict except one table label follows from its own pre-stated rule, and no defect moves any verdict. The residual risk is concentrated in two places: (a) **defect 1** is a genuine wrong statement about where a cost lands in the segment tree — it must be corrected because it is the kind of claim later work would cite; (b) **defects 6/7** mean the *mode assignment* of the six raw files (SMR vs GPU) and the per-run tree state are, today, taken on trust — the evidence was in logs that were never committed, and one raw-file header actively asserts the opposite.

**Figures recomputed: ~224. Defects found: 14 (1 wrong claim, 1 wrong verdict label, 1 wrong number, 2 unreproducible-as-presented, 1 pointer-only, 5 missing caveats, 3 inconsistencies). Arithmetic errors in the measurement figures: 0.**

## Changed files

None — read-only verification. Two scratch scripts written to the session scratchpad only (`recompute.py`, `recompute_ab.py`).

## Verification commands and results

Scripts: `C:\Users\gyro1\AppData\Local\Temp\claude\D--UNITY-UNITY-PROJECT-ActionFitPro-AF-CrowdCity\7be25ba5-e341-4238-8642-5da87b3a7962\scratchpad\recompute.py` and `recompute_ab.py` (re-runnable with `python &lt;path&gt;`).

**Serial/JobWait sums (median-of-sums), verbatim output:**
```
step1(SMR) @5000 : serial ['4.3902','4.3492','4.4117'] med 4.3902 | job ['0.5217','0.5233','0.5378'] med 0.5233
                   serial%=68.26 job%=8.14 diff=3.8669 spread=0.0786 ratio=49.1972
step1(SMR) @10000: serial ['14.4744','14.6107','14.4585'] med 14.4744 | job ['2.8001','2.7847','2.7556'] med 2.7847
                   serial%=70.41 job%=13.55 diff=11.6897 spread=0.1967 ratio=59.4291
gpupath   @5000  : serial ['1.7342','1.7402','1.9641'] med 1.7402 | job ['0.5435','0.5344','0.6815'] med 0.5435
                   serial%=45.37 job%=14.17 diff=1.1967 spread=0.3770 ratio=3.1743
gpupath   @10000 : serial ['3.0472','3.1464','3.4338'] med 3.1464 | job ['2.8221','3.0095','3.5640'] med 3.0095
                   serial%=32.92 job%=31.48 diff=0.1369 spread=1.1285 ratio=0.1213
ALT sum-of-medians ratios: 48.9949 / 59.4555 / 3.1859 / 0.1215  → no verdict change
```

**§7.6 A/B table — all 9 rows, all 6 computed quantities each, tolerance-checked against MANIFEST: `OK` on every row.** e.g. `simtick_median_ms` B 23.916/22.310 → 23.1130; A 10.416/10.158 → 10.2870; Δ −12.8260; −55.49%; spread 1.606; ratio 7.986.

**§7.7 models:** `3.9935×23.113 = 92.301766`; `0.912×10.287 = 9.381744`; diff `82.920022`; render Δ `14.8845`; model `97.804522` → prints 97.805 ✓ (the displayed addends 82.920+14.884 sum to 97.804 — a 0.001 display artifact, not an error); measured gap `−97.4365`; residual `0.368022` = **0.378%** ✓. `12.826/10004 = 1.2821 µs` ✓. `1/3.9935=0.25041`, `1/0.912=1.09649`, diff `0.84608`; `RI ≤ 9.3817` → `ΔSimTick ∈ [−20.7637, −12.8260]` ✓.

**§8.6/§8.7:** `Total −53.50%`, `FollowerPresent −83.44%`, `NeutralPresent −84.01%`, `*Present 합 13.5750→2.2220 = −83.63%`, `FollowerSteer −61.05%`, `NeutralMove −71.32%`, `FollowerJobWait +8.83%`, `NeutralJobWait −8.31%`, `CCMove +8.02%`, `Recruit +3.98%`, `Combat +3.43%`; 5k `3.6895→1.0093 = −72.64%`, `Total −40.36%`. Breakeven `(14.4744−2.7847)/13.5750 = 86.1120%`; drop `83.6317%`; gap `2.4803 pp`; predicted serial `3.1214` vs measured `3.1464`, residual `0.0250 = 0.795%`; `12.826/13.575 = 94.48%`. §8.8-e: `1.1285/0.1967 = 5.737x`, `0.7419/0.3866 = 1.919x`.

**Source-line spot checks (all exact):** `grep -n 'if (!_gpuRenderActive)' CrowdRoot.cs` → 889, **1431, 1441, 1527, 1537, 1632, 1674**; `grep -n 'Seg.CCMove'` → Begin at **1185, 1390, 1479, 1608, 1657**; `Quaternion.Euler(0f` → 1205, **1430, 1440**, 1526, 1536, **1631**, 1673; `Human.cs` 47 / 78 / 79 / 87 as cited; `CrowdRenderer.cs:98-102` silent `return false` on `GraphicsDeviceType.Null`; `DestroyVisualRig` call sites 570/626 under gates 567/623 — only those two; `grep -c '^Transform:' Human.prefab` → **45**.

**Defect 1 evidence:** `grep -rn 'SetTeamMaterial'` → `CrowdRoot.cs:1790,1813,1929,1938` + `Human.cs:30,45`; `grep -n 'CommitOutcomes()'` → definition 1780, **sole call site 712**; `sed -n '705,713p'` shows `Seg.Recruit` wrapping only `_recruitResolver.Resolve` (706), `Seg.Combat` only `_combatResolver.Resolve` (709), `Seg.CommitPublish` wrapping `CommitOutcomes()` (712).

## Risks or blockers

- **Unverifiable by construction (no tracked artifact):** every `-logFile` claim — the 15008 rejection count, the `Direct3D 11.0` device lines, `[CrowdRenderer]` 0-count, `-nographics`/`-quit`/`-profileCounters` occurrence counts, exit codes, Unity build revision, host CPU/RAM header, and all ambient-CPU readings in §3/§7.4/§8.4. I confirmed the *inferences* are sound where code allows (e.g. the null-device branch really is silent, `DestroyVisualRig` really is unreachable when `_gpuRenderActive==false`), but not the observations themselves.
- **r4/r5/r6 six-run statistics (§8.5)** — correctly labelled as scratchpad-only; I could not check `0.13x`, `paired t = 1.82`, or the 5k `t = 81.9`. The committed 3 runs independently give the same conclusions (0.1213x / 3.174x).
- I did not re-run any harness (read-only mandate), so I cannot confirm the raw files are what the recorded commands produce on this machine — only that the flags, code paths, and output writers are consistent with them.
- Cross-doc check was limited to `Docs/WORK_STATE.md`, whose figures (3.15/3.01, 0.12x, 1.74/0.54, 3.17x, 13.58→2.22, −83.6%, 9.56 mean / 11.16 p95, `ΔSimTick ∈ [−20.76,−12.83]`, budget contract) all agree with MANIFEST and with the raw files; both `../../WORK_STATE.md` pointers ("운영 계약", §7) resolve. I did not audit `CROWD_GUIDE.md` or `PROJECT_MAP.md`.

# WORK_STATE — in-flight work state (repo-wide, for cold starts)

> **Verified-against commit: `1fe17e7`** — this document was confirmed true as of that commit. If `git rev-parse --short HEAD` differs from that hash, do not trust this document until you have read the commits in between — the procedure is the "Session start" rule in `CLAUDE.md`/`AGENTS.md`.

> **The question this document answers: "What is in flight, and what must not be broken?"** The reader is AI. It is a repo-wide document, split into **per-area sections**. Structure ("where is it") belongs to `Docs/PROJECT_MAP.md`; explanation and rationale ("why is it built this way") belongs to the per-area human guide — `CLAUDE.md` §1.1 is the authority on the boundary between the three.
> **Everything below is the CrowdCity area section.** The per-area section restructure has not been done yet (not started).

---

## 🔴 Operating contract (2026-07-26) — **read this before any work in this area**

> This section is the **authority** on current state, invariants, gates, and traps. When state changes, update this section even if there is no commit.
> Change **history** (what changed, why, which alternatives were rejected) lives in **git history** (`Docs/CrowdCity/CHANGELOG.md` was deleted — CLAUDE.md §1.1 Rule 1, "history is not a fourth document". The original survives in history; find the commit with `git log --diff-filter=D -- Docs/CrowdCity/CHANGELOG.md` and restore it). History that needs narrating lives in the prose of the human guide [`CROWD_GUIDE.md`](CrowdCity/CROWD_GUIDE.md); measurement history lives in [`Perf/MANIFEST.md`](CrowdCity/Perf/MANIFEST.md). A fact lives in exactly one place — for contracts, that place is this section.

Branch: **`feat/crowd-sim-10k`** (resume on another PC with `git fetch && git checkout feat/crowd-sim-10k && git pull`)

### Current state / next step

Instrumentation, the edit-mode rig fix, and T1a are all done and measured. Three things have been measured: ① the §2 "serial vs job-wait" split (**SMR path**, headless `CrowdProfileHarness`), ② T1a's GPU-path gain (play mode, `CrowdPerfHarnessP95`), and ③ the same split **re-measured on the GPU path** (edit-mode `CrowdProfileHarness`, without `-nographics`).

**The re-decision is finished.** `CrowdProfileHarness` was run 3 times without `-nographics` (with `_gpuRenderActive == true` proven 3 ways — `Perf/MANIFEST.md` §8.3) and the same segments were measured again. Results:

- **10k: inconclusive.** Serial 3.15 (32.9%) vs job-wait 3.01 (31.5%), difference/spread = **0.12×**. Extending to 6 runs gives paired t = 1.82 (df=5, critical 2.571), not significant — but the 3 supplementary runs (r4/r5/r6) are **not in the repo** (the ⚠️ warning in `Perf/MANIFEST.md` §8.5). The verdict is the same from the 3 committed runs alone.
- **5k: T1b stands.** Serial 1.74 vs job-wait 0.54, **difference/spread 3.17×** (not the 3.22 ratio of the two values — `Perf/MANIFEST.md` §8.5).
- **"So, T2 then" is also wrong.** Inconclusive means neither can be ranked above the other. On top of that, the variance in this dataset is concentrated in the parallel segment (job-wait spread is 1.9× the serial one), which **biases the reading in T2's favour** — and T2 still did not come out ahead.
- The first verdict ("T1b confirmed") **was correct on that data.** The catch is that most of its basis was writes T1a had already removed — headless is the SMR path (`CrowdRenderer.Init` returns `false` on a null graphics device — `CrowdRenderer.cs:98`–`:102`), so `*Present` included those writes. With that share gone on the GPU path, the two remaining sums came out level: `*Present` total 13.58 → **2.22 ms (−83.6%)**.
- The evidence, environment, reading caveats, and the verdict text all live in [`Perf/MANIFEST.md`](CrowdCity/Perf/MANIFEST.md) §8 (especially §8.5 verdict, §8.7 break-even comparison, §8.8 reading caveats). Not repeated here.

**🔻 The premise has changed — the next target does not come out of 10k.**

This branch's optimization plan was built **on the premise that "10k is the problem"** (`SIM_OPT_10K_PLAN.md`, which carried that premise, was deleted; the specs worth keeping moved to §9). That premise is broken. **T1a alone** brought GPU-path 10k to **9.56 ms/tick** (mean; p95 11.16), which is **about 48%** (p95 about 56%) of the 20 ms tick period of the 50Hz fixed step (`GameplayRoot.FixedStepSeconds = 0.02f` — `GameplayRoot.cs:12`). That 20 ms is **a structural fact about loop cadence**, not a performance target or an acceptance threshold (§7).

- **The next target must be re-derived from what breaks first at 20k–50k.** Do not pick it by 10k segment share — at that point the two candidates are level, and the ranking of two level values is decided by noise.
- **No target has been chosen yet. It is open.** What is needed is not a more precise 10k measurement but **a different discriminator** (scaling exponent / parallel-segment behaviour on a real device with fewer worker cores / render tier ceiling). **None of those three axes has been measured yet.** Do not pick by segment share.
- **The old rationale `T2 is deprioritized — 13.5% ceiling` is void.** That was an SMR-path number; on the GPU path job-wait is **31.5%** of Total. This does not mean T2 won — it means that deprioritization argument must not be reused.

The raw results under `Docs/Audit/2026-07-26/` were a temporary work list, not a fourth document (`CLAUDE.md` §1.1), so they were deleted — read them with `git show ecdfd45 -- Docs/Audit/2026-07-26/`.

**If you pull it back out of history, one trap: that report is single-agent output, so it is not trusted input.** Every item was re-verified against a primary source (`.cs` / `.asset` / raw `Perf/` files / `git`) before being applied, and findings the re-verification refuted were not applied — round-2 cross-verification likewise confirmed 22 and refuted 7. The report's prose totals also disagree with its own tables (the tables are authoritative).

**2 items deliberately left here even though the boundary rules put them outside this document's remit** (they have no destination, or moving them destroys the fact — do not try to move them again):
- The `14.86` commit-lineage account — its designated destination `CROWD_GUIDE.md` §15 already carries the **lesson** (an old document is confidently wrong), but what remains here is the **evidence** for a live correction, and per `CLAUDE.md` §1.1 Rule 2 an AI may not read the human guide as a source of fact.
- The §9.6 render tier table — its designated destination `CROWD_GUIDE.md` forbids numeric tables and invented targets in its own §0/§16 and explicitly disclaims ownership of this verdict in §10. Moving it would break the destination document's own contract.

### Invariants that must not be broken

| Invariant | Location | Reason |
|---|---|---|
| **All 7** `_visualYaw[…] =` writes stay **outside the `_gpuRenderActive` gate** | `CrowdRoot.cs:1205, 1430, 1440, 1526, 1536, 1631, 1673` | Yaw input for the GPU path. Gate them and crowd facing freezes at its spawn value. The 7 sites are exclusive branches (two `speed>0.001f` if/else pairs + the `sdfActive` branch), so no single execution passes through all of them — the point is that **none of them may sit behind the render flag** |
| **All 5** `_visualSpeed01[…] =` writes stay **outside the `_gpuRenderActive` gate** | `CrowdRoot.cs:1204, 1427, 1523, 1630, 1672` | Integration input for the GPU animation phase (`_phase01`). Exclusivity here comes only from the `sdfActive` branch (`:1427`/`:1523` one pair, `:1630`/`:1672` the other) — unrelated to the `speed>0.001f` if/else of the yaw row above |
| Leader `SetHeadingAndSpeed` **must not be gated** | `CrowdRoot.cs:1206` | The S4b2 contract keeps the leader transform live. With ≤4 leaders (`LeaderMove` = **0.09%** of Total — **SMR path, 10k**; GPU path 10k is 0.21%) there is no gain either |
| Keep the 2 yaw-hold reads for the stopped case | `CrowdRoot.cs:1439`, `:1535` | `float headingDeg = _visualYaw[index];` |
| `*JobWait` is an **inclusive nesting** inside `CCMove` | `CrowdRoot.cs:1390`–`:1394`, `:1608`–`:1612` | Do not sum them, do not double-subtract |
| All three Burst jobs carry `[BurstCompile(FloatMode = FloatMode.Strict, FloatPrecision = FloatPrecision.Standard)]` | `SteeringForceJob.cs:16`, `FollowerSdfMoveJob.cs:20`, `NeutralSdfMoveJob.cs:21` | Determinism contract. FastMath or the default FloatMode permit reassociation and approximation, which breaks the oracle byte-identical gate (spec text in §9.2) |
| Combat direction and budget are computed **only from the tick-start counts (`_startCounts`)** | snapshot `CombatResolver.cs:239-241`·`:261` → consumed at `:353`, `:387`, `:416`, `:547`. Pair enumeration is the fixed nested loops `:336-338`, `:412-414` | **Permutation invariance of the pair-enumeration order comes from this rule alone.** The enumeration loops are fixed, so there is no room to build a permutation, and what the dedicated test actually shuffles is only agent insertion order (`CombatResolverTests.cs:302`) → use mid-tick adjusted counts and **the tests still pass while this invariance silently disappears** (rationale in `CrowdCity/CROWD_GUIDE.md` §9) |
| The render-path gate is **pixel tolerance**, not PNG MD5 | gate section below | Screenshot harness output is not byte-reproducible |
| EventBus has **exactly 2 event types** (`CrowdCountChangedEvent`, `CrowdEliminatedEvent`) | payloads `Crowd/Contracts/Events/CrowdEvents.cs:6`, `:34` | The sanctioned exception for using the static `EventManager` was approved with "exactly 2 · boundary objects only" as its impact scope. Adding a type voids that exception's basis (rationale in `CrowdCity/CROWD_GUIDE.md` §11) |
| The publisher is **`CrowdRoot` alone**, and subscribers are **boundary objects only** | publish `CrowdRoot.cs:333-334` acquires → published only inside `PublishTickEvents` · subscribe `GameSession.cs:61-62`, `HudRoot.cs:168-169`, `CameraRoot.cs:184-185` (+ editor harness `CrowdOracleHarness.cs:231-232`) | Attaching leaves (`Human`, labels, markers) to the bus is a CLAUDE.md §13 forbidden pattern. Every `Subscribe` must have an `Unsubscribe` partner in the same lifecycle |

### Verification gates

- **Oracle byte-identical gate (simulation changes).** `CrowdOracleHarness`, n=2000 seed=12345, 1000 ticks, snapshot md5 **`4F79282EB20A79023B45F2EB2DE5271B`**. Any change whose simulation result must be unchanged passes only if this value is byte-identical (compare the events/summary CSVs as well; `phaseC_oracle_determinism.txt` contains `DateTime.Now` and is therefore not a comparison target — `CrowdOracleHarness.cs:383`). **This md5 has a committed artifact: [`Perf/oracle_baseline_n2000_s12345.md`](CrowdCity/Perf/oracle_baseline_n2000_s12345.md)** (added by `fa6eae7`). It records the new-machine procedure below run **twice**, independently, in Unity batchmode at `4ee8466` (n=2000, seed=12345, 1000 ticks, no override argument) — both runs produced this md5, and because verify was ON the harness re-ran the same combo inside each run, so **four simulations in total** came out byte-identical. The summary/events CSV md5s matched across the two runs as well and are recorded there. ⚠️ **That artifact is a checksum witness, not the snapshot itself** — `phaseC_oracle_snapshot_n2000_s12345.bin` is 18,076,104 bytes (about 17.2 MB) and is deliberately left untracked (so are the two CSVs), so re-verification means **re-running the harness and comparing checksums**, never diffing a stored file.
- **New-machine precondition — first confirm the oracle md5 reproduces on this machine.** If this machine has never run the gate above, then **before relying on any byte-identical result**, run the gate once as-is and compare the md5: `Unity.exe -batchmode -nographics -projectPath <proj> -quit -executeMethod CrowdOracleHarness.RunFromBatch -oracleOut <dir> -oracleTicks 1000 -oracleScales 2000 -oracleSeeds 12345` → the md5 of the generated `phaseC_oracle_snapshot_n2000_s12345.bin` must be `4F79282EB20A79023B45F2EB2DE5271B`. If it does not reproduce, every downstream byte-identical gate is void — do not keep going on top of checks that "pass but mean nothing"; **stop work**.
- **Render gate (render-path changes).** **≤100 pixels differing AND max channel delta ≤8**, plus a visual check. **No PNG MD5** — `CrowdShotHarness` output is not byte-reproducible (re-running with identical arguments differs by 3–16 pixels out of 921,600 per image, max delta ≤4, including the untouched `smr_off.png`). Separation from a real change: 35,587–123,161 pixels / max delta 171–184 (about 2,200× the pixel count, about 43× the delta). ⚠️ These pixel figures exist **only as prose records; the raw artifacts were never committed** — keep the thresholds (≤100 pixels / delta ≤8) as they are, but read the figures themselves as not reproducible. All that the source confirms is the frame size 921,600 = 1280×720 (`CrowdShotHarness.cs:25-26`). Also, `CrowdFlatRateShotHarness` writes the same 1280×720 PNGs (`CrowdFlatRateShotHarness.cs:27-28`), but **its noise floor has never been established**.
- **WallSdf integrity gate (any change that re-bakes or touches the wall SDF artifact).** `ResourcePathValidator` (menu `AF/CrowdCity/Validate Resource Paths` — `Game/Editor/ResourcePathValidator.cs:82`; batch `ResourcePathValidator.ValidateBatch`, also reachable from `GameSceneValidator.cs:75`·`:93-104`) FAILs unless all four hold for `Assets/@Project/City/Generated/WallSdf.asset`: ① `WallFieldBaker.Crc32(payload.bytes) == payloadCrc` (currently `2120840027` = `0x7E69735B`); ② `payload.bytes.Length == CellCount * sizeof(float)` (currently `3392768`); ③ the **seven interpretation values the runtime copies into `WallField`** (`WallField.cs:84-91`) equal the constants pinned at `ResourcePathValidator.cs:55-61` — `cols 928`, `rows 914`, `cellSize 0.1`, `originX -46.399998`, `originZ -45.7`, `maxDistance 2.5`, `bilinearBias 0.05`; ④ `schemaVersion` equals the code constant `WallSdfAsset.CurrentSchemaVersion` (`WallSdfAsset.cs:20` — both `1`). **A re-bake that changes any of the seven in ③ must update those constants in the same commit**, otherwise the gate fails by design, and the pin-mismatch message names that remedy in the log. ④ carries no such maintenance: its expected value is a code constant, not a value transcribed from the `.asset`. Regression coverage: `Game/Editor/ResourcePathValidatorWallSdfTests.cs` (EditMode — CRC check vector, pass on the shipping payload, and a same-length single-byte mutation that must fail ① only).
  - **Why ③ exists, and why the pre-existing two checks do not cover it.** `payloadCrc` covers **payload bytes only** (`WallFieldBaker.cs:383-385`), never the `.asset` metadata — and the `.asset` is the text YAML, the merge-exposed half Unity rewrites. Swapping `cols`/`rows` (928 ↔ 914) leaves `CellCount` and the byte length identical, so the runtime length check (`WallField.cs:66-70`) and the CRC both pass while `Phi` silently re-strides the same row-major blob (`WallField.cs:182-190`). `bilinearBias` is in neither `FnvParams` (`WallFieldBaker.cs:249`·`:624-628`) nor the payload, yet `WallSolver` consumes it (`WallSolver.cs:65-67`) — before this gate nothing in the project validated it at all.
  - **This is author-time detection only — a runtime CRC check in `WallField.Load` was considered and rejected.** `WallSdf` is a build-time, in-package, non-user-writable generated artifact serialized into the prefab graph (no Addressables anywhere in `Packages/manifest.json`), so author-time strictly dominates: here the remedy is "re-bake", whereas on a player device the only remedy left is the `CC.Move` fallback — the very bottleneck the SDF exists to remove. The editor CRC also covers **all** bytes regardless of what the simulation samples. `WallFieldValidator` keeps the complementary half (`SourceHash` vs the live city, re-bake determinism — `WallFieldValidator.cs:92`·`:96`). The two are **largely complementary but not disjoint**: `cellSize` and `maxDistance` are covered twice, because `WallFieldValidator.cs:87-88` restores them from the asset into its re-bake params and both enter `sourceHash` through `FnvParams` (`WallFieldBaker.cs:249`·`:624-628`), so corrupting either already breaks `matchesAsset` (`WallFieldValidator.cs:96`). The rest of ③ is covered by this gate alone — the `cols`/`rows` swap, `originX`/`originZ` (the baker recomputes those from the live city, so the asset's stored values reach neither side of `matchesAsset`), `bilinearBias`, and the on-disk payload bytes (`WallFieldValidator` compares its own re-bake's CRC against the stored `payloadCrc` and never reads the `.bytes` file). The double coverage costs nothing, and the reason is the point: `WallFieldValidator` needs the GameScene open plus two full re-bakes, while this gate is asset-only and runs on every validator pass.
  - **What ④ closed, and what stays unpinned.** `schemaVersion` — the field that says whether the layout may be read the way this code reads it — was validated nowhere: only printed (`WallFieldValidator.cs:82`), written at bake time (`WallSdfAsset.cs:103`), and ignored by `WallField.Load`. ④ closes it. Still unpinned by this gate: `sourceHash` and `yMin`, both of which `WallFieldValidator` covers (`sourceHash` compared directly at `:96`; `yMin` restored into `BandBottomOffset` at `:89`, from where it enters `FnvParams`), and `yMax`/`colliderCount`/`triangleCount`, which are bake-provenance metadata that no runtime code reads — `WallField.Load` copies only the seven values in ③.
- **TMP asset side effect.** Every headless harness run dirties `Assets/TextMesh Pro/Resources/Fonts & Materials/LiberationSans SDF - Fallback.asset` (about 10 insertions / 911 deletions — the diff size is a prose record and is not confirmed by the repo; the side effect itself and the countermeasure are valid). Revert it before committing. **No `git add -A`** — always name the paths.
- **Take timing measurements on a quiet machine.** Read host-wide CPU right before a run and **postpone if it exceeds about 30%.** With a foreground game holding one core, the split was **+66% on the parallel segment** vs **+8–13% on the serial segments** — background load concentrates in the parallel segment. That observation comes from a **discarded, uncommitted run**, and the harness/mode was **the GPU-path before/after pair of the play-mode `CrowdPerfHarnessP95`** (source: the `fdf909d` commit message — `Perf/MANIFEST.md` §3 cites it as the basis for the 30% threshold).
- **Do not invent performance target numbers** (§7 below). State gates as "an improvement larger than the run-to-run spread", and report a delta inside the spread as **inconclusive**.
- **Harnesses override config — first work out which numbers belong to the shipping configuration.**
  - **`CrowdPerfHarness` / `CrowdPerfHarnessP95` unconditionally force `SeparationVisitBudget = 48` on their cloned config** (`CrowdPerfHarness.cs:235`, `CrowdPerfHarnessP95.cs:290`). Shipping `Assets/@Project/Game/GameConfig.asset` is **0** (`79db659` went 0→48; `4adb502` reverted 48→0 because of jitter in dense clusters — only the harnesses' hardcoded 48 remains). 0 = visit every neighbour, 48 = stop scanning after the 48th matching candidate (`SteeringForceJob.cs:100`, `:144`), so **the two harnesses do less work than the shipping build in dense regions** → their absolute numbers are **not the numbers of the simulation the game runs**. In contrast, **an A/B comparing two arms of the same harness is valid** — both arms share the same override, so the delta survives and only the absolute value dies. The two harnesses forcing `useGpuCrowdRenderer = true` (`:239`, `:294`) is **not a divergence** — the shipping asset is already `1`, so it merely pins the same value.
  - **`CrowdProfileHarness` / `CrowdOracleHarness` do not force it.** They override only on runs given `-profileSepBudget`/`-oracleSepBudget`; the default is the config value as-is (`CrowdProfileHarness.cs:78`·`:192`, `CrowdOracleHarness.cs:49`·`:193`). The `UseSdfSolver = true` in both harnesses (`:201`, `:215`) also matches the prefab-serialized value (`CrowdRoot.prefab` `_useSdfSolver: 1`), so it is not a divergence either. The upshot: `Perf/MANIFEST.md` **§1–§6 and §8 were measured with the shipping budget (0)**, and **only §7 with 48**.

### Traps

- **On the GPU path, follower/neutral `transform.rotation` is permanently stale** (rotation joined a position that was already stale — pre-existing comment `CrowdRoot.cs:740`). Any future feature that reads follower/neutral `transform.rotation` or `.forward` will silently get the spawn-time value.
- **`fdf909d` (T1a) is coupled to `dcb3bfb`.** Reverting one requires reverting the other — before `dcb3bfb`, the facing of the surviving edit-mode ghosts freezes, which is a real visual regression.
- **Unity refuses deferred `Object.Destroy` in edit mode.** Every edit-mode harness (`CrowdShotHarness`, `CrowdProfileHarness`, `CrowdOracleHarness`) keeps seeing objects the runtime code believes it destroyed. `GetComponentsInChildren<SkinnedMeshRenderer>(true)` counts inactive objects too, so an `smr == 0` assertion still fails in edit mode. Play-mode performance harnesses yield every frame and are unaffected.
- **The `CrowdPerfHarnessP95` CSV column `simtick_median_ms` is neither a median nor `SimTick`-only.** ① `CrowdPerfHarnessP95.cs:413` computes `totalSimMs / totalSteps` (an arithmetic mean per 0.02 s step) — the `full_median`/`render_median` on the same row are real `Percentile(…, 0.50)` (`:409`, `:412`). ② The measured region (`:364-367`) wraps the whole of `StepSim()`, and `StepSim` calls `RenderInterpolate(alpha)` once per frame (`:472`) → `simtick = SimTick + RenderInterpolate / steps-per-frame`. The amortizing divisor differs per arm, so **arms cannot be compared on pure `SimTick`**. `RenderInterpolate` has no separate instrumentation, so pure `SimTick` is **bounded but not measured** (T1a: `ΔSimTick ∈ [−20.76, −12.83]` ms/tick). Derivation in [`Perf/MANIFEST.md`](CrowdCity/Perf/MANIFEST.md) §7.7 (c)·(d).

---

## 🔄 Earlier state update (2026-07-19) — crowd performance session results + remaining work

> The operating contract section above is the authority on current state. This section and the 2026-07-17 marker/gates/plan below are kept as background and detail reference.

### Done (committed and pushed)
- Burst hot jobs enabled (FloatMode.Strict): 10k SimTick 22.95→14.04ms (−38.8%) — ⚠️ **the harness, the mode, and the raw artifacts are all missing.** `433b5fe` was the first to write it down, and no file under `Perf/` and nothing in `MANIFEST.md` holds matching data. The two committed datasets (SMR 20.56 / GPU 9.56) are post-T1a code and therefore not directly comparable, which also leaves no way to trace back which mode either endpoint belongs to → **do not re-cite this delta as evidence** (that Burst is enabled at all is confirmed in code)
- Config defaults: useGpuCrowdRenderer=ON, CombatFlatConvertRate=ON, SeparationVisitBudget=0 (cap reverted — caused jitter in dense clusters), neutralCount=3000 (the "60fps in the editor" basis for this was retracted under "Key status" below), ConvertPerSecond=100

### Key status

> **Every performance number in this section is written down together with the mode/harness it was measured on.** This project has two datasets for the same segments (SMR path vs GPU path) and the values differ by more than twofold. Strip the mode and the number is guaranteed to be misread.

- **10k SimTick (2026-07-26, edit-mode `CrowdProfileHarness`, SDF ON, seed=12345, 3 runs):**
  - **GPU path** (without `-nographics`, `_gpuRenderActive == true`) — **9.56 ms/tick**, p95 11.16
  - **SMR path** (`-nographics`, `_gpuRenderActive == false`) — **20.56 ms/tick**, p95 22.67
  - **The two columns aggregate on different axes.** ms/tick is the 3-run median of the segment `Total` **mean**, while p95 is the 3-run median of the SUMMARY column **`simtick_p95_ms`** (not a segment mean — `Perf/MANIFEST.md` §5 warns against mixing the two statistics). The two p95 values are not in MANIFEST; they exist only in the raw files (`Perf/simopt10k_gpupath_r{1,2,3}.txt` / `Perf/simopt10k_step1_r{1,2,3}.txt`).
  - The tick period of the 50Hz fixed step is 20 ms (`GameplayRoot.FixedStepSeconds = 0.02f` — `GameplayRoot.cs:12`) — **a structural fact about loop cadence, not a performance target or an acceptance threshold** (§7). The GPU path is about 48% of that period (p95 about 56%); the SMR path exceeds it.
  - Raw output, environment, and reading caveats: `Docs/CrowdCity/Perf/MANIFEST.md` §1–§6 (SMR) / §8 (GPU).
- ~~10k CPU sim solved: 14.86ms/tick, under the 20ms (50Hz) budget.~~ **[Corrected — this number belongs to no mode at all.]** Tracing the origin of `14.86ms` shows it **is not a 10k SimTick measurement.** The **earliest** commit in which `14.86` appears is `5caae09` (2026-07-17) (do not use the **number of commits** it appears in as evidence — raw measurement data contains coincidental matches such as `5000,CCMove,0.5679,14.8674`, and the count grows with every commit that cites the document), and there it refers, on the basis of profiler screenshots (`Docs/Photo/PRO.PNG`, `PRO2.PNG`), to **`GameplayRoot.Update()` self time 14.86 ms = 32.4% of the frame** (that commit message itself says, in Korean, "프레임의 14.86ms(GameplayRoot.Update self)"). Later, `433b5fe` (2026-07-19) transcribed the same number as **"10k … ms/tick"**, and **that very commit separately records the actual 10k SimTick measurement as `22.95→14.04ms` in the same section.** In other words, `Update self` (per frame, folding several ticks together because of `MaxStepsPerFrame`) was relabelled as per-tick. **One clue could not be confirmed** — the actual agent count in those screenshots is not recorded anywhere in the docs, so "measured at a different scale" is an **inference** (§0 of this same document cites the user's Profiler for `Update ~30ms @2000`). What is certain is **that the aggregation axis differs** (frame self vs per-tick), and that alone means this number is none of the entries in the table above.
- ~~Editor 60fps population ceiling ≈ 3400 = render-bound (undecimated 2964-vertex mesh × N), not sim — 2026-07-19 editor play-mode performance harness (forced GPU sync). The harness forces GPU sync so it is conservative → a real build is likely higher.~~ **[Retracted — repo evidence is 0, and a profile inside the repo contradicts it.]** The item is kept so that nobody re-derives this number.
  - **`3400` has no backing artifact whatsoever.** No raw output, CSV, or log exists anywhere under `Docs/CrowdCity/Perf/`, and the commit that first wrote the number down (`433b5fe`, 2026-07-19) records neither which harness nor which commit it was measured on.
  - **`Docs/Photo/PRO3.PNG` (commit `4043475`, 2026-07-20 01:05) contradicts it.** With that commit's shipping configuration (`neutralCount: 3000`, `useGpuCrowdRenderer: 1`, `SeparationVisitBudget: 0`) = 3004 agents, frame CPU is **37.85 ms ≈ 26 fps**, and `PostLateUpdate.UpdateAllRenderers` is **0.00 ms** with no Animator or skinning rows at all (the GPU instancing path). Since 26 fps appears at a population 12% **below** the claimed ceiling, that ceiling cannot be the 60 fps point.
  - **Do not flip "not sim" into "sim-bound" either.** The cost sits inside `GameplayRoot.Update()` (self **19.69 ms = 52.0%**), and that region contains both simulation and **render preparation** (`RenderInterpolate`, O(N) transform preparation, GPU buffer upload). All that was measured is the fact that `PostLateUpdate.UpdateAllRenderers` is 0.00 ms — which means Unity's built-in `Renderer`/SMR layer is ≈0, **not** that main-thread render cost is ≈0. The GPU crowd's buffer uploads (`CrowdRenderer.cs:179`·`:204`) and draw submission (`:188`·`:212`) run inside `RenderInterpolate` (`CrowdRoot.cs:765` → `RenderGpuCrowd` → `:930`) — that is, **inside** the very 19.69 ms under discussion. How that splits between simulation and render preparation is **unmeasured**. Dividing `Update` self by the number of ticks per frame to manufacture a per-tick cost does not hold either (same reason).
  - **Harness bias is a partial explanation, not a reconciliation.** The claim's wording and date match the perf harness line committed on 2026-07-19 (`3bcddf1`), and that whole line forces `SeparationVisitBudget = 48` (shipping is 0 — gate section above), but there is no 0-vs-48 paired measurement and it does not explain a gap of this size. Forced GPU sync makes results **worse**, so it cannot explain why the harness produced a **better** number. ⇒ **The two readings cannot be reconciled without a new paired measurement** — do not try to make both of them true.
  - **PRO3 is not promoted to authority either.** ① 3004 is not printed on screen; it is **inferred** from `neutralCount: 3000` in `4043475` plus the `Loading.IsObjectAvailable` call count **6008 = 2 × 3004**. ② The GPU column is `--ms` (not captured) and `PlayerLoop` is only 82% of the frame, leaving about 6.8 ms unaccounted — the usual cause in play-mode profiling is `EditorLoop`, but a single screenshot cannot rule out a `Gfx.WaitForPresent*` GPU stall. ③ `4043475` predates P2 (`20985dd`), the P3 series (`1ca40d6`–`20cab38`), and T1a (`fdf909d`) in their entirety, so it is not current code either.
  - **The measurement that closes the gap is "Remaining work" item 1** below (Development build + DevHudRoot FPS, measured per population). Do not invent a target number (§7).
- ~~The key lever for raising population = a decimated render mesh (art) — a consequence of the render-bound verdict above, with no measurement of its own.~~ **[Retracted with it — the render-bound verdict that was its only basis has been retracted.]** The decimated mesh is still the task in "Remaining work" item 2, but **there is no basis for calling it the key lever for raising population**. Reviving that claim requires measurement item 1 above first.
- ~~Separation determinism baseline: OFF/Pairwise oracle n=2000 s12345 md5 = 2b3aad7f786004f1185a067bc1b31f4a.~~ **[stale — a pre-Burst value]** The baseline in force now is **`4F79282EB20A79023B45F2EB2DE5271B`** (same n=2000 s12345, 1000 ticks). Burst output is not bit-identical to the Mono oracle, so the baseline was retaken in Stage A (§7 "Burst output is not bit-identical … no direct comparison against the old baseline"). See the gate section above — the citation of that value's committed artifact ([`Perf/oracle_baseline_n2000_s12345.md`](CrowdCity/Perf/oracle_baseline_n2000_s12345.md)) is there too.

### Remaining work (by priority)
1. [user] Development build + DevHudRoot FPS, measuring real frame time per population → the true pipeline 60fps population + a sim-vs-render bottleneck verdict.
2. [art] Decimated render mesh (~200–500 vert) — the mobile shipping gate for the GPU renderer. **The verdict that this is the key lever for raising population was retracted in the retracted item above** — reviving it requires "Remaining work" item 1 first.
3. [deferred until sim becomes the bottleneck] Leader-radial policy: (a) fix bucket → exact cell occupancy count (Codex: hash collisions inflate occ → false positives → stripes), (b) T/gain/tangential tuning.
5. Scope B: the §4 milestone Burst golden baseline (extended snapshot fields, numeric tolerance, player AOT build).
6. Manual checks: DevHudRoot button/FPS behaviour, no popping after chunked spawn, whether pop-in during GPU-path spawning is acceptable (fixable with progressive reveal).
7. Harness cleanup (DestroyImmediate after Shutdown — minor).

### Deliberately deferred MINOR items (raised in MVP cross-verification → accepted and deferred, re-confirmed against code at `c9e61a4`)

The following were raised by CODEX/Claude review back in the MVP era but were **deliberately deferred**, and all of them are **still present in the current code exactly as described**. The reasons for deferring still hold, so read them as "an accepted state", not as "unfinished work". Handle them as follow-up tasks if they ever become necessary.

| Item | Current location (re-confirmed) | Reason for deferring |
|---|---|---|
| `CrowdModel` parallel-list encapsulation (no read-only view) | `CrowdModel.cs:56`, `:59` — `public List<Human> Followers { get; }` / `public List<int> FollowerAgentIndices { get; }`. Get-only auto-properties, so **reference replacement is blocked**, but the collections themselves are still mutable `List<>` | The only mutator is the owner (`CrowdRoot`); no external references. A theoretical risk |
| `CombatResolver` team-ID / `MatchRules` array-length defensive validation | `CombatResolver.cs:258-262` (lower bound only) · `MatchRules.cs:54-56` (no length-consistency check) | Only internal callers exist — an **unreachable scenario** (CLAUDE.md "No error handling for impossible scenarios") |
| `SpatialGrid` extreme-radius / Ground minimum-size guards | `SpatialGrid.cs:190` (early-out on `radius < 0f` only, no upper clamp) · `CrowdRoot.cs:989-992` (no check for area inversion after the 2 m shrink) | Fixed config and a fixed city make this unreachable in practice |
| 3 editor-hygiene items | non-recursive collider cleanup `GameSceneSetup.cs:1012-1026` · validator does not check camera identity `GameSceneValidator.cs:123` · `activeSelf` check on a prefab asset `GameSceneValidator.cs:417` | Editor-only tooling |
| `RivalAiDriver` has no boundary awareness | there is no region/map-boundary term anywhere in `RivalAiDriver.cs` (only flee/chase/neutral density + wall raycast) | Self-corrects via the neutral density vector; no gameplay problem observed |
| `HudRoot` per-event `ToString()` allocation | `HudRoot.cs:780` (`SetCrowdCount`) ← bus handler `:339` | Presentation path; both sides accepted it for MVP |
| `EventManager` `GetInvocationList()` allocation on the editor path | `EventManager.cs:196` (`#if UNITY_EDITOR` branch) | **Off-limits authoritative infrastructure; do not edit** |

### Workflow
- Loop: optimize → raise neutralCount → repeat. Goal = a stable 60fps in the editor. ConvertPerSecond=100 is intended tuning.
- Cross-verification: Codex (gpt-5.6-sol ultra) synchronous with `< /dev/null` ↔ Claude, until consensus.

---

## 🔄 Other-PC resume marker (updated 2026-07-17)
- **Rays (`Physics.Raycast`) are for wall detection, but had a layermask bug, fixed in `5caae09`.** There are only two: `Assets/@Project/Crowd/Scripts/RivalAiDriver.cs:205` (`ProbeClearance` — declared at `:200`, called from `ApplyWallAvoidance`, wall avoidance) and `Assets/@Project/Crowd/Scripts/CrowdRoot.cs:1697` (`RepickWanderHeading` — declared at `:1685`, wall detection for the wander heading). Both are for wall detection, but **they were being fired without a layermask (`Physics.DefaultRaycastLayers`), so Unit (crowd) colliders were being mistaken for walls — a real bug** — the previous marker checked only the rays' *purpose*, never the layermask, and so wrongly concluded "already cleaned up". Excluding the Unit layer from the mask **FIXED** it in `5caae09` (current mask computation `CrowdRoot.cs:240`·`:250`).
- **Crowd-to-crowd (agent-to-agent) tests are not rays; they go through `SpatialGrid.QueryCircle`.** In the current tree there are **4 call sites** (recruit = `RecruitResolver.cs:75`, combat = `CombatResolver.cs:287`·`:487`, AI neutral density = `RivalAiDriver.cs:129`), and **separation is no longer a `QueryCircle` caller** — it reproduces the same enumeration inline inside a Burst job (`SteeringForceJob.cs:104-155`; uncapped-enumeration comment `:91` / cap truncation `:143-148`). The native grid snapshot the job reads is built by `CrowdRoot.cs:1317`. → That said, the 2 wall rays above **really were hitting crowd (Unit)** because of the missing layermask (a misclassification) — **fixed** in `5caae09` by excluding Unit from the mask. Note this misclassification is a correctness issue: the **attribution** of frame time (`Update` self) is **unmeasured** — that self contains both `SimTick` and `RenderInterpolate` (the retracted item in the "Key status" section).
- There is no baseline CSV labelled 'M-sim-0' and the artifacts are labelled `step1_serialsplit_*`, so searching by that name finds nothing.
- ⚠ **Memory (local `~/.claude`) does not sync between PCs.** This document (git-tracked) is the only handover source between PCs.

### 📌 Diagnosis update (2026-07-17, profiler evidence `Docs/Photo/PRO.PNG`, `PRO2.PNG`)
- **Frame hotspot = `GameplayRoot.Update()` self 14.86ms (32.4%)**, spiking to 66ms (15FPS). Actual `Physics.Simulate` (= `PxScene.simulate`) is only 1.53ms and every `PhysX.*` child row is ≤0.47ms → the only thing ruled out is **the physics solver**. The remaining `Update` self holds `SimTick` and `RenderInterpolate` together, so this screenshot cannot settle the attribution between the two (the retracted item in the "Key status" section).
- **Candidate sites (not attribution):** `MaxStepsPerFrame=4` (`GameplayRoot.cs:13`) is an **upper bound** on ticks per frame; the actual execution count has never been recorded. The heavy terms within one tick are [the 5 neighbour-query paths — 4 `SpatialGrid.QueryCircle` call sites (recruit = `RecruitResolver.cs:75` / combat = `CombatResolver.cs:287`·`:487` / AI neutral density = `RivalAiDriver.cs:129`) + separation's inline enumeration inside a Burst job (`SteeringForceJob.cs:104-155`) + per-agent SDF `WallSolver.Resolve` (serial path `CrowdRoot.cs:1723`; the hot path moved to Burst — `FollowerSdfMoveJob.cs:70`·`NeutralSdfMoveJob.cs:67`)]. The custom `CrowdSimProfiler` does not use Unity ProfilerMarkers, so all sub-stages collapse into `GameplayRoot.Update` self, and `RenderInterpolate` — which that instrument does not even target — lands in the same self. → **This is what M-sim-1 (query density capping) and M-sim-2 (Burst) aim at.** **Do not use "M-sim-0 confirmed that SDF move-solve dominates" as evidence** — that baseline has no committed artifact, and the closest committed data (`Perf/simopt10k_step1_r{1,2,3}.txt`, 10k) puts the serial `*Present` sum at about 66% of Total and `CCMove` at about 13.5%. On top of that, with SDF ON `CCMove` is the **parallel job-wait wall clock** of SDF move resolution, not its CPU work, and these files were measured **after** the Burst jobification, so they can neither confirm nor refute a pre-jobification claim.
- **A separate correctness bug — fixed in `5caae09`:** the 2 wall-detection rays (`CrowdRoot.RepickWanderHeading` = `CrowdRoot.cs:1697`, `RivalAiDriver.ProbeClearance` = `RivalAiDriver.cs:205`) fired with `Physics.DefaultRaycastLayers` instead of a layermask, mistaking Unit (crowd) colliders for walls. `Physics.IgnoreLayerCollision(Unit,Unit)` has no effect on raycasts, so disabling unit physics collisions still leaves rays hitting crowd. → Fixed by excluding the Unit layer from the mask (`Physics.DefaultRaycastLayers & ~(1<<unitLayer)`, computed in code with no new serialized field). **This only removes a misclassification and has nothing to do with the 14.86ms** (the attribution of that frame time is itself unmeasured — "Key status" section).

> **What this document is for**: it is the entry point that lets a separate session (with no conversation context) **start crowd-sim CPU optimization work immediately** from this document's CrowdCity section alone. For not-yet-started design specs, **§9 of this document** is the authority (the separate PLAN documents `SIM_OPT_PLAN.md`/`SIM_OPT_10K_PLAN.md` were deleted).
> **Precondition**: start only **after** the user's separate structural refactor is complete. That refactor touches all of `Crowd/Core` + `CrowdRoot`, so this plan is **rebased by responsibility**, not by line.

---

## ⚠️ Pre-start gates (P0 — do not begin unless they pass)
1. **Confirm the refactor is complete**: this plan starts **after** the user's `Crowd/Core` + `CrowdRoot` refactor. Completion **cannot be judged from documents** → get explicit confirmation from the user, or judge by a "completion commit/branch" the user designates. Record the fixed baseline when starting: `git branch --show-current`, `git rev-parse HEAD`. (At `fb14a41`, where this gate was first written down, the refactor was still in progress.)
2. **Authority order**: **code > this document (WORK_STATE) > everything else.** For confirming a contract, **current code is the only truth**, and where the §9 specs of this document disagree with the code, the code is right. (`DESIGN.md`/`INTERFACES.md`/`STATUS.md`, which were pre-Phase-C snapshots, were deleted — the risk of misleading was the actual reason, and the content worth keeping has been moved into this document plus `PROJECT_MAP.md`/`CROWD_GUIDE.md`.)
3. **Artifacts must be tracked commits**: there is precedent for design-round transcripts (`codex_*.txt`) being left untracked (later committed in `fb14a41` → removed from the working tree after the conclusions were relocated; the originals restore with `git show fb14a41:<file>`). The M-sim-0 CSVs, the oracle baseline, and the measurement manifest must be committed on the reference branch (so a clean checkout/clone does not lose them).
4. **Maintain a list of intentional contract changes**: the density cap (behaviour), the RNG streams (seed reproduction), baked CC removal, SDF probes, and so on are intended changes → track them in a separate list and update DESIGN/INTERFACES to match.

---

## 0. One-paragraph context (session-independent)
AF_CrowdCity's CPU frame hotspot is `GameplayRoot.Update` (user Profiler measurement: Update self ~30ms @2000, Ryzen 5600X, editor). The live prefab is `_useSdfSolver:1` (SDF ON, `CC.Move` is fallback-only) and `gpuSkinning` is ON. **However, the split between sim and render preparation inside that self is unmeasured** — the same function holds both `CrowdRoot.SimTick` (several ticks per frame) and `RenderInterpolate` (O(N) render-position preparation, plus buffer upload and draw submission on the GPU path). Neither "it's sim, not render" nor the reverse can be claimed from this data (see the retracted item in the "Key status" section). Prime suspects (code-based hypothesis, **not measured**): combat/recruit `SpatialGrid` neighbour queries superlinear in density + victim sorting O(v²). Goal = a large reduction in SimTick CPU cost, a sim structure that scales to tens of thousands, and preservation of behaviour and determinism. **The render/anim/GameObject stack is not in scope for this work** (the mobile 10k-scale render redesign is a separate track).

## 1. Read first (in order)
1. **§9 of this document (not-yet-started design specs)** — the density capping algorithm, canonical order, `DeterministicRng`, T2/T3a/T3b, the mobile floor conditions. §5 Steps 2–4 below refer to these specs.
2. `CLAUDE.md` (repo root) — mandatory safety rules + **the main/subagent operating model** (§3 below).
3. `Docs/PROJECT_MAP.md` — the structural index ("where is it"). Kernel types, entry points, prefab loading, and event bus coordinates are there.
4. Memory (if present): `crowd-scaleup-architecture.md` (the higher-level decision for this work), `phasec-ccmove-bottleneck.md` (SDF/WallSolver background), `agents-verify-as-unity-senior.md`, `codex-cross-verify-command.md`.

## 2. Target code (may have moved or been renamed by the refactor — re-confirm in the current tree)
- **`Docs/PROJECT_MAP.md` is the authority on the file/type/entry-point coordinates of the kernel, orchestration, harnesses, and the fixed-step loop** — kernel (`Crowd/Core`) §4, Crowd orchestration (`Crowd/Scripts`) §5, verification harnesses (`Game/Editor`) §14, loop and cadence (`GameplayRoot`) §3. The same index is not duplicated here (structure is not this document's remit — `CLAUDE.md` §1.1 Rule 1).
- Branch: **`feat/crowd-sim-10k`** (continued from the earlier `feat/crowd-sdf-perf` — that branch still exists, but work happens only on `feat/crowd-sim-10k`). Audit trail: the design-round artifacts (`codex_burst_*.txt`) are not in the working tree — restore them from history with `git show fb14a41:<file>` (see §8).

## 3. Operating model

- **`CLAUDE.md` / `AGENTS.md` are the authority.** The main-agent-as-manager-only delegation model, the 4-heading subagent report format, one-file-one-owner, the Codex↔Claude cross-verification procedure and its consensus requirement (including reviewer input conditions and the consensus termination condition), and both agents' model, effort, and default `codex exec` invocation form (including sandbox flags, `< /dev/null`, the Bash-tool caveat, and the ban on arbitrary model substitution) — all of it is in that file's "Main/Subagent Operating Model", "Cross-Verification", and "Agent settings" sections. This document does not own workflow contracts (`CLAUDE.md` §1.1 Rule 1 and Rule 3). They are not restated here.
- **Long Codex prompts**: stdin is closed by `< /dev/null`, so the prompt can only come in as an argument — if it is long, write it to a file and pass it as `"$(cat promptfile)"`. (This one item stays here because it is shell usage, not a workflow contract.)
- **Do not invent performance target numbers now** — fix them after the M-sim-0 measurement and a device budget. Every number in this document today is extrapolation.

---

## 4. For the refactoring session (leave these so the rebase goes smoothly)
Keeping the following unbroken during the refactor — or clearly recording it when it changes — makes the sim-opt rebase easy:
1. Preserve the **SimTick stage order contract**: **`PrevPos` snapshot** → restore → heading → leader move → follower/neutral steer → mirror → grid rebuild → recruit → combat → commit → publish. (① The `PrevPos` copy (`CrowdRoot.cs:680`) is an unnumbered preceding stage and comes **before** restore (`:690`) — the movement stages author `buffer.Pos` directly, so code that reads "the previous tick's position" (rival AI, wander raycast origins, a job's own position) must see this snapshot to stay uncontaminated. ② The parallelization premise "neighbours are the previous tick's snapshot" depends on mirror/rebuild coming after steer.)
2. Preserve **AgentId uniqueness + population slot stability** (insertion-order ascending index invariance) — the foundation of the deterministic canonical order.
3. Make the **`SpatialGrid.QueryCircle` boundary** explicit (who calls it: combat per non-neutral + per leader `CombatResolver.cs:287`·`:487`, recruit per neutral `RecruitResolver.cs:75`, AI neutral density `RivalAiDriver.cs:129`. **separation is not a caller** — `SteeringForceJob.cs:104-155` reproduces the same enumeration inline inside the job) — density capping replaces this point. Record it if the signature changes.
4. Preserve **`CrowdSimProfiler`'s Seg enumeration** (the harnesses depend on it). Record it if names or meanings change.
5. Preserve the **oracle snapshot contract** (the positions/team/events CrowdOracleHarness reads) — the reference for re-baseline comparisons.
6. Record in this document, in one line, whether `AgentBuffer` is still SoA or has already moved to NativeArray (it changes the M-sim-2 scope). — **Resolved**: `Docs/PROJECT_MAP.md` §4 records `AgentBuffer` as "SoA, NativeArray".
7. Record the **final locations** of every `System.Random`, `Physics.Raycast`, `Physics.CheckSphere`, and `CharacterController` — the M-sim-3 targets.

## 5. For the execution session (after the refactor — in exactly this order)

### Step 0 — rebase remapping + coverage (mandatory before implementation)
> "rebase" here means **semantically remapping the design onto the current responsibility structure**; it is not permission to `git rebase`, check out, or change branches.

Delegate to subagents and produce, from the current tree:
- **A responsibility mapping table**: for each of (tick driver / sim owner and authoritative state / buffer, grid, query / profiler, harness / config source / SDF, CC / prefab, setup, validator / asmdef, package), give `old responsibility → current owner, file, type, API, lifecycle`. Establish whether the old names (`CrowdRoot`, `_useSdfSolver`, `OracleAgentCount`, `RunFromBatch`, `QueryCircle`, `SpatialGrid`, `SimTuning`) were kept, renamed, or moved. **If the refactor went to prefab serialization + Init injection, M-sim-0's `AddComponent<CrowdRoot>` harness approach may itself be void, so check this without fail.** — **Resolved:** the harness loads the live prefab (`CrowdProfileHarness.cs:157`).
- **Milestone coverage**: judge each of M-sim-0 through M-sim-3 as `not implemented / partial / done / design conflict` — if the refactor already absorbed some of it, implement **only the actual delta**.
- If the §4 handshake items are not recorded in this document or in `PROJECT_MAP.md`, derive them exhaustively from the code.
- Where the §9 specs disagree with the current code, re-reach consensus on just that part via cross-verification (Claude+Codex) before proceeding.

### Step 1 — M-sim-0: measurement (the first gate, minimal code change)
**This step has already been executed — the 4 delegated items below landed in `5c05a27`, and 3 headless 5000/10000 profile runs are committed (`ff60d39` → `Perf/simopt10k_step1_r{1,2,3}.txt`). Do not re-run it; read it as history.** The only remaining gap is that **no 2000-scale run is committed** (the SUMMARY rows in the committed files are 5000 and 10000).
- **The 4 delegated items — all complete (coordinates from the current tree):** ① hardcoded scale → CLI argument = `-profileScales` (`CrowdProfileHarness.cs:58`). ② the bug where `agents` excluded neutrals → record the real `OracleAgentCount` (`:216`). ③ work counters (grid entries, separation/recruit/combat visits, exact-radius qualifying, touching/unique pairs, victim/comparison) → `Crowd/Core/CrowdSimCounters.cs` + harness output (e.g. `victim_comparisons_per_victim` `:439`), on a counter pass separated from timing. ④ guaranteed SDF ON → **live prefab load** (`:157` `ResourceLoader.LoadPrefab<CrowdRoot>()`) + `UseSdfSolver = true` (`:201`) + `IsSdfActive` hard-fail (`:205-210`) + `CC.Move` fallback-0 hard-fail (`:231-237`).
- **`AddComponent<CrowdRoot>` does not exist in the tree.** It survives only as warning comments in the two harnesses (`CrowdProfileHarness.cs:156`, `CrowdOracleHarness.cs:209`) — do not distrust the committed baseline on the strength of the old claim that "the current harness uses `AddComponent` and therefore bypasses SDF ON".
- Execution: `-batchmode -nographics -executeMethod CrowdProfileHarness.RunFromBatch ...` (no editor GUI needed). Establish an **A/A noise band** with ≥3 independent runs at the same seed/ticks.
- **How to drive it**: the win32 headless batch is `Unity.exe -batchmode -nographics -projectPath <proj> -quit -executeMethod CrowdProfileHarness.RunFromBatch -profileOut <out> -profileLabel <label>`. **Confirm with the user** the Unity editor executable path (or the unity-cli connector) and the exact Unity version.
- **M0 hygiene**: outside instrumentation and harnesses, **do not change** game rules, query algorithms, tuning values, production scenes/prefabs, or the oracle baseline (pure measurement).
- **counter run ≠ timing run**: work counters perturb timing, so use **counter-off** results for performance gates and collect counters in a separate run.
- The resulting baseline CSV, oracle, and manifest must be **tracked commits** (start gate §3).
- **Gate/output**: median/p95 + work counters must **consistently point at the same culprit sub-stage** (grid/recruit/combat/steering). Confirm or refute the hypothesis ("combat/recruit superlinear in density") by measurement. If the ranking flips between runs, subdivide markers and re-measure (do not proceed to M-sim-1). **The performance-gate targets for everything downstream are fixed from the measurements produced here.**

### Step 2 — M-sim-1: density capping (remove the superlinear term at the root)
- **Exactly per the §9.1 algorithm**: the grid **stores every agent (no cap)** and caps **only query visit volume, per lane, by budget**. Remove the victim O(v²) sort. Cap selection is a `(distSq, AgentId)` total-order top-k. The cap parameters live in the `SimTuning` SO (consumed immutably); the values are fixed after the M-sim-0 histogram.
- **A density cap is a gameplay change, so commit it separately from performance refactoring**, plus a behaviour A/B ("no perceptible difference": recruit latency/claimant/strength/conversion·elimination/separation). Cap-not-triggered fixtures stay legacy byte-identical; only cap-triggered fixtures get an oracle update after A/B approval.
- Success condition = **removal of the superlinear work term** (not a constant factor). No Burst yet.

### Step 3 — M-sim-2: NativeArray SoA + Burst + IJobParallelFor (the 3-way split is mandatory)
- **M2-a** native storage (serial): managed arrays → `CrowdSimState`/`NativeAgentSoA` (Persistent), native position authority, transform mirror removed. Gate: byte comparison against the M-sim-1 oracle.
- **M2-b** Burst canonical (serial): unify on float2/math, `WallFieldView` + Burst SDF, `System.Random` → per-agent/team `uint4` hash streams (`DeterministicRng`), canonical reduction. Gate: RNG/math changes mean byte-identity is not automatic → behaviour A/B approval, then a `M2-canonical` baseline.
- **M2-c** parallel: only per-agent stages become IJobParallelFor (own slot, own RNG only). Gate: **every worker/batch permutation byte-identical to `M2-canonical`**, whole-SimTick (schedule+complete) measurement, 0 leaks, 0 managed allocations.
- Determinism requirements: `[BurstCompile(FloatMode=Strict, FloatPrecision=Standard)]`, no FastMath. Do not create a new asmdef.

### Step 4 — M-sim-3: clean up the remaining Physics
- Neutral wander Raycast + `RivalAiDriver` clearance Raycast → an SDF segment-clearance query. Remove the baked `CharacterController` (SDF replaces movement). The spawn `CheckSphere` is init-only and stays. Gate: probe/movement A/B + CC 0 + 0 runtime Raycasts, and an M3 baseline after behaviour approval.

## 6. Success criteria (summary)
- **Performance**: the paired 95% CI of each stage's improvement lies outside the A/A noise band (in the improving direction), and unrelated sub-stages are equivalent. M1 = work-bound hard gate, M2 = whole-SimTick hard gate. Absolute ms/% are fixed after M-sim-0.
- **Determinism**: same-seed 2× byte-identical + worker/batch permutation byte-identical + multi-stage oracle re-baselines (at the rebase/M1/M2-b/M3 boundaries). Cross-ISA is unnecessary (single player).
- **Behaviour**: 100% preservation of the critical fixtures (recruit tie-break, strict-larger wins, escort immunity, 0 false contacts outside the radius, and so on). Behaviour changes from the density cap and SDF go through the "no perceptible difference" gate approval.
- **No allocation / lifetime**: 0 managed allocations in SimTick (`GC.GetAllocatedBytesForCurrentThread` delta over 1000 ticks = 0), 0 native leaks, safe repetition of Initialize→ticks→Shutdown.

## 7. Prohibitions / caveats
- Do not invent performance target numbers (fixed after M-sim-0).
- **Stop if the hypothesis is refuted**: if M-sim-0 does not consistently establish combat/recruit as the culprit, do not start M-sim-1 → re-examine and re-approve §9.1.
- **Terminology**: M-sim-0's "CC.Move 0" = **0 calls**; M-sim-3's "CC 0" = **0 components in prefab/scene/runtime**.
- **The exact scope of "the render stack is unchanged"**: **visual presentation is unchanged** — meshes, Animator, skinning, materials, and so on. The exceptions are M2's transform-mirror removal and M3's baked CC removal (position authority moves to native; CC is unused for movement) — the visual result must still be identical.
- Do not cram M-sim-2 into one commit (3-way split — otherwise divergence causes cannot be isolated).
- Do not mix the density cap into the same commit as pure performance refactoring (it is a gameplay change).
- Redesign of the render/anim/GameObject/Human prefab stack is out of scope for this work (the mobile track).
- Burst output is not bit-identical to the Mono oracle → a re-baseline is a given (no direct comparison against the old baseline).
- Do not create a new asmdef, a new Bus, or a new global registry (CLAUDE.md §11, §13).

## 8. File index

**`Docs/PROJECT_MAP.md` §16 is the authority on the list of tracked documents** — which document owns what, and the declaration that the table is exhaustive, are both there. The same index is not duplicated here. **If you meet a statement citing a document under `Docs/` that is not in that table, that statement is stale.** What is recorded here is only the purpose of the captures this document cites directly — `Docs/Photo/PRO.PNG`·`PRO2.PNG` are the 2026-07-17 diagnosis evidence, and `PRO3.PNG` is the evidence for the retraction under "Key status" above.

**Deleted documents (recoverable from history — `git log --diff-filter=D -- <path>`):**

| Deleted document | Where the content worth keeping went |
|---|---|
| `Docs/CrowdCity/SIM_OPT_PLAN.md` | the 9-step density cap spec · the canonical order table · `DeterministicRng` → **§9.1–§9.3** of this document |
| `Docs/CrowdCity/SIM_OPT_10K_PLAN.md` | the T2 policy fixes · T3a/T3b · the mobile 10k floor → **§9.4–§9.6** of this document |
| `Docs/CrowdCity/CHANGELOG.md` | history is git. Measurement derivations are `Perf/MANIFEST.md` §7.7·§8.7; narrative is `CROWD_GUIDE.md` |
| `Docs/CrowdCity/DESIGN.md` | 4 pieces of rationale → `CROWD_GUIDE.md` §11·§17 (already present). The rest was mostly false and was discarded |
| `Docs/CrowdCity/INTERFACES.md` | nothing unique (every signature had drifted). Current coordinates are in `PROJECT_MAP.md` |
| `Docs/CrowdCity/STATUS.md` | ownership chain · kernel list · bus contract → `PROJECT_MAP.md` §2·§4·§10 + the invariants table of this document. The §5 MINOR triage → the §"Deliberately deferred MINOR items" of this document |
| `Docs/CrowdCity/PREFAB_CONSTRUCTION_PLAN.md` | the 3 §6 CC items → `CROWD_GUIDE.md` §4 (corrections included, already present) |
| `Docs/CrowdCity/GameLogicExplainer.html` | 5 items not in the guide → `CROWD_GUIDE.md` §1·§2·§5·§7. The 4 known errors were discarded |
| `Docs/CrowdCity/Sessions/2026-07-15_first.md` | MVP session notes. Nothing worth carrying forward |
| `UnityArchitectureGuide/AI_WORKING_RULES.md` | it was a proper subset of `CLAUDE.md`, and its instruction to copy itself to the root `AGENTS.md` (line 5) was a risk to the mirror contract |
| `phaseC_stage1_validator.txt` (root) | `WallFieldValidator` regenerates it on every run and nothing reads it. The authority for the hash is `City/Generated/WallSdf.asset`. Added to `.gitignore` |

- Design-round artifacts (audit): `codex_burst_prompt{,2..7}.txt` / `codex_burst_out{,2..7}.txt` + `codex_{ccmove,design,explain}_*.txt` — **removed from the working tree** (the conclusions were relocated into §9 of this document). The originals remain in history and restore at any time with `git show fb14a41:<file>`.

---

## 9. Not-yet-started design specs (authoritative)

> **This section is the authority for design specs that are not yet implemented.** Compare each item against the code at the moment you start it — if the spec has fallen behind the code, the code is right, not the spec (P0 gate 2, "authority order").
> Each item is headed by its **current tree state**. "Not implemented" is a value confirmed at `c9e61a4`.

### 9.1 Density cap — full storage + bounded lane query (9-step spec)

**Current tree state: partially implemented. The starting point is not `SpatialGrid.QueryCircleCapped`.** That method (`SpatialGrid.cs:274`) has **0 production callers** — its only caller is `Crowd/Tests/Editor/DensityCapTests.cs`. The cap that actually runs is a **duplicated inline copy** inside the follower separation Burst job (`SteeringForceJob.cs:100` `bool capped = SepBudget > 0;`, enumeration `:104-155`, truncation `:144-148`), and that job is scheduled every tick on the follower steering path (`CrowdRoot.cs:1357`). **Only the inline copy inside the job moves the live path** — fixing `SpatialGrid.QueryCircleCapped` alone leaves the runtime unchanged (0 production callers). Conversely, fixing only the inline copy really does change the live separation path, so **a determinism re-baseline is required.** Fix both copies together so that the equivalence checking in `DensityCapTests` keeps its meaning.

Both copies do **plain visit-count truncation** only — they process up to the budget-th matching candidate and do not visit the next one. The lane splitting, round-robin, hash rotation, and max-heap of the spec below are absent. However, ⑦ (consume the budget before filtering) is **already satisfied by both** — budget consumption precedes the radius test (`SpatialGrid.cs:322` vs `:326`, `SteeringForceJob.cs:113-116` vs `:120`) and, on the job side, precedes even the self/team test (`:124`). The shipping `GameConfig.asset` has `SeparationVisitBudget` at **0 (disabled)** (`79db659` went 0→48; `4adb502` reverted 48→0 because of jitter in dense clusters) → in a live build the job copy's cap does not fire either.

**No storage cap.** Truncating the agents stored in a cell breaks exact leader/AI queries and the oracle, and creates **permanent omissions**. What gets truncated is **query visit volume, not storage**.

Each bounded query goes through the following 9 steps in order.

1. Compute the broad radius's cell-offset stencil **at initialization**.
2. Fix a **canonical cell order** by cell-AABB minimum distance + cell key.
3. Visit **only the lanes** required (`0=neutral`, `1+teamId=team`).
4. An **independent visit budget `B`** per lane.
5. Walk the active cell range **round-robin** (so one dense cell cannot monopolize the budget).
6. **Rotate** the cell range's start point by a `(queryId, cell, lane, tick)` **hash** (starvation mitigation).
7. Consume the budget **before** the self/team/radius filters (strict bound — consuming after filtering does not establish the bound).
8. Push only what passed the radius into a fixed-size **max-heap `(distSq, AgentId)`**.
9. Sort by canonical key and return.

**The rationale for judging the semantic damage small** (recruit takes the 1 nearest · combat touchCount saturates · leaders count locally) is **explanation, not spec**, so the human guide `CrowdCity/CROWD_GUIDE.md` §8 carries it. What remains here is not that argument but the gate below.

**Gate:** counter proof that `CandidatesVisited ≤ QueryCount × LaneCount × B` + work/N does not increase with density under dense conditions + cap-not-triggered fixtures stay legacy byte-identical + **cap-triggered fixtures need no byte identity → update the oracle only after behaviour A/B approval ("no perceptible difference": recruit latency/claimant/strength deficit/conversion·elimination/separation deviation).** The success condition is **removal of the superlinear work term** (not a constant factor). A density cap is a **gameplay change**, so it goes in a **separate commit** from pure performance refactoring.

### 9.2 Canonical order table (determinism contract)

| Subject | Order |
|---|---|
| agent | `AgentId` |
| grid | `cellKey` → `lane` → `AgentId` |
| query tie | `distSq` → `AgentId` |
| recruit | neutral `AgentId` ascending |
| combat edge | canonicalize as `(min, max AgentId)` → sort → unique |
| team pair | `(winner, loser TeamId)` |
| victim | `(assignedDistSq, victimAgentId)` |
| leader | leader / team Id order |
| commit · presentation sync · event | **the same order** as above |

- Float sums are produced by a stable **serial reduction** in `AgentId`/team-pair order. **No atomic floats, no reduction in worker completion order.**
- `[BurstCompile(FloatMode = FloatMode.Strict, FloatPrecision = FloatPrecision.Standard)]`, **no FastMath**. There is no cross-ISA contract (single player), so `Deterministic` is not forced.
- Worker/batch permutation testing: worker ∈ {0, 1, default, max}, batch ∈ {1, 16, 64, 127, >N}, with repetition and reordering. Byte-compare the canonical snapshot after each tick's `Complete`. Restore `JobWorkerCount` in a `try/finally`.
- **Current tree state:** victim ordering is already implemented per this contract (`CombatResolver.SortVictimsByDistanceThenId` — `(assignedDistSq, AgentId)` insertion sort). Keep the spec; **only the implementation** is replaced, by T3b in §9.5.

### 9.3 `DeterministicRng` (uint4) spec — not implemented (M-sim-2b)

**Current tree state: not implemented.** `git grep DeterministicRng -- Assets/` returns nothing. What exists today is **4 `System.Random` instances** — 1 in `CrowdRoot._rng` (`CrowdRoot.cs:332`, seed = `config.Seed`) + 3 per-team `RivalAiDriver._random` (`RivalAiDriver.cs:43`, seed = `config.Seed + t`; the creation loop `CrowdRoot.cs:297-301` makes `_teamCount - 1` = `1 + RivalCount - 1` = 3). The jobs hold no RNG (it is consumed in the serial pre-pass).

- **Do not hand a shared `System.Random` to a job.**
- `DeterministicRng` = a `uint4` xoshiro-family state + a **fixed bit→float conversion** (top 24 bits × 2⁻²⁴).
- Initialization = `mix(globalSeed, stableAgentId | teamId, streamTag)`. A 0 state is replaced with a fixed non-zero.
- Stream separation: spawn = serial `SpawnRng`, wander = per-agent, rival/team tie-break = per-team.
- The rotating cursor of the density cap (§9.1 ⑥) **consumes no RNG** — it is built from a pure `(queryAgentId, cell, lane, tick)` hash.
- Replacing the RNG breaks reproduction of existing seeds → this is an **intended re-baseline**, and a new oracle md5 must be taken.

### 9.4 Mandatory policy fixes before re-introducing T2 (separation neighbour-scan density cap)

It has been reverted once before (`4adb502`, §9.1). Re-introducing it requires fixing the following at the same time.

- **Base the decision on exact cell occupancy, not bucket hash-collision occupancy.** Hash collisions inflate occupancy and produce false positives, and those false positives showed up as stripes and jitter in dense clusters.
- Pair it with `T`/gain/tangential tuning.
- Determinism: `(distSq, AgentId)` total-order top-k; anti-starvation is a pure `(queryAgentId, cell, lane, tick)` hash (consuming no RNG).
- Medium risk (visual and behavioural change) → the shot harness plus a visual check is mandatory. The render gate is pixel tolerance (gate section above).

### 9.5 T3a / T3b — resolver jobification spec (not implemented)

**T3a. Convert the grid to counting sort.** Today it is a full-rebuild LIFO linked-list hash (managed). Target form:
① compute cell/lane keys with IJobParallelFor → ② count → prefix → scatter (canonical) in a **single Burst IJob** → ③ O(N) re-lane afterwards. Stable `cellKey → lane → AgentId` layout.
- **No `NativeParallelMultiHashMap`.** Its enumeration order is non-deterministic, which breaks the §9.2 canonical order. (No usage in the current tree — confirmed at `c9e61a4`.)
- The gain on its own is small — `GridRebuild` is **1.03–1.05%** of Total at 10k (SMR path, `Perf/simopt10k_step1_r{1,2,3}.txt`) / **2.31–2.40%** (GPU path, `Perf/simopt10k_gpupath_r{1,2,3}.txt`), and `FollowerGridSnapshot`, which copies the grid to native, is a further 0.17–0.22% on the GPU path. It is nevertheless **the precondition for parallel resolver and separation queries**.
- Gate: oracle byte-identical (the query consumers are already independent of `(distSq, AgentId)` order).

**T3b. Jobify Recruit/Combat.** Parallel candidate discovery (per-agent) + a **serial Burst canonical reduction** (team pairs, victim selection, leader elimination, commit).
- **Remove the victim O(v²) insertion sort**: the instant path = deterministic radix or order-free; the rate-limited path = a bounded max-heap prefix. **Current tree state: not started** — `CombatResolver.SortVictimsByDistanceThenId` is still an insertion sort. The counter `victim_comparisons_per_victim` (`CrowdProfileHarness.cs:439`) observed **≈31**, but ⚠️ **that counter output has never been committed** (the original was in `Docs/CrowdCity/SIM_OPT_10K_PLAN.md`, added by `768130c` and deleted by `0733deb`; restore with `git show 768130c:Docs/CrowdCity/SIM_OPT_10K_PLAN.md`) **and neither the scale nor the mode is recorded** — do not use this number as gate evidence without re-measuring.
- The determinism contract is exactly the §9.2 table. RNG is the per-agent/team `uint4` hash stream of §9.3 (no shared `System.Random` brought into a job).
- Combat appears superlinear (`candidate_visits` 5k 0.89M → 10k 76.8M/tick) and **becomes the crux at 50k**. ⚠️ These two numbers also have **no committed counter output** (the original is the same `SIM_OPT_10K_PLAN.md` from `768130c` as the item above; the restore command is there too) and the harness and mode are inference, not record. Furthermore, the original attributed that jump to **a regime change in which `combat_touching` only starts occurring at 10k**, and that qualifier was dropped in the relocation — **do not read 2× population against 86× as a smooth superlinear curve**.

### 9.6 Conditions for a 10k "floor" on the weakest mobile devices (extrapolation — not measurement)

> Source: Codex R5's judgement, `git show fb14a41:codex_burst_out5.txt`. Against the weakest AOS/iOS of a 5-year window (2021–2026), the verdict on a 10k floor was **(B) conditionally realistic**. This is not a device measurement but code/ProjectSettings evidence plus extrapolation from device specs, so **do not pin the numbers below down as performance targets** (§7, "do not invent performance target numbers").

**A dual render path is mandatory.** The VAT instancing path requires a StructuredBuffer (= compute/SSBO) in the vertex stage, so it **only holds on GLES3.1+**. This project's settings are:

- `ProjectSettings/ProjectSettings.asset:179` — `AndroidMinSdkVersion: 25`
- same file `:536` — Android graphics API = `150000000b000000` = **Vulkan first + GLES3 fallback**
- same file `:541` — `openGLRequireES31: 0` → **GLES3.1 is not required**

⇒ **GLES3.0-only devices fall inside the window.** But `CrowdRenderer.cs:105` (`graphicsShaderLevel < 45 || !supportsComputeShaders`) and `:114` (`!supportsInstancing || maxComputeBufferInputsVertex <= 0`) are **only gates that drop down to the SMR path**; they provide no secondary instancing path. Claiming a 10k floor requires a **secondary path** of `DrawMeshInstanced` + `MaterialPropertyBlock`.

> **Correction to the source.** Codex R5 hung this requirement on `Graphics.RenderMeshIndirect`, but the actual call is `Graphics.RenderMeshPrimitives` (`CrowdRenderer.cs:188`, leader shadows `:212`). **Only the API name differs; the constraint that it requires compute/SSBO is identical**, so the conclusion above stands unchanged.

**The 6 items that break the 10k floor if any one is dropped:** ① a hard cap on concurrently visible agents ② frustum/distance culling ③ multi-rate behaviour LOD **decoupled** from the fixed 50Hz sim (`GameplayRoot`) ④ the density cap (§9.1) ⑤ per-agent RNG (§9.3) ⑥ a long-duration thermal-soak acceptance gate on the weakest devices.

**Definition of "10k":** logically **active** population + a hard cap on concurrently visible agents. Read as 10k all concurrently visible, the verdict flips to **(C) unrealistic**.

**Memory is not the constraint** (though R5's original "SoA 10k ≈ 1MB" is smaller than the current tree). In the current tree the per-agent native SoA is `AgentBuffer` **21 B** (`Id` 4 + `Team` 4 + `IsLeader` 1 + `Pos` 8 + `Scale` 4) + the 17 per-agent arrays of `CrowdSimState` **108 B** = **129 B/agent** ⇒ 10k ≈ **1.3 MB**, 50k ≈ **6.5 MB**. On top of that come the grid hash table (`SpatialGrid.ComputeTableSize` = the power of 2 at or above capacity×2 → `GridBucketHead` + `BucketTeamCount`×teamCount) plus the managed mirrors and `InstanceData`, so the real total is larger. The conclusion is unchanged — the binding constraints are render submission, GPU fill, and sustained heat (**this conclusion is confined to this section's weakest-mobile-device extrapolation and says nothing about the sim-vs-render-preparation split of `GameplayRoot.Update` on desktop/editor — that is still unmeasured**: the retracted item in the "Key status" section).

**Per-tier render-stack ceilings** (R4 — **a different axis** from the sim axis of §9.5's T3a and so on, and all of it extrapolated and unmeasured):

| Tier | Contents | Concurrently visible ceiling |
|---|---|---|
| T0 | current | 300–700 |
| T1 | sim optimization only | 300–900 (same order of magnitude) |
| T2 | — | 3k–8k |
| T3 | GameObject removal + indirect | **10k–20k (10k first reached)** |
| T4 | + VAT · LOD | total active 20k–40k · concurrently visible 10k–20k |

VAT/BRG/multi-LOD are quality and headroom (optional), not required for the floor. **A "guaranteed" declaration comes only after passing a real-device thermal soak on a device matrix that includes the Mali-G52 / Adreno 610 class.**

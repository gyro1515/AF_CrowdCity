# CrowdCity — Changelog (canonical, agent-facing)

Branch `feat/crowd-sim-10k`. Newest entry first.

**Convention.** One changelog pair per feature area (`CHANGELOG.en.md` canonical/agent-facing, `CHANGELOG.ko.md` human-facing and more detailed). Append an entry on every commit that **adds or changes a feature** — perf, fix and tooling commits included. Do **not** log pure docs/chore commits (typos, agent model-pin edits, transcript cleanup). This file is optimized for a cold-start agent: dense, factual, `file:line` everywhere.

---

## Current state / next step

### Done
| Item | Commit | Status |
|---|---|---|
| §2 split instrumentation (7 profiler segments) | `1485848` | landed, oracle byte-identical |
| Edit-mode rig ghost fix (`SetActive(false)` before deferred `Destroy`) | `dcb3bfb` | landed, shot-verified |
| T1a — dead `transform.rotation` write removed on the GPU path | `fdf909d` | landed, correctness + perf measured |
| §2 measurement evidence + plan correction | `ff60d39` | landed (docs only) |

Measured so far: the §2 serial-vs-job split (headless, `CrowdProfileHarness`), and T1a's GPU-path gain (play mode, `CrowdPerfHarnessP95`).

### Immediately next — this is a RE-DECISION, not new work

`SIM_OPT_10K_PLAN.md` §4 currently says "next = T1b". **That verdict is in question. Do not start T1b before resolving it.**

Why: §2 was measured **headless**, which runs the SMR path (`CrowdRenderer.Init` returns `false` on a null graphics device — `CrowdRenderer.cs:98..102`), so `_gpuRenderActive == false` and every `*Present` figure **includes the very writes T1a removed**. The plan recorded the break-even explicitly: at 10k the verdict flips if **more than 86.1%** of `Present` cost is SMR-only (`SIM_OPT_10K_PLAN.md` §2). T1a's measured gain is **−12.83 ms/tick**, which is **~94%** of the 13.58 ms combined `Present` @10k (`FollowerPresent` 8.87 + `NeutralPresent` 4.71).

Required action before T1b: **re-measure the segments with the GPU path ACTIVE** — run `CrowdProfileHarness` **without `-nographics`** so `CrowdRenderer.Init` (`CrowdRenderer.cs:64`) succeeds and `_gpuRenderActive` (`CrowdRoot.cs:119`, set at `CrowdRoot.cs:888`) is true.

Caveat to state in any writeup: the two numbers come from **different harnesses in different modes** (headless edit-mode `CrowdProfileHarness` vs play-mode `CrowdPerfHarnessP95`). This is a **tension to resolve**, not a proven refutation of T1b.

### Standing invariants — do not break
- All **7** `_visualYaw[…] =` writes stay unconditional: `CrowdRoot.cs:1205, 1430, 1440, 1526, 1536, 1631, 1673`. They are the GPU path's yaw input.
- All **5** `_visualSpeed01[…] =` writes stay unconditional: `CrowdRoot.cs:1204, 1427, 1523, 1630, 1672`.
- The **leader** `SetHeadingAndSpeed` at `CrowdRoot.cs:1206` stays **ungated** (S4b2 contract keeps leader transforms live; ≤4 agents = 0.09% of SimTick).
- The two idle-retain reads stay: `CrowdRoot.cs:1439`, `:1535` (`float headingDeg = _visualYaw[index];`).
- `*JobWait` segments are **inclusive overlaps of `CCMove`** — never sum them with `CCMove`, never double-subtract.
- Render-path gates use **pixel tolerance**, never PNG MD5 (see Conventions).

### Traps
- **Follower/neutral `transform.rotation` is now permanently stale on the GPU path**, joining the already-stale positions (existing comment at `CrowdRoot.cs:740`). Any future feature reading a follower's/neutral's `transform.rotation` or `.forward` silently gets spawn-time values.
- `fdf909d` (T1a) is **only safe together with `dcb3bfb`**. Revert them together — see the T1a entry.
- Unity **refuses deferred `Object.Destroy` in edit mode**. Every edit-mode harness (`CrowdShotHarness`, `CrowdProfileHarness`, `CrowdOracleHarness`) observes objects runtime code believes it destroyed. `GetComponentsInChildren<SkinnedMeshRenderer>(true)` counts inactive objects, so `smr == 0` assertions still fail in edit mode. Play-mode perf harnesses are unaffected (they yield per frame).
- `CrowdPerfHarnessP95` CSV column `simtick_median_ms` is **not a median** — `CrowdPerfHarnessP95.cs:413` computes `totalSimMs / totalSteps`, an arithmetic mean per 0.02 s step. `full_*`, `render_median` and `mainthread_*` **are** genuine percentiles/max.

---

## Conventions / gates

- **Oracle byte-identity gate (sim changes).** `CrowdOracleHarness`, n=2000 seed=12345 1000 ticks, snapshot md5 **`4F79282EB20A79023B45F2EB2DE5271B`**. Note `SIM_OPT_HANDOFF.md:25` still lists `2b3aad7f786004f1185a067bc1b31f4a` — that is a **stale pre-Burst baseline**, do not gate on it.
- **Render gates (render-path changes).** Pixel tolerance **≤100 differing pixels AND max channel delta ≤8**, plus a visual read. **Never PNG MD5** — `CrowdShotHarness` output is not byte-reproducible (measured 3–16 differing pixels of 921,600 per image across identical runs, max channel delta ≤4, including the untouched `smr_off.png`). Measured separation from real changes: 35,587–123,161 px with max delta 171–184 (~2,200× in pixel count, ~43× in delta).
- **TMP asset side effect.** Every headless Unity harness run dirties `Assets/TextMesh Pro/Resources/Fonts & Materials/LiberationSans SDF - Fallback.asset` (~10 insertions / 911 deletions). Revert it before committing. **Never `git add -A`** — always name the paths.
- **Quiet machine for timing runs.** Read host-wide CPU before each run; defer above **~30%** ambient. A foreground game holding a full core inflated parallel segments **+66%** while serial segments moved only **+8–13%**.
- **No invented performance targets** (`SIM_OPT_HANDOFF.md` §7). Gates are phrased as "improvement beyond the run-to-run spread"; a delta inside the spread is reported **inconclusive**, not resolved.

---

## `ff60d39` — [Docs] §2 measurement evidence + plan correction

Committed `Docs/CrowdCity/Perf/simopt10k_step1_r{1,2,3}.txt` + `Docs/CrowdCity/Perf/MANIFEST.md`; rewrote `SIM_OPT_10K_PLAN.md` §2 and §4. No code/runtime surface change.

**Setup.** `CrowdProfileHarness`, headless (`-batchmode -nographics`), SDF ON (+ `IsSdfActive` and `CcMoveFallbacks == 0` asserts), seed=12345, scales 5000/10000, 3 runs with identical args. Per scale: sdfVerify 20 + warmup 200 + timing 1000 + gc 500 ticks. `-profileCounters` deliberately **not** passed (counters perturb timing).

**Verdict data (medians of 3 runs).**

| Scale | T1b-relevant serial | T2-relevant job wait | Total | gap / spread |
|---|---|---|---|---|
| 5000 | **4.39 ms (68.3%)** | 0.52 ms (8.1%) | 6.43 ms | 3.87 / 0.079 = **49×** |
| 10000 | **14.47 ms (70.4%)** | 2.78 ms (13.5%) | 20.56 ms | 11.69 / 0.197 = **59×** |

- Serial breakdown @10k: `FollowerPresent` 8.87 (43.1%) > `NeutralPresent` 4.71 (22.9%) >> `NeutralPrepass` 0.70 > `FollowerPrepass` 0.18 > `FollowerGridSnapshot` 0.02. **The two Present loops alone = 66% of SimTick.**
- **T2 ceiling:** zeroing both job waits recovers at most 13.5% @10k, 8.1% @5k.
- **Scaling 5000→10000:** Total **3.20× for 2× agents** (superlinear). Follower segments 5.95–10.04× (exception `FollowerGridSnapshot` 2.49×, negligible magnitude 0.0077→0.0192 ms); neutral segments 1.06–1.69×. `FollowerPresent` is both the largest segment and the worst scaler (10.04×).

**Explicit correction recorded.** The plan's original arithmetic — `FollowerSteer − CCMove − .Complete wait` — **cannot** isolate serial cost. `Seg.CCMove` aggregates **five** sites (`CrowdRoot.cs:1185` leader `ApplyHorizontalMove`, `:1390` follower SDF move job wait, `:1479` follower CC fallback, `:1608` neutral SDF move job wait, `:1657` neutral CC fallback) **and** `*JobWait` nests inside `CCMove`, so the formula double-subtracts. That is why per-branch segments (`1485848`) were required.

**Load control.** Run 2 launched at **19.4%** ambient CPU vs 8.7% / 8.3% for runs 1/3 (2.3×), and its job waits were **not** systematically inflated: `FollowerJobWait`@10k r1 2.6809 / r2 2.6650 / r3 2.6410 → r2 is the middle value; @5k the lowest-load run (r3, 8.3%) was the **highest** (0.4377 vs 0.4223 / 0.4224). Contrast the discarded earlier attempt where a foreground game held a full core (~3.16 cores of background load total): parallel segments inflated **+66%** while serial moved only **+8–13%**. The 30% threshold comes from that observation.

**Known gaps (both recorded in `Perf/MANIFEST.md` §6).**
1. The harness tracks follower/neutral populations internally but does not emit them, so the follower-vs-neutral scaling asymmetry is **inferred** as a population-mix shift, not measured.
2. `RenderInterpolate`'s per-agent transform writes (`CrowdRoot.cs:747`, `:755`) are **outside this measurement entirely** — the harness never calls `RenderInterpolate`.

---

## `fdf909d` — [Perf] T1a: remove the dead `transform.rotation` write on the GPU path

`Assets/@Project/Crowd/Scripts/CrowdRoot.cs`, `+26 / −10`.

**Change.** Six `SetHeadingAndSpeed` call sites in `CrowdRoot.SteerFollowersAndNeutrals` wrapped in `if (!_gpuRenderActive)`:

| Site | Call | Guard |
|---|---|---|
| follower SDF move | `:1433` | `:1431` |
| follower SDF idle | `:1443` | `:1441` |
| follower CC fallback move | `:1529` | `:1527` |
| follower CC fallback idle | `:1539` | `:1537` |
| neutral SDF | `:1634` | `:1632` |
| neutral CC fallback | `:1676` | `:1674` |

Two vestigial `Human human = _humanByAgent[index];` locals deleted; array access inlined into the guards (matches the pre-existing style at the neutral sites).

**Why the write is dead.** On the GPU path yaw is rendered from `_visualYaw` and animation phase from `_visualSpeed01` / `_phase01` (`CrowdRoot.cs:909`, `:914`), and the visual rig is destroyed/deactivated — so `Human.SetHeadingAndSpeed`'s `transform.rotation` write (`Human.cs:89`) has no consumer. ~10k calls/tick at 10k agents.

**Why gated at the CALLER.** `_gpuRenderActive` is a private field on `CrowdRoot` (`CrowdRoot.cs:119`) and `Human` is by design a fully passive leaf — the render-mode decision is not pushed into `Human`. Mirrors S4b2's caller-side position gate (`CrowdRoot.cs:747`, `:755`).

**Deliberately kept.** The 7th (leader) call at `:1206` (S4b2 contract keeps leader transforms live; ≤4 agents, 0.09% of SimTick); all 7 `_visualYaw` writes; all 5 `_visualSpeed01` writes; the 2 idle-retain reads (`:1439`, `:1535`). **No** `|| _buffer.IsLeader[i]` clause was added — it would be dead code, because these loops iterate `model.FollowerAgentIndices` (leader excluded) or skip non-neutrals.

**Coupling — record prominently.** This change is **only safe together with `dcb3bfb`**. Before that fix, gating these rotation writes would visibly **freeze the surviving edit-mode ghosts' facing** — a real visual regression. **Revert them together.**

**Latent trap.** Follower/neutral `transform.rotation` is now permanently stale on the GPU path, joining the already-stale positions (see the existing comment at `CrowdRoot.cs:740`).

**Correctness verification.** Oracle n=2000 seed=12345 1000 ticks md5 `4F79282EB20A79023B45F2EB2DE5271B` — unchanged (headless has `_gpuRenderActive == false`, so all six guards are true and the SMR path is bit-identical); events/summary CSV byte-identical; 0 `error CS`. Shot comparison: all four PNGs within noise — `gpu_on` 5 px, `gpu_on_top` 10 px, `gpu_on_closeup` 1 px, `smr_off` 3 px, **max channel delta 1** on each. Epistemics: because the rig is hidden, removing a dead write must produce **ZERO** visual change — so "within noise" is itself the proof that `_visualYaw` was not accidentally gated.

**Performance measurement.** `CrowdPerfHarnessP95`, play mode with graphics, **A/B/B/A order to cancel drift**, 10000 agents (10004 incl. leaders), 300 frames/count, uncapped, 1920×1080. Validity gate `gpuActive=T, smr=0, anim=0` passed on 4/4 runs. Raw CSVs: `Docs/CrowdCity/Perf/t1a_p95_{before,after}_r{1,2}.csv`.

| Metric | BEFORE | AFTER | Delta | Largest same-arm spread | Ratio | Call |
|---|---|---|---|---|---|---|
| SimTick per step | 23.11 ms | 10.29 ms | **−12.83 ms (−55.5%)** | 1.61 ms | **8.0×** | PASS |
| `full_median` | 115.3 ms | 17.9 ms | −97.4 ms | — | — | see headline note |
| `mainthread_p95` | 147.3 ms | 26.1 ms | −121.2 ms | — | — | — |
| `full_max` | 460.0 ms | 402.9 ms | −57 ms | 134 ms | 0.43× | **inconclusive** |
| `render_median` | 21.46 ms | 6.58 ms | −14.9 ms | 11.44 ms | 1.30× | **inconclusive** |

- **The honest headline is −12.83 ms/tick, NOT "7× faster frames."** The ~97 ms frame gap is amplified because BEFORE's 115 ms frame period exceeds `MaxStepsPerFrame × FixedStep` (4 × 20 ms = 80 ms — `CrowdPerfHarnessP95.cs:164`, `:165`), so the catch-up accumulator saturates at the 4-tick cap: `ticksPerFrame` 3.994 (BEFORE) vs 0.912 (AFTER). Arithmetic check: `3.994 × 23.113 − 0.912 × 10.287 = 82.9 ms` of sim, plus 14.9 ms render = **97.8 ms ≈ the measured 97.4 ms**.
- **Mechanism** (why 1.28 µs per removed call is far more than a bare transform write): `Human.SetHeadingAndSpeed` (`Human.cs:87`) also runs `if (_animator != null)` (`Human.cs:91`), and on the GPU path `_animator` references a **destroyed** `UnityEngine.Object`, so that comparison routes through the native alive-check ICall — the same cost this project already recorded as a top offender in an earlier editor profiler capture (`Loading.IsObjectAvailable`, 6008 calls/frame at 3004 agents) — roughly 10k times per tick at 10k agents.
- **Scope honesty.** Editor/Mono on Ryzen 5 5600X + RTX 4080, D3D11. **Not** a player-build or mobile number. The **sign** holds unconditionally (the work is provably dead), but the per-call constant should shrink under IL2CPP/AOT; conversely the 4-tick accumulator cap that BEFORE pins against is **likelier** to be hit on mobile, so the value there may be higher — must be **confirmed on device, not extrapolated**.
- **Harness gotcha.** CSV `simtick_median_ms` is not a median (`CrowdPerfHarnessP95.cs:413`, `totalSimMs / totalSteps`).

---

## `dcb3bfb` — [Fix] deactivate the GPU-path rig immediately before the deferred `Destroy`

`Assets/@Project/Human/Scripts/Human.cs`, `+2` lines including its comment. `Human.DestroyVisualRig()` (`Human.cs:72`) now runs `_animator.gameObject.SetActive(false);` (`Human.cs:78`) immediately before the existing deferred `Destroy(_animator.gameObject)` (`Human.cs:79`).

**Root cause.** Unity refuses deferred `Object.Destroy` when called from **edit mode**. `CrowdShotHarness` runs in edit mode and ticks the sim 150 times with **zero frame boundaries** after spawning (`CrowdShotHarness.cs:32` default `ticks = 150`, `:210` `crowd.SimTick(Dt)`), so the rigs survive. Measured: the refusal message appeared **704 times for 704 agents** — 100% survival (700 neutrals + 4 leaders; `CrowdShotHarness.cs:31` default `neutral = 700`). Because S4b2 gated position writes but **not** rotation writes, each survivor became a ghost frozen at its spawn position with its yaw still updated every tick; `gpu_on.png` visibly double-rendered (the red leader group appeared twice, neutral density roughly doubled, foreground props were occluded). That made screenshot-based verification of any render-path change impossible.

**Rejected alternatives.** Replacing with `DestroyImmediate`, or branching on `Application.isPlaying` — both put an **editor-only branch inside runtime code** (CLAUDE.md §11). Rejected during cross-verification.

**Safety argument.** `DestroyVisualRig` has exactly two callers, `CrowdRoot.cs:570` and `:626`, both inside `if (_gpuRenderActive)` (`:567`, `:623`); `_gpuRenderActive` is set true only at `CrowdRoot.cs:888` when `CrowdRenderer.Init` (`CrowdRenderer.cs:64`) succeeds. The SMR fallback path never reaches this function, so **no shipping configuration can have visible characters hidden.** `SetActive(false)` does not null the `Animator` reference, so `SetHeadingAndSpeed`'s `_animator != null` guard (`Human.cs:91`) still behaves as before.

**Play-mode effect.** Removes up to one frame of double-render per freshly spawned agent — an improvement, not merely a harness workaround.

**Verification.** Oracle md5 unchanged (the changed code is on a GPU-only path headless never reaches); events/summary CSV byte-identical; 0 `error CS`. Shot re-run showed the ghosts gone: 35,587–123,161 differing pixels vs the pre-fix images, and all three `gpu_on*` files shrank.

**Important note.** The error count **stays at 704** after the fix — `Destroy` is still refused in edit mode; only *rendering* stops. So the verification signal is **visual/pixel, not the log count**. Related: `GetComponentsInChildren<SkinnedMeshRenderer>(true)` counts inactive objects, so `smr == 0` assertions still fail in edit mode (the play-mode perf harnesses are unaffected — they yield per frame).

**Side finding that changed the project's verification method.** `CrowdShotHarness` PNGs are **NOT byte-reproducible**. Two runs with identical arguments differed on **3–16 of 921,600 pixels** per image — including the untouched `smr_off.png` — with max channel delta ≤4, while the simulation was proven bit-stable by the oracle. So the noise is in the **render stage**. Render-path gates therefore use **pixel tolerance (≤100 differing pixels AND max channel delta ≤8) plus a visual read**, never PNG MD5. Measured separation: real changes register 35,587–123,161 px with max delta 171–184, i.e. ~2,200× in pixel count and ~43× in delta.

---

## `1485848` — [Tooling] §2 split instrumentation: 7 new profiler segments

`Assets/@Project/Crowd/Scripts/CrowdSimProfiler.cs` `+18 / −3`; `Assets/@Project/Crowd/Scripts/CrowdRoot.cs` `+14 / −0`.

**`CrowdSimProfiler.cs`.** 7 members appended between `CCMove` and `Count` — `FollowerPrepass` (`:37`), `FollowerGridSnapshot` (`:38`), `FollowerJobWait` (`:39`), `FollowerPresent` (`:40`), `NeutralPrepass` (`:41`), `NeutralJobWait` (`:42`), `NeutralPresent` (`:43`). Existing ordinals unchanged; `Seg.Count` 12 → 19. The enum XML doc (`:13-22`) was rewritten to state: spans are **not** mutually exclusive (nested/overlapping — never sum percentages); `CCMove` aggregates five sites and, on the SDF path, times `WallSolver.Resolve` rather than `CharacterController.Move` (actual CC.Move calls are **0**); `Follower*`/`Neutral*` instrument the **SDF path only**; `*JobWait` is an **inclusive duplicate** of `CCMove` and must never be double-subtracted.

**`CrowdRoot.cs`.** 14 `Begin`/`End` calls inserted in `SteerFollowersAndNeutrals`, **zero deletions** — no pre-existing line was touched, which is itself the cleanest proof no logic changed. The two job waits nest strictly as `Begin(CCMove) → Begin(*JobWait) → .Complete() → End(*JobWait) → End(CCMove)` (`CrowdRoot.cs:1390-1394` follower, `:1608-1612` neutral). Per-`Seg` `_start` slots (`CrowdSimProfiler.cs:51`) make cross-segment nesting safe, and no `Seg` is ever re-entered.

**Deliberate omissions.** The `!sdfActive` CC-fallback branches are uninstrumented (the profile harness hard-asserts SDF ON and `CcMoveFallbacks == 0`, and in the CC branch move and presentation are physically interleaved and cannot be split). An 8th per-event `NeutralRepick` segment was rejected as speculative instrumentation.

**`CrowdProfileHarness` needed no edit** — it enumerates from `Seg.Count` and sizes from `Accum.Length` (`CrowdSimProfiler.cs:54`), so the SEGMENTS table simply grew from 12 to 19 rows per scale.

**Measurement coverage achieved.** The sub-segments account for **99.7–99.98%** of their parent (`FollowerSteer` 99.66–99.95%, `NeutralMove` 99.97–99.98%). The only uninstrumented region inside `FollowerSteer` is `CrowdRoot.cs:1320-1389` (SteeringForceJob struct wiring + `Schedule`), **≤0.34%**. Caveat: `FollowerJobWait` absorbs residual `SteeringForceJob` execution, because the move job is scheduled with the force job as a dependency (`CrowdRoot.cs:1392`) and `.Complete()` drains the chain — so it is **not pure move-job cost**, which makes T2 look larger than it is.

**Verification.** Oracle md5 `4F79282EB20A79023B45F2EB2DE5271B` byte-identical, determinism **PASS**, zero `error CS` / `warning CS`. `CrowdSimProfiler.Enabled` (`CrowdSimProfiler.cs:48`) defaults false, so `Begin`/`End` are a single bool branch in production (`:71`, `:80`).

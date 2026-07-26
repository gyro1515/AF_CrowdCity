<!-- RECOVERED VERBATIM from session transcript
     source: 7be25ba5-e341-4238-8642-5da87b3a7962.jsonl line 1537 (task-notification <result> block)
     task-id: ade40585fda2504d9 | agent: "Verify PROJECT_MAP exhaustively" | audited doc: Docs/PROJECT_MAP.md
     Body below is byte-identical to the transcript's <result> payload (HTML entities
     &lt; &gt; &amp; are as stored by the transcript). Nothing added, removed, or reworded. -->

## Conclusion

`Docs/PROJECT_MAP.md` is **substantially accurate**. I opened **all 368 numeric `file:line` citations**, every path, and recounted every asserted count. **The §10 premise in your prompt is stale** — commit `c3c194a` ("PROJECT_MAP 인용 좌표 검증 — 잘못된 4곳 정정") already repaired those coordinates, including the exact §10 row (`:166`, `:189` → `:189-207`). §10 is now exact, uniqueness claims included. Coordinate drift in the code-pointer body is **zero** except for the two range/enumeration issues below.

| Map line | Claims | Actually true | Severity |
|---|---|---|---|
| 17 (§1) | `Packages/manifest.json` \| Unity `6000.3.9f1`, URP, Input System, Burst/Collections/Mathematics | manifest.json has **no editor-version field**. `6000.3.9f1` lives in `ProjectSettings/ProjectVersion.txt:1` (`m_EditorVersion: 6000.3.9f1`). The package half is correct (URP 17.3.0, inputsystem 1.18.0, burst 1.8.28, collections 2.6.2, mathematics 1.3.3). | **WRONG** |
| 5 (header) | "Line numbers are coordinates as of the referenced commit." | **No commit is referenced anywhere in the file** (grep for a hash returns only `:NNN` coordinates). `WORK_STATE.md:3` carries an explicit stamp (`검증 기준 커밋: 9f23df4`); the map has none, so its 368 coordinates have no verifiable as-of point — and "the referenced commit" is precisely the self-relative phrase `CLAUDE.md` §1.1 Rule 4 forbids. | **WRONG** |
| 253, 254, 255, 256 (§13) | Menu names "Bake Human Prefab" / "Bake Crowd Renderer" / "Bake Feature Root Prefabs" / "Bake Dev HUD" | Actual `[MenuItem]` literals are longer: `…Bake Human Prefab (Resources + CC and Human)` (`GameSceneSetup.cs:1116`), `…Bake Crowd Renderer (VAT material + CrowdRenderer child)` (`:1698`), `…Bake Feature Root Prefabs (Resources)` (`:1926`), `…Bake Dev HUD (Resources)` (`:2175`). A grep for the map's string finds nothing. All 12 project MenuItems are represented and all line numbers are exact. | **IMPRECISE** |
| 94 (§5) | "visual-only arrays `_visualPrev/_visualCur/_visualRender/_visualYaw/_visualSpeed01/_phase01` \| `:104-115`" | Range over-claims: `:108-111` are `_leaderYawDeg`, `_wanderHeadingDeg`, `_wanderTimer`, `_lastPublishedCounts` — two are kernel-read `NativeArray`s, not visual-only. Correct coordinates: `:104-107` + `:114-115`. | **IMPRECISE** |
| 102–113 (§5) | "**`SimTick`** (10 pinned phases)" then sub-rows ② … ⑩ | Only 9 numbered phases are listed. Code phase **① is `CrowdRoot.cs:666-671`** (apply pending match state at tick start — the pair of `OnMatchStateChanged` `:799`) and is absent; the two unnumbered rows (`:680` PrevPos, `:690` Restore) are *not* ① in the code's own numbering. | **IMPRECISE** |
| 207 (§10) | "Editor-only null-callback log in `Subscribe` `:166`" | `:166` is the `#if UNITY_EDITOR` directive; the `Debug.LogError` is `:167` (`Subscribe` at `:162`). Off-by-one. | **IMPRECISE** |
| — (whole file) | — | **`ProjectSettings/` appears nowhere.** Two things a cold-start agent needs are unreachable: the editor version, and the `Unit` physics layer at `ProjectSettings/TagManager.asset:16`, which `CrowdRoot.UnitLayerName` (`:39`) and `RivalAiDriver.WallProbeMask` (`:25`) both depend on. | **GAP** |
| 21 (§1) | folder convention: `Scripts/ · Editor/ · Contracts/ · Core/ · Tests/Editor/ · Resources/{Prefabs,UI}/ · Externals/ · Generated/ · Prefabs/ · Materials/ · VAT/` | Omits `Animations/`, which exists and is cited by the map itself at line 238 (`Human/Animations/HumanWalk.controller`). | **GAP** (trivial) |
| 310 (§16) | "The retired `CHANGELOG.md` · … · `Sessions/` are recoverable from history; `Docs/WORK_STATE.md` §8 records where each one's surviving content went." | Factually correct (the 9 names match `0733deb`'s Docs deletions; `CHANGELOG.en.md` went earlier in `65268e0`, out of scope). But it is change-history narration duplicating `WORK_STATE.md` §8's table — `CLAUDE.md` §1.1 Rule 1: "Change history is not a fourth document." | **CONTRACT** |
| 270–275 (§14 Mode) | "edit mode (SMR vs GPU path decided by presence of `-nographics`)", "**play mode**, graphics required" | Mechanism/gate explanation, not a location — and line 277 already points at `Perf/MANIFEST.md` + `WORK_STATE.md` for exactly this. | **CONTRACT** |
| 151 (§6) | "all jobs: `FloatMode.Strict` + `FloatPrecision.Standard`" | True (verified at `SteeringForceJob.cs:16`, `FollowerSdfMoveJob.cs:20`, `NeutralSdfMoveJob.cs:21`) but it is a determinism **invariant restated with no pointer** — WORK_STATE's territory. | **CONTRACT** |
| 162, 175 (§7/§8 "live values") | copies `WallSdf.asset` and `GameConfig.asset` field values into the map | All values verified exact. Still re-hosts a value authority the same row points at — and it has already gone stale once: `c3c194a` had to fix `yMax 3.2` → `3.1999998`. | **CONTRACT** (mild) |

Also restated-invariant nits (all verified correct, both are §8-flavoured ordering contracts): line 37 "teardown order (own bindings → `GameplayRoot.Shutdown` → `_session.Dispose`)", line 117 "`Shutdown` (renderer → WallField → simState → clones)". Line 88's "(1987 lines, feature root)" is exact today but is pure maintenance debt.

**No measurement or performance number appears anywhere in the file** — that half of the contract is clean.

**Known landmine: PASS.** §9 reads exactly `LoadPrefab&lt;T&gt;()` → `Prefabs/&lt;ClassName&gt;` `:24`, `LoadUI&lt;T&gt;()` → `UI/&lt;ClassName&gt;` `:36`, `LoadSO&lt;T&gt;(name)` → `SO/&lt;name&gt;` `:47`, all three verified in `Assets/@Project/Manager/ResourceLoader/Scripts/ResourceLoader.cs`. Zero occurrences of `LoadRoot` or `Roots/` in the map.

**Verdict: trustworthy as an entry point.** Every code coordinate an agent would navigate by is correct; the two WRONG entries are a metadata mis-attribution (`manifest.json` vs `ProjectVersion.txt`) and a missing freshness stamp, neither of which misdirects a code edit. Fix the 6 concrete items and add a literal HEAD-parent hash to line 5.

**Checked vs found:** 368 numeric citations, ~60 paths, 20 asserted counts/structural claims → **2 WRONG, 4 IMPRECISE, 2 GAP, 4 CONTRACT**. All 56 `.cs` files under `Assets/@Project/` are represented in the map.

Counts recounted and **all correct**: 2 asmdefs · 37 building prefabs + 37 meshes (`ExpectedBuildingCount = 37`) · 45 Transforms in `Human.prefab` · 5 team materials · 2 event payloads · 7 split profiler segments (`:37-43`) · 14-column CSV (`CsvColumnCount = 14`) · 4 child roots · 3 Burst jobs · 1 scene with exactly the 5 claimed roots and `GameArea/{City,Human}` · 6 Resources prefabs matching §9's list exactly · 12 MenuItems (all listed) · 7 `*Tests.cs` (all listed) · `CrowdRoot.cs` = 1987 lines · `InstanceStride = 28` · §16 doc table exhaustive against `git ls-files`.

## Changed files

None — read-only. One scratchpad helper written outside the repo: `C:\Users\gyro1\AppData\Local\Temp\claude\...\scratchpad\showlines.sh`. `git status` clean.

## Verification commands and results

- `git rev-parse --short HEAD` → `d136350`; `git status --porcelain` → clean.
- Line dumps via a helper `sed -n`/`nl` script, one call per file, e.g.
  `sh showlines.sh "Assets/@Project/Crowd/Scripts/CrowdRoot.cs" 659 680 684 690 693 696 698 700 703 706 709 712 717 1130 1157 1212 1741 1780 1945 …` — every printed line matched the map's description.
- Citation inventory (so nothing was sampled): a Python one-liner extracting `:(\d+(-\d+)?)` per map line → **368** citations, printed grouped by map line; each group was covered by a dump above.
- Path existence: two `for p in … do test -e` sweeps over every path in the map → all exist.
- Ranges: endpoints printed and enclosing member confirmed with `grep -nE '^    (private|public|…)'` (e.g. `SteerFollowersAndNeutrals` spans 1212–1683, so all of §6's sub-ranges 1215-1679 are genuinely inside it; the `else` at `:1450`/`:1639` pair with `if (sdfActive)` at `:1360`/`:1552`, and `:1544` is the closing brace).
- Uniqueness claims: `grep -rn "EventManager.Get" Assets/@Project --include=*.cs` and `grep -rn "\.Publish("` → exactly the sites §10 lists (2 publisher acquisitions, 3 publish sites, 4 subscriber pairs incl. the editor harness).
- Menu strings: `grep -rn "MenuItem(" Assets/@Project --include=*.cs` → 12 hits, all lines match §13; 4 strings truncated in the map.
- Scene structure: Python YAML walk over `GameScene.unity` resolving `m_Father: {fileID: 0}` → roots `GameArea{Human,City}`, `Main Camera`, `Directional Light`, `GameSceneController`, `Global Volume`.
- Counts: `find Assets -name '*.asmdef'`, `ls City/Prefabs/GeneratedBuildings | grep -c '\.prefab$'`, `grep -c 'u!4 &amp;' Human/Prefabs/Human.prefab`, `grep -c "^Transform:"`, `find Assets -name '*.unity'`, `git ls-files | grep -iE '\.(md|html|txt|csv)$'`.
- Coverage: `find "Assets/@Project" -name '*.cs'` (56 files) each grepped for its basename in the map — the 4 apparent misses (`CrowdShotHarness.cs` etc.) are listed in §14 without the extension, so coverage is 100%.
- Landmine: `grep -nE "LoadRoot|Roots/|Resources/Roots" Docs/PROJECT_MAP.md` → no hits.
- Premise check: `git log --oneline -- Docs/PROJECT_MAP.md` + `git show c3c194a -- Docs/PROJECT_MAP.md` → the 4 previously-wrong coordinates (incl. §10's `:166`/`:189`, §9's `:49-63`/`:30`, §7's `yMax 3.2`) were already fixed pre-HEAD.

## Risks or blockers

- **Behavioural claims not executed.** §14's mode/output claims ("play mode, graphics required", "SMR vs GPU decided by `-nographics`") were verified only by reading the harness headers and `EnterPlaymode`/`nographics` greps — I did not run Unity, so I cannot confirm runtime behaviour (also the reason I flagged them as CONTRACT rather than WRONG).
- **Prefab binary content.** `.prefab`/`.asset` citations were checked as YAML text lines (`CrowdRoot.prefab:48-51`, `:100`); GUID targets were not resolved to their referenced assets, so "`_wallSdfAsset` points at `WallSdf.asset`" is unverified beyond the field being non-empty at the cited line.
- **§16's "surviving content went" mapping** was verified to exist in `WORK_STATE.md` §8 as a table with an entry per retired doc; I did not verify that each retired doc's content actually landed where that table says.
- `Assets/@Project/@Ex/`, `@Externals/`, `City/Resources/`, `City/Scripts/` are tracked-but-empty scaffolds (only `.meta`) not mentioned by the map — deliberately excluded from the defect table as non-issues, flagging only so §1's feature-folder list isn't mistaken for the tracked-directory set.

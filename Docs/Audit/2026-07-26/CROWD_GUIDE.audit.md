<!-- RECOVERED VERBATIM from session transcript
     source: 7be25ba5-e341-4238-8642-5da87b3a7962.jsonl line 1566 (task-notification <result> block)
     task-id: a8365a49be9158c81 | agent: "Verify CROWD_GUIDE exhaustively" | audited doc: Docs/CrowdCity/CROWD_GUIDE.md
     Body below is byte-identical to the transcript's <result> payload (HTML entities
     &lt; &gt; &amp; are as stored by the transcript). Nothing added, removed, or reworded. -->

All four verification streams are complete and I've independently confirmed the highest-impact findings. Final report:

## Conclusion

**~215 discrete claims checked → 38 defects: 8 FALSE, 23 IMPRECISE, 4 BOUNDARY, 2 STRUCTURE, 1 STALE, 0 RULE4.**

### FALSE — would misdirect a human debugging

| § / line | Claim | Actual truth |
|---|---|---|
| **§14 L551** | "`RenderInterpolate`는 어디서도 별도로 계측되지 않습니다 … 그래서 **순수 `SimTick`은 어느 쪽에서도** 범위만 잡히고 측정되지 않습니다" | **`CrowdProfileHarness` measures pure `SimTick` directly.** `CrowdProfileHarness.cs:249-256` wraps only `crowd.SimTick(Dt)`; it never calls `RenderInterpolate` (confirmed `MANIFEST §6`, `§8.8-c`). Raw output `simopt10k_gpupath_r1.txt:19` → `simtick_median_ms = 9.1601`, and `Seg.Total = 9.2103`. The "범위만" caveat is P95-only (`WORK_STATE.md:76` scopes it correctly). **Self-contradicts §14's own "증명하는 것: SimTick 내부의 단계별 벽시계 분포" and §16 row 1.** This tells a reader the project's headline 9.56 ms/tick figure doesn't exist. |
| **§4 L160** | "런타임 물리 질의가 이 유닛 CC 위치에 의존하지 않고(배회·AI raycast는 Unit 레이어를 마스크에서 제외…)" | Enumeration is incomplete. `CameraRoot.SphereCastWithOverflowHandling` fires a per-`LateUpdate` `Physics.SphereCastNonAlloc` with **`Physics.AllLayers`** (`CameraRoot.cs:418-425`) — it *does* hit the stranded follower/neutral capsules. Occlusion stays correct only because hits are filtered through `_buildingByCollider` (`:364`); stale capsules still consume hit-buffer slots and can drive the grow/overflow fallback (`:427-447`). The verdict "지금은 안전합니다" survives by a different mechanism than the one stated. |
| **§11 L416** | "예외는 SDF 에셋 **하나**입니다(미배선이면 `CC.Move`로 안전 폴백)" | There are **three** non-throwing unwired paths: (1) `_wallSdfAsset` (`CrowdRoot.cs:339-345`, the documented one); (2) `CrowdRenderer.Init` with unwired VAT mesh/material/textures → `LogError` + `return false` → silent SMR fallback (`CrowdRenderer.cs:82-88`); (3) `_crowdRenderer` itself null → GPU path silently stays off (`CrowdRoot.cs:876`). **(2) is exactly the failure behind §13 row 8's "크라우드가 아예 안 보인다".** |
| **§5 L180** | "`payloadCrc`는 … 그래서 '메타와 본체가 짝이 맞는가'를 **로드 시점에 검사할 수 있습니다**" | No runtime CRC check exists. `WallField.Load` validates byte **length** only (`WallField.cs:66`). `PayloadCrc` has exactly one consumer project-wide — the Editor validator (`WallFieldValidator.cs:96`). A same-size, wrong-content `WallSdf.bytes` loads silently and gives wrong wall geometry for the whole session. (Wording is modal, but a reader will conclude the check happens.) |
| **§4 L152** | "transform은 **렌더 프레임에만** 쓰이고, GPU 렌더가 활성이면 그마저도 리더만 씁니다" | Second half correct. First half false for leaders: leader transforms are written **three times inside `SimTick`** — Restore `CrowdRoot.cs:690`, SDF solve result `:1728`, clamp/Y-pin `:1197`. **Directly contradicts §7 L235** ("이동 결과가 … `transform.position`에 먼저 쓰이는 유일한 역할"). Also the gate keys on `_gpuRenderActive`, not `sdfActive`. |
| **§10 L346** | loop-close → drop duplicate row + modulo; "아니면 **한 행을 더 두고 clamp**합니다" | **The clamp path does not exist.** `HumanVatBaker.cs:214-220` *throws*: "셰이더(HumanVat)는 modulo wrap(LoopClose)만 지원하므로 clamp 베이크를 거부합니다". `rows = sampleCount - 1` is unconditional (`:222`); the shader has only `fmod` (`HumanVat.shader:62-63`). A non-looping clip is a hard bake failure, not a clamp bake. The guide faithfully reproduced the baker's own **stale** comments (`:20-21`, `:49`, `:65`) and its dead else-branch (`:624-630`). |
| **§15 L669** | 계측이 결정성을 깨지 않음을 "**diff에 삭제된 줄이 하나도 없다**"는 것으로 증명했다 | `git show --numstat 1485848` → `CrowdRoot.cs 14 0`, **`CrowdSimProfiler.cs 15 3`**. The commit message scopes the zero-deletion claim to `CrowdRoot.cs` only. **The tell derived at L671 ("`--stat`에 `-`가 있으면 검토 대상") fires on the very commit it cites** — anyone who checks concludes the guide is lying. |
| **§18 L800** | WORK_STATE's trailing sections are "**배경·상세 참조용** 절들" — list of 11 | Omits **§9 미착수 설계 사양**, which WORK_STATE itself declares 정본 three times (`:152`, `:270`, `:300`), and §4 리팩토링 세션에게. Mislabels an authoritative section as background. §18 exists to be the human's map of the AI docs, so this sends a person past the one authoritative spec section. |

### IMPRECISE (23)

**Debugging-relevant (would slow or misdirect a hunt):**
- **§13 L535** — "미로드면 **조용히** `CC.Move` 폴백이다": both load-failure branches `Debug.LogError` and name the fallback (`CrowdRoot.cs:342-345`, `:353-361`). Only the per-move fallback (`:1716`) is silent. Suppresses the cheapest diagnostic.
- **§13 L531** — "정상 설계에서는 불가능하다": true only for the initial `Init`. `_gpuRenderActive` is latched at `:888` and never re-evaluated, while `Render` early-returns on `!_active || !isActiveAndEnabled` (`CrowdRenderer.cs:173`), `OnDisable` releases buffers (`:246-249`), and a failed `OnEnable` re-`Init` (`:256-258`) leaves `_active = false` — with rigs already gone, and no log.
- **§3 L125 + §9 L290** — "영입·전환·제거는 모두 **틱 시작 상태**로 계산": true for `Team`/`IsLeader`/counts (mutated only in ⑨), **false for positions** — ⑤ mirror and ⑥ rebuild precede ⑦⑧, so resolvers see this tick's post-move `_buffer.Pos` (`MirrorPositionsToBuffer:1741`, `Rebuild:703`). Correct conclusion, wrong reason; stated twice.
- **§13 L527** — "성능 하네스는 이 캡을 강제로 켠다" at the entry-point row: only 2 harnesses do (`CrowdPerfHarness.cs:235`, `CrowdPerfHarnessP95.cs:290`); `CrowdProfileHarness`/`CrowdOracleHarness` default to config.
- **§5 L176** — floors/grass "**자연히** 제외": the real guard is the explicit `BandBottomOffset = 0.3f` (`WallFieldBaker.cs:41,54`). Hidden consequence: **any obstacle under ~0.3 m is invisible to the SDF.**
- **§15 L645 / §14 L576** — the `simtick_median_ms` name collision is never mentioned: `CrowdProfileHarness`'s identically named column **is** a real median of pure `SimTick`. A reader applying §15's trap to a profile-harness file distrusts a correct number.

**Others:** §3 L116 (ten-phase list omits the `PrevPos` snapshot `:680` and Restore `:688-691` — same omission as the code's own docstring); §7 L233 ("매 틱 **4번**" → rival loop is `t = 1..teamCount`, ≤**3**); §7 L235 (leader-only `ApplyHorizontalMove` holds only while SDF active; fallback adds `:1480`/`:1658`); §8 L267 ("거리에 **반비례**" → linear ramp `(1 − d/R)`, `SteeringForceJob.cs:133`); §9 L304 ("전용 테스트가 검증" → `CombatResolverTests.cs:301-336` permutes **agent-insertion order only**; pair-enumeration order is a fixed nested loop, permuted by no test); §4 L158 (GPU → actually GPU **+SDF**); §12 L505 (present pass "touches Transform/Animator" is stale — both are gated off on the shipping path); §2 L99 ("스폰에 걸린 벽시계 시간이 누산기로 새어 들어와" — spawn frames return before `_accumulator +=`; what's discarded is one frame's delta); §10 L341 (leader shadow draw is conditional, `if (packed &gt; 0)`; API is `Graphics.RenderMeshPrimitives`); §10 L354 (per-unit immediate destroy bounds peak memory only on the chunked path — every harness uses single-frame `SpawnInitial`); §17 L719 ("스크립트 play-smoke로 대체" — no such script is committed, only Editor-only hooks); §14 L556 (Burst synchronous is **forced**, not asserted); §14 L561 ("파일명과 라벨만이 정확한 표시" — both are operator-supplied free text; the derived `# SeparationVisitBudget override:` line is ignored); §14 L587 ("헤드리스라 GPU 경로가 성립하지 않으므로" — a property of the `-nographics` invocation, not of the harness; nothing in `CrowdOracleHarness` forces it); §15 L615 (refusal count equals the **sum of populations across all scales** in a run — `MANIFEST §8.3`: 15008 = 5004 + 10004 — so the default 2-scale sweep shows ~1.5× one population); §16 L696 (column shortened to `simtick`, dropping the "median" trap); §0 L25 / §18 L730 (states `WORK_STATE.md` is Korean as design, but `CLAUDE.md` §1.1 mandates English for it and says "where a file disagrees, the file is what changes").

### BOUNDARY — owns a fact it should cite

| § / line | Quote | Owner |
|---|---|---|
| §8 L284, §14 L550 | "`SeparationVisitBudget = 48`을 리플렉션으로 넣습니다" | `WORK_STATE.md:68` (gate section) |
| **§15 L623** | "실제 변경 신호는 픽셀 수로 **수만 배**, 채널 델타로 수십 배" | `WORK_STATE.md:63` says **약 2,200배** / 약 43배 — the pixel ratio is overstated ~10×, and these are render-gate figures the gate section owns |
| §2 L95 | "틱당 약 −13 ms가 프레임 −97 ms로 나타났고" | `MANIFEST §7.7-(a)`. Also stated **without inline harness/mode**, violating the guide's own §16 rule 1 |
| §15 L657, §14 L562 | "`CCMove`는 **다섯 지점**을 합산하는 단일 버킷" | `MANIFEST §5:129` + `CrowdSimProfiler.cs:16-19`. Correct today (verified: 5 `Begin(Seg.CCMove)` sites); rots silently on a sixth |

### STRUCTURE / STALE / RULE4
- **STRUCTURE:** TOC (L39-56) omits §0. L6 promises collapsible blocks for "긴 원시 근거·기각된 대안" but only one `&lt;details&gt;` exists, holding only rejected alternatives.
- **STALE (1):** §15 L659 "이 정정을 **계획 문서 본문에** 남긴 이유" — `SIM_OPT_10K_PLAN.md` was deleted in `0733deb`; the correction now lives in `WORK_STATE.md:54` and `MANIFEST §5`. Every other retired-doc mention (L31, L164, L461, L675, L808) is correctly framed as retired — no live links to deleted files.
- **RULE4: clean.** No commit hashes anywhere in the guide, and no "this commit / current branch / latest measurement / next pass" references.

### Verdict

**Yes, with four corrections first.** This is a strong document — considerably better than its "never cross-verified" provenance suggests.

What is genuinely solid: **§13 is the best part** — all 16 symptom→cause rows map correctly, and **no row sends a person to the wrong file, array, or flag**; every cited identifier exists and controls the named symptom (`_visualYaw` 7 writes / `_visualSpeed01` 5 writes independently confirmed, and the guide correctly *defers* those counts to `WORK_STATE.md` rather than owning them). §11 is fully accurate including all 8 lifecycle rows and every EventManager detail (down to `_callback -= callback; _callback += callback;` moving a re-subscribed delegate to the end). §12's serial-equivalence argument, §9's elimination-graph section, §5's solver contract, §6's grid section, §10's fallback enumeration (all 7 conditions, none missing), and all 10 §17 rows check out. Boundary discipline is real: zero hashes, zero gate values, zero invariant lists.

The four that must be fixed before it's safe to debug alongside: **§14 L551** (denies the existence of the project's only pure-`SimTick` measurement), **§4 L160** (the one runtime physics query that *does* see the stranded capsules is missing — someone extending `CameraRoot`'s occlusion code will act on the wrong premise), **§11 L416** (the second silent fallback it omits is precisely §13 row 8's failure mode), and **§15 L669** (verifiably false against the commit it names — the kind of error that costs trust in the whole section).

Two of the FALSE items are **mirrored in the code they describe**, so fixing only the doc leaves the next reader to re-derive the same error: `HumanVatBaker.cs:20-21,49,65,624-630` still documents the removed clamp scheme, and `CrowdRoot.cs:654-657`'s `SimTick` docstring carries the same phase-list omission plus a stale "③리더 CC.Move".

## Changed files

None. Read-only throughout; working tree clean at `d136350`.

## Verification commands and results

```
git show --numstat 1485848              → CrowdRoot.cs 14 0 ; CrowdSimProfiler.cs 15 3   (§15 L669 FALSE)
git grep -c -F 'Begin(CrowdSimProfiler.Seg.CCMove)' HEAD -- '*CrowdRoot.cs'  → 5
git grep -c -F 'if (!_gpuRenderActive)' HEAD -- '*CrowdRoot.cs'              → 7
grep -rn 'PayloadCrc|payloadCrc' Assets/@Project --include=*.cs
        → baker + WallSdfAsset + WallFieldValidator.cs:96 only; no runtime consumer  (§5 L180 FALSE)
grep -rn '\.Move(' Assets/@Project --include=*.cs   → one real site: CrowdRoot.cs:1733 (else-branch)
grep -rn 'StartCoroutine|IEnumerator' (non-Editor)  → none  (lifecycle table row 8 correct)
grep -rn 'ClearAll' → only EventManager.cs:131 under [RuntimeInitializeOnLoadMethod(SubsystemRegistration)]
head -20 Docs/CrowdCity/Perf/simopt10k_gpupath_r1.txt
        → line 7 hardcoded "edit-mode headless 측정" in a run with NO -nographics  (§14 L561 tell TRUE)
        → line 19  10000,10004,9.1601,...   = pure-SimTick median  (§14 L551 FALSE)
grep '^#\{1,4\} ' CROWD_GUIDE.md | sed 's/^#* //' | sort | uniq -d  → empty (no duplicate headings)
grep -c '&lt;details&gt;' / '&lt;/details&gt;'  → 1/1 balanced (the L6 hit is inside backticks)
awk table-column check → all tables internally consistent; all 25 md links resolve
```
Read in full or in the relevant regions: `CrowdRoot.cs`, `CrowdRenderer.cs`, `Human.cs`, `GameplayRoot.cs`, `GameSceneController.cs`, `CameraRoot.cs`, `HudRoot.cs`, `GameSession.cs`, `EventManager.cs`, `ResourceLoader.cs`, all of `Crowd/Core/`, `WallFieldBaker.cs`, `WallFieldValidator.cs`, `HumanVatBaker.cs`, `HumanVat.shader`, all 6 harnesses + 2 validators in `Game/Editor/`, all 5 kernel test files, `GameConfigSO.cs`, and the live assets `GameConfig.asset` / `WallSdf.asset` / `CrowdRoot.prefab` / `Human.prefab`. Cross-checked against `WORK_STATE.md`, `PROJECT_MAP.md`, `Perf/MANIFEST.md` §1-§8.8, and `git show 0733deb^:Docs/CrowdCity/GameLogicExplainer.html`.

## Risks or blockers

**Empirical — asserted in the guide, not derivable by reading. These must be labelled, not accepted:**
1. **§8 L270/L272** — that LeaderRadial produces *stripes*, and that hash-bucket occupancy inflation is *the cause*. The bucket-vs-cell counting is a verified code fact (`CrowdRoot.cs:1281,1302`); the visual symptom and the causal link rest on past observation with **no committed screenshot or measurement artifact anywhere**. Substantiating them requires re-running the shot harness with `separationMode=1`.
2. **§8 L278-281** — that the visit-budget cap caused visible jitter. Mechanism is consistent with the code (truncation returns the first *budget* matches in LIFO scan order); the symptom is unrecorded.
3. **§2 L95, §12 L512** — both perf figures. Consistent with `MANIFEST §7.7-(a)` / `§8.5`, but I verified citation consistency only, not the measurements.
4. **§15 L615** — the Destroy-refusal counts. Independently recorded twice (`dcb3bfb`: 704/704; `MANIFEST §8.3`: 15008 = 5004+10004), and derivable (one `Destroy` per unit, `Human.cs:75-80`). Caveat: `CrowdRoot` has three other `Destroy` sites (`:545`, `:607`, `:851`) that would also be refused in edit mode — the measured runs show they contributed 0, so the equality holds but not by the guide's stated reasoning alone.
5. **§2 L101** — "두 경로가 같은 헬퍼를 같은 순서로 호출하므로 최종 상태는 동일합니다". Structurally sound (all `_rng` draws and `CheckSphere` are confined to `PrepareSpawnPlacements`; the chunked path only splits the neutral range monotonically), but **the oracle byte-gate only ever exercises the synchronous path** — chunked byte-identity is never measured.
6. **§4/§7's "`CC.Move` is never called"** — the last link is `WallField.Load` succeeding at runtime, which reading cannot close. The profile/oracle harnesses hard-assert `CcMoveFallbacks == 0`, which is the strongest available evidence.
7. **§10's two rejected alternatives** — design rationale, unverifiable by construction; correctly presented as such in a `기각한 대안` block.

**Coupled defects outside this doc (flagging, not fixing):**
- `WallField.Load` verifying only byte length is a **real code gap**, not just a doc error — either scope the §5 sentence to the validator or add `Crc32(bytes) == asset.PayloadCrc`.
- `HumanVatBaker.cs` and `CrowdRoot.cs:654-657` carry the same two FALSE statements as the guide (see verdict).
- `WORK_STATE.md`'s invariant table does **not** own the overshoot invariant (`followerMaxAccel &gt;= cohesionGain × maxSpeed² / arriveRadius`), so §13 row 6 has to point at the guide's own §7 instead of the canonical location. Per `CLAUDE.md` §1.1 that fact belongs in `WORK_STATE.md`.
- `CrowdRoot.cs:1411` assigns a `Transform followerTransform` local that is never used (dead, left from the pre-`buffer.Pos` era).

**Freshness:** `WORK_STATE.md`'s stamp is `9f23df4`, HEAD is `d136350`; `git diff --stat 9f23df4..HEAD -- Assets/` is empty (4 docs-only commits), so the docs-only carve-out applies and WORK_STATE was safe to use as an authority throughout.

**Four config-conditional claims** are stated unconditionally and flip on a tuning change, with no hint of the dependency: §7 L235 (`_useSdfSolver`), §9 L298 (`RateLimitConversion`), §9 L310 (`LeaderProtection`), §9 L323 (survivor routing, also `LeaderProtection`). All four are true for the current live asset.

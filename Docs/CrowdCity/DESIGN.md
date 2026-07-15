# Crowd City MVP — Final Technical Design (v4, post round-3 review)

Round-1 (CODEX REVISE/15, Claude REVISE/13), round-2 (CODEX REVISE/8, Claude APPROVE/4-minor), round-3 (CODEX REVISE/8, Claude APPROVE/3-minor) findings incorporated. Marks: ⟲ v2, ⟲⟲ v3, ⟲⟲⟲ v4.

**AUTHORITY RULE: INTERFACES.md is authoritative for all public signatures, method names, and wave assignments wherever the two documents differ.**

**LATEST APPROVED CONTRACT SUPERSESSION (2026-07-15):** The user explicitly selected independent, Hud-owned world-space leader labels after reconsidering the temporary leader-attached direction. `HudRoot` owns every leader count label, its runtime TMP materials, screen HUD, and rival off-screen markers; `CrowdRoot` remains the simulation/runtime-model owner and exposes leader transforms for parent-level binding. This intentionally replaces the immediately preceding ownership direction; it is the selected model, not an architecture-guide exception.

Target: Voodoo's "Crowd City" MVP clone inside `Assets/@Project/Scenes/GameScene.unity` of Unity project **AF_CrowdCity** (`D:\UNITY\UNITY_PROJECT\ActionFitPro\AF_CrowdCity`). Playable by pressing Play with zero manual steps after our editor-driven setup runs.

## 0. Verified environment facts (⟲ corrected against disk after round-1)

- Unity **6000.3.9f1**, URP 17.3.0, Linear. **Input System 1.18.0 ONLY**. No UniTask/DOTween/Cinemachine/Addressables. uGUI 2.0; TMP Essential Resources are tracked project infrastructure under `Assets/TextMesh Pro/**`, and all runtime UI text uses TextMesh Pro. `HudRoot.Initialize` requires `Resources/Fonts & Materials/LiberationSans SDF - Fallback` and fails fast if it is missing. No new packages.
- Editor is running with GameScene open (clean). All scene/asset authoring via editor APIs through unity-cli (`exec`, `refresh --compile`, `console`, `test`, `play`, `screenshot`). Zero hand-authored YAML.
- ⟲ **EventManager (real API, verified by direct read)**: `public static class EventManager` — global static entry point. Track 1 events: `GetPublisher<T>() : IEventPublisher<T>` (`Publish()`, `Publish(T)`), `GetSubscriber<T>() : IEventSubscriber<T>` (`Subscribe(Action<T>) : IDisposable`, `Unsubscribe(Action<T>)`; re-subscribing the same delegate replaces, never duplicates). Track 2 queries: `GetProvider<TReq,TRes>()`/`GetRequester<TReq,TRes>()` — **unused by this design** (CLAUDE.md: no QueryBus before a real need). `ClearAll()` clears every channel; `[RuntimeInitializeOnLoadMethod(SubsystemRegistration)]` auto-clears at play-session start (domain-reload-off safe). Dispatch is synchronous; **per-subscriber exception isolation exists only `#if UNITY_EDITOR`** — in player builds one throwing subscriber breaks the rest ⇒ implementation rule: bus handlers must be trivial, non-throwing updates.
- ⟲ **Surfaced convention conflict (CLAUDE.md §0.1 duty)**: TEAM_ARCHITECTURE_GUIDE warns against a *global static* EventBus, but the project's existing EventManager IS static. Per CLAUDE.md §0.1 we follow the existing implementation within this request's scope and do not refactor it; the conflict is reported, not hidden. Mitigations built in: token discipline (every Subscribe token disposed in the subscriber's paired lifecycle), SubsystemRegistration auto-clear at play-session start. ⟲⟲⟲ GameSession.Dispose releases only its OWN tokens — no ClearAll anywhere in session code.
- Scene: roots = Main Camera (default transform, FOV 60), Directional Light, Global Volume, GameArea. ⟲ `SceneRoots.m_Roots` already lists all four — **no anomaly exists; do not code around one**. `GameArea/Human` = transform-only wrapper; its single child **`Human_Base` (GO 1157632472153579824) is the unpacked FBX root** with children: mesh node `Human_Base` (SkinnedMeshRenderer, rot -90°X, scale 0.0001 — visually correct per user) and armature `HumanAnimation` (41 mixamo bones). No Animator anywhere. `GameArea/City` children (no colliders): Buildings, Ground, Parks, RoadMarks, StreetProps, Vehicles. No UI/EventSystem. GameScene is scene 0 in EditorBuildSettings (LoadScene restart valid).
- Human_Base.fbx (guid dcc1660c…): Generic rig (`animationType: 2`), `avatarSetup: 0` — a null-Avatar Animator plays Generic clips by transform path. Contains one take-clip (name contains pipes, ends `Just walk`); material sub-asset `CrowdBase`. `clipAnimations: []` → loop not set. `Assets/@Project/Human/Scripts/Human.cs` = empty stub (will be rewritten as the unit class).

## 1. Decision Log (CLAUDE.md §1)

```txt
1. Target feature owner: Crowd (CrowdRoot: crowds, neutrals, sim kernel, runtime CrowdModels/Human clones, crowd events; no UI/TMP ownership) / Game (GameSceneController, GameSession, GameplayRoot, InputRoot, CameraRoot, GameConfigSO, editor setup/validator) / Hud (HudRoot: independent world leader labels, their runtime TMP materials, screen HUD, rival off-screen markers)
2. New file location: Assets/@Project/Crowd/{Core, Scripts, Contracts/Events, Tests/Editor}, Assets/@Project/Game/{Scripts, Contracts/Runtime, Editor}, Assets/@Project/Hud/Scripts/{HudRoot.cs, TMPTextStyleSO.cs}, Assets/@Project/Hud/CrowdCountTextStyle.asset, Assets/@Project/Human/Scripts/Human.cs, Assets/@Project/Human/Materials + Animations, Assets/TextMesh Pro (TMP Essential Resources)
3. State location: GameConfigSO/TMPTextStyleSO assets = immutable source config only; authoritative live count in CrowdModel; label presentation cache/runtime TMP materials in HudRoot; session state in GameSession; no SaveData; no Server (no economy)
4. Communication method: parent→child direct calls; child→parent C# events; GameplayRoot binds all leader transforms to HudRoot through CrowdLabelBinding/BindLeaderLabels; static EventManager bus remains limited to the two crowd-domain facts consumed by Camera/GameSession/Hud label-marker-ranking presentation (justification §3); QueryBus track unused
5. Affected files: §6 table plus the approved Hud-owned label/style assets; editor-authored GameScene/prefab/TMP/style assets remain owned by E1
```

Justified exceptions recorded: (a) two asmdefs (`Project.CrowdCity.Core` + its Editor test asmdef) — NUnit EditMode tests cannot reference Assembly-CSharp; mirrors the repo's existing EventManager test pattern; owner: Crowd; (b) ⟲ bus event cadence bound: worst case 1 event × 4 crowds per sim tick, published only on actual count change, coalesced — not per-agent/per-frame bulk data; camera zoom smoothing is time-based and independent of event arrival rate; (c) ⟲⟲ **formal exception record for using the existing static EventManager** (CLAUDE.md §13 format) — Owner: Crowd feature (publisher) with Game/Hud boundary subscribers. Reason: the bus is the project's pre-built EventBus that the user explicitly instructed us to use ("이벤트 버스 패턴 약식은 이미 만들어 뒀어. EventManager 봐봐"); CLAUDE.md §0.2 permits a pre-existing EventBus for facts observed by 2+ boundary objects, which both events satisfy; we add zero code to EventManager itself (channels are implicit per payload type; payload structs live in the Crowd feature). Impact scope: exactly two event types, boundary objects only. Removal condition: if the team refactors EventManager to the guide's instance-based session-bus shape, the two `GetPublisher/GetSubscriber` call sites and token fields migrate mechanically. This is surfaced (not hidden) as a conflict between the guide's "no global static EventBus" ideal and the existing implementation, per CLAUDE.md §0.1.

## 2. Gameplay rules

- **Match**: 120 s countdown, fixed-timestep simulation (§4). `Ready` (world frozen, "DRAG TO START") → first drag → `Playing` → `Finished` on: timer 0 (biggest crowd wins, ties favor player), player eliminated (defeat), all rivals eliminated (victory). ⟲ Defeat precedence: if the player and rivals are eliminated in the same tick's batch, defeat wins.
- **Crowds**: player (team 0, blue #2E86FF) + 3 AI rivals (red/orange/green), neutrals white (team -1). Everyone starts as a lone leader.
- **Movement**: hold+drag = direction; heading persists on release. Leader speed 5 m/s, turn 720°/s, `CharacterController` (radius 0.35, skin 0.08, no gravity, fixed Y) vs city MeshColliders. Followers: transform steering to golden-angle slots (`r=spacing·√i, θ=i·2.39996`, spacing 0.6) + same-crowd separation via grid, max 6.5 m/s. Follower wall-clipping = accepted MVP compromise (documented). Walls do NOT block recruiting/combat (original-game feel; documented decision).
- **Recruiting**: neutral within 1.2 m of ANY member joins that crowd instantly; contested: nearest member's crowd, tie → lowest agent id. Central, deterministic.
- **Combat (conversion, original steals members)**: while two crowds have members within 1.0 m, the strictly larger converts from the smaller at `10 · clamp01(touchingPairs/8)` members/s, fractional accumulator per ordered pair (state persists across ticks, reset when contact breaks). Equal sizes inert; flow re-evaluated per tick. ⟲⟲ **Snapshot purity**: conversion direction and rates for ALL crowd pairs are computed from the immutable start-of-step counts (never from counts adjusted mid-step), victims are globally arbitrated (each agent converted at most once per step, nearest-to-contact, tie lowest id), then committed once — outcome is invariant under pair-enumeration or agent-insertion permutation (tested). **Leader elimination** (evaluated AFTER conversions commit, on end-of-step counts): crowd reduced to a lone leader with ≥1 strictly-larger crowd's member within 1.0 m of the leader → eliminated; eliminator = the larger crowd owning the nearest such member, tie → lowest crowd id; ⟲⟲ the leader — player included — converts to the eliminator like any member (kernel counts stay consistent with tests); the buffer's `IsLeader` flag is authoritative and flips false on demotion; CrowdRoot's commit destroys the ex-leader's CharacterController component, turns off its shadow casting, and moves the clone into the winner's followers. Two lone leaders (1v1) = inert.
- **Neutrals**: 150, rejection-sampled spawn against ALL city colliders (not only Buildings); idle wander 0.5 m/s, ⟲ new direction every 2–5 s rejected if a 1.5 m raycast hits a city collider.
- **Camera**: Main Camera driven by CameraRoot: pitch ~55°, fixed yaw, smooth follow; distance `16 + 0.8·√playerCount` clamp [16, 40]; ⟲⟲ on player elimination latch an independent pose snapshot (stored position/rotation — NOT the leader transform, which walks off with the eliminator).
- ⟲⟲ **Spawn/placement contract** (zero-manual-step + reproducible): city walkable region = bounds of the `GameArea/City/Ground` renderer shrunk 2 m (⟲⟲⟲ Ground child is REQUIRED — hard error if missing; no fallback). Player leader at region center, rivals at 3 of the 4 corner points inset 15% (deterministic assignment by team id). Position validity = `!Physics.CheckSphere(pos + 0.9·up, 0.6f)`; invalid leader spots probe outward on a 1 m spiral (≤50 probes). Neutrals: seeded `System.Random(config.Seed)` (default 12345), rejection sampling ≤20 attempts each, skipped on failure with a summary warning. ⟲⟲⟲ ALL placements (leaders + neutrals) are computed BEFORE anything is instantiated, so CheckSphere can only ever hit city colliders (no runtime CharacterControllers exist yet — no mask needed). `RejectionRate` = rejected candidate attempts ÷ total candidate attempts during neutral sampling; asserted ≤ 0.8 by the play-smoke (NOT the edit-time validator, which cannot run the sampler).
- **HUD/presentation**: runtime uGUI with `TextMeshProUGUI` (CanvasScaler, no EventSystem/no Buttons): timer top-center, 4-row leaderboard top-right, start hint, result overlay (+standings, "R or tap to restart"), and rival off-screen direction/count markers. `HudRoot` also owns independent world-space `TextMeshPro` labels for all living leaders: Bold white face, team-color outline with width from `TMPTextStyleSO`, and a team-colored player arrow. `GameplayRoot` supplies all leader transforms through `CrowdLabelBinding`; HudRoot updates count/elimination from the existing crowd events and, in its ordered LateUpdate, synchronizes each label to leader position + offset and the latest camera rotation before updating rival markers.
- **Restart**: `R`/tap during `Finished` → GameplayRoot raises `ReloadRequested` (policy: gated to Finished) → GameSceneController executes `SceneManager.LoadScene` (mechanical scene-level op only).
- **Animation**: ⟲ editor setup builds explicit clip from `importer.defaultClipAnimations` (copies takeName + frame range — never hand-typed), renames to `HumanWalk`, `loopTime=true`, `SaveAndReimport()`, then **re-fetches the clip via `LoadAllAssetRepresentationsAtPath` filtered to AnimationClip (fileIDs churn on rename — never cache them)**; creates single-state looping `HumanWalk.controller`; **Animator goes on the `Human_Base` FBX-root child (NOT the `Human` wrapper)** so Generic curve paths (`HumanAnimation/mixamorig:…`) resolve; null Avatar is fine. `Human.cs` finds it via `GetComponentInChildren<Animator>`. Clones: activate first, then `animator.Play(stateHash, 0, Random.value)` (Animator binds on enable). `Animator.speed` ∝ move speed.
- ⟲ **Team visuals — shared materials, not MaterialPropertyBlock** (MPB would break SRP batching on every SMR): setup generates 5 material assets in `Assets/@Project/Human/Materials/` (Team_Player/RivalA/RivalB/RivalC/Neutral) cloned from the FBX's `CrowdBase` material with `_BaseColor` set (validator first asserts shader == "Universal Render Pipeline/Lit" && HasProperty("_BaseColor")); recolor = `smr.sharedMaterial = teamMaterial`. Shadows: leaders cast, followers/neutrals don't (set per role in `Human.Init`).

## 3. Ownership & communication

```
GameScene
└─ GameSceneController (only new scene GO; serialized: GameConfigSO, TMPTextStyleSO, humanPrefab, mainCamera, cityRoot, buildingOccludedMaterial)
   │  Awake: validate refs. Start: assemble (below). Executes reload on request. NOTHING else.
   │  ⟲⟲ OnDestroy (EXACT teardown sequence): ① unbind controller-level handlers (ReloadRequested) ② gameplayRoot.Shutdown(): unbind its C# bindings, then HudRoot.Shutdown → CameraRoot.Shutdown → CrowdRoot.Shutdown → InputRoot.Shutdown (presentation → sim → input; each idempotent), then destroy its child root GOs ③ session.Dispose() LAST (session deliberately outlives roots so late events during root teardown are still handled; token disposal is order-safe by EventManager design)
   ├─ GameSession (plain C#)  — match state machine + timer + standings cache; subscribes both bus events (tokens); ⟲⟲ Dispose = its OWN tokens only — NO ClearAll (clearing channels the session never acquired exceeds its ownership; SubsystemRegistration auto-clear covers play-session boundaries, and every subscriber disposes its tokens on scene unload)
   └─ GameplayRoot (child GO, MB) — creates + binds + DRIVES the ordered tick
        ├─ InputRoot  (MB, Game) : null-safe Mouse/Touchscreen polling (no EnhancedTouch, no EventSystem); heading persistence; FirstDrag / RestartTapped C# events; editor-only synthetic-heading hook for play-smoke
        ├─ CrowdRoot  (MB, Crowd): owns kernel sim, authoritative CrowdModels and Human clones; publishes the two bus events; no TMPro/view/material ownership
        ├─ CameraRoot (MB, Game) : drives Main Camera; subscribes both bus events
        └─ HudRoot    (MB, Hud)  : builds screen uGUI + independent world leader labels + rival off-screen markers; owns their TMP materials, subscribes both bus events, reads IGameSessionReadOnly
```

⟲ **Pinned init order** (fixes subscribe-before-first-publish): controller.Start → `new GameSession(config)` → create GameplayRoot → `gameplayRoot.Initialize(...)` which: creates all four roots → calls each root's `Initialize(...)` (HudRoot receives the text style/camera and registers its bus subscriptions) → `session.Initialize()` and C# event binding → `crowdRoot.SpawnInitial()` (first coalesced count publish after every bus subscriber is listening) → Camera target binding → `hudRoot.BindLeaderLabels(...)` with all living teams 0..3. Label binding reads the current session snapshot because it occurs after the initial count publish and creates rival markers for teams 1..3.

⟲ **Pinned tick order** (single driver, no script-order dependence): `GameplayRoot.Update()`: ① ⟲⟲⟲ `inputRoot.Poll()` (explicit call — InputRoot has NO self-Update, so input freshness never depends on Unity script execution order); ② restart gate; ③ fixed-step loop (dt=0.02 s accumulator, ≤4 steps/frame): per step `crowdRoot.SetPlayerHeading(...)`, `session.Tick(step)`, `crowdRoot.SimTick(step)`; presentation (CameraRoot.LateUpdate, HUD timer poll) reads freely afterwards. Determinism contract: identical initial state + identical per-step inputs ⇒ identical outcomes (kernel-internal; no cross-run replay claim).

⟲ **Re-entrancy contract**: CrowdRoot publishes all events only AFTER all tick mutation completes (last phase of SimTick); it never updates presentation directly. Session state changes triggered by bus events (e.g. elimination → Finished → `StateChanged`) reach CrowdRoot as `OnMatchStateChanged` which only sets `_pendingState`, consumed at the top of the next SimTick. HudRoot count/elimination handlers update label/marker text or presentation dirty flags only. No handler mutates sim state synchronously.

**Bus usage (the only two events; both already-happened facts with 3 boundary observers each)**:
- `CrowdCountChangedEvent { int CrowdId; int MemberCount; }` — publisher CrowdRoot (coalesced ≤1/crowd/tick, only on change). Subscribers: HudRoot, CameraRoot, GameSession.
- `CrowdEliminatedEvent { int CrowdId; int ByCrowdId; }` — publisher CrowdRoot. Subscribers: GameSession, HudRoot, CameraRoot.
- ⟲ Location: `Assets/@Project/Crowd/Contracts/Events/CrowdEvents.cs` (readonly structs; other features subscribe ⇒ Contracts per guide). ⟲ `IGameSessionReadOnly` (State, TimeRemaining, Standings, WinnerTeam) in `Assets/@Project/Game/Contracts/Runtime/`. Leaf `Human` never touches the bus. Session publishes nothing on the bus (state flows via C# event bound at GameplayRoot).

**Human ownership (⟲ explicit)**: `Human` is a Crowd-owned passive leaf/view — zero upward references, zero subscriptions, driven exclusively by CrowdRoot direct calls. World labels are separate HudRoot children that only retain bound leader transforms; no UI component is attached to the Human root. `Human` stays in `Human/Scripts/` for asset cohesion.

## 4. Simulation kernel (`Project.CrowdCity.Core`, pure C#, deterministic, alloc-free after warmup)

- `AgentBuffer` (SoA: id, team, isLeader, alive, pos Vector2) — ⟲ authority split pinned: Unity transforms author POSITIONS (leader CC.Move, follower steering) which are mirrored into the buffer at step start; the buffer is authoritative for TEAM/ALIVE; resolvers read the buffer as an immutable snapshot and emit outcomes; CrowdRoot commits outcomes atomically (buffer + CrowdModel + visuals) after resolve returns. Nothing mutates the buffer mid-resolve.
- `SpatialGrid` (uniform hash, cell 1.5 m): `Rebuild(buffer)`, `QueryCircle(pos, r, List<int>)`; also serves follower separation.
- `RecruitResolver.Resolve(buffer, grid, radius, out List<Recruit>)` — one claim per neutral: nearest member, tie lowest agent id.
- `CombatResolver.Resolve(buffer, grid, in SimTuning, dt, CombatState, CombatOutcome)` — ⟲⟲ snapshot-pure: conversion direction and budgets for ALL crowd pairs derive from immutable start-of-step counts; victims globally arbitrated (one conversion per agent per step, nearest-to-contact, tie lowest id; leaders never converted in the conversion phase); commit once; elimination pass afterwards on end-of-step counts with explicit eliminator attribution (§2). Outcome invariant under pair-enumeration/agent-order permutation (dedicated tests). Buffer authoritative for team AND ⟲⟲ IsLeader; resolver never mutates the buffer.
- `MatchRules.ResolveStandings(counts, eliminated) → standings + winner` (ties favor team 0); timer lives in GameSession but end-resolution math is here (testable).
- `FollowerSteering.SlotOffset(index, spacing)` pure.

`CrowdRoot.SimTick(dt, playerHeading)` phases: ① consume `_pendingState`; ② headings (player passed in; rivals via `RivalAiDriver` plain-C# at 0.4 s cadence counted in sim steps: flee >1.15×, hunt <0.7× when own ≥15, else nearest neutral density; 3-ray wall avoidance) ③ move leaders (CC.Move) ④ steer followers (slots + separation) ⑤ mirror positions → buffer ⑥ grid rebuild ⑦ recruit ⑧ combat ⑨ commit outcomes atomically (sharedMaterial swaps, membership moves between CrowdModels; elimination: IsLeader→false in buffer, ex-leader clone loses CharacterController + shadow casting and joins the eliminator's followers — player leader identically; CameraRoot has already latched a pose snapshot via the elimination event) ⑩ publish coalesced events in ⟲⟲⟲ PINNED ORDER: all `CrowdCountChangedEvent`s first (team id ascending), then `CrowdEliminatedEvent`s — player's elimination FIRST if present, remaining by team id. With GameSession.Finish idempotent, this makes defeat precedence enforceable under synchronous dispatch (a rival's elimination can never produce a premature victory before the player's simultaneous elimination is seen, standings never use stale counts, and HudRoot receives authoritative label/marker values).

⟲ **Perf envelope corrected**: total humans = 4 leaders + 150 neutrals = **154 max** (population conserved; followers ARE converted neutrals). 154 SMRs (41 bones) + 154 Animators, SRP-batched via shared materials, follower/neutral shadows off. Gate-4 criterion: ≥60 fps sustained in editor play at full population. Fallback ladder if missed: pause Animators on stationary neutrals → reduce neutralCount to 120.

## 5. Editor-driven setup & verification

`Assets/@Project/Game/Editor/GameSceneSetup.cs` — `[MenuItem]` + `public static void Apply()`, **operationally idempotent** (⟲ pinned): every step is load-or-create + converge-to-desired-state; saves only when something changed.
1. FBX clip: copy `defaultClipAnimations` → name `HumanWalk`, `loopTime=true` → assign → `SaveAndReimport()`.
2. Re-fetch clip (type-filtered, never by cached fileID). Load-or-create `Assets/@Project/Human/Animations/HumanWalk.controller`; enforce exactly one layer/one state/motion=clip (converge, don't append).
3. Validate FBX material (URP Lit + `_BaseColor`); load-or-create the 5 team materials with configured colors; load-or-create the immutable `CrowdCountTextStyle.asset` source settings used by HudRoot's world labels. TMP Essential Resources/font files are pre-existing tracked project infrastructure, not GameSceneSetup output.
4. Scene: ensure Animator on `GameArea/Human/Human_Base` (controller assigned, no root motion, cullingMode CullUpdateTransforms); ensure MeshColliders on Buildings/StreetProps/Vehicles/Parks (Ground/RoadMarks none). ⟲⟲⟲ Collider sanity is measured by the play-smoke's spawn-rejection assertion (`crowdRoot.RejectionRate ≤ 0.8`, §2 spawn contract), not by the edit-time validator and not by bounds heuristics.
5. Load-or-create `Assets/@Project/Game/GameConfig.asset` (defaults §2; team material refs wired).
6. Ensure root GO `GameSceneController` + component + serialized refs (`config`, `crowdCountTextStyle`, `humanPrefab`, `mainCamera`, `cityRoot`, `buildingOccludedMaterial`).
7. `EditorSceneManager.MarkSceneDirty` + save scene + `AssetDatabase.SaveAssets` (only if changed).

`GameSceneValidator.cs` (separate owner): refs non-null; `crowdCountTextStyle` points to the intended style asset and its `OutlineWidth` is valid; prefab intact (SMR + Animator on Human_Base + looping clip); ⟲ every `AnimationUtility.GetCurveBindings(clip)` path resolves via `transform.Find` under the Animator; colliders present; material shader assertions; config ranges. It does not import or validate the TMP font resource; missing-font detection is HudRoot's runtime fail-fast contract. Prints `[Validator] PASS/FAIL`; `EditorApplication.Exit` only in batch.

Pipeline gates (each must pass before the next):
1. `unity-cli editor refresh --compile` + console error scan → zero errors.
2. `unity-cli test` (EditMode: kernel suite + existing EventManager suite untouched & green).
3. `unity-cli exec` → `GameSceneSetup.Apply()` → `GameSceneValidator.Validate()` → PASS.
4. ⟲⟲ Scripted play-smoke via unity-cli (accepted lighter substitute for a PlayMode-test assembly; verification agent independent of implementers). Debug surface (all `#if UNITY_EDITOR`, owned by their file's owner): `GameSceneController.DebugSession { get; }`, `GameSession.DebugSetTimeRemaining(float)`, `InputRoot.SetSyntheticHeading(Vector2)/ClearSynthetic()`, `InputRoot.DebugTapRestart()`. Procedure: `editor play --wait` → exec: find GameSceneController, `SetSyntheticHeading` → assert State becomes Playing → ⟲⟲⟲ assert `crowdRoot.RejectionRate ≤ 0.8` → run ~15 s → assert player MemberCount grew and standings ordered → `DebugSetTimeRemaining(3)` → wait → assert State Finished + result overlay exists → `DebugTapRestart()` → assert scene reloaded (fresh controller, State Ready) → console error scan → screenshots (start/mid/result) → fps sample (`1/Time.smoothDeltaTime` avg over 5 s) ≥60 → `editor stop`. Scope: conversion/elimination BEHAVIOR is covered by the named kernel tests (T1), not gated in smoke; smoke only fails on console errors during any combat that occurs naturally.
5. Cross-review: CODEX + independent Claude reviewer over the full diff; iterate to consensus.

## 6. Files & ownership (one owner per file; E1 owns ALL editor-time asset/scene mutations)

| # | Path (Assets/@Project/) | Contents |
|---|---|---|
| K1 | Crowd/Core/Project.CrowdCity.Core.asmdef | kernel asmdef (autoReferenced, no refs) |
| K2 | Crowd/Core/AgentBuffer.cs + SimTuning.cs | SoA buffer + tuning struct |
| K3 | Crowd/Core/SpatialGrid.cs | uniform grid |
| K4 | Crowd/Core/RecruitResolver.cs | neutral capture |
| K5 | Crowd/Core/CombatResolver.cs (+CombatState, CombatOutcome) | conversion combat + elimination |
| K6 | Crowd/Core/MatchRules.cs + FollowerSteering.cs | standings/winner + slot math |
| T1 | Crowd/Tests/Editor/ (asmdef + CombatResolverTests + RecruitResolverTests + GridAndSteeringTests + MatchRulesTests) | EditMode tests; required cases pinned in INTERFACES.md T1 (3-way determinism/attribution, ⟲⟲⟲ multi-claim victim, elimination-pass no-feedback, unmet-budget retention, both-lone-leaders inert, permutation invariance, contested recruit, conversion math, standings ties) |
| C1 | Crowd/Scripts/CrowdRoot.cs | feature root (SimTick pipeline, spawning, commit, authoritative count publish; no presentation ownership) |
| C2 | Crowd/Scripts/CrowdModel.cs + RivalAiDriver.cs | runtime model + AI |
| C3 | Crowd/Contracts/Events/CrowdEvents.cs | the two payload structs |
| H1 | Human/Scripts/Human.cs | Crowd-owned passive Human visual leaf; no attached UI |
| G1 | Game/Scripts/GameSceneController.cs | assemble, validate refs, template deactivate, reload executor, teardown order |
| G2 | Game/Scripts/GameSession.cs | plain C# state machine, timer, standings cache, bus tokens, Dispose = own tokens only |
| G3 | Game/Scripts/GameplayRoot.cs | root creation, all-leader CrowdLabelBinding construction, ordered tick driver, Shutdown (reverse order), ReloadRequested |
| G4 | Game/Scripts/InputRoot.cs | null-safe polling, heading persistence, FirstDrag/RestartTapped, editor synthetic hook |
| G5 | Game/Scripts/CameraRoot.cs | follow/zoom/latch, tokens |
| G6 | Game/Scripts/GameConfigSO.cs | config SO type (incl. Material[] teamMaterials, colors, all §2 numbers, OnValidate clamps) |
| G7 | Game/Contracts/Runtime/IGameSessionReadOnly.cs | HUD-facing read interface |
| U1/U2 | Hud/Scripts/HudRoot.cs + TMPTextStyleSO.cs | independent Bold TMP world labels + screen uGUI + rival off-screen markers + runtime label materials/style source, tokens |
| E1 | Game/Editor/GameSceneSetup.cs | setup (owns scene/FBX-meta/controller/materials/config mutations) |
| E2 | Game/Editor/GameSceneValidator.cs | validator (owner ≠ E1) |

Waves (⟲⟲⟲ E1/E2 in Wave 3 — E1 references GameSceneController): 1 = K1–K6, G6, G7, C3, H1 → gate 1. 2 = T1, G2, C2, G4, G5 → gates 1–2. 3 = C1, G3, G1, U1/U2, E1, E2 → gates 1–3 → gate 4 → cross-review. (All waves' files may be authored in parallel against the pinned contracts; the gates run once after authoring completes.)

## 7. Lifecycle/teardown

| Acquire | Where | Paired release |
|---|---|---|
| Bus tokens (HudRoot/CameraRoot) | their Initialize | their Shutdown (explicit, idempotent), called by GameplayRoot.Shutdown; OnDestroy = safety net only |
| Bus tokens (GameSession) | session.Initialize | ⟲⟲⟲ session.Dispose(), called by GameSceneController.OnDestroy AFTER gameplayRoot.Shutdown (G1 owns this, not GameplayRoot) |
| session.StateChanged, inputRoot events | GameplayRoot.Initialize | GameplayRoot.Shutdown/OnDestroy |
| Human clones | CrowdRoot creates | CrowdRoot.Shutdown destroys; prefab asset is never destroyed |
| HUD canvas + independent world leader labels + rival markers + per-team runtime TMP materials | HudRoot creates/binds after roots initialize | HudRoot removes a team's visuals on elimination and destroys all remaining presentation/materials in Shutdown |
| EventManager static channels | n/a (global) | per-token discipline above (every subscriber disposes its tokens on scene unload) + SubsystemRegistration auto-clear each play session; ⟲⟲⟲ nobody calls ClearAll at runtime |
| Coroutines | none (Update-driven with state gates) | n/a |

Restart = scene reload; static bus channels survive it ⇒ token discipline above is mandatory; SubsystemRegistration auto-clear covers play-session boundaries.

## 8. Accepted MVP compromises (explicit, reviewed)

1. Followers/neutrals have no wall collision (leaders only); wander direction raycasts reduce visible clipping; recruiting/combat ignore walls (matches original's feel at this fidelity).
2. Neutrals' Animator shows walk pose frozen at speed 0 / slow walk while wandering.
3. Player-build bus dispatch lacks exception isolation → handlers are trivial by rule.
4. No PlayMode NUnit assembly; unity-cli scripted play-smoke instead (rationale §5.4).
5. No neutral respawn trickle; 150 initial only.
6. AI is heuristic; tuning pass only if gate-4 play feels degenerate.
7. ⟲⟲ Fixed-step sim (50 Hz) rendered without interpolation can show mild temporal aliasing at 60 fps (alternating 1/2-step frames); camera smoothing masks most of it. Escape hatch if gate-4 feel/screenshots flag it: interpolate `Human` visual positions between previous/current sim positions (localized change in H1/C1).

## 9. Success criteria

- Gates 1–4 pass (compile clean; kernel+EventManager tests green; setup+validator PASS; play-smoke: drag→Playing, RejectionRate ≤0.8, recruiting grows count, timer→result overlay, R/tap restart clean, zero console errors, ≥55 fps sampled — ⟲⟲⟲ conversion/elimination BEHAVIOR is verified by the named kernel tests, not smoke-gated).
- Walk animation visibly plays on clones (curve-path validator + screenshot).
- CODEX + Claude reviewer consensus on the final diff (no unresolved BLOCKER/MAJOR).
- CLAUDE.md compliance incl. the surfaced static-bus conflict and this Decision Log in the final report.

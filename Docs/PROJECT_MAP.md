# PROJECT_MAP — structure index (repo-wide)

> **Question this document answers: "where is it?"** Reader is an AI. **Pointers only** — no explanation, no rationale, no measurements.
> Current state, invariants, gates and traps live in [`WORK_STATE.md`](WORK_STATE.md); explanation and rationale live in the per-area human guide. `CLAUDE.md` §1.1 is authoritative for the boundary between the three.
> Line numbers are coordinates as of commit `ecdfd45`. If one is off, re-find by symbol name and fix this file.

---

## 1. Repo roots

| Path | Contents |
|---|---|
| `Assets/@Project/` | **code root** (fixed — `CLAUDE.md` §0.1) |
| `Assets/@Project/{City,Crowd,DevTools,Game,Hud,Human,Manager}/` | feature folders |
| `Assets/@Project/Scenes/GameScene.unity` | the only scene. Roots: `GameArea` (`City`/`Human`), `Main Camera`, `Directional Light`, `Global Volume`, `GameSceneController` |
| `Assets/Settings/`, `Assets/TextMesh Pro/` | URP settings, TMP Essential (tracked infrastructure) |
| `Packages/manifest.json` | URP, Input System, Burst/Collections/Mathematics |
| `ProjectSettings/` | editor version `6000.3.9f1` — `ProjectVersion.txt:1` · `Unit` physics layer (layer 8) — `TagManager.asset:16`, consumed by `CrowdRoot.cs:39` (`UnitLayerName`) and `RivalAiDriver.cs:25` (`WallProbeMask`) |
| `Docs/` | documents (§16) |
| `UnityArchitectureGuide/` | general reference material (not a project authority) |

Per-feature folder convention: `Scripts/` · `Editor/` · `Contracts/` · `Core/` · `Tests/Editor/` · `Resources/{Prefabs,UI}/` · `Externals/` · `Generated/` · `Prefabs/` · `Materials/` · `Animations/` · `VAT/`

Only two asmdefs: `Assets/@Project/Crowd/Core/Project.CrowdCity.Core.asmdef`, `Assets/@Project/Crowd/Tests/Editor/Project.CrowdCity.Core.Tests.asmdef`. Everything else is `Assembly-CSharp`, or `Assembly-CSharp-Editor` for the non-asmdef `Editor/` folders (`City/Editor`, `Game/Editor`, `Human/Editor`).

---

## 2. Bootstrap and ownership chain

`GameSceneController → GameplayRoot → {InputRoot, CrowdRoot, CameraRoot, HudRoot} → Human`

| Step | Location |
|---|---|
| scene entry point (serialized `config`/`mainCamera`/`cityRoot`) | `Assets/@Project/Game/Scripts/GameSceneController.cs:13-15` |
| reference validation | `GameSceneController.cs:36` (`Awake`) |
| dev startup-screen branch → creates `DevHudRoot` | `GameSceneController.cs:65` (`Start`), `:70-78` |
| gameplay assembly (new `GameSession` + load/Instantiate/Initialize `GameplayRoot`) | `GameSceneController.cs:97` (`BuildGameplay`), `:104-111` |
| teardown order (own bindings → `GameplayRoot.Shutdown` → `_session.Dispose`) | `GameSceneController.cs:136` |
| scene restart execution | `GameSceneController.cs:161` |
| Instantiate the 4 child roots | `Assets/@Project/Game/Scripts/GameplayRoot.cs:67-70` |
| per-root Initialize order | `GameplayRoot.cs:74-77` |
| spawn path branch (batch = synchronous / interactive = chunked) | `GameplayRoot.cs:90-106` |
| leader-only presentation binding | `GameplayRoot.cs:299` (`BindLeaders`), `:305` |
| reverse-order teardown | `GameplayRoot.cs:124` (`Shutdown`) |
| dev-bootstrap exception record | `CLAUDE.md` §11.5 "Dev-tool bootstrap exception" |

---

## 3. Loop and cadence

| Item | Location |
|---|---|
| `FixedStepSeconds = 0.02f` | `Assets/@Project/Game/Scripts/GameplayRoot.cs:12` |
| `MaxStepsPerFrame = 4` | `GameplayRoot.cs:13` |
| frame loop | `GameplayRoot.cs:177` (`Update`) |
| chunked-spawn progress (no ticks) | `GameplayRoot.cs:186-203` |
| input polling (single driver) | `GameplayRoot.cs:206` |
| one-shot delta discard after spawn completes | `GameplayRoot.cs:216-221` |
| accumulator + fixed-step while | `GameplayRoot.cs:224-235` |
| overflow discard | `GameplayRoot.cs:238-241` |
| `RenderInterpolate(alpha)` call site (once per frame) | `GameplayRoot.cs:245` |
| session tick | `GameplayRoot.cs:233` → `Game/Scripts/GameSession.cs:114` |

---

## 4. Sim kernel — `Assets/@Project/Crowd/Core/` (pure C#, separate asmdef)

| Type | File | Entry points |
|---|---|---|
| `AgentBuffer` (SoA, NativeArray) | `AgentBuffer.cs:13` | `NeutralTeam = -1` `:18` · `Id/Team/IsLeader/Pos/Scale` `:23,:28,:33,:38,:44` · `Add` `:77` · `Dispose` `:98` |
| `CrowdSimState` (Persistent owner) | `CrowdSimState.cs:14` | ctor `:131` · `PrevPos` `:45` · `CommandedVelocity` `:52` · `FollowerList` `:57` · `Grid*` snapshot `:121-125` · `Dispose` `:179` |
| `SpatialGrid` (hash grid) | `SpatialGrid.cs:11` | `Rebuild` `:124` · `QueryCircle` `:178` · `QueryTeamMask` `:242` · `QueryCircleCapped` `:274` · `CopyNativeSnapshot` `:101` · `HashCell` `:352` |
| `RecruitResolver` | `RecruitResolver.cs:34` | `Resolve` `:45` · grid query `:75` |
| `CombatResolver` (+`CombatState`/`CombatOutcome`) | `CombatResolver.cs:135` (`:67`, `:98`) | `Resolve` `:215` · footprint mask `:282` · contact query `:290` · convert-rate branch `:364` · budget gate `:432` · victim sort `:454`,`:587` · leader query `:495` |
| `MatchRules` | `MatchRules.cs:38` | `PlayerTeam = 0` `:43` · `ResolveStandings` `:50` · `ResolveWinner` `:82` |
| `FollowerSteering` | `FollowerSteering.cs:7` | `SlotOffset` `:16` |
| `SimTuning` (config struct) | `SimTuning.cs:8` | fields `:14-80` |
| `WallField` / `WallFieldView` | `WallField.cs:15` / `:136` | `Load` `:52` · `AsView` `:112` · `Dispose` `:119` · `Phi` `:171` · `Gradient` `:198` |
| `WallSdfAsset` (SO) | `WallSdfAsset.cs:17` | `CurrentSchemaVersion` `:20` · `EditorInitialize` `:98` |
| `WallSolver` (pure function) | `WallSolver.cs:16` | `Resolve(WallField…)` `:44` · `Resolve(in WallFieldView…)` `:54` · `Depenetrate` `:112` · constants `:19-31` |
| `CrowdSimCounters` (work counters) | `CrowdSimCounters.cs:14` | `QuerySource` `:17` · `Enabled` `:28` · `Reset` `:66` |

The three Burst jobs are in §6.

---

## 5. Crowd orchestration — `Assets/@Project/Crowd/Scripts/`

### `CrowdRoot.cs` (1987 lines, feature root)

| Item | Line |
|---|---|
| constants (spawn/grid/wall clearance `0.43`/VAT period) | `:15-39` |
| serialized fields `_humanPrefab`/`_wallSdfAsset`/`_useSdfSolver`/`_crowdRenderer`/`_spawnBatchSize` | `:54`, `:61`, `:69`, `:72`, `:153` |
| visual-only arrays `_visualPrev/_visualCur/_visualRender/_visualYaw` · `_visualSpeed01/_phase01` | `:104-107` · `:114-115` |
| `_gpuRenderActive` | `:119` |
| observation API `IsSdfActive`/`OracleAgentCount`/`LastScheduledFollowerCount`/`OracleReadAgent` | `:177`, `:199`, `:205`, `:211` |
| `Initialize` (kernel allocation + WallField load + publisher acquisition) | `:227` |
| `SpawnInitial` / `BeginChunkedSpawn` / `StepChunkedSpawn` | `:375` / `:404` / `:431` |
| `PrepareSpawnPlacements` (activates GPU renderer + precomputes all placements) | `:453`, `:458` |
| `SpawnLeaders` / `SpawnNeutralRange` / `FinalizeSpawn` | `:526` / `:578` / `:632` |
| rig-destroy call sites (both inside `if (_gpuRenderActive)`) | `:567-570`, `:623-626` |
| **`SimTick`** (10 pinned phases) | `:659` |
| ├ apply pending match state ① (flag set by `OnMatchStateChanged` `:799`) | `:666-671` |
| ├ `PrevPos` snapshot / `sdfActive` capture | `:680` / `:684` |
| ├ Restore (leaders only on the SDF path) | `:690` |
| ├ `UpdateHeadings` ② | `:693` → `:1130` |
| ├ `MoveLeaders` ③ | `:696` → `:1157` |
| ├ `SteerFollowersAndNeutrals` ④ | `:698` → `:1212` |
| ├ `MirrorPositionsToBuffer` ⑤ | `:700` → `:1741` |
| ├ `_grid.Rebuild` ⑥ | `:703` |
| ├ `_recruitResolver.Resolve` ⑦ | `:706` |
| ├ `_combatResolver.Resolve` ⑧ | `:709` |
| ├ `CommitOutcomes` ⑨ | `:712` → `:1780` |
| └ `PublishTickEvents` ⑩ | `:717` → `:1945` |
| `RenderInterpolate` (visual only; transform-write gates) | `:727`, `:747`, `:755` |
| `TryGetCrowdRenderBounds` | `:775` |
| `OnMatchStateChanged` (sets pending flag only) | `:799` |
| `Shutdown` (renderer → WallField → simState → clones) | `:808` |
| `TryActivateGpuRenderer` / `_crowdRenderer.Init` call | `:874` / `:888` |
| `RenderGpuCrowd` (phase integration + instance scratch) | `:900` |
| `ComputeWalkableRegion` / `SpawnClone` / `GetBakedController` | `:966` / `:1047` / `:1072` |
| `RepickWanderHeading` (`_rng` + `Physics.Raycast`) | `:1685`, `:1697` |
| `ApplyHorizontalMove` (SDF ↔ `CC.Move` fallback branch) | `:1714`, `:1716`, `:1733` |
| `ResolveTerminalSurvivor` / `RouteToSurvivor` / `Neutralize` | `:1901` / `:1925` / `:1935` |

### Rest

| File | Entry points |
|---|---|
| `CrowdModel.cs:9` | `AddFollower` `:64` · `RemoveFollowerByAgentIndex` `:74` · `MarkEliminated` `:101` |
| `RivalAiDriver.cs:10` | `WallProbeMask` `:25` · `DecideHeadingDeg` `:56` · neutral-density query `:129` · `ApplyWallAvoidance` `:165` · `ProbeClearance` `:200` |
| `CrowdRenderer.cs:14` | `InstanceData` `:21` · `InstanceStride = 28` `:31` · `Init` `:64` · null-device return `:99-102` · `Render` `:170` · leader shadow draw `:202-213` · `Dispose` `:220` · `OnDisable`/`OnEnable` re-init `:246`/`:254` |
| `CrowdSimProfiler.cs:11` | `Seg` `:23` · `CCMove` `:36` · the 7 split segments `:37-43` · `Enabled` `:48` · `Accum` `:54` · `Begin`/`End` `:69`/`:78` |
| `CrowdOracleRecorder.cs:16` | oracle observation hooks — `RecordWanderRepick` `:58` |
| `Contracts/Events/CrowdEvents.cs` | `CrowdCountChangedEvent` `:6` · `CrowdEliminatedEvent` `:34` |
| `Human/Scripts/Human.cs:8` | `MaxAnimatorSpeed` `:13` · `Init` `:25` · `SetTeamMaterial` `:45` · `SetLeader` `:56` · `DestroyVisualRig` `:72` (`SetActive(false)` `:78`, `Destroy` `:79`) · `SetHeadingAndSpeed` `:87` (rotation `:89`, animator null check `:91`) |

---

## 6. Burst jobs and their schedule sites

| Job | Definition | Schedule |
|---|---|---|
| `SteeringForceJob` (follower commanded velocity) | `Crowd/Core/SteeringForceJob.cs:17` (`[BurstCompile]` `:16`, `Execute` `:68`) | `CrowdRoot.cs:1321-1357` |
| `FollowerSdfMoveJob` (follower SDF move) | `Crowd/Core/FollowerSdfMoveJob.cs:21` (`:20`, `Execute` `:59`) | `CrowdRoot.cs:1365-1392` |
| `NeutralSdfMoveJob` (neutral SDF move) | `Crowd/Core/NeutralSdfMoveJob.cs:22` (`:21`, `Execute` `:57`) | `CrowdRoot.cs:1586-1610` |

- innerloop batch constant `SteeringForceBatch = 64` — `CrowdRoot.cs:29`
- native grid snapshot (job input) — `CrowdRoot.cs:1317`
- serial prepass / serial present pass (each bracketed by `CrowdSimProfiler.Begin/End(Seg.…)`, all inside `SteerFollowersAndNeutrals` `:1212`) — followers `:1215-1313` / `:1397-1448`, neutrals `:1557-1581` / `:1616-1637`
- serial `CC.Move` fallback path (`!sdfActive`) — followers `:1450-1544`, neutrals `:1639-1679`
- `[BurstCompile]` attribute lines (the determinism invariant they carry is owned by `Docs/WORK_STATE.md`) — `SteeringForceJob.cs:16` · `FollowerSdfMoveJob.cs:20` · `NeutralSdfMoveJob.cs:21`

---

## 7. Wall SDF pipeline

| Step | Location |
|---|---|
| bake tool (editor, once) | `Assets/@Project/City/Editor/WallFieldBaker.cs:24` · menu `:90` · `BakeParams.Default` `:45` |
| validator (`SourceHash` vs live city · re-bake determinism) | `Assets/@Project/City/Editor/WallFieldValidator.cs:27` (menu), `:57` (`Validate`) |
| artifact integrity check (payload CRC · payload length · pinned interpretation constants · `schemaVersion`) | `Assets/@Project/Game/Editor/ResourcePathValidator.cs:321` (`ValidateWallSdfIntegrity`), `:343` (`CheckWallSdfIntegrity`) — pinned constants `:55-61`, CRC32 reused from `City/Editor/WallFieldBaker.cs:647` |
| output assets | `Assets/@Project/City/Generated/WallSdf.asset` + `WallSdf.bytes` |
| **live values (authority)** — field coordinates only, read the asset for the values | `Assets/@Project/City/Generated/WallSdf.asset` — `cellSize` `:18` · `cols`/`rows` `:19-20` · `yMin`/`yMax` `:21-22` · `maxDistance` `:23` · `bilinearBias` `:24` · `colliderCount` `:25` |
| runtime load | `CrowdRoot.cs:339-362` → `Crowd/Core/WallField.cs:52` |
| query | `Crowd/Core/WallField.cs:171` (`Phi`), `:198` (`Gradient`) |
| move resolve | `Crowd/Core/WallSolver.cs:54` |
| prefab wiring | `Assets/@Project/Crowd/Resources/Prefabs/CrowdRoot.prefab:49` (`_wallSdfAsset`) |

---

## 8. Config and state location

| What | Where |
|---|---|
| SO type definition | `Assets/@Project/Game/Scripts/GameConfigSO.cs:9` · `Sim` property `:127` · `UseGpuCrowdRenderer` `:221` · `OnValidate` clamps `:247` |
| **live values (authority)** | `Assets/@Project/Game/GameConfig.asset` — `neutralCount` `:17` · `followerMaxSpeed` `:20` · `followerArriveRadius` `:31` · `followerMaxAccel` `:33` · `followerTrailingOffset` `:34` · `sim` block `:36-48` · `neutralAnimationSpeed` `:58` · `neutralMaxScale` `:59` · `useGpuCrowdRenderer` `:66` · `teamMaterials` `:73-78` |
| HUD style SO | `Assets/@Project/Hud/Scripts/TMPTextStyleSO.cs:8` / asset `Assets/@Project/Hud/CrowdCountTextStyle.asset` |
| session state | `Assets/@Project/Game/Scripts/GameSession.cs:12` (read interface `Game/Contracts/Runtime/IGameSessionReadOnly.cs:29`, `MatchState` `:7`) |
| runtime crowd state | `Crowd/Scripts/CrowdModel.cs:9` + `Crowd/Core/CrowdSimState.cs:14` (authoritative position/team) |
| prefab-serialized values | `Crowd/Resources/Prefabs/CrowdRoot.prefab:48-51` (`_humanPrefab`/`_wallSdfAsset`/`_useSdfSolver:1`/`_crowdRenderer`), `:100` (`_vatRows:21`) |
| SaveData / server | none |

---

## 9. Prefab loading and Resources convention

| Item | Location |
|---|---|
| central stateless loader | `Assets/@Project/Manager/ResourceLoader/Scripts/ResourceLoader.cs:12` |
| `LoadPrefab<T>()` → `Prefabs/<ClassName>` | `:24` |
| `LoadUI<T>()` → `UI/<ClassName>` | `:36` |
| `LoadSO<T>(name)` → `SO/<name>` | `:47` |
| internals (`Resources.Load<GameObject>` + `GetComponent<T>`) | `:68` |
| loaded prefabs | `Game/Resources/Prefabs/{GameplayRoot,InputRoot,CameraRoot}.prefab` · `Crowd/Resources/Prefabs/CrowdRoot.prefab` · `Hud/Resources/UI/HudRoot.prefab` · `DevTools/Resources/UI/DevHudRoot.prefab` |
| non-Resources serialized prefabs | `Human/Prefabs/Human.prefab` · `Hud/Prefabs/{CrowdLabel,RivalMarker}.prefab` · `City/Prefabs/GeneratedBuildings/Building_00…36.prefab` |
| validator (duplicate keys · root contract · wiring · source-policy · WallSdf integrity) | `Assets/@Project/Game/Editor/ResourcePathValidator.cs:82` (menu), `:93` (`Validate`) — target type lists `PrefabRootTypes` `:64-70` / `UiRootTypes` `:73-77`, canonical loader path constant `:34` |
| rule authority | `CLAUDE.md` §11.5 |

---

## 10. Contracts and event bus

| Item | Location |
|---|---|
| bus implementation (static) | `Assets/@Project/Manager/EventManager/Scripts/EventManager.cs:75` |
| `GetPublisher<T>` / `GetSubscriber<T>` | `:82` / `:91` |
| `ClearAll` / auto-clear at play-session start | `:119` / `:127-128` |
| per-subscriber exception isolation (**Editor only**) | `:189-207` (Editor-only null-callback log in `Subscribe` `:162` — log at `:167`) |
| the 2 event payloads | `Crowd/Contracts/Events/CrowdEvents.cs:6`, `:34` |
| publisher acquisition (the only one) | `CrowdRoot.cs:333-334` |
| publish sites (the only ones) | `CrowdRoot.cs:1953`, `:1964`, `:1976` (all inside `PublishTickEvents` `:1945`; called from `FinalizeSpawn` `:640` and `SimTick` ⑩ `:717`) |
| subscribers (runtime, all boundary objects) | `GameSession.cs:61-62` (unsub `:157-158`) · `HudRoot.cs:168-169` (unsub `:253`, `:259`) · `CameraRoot.cs:184-185` (unsub `:214-215`) |
| subscriber (editor harness) | `Game/Editor/CrowdOracleHarness.cs:231-232` (unsub `:358-359`) |
| read-only runtime interface | `Game/Contracts/Runtime/IGameSessionReadOnly.cs:29` |
| QueryBus track | unused (`EventManager.cs:100`, `:109` — definitions only) |

---

## 11. Game · Hud · DevTools features

| File | Entry points |
|---|---|
| `Game/Scripts/InputRoot.cs:10` | `Initialize` `:55` · `Poll` `:73` · `Shutdown` `:155` · editor-only hooks `:176`,`:194`,`:202` |
| `Game/Scripts/CameraRoot.cs:11` | `Initialize` `:125` · `SetTarget` `:194` · `Shutdown` `:208` · `LateUpdate` (follow/zoom/occlusion) `:230` · serialized `buildingOccludedMaterial` `:22` |
| `Game/Scripts/GameSession.cs:12` | ctor `:26` · `Initialize` `:54` · `Begin` `:99` · `Tick` `:114` · `Dispose` `:149` · `Finish` `:219` |
| `Hud/Scripts/HudRoot.cs:43` | `Initialize` `:119` · `BindLeaderLabels` `:183` · `Shutdown` `:242` · `Update` (timer) `:287` · `LateUpdate` (label/marker projection) `:310` · `CrowdLabelBinding` `:12` · serialized fields `:65-87` |
| `DevTools/Scripts/DevHudRoot.cs:12` | `Init` `:42` · `CountConfirmed` `:31` · `HideStartupPanel` `:92` · FPS `Update` `:107` |

---

## 12. City and Human asset pipelines

| Item | Location |
|---|---|
| city source FBX / atlas | `Assets/@Project/City/Externals/City.fbx`, `city_atlas.png` |
| 37-building split generator | `Assets/@Project/City/Editor/CityBuildingsGenerator.cs:14` (path constants `:16-21`, expected-count constants `:22-25`) |
| generated output | `City/Generated/BuildingMeshes/Building_00…36.asset`, `City/Prefabs/GeneratedBuildings/` |
| occluded material | `City/Materials/City_Occluded.mat` |
| Human source FBX / animator controller | `Human/Externals/Human_Base.fbx`, `Human/Animations/HumanWalk.controller` |
| Human prefab (root + rig subtree, 45 Transforms) | `Human/Prefabs/Human.prefab` |
| 5 team materials | `Human/Materials/Team_{Player,RivalA,RivalB,RivalC,Neutral}.mat` |
| VAT baker | `Assets/@Project/Human/Editor/HumanVatBaker.cs:23` · menu `:68` · output paths `:30-32` |
| VAT output | `Human/VAT/{HumanWalkVatPosition,HumanWalkVatNormal,HumanWalkVatMesh}.asset`, `HumanVat.mat`, `HumanVat.shader` |
| City README | `Assets/@Project/City/README.md` |

---

## 13. Editor tooling (`AF/CrowdCity/` menu)

| Menu | Location |
|---|---|
| Setup Game Scene | `Game/Editor/GameSceneSetup.cs:236` (`Apply` `:237`) |
| Setup City Buildings | `GameSceneSetup.cs:326` |
| Bake Human Prefab (Resources + CC and Human) | `GameSceneSetup.cs:1116` |
| Bake Crowd Renderer (VAT material + CrowdRenderer child) | `GameSceneSetup.cs:1698` |
| Bake Feature Root Prefabs (Resources) | `GameSceneSetup.cs:1926` |
| Bake Dev HUD (Resources) | `GameSceneSetup.cs:2175` |
| Validate Game Scene | `Game/Editor/GameSceneValidator.cs:49` (`Validate` `:62`) — pinned CC spec constants `:37-41` |
| Validate Resource Paths | `Game/Editor/ResourcePathValidator.cs:82` |
| Bake Wall SDF | `City/Editor/WallFieldBaker.cs:90` |
| Validate Wall SDF | `City/Editor/WallFieldValidator.cs:27` |
| Bake Human VAT | `Human/Editor/HumanVatBaker.cs:68` |
| Run Phase C Baseline Profile | `Game/Editor/CrowdProfileHarness.cs:38` |

---

## 14. Verification harnesses (`Assets/@Project/Game/Editor/`, all with `-executeMethod` batch entry points)

| Harness | Entry point | Mode | Output |
|---|---|---|---|
| `CrowdProfileHarness` | `.cs:46` (`RunFromBatch`) | edit mode | segment text report |
| `CrowdOracleHarness` | `.cs:35` | edit mode headless | `phaseC_oracle_snapshot_n{n}_s{seed}.bin` `:158` · `phaseC_oracle_determinism.txt` `:380` · summary/events CSV |
| `CrowdPerfHarnessP95` | `.cs:49` (driver `:160`) | **play mode**, graphics required | 14-column CSV `:44` (`simtick` computation `:413`, `StepSim` timing span `:364-367`, `RenderInterpolate` call `:472`) |
| `CrowdPerfHarness` (base variant) | `.cs:40` | play mode | text append |
| `CrowdShotHarness` | `.cs:28` | edit mode, graphics required | `gpu_on.png` `:147` · `gpu_on_top.png` `:148` · `gpu_on_closeup.png` `:149` · `smr_off.png` `:184` |
| `CrowdFlatRateShotHarness` | `.cs:30` | edit mode, graphics required | PNG per tick milestone |

Arguments, environment, which path each invocation actually measures, and reading caveats: `Docs/CrowdCity/Perf/MANIFEST.md`. Gate values: `Docs/WORK_STATE.md`.

---

## 15. Tests

| File | Subject |
|---|---|
| `Crowd/Tests/Editor/Project.CrowdCity.Core.Tests.asmdef` | kernel test asmdef |
| `Crowd/Tests/Editor/CombatResolverTests.cs:11` | conversion / elimination / permutation invariance |
| `Crowd/Tests/Editor/RecruitResolverTests.cs:8` | recruit tie-break |
| `Crowd/Tests/Editor/GridAndSteeringTests.cs:9` | grid queries + slot math |
| `Crowd/Tests/Editor/MatchRulesTests.cs:7` | standings / winner |
| `Crowd/Tests/Editor/DensityCapTests.cs:10` | separation visit-budget cap |
| `Game/Editor/CameraRootLifecycleTests.cs` | CameraRoot lifecycle |
| `Game/Editor/ResourcePathValidatorWallSdfTests.cs` | WallSdf payload CRC / length / pinned constants |
| `City/Editor/CityBuildingsGeneratorTests.cs` | building-split determinism |

---

## 16. Doc map

| File | Question it answers / nature |
|---|---|
| `CLAUDE.md` · `AGENTS.md` | working rules (mirrored in the same commit) |
| `Docs/PROJECT_MAP.md` | **where is it** (this file, AI-facing) |
| `Docs/WORK_STATE.md` | **what is in flight, what must not be broken** (AI-facing; authority for invariants, gates, traps) |
| `Docs/CrowdCity/CROWD_GUIDE.md` | **why is it built this way** (human only; agents maintain it, never read it as a fact source) |
| `Docs/CrowdCity/Perf/MANIFEST.md` | measurement evidence (environment, command lines, reading caveats) |
| `Docs/CrowdCity/Perf/*.txt`, `*.csv` | raw measurement output |
| `Docs/Photo/` | profiler captures |
| `Assets/@Project/City/README.md` | City asset-pipeline local note |
| `UnityArchitectureGuide/TEAM_ARCHITECTURE_GUIDE.md` | general reference (not a project authority) |

That table is exhaustive — those files are the only tracked documents. Unimplemented sim-optimization specs (density cap, canonical order, `DeterministicRng`, T2/T3a/T3b, the mobile 10k floor) live in `Docs/WORK_STATE.md` §9; change history lives in git. Retired documents — which ones, and where each one's surviving content went — are listed in `Docs/WORK_STATE.md` §8.

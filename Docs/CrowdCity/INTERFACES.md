# Pinned Public API Contracts (implementation coordination — EXACT signatures)

Rules for every implementer: global namespace (NO `namespace` blocks). Unity 6000.3.9f1 APIs. New Input System only. No LINQ/allocations in per-tick paths. Every `Subscribe` token disposed in the same lifecycle. XML doc comments in Korean (match EventManager.cs style) on public members. Counts: `MemberCount` INCLUDES the leader (lone leader = 1). Teams: -1 = neutral, 0 = player, 1..3 = rivals. Files must contain EXACTLY the public surface pinned here (private helpers free). If you believe a pinned signature is wrong, STOP and report — do not silently change it.

Supersedes DESIGN.md where they differ. **Latest approved ownership supersession (2026-07-15):** the user selected Hud-owned Screen Space Overlay leader labels so buildings cannot occlude them. `HudRoot` owns all leader labels, their runtime TMP materials, screen HUD, and rival off-screen markers; `CrowdRoot` owns simulation/runtime models and publishes authoritative count/elimination facts only. This replaces the preceding world-space rendering detail while preserving Hud ownership and is not an architecture-guide exception. E1/E2 belong to Wave 3 (E1 references GameSceneController).

## Wave 1

### K1 `Assets/@Project/Crowd/Core/Project.CrowdCity.Core.asmdef`
```json
{
    "name": "Project.CrowdCity.Core",
    "rootNamespace": "",
    "references": [],
    "includePlatforms": [],
    "excludePlatforms": [],
    "allowUnsafeCode": false,
    "overrideReferences": false,
    "precompiledReferences": [],
    "autoReferenced": true,
    "defineConstraints": [],
    "versionDefines": [],
    "noEngineReferences": false
}
```

### K2 `Crowd/Core/AgentBuffer.cs` + `Crowd/Core/SimTuning.cs`
```csharp
public sealed class AgentBuffer
{
    public const int NeutralTeam = -1;
    public AgentBuffer(int capacity);
    public int Count { get; }               // used slots; population is CONSTANT after spawn (conversion never removes agents)
    public int[] Id { get; }                // unique, stable, ascending insertion order
    public int[] Team { get; }              // -1 neutral, 0..3
    public bool[] IsLeader { get; }
    public UnityEngine.Vector2[] Pos { get; }  // world XZ
    public int Add(int id, int team, bool isLeader, UnityEngine.Vector2 pos); // returns index; throws if full
}

[System.Serializable]
public struct SimTuning
{
    public float RecruitRadius;     // default 1.2
    public float CombatRadius;      // default 1.0
    public float ConvertPerSecond;  // default 10
    public int PairNormalizer;      // default 8
}
```

### K3 `Crowd/Core/SpatialGrid.cs`
```csharp
public sealed class SpatialGrid
{
    public SpatialGrid(float cellSize, int capacity);
    public void Rebuild(AgentBuffer buffer);
    public void QueryCircle(UnityEngine.Vector2 center, float radius, System.Collections.Generic.List<int> results); // CLEARS results first; appends agent indices (exact distance-filtered, not just cell hits)
}
```

### K4 `Crowd/Core/RecruitResolver.cs`
```csharp
public readonly struct RecruitAssignment
{
    public readonly int AgentIndex;
    public readonly int ToTeam;
    public RecruitAssignment(int agentIndex, int toTeam);
}

public sealed class RecruitResolver
{
    // Clears results. One claim per neutral: neutral joins the team of the nearest non-neutral agent within radius; distance tie -> lower claimant agent Id; deterministic regardless of grid order.
    public void Resolve(AgentBuffer buffer, SpatialGrid grid, float recruitRadius, System.Collections.Generic.List<RecruitAssignment> results);
}
```

### K5 `Crowd/Core/CombatResolver.cs` (contains all four types)
```csharp
public readonly struct CrowdConversion
{
    public readonly int AgentIndex;
    public readonly int ToTeam;
    public CrowdConversion(int agentIndex, int toTeam);
}

public readonly struct CrowdElimination
{
    public readonly int Team;
    public readonly int ByTeam;
    public readonly int LeaderAgentIndex;
    public CrowdElimination(int team, int byTeam, int leaderAgentIndex);
}

public sealed class CombatState
{
    public CombatState(int teamCount);      // teamCount = 4
    public void Reset();                    // clears all pair accumulators
}

public sealed class CombatOutcome
{
    public CombatOutcome(int capacity);
    public System.Collections.Generic.List<CrowdConversion> Conversions { get; }
    public System.Collections.Generic.List<CrowdElimination> Eliminations { get; }
    public void Clear();
}

public sealed class CombatResolver
{
    public CombatResolver(int teamCount, int agentCapacity);
    // Snapshot-PURE; NEVER mutates buffer. Semantics (must match tests):
    // 1. Count touching cross-team pairs (both non-neutral, distance <= CombatRadius) per unordered team pair via grid.
    // 2. Conversion direction and budget for EVERY crowd pair derive from the IMMUTABLE start-of-step counts (never adjusted mid-step):
    //    the strictly larger side converts from the smaller at ConvertPerSecond * clamp01(pairs / PairNormalizer) * dt, accumulated in
    //    CombatState per ORDERED (winner,loser) pair across calls; accumulator resets to 0 when that pair has no touching pairs this call.
    //    Equal start-of-step counts -> no conversion, accumulator holds.
    // 3. Victims globally arbitrated: candidates are non-leader members of each losing team, nearest to any winning-team touching member;
    //    distance tie -> lower agent Id; each agent converted AT MOST ONCE per call; leaders never converted in this phase.
    //    A victim claimable by MULTIPLE winning teams converts to the team owning the nearest touching member; distance tie -> lower
    //    team id; the other pair's budget goes UNMET. An unmet budget is NOT consumed — the accumulated fraction is RETAINED in
    //    CombatState for the next call (only emitted conversions consume accumulator). All conversions emitted as one batch.
    //    RESULT MUST BE INVARIANT under pair-enumeration order and agent insertion order (dedicated permutation tests).
    // 4. Elimination pass evaluates EVERY team against ONE fixed virtual team mapping = start-of-step teams overlaid with the phase-3
    //    conversions ONLY (leader conversions emitted by the elimination pass itself do NOT feed back into other teams' elimination
    //    checks within the same call — no order dependence). A team reduced to a lone leader (virtual count == 1) with >= 1
    //    strictly-larger (virtual count) team's member within CombatRadius of the leader -> eliminated. Eliminator = larger team owning
    //    the nearest such member; tie -> lower team id. Emit CrowdElimination AND CrowdConversion(leaderIndex, eliminatorTeam); the
    //    caller applies IsLeader=false in the buffer (buffer is authoritative for IsLeader). Two lone leaders (1 v 1) are inert.
    public void Resolve(AgentBuffer buffer, SpatialGrid grid, in SimTuning tuning, float dt, CombatState state, CombatOutcome outcome);
}
```

### K6 `Crowd/Core/MatchRules.cs` + `Crowd/Core/FollowerSteering.cs`
```csharp
public readonly struct CrowdStanding
{
    public readonly int Team;
    public readonly int MemberCount;
    public readonly bool Eliminated;
    public CrowdStanding(int team, int memberCount, bool eliminated);
}

public static class MatchRules
{
    public const int PlayerTeam = 0;
    // Sort: non-eliminated first by MemberCount desc; count tie -> lower team first; eliminated last (count 0) by team asc. Clears results.
    public static void ResolveStandings(int[] countsByTeam, bool[] eliminated, System.Collections.Generic.List<CrowdStanding> results);
    // Highest-count non-eliminated team; tie including PlayerTeam -> PlayerTeam, otherwise lowest team id. All eliminated -> -1.
    public static int ResolveWinner(int[] countsByTeam, bool[] eliminated);
}

public static class FollowerSteering
{
    // Golden-angle spiral on XZ: slotIndex 0 = first follower. r = spacing * sqrt(slotIndex + 1), theta = slotIndex * 2.39996f.
    public static UnityEngine.Vector2 SlotOffset(int slotIndex, float spacing);
}
```

### C3 `Crowd/Contracts/Events/CrowdEvents.cs`
```csharp
public readonly struct CrowdCountChangedEvent
{
    public readonly int CrowdId;
    public readonly int MemberCount;   // includes leader
    public CrowdCountChangedEvent(int crowdId, int memberCount);
}

public readonly struct CrowdEliminatedEvent
{
    public readonly int CrowdId;
    public readonly int ByCrowdId;
    public CrowdEliminatedEvent(int crowdId, int byCrowdId);
}
```

### G6 `Game/Scripts/GameConfigSO.cs`
⟲⟲⟲⟲ **SO consumer-immutability (user-mandated):** values are stored in `[SerializeField] private _camelCase` fields (inspector-editable) and exposed to consumers ONLY through **get-only properties**; arrays as `IReadOnlyList<T>` so callers cannot mutate elements. No `public` mutable field. `OnValidate` clamps the private fields. Consumers read exactly as before (`config.MatchSeconds`, `config.TeamMaterials[t]`, `config.Sim` — all still compile: property read, `IReadOnlyList` indexer, struct-copy). The editor setup writes `TeamMaterials` via `SerializedObject.FindProperty("_teamMaterials")` (serialized field name).
```csharp
[UnityEngine.CreateAssetMenu(menuName = "AF/CrowdCity/Game Config", fileName = "GameConfig")]
public sealed class GameConfigSO : UnityEngine.ScriptableObject
{
    // pattern for EVERY value (names shown; keep the pinned defaults/headers/docs from the current file):
    [UnityEngine.SerializeField] private float _matchSeconds = 120f;
    public float MatchSeconds => _matchSeconds;
    // ...same for: RivalCount, NeutralCount, Seed, LeaderSpeed, FollowerMaxSpeed, TurnRateDegPerSec,
    // SlotSpacing, SeparationRadius, SeparationPush, AiDecideInterval, FleeSizeRatio, HuntSizeRatio,
    // HuntMinCount, AiVisionRadius, WallProbeDistance, NeutralWanderSpeed, WanderRepickMinSeconds,
    // WanderRepickMaxSeconds, CamPitchDeg, CamBaseDistance, CamDistancePerSqrtCount, CamMaxDistance,
    // CamFollowSmoothTime, NeutralColor.

    [UnityEngine.SerializeField] private SimTuning _sim = new SimTuning { RecruitRadius = 1.2f, CombatRadius = 1f, ConvertPerSecond = 10f, PairNormalizer = 8 };
    public SimTuning Sim => _sim;   // struct copy on read

    [UnityEngine.SerializeField] private UnityEngine.Color[] _teamColors = new UnityEngine.Color[]  // EXPLICIT literals — never new Color[4]
    {
        new UnityEngine.Color(0.180f, 0.525f, 1.000f), new UnityEngine.Color(1.000f, 0.255f, 0.212f),
        new UnityEngine.Color(1.000f, 0.522f, 0.106f), new UnityEngine.Color(0.180f, 0.800f, 0.251f),
    };
    public System.Collections.Generic.IReadOnlyList<UnityEngine.Color> TeamColors => _teamColors;

    [UnityEngine.SerializeField] private UnityEngine.Material[] _teamMaterials = new UnityEngine.Material[5];
    public System.Collections.Generic.IReadOnlyList<UnityEngine.Material> TeamMaterials => _teamMaterials;

    private void OnValidate(); // clamp all private numeric fields to sane positive ranges; enforce array lengths 4/5
}
```

### G7 `Game/Contracts/Runtime/IGameSessionReadOnly.cs`
```csharp
public enum MatchState { Ready, Playing, Finished }

public interface IGameSessionReadOnly
{
    MatchState State { get; }
    float TimeRemaining { get; }
    int WinnerTeam { get; }     // -1 until Finished
    bool PlayerWon { get; }     // false until Finished
    void GetStandings(System.Collections.Generic.List<CrowdStanding> results);
    event System.Action<MatchState> StateChanged;
}
```

### H1 `Human/Scripts/Human.cs` (rewrite of the existing stub — SAME file, keep its GUID/.meta)
```csharp
public sealed class Human : UnityEngine.MonoBehaviour
{
    // Call AFTER the clone is activated. Caches SkinnedMeshRenderer + Animator (GetComponentInChildren), applies material,
    // sets shadow casting (leaders on, others off), plays the walk state with a random normalized time offset.
    public void Init(UnityEngine.Material teamMaterial, bool isLeader);
    public void SetTeamMaterial(UnityEngine.Material teamMaterial);   // sharedMaterial assignment — NEVER .material
    public void SetLeader(bool isLeader);                             // updates shadow casting
    public void SetHeadingAndSpeed(float headingDeg, float speed01);  // rotates Y toward heading; animator.speed = speed01 (clamped 0..1.5)
}
```
No upward references, no events, no bus, no Update() — fully passive; driven by CrowdRoot.

## Wave 2

### T1 `Crowd/Tests/Editor/` — `Project.CrowdCity.Core.Tests.asmdef` + `CombatResolverTests.cs`, `RecruitResolverTests.cs`, `GridAndSteeringTests.cs`, `MatchRulesTests.cs`
asmdef mirrors `Assets/@Project/EventManager/Tests/Editor/Project.EventManager.Tests.asmdef` exactly (read it), but references `Project.CrowdCity.Core`. Required cases: conversion-rate math (accumulator, clamp, reset-on-contact-break); equal-size inert; flow flip when sizes cross; 3-way contact determinism (identical input twice ⇒ identical outcome; permuted agent insertion order ⇒ same logical outcome by Id); 3-way leader-elimination attribution (nearest, tie lower team); both-lone-leaders inert; lone-leader eliminated only vs strictly larger; victim claimable by two winning teams (nearest team wins, other budget retained unmet in accumulator — assert next-call carryover); elimination-pass conversions do NOT affect other eliminations in the same call; contested recruit (nearest, Id tie); grid QueryCircle equals brute force on random data (fixed seed); SlotOffset monotonic radius + distinct offsets; standings sort + winner ties favor player; all-eliminated → -1.

### G2 `Game/Scripts/GameSession.cs`
```csharp
public sealed class GameSession : IGameSessionReadOnly, System.IDisposable
{
    public GameSession(GameConfigSO config);
    public void Initialize();                 // subscribes CrowdCountChangedEvent + CrowdEliminatedEvent via EventManager.GetSubscriber<T>(), stores tokens
    public MatchState State { get; }
    public float TimeRemaining { get; }
    public int WinnerTeam { get; }
    public bool PlayerWon { get; }
    public event System.Action<MatchState> StateChanged;
    public void Begin();                      // Ready -> Playing only
    public void Tick(float dt);               // Playing only: countdown; at <= 0 -> Finish(MatchRules.ResolveWinner)
    public void GetStandings(System.Collections.Generic.List<CrowdStanding> results);
    public void Dispose();                    // idempotent: disposes ITS OWN tokens only (no ClearAll — clearing channels it never acquired exceeds session ownership)
#if UNITY_EDITOR
    public void DebugSetTimeRemaining(float seconds);   // play-smoke hook
#endif
}
```
Elimination handling: player eliminated → Finish(defeat) even if other eliminations arrive in the same batch (defeat precedence). All rivals eliminated → Finish(victory). Handlers must not throw. Finish is idempotent; StateChanged fires once per transition.

### C2 `Crowd/Scripts/CrowdModel.cs` + `Crowd/Scripts/RivalAiDriver.cs`
```csharp
public sealed class CrowdModel
{
    public CrowdModel(int teamId, UnityEngine.Material teamMaterial, Human leader, int leaderAgentIndex);
    public int TeamId { get; }
    public UnityEngine.Material TeamMaterial { get; }
    public Human Leader { get; }              // null after elimination
    public int LeaderAgentIndex { get; }      // -1 after elimination
    public bool Eliminated { get; }
    public int MemberCount { get; }           // followers + (leader alive ? 1 : 0)
    public float HeadingDeg;                  // current desired heading (written by input/AI)
    public System.Collections.Generic.List<Human> Followers { get; }
    public System.Collections.Generic.List<int> FollowerAgentIndices { get; }  // parallel to Followers
    public void AddFollower(Human h, int agentIndex);
    public bool RemoveFollowerByAgentIndex(int agentIndex, out Human removed); // swap-remove
    public void MarkEliminated();             // Leader -> null, LeaderAgentIndex -> -1
}

public sealed class RivalAiDriver
{
    public RivalAiDriver(int teamId, GameConfigSO config, int seed);
    // Called by CrowdRoot every AiDecideInterval (sim time). Reads world, returns desired heading in degrees.
    // Policy: nearest crowd with MemberCount > FleeSizeRatio * own within AiVisionRadius -> flee (opposite dir);
    // else nearest crowd with MemberCount < HuntSizeRatio * own (own >= HuntMinCount) within AiVisionRadius -> chase;
    // else densest neutral direction (grid QueryCircle around self, neutral majority vector); fallback: keep heading.
    // Wall avoidance: Physics.Raycast forward/±35° at WallProbeDistance from leader position; blocked -> rotate toward clearest probe.
    public float DecideHeadingDeg(CrowdModel self, System.Collections.Generic.IReadOnlyList<CrowdModel> crowds, AgentBuffer buffer, SpatialGrid grid);
}
```

### G4 `Game/Scripts/InputRoot.cs`
```csharp
public sealed class InputRoot : UnityEngine.MonoBehaviour
{
    public void Initialize(GameConfigSO config);
    public void Poll();                              // reads devices NOW; called by GameplayRoot at the top of its Update — InputRoot has NO Update() of its own (single-driver rule)
    public UnityEngine.Vector2 HeadingDir { get; }   // world XZ dir, normalized; persists after release; (0,0) until first drag
    public bool HasHeading { get; }
    public event System.Action FirstDrag;            // once, when drag first exceeds 20 px deadzone
    public event System.Action RestartTapped;        // R key, or press+release without exceeding deadzone; NOT gated here
    public void Shutdown();                          // clears events/state; idempotent
#if UNITY_EDITOR
    public void SetSyntheticHeading(UnityEngine.Vector2 dir); // play-smoke hook; overrides polling until ClearSynthetic; raises FirstDrag once like a real drag
    public void ClearSynthetic();
    public void DebugTapRestart();                   // raises RestartTapped
#endif
}
```
Polling inside Poll() only: `Mouse.current` and `Touchscreen.current.primaryTouch` behind null checks; drag vector = current screen pos − press-origin; screen (x,y) maps to world (x,z) (camera yaw is fixed 0). No EnhancedTouch, no EventSystem, never throws when devices are absent.

### G5 `Game/Scripts/CameraRoot.cs`
```csharp
public sealed class CameraRoot : UnityEngine.MonoBehaviour
{
    public void Initialize(UnityEngine.Camera camera, GameConfigSO config, UnityEngine.Transform cityRoot, UnityEngine.Material buildingOccludedMaterial); // clones the material, enables ShadowCaster on the CameraRoot-owned clone, subscribes both bus events, and registers the 37 generated building colliders
    public void SetTarget(UnityEngine.Transform playerLeader);
    public void Shutdown();                     // disposes tokens; idempotent
    // LateUpdate: SmoothDamp toward pitch-55 orbit at distance CamBaseDistance + CamDistancePerSqrtCount*sqrt(playerCount), clamped;
    // after the final camera pose, registered buildings intersecting the target-to-camera sphere cast use the runtime clone; every clear/target-change/latch path restores original sharedMaterials, and reinitialize/shutdown destroys the clone after restore
    // CrowdEliminatedEvent(CrowdId==MatchRules.PlayerTeam) -> latch current pose, null target. Null/inactive target guard every frame.
}
```

## Wave 3

### C1 `Crowd/Scripts/CrowdRoot.cs`
```csharp
public sealed class CrowdRoot : UnityEngine.MonoBehaviour
{
    public void Initialize(GameConfigSO config, UnityEngine.GameObject humanPrefab, UnityEngine.Transform cityRoot);
    public void SpawnInitial();                       // Placement contract: walkable region = bounds of cityRoot child "Ground" renderer shrunk 2 m (Ground REQUIRED — throw if missing, no fallback); player at region center, rivals at 3 corner points inset 15% (assignment by team id); validity = !Physics.CheckSphere(pos + 0.9f*up, 0.6f); invalid leader spot probes outward on 1 m spiral (<=50). Neutrals: System.Random(config.Seed), <=20 attempts each, skip+summary-warning on failure. ALL placements (leaders + neutrals) are computed BEFORE any Instantiate, so CheckSphere only ever sees city colliders. RejectionRate = rejected attempts / total attempts over neutral sampling (asserted <= 0.8 by play-smoke). Ends with first coalesced count publish.
    public void SetPlayerHeading(UnityEngine.Vector2 dir, bool hasHeading);
    public void SimTick(float dt);                    // pinned phase order from DESIGN.md §4 (pending-state -> headings -> leader CC.Move -> followers -> mirror -> grid -> recruit -> combat -> atomic commit -> publish). Commit on elimination: buffer.IsLeader=false, Destroy ex-leader's CharacterController, shadows off, clone joins eliminator's followers (player leader identically). PUBLISH ORDER (pinned): all CrowdCountChangedEvents first (team asc), then CrowdEliminatedEvents — player first if present, remaining by team asc (enforces defeat precedence under synchronous dispatch).
    public float RejectionRate { get; }               // neutral spawn rejection statistic (0..1), set by SpawnInitial
    public void OnMatchStateChanged(MatchState s);    // sets pending flag only
    public System.Collections.Generic.IReadOnlyList<CrowdModel> Crowds { get; }
    public UnityEngine.Transform PlayerLeaderTransform { get; }   // null after player elimination
    public void Shutdown();                           // destroys spawned clones (never the prefab asset); idempotent
}
```
⟲⟲⟲⟲ SpawnClone = `Instantiate(humanPrefab, ...)` (prefab asset, NOT a scene object). The prefab is created by the editor setup from the fully-configured scene template (Animator on Human_Base child, walk controller, materials) and is active, so no scene-template dependency exists at runtime. Publishes authoritative counts/eliminations via cached `EventManager.GetPublisher<CrowdCountChangedEvent>()` / `<CrowdEliminatedEvent>()`. CrowdRoot has no `TMPro` dependency and owns no label, presentation material, or UI lifecycle. Leaders get a `CharacterController` (radius 0.35, height 1.8, center y 0.9, skinWidth 0.08) added at spawn; movement fixed Y (Move with y-delta 0). Followers/neutrals: transform steering only. City walkable bounds = the `cityRoot/Ground` child renderer bounds shrunk 2 m (same rule as SpawnInitial — Ground REQUIRED). No object pooling: population is conserved.

### G3 `Game/Scripts/GameplayRoot.cs`
```csharp
public sealed class GameplayRoot : UnityEngine.MonoBehaviour
{
    public void Initialize(GameSession session, GameConfigSO config, TMPTextStyleSO crowdCountTextStyle, UnityEngine.GameObject humanPrefab, UnityEngine.Camera mainCamera, UnityEngine.Transform cityRoot, UnityEngine.Material buildingOccludedMaterial);
    public event System.Action ReloadRequested;   // raised only when session.State == Finished and RestartTapped fires; after raising, Update RETURNS immediately (the fixed-step loop must not run in the same frame as a synchronous scene reload)
    public void Shutdown();                       // idempotent. EXACT order: unbind own C# bindings -> HudRoot.Shutdown -> CameraRoot.Shutdown -> CrowdRoot.Shutdown -> InputRoot.Shutdown (presentation -> sim -> input) -> destroy the child root GOs it created
    // Initialize order (PINNED): create child GOs + AddComponent all four roots -> inputRoot.Initialize -> crowdRoot.Initialize(config, prefab, cityRoot) -> cameraRoot.Initialize -> hudRoot.Initialize(session, config, style, camera)
    //   -> session.Initialize() -> bind C# events (FirstDrag -> session.Begin; RestartTapped -> gated ReloadRequested; session.StateChanged -> crowdRoot.OnMatchStateChanged)
    //   -> crowdRoot.SpawnInitial() -> cameraRoot.SetTarget(crowdRoot.PlayerLeaderTransform) -> hudRoot.BindLeaderLabels(all teams 0..3)
    // Update (PINNED): ① inputRoot.Poll() ② restart gate ③ fixed-step accumulator dt=0.02 (<=4 steps/frame): per step { crowdRoot.SetPlayerHeading(inputRoot.HeadingDir, inputRoot.HasHeading); session.Tick(step); crowdRoot.SimTick(step); }
}
```

### G1 `Game/Scripts/GameSceneController.cs`
```csharp
public sealed class GameSceneController : UnityEngine.MonoBehaviour
{
    // EXACT serialized field names (editor setup wires them via SerializedObject): config, crowdCountTextStyle, humanPrefab, mainCamera, cityRoot, buildingOccludedMaterial
    [UnityEngine.SerializeField] private GameConfigSO config;
    [UnityEngine.SerializeField] private TMPTextStyleSO crowdCountTextStyle;
    [UnityEngine.SerializeField] private UnityEngine.GameObject humanPrefab;    // Assets/@Project/Human/Prefabs/Human.prefab (editor setup creates it from the configured scene template)
    [UnityEngine.SerializeField] private UnityEngine.Camera mainCamera;
    [UnityEngine.SerializeField] private UnityEngine.Transform cityRoot;        // GameArea/City
    [UnityEngine.SerializeField] private UnityEngine.Material buildingOccludedMaterial; // Assets/@Project/City/Materials/City_Occluded.mat
    // Awake: null-validate only (Debug.LogError + disable self on missing ref). NO scene-template deactivation — runtime uses the prefab asset; editor setup already deactivated the scene GameArea/Human.
    // Start: _session = new GameSession(config); create "GameplayRoot" child GO; _gameplayRoot.Initialize(session, config, crowdCountTextStyle, humanPrefab, mainCamera, cityRoot, buildingOccludedMaterial); _gameplayRoot.ReloadRequested += OnReloadRequested
    // OnReloadRequested: SceneManager.LoadScene(gameObject.scene.buildIndex)
    // OnDestroy EXACT order: ① _gameplayRoot.ReloadRequested -= ② _gameplayRoot.Shutdown() ③ _session.Dispose() LAST (session outlives roots so late events during root teardown are still handled)
#if UNITY_EDITOR
    public GameSession DebugSession { get; }   // play-smoke hook
#endif
}
```

### U1 `Hud/Scripts/HudRoot.cs`
```csharp
public readonly struct CrowdLabelBinding
{
    public readonly int TeamId;
    public readonly UnityEngine.Transform LeaderTransform;
    public CrowdLabelBinding(int teamId, UnityEngine.Transform leaderTransform);
}

[UnityEngine.DefaultExecutionOrder(100)]
public sealed class HudRoot : UnityEngine.MonoBehaviour
{
    public void Initialize(IGameSessionReadOnly session, GameConfigSO config, TMPTextStyleSO crowdCountTextStyle, UnityEngine.Camera worldCamera); // loads the TMP font, creates Canvas + per-team label materials, subscribes bus + session.StateChanged
    public void BindLeaderLabels(System.Collections.Generic.List<CrowdLabelBinding> bindings); // binds all living teams 0..3; creates Screen Space Overlay labels for every leader and off-screen markers for rivals 1..3; reads current session counts after the initial publish
    public void Shutdown(); // disposes tokens, unhooks session, destroys Canvas, labels, markers, and runtime label materials; idempotent
}
```
All HudRoot text uses TextMesh Pro; no legacy `Text`, `TextMesh`, or `LegacyRuntime.ttf`. `HudRoot.Initialize` loads `Resources/Fonts & Materials/LiberationSans SDF - Fallback` and throws immediately if that tracked TMP resource is missing. Elements: timer top-center (M:SS), leaderboard top-right 4 rows "color swatch (Image) + count", start hint "DRAG TO START" (visible in Ready), result overlay (dark Image + WIN!/LOSE + final standings + "R / TAP TO RESTART") shown on Finished, Screen Space Overlay labels for all leaders, and rival off-screen direction/count markers. Leader labels use `TextMeshProUGUI`, `FontStyles.Bold`, white face color, each team's outline color, and `TMPTextStyleSO.OutlineWidth`; player text retains its team-colored arrow. `DefaultExecutionOrder(100)` makes HudRoot run after the default-order camera update. Each `LateUpdate` projects the latest rendered leader position plus the label offset into Overlay Canvas coordinates, then updates rival markers with the same viewport predicate: an on-screen rival shows only its label, while an off-screen or behind-camera rival shows only its marker. Count/elimination events update or remove labels, markers, and ranking/result presentation; handlers never mutate simulation state or throw.

### U2 `Hud/Scripts/TMPTextStyleSO.cs`

```csharp
[UnityEngine.CreateAssetMenu(menuName = "AF/CrowdCity/TMP Text Style", fileName = "TMPTextStyle")]
public sealed class TMPTextStyleSO : UnityEngine.ScriptableObject
{
    public UnityEngine.Color FaceColor { get; }
    public float OutlineWidth { get; } // clamped 0..1
}
```
The SO is immutable source configuration only; it stores no runtime material or current count. `HudRoot` combines its face/width with each `GameConfigSO.TeamColors` value to create and own the team-outline runtime materials.

### E1 `Game/Editor/GameSceneSetup.cs`
`[MenuItem("AF/CrowdCity/Setup Game Scene")] public static void Apply()` — implements DESIGN.md §5 steps 1–7 exactly (load-or-create/converge, save only if changed). Constants for all asset paths. Also `public static void ApplyBatch()` (Apply + `EditorApplication.Exit(0/1)`).
⟲⟲⟲⟲ **Prefab step (new step 4b, after the scene template is fully configured — Animator on Human_Base child, controller, materials — and BEFORE controller wiring):** create/refresh `Assets/@Project/Human/Prefabs/Human.prefab` from the configured `GameArea/Human` scene GameObject via `PrefabUtility.SaveAsPrefabAsset` (load-or-create: overwrite same path each run so it converges). Then `SetActive(false)` on the scene `GameArea/Human` (it is no longer used at runtime; runtime clones the prefab) and save the scene. Load-or-create `Assets/@Project/Hud/CrowdCountTextStyle.asset`; wire `GameSceneController.humanPrefab` and `crowdCountTextStyle` to the intended assets via SerializedObject; wire `config`, `mainCamera` (scene Main Camera), `cityRoot` (GameArea/City), and `buildingOccludedMaterial` (`Assets/@Project/City/Materials/City_Occluded.mat`) as before (no `humanTemplate` field anymore). TMP Essential Resources under `Assets/TextMesh Pro/**` are tracked project infrastructure and are not imported or generated by GameSceneSetup. City building generation must preflight source/scene/provenance before writes and converges 37 generated mesh/prefab instances transactionally.

City policy: Full `Setup Game Scene` only read-validates the City source, generated assets, and 37-building hierarchy before other writes; that preflight does not require an existing `GameSceneController` or its references. Full Setup never mutates City assets/hierarchy, but afterward load-or-creates `GameSceneController` and wires `cityRoot`, `mainCamera`, and `buildingOccludedMaterial`. `[MenuItem("AF/CrowdCity/Setup City Buildings")]` is the sole City convergence entry point, requires exactly one open scene (the active GameScene, no additive scenes), and succeeds even when the controller is absent; when present it may converge only `buildingOccludedMaterial`, while Full Setup owns controller creation and complete reference wiring.

### E2 `Game/Editor/GameSceneValidator.cs`
`[MenuItem("AF/CrowdCity/Validate Game Scene")] public static void ValidateMenu()`; `public static bool Validate()` — DESIGN.md §5 assertions incl. AnimationUtility curve-path resolution; logs `[Validator] PASS` / `[Validator] FAIL: <reasons>`; `public static void ValidateBatch()` exits 0/1. ⟲⟲⟲⟲ Validate the PREFAB (`Assets/@Project/Human/Prefabs/Human.prefab`), not the scene template: it exists, has a `Human_Base` child carrying SkinnedMeshRenderer + Animator whose controller default-state motion is a looping AnimationClip, and every `AnimationUtility.GetCurveBindings(clip)` path resolves via `transform.Find` under the Animator inside the prefab. Also assert `GameSceneController.humanPrefab` and `crowdCountTextStyle` refs are non-null and point at the intended assets and that `TMPTextStyleSO.OutlineWidth` is in range. The required LiberationSans fallback is a HudRoot runtime `Resources.Load` fail-fast dependency, not an Editor-validator assertion.

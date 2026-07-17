# Unity AI Working Rules

This document defines the working rules an AI must follow when creating or modifying code in a Unity project.

To have an AI agent read these rules automatically in a new project, copy this file to the project root as `AGENTS.md` or as the instruction file read by that tool.
This file is a condensed, AI-focused subset of `TEAM_ARCHITECTURE_GUIDE.md`'s mandatory safety rules.

This `CLAUDE.md` applies to the repository root and everything below it. The source of truth for detailed architecture is the in-project [`UnityArchitectureGuide/TEAM_ARCHITECTURE_GUIDE.md`](UnityArchitectureGuide/TEAM_ARCHITECTURE_GUIDE.md); follow that guide for details not covered here. If a more specific instruction file applies to a nested scope, merge it with this document, and surface any conflict instead of hiding it.

**Keep this file lean (self-maintenance).** `CLAUDE.md` is the always-loaded layer — mandatory safety contracts, the operating model, decision triggers, and concise rules with pointers. Put implementation-level or situational detail in the team guide (read on demand), not here; when a rule here grows into such detail, relocate the detail to the guide and leave a short rule + pointer, and prefer editing an existing rule over appending a new one. But **never move a mandatory safety contract behind a pointer** — it must stay inline so it is always in context. Length itself is not the enemy: keep load-bearing contracts inline and relocate only genuine detail; slim only when a section has grown into detail, not for its own sake.

## Main/Subagent Operating Model and Context Isolation

- In sessions where subagents are available, the main agent must act only as manager and controller. There is no exception that allows the main agent to perform investigation, file reading, detailed analysis, implementation, editing, testing, or diff review directly, even for trivial tasks.
- The main agent is responsible only for defining the goal and scope; surfacing outcome-relevant assumptions and tradeoffs; defining success criteria; decomposing work; assigning file ownership to subagents; judging results; and delivering the final report.
- Delegate investigation, file reading, detailed analysis, implementation, file modification, testing, and complete diff review to subagents with explicit ownership. If concurrency slots are unavailable, delegate sequentially instead of having the main agent do the work.
- Keep only confirmed facts, decisions, change results, key verification results, and remaining risks in the main context. Keep source dumps, detailed reasoning, repeated logs, and trial-and-error in subagent contexts.
- A subagent's report to the main agent must contain only `Conclusion`, `Changed files`, `Verification commands and results`, and `Risks or blockers`. Do not include process logs or unnecessary source dumps.
- Never allow two or more subagents to edit the same file concurrently. Assign one owner per file before delegation, and confirm that earlier work has ended before transferring ownership.
- For non-trivial work, assign implementation and verification to different subagents. One subagent may execute and verify a trivial task, but the main agent still has no exception to implement or verify it directly.
- Compare every subagent report against the success criteria. If evidence is insufficient, delegate corrective work within the same scope. Never report unverified work as complete.

## Behavioral Guidelines to Reduce Common LLM Coding Mistakes

Merge with project-specific instructions. Tradeoff: these bias toward caution over speed; for trivial tasks, use judgment. They are working when diffs carry fewer unnecessary changes, there are fewer overcomplication rewrites, and clarifying questions come before implementation rather than after mistakes.

1. **Think before coding.** State assumptions explicitly; if uncertain, ask. If multiple interpretations exist, present them — don't pick silently. If a simpler approach exists, say so. If something is unclear, stop, name it, and ask.
2. **Simplicity first — minimum code that solves the problem, nothing speculative.** No features beyond what was asked, no abstractions for single-use code, no unrequested "flexibility"/"configurability", **No error handling for impossible scenarios**. If 200 lines could be 50, rewrite it. If a senior engineer would call it overcomplicated, simplify.
3. **Surgical changes — touch only what you must; clean up only your own mess.** Don't "improve" adjacent code/comments/formatting or refactor things that aren't broken; match existing style. Remove only the imports/variables/functions your changes made unused; mention pre-existing dead code, don't delete it. Every changed line should trace directly to the request.
4. **Goal-driven execution — define success criteria, then loop until verified.** Turn tasks into verifiable goals ("add validation" → "write tests for invalid inputs, then make them pass"; "fix the bug" → "write a reproducing test, then make it pass"; "refactor X" → "tests pass before and after"). For multi-step tasks, state a brief plan with a verify check per step.

### Project-Specific Safety Precedence

The instruction "No error handling for impossible scenarios" forbids only speculative handling for scenarios that are genuinely impossible. It never permits omitting this guide's mandatory safety contracts: async cancellation and exception handling, event/EventBus unsubscription, matching Addressables handle and instance Release, or duplicate-call prevention and receipt/server/integrity validation for purchases, rewards, and currency. These requirements take precedence and must not be classified as handling impossible scenarios.

## 0. Default Mindset

- Do not build a complex framework. Make ownership, state location, communication method, and lifetime responsibility explicit.
- Check existing project conventions before applying this document.
- When uncertain, do not guess. State what is uncertain, then ask.
- Keep changes small. Do not refactor, clean up, or rename anything unrelated to the request.
- Define verifiable success criteria first, verify them as far as possible, and only then finish.

## 0.1 Applying These Rules to an Existing Project

Do not overhaul a project that is already in progress when applying this document.

- This project's existing code root is `Assets/@Project`. In this project, this known fact takes precedence over the general root-detection rule below. Keep new code under this root and follow its current naming; do not move it to `Assets/_Project` or `Assets/Game` or create a parallel root outside the request's scope.
- First determine whether the current folder root is `Assets/_Project` or `Assets/Game`, and follow the existing root and naming.
- Even if the existing structure differs from this document, do not change structure outside the request's scope.
- Apply these rules first to new features, new files, and changes that can be isolated.
- If you find an existing static hub, Manager, direct SaveData access, or centralized event structure, do not extend that pattern in new code.
- To correct an existing counterexample, separate it into a dedicated refactoring task and present the impact scope and verification method first.
- If file placement is unclear, inspect `rg --files`, nearby folders, existing namespaces, and asmdefs before deciding.
- If the project contains more than one path pattern, do not choose arbitrarily; ask the user which standard to follow.

## 0.2 Adoption Stages

Do not create every structure up front. Apply only the rules required by the project's current stage.

- Treat this project as currently being at the MVP stage. Introduce Growth or Advanced structures only after the corresponding problem and requirement actually exist.
- Do not newly introduce asmdefs, EventBus, UniTask, or Addressables before a current feature requirement, an existing dependency, or a real boundary or lifetime problem requires them. For this project, do not use the general MVP option below that permits placing an EventBus implementation in advance. When a tool is already in use, follow the existing approach and its mandatory lifetime rules within the request's scope.

### MVP Stage: Mandatory from the Start

- Give every feature a clear owner.
- Use direct calls, C# events/callbacks, and parent-level binding as the default communication methods.
- Do not store runtime state in a ScriptableObject.
- Unsubscribe in the same lifecycle in which you subscribed.
- Define cancellation and exception-handling rules for asynchronous work.
- Separate Editor code from runtime code.
- Do not trust the client alone for sensitive values such as rewards, purchases, currency, and rankings.
- An EventBus implementation may exist in advance, but do not use it as the default communication method.
- At MVP, limit EventBus exceptions to cases where two or more independent boundary objects must observe the same fact that has already happened.

### Growth Stage: Introduce When Connections Between Features Increase

- Introduce EventBus when multiple features or boundary objects begin observing the same fact that has already happened. Do not use EventBus for a single receiver or when parent-level binding is sufficient.
- Introduce QueryBus or Command when sibling-feature connections proliferate and you need an immediate query or state-change entry point.
- Add an AssetProvider when dynamic loading and release responsibilities become scattered.
- Create a read-only Runtime interface when multiple features begin reading the same Runtime value.
- Split major areas into asmdefs when compilation time or reference-boundary problems actually occur.

### Advanced Stage: Introduce for Live Operations or Large-Scale Collaboration

- Add RuntimeRegistry only when the session has many runtime endpoints.
- Introduce per-feature asmdefs when feature independence and team size grow.
- Treat Remote Addressables, Remote Config rollout, and server-side reward validation as mandatory contracts as soon as live-operation features are introduced.

## 1. Decision Log Before Work

Before adding a new public API, state store, event/query/save key/asset key, asmdef, or resource-lifetime change, write down these five lines:

```txt
1. Target feature owner:
2. New file location:
3. State location: SO / RuntimeModel / SaveData / Server
4. Communication method: direct call / C# event/callback / parent-level binding / EventBus / QueryBus / Command
5. Affected files:
```

The Decision Log is a pre-work decision note, not an actual commit message. Unless the user asks to keep it in code, include it in the final report; if the team has designated a location, store it in a feature README or `Docs/.../DECISIONS.md`.

If the decision is ambiguous, do not begin implementation; ask.

## 2. Ownership Rules

- Scenes assemble; features own.
- `SceneController` handles only creation order and wiring.
- `GameplayRoot` connects multiple feature roots and binds features to one another.
- `GameSession` owns session flow such as Ready/Playing/Pause/GameOver.
- `FeatureRoot` owns creation, teardown, rules, and RuntimeModel within its own feature.
- A parent may call its child directly.
- A child must not know its parent directly. Use a C# event/callback when a child must notify its parent.
- Sibling features must not reference one another's implementations directly.

## 3. State Location Rules

- Store source data, balance data, asset references, and catalogs in `ScriptableObject`.
- Store state that changes during the current play session in `RuntimeModel` or `GameSession`.
- Store values that must survive an app restart in `SaveData`.
- The client must not be the final authority for purchase-, currency-, reward-, ranking-, or cheat-sensitive values. Treat `Server` validation as a mandatory contract for live-economy features.
- Do not modify SO values at runtime as if the SO were the current-state store.
- If an SO runtime cache is needed, separate source data from play state.

## 4. Communication Method Rules

- A parent controls a child: use a direct call.
- A child notifies a parent: use a C# event/callback.
- Sibling features communicate: bind them at a parent root.
- Notify a distant feature of a fact that has already happened: use EventBus.
- Read the current value of a distant feature immediately: use QueryBus or a read-only Runtime interface.
- Change state: use the state owner's public API or a Command.
- Do not use EventBus/QueryBus directly from leaf Views, Tiles, Buttons, or Enemies.
- Limit EventBus/QueryBus consumers to boundary objects such as feature roots, sessions, tutorials, HUD, and Analytics.

Here, "distant feature" refers to ownership distance, not hierarchy distance. Features are distant when they are not in a parent/child relationship and must not reference each other's implementations directly.

## 5. EventBus Rules

EventBus communicates only facts that have already happened by default.
Send requests to change rewards, purchases, currency, saves, or game rules through the state owner's API or a Command, not through EventBus.

Good examples:

```txt
BoardItemMerged
CustomerOrderCompleted
RewardClaimSucceeded
RewardClaimFailed
```

Bad examples:

```txt
GetGoldRequest -> GetGoldResponse
SetInventoryItem
RewardClaimRequested
EveryFrameEnemyPositionChanged
```

Payload rules:

- Prefer IDs, coordinates, enums, numbers, and immutable snapshots.
- Do not include `GameObject`, `MonoBehaviour`, mutable collections, or internal implementations.
- Do not send high-volume per-frame data through EventBus.
- Always unsubscribe.

Event type location:

- Put only infrastructure such as `Publish`, `Subscribe`, and `IEventBus` in the EventBus folder.
- By default, place a `readonly struct` event payload under `Events` in the feature where the event occurred.
- Place a public event that other features also subscribe to under `Contracts/Events` in the originating feature.
- Use `Shared.Contracts/Events` only for ownerless common contracts such as app initialization, session changes, or shared-currency changes.
- Decide event location based on "which domain did the event occur in," not "who listens."
- Do not collect every event under `EventBus/Events`, `Shared/Events`, or `GameEvents.cs`.

Dispatch rules:

- An MVP-light EventBus uses synchronous dispatch on the Unity main thread by default.
- Game rules must not depend on subscriber execution order.
- If a subscriber must call `Publish` again, record the cycle risk and execution order in the Decision Log.
- Do not create a cycle in which event A publishes B and B publishes A again.
- If the implementation cannot safely handle subscription or unsubscription during dispatch, copy the subscriber list into a snapshot before publishing.

## 6. QueryBus Rules

QueryBus is an access point for values needed immediately.

- Keep the minimum API small, around `Register<T>(T provider) : IDisposable` and `TryGet<T>(out T provider)`.
- Register only read-only interfaces with no mutators in QueryBus, never implementations.
- Do not send write commands through QueryBus.
- If query results are cached for a long time, define an invalidation rule as well.
- Register Providers only from a feature root or parent root.
- Unregister a Provider in the same lifecycle in which it was registered.
- Treat duplicate registrations of the same type as an error or define an explicit replacement policy.
- Do not register leaf objects such as individual tiles, buttons, enemies, or cells as Providers.
- Do not call QueryBus directly from leaf objects or per-frame hot paths.
- Prevent QueryBus from becoming a Service Locator used to retrieve arbitrary features.

## 7. Command / API Rules

A Command/API is an explicit entry point for state changes.

- The feature or domain service that owns the state must own the Command/API.
- Process rewards, purchases, currency, saves, and server requests through a Command/API, not EventBus.
- The caller must be able to observe success, failure, and cancellation.
- An asynchronous Command must accept a `CancellationToken`.
- Define duplicate-call prevention for reward, purchase, and currency Commands.
- For live-economy Commands, evaluate the need for an idempotency key, server validation, and a reward ledger.
- Do not funnel every write request through one global `CommandBus`.
- Notify other features through EventBus only of facts that result from the state change.

## 8. Subscription and Unsubscription

Unsubscribe in the same lifecycle in which you subscribed.

```txt
Awake      -> OnDestroy
OnEnable   -> OnDisable
Initialize -> Dispose / Clear
Open       -> Close
OnSpawn    -> OnDespawn
```

- If you subscribe in `OnEnable`, unsubscribe in `OnDisable`.
- If you subscribe in `Initialize`, unsubscribe in `Dispose`.
- If you subscribe in `OnSpawn`, unsubscribe in `OnDespawn`.
- If `Initialize` can be called twice, unsubscribe the existing subscription first or prevent duplicate initialization.
- Use `OnDestroy` only as a final safety net.
- When a pooled object returns to the pool, clear its previous runtime state and subscriptions.

## 9. UniTask / Async Rules

- Whenever possible, asynchronous work must accept a `CancellationToken`.
- Work based on MonoBehaviour must use `destroyCancellationToken` or `GetCancellationTokenOnDestroy()`.
- Every repeated async loop must have a cancellation condition.
- `await` work whose result or failure matters.
- Allow `.Forget()` only for work whose failure cannot affect the flow.
- When using `.Forget()`, handle exceptions inside the work or provide an exception handler.

## 10. Resources / Addressables Rules

- A feature root or that feature's Factory must own dynamic creation.
- Whoever creates an object is responsible for teardown, Pool return, and Addressables Release.
- Do not scatter `Resources.Load("string/path")` across multiple features.
- Manage Resources paths and Addressables keys in a searchable location such as constants, CatalogSO, or AssetId.
- Do not Release an Addressables handle before the instance lifetime ends.
- If you use `Addressables.InstantiateAsync`, define the corresponding Release policy.
- If you call `Instantiate` directly after `LoadAssetAsync`, the load-handle owner must remain alive for the entire instance lifetime.

## 11. asmdef / Editor Rules

- Separate Editor code from runtime code.
- Restrict `*.Editor.asmdef` to the Editor platform.
- An Editor assembly may reference a runtime assembly.
- A Runtime assembly must not reference an Editor assembly or `UnityEditor`.
- If ScriptableObject type code is required at runtime, place it in a runtime assembly.
- Place custom inspectors, importers, validators, and build tools in an Editor assembly.
- Introduce per-feature asmdefs only when needed. Do not split them into tiny units from the start.
- When creating a `Feature.Contracts` assembly, include only public events, read-only Runtime interfaces, and shared IDs.
- `Feature.Contracts` must not reference that feature's Runtime implementation or an EventBus implementation.
- Other features must not reference `Feature.Runtime`; when necessary, they may reference `Feature.Contracts` only.
- Use `Shared.Contracts` only for ownerless common contracts.
- Do not move arbitrary types into Shared to resolve circular references.

## 11.5 Prefab Construction Rules

These rules cover how runtime object trees are constructed and loaded. They add only the construction/loading perspective on top of §2 (Ownership), §4 (Communication), §8 (Subscription), and §10 (Resources); do not duplicate those rules.

- **Minimize scene placement.** A scene holds only the bootstrap (`GameSceneController`) and designated environment objects (Main Camera, `GameArea/City`, and inactive authoring templates such as `GameArea/Human`). Every other feature/spawn-target tree is created at runtime from prefabs.
- **Script-preattached prefabs.** Author features and spawn targets as prefabs with their scripts already attached. Do not `AddComponent<FeatureScript>` on a clone, and do not assemble a feature tree at runtime with `new GameObject() + AddComponent` — this includes uGUI (HUD Canvas, labels, markers).
- **The ownership/assembly chain IS the (documentary) mediator.** Creation and assembly flow only along `SceneController → GameplayRoot → FeatureRoot → spawn target`. A parent Instantiates a child prefab, then injects dependencies synchronously exactly once via `Init(deps)`; siblings are bound at the parent root. This tree serves the mediator role — **do not add a separate Mediator/central-hub class.** A child never references its parent or `SceneController` directly (consistent with §2).
- **Dynamic load via a central stateless loader (class name for prefabs).** Code-loaded prefabs whose root carries a single main-script component load **by that component's class name**: the prefab file name MUST equal its root component's `Type.Name`. A **single shared, stateless** central loader is the sanctioned tool (this is the guide's AssetProvider role, not a God Manager) — a `ResourceLoader` with **kind-scoped methods, one per `Resources/<kind>/` folder** (`LoadRoot<T>()` now; add `LoadPrefab<T>()` / `LoadAsset<T>(name)` later only when a real code-load need for that kind appears — §0.2). Name each method for its verb; do **not** name the type `ResourceManager` (that invites scope creep). Prefab kinds resolve **by class name** — `LoadRoot<T>()` does `Resources.Load<GameObject>("Roots/" + typeof(T).Name)` → `GetComponent<T>()` → return the component (do not rely on `Resources.Load<T>` returning a component — undocumented for prefab components). The class-name-derived key **is** the searchable key contract — do not hand-maintain a per-asset path-string constant for these roots. Prefab files stay under each feature's own `…/Resources/<kind>/` folder (ownership); only the post-`Resources/` segment forms Unity's load key, so files remain feature-local while the key is flat `<kind>/<ClassName>`. **The loader MUST stay a pure function:** no cached/held instances, no lifetime ownership (the caller Instantiates and Destroys/Releases per §10), no live-feature/service retrieval, no save/UI/game-rule work. That boundary is exactly what separates an acceptable load API from the forbidden **God Manager / Service Locator** (§6, §13) — kind-scoped load *methods that compute the key* are fine, but adding a `Get<TFeature>()`, an instance cache, or a hand-maintained **map that lists every asset's path** crosses it. Flat `<kind>/<ClassName>` is collision-safe **only with both Editor validators kept mandatory**: (i) the existing merged-Resources duplicate-key check — the real backstop across ALL `Resources/` assets (SOs, non-root prefabs, fonts, `.bytes`, case-only/extension dupes); and (ii) a per-root check: file name == root `Type.Name`, component on the **root** and the **only** one of its exact type, type concrete/non-nested/non-generic, and root simple-names globally unique. Only if the project later adopts namespaces and needs same-simple-name roots across features, promote the key to a feature-segmented form (`<Feature>/Roots/<ClassName>`) then (§0.2). **ScriptableObjects/shared assets do NOT use class-name resolution:** one class often backs many named assets and the file name usually differs from the class (e.g. a style SO; `WallSdf.asset` ↔ `WallSdfAsset`, `GameConfig.asset` ↔ `GameConfigSO`), so `LoadAsset<T>(assetName)` takes an **explicit name** with a feature-owned constant — and prefer serializing such assets on the consuming prefab (see Injection scope), so most SOs/UI templates never need a loader kind at all. Promote to Addressables only when large/remote/independently-releasable assets require it (§0.2).
- **Injection scope (inject only what belongs in the chain).** The parent's `Init(deps)` passes down ONLY: (a) **scene-object references** (Camera, scene Transforms, scene-instance Materials) — a Resources prefab cannot serialize a scene reference (it deserializes null), so these MUST be parent-injected; (b) data **genuinely consumed by ≥2 sibling features at the same rank** (bind at the common parent — §4); (c) **runtime/session state** (GameSession, RuntimeModel handles). Everything else — editor-authored assets (SO/prefab/asset-Material/font) used by a **single** feature — must NOT be drilled through the tree: place it as a `[SerializeField]` on that feature's own prefab (or feature self-load via the typed loader above), chosen per the §3 state-location table. Never pass a dependency through a level that does not consume it — if an `Initialize` parameter is never read, delete it (no dead pass-through). Validate serialized authoring refs as non-null during `Init`; never add a silent `Resources.Load` fallback that hides an unwired field. Direct-construction tests/harnesses (`AddComponent`) bypass prefab serialization — they must instantiate the wired prefab or assign test-only fields explicitly.
- **Init-only lifecycle & injection.** Author prefabs active. `Awake`/`OnEnable` must be dependency-free (no reading injected dependencies, no parent/sibling/other-feature references, no external publish, no manager/bus/tick registration); the first external publish is a separate owner-driven step after wiring completes. `Init` runs synchronously exactly once; on failure the owner destroys the clone it just created and cancels its registration.
- **Baked physics & determinism exception.** Baked colliders/`CharacterController` are live the instant they are Instantiated, so place no physics step or overlap query between Instantiate and Init (all spawn-placement checks run before Instantiate). Bake determinism-pinned specs (e.g. CC radius/height/center/skinWidth) into the prefab, but verify the values only with an Editor validator (constant match) — never repair them at runtime. The pure-C# simulation kernel (`Crowd/Core`) is exempt from prefab rules.

## 12. Security / Live Operations Rules

- Do not trust client SaveData as the final truth.
- Account for duplicate grants and tampering in purchases, ad rewards, and event rewards.
- When IAP, ads, server rewards, or live currency are introduced, evaluate receipt validation, ad SSV, idempotency keys, reward ledgers, replay prevention, and server-authoritative time.
- Validate external URLs, deep links, push payloads, and server responses before use.
- Give Remote Config values a fallback/default and range validation.
- For Remote Config, SaveData, and Addressables data, evaluate schema version, minimum app version, fallback, and migration requirements.
- If game rules, prices, events, or a Remote Addressables catalog can change remotely, define kill-switch, rollout, and rollback criteria.
- Do not log personal information, tokens, or raw account IDs in Analytics/Crash logs.
- Prevent development cheat/debug menus from being exposed in production builds.

## 13. Forbidden Anti-Patterns

The following are forbidden by default. If an exception is necessary, record the feature owner, reason, impact scope, and removal condition in the Decision Log.

- Overhauling an existing project outside the request's scope.
- Creating a God Manager.
- Directly referencing one feature implementation from another.
- Having a child directly reference its parent or `SceneController`.
- Having UI modify domain state directly.
- Storing current session state in a global singleton/static.
- Creating a static event without assigning responsibility for clearing it.
- Using a Shared folder as a warehouse for hypothetical common code.
- Storing current play state in an SO.
- Reading and writing SaveData continuously as if it were a RuntimeModel.
- Imitating request-response RPC through EventBus.
- Sending write commands through QueryBus.
- Attaching leaf objects directly to EventBus/QueryBus.
- Putting internal implementations or mutable collections in event payloads.
- Collecting domain event payloads in the EventBus folder.
- Choosing event payload location based on consumers.
- Using Shared as a collection of all events.
- Sending all write requests through a global CommandBus.
- Using QueryBus as a Service Locator.
- Repeatedly calling QueryBus from leaf objects or hot paths.
- Sending high-volume per-frame data through EventBus.
- Subscribing to an event/EventBus without unsubscribing.
- Using `.Forget()` without exception handling.
- Losing the owner of an Addressables handle.
- Referencing `UnityEditor` from a Runtime assembly.
- Mixing a public API change and an internal refactor in one task.

## 14. Post-Work Report

Include the following in the completion report:

- Changed files.
- Decisions about ownership, state location, and communication method.
- What was verified.
- Remaining risks or items requiring manual confirmation.
- If existing project conventions conflict with this document, which one took precedence.

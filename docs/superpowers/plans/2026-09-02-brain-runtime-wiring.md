# Brain Runtime Wiring Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Connect the existing PlayerParity Brain, policy transport, episode state, reward attribution, and reset adapter to the main-thread game loop so one manually entered mission can run unattended episodes.

**Architecture:** A pure `BrainRuntimeCoordinator` owns lifecycle, generation guards, decision cadence, event audit, and pending pipe DTOs. A Logic adapter calls it only from `FcsModule.Update()` and translates accepted actions to the existing `PlayerParityActionBridge`; the pipe worker never touches Unity or IL2CPP. `BrainResetHarness` remains the watchdog and `MissionManagerResetAdapter` supplies lifecycle predicates.

**Tech Stack:** .NET 6 C#, existing MelonLoader/Unity IL2CPP bindings, newline-delimited JSON Named Pipe, stdlib-only Python client, console integration tests with fake adapters.

**Spec:** User request dated 2026-09-02: connect the pieces; no PPO, simulator, menu automation, new parser/reward design, or manual input dependency.

## Global Constraints

- Brain remains disabled unless its existing opt-in flags are enabled; ordinary FCS behavior must be unchanged.
- Unity/IL2CPP access is main-thread-only; no `async`/`Task.Delay` is allowed for game operations.
- PlayerParity observation may contain only legal Telegram/MapReveal/ScoutRecon knowledge and public action masks.
- Pipe requests are pure DTOs and are queued for `FcsModule.Update()`; timeout/disconnect falls back to `Hold`.
- Every callback/action/event carries the active `episode_id`; stale generations are rejected and audited.
- A real mission reset must be reported `UNVERIFIED IN REAL MISSION` until manually entered runtime testing succeeds.
- This bring-up hardening pass does not add RL, simulator, planner, parser, reward tuning, or menu automation.

### Task 1: Define the fake-runtime contract and failing integration tests

**Files:**
- Create: `IronNestFCS.Abstractions/Brain/BrainRuntimeCoordinator.cs`
- Modify: `IronNestFCS.Brain.Tests/Program.cs`

**Interfaces:**
- `BrainRuntimeCoordinator` consumes an `IBrainFacade`, a `BrainResetHarness`-compatible reset callback set, and a pure `IBrainRuntimeTransport`.
- It produces `WaitingForMission`, `DecisionRequest`, `ActionAccepted/Rejected`, `EpisodeTerminal`, and reset audit events.

- [x] Step 1: Add tests for normal observation → action → transition → terminal → reset → next episode, timeout Hold, stale decision rejection, old-event rejection, reset timeout safe state, and disconnect Hold.
- [x] Step 2: Run the console test harness and verify each new test fails because the coordinator/contract is absent.
- [x] Step 3: Define the smallest DTO/adapter interfaces needed by those tests, without Unity references.

### Task 2: Implement the pure event-driven coordinator

**Files:**
- Create/modify: `IronNestFCS.Abstractions/Brain/BrainRuntimeCoordinator.cs`
- Modify: `IronNestFCS.Abstractions/Brain/BrainPipeProtocol.cs`
- Modify: `IronNestFCS.Abstractions/Brain/BrainFacade.cs` only if a missing decision/episode guard is demonstrated by a failing test.

- [x] Step 1: Implement `WaitingForMission` idle behavior and `BeginMission` only after a main-thread mission-ready signal.
- [x] Step 2: Emit one decision request only at configured event points, never every frame.
- [x] Step 3: Queue incoming pure pipe actions, validate episode and decision IDs plus current `ActionMask`, and invoke the game action sink only from `Tick()`.
- [x] Step 4: On missing/late policy response, synthesize `Hold` and continue without blocking.
- [x] Step 5: Apply typed reward events only for the active episode and terminal exactly once.
- [x] Step 6: Enter reset once, stop new actions, call the reset adapter, and accept a new episode only after reset success and first observation.
- [x] Step 7: Run all integration tests and commit `feat(brain): add runtime coordinator`.

### Task 3: Wire the coordinator to FcsModule main-thread lifecycle

**Files:**
- Modify: `IronNestFCS.Logic/FcsModule.cs` in Brain opt-in initialization, `Update()`, scene/mission detection, and shutdown paths.
- Modify: `IronNestFCS.Logic/Brain/MissionManagerResetAdapter.cs` only for adapter callbacks required by the coordinator.
- Create if needed: `IronNestFCS.Logic/Brain/BrainRuntimeLogger.cs`.

- [x] Step 1: Keep Brain disabled by default and initialize the coordinator only when the Brain runtime flag is enabled.
- [x] Step 2: Detect no active `FireMission` as `WaitingForMission`, without logging an error or issuing resets.
- [x] Step 3: On mission detection, bind/rebind map/FCS/probes and start an episode automatically.
- [x] Step 4: In `Update()`, drain transport actions, build pure observation/mask, submit accepted actions, poll stats, publish transitions, and tick reset—all on the main thread.
- [x] Step 5: Guard `Shutdown()` and planner mode changes so pipe threads stop and no stale callbacks reach the new episode.
- [x] Step 6: Build Logic with the actual IL2CPP references and commit `feat(brain): wire runtime into fcs loop`.

### Task 4: Make the Python client exercise the real runtime protocol

**Files:**
- Modify: `tools/brain_pipe_random_client.py`.
- Modify: `IronNestFCS.Brain.Tests/Program.cs` for protocol-level reconnect/timeout cases.

- [x] Step 1: Use scripted selection: legal `Engage` first, otherwise `Hold`.
- [x] Step 2: Print episode, decision, observation mask, selected action, acceptance, transition reward, terminal, and reconnect messages.
- [x] Step 3: Reconnect per request and stop cleanly on terminal; treat pipe close as Hold-safe rather than crashing the game.
- [x] Step 4: Run the fake server/client and commit `test(brain): exercise runtime pipe client`.

### Task 5: Audit, verify, and document the real-game boundary

**Files:**
- Modify: `BRAIN_PROGRESS.md`.
- Modify: `docs/superpowers/plans/2026-09-02-brain-runtime-wiring.md`.

- [x] Step 1: Run Brain tests, Logic build, and any fake coordinator integration suite using unique output paths.
- [x] Step 2: Record exact test counts and retain `UNVERIFIED IN REAL MISSION` for reset/episode throughput until a human enters a mission.
- [x] Step 3: Confirm ordinary FCS remains unchanged when Brain flags are absent.
- [x] Step 4: Update the “first real test” instructions to: create marker files, start game, enter one fixed mission, run Python client.
- [x] Step 5: Commit `docs(brain): document runtime bring-up status`.

## Bring-up hardening addendum

The following checks are the required pre-contact closeout. They are deliberately
limited to lifecycle safety, observability, and the existing position-only
actuator path.

### Task 6: Harden generation, delayed-event ownership, and reset single-flight

**Files:** `BrainRuntimeCoordinator.cs`, `BrainResetHarness.cs`, `Program.cs`.

- [x] Add failing tests for episode-mismatched actions, delayed impact after reset, duplicate terminal callbacks, and reset request while already resetting.
- [x] Keep event ownership as `episode_id → mission_id → decision_id`; reject old generations before ledger, knowledge, or decision-point work.
- [x] Make terminal/reset idempotent and expose reset audit milestones to the runtime logger.

### Task 7: Audit scene subscriptions, shutdown, and position-only mission execution

**Files:** `FcsModule.cs`, `FcsBrainRuntimeHost.cs`, `PlayerParityActionBridge.cs`, `FSC.cs`, `Mission.cs`, `Program.cs`.

- [x] Add a test or explicit invariant that each scene-bound probe is disposed before reinstallation and that shutdown is safe when called twice.
- [x] Verify `EntityId` remains empty from `Engage(track_id)` through `MissionRound`, scheduler, moving-target lead, completion, and attribution; no hidden lookup may be introduced.
- [x] Keep PlayerParity DTOs free of internal entity IDs, health, hidden positions, and native target references.

### Task 8: Add deterministic in-process bring-up policy and staged diagnostics

**Files:** `BrainRuntimeCoordinator.cs`, `FcsModule.cs`, create `InProcessScriptedPolicy.cs`, `Program.cs`, `BRAIN_PROGRESS.md`.

- [x] Add a debug-only policy with the same observation/mask/action boundary as the pipe client.
- [x] Add stage milestones 01–14 and a read-only state dump hotkey; no policy or game state changes are allowed by the dump.
- [x] Re-run the complete suite and record that real-game stages remain `UNVERIFIED IN REAL MISSION`.

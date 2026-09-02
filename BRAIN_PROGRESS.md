# IronNestFCS Brain progress

Updated 2026-09-02. This is the first implementation slice; no PPO or ML runtime has been started.

## Completed in this slice

- Added a Unity/IL2CPP-free `PlayerParityKnowledge` model with provenance checks.
- Added text-only telegram parsing for visible grid/time/course/speed, explicit distance (including the future `5KM`/`5千米` intercept line), route waypoint/bearing facts, and future intercept times. The parser also handles the extracted Chinese motion phrasing.
- Added constant-course extrapolation with age-based uncertainty. It uses only the last legal report; it does not refresh from `FireMission.Entities` or `TacticalRadar`.
- Added future-time projection (`TryEstimateAt`) so a policy can predict at fire-time + shell-flight-time, plus semantic `BrainAction`, strict `ActionMask`, `BrainObservation`, `BrainTransition`, and `BrainFacade` contracts.
- Added `PlayerParityBrainSession`, which ingests visible telegram text, refreshes observations, and clears knowledge on episode reset.
- Added an auditable transition history and explicit clock-domain handling: report `T:10:00:00` is not subtracted directly from Unity/episode seconds until a runtime adapter normalizes it.
- Added a route-only parser guard: `TrainStation`/`WPA`/`WPB`/… are treated as map context, not silently as the moving train. If no moving designation is printed, the route is retained under an auditable `route:<sourceId>` identity.
- Added an opt-in main-thread `Teleprinter` probe in Logic. Set `IRONNEST_BRAIN_TELEGRAM=1` to capture a `PrintJob` at queue completion (not at submission, so HumanTiming is preserved). The probe resolves the compiler-generated `<RunQueue>` state-machine type by name rather than a build-specific numeric suffix; the shipped game logged a successful hook install.
- Added `PlayerParityMapGridAdapter`: a printed grid is converted through `GridCodeConverter` and public `FireMission` map geometry only. It never enumerates `FireMission.Entities` and can produce either parity `MapPoint` coordinates or a world-space point for the future actuator.
- Added explicit action-mask refresh: a track with course/speed but no legal map position remains visible but cannot be selected by `Engage`; a track becomes engageable only after a legal map position is attached.

## Verification

- Pure Brain harness: **21/21 tests passed**.
- Logic build with an output override: **0 errors, 21 existing warnings**.
- Runtime smoke check: game loaded the deployed Logic/Abstractions assemblies and logged `[Brain] player-parity telegram capture enabled (job-complete timing)` with no `TypeLoadException`. The old hard-coded `<RunQueue>d__91` assumption was replaced after the live runtime exposed `<RunQueue>d__98`.
- The test project is now included in `IronNestFCS.sln`.

## Not yet measured / not yet implemented

- No complete Unity runtime episode has been run; the current smoke checks stopped at the main menu/scene-bind boundary.
- The Teleprinter hook has been runtime-loaded, but no completed mission print job has been captured yet because no mission interaction was driven during the unattended check. This remains the next evidence gap.
- The public-geometry grid adapter is wired for telegram processing, but a player-triggered map-reveal/scout event has not yet been exercised in a mission.
- No Brain action has been connected to `FSC.EnqueueMission`; existing `BaselinePlanner`/`CbtPlanner` behavior is unchanged.
- No native reward/event tracker, terminal detector, mission reset harness, IPC, 4x/8x benchmark, or episodes/hour metric exists yet.

## Current blockers and next experiments

1. Drive one real mission (manually or via a narrowly scoped UI harness) and capture a complete, player-visible Teleprinter message at PrintJob completion. Do not pass `State_TeleprinterText` entity replacements or `FireMission.Entities` into the formal Brain.
2. Exercise the legal map marker/reveal path. `MapMarkerPlacer.OnMarkerFinalized` and `MapMarkerLineUI` are candidate public events; verify their coordinates against the same public map geometry before using them for `Engage(trackId)`.
3. Add an event-driven Logic adapter and a no-op/scripted policy first. Only after one real mission produces audited transitions should reset and 4x/8x unattended runs be attempted.

The environment is **not ready for formal RL training** yet. The information boundary and pure transition contract are now testable; the next milestone is a real-game, player-parity observation → high-level action → FCS → reward → reset loop.

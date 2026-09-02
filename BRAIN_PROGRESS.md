# IronNestFCS Brain progress

Updated 2026-09-02. This is the first implementation slice; no PPO or ML runtime has been started.

## Completed in this slice

- Added a Unity/IL2CPP-free `PlayerParityKnowledge` model with provenance checks.
- Added text-only telegram parsing for visible grid/time/course/speed, route waypoint/bearing facts, and explicit future intercept times. The parser also handles the extracted Chinese motion phrasing.
- Added constant-course extrapolation with age-based uncertainty. It uses only the last legal report; it does not refresh from `FireMission.Entities` or `TacticalRadar`.
- Added future-time projection (`TryEstimateAt`) so a policy can predict at fire-time + shell-flight-time, plus semantic `BrainAction`, strict `ActionMask`, `BrainObservation`, `BrainTransition`, and `BrainFacade` contracts.
- Added `PlayerParityBrainSession`, which ingests visible telegram text, refreshes observations, and clears knowledge on episode reset.
- Added an auditable transition history and explicit clock-domain handling: report `T:10:00:00` is not subtracted directly from Unity/episode seconds until a runtime adapter normalizes it.
- Added a route-only parser guard: `TrainStation`/`WPA`/`WPB`/… are treated as map context, not silently as the moving train. If no moving designation is printed, the route is retained under an auditable `route:<sourceId>` identity.

## Verification

- Pure Brain harness: **19/19 tests passed**.
- Full solution build with an output override (so the build did not copy into the Steam game directory): **0 errors, 22 existing warnings**.
- The test project is now included in `IronNestFCS.sln`.

## Not yet measured / not yet implemented

- No Unity runtime episode has been run.
- No Teleprinter hook captures the game’s final rendered text yet; the current adapter is ready for that boundary but still requires a main-thread Logic integration.
- No legal map-reveal/scout adapter has been wired, so a telegram with only a grid does not yet become a world-coordinate firing target.
- No Brain action has been connected to `FSC.EnqueueMission`; existing `BaselinePlanner`/`CbtPlanner` behavior is unchanged.
- No native reward/event tracker, terminal detector, mission reset harness, IPC, 4x/8x benchmark, or episodes/hour metric exists yet.

## Current blockers and next experiments

1. Capture a complete, player-visible Teleprinter message at the point the print job is completed, preserving its source id and game-clock timestamp. Do not pass `State_TeleprinterText` entity replacements or `FireMission.Entities` into the formal Brain.
2. Identify the legal map marker/reveal API that supplies a player-visible target position, then map that designation to the high-level `Engage(trackId)` action without exposing radar internals.
3. Add an event-driven Logic adapter and a no-op/scripted policy first. Only after one real mission produces audited transitions should reset and 4x/8x unattended runs be attempted.

The environment is **not ready for formal RL training** yet. The information boundary and pure transition contract are now testable; the next milestone is a real-game, player-parity observation → high-level action → FCS → reward → reset loop.

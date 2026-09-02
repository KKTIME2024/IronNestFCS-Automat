# IronNest Brain Environment Foundation Continuation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 在无需人工操作游戏的前提下，把 Brain 的玩家信息边界、情报生命周期、策略接口、事件归因和 episode 契约推进到可离线验证、可接入 IPC 的状态。

**Architecture:** `IronNestFCS.Abstractions` 只包含纯 .NET DTO 和确定性状态机；所有游戏/IL2CPP 适配仍留在 `IronNestFCS.Logic`，并只能把玩家可见证据转换成 DTO。测试使用现有无依赖 console harness，先验证不泄漏和生命周期，再添加脚本策略与本地主机传输；不启动 PPO、PyTorch 或大型模拟器。

**Tech Stack:** C# / .NET 6, existing console test harness, JSONL audit records, localhost named-pipe transport only after the pure contract is stable.

**Spec:** 用户 2026-09-02 继续推进 IronNest Brain 的阶段性要求（当前对话附件 `7017d468-b5a1-412d-beac-57011d6b47bb/pasted-text.txt`）。

## Global Constraints

- 正式 PlayerParity observation 不得读取 `FireMission.Entities`、`TacticalRadar`、`MapEntity.Position/Health/State`、Enemy GameObject 或 FSC 隐藏 TargetInfo。
- 隐藏实体的移动、受伤和状态变化，在没有新的合法情报时不得改变 Brain observation。
- 电报 parser 只消费最终可见文本；无法安全解释的语义必须保留 raw text 并标记 unsupported，不得猜测。
- Track association 只能使用玩家可获得的公开名称、任务标识或显式来源 ID，不得使用内部 entity ID。
- Brain 动作只允许高层语义；炮塔、仰角、装填、弹道和扳机继续由 FSC 执行。
- 所有 Unity/IL2CPP 访问留在游戏主线程；IPC 线程只处理纯 DTO；Brain 默认关闭。
- 每项改动先写失败测试，看到正确失败后再写最小实现；每个独立切片单独构建并提交。

### Task 1: Enforce player-parity observation isolation

**Files:**
- Create: `IronNestFCS.Abstractions/Brain/PlayerParityIsolation.cs`
- Modify: `IronNestFCS.Abstractions/Brain/PlayerParityKnowledge.cs`
- Modify: `IronNestFCS.Brain.Tests/Program.cs`

**Interfaces:**
- Consumes: `TrackIntel` and visible semantic events only.
- Produces: a deterministic projection/fingerprint of PlayerParity observation and a testable guarantee that hidden debug facts cannot affect it.

- [ ] **Step 1: Write a failing test** that builds two knowledge stores with identical visible telegram facts, mutates a separate hidden-entity fixture (position, health, velocity, state), and asserts equal observation fingerprints; also assert an `OmniscientDebug` update is rejected.
- [ ] **Step 2: Run the harness** and verify the new test fails because no isolation projection exists.
- [ ] **Step 3: Implement** a projection that hashes only sorted `TrackSnapshot` player-visible fields and action-mask IDs; do not accept an entity collection or object reference anywhere in the API.
- [ ] **Step 4: Run all Brain tests** and verify the invariant passes.
- [ ] **Step 5: Commit** with `test(brain): enforce player-parity information isolation`.

### Task 2: Add a corpus-driven telegram coverage harness

**Files:**
- Create: `IronNestFCS.Brain.Tests/TelegramCorpus.cs`
- Create: `IronNestFCS.Brain.Tests/TelegramCorpusReport.cs`
- Modify: `IronNestFCS.Brain.Tests/Program.cs`
- Modify: `BRAIN_PROGRESS.md`

**Interfaces:**
- Consumes: checked-in visible telegram fixtures and expected semantic categories.
- Produces: deterministic scanned/semantic/fully-parsed/partial/unsupported counts and regression tests for course, speed, distance, timers, route, damage, scouting, urgency, and unresolved templates.

- [ ] **Step 1: Add failing corpus cases** for every semantic class already identified in extracted assets, including unsupported destruction/scouting/urgency wording.
- [ ] **Step 2: Run the focused corpus test** and verify unsupported cases are currently reported instead of silently parsed.
- [ ] **Step 3: Implement** a fixture table and coverage calculator; extend parser only for unambiguous text forms, and expose `UnsupportedSemantic` with original text for the rest.
- [ ] **Step 4: Run the corpus suite** and print exact coverage numbers.
- [ ] **Step 5: Commit** with `feat(brain): expand telegram semantic corpus`.

### Task 3: Make Track knowledge an explicit lifecycle state machine

**Files:**
- Modify: `IronNestFCS.Abstractions/Brain/BrainIntelModels.cs`
- Modify: `IronNestFCS.Abstractions/Brain/PlayerParityKnowledge.cs`
- Modify: `IronNestFCS.Brain.Tests/Program.cs`

**Interfaces:**
- Consumes: partial and conflicting `TrackIntel` reports.
- Produces: `TrackLifecycleState`, stale/lost/destroyed-confirmed snapshots, deterministic association by public track ID, and a reset-safe knowledge store.

- [ ] **Step 1: Write failing lifecycle tests** for no-position reports, position completion, same-ID merge, conflicting newer report, stale/lost thresholds, legal destroyed confirmation, re-observation, and reset.
- [ ] **Step 2: Run tests** and verify the missing lifecycle members fail.
- [ ] **Step 3: Implement** explicit state transitions based only on report time and legal evidence; preserve conflict metadata instead of overwriting silently; never infer destruction from hidden entity disappearance.
- [ ] **Step 4: Run all tests** and verify transition behavior.
- [ ] **Step 5: Commit** with `feat(brain): add track lifecycle state machine`.

### Task 4: Stabilize the Brain-facing pure contract and policies

**Files:**
- Modify: `IronNestFCS.Abstractions/Brain/BrainControlModels.cs`
- Create: `IronNestFCS.Abstractions/Brain/BrainPolicies.cs`
- Modify: `IronNestFCS.Abstractions/Brain/BrainFacade.cs`
- Modify: `IronNestFCS.Brain.Tests/Program.cs`

**Interfaces:**
- Consumes: `BrainObservation`, `ActionMask`, and deterministic RNG injection.
- Produces: `ValidActionSet` alias/projection, `BrainEvent` drain, `EpisodeState`, `RandomLegalPolicy`, and `ScriptedBaselinePolicy` with no Unity types.

- [ ] **Step 1: Write failing tests** for deterministic scripted priority, random-only-legal selection, event draining, and terminal decision rejection.
- [ ] **Step 2: Run tests** and verify the policy/event API is absent.
- [ ] **Step 3: Implement** minimal policies and event queue; scripted policy prefers fresh located tracks, then lower uncertainty/distance, otherwise Continue/Hold; random policy samples only the mask.
- [ ] **Step 4: Run all tests** and verify repeatability with a seeded RNG.
- [ ] **Step 5: Commit** with `feat(brain): add scripted and random legal policies`.

### Task 5: Add pure event attribution and episode lifecycle

**Files:**
- Create: `IronNestFCS.Abstractions/Brain/BrainEvents.cs`
- Create: `IronNestFCS.Abstractions/Brain/BrainEpisode.cs`
- Modify: `IronNestFCS.Abstractions/Brain/BrainFacade.cs`
- Modify: `IronNestFCS.Brain.Tests/Program.cs`

**Interfaces:**
- Consumes: decision/mission/shot/impact/damage/terminal events with decision, mission, and round IDs.
- Produces: delayed reward attribution, componentized transitions, and `Inactive → Starting → WaitingForInitialObservation → Running → Terminal → Resetting` lifecycle.

- [ ] **Step 1: Write failing tests** for delayed impact attribution, one-time terminal, reset clearing pending decisions/rewards/tracks, and stale callback rejection by episode ID.
- [ ] **Step 2: Run tests** and verify lifecycle/event types are missing.
- [ ] **Step 3: Implement** pure attribution ledger and episode state machine; keep reward components separate and reject callbacks from an old episode.
- [ ] **Step 4: Run complete tests** and verify exact totals and terminal semantics.
- [ ] **Step 5: Commit** with `feat(brain): add decision-to-impact attribution and episode lifecycle`.

### Task 6: Update progress and perform unattended-safe verification

**Files:**
- Modify: `BRAIN_PROGRESS.md`
- Modify: `docs/superpowers/plans/2026-09-02-brain-continuation.md`

- [ ] **Step 1:** Run the pure Brain harness and Logic build without launching a mission or requiring input.
- [ ] **Step 2:** If the installed game is safely launchable, perform only opt-in marker smoke loading; do not simulate UI clicks and do not claim a real episode.
- [ ] **Step 3:** Record unit/build/game-load/real-mission status separately, exact telegram coverage, fairness invariant, environment matrix, blockers, and the next 1–3 highest-value steps.
- [ ] **Step 4:** Commit the progress update with `docs(brain): record foundation continuation status`.

## Self-review checklist

- The plan covers P0 isolation, P1 corpus, P2 lifecycle, P3 contract, P4 policies, P5 attribution, P6 episode state, and unattended verification; IPC/reset harness remain explicitly later work because they depend on the pure contract.
- No task accepts hidden GameObjects or invents semantics from internal entities.
- Every implementation task has a concrete failing test, a focused run, and a commit boundary.

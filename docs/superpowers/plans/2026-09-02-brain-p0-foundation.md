# IronNest Brain P0 Foundation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 建立不读取隐藏实体真值的 PlayerParity Brain 基础契约，并用纯逻辑测试证明合法电报、移动外推和高层动作边界。

**Architecture:** Brain 契约放在 `IronNestFCS.Abstractions`，不引用 Unity、IL2CPP 或 FCS 具体类型，因此可跨热重载边界和在无游戏进程的测试中使用。电报解析只消费最终可见文本，生成带来源证明的 semantic event；`PlayerParityKnowledge` 只接受 Telegram/地图揭示/合法侦察来源，拒绝 `OmniscientDebug`。本切片提供高层 action/observation/transition 数据类型和一个纯状态 facade，不接 PPO、不改现有 TacticalRadar、不触碰炮塔执行器。

**Tech Stack:** C# / .NET 6, existing `IronNestFCS.Abstractions` project, a dependency-free .NET 6 console test harness.

**Spec:** 用户 2026-09-02 粘贴的 Brain 实验说明（当前对话附件 `pasted-text.txt`）。

## Global Constraints

- 正式 Brain 不允许把 `FireMission.Entities`、隐藏 `Position`、`Health` 或实时 `velocity` 作为 observation。
- `OmniscientDebug` 只能作为显式调试来源，不能进入 PlayerParity knowledge。
- 电报 semantic event 的信息量不得超过最终打印文本。
- 移动目标在失去合法侦察后只能从最后合法报告外推，真实 GameObject 移动不得影响 track。
- Brain 只产生 `Hold / Engage / ContinueCurrentMission / AbortIfSafe` 等语义级动作，不处理角度、装填、弹道或扳机。
- 现有 FCS 普通使用方式必须保持不变；本切片不修改 `TacticalRadar` 或 `FSC`。

---

### Task 1: Add pure Brain knowledge and semantic model

**Files:**
- Create: `IronNestFCS.Abstractions/Brain/BrainIntelModels.cs`
- Create: `IronNestFCS.Abstractions/Brain/PlayerParityKnowledge.cs`

**Interfaces:**
- Consumes: immutable semantic events carrying `IntelProvenance`.
- Produces: `MapPoint`, `IntelProvenance`, `TrackIntel`, `TelegramSemanticEvent`, `TrackSnapshot`, `PlayerParityKnowledge`.

- [x] **Step 1: Write failing tests for source provenance and hidden-information rejection**

Create a test that applies a Telegram event successfully, applies an `OmniscientDebug` event unsuccessfully, and asserts that no hidden health/position field can be supplied through the player-parity API.

- [x] **Step 2: Run the test and verify the expected compile failure**

Run: `dotnet run --project IronNestFCS.Brain.Tests/IronNestFCS.Brain.Tests.csproj`

Expected: FAIL because the Brain model types do not exist.

- [x] **Step 3: Implement the smallest immutable model and knowledge store**

Use explicit optional fields for reported grid, reported position, course, speed, route, and known destruction. Every update carries `IntelProvenance`; `PlayerParityKnowledge` rejects `OmniscientDebug` and exposes snapshots without internal game-object references.

- [x] **Step 4: Run the focused test and verify it passes**

Run: `dotnet run --project IronNestFCS.Brain.Tests/IronNestFCS.Brain.Tests.csproj`

Expected: PASS for provenance and rejection tests.

- [x] **Step 5: Commit the model**

```bash
git add IronNestFCS.Abstractions/Brain/BrainIntelModels.cs IronNestFCS.Abstractions/Brain/PlayerParityKnowledge.cs IronNestFCS.Brain.Tests
git commit -m "feat(brain): add player-parity knowledge model"
```

### Task 2: Parse visible telegram motion and route semantics

**Files:**
- Create: `IronNestFCS.Abstractions/Brain/TelegramSemanticParser.cs`
- Modify: `IronNestFCS.Brain.Tests/Program.cs`

**Interfaces:**
- Consumes: final visible telegram text plus receive time and source label.
- Produces: `TelegramSemanticEvent` containing only explicit position/grid, report time, course, speed, route waypoint, and arrival facts.

- [x] **Step 1: Add failing parser tests**

Cover the extracted vessel text (`12°`, `9.7 knots`, report time), the High Tide text (`180°`, `19.4 knots`, `T+500` future timing), and an Iron Road waypoint line. Add a negative test proving that a sentence with no explicit speed/course yields no motion fact.

- [x] **Step 2: Run the parser tests and verify they fail**

Run: `dotnet run --project IronNestFCS.Brain.Tests/IronNestFCS.Brain.Tests.csproj`

Expected: FAIL because no parser exists.

- [x] **Step 3: Implement text-only parsing**

Use bounded regular expressions over visible text. Do not accept an entity object, entity ID dictionary, health, or live position provider. Keep literal course/speed and dynamic grid/time fields separate so provenance remains auditable.

- [x] **Step 4: Run all parser tests**

Run: `dotnet run --project IronNestFCS.Brain.Tests/IronNestFCS.Brain.Tests.csproj`

Expected: PASS with no warnings.

- [x] **Step 5: Commit the parser**

```bash
git add IronNestFCS.Abstractions/Brain/TelegramSemanticParser.cs IronNestFCS.Brain.Tests/Program.cs
git commit -m "feat(brain): parse visible telegram motion intel"
```

### Task 3: Add legal-track extrapolation and high-level Brain contract

**Files:**
- Create: `IronNestFCS.Abstractions/Brain/BrainControlModels.cs`
- Modify: `IronNestFCS.Abstractions/Brain/PlayerParityKnowledge.cs`
- Modify: `IronNestFCS.Brain.Tests/Program.cs`

**Interfaces:**
- Consumes: `TrackSnapshot` motion facts and game-clock time.
- Produces: `TrackEstimate`, `BrainObservation`, `BrainAction`, `ActionMask`, `BrainTransition`, `BrainFacade`.

- [x] **Step 1: Add failing extrapolation and action-mask tests**

Assert that a 180° target reported at `(0, 0)` with `0.01 km/s` estimates `(0, -5)` after 500 seconds, that uncertainty grows with report age, that stale tracks remain based on the report, and that hidden entities cannot appear merely by being present in a supplied debug collection. Assert that semantic actions expose no low-level actuator verbs.

- [x] **Step 2: Run the tests and verify failure**

Run: `dotnet run --project IronNestFCS.Brain.Tests/IronNestFCS.Brain.Tests.csproj`

Expected: FAIL because estimation/control types do not exist.

- [x] **Step 3: Implement extrapolation and facade contracts**

Implement deterministic constant-course extrapolation from legal report facts only. Add an explicit action mask and transition record; invalid actions are rejected before execution. Keep the facade runtime-neutral so a later Logic adapter can delegate `Engage` to existing planners without exposing FCS internals.

- [x] **Step 4: Run the complete pure-logic suite**

Run: `dotnet run --project IronNestFCS.Brain.Tests/IronNestFCS.Brain.Tests.csproj`

Expected: PASS.

- [x] **Step 5: Commit the control boundary**

```bash
git add IronNestFCS.Abstractions/Brain IronNestFCS.Brain.Tests
git commit -m "feat(brain): add high-level observation and action boundary"
```

### Task 4: Build and document the current integration boundary

**Files:**
- Create: `BRAIN_PROGRESS.md`
- Modify: `IronNestFCS.sln` only if the test project is added to the solution.

- [x] **Step 1: Run the Brain test harness and existing solution build**

Run: `dotnet run --project IronNestFCS.Brain.Tests/IronNestFCS.Brain.Tests.csproj` and `dotnet build IronNestFCS.sln --no-restore`.

- [x] **Step 2: Record exact results**

Record test count, build result, and blockers. Explicitly state that no Unity runtime episode/reset or PPO has been started in this slice.

- [x] **Step 3: Commit the progress report**

```bash
git add BRAIN_PROGRESS.md IronNestFCS.sln
git commit -m "docs(brain): record P0 experiment status"
```

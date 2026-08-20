# Autonomous Engagement Core Refactor Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace `master`'s automatic target-picking path with a pure, whole-fire-unit baseline planner that is behavior-equivalent under shadow comparison, while retaining the existing FSC as the mechanical executor and preserving an immediate legacy fallback.

**Architecture:** A new `IronNestFCS.Domain` assembly owns immutable observations, engagement plans, baseline selection, clustering, and contract validation without Unity or IL2CPP references. `IronNestFCS.Logic` captures one versioned runtime snapshot, runs the legacy path as authority while comparing fingerprints, then converts only supported independent fire groups back to existing `ArtilleryTask` objects through `LegacySchedulerAdapter`; the old picker is removed only after automated and game-runtime acceptance.

**Tech Stack:** C# / .NET 6 (`net6.0`), MelonLoader cooperative coroutines, Unity IL2CPP adapters, dependency-free console test runner.

**Spec:** `docs/superpowers/specs/2026-08-19-autonomy-core-refactor-design.md`

## Global Constraints

- Code baseline is exactly `master@87405a4`; `dev`, historical CBT, and dirty `refactor/planner` are reference-only and must not be merged or cherry-picked wholesale.
- Milestone 1 preserves `master` firing behavior; only separately identified deadlock, misfire, lock-leak, hot-reload, or invalid-observation safety fixes may differ.
- Do not implement a new scheduler, split FSC's mechanical routine, enable CBT, add multi-round execution, add new cluster policy, automate ejection, rewrite `CleanState`, or add kill attribution.
- All Unity/IL2CPP access remains on the MelonCoroutine main thread; do not introduce `async`, `Task`, or `Task.Delay`.
- Normal builds write only below the repository's `artifacts/` and `Release/` directories. Copying into the game directory requires a separate explicit deployment command after user approval.
- Treat a failed entity scan as `ObservationInvalid`; never reconcile an empty alive set or infer that all targets died from a failed read.
- The first milestone executes only one-shot `Independent` fire groups. `SameTrigger` and inter-group timing relations are modeled and validated but are not adapted to FSC.
- Player T1–T4 tasks remain strict FIFO and are never rewritten by the planner. A non-empty pending manual queue suppresses creation of a new automatic plan.
- Run pure tests after every domain change and build the full solution after every Logic integration change.
- Runtime evidence comes from `F:\SteamLibrary\steamapps\common\Iron Nest Heavy Turret Simulator\MelonLoader\Logs` and must record build commit, scenario, start time, actions, and conclusion.

---

## File Map

### New pure domain assembly

- `IronNestFCS.Domain/IronNestFCS.Domain.csproj` — dependency-free `net6.0` library.
- `IronNestFCS.Domain/Geometry/WorldPoint.cs` — value type and vector operations in game world units.
- `IronNestFCS.Domain/Geometry/MapGeometry.cs` — captured world-to-map affine transform and pure bearing/distance calculation.
- `IronNestFCS.Domain/Observation/BattleSnapshot.cs` — immutable targets, barrels, reservations, manual queue, and observation validity.
- `IronNestFCS.Domain/Planning/EngagementPlan.cs` — `ShotIntent`, `FireGroup`, trigger semantics, relations, and no-plan reasons.
- `IronNestFCS.Domain/Planning/ShellRules.cs` — baseline shell choice, costs, blast radii, and friendly-safe radii.
- `IronNestFCS.Domain/Planning/ClusterSolver.cs` — current MEC cluster algorithm ported without Unity.
- `IronNestFCS.Domain/Planning/BaselinePlanner.cs` — exact non-CBT `master` selection for the whole two-barrel unit.
- `IronNestFCS.Domain/Planning/PlanContractValidator.cs` — version, group atomicity, and supported-mode validation.
- `IronNestFCS.Domain/Execution/FireGateAuthorization.cs` — pure authorization contract for single-barrel versus `SameTrigger` arming.
- `IronNestFCS.Domain/Diagnostics/PlanFingerprint.cs` — stable, locale-independent comparison representation.

### New dependency-free test executable

- `IronNestFCS.Domain.Tests/IronNestFCS.Domain.Tests.csproj` — console project referencing only the domain assembly.
- `IronNestFCS.Domain.Tests/AssertEx.cs` — minimal equality, sequence, null, and exception assertions.
- `IronNestFCS.Domain.Tests/SnapshotBuilder.cs` — named scenario construction with explicit defaults.
- `IronNestFCS.Domain.Tests/Program.cs` — enumerates named tests and returns non-zero on any failure.
- `IronNestFCS.Domain.Tests/BattleSnapshotTests.cs` — validity and dirty-barrel representation tests.
- `IronNestFCS.Domain.Tests/PlanContractTests.cs` — group atomicity and fire-gate authorization tests.
- `IronNestFCS.Domain.Tests/BaselinePlannerTests.cs` — target order, shell, reservation, manual FIFO, and stale-input cases.
- `IronNestFCS.Domain.Tests/ClusterPlannerTests.cs` — current HE/HCHE cluster parity and friendly exclusion.

### Logic integration

- `IronNestFCS.Logic/Planning/ObservationAdapter.cs` — only entry point that turns current runtime state into a domain snapshot.
- `IronNestFCS.Logic/Planning/LegacyDecisionRecorder.cs` — records the authoritative old decision without changing it.
- `IronNestFCS.Logic/Planning/ShadowPlanComparer.cs` — compares old decisions with a pure plan and emits one structured result.
- `IronNestFCS.Logic/Planning/LegacySchedulerAdapter.cs` — validates and converts supported groups to `ArtilleryTask`.
- `IronNestFCS.Logic/Planning/PlannerRollout.cs` — legacy/new authority mode, shadow counters, and fallback reason.
- `IronNestFCS.Logic/TacticalRadar.cs` — make scan success explicit and preserve last trustworthy data on failure.
- `IronNestFCS.Logic/FCS/GunSystem.cs` — add read-only mechanical observation accessors only.
- `IronNestFCS.Logic/FSC.cs` — expose read-only queue/task information needed by observation and remove cluster creation only at the final cutover.
- `IronNestFCS.Logic/FcsModule.cs` — compose observation, legacy recorder, planner, comparer, adapter, and rollout switch.
- `IronNestFCS.Logic/FcsWindow.cs` — display planner authority and last shadow/fallback status.

### Build and evidence

- `IronNestFCS.Logic/IronNestFCS.Logic.csproj` — repository-local default output.
- `IronNestFCS.sln` — add domain and test projects.
- `global.json` — select a valid .NET 6 feature band.
- `.gitignore` — ignore repository-local build artifacts and runtime evidence.
- `build.ps1` — package only repository-local outputs.
- `deploy.ps1` — explicit, separately invoked deployment from `Release/` to the game.
- `docs/runtime-validation/autonomy-core-template.md` — reproducible runtime acceptance record.

---

### Task 1: Make Builds Non-Deploying and Establish the Test Runner

**Files:**
- Modify: `global.json`
- Modify: `.gitignore`
- Modify: `IronNestFCS.Logic/IronNestFCS.Logic.csproj`
- Modify: `IronNestFCS.sln`
- Modify: `build.ps1`
- Create: `deploy.ps1`
- Create: `IronNestFCS.Domain/IronNestFCS.Domain.csproj`
- Create: `IronNestFCS.Domain.Tests/IronNestFCS.Domain.Tests.csproj`
- Create: `IronNestFCS.Domain.Tests/AssertEx.cs`
- Create: `IronNestFCS.Domain.Tests/Program.cs`

**Interfaces:**
- Consumes: installed .NET SDK `6.0.428` and game reference assemblies under the `GameDir` MSBuild property.
- Produces: `dotnet run --project IronNestFCS.Domain.Tests` as the pure test command; `./build.ps1` as a non-deploying package command; `./deploy.ps1` as the only game-directory write path.

- [ ] **Step 1: Write a test-runner smoke test that currently cannot build**

Create `IronNestFCS.Domain.Tests/Program.cs` with a named test registry and exit code:

```csharp
using IronNestFCS.Domain.Tests;

var tests = new (string Name, Action Run)[]
{
    ("test runner reports a passing assertion", () => AssertEx.Equal(2, 1 + 1))
};

var failed = 0;
foreach (var test in tests)
{
    try { test.Run(); Console.WriteLine($"PASS {test.Name}"); }
    catch (Exception ex) { failed++; Console.Error.WriteLine($"FAIL {test.Name}: {ex.Message}"); }
}

Console.WriteLine($"RESULT total={tests.Length} failed={failed}");
return failed == 0 ? 0 : 1;
```

Create `AssertEx.cs` with concrete helpers used by later tasks:

```csharp
namespace IronNestFCS.Domain.Tests;

internal static class AssertEx
{
    public static void True(bool value, string? message = null)
    { if (!value) throw new InvalidOperationException(message ?? "Expected true."); }

    public static void False(bool value, string? message = null) => True(!value, message ?? "Expected false.");

    public static void Equal<T>(T expected, T actual)
    { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new InvalidOperationException($"Expected <{expected}> but got <{actual}>."); }

    public static void SequenceEqual<T>(IEnumerable<T> expected, IEnumerable<T> actual)
    { if (!expected.SequenceEqual(actual)) throw new InvalidOperationException($"Expected [{string.Join(",", expected)}] but got [{string.Join(",", actual)}]."); }

    public static T Throws<T>(Action action) where T : Exception
    { try { action(); } catch (T ex) { return ex; } throw new InvalidOperationException($"Expected {typeof(T).Name}."); }
}
```

- [ ] **Step 2: Run the test command and verify the missing project failure**

Run:

```powershell
dotnet run --project IronNestFCS.Domain.Tests
```

Expected: FAIL because `IronNestFCS.Domain.Tests.csproj` and the referenced domain assembly do not exist.

- [ ] **Step 3: Add dependency-free projects and valid SDK selection**

Use this project shape for the domain assembly:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net6.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
  </PropertyGroup>
</Project>
```

Use this project shape for tests:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net6.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\IronNestFCS.Domain\IronNestFCS.Domain.csproj" />
  </ItemGroup>
</Project>
```

Change `global.json` to a legal feature-band version available through roll-forward:

```json
{
  "sdk": {
    "version": "6.0.100",
    "rollForward": "latestFeature",
    "allowPrerelease": false
  }
}
```

Add both projects to `IronNestFCS.sln`. Add `/artifacts/` and `/docs/runtime-validation/runs/` to `.gitignore`.

- [ ] **Step 4: Redirect all ordinary Logic outputs into the repository**

Replace the hard-coded Logic `OutputPath` with:

```xml
<ArtifactsDir Condition="'$(ArtifactsDir)' == ''">$(MSBuildThisFileDirectory)..\artifacts</ArtifactsDir>
<OutputPath>$(ArtifactsDir)\bin\$(MSBuildProjectName)\$(Configuration)\</OutputPath>
```

Update `build.ps1` to pass `-p:ArtifactsDir=$repositoryRoot\artifacts`, and copy `IronNestFCS.Logic.dll` from `artifacts\bin\IronNestFCS.Logic\Release\IronNestFCS.Logic.dll`, never from `UserData`. Create `deploy.ps1` that requires an existing `Release\IronNestFCS` package, validates the exact game root, and copies these three explicit files only:

```text
Release/IronNestFCS/Mods/IronNestFCS.dll -> <GameDir>/Mods/IronNestFCS.dll
Release/IronNestFCS/UserData/IronNestFCS/IronNestFCS.Logic.dll -> <GameDir>/UserData/IronNestFCS/IronNestFCS.Logic.dll
Release/IronNestFCS/UserLibs/IronNestFCS.Abstractions.dll -> <GameDir>/UserLibs/IronNestFCS.Abstractions.dll
```

The deploy script must print source and destination paths before copying and must not run from `build.ps1`.

- [ ] **Step 5: Verify tests and package build without touching the game DLL**

Record the game Logic DLL timestamp, run both commands, then assert the timestamp is unchanged:

```powershell
$gameLogic = 'F:\SteamLibrary\steamapps\common\Iron Nest Heavy Turret Simulator\UserData\IronNestFCS\IronNestFCS.Logic.dll'
$before = (Get-Item -LiteralPath $gameLogic).LastWriteTimeUtc
dotnet run --project IronNestFCS.Domain.Tests
.\build.ps1
$after = (Get-Item -LiteralPath $gameLogic).LastWriteTimeUtc
if ($before -ne $after) { throw 'Ordinary build deployed into the game directory.' }
```

Expected: test runner reports `failed=0`, build succeeds, package exists below `Release/`, and `$before -eq $after`.

- [ ] **Step 6: Commit the safe foundation**

```powershell
git add global.json .gitignore IronNestFCS.sln build.ps1 deploy.ps1 IronNestFCS.Logic/IronNestFCS.Logic.csproj IronNestFCS.Domain IronNestFCS.Domain.Tests
git commit -m "build: isolate artifacts and add domain test runner"
```

---

### Task 2: Define Immutable Observation and Engagement Contracts

**Files:**
- Create: `IronNestFCS.Domain/Geometry/WorldPoint.cs`
- Create: `IronNestFCS.Domain/Geometry/MapGeometry.cs`
- Create: `IronNestFCS.Domain/Observation/BattleSnapshot.cs`
- Create: `IronNestFCS.Domain/Planning/EngagementPlan.cs`
- Create: `IronNestFCS.Domain.Tests/SnapshotBuilder.cs`
- Create: `IronNestFCS.Domain.Tests/BattleSnapshotTests.cs`
- Modify: `IronNestFCS.Domain.Tests/Program.cs`

**Interfaces:**
- Consumes: no runtime types.
- Produces: `BattleSnapshot`, `TargetSnapshot`, `BarrelSnapshot`, `EngagementPlan`, `FireGroup`, and `ShotIntent`, all immutable and free of Unity references.

- [ ] **Step 1: Write failing snapshot-contract tests**

Add named tests that prove invalid observations are distinct from valid empty battlefields and unknown barrel values stay unknown:

```csharp
internal static class BattleSnapshotTests
{
    public static void InvalidScanIsNotAValidEmptyBattlefield()
    {
        var snapshot = SnapshotBuilder.Invalid(version: 7, reason: "EntitiesUnavailable");
        AssertEx.Equal(ObservationStatus.Invalid, snapshot.Status);
        AssertEx.Equal("EntitiesUnavailable", snapshot.InvalidReason);
        AssertEx.Equal(0, snapshot.Targets.Count);
    }

    public static void UnknownChargeIsNotZero()
    {
        var barrel = SnapshotBuilder.Barrel(charges: null, state: BarrelMechanicalState.ShellLoaded);
        AssertEx.Equal<int?>(null, barrel.ChamberCharges);
        AssertEx.False(barrel.State == BarrelMechanicalState.Empty);
    }

    public static void SnapshotCopiesMutableInputs()
    {
        var targets = new List<TargetSnapshot> { SnapshotBuilder.Target("a") };
        var snapshot = SnapshotBuilder.Valid(targets: targets);
        targets.Clear();
        AssertEx.Equal(1, snapshot.Targets.Count);
    }

    public static void BarrelStatesRemainDistinct()
    {
        AssertEx.Equal(BarrelMechanicalState.Empty, SnapshotBuilder.Barrel(state: BarrelMechanicalState.Empty).State);
        AssertEx.Equal(BarrelMechanicalState.ShellLoaded, SnapshotBuilder.Barrel(state: BarrelMechanicalState.ShellLoaded, shell: ShellKind.HE).State);
        AssertEx.Equal(BarrelMechanicalState.Transitioning, SnapshotBuilder.Barrel(state: BarrelMechanicalState.Transitioning, shell: ShellKind.AP).State);
        AssertEx.Equal(BarrelMechanicalState.Committed, SnapshotBuilder.Barrel(state: BarrelMechanicalState.Committed, shell: ShellKind.DRIL, canFire: true).State);
        AssertEx.Equal(BarrelMechanicalState.Unknown, SnapshotBuilder.Barrel(state: BarrelMechanicalState.Unknown, charges: null, canFire: null).State);
    }

    public static void LoadedShellCompatibilityIsExplicit()
    {
        var barrel = SnapshotBuilder.Barrel(state: BarrelMechanicalState.ShellLoaded, shell: ShellKind.HE);
        AssertEx.True(barrel.HasCompatibleShell(ShellKind.HE));
        AssertEx.False(barrel.HasCompatibleShell(ShellKind.AP));
    }
}
```

Register every method explicitly in `Program.cs`; do not use reflection-based discovery.

- [ ] **Step 2: Run tests and verify type-not-found failures**

Run `dotnet run --project IronNestFCS.Domain.Tests`.

Expected: FAIL to compile because `BattleSnapshot`, `ObservationStatus`, and `BarrelMechanicalState` do not exist.

- [ ] **Step 3: Implement geometry and observation records**

Define `WorldPoint` with only deterministic float operations required by planning:

```csharp
public readonly record struct WorldPoint(float X, float Y, float Z)
{
    public static WorldPoint operator +(WorldPoint a, WorldPoint b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
    public static WorldPoint operator -(WorldPoint a, WorldPoint b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
    public static WorldPoint operator *(WorldPoint a, float k) => new(a.X * k, a.Y * k, a.Z * k);
    public float LengthSquared => X * X + Y * Y + Z * Z;
    public float Length => MathF.Sqrt(LengthSquared);
    public static float Distance(WorldPoint a, WorldPoint b) => (a - b).Length;
    public static float Dot(WorldPoint a, WorldPoint b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
    public static WorldPoint Cross(WorldPoint a, WorldPoint b) => new(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);
}
```

Define `MapAffineTransform` as twelve row-major floats (`M11..M14`, `M21..M24`, `M31..M34`) and implement point transformation without Unity:

```csharp
public readonly record struct MapAffineTransform(
    float M11, float M12, float M13, float M14,
    float M21, float M22, float M23, float M24,
    float M31, float M32, float M33, float M34)
{
    public WorldPoint TransformPoint(WorldPoint p) => new(
        M11 * p.X + M12 * p.Y + M13 * p.Z + M14,
        M21 * p.X + M22 * p.Y + M23 * p.Z + M24,
        M31 * p.X + M32 * p.Y + M33 * p.Z + M34);
}

public readonly record struct BearingDistance(float BearingDeg, float DistanceKm);

public sealed record MapGeometry(MapAffineTransform WorldToMap, WorldPoint TurretMapLocal)
{
    public BearingDistance Locate(WorldPoint world)
    {
        var d = WorldToMap.TransformPoint(world) - TurretMapLocal;
        var angle = MathF.Atan2(d.X, d.Y) * (180f / MathF.PI);
        if (angle < 0f) angle += 360f;
        return new(angle, d.Length * 3.8164f);
    }
}
```

Add identity, translation, north/east/west bearing, and distance tests. Define exact observation enums and records:

```csharp
public enum ObservationStatus { Valid, Invalid }
public enum BarrelId { Left, Right }
public enum BarrelMechanicalState { Empty, ShellLoaded, Transitioning, Committed, Recovering, Unknown }
public enum ShellKind { AP, DRIL, LE, HE, HCHE, STAR }

public sealed record BarrelSnapshot(
    BarrelId Id, long StateVersion, BarrelMechanicalState State,
    ShellKind? ChamberShell, int? ChamberCharges, bool? CanFire,
    bool? PendingReload, bool? BreechLocked, bool? SafetyReleased,
    bool HasActiveTask, string? ActiveLeaseId)
{
    public bool HasCompatibleShell(ShellKind required) => ChamberShell == required;
}

public sealed record TargetSnapshot(
    string EntityId, string Name, WorldPoint Position, float BearingDeg,
    float DistanceKm, int Priority, int Stars, bool IsArmored,
    bool IsUnderground, bool IsFdc, bool IsMoving, bool VelocityKnown,
    WorldPoint VelocityWorldPerSecond, IReadOnlyList<ShellKind> ImmuneShells,
    bool IsEntityReserved, bool IsPositionReserved);

public sealed record BattleSnapshot(
    long Version, float GameTime, ObservationStatus Status, string? InvalidReason,
    float TurretBearingDeg, MapGeometry Geometry,
    BarrelSnapshot LeftBarrel, BarrelSnapshot RightBarrel,
    IReadOnlyList<TargetSnapshot> Targets, IReadOnlyList<WorldPoint> Friendlies,
    int PendingManualTaskCount, bool CbtEnabled)
{
    public bool IsValid => Status == ObservationStatus.Valid;
}
```

For every collection-bearing record, replace the positional-constructor shorthand with an explicit constructor that copies inputs into `Array.AsReadOnly(input.ToArray())`; expose only those read-only wrappers so callers cannot mutate a completed snapshot or plan.

- [ ] **Step 4: Implement plan records without execution behavior**

```csharp
public enum TriggerMode { Independent, SameTrigger }
public enum GroupRelationKind { None, InOrder, ImpactInOrder, AlignImpact }
public enum PlanSource { Autonomous, Manual }
public enum ChargePolicy { Minimum, Maximum }
public enum NoPlanReason { None, ObservationInvalid, ManualQueuePending, NoFreeBarrel, NoEligibleTarget }

public sealed record ShotIntent(
    string ShotId, string TargetEntityId, string DisplayName,
    WorldPoint ImpactPoint, float BearingDeg, float DistanceKm,
    ShellKind Shell, ChargePolicy ChargePolicy, bool IsMoving,
    bool VelocityUnknown, WorldPoint AimOrigin, WorldPoint AimVelocity,
    float AimStartTime, float BlastRadiusKm, IReadOnlyList<string> ClusterMembers,
    string TrackEntityId, WorldPoint ClusterOffset);

public sealed record FireGroup(
    string GroupId, TriggerMode TriggerMode, IReadOnlyList<ShotIntent> Shots,
    bool AllowIndependentFallback);

public sealed record GroupRelation(string BeforeGroupId, string AfterGroupId, GroupRelationKind Kind);

public sealed record EngagementPlan(
    long SnapshotVersion, PlanSource Source, IReadOnlyList<FireGroup> Groups,
    IReadOnlyList<GroupRelation> Relations, NoPlanReason NoPlanReason)
{
    public bool HasFire => Groups.Count != 0;
}
```

Copy plan collections at construction just like snapshot collections. Do not add `Consecutive` or `SameBarrel`.

- [ ] **Step 5: Run tests and commit contracts**

Run `dotnet run --project IronNestFCS.Domain.Tests`.

Expected: all snapshot tests pass and the test runner reports `failed=0`.

```powershell
git add IronNestFCS.Domain IronNestFCS.Domain.Tests
git commit -m "feat: define immutable battle and engagement contracts"
```

---

### Task 3: Enforce Group and Fire-Gate Safety Contracts

**Files:**
- Create: `IronNestFCS.Domain/Planning/PlanContractValidator.cs`
- Create: `IronNestFCS.Domain/Execution/FireGateAuthorization.cs`
- Create: `IronNestFCS.Domain.Tests/PlanContractTests.cs`
- Modify: `IronNestFCS.Domain.Tests/Program.cs`

**Interfaces:**
- Consumes: `EngagementPlan`, current snapshot version, requested barrel arming set.
- Produces: `PlanValidationResult Validate(EngagementPlan plan, long currentVersion, bool legacyAdapter)` and `bool MayReleaseSafety(BarrelId barrel)`.

- [ ] **Step 1: Write failing atomicity and safety tests**

```csharp
internal static class PlanContractTests
{
    public static void StalePlanIsRejectedBeforeAnyGroup()
    {
        var plan = SnapshotBuilder.Plan(version: 4, SnapshotBuilder.IndependentShot("a"));
        var result = PlanContractValidator.Validate(plan, currentVersion: 5, legacyAdapter: true);
        AssertEx.False(result.Accepted);
        AssertEx.Equal(PlanRejection.StaleSnapshot, result.Rejection);
        AssertEx.Equal(0, result.AcceptedGroupIds.Count);
    }

    public static void SameTriggerIsAtomicAndUnsupportedByLegacyAdapter()
    {
        var group = SnapshotBuilder.SameTriggerGroup("a", "b");
        var result = PlanContractValidator.Validate(SnapshotBuilder.Plan(5, group), 5, legacyAdapter: true);
        AssertEx.False(result.Accepted);
        AssertEx.Equal(PlanRejection.UnsupportedTriggerMode, result.Rejection);
        AssertEx.Equal(0, result.AcceptedGroupIds.Count);
    }

    public static void SingleShotGateAuthorizesOnlyAssignedBarrel()
    {
        var gate = FireGateAuthorization.Independent(BarrelId.Left);
        AssertEx.True(gate.MayReleaseSafety(BarrelId.Left));
        AssertEx.False(gate.MayReleaseSafety(BarrelId.Right));
    }

    public static void SameTriggerGateAuthorizesBothBarrels()
    {
        var gate = FireGateAuthorization.SameTrigger(BarrelId.Left, BarrelId.Right);
        AssertEx.True(gate.MayReleaseSafety(BarrelId.Left));
        AssertEx.True(gate.MayReleaseSafety(BarrelId.Right));
    }
}
```

- [ ] **Step 2: Run tests and verify missing validator failures**

Run `dotnet run --project IronNestFCS.Domain.Tests`.

Expected: FAIL to compile because contract validator and gate authorization do not exist.

- [ ] **Step 3: Implement fail-closed validation**

Define:

```csharp
public enum PlanRejection { None, StaleSnapshot, EmptyGroup, DuplicateGroupId, DuplicateShotId, UnsupportedTriggerMode, InvalidIndependentGroup, InvalidSameTriggerGroup, InvalidRelation }
public sealed record PlanValidationResult(bool Accepted, PlanRejection Rejection, IReadOnlyList<string> AcceptedGroupIds);
```

Validation order must be deterministic:

1. reject `SnapshotVersion != currentVersion` with no accepted groups;
2. reject duplicate group or shot IDs;
3. reject empty groups;
4. require exactly one shot for `Independent` and exactly two shots for `SameTrigger`;
5. when `legacyAdapter == true`, reject every non-`Independent` group and every non-`None` relation;
6. require every relation endpoint to name an existing group and disallow self-relations;
7. return all group IDs only after every group and relation passes, so validation is all-or-nothing.

- [ ] **Step 4: Implement immutable fire-gate authorization**

```csharp
public sealed class FireGateAuthorization
{
    private readonly HashSet<BarrelId> _authorized;
    private FireGateAuthorization(IEnumerable<BarrelId> authorized) => _authorized = new(authorized);
    public static FireGateAuthorization Independent(BarrelId barrel) => new(new[] { barrel });
    public static FireGateAuthorization SameTrigger(BarrelId first, BarrelId second)
        => first == second ? throw new ArgumentException("SameTrigger requires two barrels.") : new(new[] { first, second });
    public bool MayReleaseSafety(BarrelId barrel) => _authorized.Contains(barrel);
}
```

This class is a tested domain contract only in milestone 1; do not wire it into `TriggerConsole` or alter current arming behavior.

- [ ] **Step 5: Run tests and commit safety contracts**

Run `dotnet run --project IronNestFCS.Domain.Tests`.

Expected: all tests pass.

```powershell
git add IronNestFCS.Domain IronNestFCS.Domain.Tests
git commit -m "feat: validate fire groups and gate authorization"
```

---

### Task 4: Reproduce Baseline Target Order and Single-Shot Creation

**Files:**
- Create: `IronNestFCS.Domain/Planning/ShellRules.cs`
- Create: `IronNestFCS.Domain/Planning/BaselinePlanner.cs`
- Create: `IronNestFCS.Domain/Diagnostics/PlanFingerprint.cs`
- Create: `IronNestFCS.Domain.Tests/BaselinePlannerTests.cs`
- Modify: `IronNestFCS.Domain.Tests/SnapshotBuilder.cs`
- Modify: `IronNestFCS.Domain.Tests/Program.cs`

**Interfaces:**
- Consumes: one valid `BattleSnapshot` whose targets may be in arbitrary input order.
- Produces: `EngagementPlan BaselinePlanner.Plan(BattleSnapshot snapshot)` containing up to the number of free barrels as independent one-shot groups.

- [ ] **Step 1: Write failing baseline behavior tests**

Cover each observable master rule with named scenarios:

```csharp
public static void HigherPriorityWinsBeforeMechanicalCost()
{
    var snapshot = SnapshotBuilder.Valid(targets: new[] {
        SnapshotBuilder.Target("near", priority: 2, distanceKm: 1, bearingDeg: 0),
        SnapshotBuilder.Target("gun", priority: 5, distanceKm: 10, bearingDeg: 180)
    });
    AssertEx.Equal("gun", new BaselinePlanner().Plan(snapshot).Groups[0].Shots[0].TargetEntityId);
}

public static void EqualPriorityUsesDistanceAndTurretCost()
{
    var snapshot = SnapshotBuilder.Valid(turretBearingDeg: 10, targets: new[] {
        SnapshotBuilder.Target("slow", priority: 2, distanceKm: 5, bearingDeg: 170),
        SnapshotBuilder.Target("fast", priority: 2, distanceKm: 3, bearingDeg: 15)
    });
    AssertEx.Equal("fast", new BaselinePlanner().Plan(snapshot).Groups[0].Shots[0].TargetEntityId);
}

public static void ShellFallbackMatchesMaster()
{
    AssertEx.Equal(ShellKind.AP, PlanOne(SnapshotBuilder.Target("armor", armored: true)).Shell);
    AssertEx.Equal(ShellKind.HE, PlanOne(SnapshotBuilder.Target("armor", armored: true, immune: new[] { ShellKind.AP })).Shell);
    AssertEx.Equal(ShellKind.DRIL, PlanOne(SnapshotBuilder.Target("soft")).Shell);
    AssertEx.Equal(ShellKind.LE, PlanOne(SnapshotBuilder.Target("soft", immune: new[] { ShellKind.DRIL })).Shell);
    AssertEx.Equal(ShellKind.HCHE, PlanOne(SnapshotBuilder.Target("soft", immune: new[] { ShellKind.DRIL, ShellKind.LE, ShellKind.HE })).Shell);
}

public static void InvalidObservationCreatesNoPlan()
{
    var plan = new BaselinePlanner().Plan(SnapshotBuilder.Invalid(8, "EntitiesUnavailable"));
    AssertEx.False(plan.HasFire);
    AssertEx.Equal(NoPlanReason.ObservationInvalid, plan.NoPlanReason);
}

public static void PendingManualFifoSuppressesAutonomy()
{
    var plan = new BaselinePlanner().Plan(SnapshotBuilder.Valid(pendingManualTasks: 1, targets: new[] { SnapshotBuilder.Target("a") }));
    AssertEx.Equal(NoPlanReason.ManualQueuePending, plan.NoPlanReason);
}

public static void StableBaselineWithCbtDisabledWithholdsFdc()
{
    var snapshot = SnapshotBuilder.Valid(targets: new[] {
        SnapshotBuilder.Target("fdc", priority: 6, isFdc: true),
        SnapshotBuilder.Target("gun", priority: 5)
    });
    AssertEx.Equal("gun", new BaselinePlanner().Plan(snapshot).Groups[0].Shots[0].TargetEntityId);
}
```

Also add tests for both barrels busy, one barrel busy, entity-reserved, position-reserved, moving snapshot preservation, and deterministic fingerprint formatting under a non-English current culture.

- [ ] **Step 2: Run tests and verify planner-not-found failures**

Run `dotnet run --project IronNestFCS.Domain.Tests`.

Expected: FAIL to compile because `BaselinePlanner`, `ShellRules`, and `PlanFingerprint` do not exist.

- [ ] **Step 3: Implement baseline shell rules exactly**

```csharp
public static ShellKind Choose(TargetSnapshot target)
{
    var immune = target.ImmuneShells;
    if (target.IsArmored || target.IsUnderground)
    {
        if (!immune.Contains(ShellKind.AP)) return ShellKind.AP;
        if (!immune.Contains(ShellKind.HE)) return ShellKind.HE;
        return ShellKind.HCHE;
    }
    if (!immune.Contains(ShellKind.DRIL)) return ShellKind.DRIL;
    if (!immune.Contains(ShellKind.LE)) return ShellKind.LE;
    if (!immune.Contains(ShellKind.HE)) return ShellKind.HE;
    return ShellKind.HCHE;
}
```

Copy the current `ShellData` constants exactly: `KmPerWorldUnit=3.8164`, blast radii `AP=.10`, `DRIL=.05`, `HE=.19`, `HCHE=.43`, `LE=.10`, damage radii `.15/.07/.25/.55/.15`, and costs `HCHE=18`, `DRIL=3`, all others `10`.

- [ ] **Step 4: Implement selection without Unity or CBT branches**

The planner must:

1. return explicit no-plan reasons for invalid observation, pending manual FIFO, no free barrels, or no eligible targets;
2. copy targets into `List<TargetSnapshot>` and call `List.Sort` with the same two-key comparator as `TacticalDecider.SortTargets`: priority descending, then `distanceKm * 2.56f + AngleDelta(turretBearing, targetBearing) * 0.30f` ascending; return `0` on an exact tie and do not add a tie-breaker that master does not have;
3. simulate the old Left-then-Right dispatch capacity using `HasActiveTask`, but do not assign a barrel in the plan;
4. skip `IsEntityReserved || IsPositionReserved`; because milestone 1 fixes `CbtEnabled=false` to match stable `master`, also skip `IsFdc=true` exactly as current `FdcDispatchable()` does outside CBT;
5. create one `Independent` group per shot with `AllowIndependentFallback=false` and `ChargePolicy.Minimum`;
6. copy motion data into `AimOrigin`, `AimVelocity`, `AimStartTime`, and `VelocityUnknown` exactly;
7. reserve a selected single target locally before selecting the next shot.

Use this exact angle helper:

```csharp
private static float AngleDelta(float a, float b)
{
    var d = MathF.Abs(a - b) % 360f;
    return d > 180f ? 360f - d : d;
}
```

- [ ] **Step 5: Implement invariant fingerprinting**

`PlanFingerprint.Format(plan)` must use `CultureInfo.InvariantCulture`, order groups and shots as stored, and include these fields in one line: snapshot version, no-plan reason, group ID, trigger mode, target ID, impact XYZ to 4 decimals, shell, charge policy, moving flag, velocity-known flag, cluster members in ordinal order, tracking ID, and cluster offset XYZ. This is comparison data, not prose logging.

- [ ] **Step 6: Run tests and commit baseline single-shot planning**

Run `dotnet run --project IronNestFCS.Domain.Tests`.

Expected: all baseline tests pass.

```powershell
git add IronNestFCS.Domain IronNestFCS.Domain.Tests
git commit -m "feat: reproduce baseline target and shell decisions"
```

---

### Task 5: Port Existing Cluster Decisions and Whole-Unit Virtual Reservations

**Files:**
- Create: `IronNestFCS.Domain/Planning/ClusterSolver.cs`
- Create: `IronNestFCS.Domain.Tests/ClusterPlannerTests.cs`
- Modify: `IronNestFCS.Domain/Planning/BaselinePlanner.cs`
- Modify: `IronNestFCS.Domain.Tests/SnapshotBuilder.cs`
- Modify: `IronNestFCS.Domain.Tests/Program.cs`

**Interfaces:**
- Consumes: ordered soft targets, friendlies, existing reservation flags, and the first selected target.
- Produces: the same HE/HCHE MEC impact, coverage count, moving-member IDs, and second-shot exclusion behavior as `FSC.TryBuildClusterTask`.

- [ ] **Step 1: Write failing golden-vector cluster tests**

Use explicit points in world units and verify:

- two nearby static soft targets choose HE and the midpoint impact;
- four targets coverable only by HCHE choose HCHE;
- HCHE is rejected when its targets-per-cost ratio does not beat a valid HE cluster;
- a friendly inside `DamageRadius * 1.2` rejects that impact;
- armored and underground targets never enter candidates;
- different motion flags or velocity differences above `0.002 km/s` never share a moving cluster;
- a first cluster's blast-position reservation prevents the second planned shot from selecting a covered static member;
- a moving cluster records every alive, soft, moving entity with matching velocity inside the final blast radius—including the leader and even a previously reserved entity—exactly as current `TryBuildClusterTask` does, in ordinal order, and preserves leader-relative `ClusterOffset`;
- a static cluster records diagnostic members as every unreserved, static soft target inside the final blast radius, while the legacy adapter later omits those IDs from `ArtilleryTask.ClusterMembers` to preserve position-only registration.

One representative test:

```csharp
public static void TwoNearbySoftTargetsBecomeOneHeGroup()
{
    var leader = SnapshotBuilder.Target("a", position: new(0, 0, 0));
    var neighbor = SnapshotBuilder.Target("b", position: new(0.05f, 0, 0));
    var plan = new BaselinePlanner().Plan(SnapshotBuilder.Valid(targets: new[] { leader, neighbor }));
    var shot = plan.Groups[0].Shots[0];
    AssertEx.Equal(ShellKind.HE, shot.Shell);
    AssertEx.Equal("", shot.TargetEntityId);
    AssertEx.True(MathF.Abs(shot.ImpactPoint.X - 0.025f) < 0.0001f);
}
```

- [ ] **Step 2: Run tests and verify they fail as single-shot DRIL plans**

Run `dotnet run --project IronNestFCS.Domain.Tests`.

Expected: cluster cases FAIL because the baseline planner still emits single-target DRIL shots.

- [ ] **Step 3: Port the current bounded MEC solver exactly**

Port `FCS/ClusterSolver.cs` using `WorldPoint` and `MathF`:

- retain `MaxCandidates = 8`;
- candidates must be within `(2R)^2` of the leader;
- enumerate subsets that include the leader;
- choose a larger cover count only, retaining first encounter on ties;
- compute one-, two-, and three-point defining circles;
- use `1e-4f` containment tolerance and `1e-8f` collinearity threshold;
- reject any center with a friendly at or inside the friendly-safe radius.

Expose only:

```csharp
public readonly record struct ClusterResult(WorldPoint Impact, int Count, float RadiusKm);
public static ClusterResult? Best(WorldPoint leader, IReadOnlyList<WorldPoint> candidates, float blastRadiusKm, IReadOnlyList<WorldPoint> friendlies, float friendlySafeKm);
```

- [ ] **Step 4: Add cluster choice and virtual reservation to `BaselinePlanner`**

For each selected soft leader, calculate HE and HCHE results. HE is valid at count `>=2`; HCHE at count `>=4`. Choose HCHE only when HE is invalid or:

```csharp
hche.Count * ShellRules.Cost(ShellKind.HE) > he.Count * ShellRules.Cost(ShellKind.HCHE)
```

Cluster shots use empty `TargetEntityId`. Static clusters include diagnostic `ClusterMembers` as defined in Step 1 but rely only on the local blast-position reservation for subsequent selection. Moving clusters include the current implementation's broader matching covered entity set, `TrackEntityId=leader.EntityId`, and `ClusterOffset=impact-leader.Position`; locally reserve those moving IDs as well. Compute the cluster shot's `BearingDeg` and `DistanceKm` exclusively with `snapshot.Geometry.Locate(impact)` from Task 2; do not call Unity or accept a geometry delegate in the planner.

- [ ] **Step 5: Run full pure suite and commit cluster parity**

Run `dotnet run --project IronNestFCS.Domain.Tests`.

Expected: all selection, shell, cluster, reservation, and geometry tests pass.

```powershell
git add IronNestFCS.Domain IronNestFCS.Domain.Tests
git commit -m "feat: port baseline cluster planning"
```

---

### Task 6: Capture Trustworthy Runtime Snapshots and Golden Legacy Decisions

**Files:**
- Create: `IronNestFCS.Logic/Planning/ObservationAdapter.cs`
- Create: `IronNestFCS.Logic/Planning/LegacyDecisionRecorder.cs`
- Create: `IronNestFCS.Logic/Planning/PlannerRollout.cs`
- Modify: `IronNestFCS.Logic/TacticalRadar.cs`
- Modify: `IronNestFCS.Logic/FCS/GunSystem.cs`
- Modify: `IronNestFCS.Logic/FSC.cs`
- Modify: `IronNestFCS.Logic/FcsModule.cs`
- Modify: `IronNestFCS.Logic/IronNestFCS.Logic.csproj`

**Interfaces:**
- Consumes: runtime state read on the main thread.
- Produces: `ObservationCapture Capture()`, where `ObservationCapture` contains the current valid/invalid `BattleSnapshot`, the last trustworthy snapshot (if any), and exact `ScanFailure` diagnostics; `LegacyDecisionRecorder.Record(ArtilleryTask, IReadOnlyList<TargetInfo>)` produces a domain fingerprint input.

- [ ] **Step 1: Add a pure invalid-scan regression test before changing radar**

Extend `BattleSnapshotTests` with:

```csharp
public static void InvalidObservationNeverCarriesDeathEvidence()
{
    var snapshot = SnapshotBuilder.Invalid(9, "EnumeratorUnavailable");
    AssertEx.False(snapshot.IsValid);
    AssertEx.Equal(0, snapshot.Targets.Count);
    AssertEx.Equal(NoPlanReason.ObservationInvalid, new BaselinePlanner().Plan(snapshot).NoPlanReason);
}
```

Run `dotnet run --project IronNestFCS.Domain.Tests`; expected PASS, establishing the domain side before runtime integration.

- [ ] **Step 2: Make radar scanning transactional**

Change `TacticalRadar.Scan()` to return:

```csharp
public readonly record struct RadarScanResult(bool Valid, string Failure, long Version);
```

Build local `targets`, `friendlies`, and `aliveIds` first. Refactor entity enumeration to `TryForEachEntity(..., out string failure)`. Only when enumeration and required reflection properties succeed:

1. sort local targets;
2. call `Registry.Reconcile(aliveIds)`;
3. replace `ObservedHostiles` with the pre-sort enumeration-order copy, `AliveHostiles` with the sorted copy used by the legacy path, and `AllyPositions` with the successful local list;
4. increment the successful-scan version;
5. update T1/T2 auto markers;
6. return `Valid=true`.

On failure, leave previous lists and registry untouched, do not move/reset markers, and return a specific failure such as `FireMissionMissing`, `EntitiesMissing`, `EnumeratorMissing`, `CurrentMissing`, or `EntityReadException:<Type>`.

- [ ] **Step 3: Add read-only barrel accessors without recovery actions**

Add only these methods to `GunSystem`:

```csharp
public bool? ObserveReloadWorking() => reloadController == null ? null : reloadController.working;
public bool? ObserveBreechLocked() => gunController == null ? null : gunController.ExternalReloadLoweringLocked;
public bool? ObservePendingReload() => gunController == null ? null : gunController.pendingReload;
public bool? ObserveCanFire() => gunController == null ? null : gunController.CanFire;
public bool ObservationBound => gunController != null;
```

Use existing `BulletInChamber`, `ChamberCharges`, and `AssessChamber` plus these nullable accessors to map domain state. Convert `ChamberCharges() < 0` to `null`; do not emit `-1`. The current trigger adapter has no trustworthy read API for lever position, so set `SafetyReleased=null` rather than inferring it from task progress. Do not call `CleanState`, `RegressState`, `EjectChamberedShell`, or any interaction from observation.

- [ ] **Step 4: Implement `ObservationAdapter.Capture()`**

On each call, capture `Time.time` once. Keep `BattleSnapshot? LastTrustedSnapshot` inside the adapter. If radar scan is invalid, return an invalid current snapshot with the returned failure and no targets plus the unchanged `LastTrustedSnapshot`; do not plan from either one. If valid, replace `LastTrustedSnapshot` only after the complete snapshot has been constructed, then return it as both current and trusted:

- map `UnityEngine.Vector3` to `WorldPoint` by components;
- copy enumeration-order fields from `radar.ObservedHostiles`, including `IsFdc` and immune shells; the pure planner performs the one and only sort for its path while the legacy path continues using `radar.AliveHostiles`;
- compute reservation flags from `Registry.IsHandled(id)` and `Registry.IsHandledNear(position, 0)`;
- capture map geometry required by Task 5 from the same `Draggable Surface` and turret reference used by radar: copy `worldToLocalMatrix.m00..m03`, `m10..m13`, and `m20..m23` into `MapAffineTransform`, and use `worldToLocalMatrix.MultiplyPoint3x4(turret.position)` as `TurretMapLocal`;
- set barrel `HasActiveTask` from `LeftTask`/`RightTask`; while FSC is still the executor, expose a synthetic immutable lease ID `legacy:<left|right>:<targetId>` for an active task and `null` for an idle barrel;
- count `QueueCan` entries whose `Source == Manual`;
- set `CbtEnabled=false` unconditionally in milestone 1;
- map `ObservationBound=false` to `Unknown`; otherwise map `pendingReload=true` to `Recovering`, `reloadWorking=true` to `Transitioning`, reliable `CanFire=true` to `Committed`, a non-null chamber shell to `ShellLoaded`, and only a reliably bound empty chamber to `Empty`.

Add a project reference from Logic to Domain with `<Private>false</Private>` and include `IronNestFCS.Domain.dll` in release packaging and explicit deployment under `UserLibs`.

- [ ] **Step 5: Record legacy decisions without moving ownership**

`LegacyDecisionRecorder` maps each old `ArtilleryTask` immediately after creation to the same fields as `ShotIntent`. For static cluster tasks whose `ClusterMembers` is null, derive comparison members from the successfully scanned list using the same static-soft, unreserved, in-radius rule from Task 5, ordered by entity ID. The recorder must not enqueue, commit, release, or mutate the task.

Emit one invariant structured line per scan:

```text
[PLAN-GOLDEN] scan=<version>|valid=<0/1>|free=<0..2>|manual=<count>|legacy=<fingerprint>|reason=<failure-or-none>
```

Do not log raw IL2CPP object `ToString()` output.

- [ ] **Step 6: Build and inspect the diff for forbidden mechanics changes**

Run:

```powershell
dotnet run --project IronNestFCS.Domain.Tests
dotnet build IronNestFCS.sln -c Release -p:ArtifactsDir="$PWD\artifacts"
git diff --check
git diff -- IronNestFCS.Logic/FSC.cs IronNestFCS.Logic/FCS/GunSystem.cs
```

Expected: tests and build pass; FSC changes are read-only exposure/recording only; GunSystem changes contain observation accessors only.

- [ ] **Step 7: Commit observation and golden logging**

```powershell
git add IronNestFCS.Logic IronNestFCS.Domain IronNestFCS.Domain.Tests build.ps1 deploy.ps1
git commit -m "feat: capture trustworthy planning snapshots"
```

---

### Task 7: Run the New Planner in Non-Mutating Shadow Mode

**Files:**
- Create: `IronNestFCS.Logic/Planning/ShadowPlanComparer.cs`
- Modify: `IronNestFCS.Logic/Planning/PlannerRollout.cs`
- Modify: `IronNestFCS.Logic/FcsModule.cs`
- Modify: `IronNestFCS.Logic/FcsWindow.cs`
- Create: `docs/runtime-validation/autonomy-core-template.md`

**Interfaces:**
- Consumes: the pre-dispatch `BattleSnapshot`, pure `EngagementPlan`, and recorded authoritative legacy decisions from the same scan.
- Produces: `ShadowComparison` with `Match`, exact differing fields, cumulative match/mismatch counts, and last mismatch fingerprint.

- [ ] **Step 1: Write failing fingerprint comparison tests**

Add pure tests for exact match and one-field differences in target, order, cluster/single, impact, members, shell, charge policy, and motion snapshot. A representative assertion:

```csharp
var diff = PlanFingerprint.Compare(expected, actual);
AssertEx.SequenceEqual(new[] { "groups[0].shots[0].shell:DRIL!=LE" }, diff);
```

Run `dotnet run --project IronNestFCS.Domain.Tests`.

Expected: FAIL because `PlanFingerprint.Compare` does not exist.

- [ ] **Step 2: Implement field-level comparison in the domain assembly**

Compare list lengths first, then group order, then fields in the exact acceptance order: target, group trigger, impact with `0.0001f` component tolerance, cluster members as ordinal sets, shell, charge policy, moving flag, velocity-known flag, aim origin/velocity/start time with `0.0001f` tolerance. Return deterministic strings; never stop at the first difference.

- [ ] **Step 3: Integrate shadow calculation before legacy side effects**

In `FcsModule.OnGunIdle`:

1. call `ObservationAdapter.Capture()` once;
2. if invalid, log and return without `ClearPendingTasks` or planning;
3. call `BaselinePlanner.Plan(snapshot)` before enqueuing any old task;
4. run the unchanged legacy Left-then-Right loop as authority and record its decisions;
5. compare only after the loop has produced its decision records;
6. emit one line:

```text
[PLAN-SHADOW] scan=<version>|match=<0/1>|legacy=<fingerprint>|new=<fingerprint>|diff=<semicolon-separated-fields>
```

Shadow mode must not call `EnqueueTask`, `Registry.Commit`, hardware methods, or marker methods from the new path. Add `PlannerRollout.AssertShadowReadOnly()` checks around `QueueCan.Count` and the object identities of `LeftTask`/`RightTask`; log an error and force legacy-only mode if any value changes while `BaselinePlanner.Plan` and `ShadowPlanComparer.Compare` run. Domain and shadow assemblies receive no `FSC` or `TargetRegistry` reference, which makes registry mutation structurally unavailable.

- [ ] **Step 4: Display rollout state without adding controls yet**

Add one compact line to `FcsWindow`: `Planner: LEGACY+SHADOW  match X / mismatch Y`, plus the last invalid-observation or mismatch reason. No gameplay behavior changes.

- [ ] **Step 5: Add the runtime evidence template**

The template must contain:

```markdown
# Autonomy Core Runtime Validation
- Commit:
- DLL SHA-256:
- Scenario / target composition:
- Start and end local time:
- Inputs: auto toggle, T1–T4 actions, hot reload action, invalid-scan action
- Complete automatic engagement cycle observed:
- PLAN-SHADOW matches:
- PLAN-SHADOW mismatches:
- Lock leak / stuck task / unintended fire:
- Relevant log filename and line timestamps:
- Result: PASS or FAIL
```

- [ ] **Step 6: Verify shadow purity and commit**

Run pure tests, full build, `git diff --check`, and search the new shadow files:

```powershell
rg -n "EnqueueTask|Commit\(|Release\(|TriggerConsole|LoadBullet|LoadPowder|Arm\(|Fire\(" IronNestFCS.Logic/Planning/ShadowPlanComparer.cs IronNestFCS.Domain
```

Expected: no mutating runtime call appears in shadow/domain code.

```powershell
git add IronNestFCS.Domain IronNestFCS.Domain.Tests IronNestFCS.Logic docs/runtime-validation/autonomy-core-template.md
git commit -m "feat: compare baseline planner in shadow mode"
```

---

### Task 8: Add the Legacy Scheduler Adapter and Reversible Authority Switch

**Files:**
- Create: `IronNestFCS.Logic/Planning/LegacySchedulerAdapter.cs`
- Modify: `IronNestFCS.Logic/Planning/PlannerRollout.cs`
- Modify: `IronNestFCS.Logic/FcsModule.cs`
- Modify: `IronNestFCS.Logic/FcsWindow.cs`

**Interfaces:**
- Consumes: a validated plan and the exact snapshot version that produced it.
- Produces: `AdapterResult TryConvert(EngagementPlan plan, long currentSnapshotVersion, float gameTime)` containing either ordered `ArtilleryTask` objects or a typed rejection; no task is enqueued inside the adapter.

- [ ] **Step 1: Add pure adapter-eligibility tests at the contract boundary**

Extend `PlanContractTests` to prove legacy acceptance for two independent one-shot groups and rejection for stale version, `SameTrigger`, any relation, or group cardinality other than one. Run tests; expected PASS through the validator built in Task 3.

- [ ] **Step 2: Implement conversion with no partial output**

`TryConvert` first calls `PlanContractValidator.Validate(..., legacyAdapter:true)`. On any rejection, return an empty task list. On success, map every shot in a temporary list, and publish the list only when all enum and numeric conversions succeed.

Map fields exactly, except for the intentional diagnostic-only static cluster members noted below:

```text
ShotIntent.TargetEntityId -> ArtilleryTask.entityId
ShotIntent.DisplayName -> entityName
ImpactPoint -> position (Unity Vector3)
BearingDeg -> angel
DistanceKm -> distance
ShellKind -> BulletType by explicit switch
ChargePolicy.Maximum -> useMaxCharge=true
IsMoving/AimOrigin/AimVelocity/AimStartTime/VelocityUnknown -> matching task fields
BlastRadiusKm/TrackEntityId/ClusterOffset -> matching task fields
ClusterMembers -> matching task field only when IsMoving=true; otherwise null to retain master position-only registration
Source -> TaskSource.Auto
```

Reject NaN/infinite bearing, distance, position, or velocity. Do not assign a barrel, commit registry entries, or enqueue inside the adapter.

- [ ] **Step 3: Add rollout modes and an immediate fallback**

Define:

```csharp
internal enum PlannerAuthority { Legacy, Baseline }
```

Default to `Legacy` on module initialization and every hot reload. Bind `Ctrl+Numpad7` to toggle authority and log the new mode. In `Baseline` mode:

1. capture snapshot and plan;
2. if plan has no fire and the reason is `ManualQueuePending`, `NoFreeBarrel`, or `NoEligibleTarget`, enqueue nothing; if the reason is `ObservationInvalid`, return before queue cleanup; `NoPlanReason.None` with no groups is a contract rejection and triggers legacy fallback;
3. convert through the adapter;
4. verify snapshot version has not changed;
5. enqueue converted tasks in plan order through existing `fcs.EnqueueTask`;
6. on exception, validation rejection, or version mismatch, log `[PLAN-FALLBACK]` and run the legacy picker for that scan.

The fallback must run at most once per scan. It must not run after any converted task has been enqueued; conversion is all-or-nothing before the first side effect.

- [ ] **Step 4: Keep T1–T4 ahead of autonomous planning**

Before either legacy or baseline automatic selection, inspect `QueueCan` for pending `TaskSource.Manual`. If non-empty, skip automatic planning and call no queue-clearing method that could disturb manual order. Do not cancel either active task. Existing `EnqueueTask` and `TryDispatch` remain the sole T1–T4 assignment path.

- [ ] **Step 5: Update status UI and structured logs**

Display `Planner: LEGACY` or `Planner: BASELINE (fallback armed)`. Log the authority and result with scan version. Do not use Numpad8, which is reserved for later planning modes.

- [ ] **Step 6: Verify both authority paths and commit**

Run:

```powershell
dotnet run --project IronNestFCS.Domain.Tests
dotnet build IronNestFCS.sln -c Release -p:ArtifactsDir="$PWD\artifacts"
git diff --check
```

Then inspect `FcsModule.OnGunIdle` to confirm there is one snapshot capture, one authority branch, and no duplicated enqueue after fallback.

```powershell
git add IronNestFCS.Logic IronNestFCS.Domain IronNestFCS.Domain.Tests
git commit -m "feat: route baseline plans through legacy executor"
```

---

### Task 9: Runtime Acceptance, Then Remove the Old Picker

**Files:**
- Create: `docs/runtime-validation/runs/2026-08-20-scenario-1.md`
- Create: `docs/runtime-validation/runs/2026-08-20-scenario-2.md`
- Create: `docs/runtime-validation/runs/2026-08-20-scenario-3.md`
- Create: `docs/runtime-validation/runs/2026-08-20-baseline-authority.md`
- Modify: `IronNestFCS.Logic/FcsModule.cs`
- Modify: `IronNestFCS.Logic/FSC.cs`
- Modify: `IronNestFCS.Logic/FcsWindow.cs`
- Modify: `docs/superpowers/specs/2026-08-19-autonomy-core-refactor-design.md`

**Interfaces:**
- Consumes: user-approved deployment and game-runtime logs.
- Produces: three zero-difference runtime records, a default-authoritative baseline planner, and removal of obsolete legacy picker/task-building code only after the acceptance gate.

- [ ] **Step 1: Produce a release package and stop for explicit deployment approval**

Run pure tests and `./build.ps1`. Compute SHA-256 for packaged DLLs:

```powershell
Get-FileHash -Algorithm SHA256 Release/IronNestFCS/Mods/IronNestFCS.dll
Get-FileHash -Algorithm SHA256 Release/IronNestFCS/UserData/IronNestFCS/IronNestFCS.Logic.dll
Get-FileHash -Algorithm SHA256 Release/IronNestFCS/UserLibs/IronNestFCS.Domain.dll
```

Present hashes to the user. Do not run `deploy.ps1` until the user explicitly approves this deployment.

- [ ] **Step 2: Deploy only after approval and verify installed hashes**

Run `./deploy.ps1`, then hash the exact installed destinations and compare to the package. If any mismatch occurs, stop before launching runtime validation.

- [ ] **Step 3: Complete three legacy-authority shadow runs**

The user performs three different target compositions. Each run must cover one complete autonomous engagement cycle and record zero `[PLAN-SHADOW] ... match=0` lines. Copy only relevant timestamped excerpts from the MelonLoader log into the run record; do not copy the full log.

- [ ] **Step 4: Complete control-path runtime cases**

In at least one recorded run:

- submit T1, T2, T3, and T4 while an automatic task is active; verify active fire is not interrupted and manual FIFO order is preserved;
- hot reload once; verify authority returns to Legacy, no old coroutine continues, and no lock/task remains;
- cause or observe one invalid scan (scene transition is acceptable); verify `[PLAN-GOLDEN] valid=0`, no death reconciliation, no new plan, and recovery on the next valid scan;
- inspect read-only barrel logs for empty, loading/transitioning, and committed/`CanFire` states; unknown charge must appear as `unknown`, never `0` or `-1`;
- verify no unintended simultaneous fire and no task stuck past existing watchdog limits.

- [ ] **Step 5: Run one baseline-authority cycle with fallback armed**

Toggle `Ctrl+Numpad7` to Baseline. Complete one automatic cycle. Acceptance requires identical target/order/cluster/impact/shell/charge/motion logs, no `[PLAN-FALLBACK]`, correct T1–T4 priority, and clean hot reload. Any fallback or mismatch returns authority to Legacy and opens a new fix task; do not remove old code.

- [ ] **Step 6: Obtain explicit user acceptance before the irreversible cleanup**

Summarize the four runtime records and ask the user to approve deleting the legacy picker. This is a hard checkpoint. Without explicit approval, leave both authority paths intact and do not proceed.

- [ ] **Step 7: Delete only superseded decision code**

After approval:

- remove `FcsModule.PickTarget` and `FcsModule.CreateAutoTask`;
- remove the legacy decision loop and `LegacyDecisionRecorder`;
- remove `FSC.TryBuildClusterTask` only after confirming no remaining call sites with `rg`;
- default `PlannerAuthority` to `Baseline`;
- retain the adapter, current FSC queue/executor, invalid-observation handling, golden-format diagnostics, and all domain tests;
- remove `PlannerAuthority.Legacy` and the legacy runtime fallback after the old picker is deleted; rename UI text from `BASELINE (fallback armed)` to `BASELINE`;
- change `Ctrl+Numpad7` to toggle `Baseline` versus `PlanningDisabled`. `PlanningDisabled` creates no automatic task and leaves T1–T4 functional; it is the post-acceptance emergency stop, not a simulated legacy fallback.

- [ ] **Step 8: Run final verification**

```powershell
dotnet run --project IronNestFCS.Domain.Tests
dotnet build IronNestFCS.sln -c Release -p:ArtifactsDir="$PWD\artifacts"
rg -n "PickTarget|CreateAutoTask|TryBuildClusterTask|LegacyDecisionRecorder" IronNestFCS.Logic
git diff --check
git status --short
```

Expected: tests/build pass; search has no obsolete decision implementation; only intended files are modified.

- [ ] **Step 9: Mark the milestone and commit**

Update the design status to `Milestone 1 accepted`, linking the runtime records and final commit. Then commit:

```powershell
git add IronNestFCS.Logic IronNestFCS.Domain IronNestFCS.Domain.Tests docs
git commit -m "refactor: make baseline planner authoritative"
git tag autonomy-core-m1
```

Do not merge to `master` or `dev` as part of this plan; integration is a separate user decision.

---

## Completion Gate

Milestone 1 is complete only when all of the following are true:

- ordinary builds provably leave the installed game DLL unchanged;
- the pure test executable covers all listed snapshot, dirty-barrel representation, selection, shell, cluster, stale-plan, group atomicity, and fire-gate cases and exits zero;
- invalid scans cannot reconcile an empty alive set or create a plan;
- shadow mode is side-effect free and records zero differences across three distinct complete runtime cycles;
- T1–T4, hot reload, and invalid-scan runtime cases pass;
- one Baseline-authority full cycle passes without fallback;
- the user explicitly approves removal of legacy selection code;
- no new scheduler, SameTrigger execution, CBT behavior, multi-round execution, ejection automation, `CleanState` rewrite, or FSC mechanical-flow rewrite entered the diff.

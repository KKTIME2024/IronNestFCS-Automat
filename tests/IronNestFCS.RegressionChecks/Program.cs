using IronNestFCS.Logic.FCS;
using IronNestFCS.Abstractions;
using System.Collections;

var failures = new List<string>();

var reloadFlight = new IronNestFCS.ReloadFlightState();
reloadFlight.BeginReload(false, false);
Require(!reloadFlight.Possible, "first load must not block execution as an unknown-flight reload");
reloadFlight.BeginReload(false, false);
Require(!reloadFlight.Possible, "retrying a module that never initialized must not invent a flight history");
reloadFlight.BeginReload(true, false);
Require(reloadFlight.Possible, "same-scene reload must retain unknown flight history");
reloadFlight.BeginReload(false, false);
Require(reloadFlight.Possible, "failed reload retries must not erase unknown flight history");
reloadFlight.BeginReload(true, true);
Require(!reloadFlight.Possible, "new scene must clear previous-scene flight uncertainty");

void Equal(float expected, float actual, float tolerance, string name)
{
    if (Math.Abs(expected - actual) > tolerance)
        failures.Add($"{name}: expected {expected}, got {actual}");
}

void Require(bool condition, string name)
{
    if (!condition)
        failures.Add(name);
}

// Catches replacing the measured linear model with the old rounded lookup table.
Equal(47.58869f, ToFTable.FlightTime(10f, 1), 0.0001f, "C1 at 10 km");
Equal(38.30061f, ToFTable.FlightTime(10f, 2), 0.0001f, "C2 at 10 km");
Equal(26.13011f, ToFTable.FlightTime(10f, 3), 0.0001f, "C3 at 10 km");
Equal(18.94451f, ToFTable.FlightTime(10f, 4), 0.0001f, "C4 at 10 km");
Equal(15.40442f, ToFTable.FlightTime(10f, 5), 0.0001f, "C5 at 10 km");
Equal(14.27168f, ToFTable.FlightTime(10f, 6), 0.0001f, "C6 at 10 km");

var leases = new ExecutionLeaseLedger();
var firstLeftLease = leases.Acquire("Left");
var secondLeftLease = leases.Acquire("Left");
Require(firstLeftLease != secondLeftLease, "slot lease ids must advance");
Require(!leases.IsOwner("Left", firstLeftLease), "stale slot lease must lose ownership");
Require(!leases.Release("Left", firstLeftLease), "stale slot release must be rejected");
Require(leases.IsOwner("Left", secondLeftLease), "current slot lease must remain owner");
Require(leases.Release("Left", secondLeftLease), "current slot lease must release");
Require(!leases.IsOwner("Left", secondLeftLease), "released slot lease must no longer own");

// A timed-out transfer must not leave a nested cleaning action driving the next task's gun.
var authorized = true;
var nestedMoves = 0;
var childDisposed = false;
var parentDisposed = false;
IEnumerator Child()
{
    try
    {
        nestedMoves++;
        yield return null;
        nestedMoves++;
    }
    finally { childDisposed = true; }
}
IEnumerator Parent()
{
    try
    {
        yield return Child();
        nestedMoves++;
    }
    finally { parentDisposed = true; }
}
var cleanup = CancellableRoutine.Run(Parent(), () => authorized);
Require(cleanup.MoveNext() && nestedMoves == 1, "nested cleanup should begin under one owner");
authorized = false;
Require(!cleanup.MoveNext(), "revoked cleanup must stop");
Require(nestedMoves == 1, "revoked nested cleanup must not perform later actions");
Require(childDisposed && parentDisposed, "revocation must dispose child and parent");

authorized = true;
nestedMoves = 0;
childDisposed = parentDisposed = false;
cleanup = CancellableRoutine.Run(Parent(), () => authorized);
Require(cleanup.MoveNext(), "cleanup must reach its first wait");
((IDisposable)cleanup).Dispose();
Require(childDisposed && parentDisposed && nestedMoves == 1,
    "stopping the outer handle must dispose its nested cleanup without advancing it");

authorized = true;
nestedMoves = 0;
cleanup = CancellableRoutine.Run(Parent(), () => authorized);
while (cleanup.MoveNext()) { }
Require(nestedMoves == 3, "authorized cleanup must complete child then parent");

IEnumerator BrokenChild()
{
    try { yield return null; }
    finally { throw new InvalidOperationException("simulated child cleanup failure"); }
}
IEnumerator BrokenParent()
{
    try { yield return BrokenChild(); }
    finally { parentDisposed = true; }
}
parentDisposed = false;
cleanup = CancellableRoutine.Run(BrokenParent(), () => true);
Require(cleanup.MoveNext(), "failing cleanup must reach its wait");
var cleanupFailureReported = false;
try { ((IDisposable)cleanup).Dispose(); } catch (AggregateException) { cleanupFailureReported = true; }
Require(parentDisposed && cleanupFailureReported,
    "a child disposal failure must not skip parent cleanup and must be reported");

var plans = new FirePlanRegistry();

// The installed IL2CPP scheduler only stops pumping; it never calls managed Dispose.
// Exercise the production owner against that behavior, including scheduler failure.
foreach (var stopThrows in new[] { false, true })
{
    childDisposed = parentDisposed = false;
    nestedMoves = 0;
    var stopCalls = 0;
    var completions = 0;
    var routine = new OwnedRoutine(Parent(), _ =>
    {
        stopCalls++;
        if (stopThrows) throw new InvalidOperationException("scheduler stop failed");
    }, _ => completions++);
    routine.Start(_ => new object());
    Require(routine.MoveNext(), "owned routine must start the nested operation");
    try { routine.Stop(); } catch (InvalidOperationException) when (stopThrows) { }
    routine.Stop();
    Require(childDisposed && parentDisposed && nestedMoves == 1,
        "stopping a non-disposing scheduler must still unwind every nested iterator");
    Require(!routine.MoveNext() && stopCalls == 1 && completions == 1,
        "stopped routine must reject late pumping and repeated cleanup");
}
var completedCount = 0;
var completedRoutine = new OwnedRoutine(Parent(), _ => { }, _ => completedCount++);
completedRoutine.Start(_ => new object());
while (completedRoutine.MoveNext()) { }
completedRoutine.Stop();
Require(completedCount == 1, "natural completion must unregister the routine exactly once");

parentDisposed = false;
completedCount = 0;
var failingOwned = new OwnedRoutine(BrokenParent(), _ => { }, _ => completedCount++);
failingOwned.Start(_ => new object());
Require(failingOwned.MoveNext(), "owned failing cleanup must enter its child");
cleanupFailureReported = false;
try { failingOwned.Stop(); } catch (AggregateException) { cleanupFailureReported = true; }
Require(cleanupFailureReported && parentDisposed && completedCount == 1 && !failingOwned.MoveNext(),
    "cleanup failure must still unwind the parent, unregister, and prevent late actions");

var cleanupLock = new CoroutineLock();
var cleanupLease = cleanupLock.CreateLease();
IEnumerator LockedOperation()
{
    yield return cleanupLock.Acquire(cleanupLease);
    try { yield return Parent(); }
    finally { cleanupLock.Release(cleanupLease); }
}
var lockedRoutine = new OwnedRoutine(LockedOperation(), _ => { });
lockedRoutine.Start(_ => new object());
Require(lockedRoutine.MoveNext(), "operation must hold its lock across the nested wait");
var successorLease = cleanupLock.CreateLease();
var successorWaiter = cleanupLock.Acquire(successorLease);
Require(successorWaiter.MoveNext(), "successor must wait before the old operation stops");
lockedRoutine.Stop();
Require(!successorWaiter.MoveNext(), "explicit managed cleanup must release the lock for its successor");
Require(!cleanupLock.Release(cleanupLease) && cleanupLock.Release(successorLease),
    "late cleanup must not release the successor's lock");

var sharedLock = new CoroutineLock();
var oldOwner = sharedLock.CreateLease();
Require(!sharedLock.Acquire(oldOwner).MoveNext(), "first shared resource owner must acquire");
var nextOwner = sharedLock.CreateLease();
var waiter = sharedLock.Acquire(nextOwner);
Require(waiter.MoveNext(), "shared resource waiter must wait");
Require(sharedLock.Release(oldOwner), "current resource owner must release");
Require(!waiter.MoveNext(), "next owner must acquire after release");
Require(!sharedLock.Release(oldOwner), "late shared lock release must not release a new owner");
var thirdOwner = sharedLock.CreateLease();
var thirdWaiter = sharedLock.Acquire(thirdOwner);
Require(thirdWaiter.MoveNext(), "new owner must remain protected after stale release");
sharedLock.Reset();
var rejectedStaleWaiter = false;
try { thirdWaiter.MoveNext(); } catch (InvalidOperationException) { rejectedStaleWaiter = true; }
Require(rejectedStaleWaiter, "rebind must invalidate pending old resource operations");

var exclusions = new AutomaticTargetExclusions();
var canceledGroup = new ArtilleryTask { entityId = "leader", ClusterMembers = new() { "member" } };
exclusions.Exclude(canceledGroup);
Require(exclusions.Contains("leader") && exclusions.Contains("member"), "explicit cancellation must exclude all fixed members");
exclusions.ObserveAliveIds(new[] { "leader", "member" });
Require(exclusions.Count == 2, "mode switches and repeated scans must retain explicit exclusions");
exclusions.ObserveAliveIds(new[] { "member" });
exclusions.ObserveAliveIds(new[] { "leader", "member" });
Require(!exclusions.Contains("leader"), "observed replacement of an old id must not inherit its cancellation");
exclusions.Clear();
Require(exclusions.Count == 0, "explicitly restoring automatic selection must clear exclusions");
exclusions.Exclude(canceledGroup);
var restoredExclusions = new AutomaticTargetExclusions();
restoredExclusions.Restore(exclusions.Snapshot());
Require(restoredExclusions.Contains("leader") && restoredExclusions.Contains("member"),
    "same-scene reload must preserve cancellation exclusions as value data");
var targets = new TargetRegistry();

var explicitlyCanceled = new ArtilleryTask { entityId = "explicitly-canceled" };
plans.Admit(explicitlyCanceled);
explicitlyCanceled.RevokeFireAuthorization("player cancel");
plans.Revoke(explicitlyCanceled, explicitlyCanceled.CancelReason);
plans.Pause(explicitlyCanceled, "mode switch during physical handover");
plans.Resume(explicitlyCanceled);
Require(plans.Get(explicitlyCanceled.PlanId)?.State == FirePlanState.Canceled
    && plans.Get(explicitlyCanceled.PlanId)?.FireAuthorized == false,
    "pause/resume during handover must not resurrect an explicitly canceled plan");

var group = new ArtilleryTask
{
    entityId = "group-leader",
    ClusterMembers = new() { "group-leader", "group-middle", "group-tail" }
};
UnityEngine.Time.time = 10f;
targets.Commit(group);
group.Fired = true;
targets.MarkFiredBlast(group, group.position, 0.12f);
UnityEngine.Time.time += TargetRegistry.FlightWindow + 1f;
Require(group.ClusterMembers.All(id => !targets.IsHandled(id)),
    "all fired group members must leave the flight reservation window together");
UnityEngine.Time.time = 0f;

// STOP invalidates both command sources and every old execution generation.
var execution = new ExecutionControl();
var stopPlans = new FirePlanRegistry();
execution.StartAutomatic();
var sealedAuto = new ArtilleryTask { bulletType = BulletType.AP, LoadedCharge = 6,
    Source = TaskSource.Auto, progress = Progress.WaitingForFire, ArmConfirmed = true };
var queuedManual = new ArtilleryTask { Source = TaskSource.Manual, progress = Progress.Pending };
var activeManual = new ArtilleryTask { Source = TaskSource.Manual, progress = Progress.LoadingPowder };
var oldCommands = new[] { sealedAuto, queuedManual, activeManual };
foreach (var task in oldCommands) { execution.Admit(task); stopPlans.Admit(task); }
var irreversibleCalls = 0;
var finalized = false;
IEnumerator ArmedOperation()
{
    try
    {
        yield return null; // sealed and armed, but no trigger submitted
        SingleShotGate.TrySubmit(true, true, false, () => execution.IsAuthorized(sealedAuto),
            () => sealedAuto.TriggerSubmitted = true, () => irreversibleCalls++);
    }
    finally { finalized = true; }
}
var armedOperation = new OwnedRoutine(ArmedOperation(), _ => { });
armedOperation.Start(_ => new object());
Require(armedOperation.MoveNext(), "sealed shot must be waiting before STOP");
execution.Stop(oldCommands, stopPlans, "STOP");
armedOperation.Stop();
Require(finalized && !armedOperation.MoveNext() && irreversibleCalls == 0,
    "STOP before submission must unwind a sealed shot without pulling the trigger");
Require(oldCommands.All(task => !execution.IsAuthorized(task) && !task.Paused
    && task.progress == Progress.Canceled && !stopPlans.IsAuthorized(task)),
    "STOP must revoke active and queued manual/automatic commands without resumable holds");
Require(sealedAuto.bulletType == BulletType.AP && sealedAuto.LoadedCharge == 6,
    "stopping execution must not replace the retained ammunition snapshot");
execution.StartAutomatic();
Require(oldCommands.All(task => !execution.IsAuthorized(task)), "AUTO must not revive old commands");
// Even a late callback incorrectly restoring flags cannot restore an old generation.
sealedAuto.Canceled = false;
sealedAuto.FireAuthorized = true;
Require(!execution.IsAuthorized(sealedAuto), "stale callbacks cannot restore firing authority after AUTO");

var fresh = new ArtilleryTask { Source = TaskSource.Auto, bulletType = BulletType.AP, LoadedCharge = 6 };
execution.Admit(fresh);
stopPlans.Admit(fresh);
var submissions = 0;
void Submitted()
{
    submissions++;
    fresh.TriggerSubmitted = true;
    stopPlans.MarkTriggerSubmitted(fresh);
}
Require(!SingleShotGate.TrySubmit(true, true, true, () => execution.IsAuthorized(fresh), Submitted,
        () => irreversibleCalls++), "an armed canceled peer must block the global trigger");
Require(!SingleShotGate.TrySubmit(true, false, false, () => execution.IsAuthorized(fresh), Submitted,
        () => irreversibleCalls++), "unknown peer arm state must block the global trigger");
Require(submissions == 0 && irreversibleCalls == 0, "rejected fire must not create a submission");
Require(SingleShotGate.TrySubmit(true, true, false, () => execution.IsAuthorized(fresh), Submitted,
        () => irreversibleCalls++), "a freshly authorized shot may fire after peer OFF confirmation");
Require(!SingleShotGate.TrySubmit(true, true, false, () => execution.IsAuthorized(fresh), Submitted,
        () => irreversibleCalls++), "a submitted shot must never submit the global trigger twice");
execution.Stop(new[] { fresh }, stopPlans, "STOP after submission");
Require(ExecutionControl.NeedsShotObservation(fresh) && !fresh.ShouldReleaseTargetOnExit
    && !fresh.Fired && stopPlans.Get(fresh.PlanId)?.Result == FirePlanResult.Pending
    && stopPlans.Get(fresh.PlanId)?.FireAuthorized == false,
    "STOP after submission must revoke authority while retaining observation and reservation");
fresh.Fired = true; // physical observation, not the submit callback
stopPlans.Finish(fresh);
Require(stopPlans.Get(fresh.PlanId)?.Result == FirePlanResult.Pending && submissions == 1 && irreversibleCalls == 1,
    "actual launch after STOP remains an in-flight result without a repeat shot");
var newManual = new ArtilleryTask { Source = TaskSource.Manual };
execution.Admit(newManual);
Require(execution.IsAuthorized(newManual), "a new explicit manual command is allowed after STOP");

execution.StartAutomatic();
var uncertain = new ArtilleryTask { Source = TaskSource.Auto };
execution.Admit(uncertain);
stopPlans.Admit(uncertain);
try
{
    SingleShotGate.TrySubmit(true, true, false, () => execution.IsAuthorized(uncertain),
        () => { uncertain.TriggerSubmitted = true; stopPlans.MarkTriggerSubmitted(uncertain); },
        () => throw new InvalidOperationException("actuator result unavailable"));
    Require(false, "actuator exception must reach the execution guard");
}
catch (InvalidOperationException) { }
Require(ExecutionControl.NeedsShotObservation(uncertain) && !execution.IsAuthorized(uncertain),
    "actuator exception must preserve submission uncertainty and prevent retry");

Equal(16f, TargetLeadSolver.Elevation(8f, 6), 0.0001f, "8 km retained C6 elevation");
Equal(48f, TargetLeadSolver.Elevation(8f, 2), 0.0001f, "8 km minimum C2 elevation");
Require(ToFTable.FlightTime(8f, 6) < ToFTable.FlightTime(8f, 2),
    "retained high charge must use its own flight time for lead prediction");
var noImmunity = new HashSet<string>();
Require(RetainedAmmoPolicy.CanUse(BulletType.AP, 6, 8f, false, false, noImmunity),
    "retained AP + 6 may serve a soft target even when the normal recommendation is DRIL");
Require(!RetainedAmmoPolicy.CanUse(BulletType.AP, 6, 28f, false, false, new HashSet<string> { "AP" }),
    "retained ammunition cannot override target immunity");
Require(!RetainedAmmoPolicy.CanUse(BulletType.DRIL, 6, 28f, true, false, noImmunity),
    "retained DRIL must not be used against armor");
Require(!RetainedAmmoPolicy.CanUse(BulletType.STAR, 6, 28f, false, false, noImmunity),
    "illumination is not a substitute for a lethal automatic shot");
Require(RetainedAmmoPolicy.CanReach(8f, 6),
    "high actual charge must remain usable against a near target");
Require(!RetainedAmmoPolicy.CanReach(float.NaN, 6)
    && !RetainedAmmoPolicy.CanReach(28f, -1)
    && !RetainedAmmoPolicy.CanReach(31f, 6),
    "retained reuse must reject invalid readings and insufficient range");
Require(RetainedAmmoPolicy.CanReach(25f, 6) && RetainedAmmoPolicy.CanReach(30f, 6),
    "extra charge must not exclude shorter distances within the reachable range");

if (failures.Count > 0)
    throw new Exception(string.Join(Environment.NewLine, failures));

Console.WriteLine("Regression checks passed.");

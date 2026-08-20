using IronNestFCS.Domain.Geometry;
using IronNestFCS.Domain.Observation;

namespace IronNestFCS.Domain.Planning;

public sealed class BaselinePlanner
{
    public EngagementPlan Plan(BattleSnapshot snapshot)
    {
        if (!snapshot.IsValid)
        {
            return NoPlan(snapshot, NoPlanReason.ObservationInvalid);
        }
        if (snapshot.PendingManualTaskCount > 0)
        {
            return NoPlan(snapshot, NoPlanReason.ManualQueuePending);
        }

        var freeBarrels = 0;
        if (!snapshot.LeftBarrel.HasActiveTask) freeBarrels++;
        if (!snapshot.RightBarrel.HasActiveTask) freeBarrels++;
        if (freeBarrels == 0)
        {
            return NoPlan(snapshot, NoPlanReason.NoFreeBarrel);
        }

        var ordered = new List<TargetSnapshot>(snapshot.Targets);
        ordered.Sort((a, b) =>
        {
            var pc = b.Priority.CompareTo(a.Priority);
            if (pc != 0) return pc;
            var costA = a.DistanceKm * 2.56f + AngleDelta(snapshot.TurretBearingDeg, a.BearingDeg) * 0.30f;
            var costB = b.DistanceKm * 2.56f + AngleDelta(snapshot.TurretBearingDeg, b.BearingDeg) * 0.30f;
            return costA.CompareTo(costB);
        });

        var reservedIds = new HashSet<string>(StringComparer.Ordinal);
        var groups = new List<FireGroup>();
        foreach (var target in ordered)
        {
            if (groups.Count >= freeBarrels) break;
            if (target.IsEntityReserved || target.IsPositionReserved) continue;
            if (reservedIds.Contains(target.EntityId)) continue;
            if (target.IsFdc) continue;

            reservedIds.Add(target.EntityId);
            groups.Add(CreateShot(snapshot, target));
        }

        if (groups.Count == 0)
        {
            return NoPlan(snapshot, NoPlanReason.NoEligibleTarget);
        }
        return new EngagementPlan(snapshot.Version, PlanSource.Autonomous, groups, Array.Empty<GroupRelation>(), NoPlanReason.None);
    }

    private static FireGroup CreateShot(BattleSnapshot snapshot, TargetSnapshot target)
    {
        var shell = ShellRules.Choose(target);
        var located = snapshot.Geometry.Locate(target.Position);
        var shot = new ShotIntent(
            ShotId: $"shot-{target.EntityId}",
            TargetEntityId: target.EntityId,
            DisplayName: target.Name,
            ImpactPoint: target.Position,
            BearingDeg: located.BearingDeg,
            DistanceKm: located.DistanceKm,
            Shell: shell,
            ChargePolicy: ChargePolicy.Minimum,
            IsMoving: target.IsMoving,
            VelocityUnknown: !target.VelocityKnown,
            AimOrigin: target.Position,
            AimVelocity: target.VelocityWorldPerSecond,
            AimStartTime: snapshot.GameTime,
            BlastRadiusKm: ShellRules.BlastRadiusKm(shell),
            ClusterMembers: Array.Empty<string>(),
            TrackEntityId: null,
            ClusterOffset: WorldPoint.Zero);
        return new FireGroup($"group-{target.EntityId}", TriggerMode.Independent, new[] { shot }, false);
    }

    private static EngagementPlan NoPlan(BattleSnapshot snapshot, NoPlanReason reason) =>
        new(snapshot.Version, PlanSource.Autonomous, Array.Empty<FireGroup>(), Array.Empty<GroupRelation>(), reason);

    private static float AngleDelta(float a, float b)
    {
        var d = MathF.Abs(a - b) % 360f;
        return d > 180f ? 360f - d : d;
    }
}

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
        var reservedPositions = new List<PositionReservation>();
        var groups = new List<FireGroup>();
        foreach (var target in ordered)
        {
            if (groups.Count >= freeBarrels) break;
            if (target.IsEntityReserved || target.IsPositionReserved) continue;
            if (reservedIds.Contains(target.EntityId)) continue;
            if (IsPositionCovered(target.Position, reservedPositions)) continue;
            if (target.IsFdc) continue;

            var shot = CreateShot(snapshot, target, reservedIds, reservedPositions);
            Reserve(shot, target, snapshot, reservedIds, reservedPositions);
            groups.Add(new FireGroup($"group-{shot.ShotId}", TriggerMode.Independent, new[] { shot }, false));
        }

        if (groups.Count == 0)
        {
            return NoPlan(snapshot, NoPlanReason.NoEligibleTarget);
        }
        return new EngagementPlan(snapshot.Version, PlanSource.Autonomous, groups, Array.Empty<GroupRelation>(), NoPlanReason.None);
    }

    private const float MovingVelocityToleranceKmPerS = 0.002f;

    /// <summary>虚拟位置预约: 落点 + 覆盖半径²。模拟 TargetRegistry 的位置条目
    /// (集群/爆区任务 Commit 即带毁伤半径, 屏蔽半径内任何后续候选, 不分类型/运动组)。</summary>
    private readonly record struct PositionReservation(WorldPoint Impact, float RadiusSq);

    private static bool IsPositionCovered(WorldPoint position, IReadOnlyList<PositionReservation> reservedPositions)
    {
        foreach (var reserved in reservedPositions)
            if (WorldPoint.DistanceSquared(position, reserved.Impact) <= reserved.RadiusSq) return true;
        return false;
    }

    private static void Reserve(
        ShotIntent shot, TargetSnapshot leader, BattleSnapshot snapshot,
        HashSet<string> reservedIds, List<PositionReservation> reservedPositions)
    {
        reservedIds.Add(leader.EntityId);
        if (!string.IsNullOrEmpty(shot.TargetEntityId))
        {
            // 单点任务: 旧注册表 Commit 只登记实体(位置条目在击发 MarkFiredBlast 才建),
            // 同周期内不按位置屏蔽邻居 → 半径 0 保持等价。
            reservedPositions.Add(new PositionReservation(shot.ImpactPoint, 0f));
            return;
        }

        // 集群任务: 旧注册表 Commit 即建带毁伤半径的位置条目, 半径内任何目标(含装甲/地下/
        // 异运动组)同周期内都被屏蔽 → 预约必须带半径, 否则等价性破坏。
        var blastWorld = shot.BlastRadiusKm / MapGeometry.KmPerWorldUnit;
        var blastSq = blastWorld * blastWorld;
        reservedPositions.Add(new PositionReservation(shot.ImpactPoint, blastSq));
        foreach (var member in snapshot.Targets)
        {
            if (member.IsArmored || member.IsUnderground) continue;
            if (member.IsMoving != leader.IsMoving) continue;
            if (leader.IsMoving && VelocityDiffKmPerS(member, leader) > MovingVelocityToleranceKmPerS) continue;
            if (WorldPoint.DistanceSquared(member.Position, shot.ImpactPoint) <= blastSq)
                reservedIds.Add(member.EntityId);
        }
    }

    private static float VelocityDiffKmPerS(TargetSnapshot a, TargetSnapshot b) =>
        WorldPoint.Distance(a.VelocityWorldPerSecond, b.VelocityWorldPerSecond) * MapGeometry.KmPerWorldUnit;

    private static ShotIntent CreateShot(
        BattleSnapshot snapshot, TargetSnapshot target,
        HashSet<string> reservedIds, List<PositionReservation> reservedPositions)
    {
        var cluster = TryCluster(snapshot, target, reservedIds, reservedPositions);
        if (cluster is not null)
        {
            return CreateClusterShot(snapshot, target, cluster.Value);
        }

        var shell = ShellRules.Choose(target);
        var located = snapshot.Geometry.Locate(target.Position);
        return new ShotIntent(
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
    }

    private readonly record struct ChosenCluster(ShellKind Shell, WorldPoint Impact, int Count);

    private static ChosenCluster? TryCluster(
        BattleSnapshot snapshot, TargetSnapshot leader,
        HashSet<string> reservedIds, List<PositionReservation> reservedPositions)
    {
        if (leader.IsArmored || leader.IsUnderground) return null;

        var candidates = new List<WorldPoint>();
        foreach (var other in snapshot.Targets)
        {
            if (other.EntityId == leader.EntityId) continue;
            if (other.IsArmored || other.IsUnderground) continue;
            if (other.IsMoving != leader.IsMoving) continue;
            if (leader.IsMoving && VelocityDiffKmPerS(other, leader) > MovingVelocityToleranceKmPerS) continue;
            if (other.IsEntityReserved || other.IsPositionReserved) continue;
            if (reservedIds.Contains(other.EntityId)) continue;
            if (IsPositionCovered(other.Position, reservedPositions)) continue;
            candidates.Add(other.Position);
        }

        var he = ClusterSolver.Best(leader.Position, candidates,
            ShellRules.BlastRadiusKm(ShellKind.HE), snapshot.Friendlies, ShellRules.FriendlySafeRadiusKm(ShellKind.HE));
        var hche = ClusterSolver.Best(leader.Position, candidates,
            ShellRules.BlastRadiusKm(ShellKind.HCHE), snapshot.Friendlies, ShellRules.FriendlySafeRadiusKm(ShellKind.HCHE));

        if (he.HasValue && he.Value.Count >= 2 && hche.HasValue && hche.Value.Count >= 4)
        {
            var heCount = he.Value.Count;
            var hcheCount = hche.Value.Count;
            var hcheWorthIt = hcheCount * ShellRules.Cost(ShellKind.HE) > heCount * ShellRules.Cost(ShellKind.HCHE);
            if (hcheWorthIt) return new ChosenCluster(ShellKind.HCHE, hche.Value.Impact, hcheCount);
            return new ChosenCluster(ShellKind.HE, he.Value.Impact, heCount);
        }
        if (he.HasValue && he.Value.Count >= 2)
            return new ChosenCluster(ShellKind.HE, he.Value.Impact, he.Value.Count);
        if (hche.HasValue && hche.Value.Count >= 4)
            return new ChosenCluster(ShellKind.HCHE, hche.Value.Impact, hche.Value.Count);
        return null;
    }

    private static ShotIntent CreateClusterShot(
        BattleSnapshot snapshot, TargetSnapshot leader, ChosenCluster cluster)
    {
        var located = snapshot.Geometry.Locate(cluster.Impact);
        var blastWorld = ShellRules.BlastRadiusKm(cluster.Shell) / MapGeometry.KmPerWorldUnit;
        var blastSq = blastWorld * blastWorld;
        var members = new List<string>();
        foreach (var other in snapshot.Targets)
        {
            if (other.IsArmored || other.IsUnderground) continue;
            if (other.IsMoving != leader.IsMoving) continue;
            if (leader.IsMoving && VelocityDiffKmPerS(other, leader) > MovingVelocityToleranceKmPerS) continue;
            if (WorldPoint.DistanceSquared(other.Position, cluster.Impact) > blastSq) continue;
            if (!leader.IsMoving && (other.IsEntityReserved || other.IsPositionReserved)) continue;
            members.Add(other.EntityId);
        }

        return new ShotIntent(
            ShotId: $"cluster-{leader.EntityId}",
            TargetEntityId: "",
            DisplayName: leader.Name,
            ImpactPoint: cluster.Impact,
            BearingDeg: located.BearingDeg,
            DistanceKm: located.DistanceKm,
            Shell: cluster.Shell,
            ChargePolicy: ChargePolicy.Minimum,
            IsMoving: leader.IsMoving,
            VelocityUnknown: false,
            AimOrigin: cluster.Impact,
            AimVelocity: leader.VelocityWorldPerSecond,
            AimStartTime: snapshot.GameTime,
            BlastRadiusKm: ShellRules.BlastRadiusKm(cluster.Shell),
            ClusterMembers: members,
            TrackEntityId: leader.IsMoving ? leader.EntityId : null,
            ClusterOffset: cluster.Impact - leader.Position);
    }

    private static EngagementPlan NoPlan(BattleSnapshot snapshot, NoPlanReason reason) =>
        new(snapshot.Version, PlanSource.Autonomous, Array.Empty<FireGroup>(), Array.Empty<GroupRelation>(), reason);

    private static float AngleDelta(float a, float b)
    {
        var d = MathF.Abs(a - b) % 360f;
        return d > 180f ? 360f - d : d;
    }
}

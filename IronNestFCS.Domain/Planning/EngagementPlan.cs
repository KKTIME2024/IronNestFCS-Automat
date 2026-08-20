using IronNestFCS.Domain.Geometry;
using IronNestFCS.Domain.Observation;

namespace IronNestFCS.Domain.Planning;

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
    string? TrackEntityId, WorldPoint ClusterOffset)
{
    public ShotIntent(
        string shotid, string targetentityid, string displayname,
        WorldPoint impactpoint, float bearingdeg, float distancekm,
        ShellKind shell, ChargePolicy chargepolicy, bool ismoving,
        bool velocityunknown, WorldPoint aimorigin, WorldPoint aimvelocity,
        float aimstarttime, float blastradiuskm, IEnumerable<string> clustermembers,
        string? trackentityid, WorldPoint clusteroffset)
        : this(shotid, targetentityid, displayname, impactpoint, bearingdeg, distancekm,
            shell, chargepolicy, ismoving, velocityunknown, aimorigin, aimvelocity,
            aimstarttime, blastradiuskm, Array.AsReadOnly(clustermembers.ToArray()),
            trackentityid, clusteroffset)
    {
    }
}

public sealed record FireGroup(
    string GroupId, TriggerMode TriggerMode, IReadOnlyList<ShotIntent> Shots,
    bool AllowIndependentFallback)
{
    public FireGroup(string groupid, TriggerMode triggermode, IEnumerable<ShotIntent> shots, bool allowindependentfallback)
        : this(groupid, triggermode, Array.AsReadOnly(shots.ToArray()), allowindependentfallback)
    {
    }
}

public sealed record GroupRelation(string BeforeGroupId, string AfterGroupId, GroupRelationKind Kind);

public sealed record EngagementPlan(
    long SnapshotVersion, PlanSource Source, IReadOnlyList<FireGroup> Groups,
    IReadOnlyList<GroupRelation> Relations, NoPlanReason NoPlanReason)
{
    public EngagementPlan(
        long snapshotversion, PlanSource source, IEnumerable<FireGroup> groups,
        IEnumerable<GroupRelation> relations, NoPlanReason noplreason)
        : this(snapshotversion, source, Array.AsReadOnly(groups.ToArray()),
            Array.AsReadOnly(relations.ToArray()), noplreason)
    {
    }

    public bool HasFire => Groups.Count != 0;
}

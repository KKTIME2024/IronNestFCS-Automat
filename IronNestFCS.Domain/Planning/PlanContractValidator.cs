namespace IronNestFCS.Domain.Planning;

public enum PlanRejection
{
    None,
    StaleSnapshot,
    EmptyGroup,
    DuplicateGroupId,
    DuplicateShotId,
    UnsupportedTriggerMode,
    InvalidIndependentGroup,
    InvalidSameTriggerGroup,
    InvalidRelation
}

public sealed record PlanValidationResult(bool Accepted, PlanRejection Rejection, IReadOnlyList<string> AcceptedGroupIds);

public static class PlanContractValidator
{
    public static PlanValidationResult Validate(EngagementPlan plan, long currentVersion, bool legacyAdapter)
    {
        if (plan.SnapshotVersion != currentVersion)
        {
            return Rejected(PlanRejection.StaleSnapshot);
        }

        var seenGroups = new HashSet<string>(StringComparer.Ordinal);
        var seenShots = new HashSet<string>(StringComparer.Ordinal);
        foreach (var group in plan.Groups)
        {
            if (!seenGroups.Add(group.GroupId))
            {
                return Rejected(PlanRejection.DuplicateGroupId);
            }
            if (group.Shots.Count == 0)
            {
                return Rejected(PlanRejection.EmptyGroup);
            }
            foreach (var shot in group.Shots)
            {
                if (!seenShots.Add(shot.ShotId))
                {
                    return Rejected(PlanRejection.DuplicateShotId);
                }
            }
        }

        foreach (var group in plan.Groups)
        {
            if (group.TriggerMode == TriggerMode.Independent && group.Shots.Count != 1)
            {
                return Rejected(PlanRejection.InvalidIndependentGroup);
            }
            if (group.TriggerMode == TriggerMode.SameTrigger && group.Shots.Count != 2)
            {
                return Rejected(PlanRejection.InvalidSameTriggerGroup);
            }
            if (legacyAdapter && group.TriggerMode != TriggerMode.Independent)
            {
                return Rejected(PlanRejection.UnsupportedTriggerMode);
            }
        }

        foreach (var relation in plan.Relations)
        {
            if (relation.Kind == GroupRelationKind.None)
            {
                continue;
            }
            if (legacyAdapter)
            {
                return Rejected(PlanRejection.InvalidRelation);
            }
            if (relation.BeforeGroupId == relation.AfterGroupId)
            {
                return Rejected(PlanRejection.InvalidRelation);
            }
            if (!seenGroups.Contains(relation.BeforeGroupId) || !seenGroups.Contains(relation.AfterGroupId))
            {
                return Rejected(PlanRejection.InvalidRelation);
            }
        }

        return new PlanValidationResult(true, PlanRejection.None, Array.AsReadOnly(plan.Groups.Select(g => g.GroupId).ToArray()));
    }

    private static PlanValidationResult Rejected(PlanRejection rejection) =>
        new(false, rejection, Array.Empty<string>());
}

using IronNestFCS.Domain.Tests;

var tests = new (string Name, Action Run)[]
{
    ("test runner reports a passing assertion", () => AssertEx.Equal(2, 1 + 1)),
    ("invalid scan is not a valid empty battlefield", BattleSnapshotTests.InvalidScanIsNotAValidEmptyBattlefield),
    ("unknown charge is not zero", BattleSnapshotTests.UnknownChargeIsNotZero),
    ("snapshot copies mutable inputs", BattleSnapshotTests.SnapshotCopiesMutableInputs),
    ("barrel states remain distinct", BattleSnapshotTests.BarrelStatesRemainDistinct),
    ("loaded shell compatibility is explicit", BattleSnapshotTests.LoadedShellCompatibilityIsExplicit),
    ("identity transform keeps point unchanged", BattleSnapshotTests.IdentityTransformKeepsPointUnchanged),
    ("translation transform shifts point", BattleSnapshotTests.TranslationTransformShiftsPoint),
    ("bearing north east west are canonical", BattleSnapshotTests.BearingNorthEastWestAreCanonical),
    ("distance uses world unit scale", BattleSnapshotTests.DistanceUsesWorldUnitScale),
    ("invalid observation never carries death evidence", BattleSnapshotTests.InvalidObservationNeverCarriesDeathEvidence),
    ("stale plan is rejected before any group", PlanContractTests.StalePlanIsRejectedBeforeAnyGroup),
    ("same trigger is atomic and unsupported by legacy adapter", PlanContractTests.SameTriggerIsAtomicAndUnsupportedByLegacyAdapter),
    ("single shot gate authorizes only assigned barrel", PlanContractTests.SingleShotGateAuthorizesOnlyAssignedBarrel),
    ("same trigger gate authorizes both barrels", PlanContractTests.SameTriggerGateAuthorizesBothBarrels),
    ("legacy adapter accepts two independent one-shot groups", PlanContractTests.LegacyAdapterAcceptsTwoIndependentOneShotGroups),
    ("legacy adapter rejects any relation", PlanContractTests.LegacyAdapterRejectsAnyRelation),
    ("legacy adapter rejects independent group with two shots", PlanContractTests.LegacyAdapterRejectsIndependentGroupWithTwoShots),
    ("higher priority wins before mechanical cost", BaselinePlannerTests.HigherPriorityWinsBeforeMechanicalCost),
    ("equal priority uses distance and turret cost", BaselinePlannerTests.EqualPriorityUsesDistanceAndTurretCost),
    ("shell fallback matches master", BaselinePlannerTests.ShellFallbackMatchesMaster),
    ("invalid observation creates no plan", BaselinePlannerTests.InvalidObservationCreatesNoPlan),
    ("pending manual fifo suppresses autonomy", BaselinePlannerTests.PendingManualFifoSuppressesAutonomy),
    ("stable baseline with cbt disabled withholds fdc", BaselinePlannerTests.StableBaselineWithCbtDisabledWithholdsFdc),
    ("both barrels busy plans nothing", BaselinePlannerTests.BothBarrelsBusyPlansNothing),
    ("one barrel busy plans one shot", BaselinePlannerTests.OneBarrelBusyPlansOneShot),
    ("reserved targets are skipped", BaselinePlannerTests.ReservedTargetsAreSkipped),
    ("moving snapshot fields are preserved", BaselinePlannerTests.MovingSnapshotFieldsArePreserved),
    ("fingerprint is invariant under culture", BaselinePlannerTests.FingerprintIsInvariantUnderCulture),
    ("two nearby soft targets become one he group", ClusterPlannerTests.TwoNearbySoftTargetsBecomeOneHeGroup),
    ("four spread targets choose hche", ClusterPlannerTests.FourSpreadTargetsChooseHche),
    ("hche rejected when ratio does not beat he", ClusterPlannerTests.HcheRejectedWhenRatioDoesNotBeatHe),
    ("friendly inside safe radius rejects impact", ClusterPlannerTests.FriendlyInsideSafeRadiusRejectsImpact),
    ("armored and underground never enter clusters", ClusterPlannerTests.ArmoredAndUndergroundNeverEnterClusters),
    ("different motion flags never share moving cluster", ClusterPlannerTests.DifferentMotionFlagsNeverShareMovingCluster),
    ("velocity difference above threshold splits cluster", ClusterPlannerTests.VelocityDifferenceAboveThresholdSplitsCluster),
    ("first cluster blast reserves covered static member", ClusterPlannerTests.FirstClusterBlastReservesCoveredStaticMember),
    ("moving cluster records matching covered entities", ClusterPlannerTests.MovingClusterRecordsMatchingCoveredEntities),
    ("static cluster records unreserved diagnostic members", ClusterPlannerTests.StaticClusterRecordsUnreservedDiagnosticMembers),
    ("identical plans compare clean", FingerprintCompareTests.IdenticalPlansCompareClean),
    ("target difference is reported", FingerprintCompareTests.TargetDifferenceIsReported),
    ("group order difference is reported", FingerprintCompareTests.GroupOrderDifferenceIsReported),
    ("trigger mode difference is reported", FingerprintCompareTests.TriggerModeDifferenceIsReported),
    ("cluster vs single difference is reported", FingerprintCompareTests.ClusterVsSingleDifferenceIsReported),
    ("members order does not matter", FingerprintCompareTests.MembersOrderDoesNotMatter),
    ("impact within tolerance is clean", FingerprintCompareTests.ImpactWithinToleranceIsClean),
    ("impact difference is reported", FingerprintCompareTests.ImpactDifferenceIsReported),
    ("shell difference is reported", FingerprintCompareTests.ShellDifferenceIsReported),
    ("charge policy difference is reported", FingerprintCompareTests.ChargePolicyDifferenceIsReported),
    ("motion snapshot differences are reported", FingerprintCompareTests.MotionSnapshotDifferencesAreReported),
    ("all differences are reported", FingerprintCompareTests.AllDifferencesAreReported),
    ("group count mismatch is reported", FingerprintCompareTests.GroupCountMismatchIsReported),
    ("no plan reason difference is reported", FingerprintCompareTests.NoPlanReasonDifferenceIsReported)
};

var failed = 0;
foreach (var test in tests)
{
    try { test.Run(); Console.WriteLine($"PASS {test.Name}"); }
    catch (Exception ex) { failed++; Console.Error.WriteLine($"FAIL {test.Name}: {ex.Message}"); }
}

Console.WriteLine($"RESULT total={tests.Length} failed={failed}");
return failed == 0 ? 0 : 1;

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
    ("stale plan is rejected before any group", PlanContractTests.StalePlanIsRejectedBeforeAnyGroup),
    ("same trigger is atomic and unsupported by legacy adapter", PlanContractTests.SameTriggerIsAtomicAndUnsupportedByLegacyAdapter),
    ("single shot gate authorizes only assigned barrel", PlanContractTests.SingleShotGateAuthorizesOnlyAssignedBarrel),
    ("same trigger gate authorizes both barrels", PlanContractTests.SameTriggerGateAuthorizesBothBarrels)
};

var failed = 0;
foreach (var test in tests)
{
    try { test.Run(); Console.WriteLine($"PASS {test.Name}"); }
    catch (Exception ex) { failed++; Console.Error.WriteLine($"FAIL {test.Name}: {ex.Message}"); }
}

Console.WriteLine($"RESULT total={tests.Length} failed={failed}");
return failed == 0 ? 0 : 1;

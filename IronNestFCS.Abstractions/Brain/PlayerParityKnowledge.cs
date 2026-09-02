namespace IronNestFCS.Abstractions.Brain;

/// <summary>
/// 只保存玩家可能得到的事实。它没有“从 GameObject 刷新”的入口，
/// 因此隐藏实体位置不能绕过来源审计进入正式 Brain。
/// </summary>
public sealed class PlayerParityKnowledge
{
    private const double DefaultBaseUncertaintyKm = 0.05;
    private const double DefaultUncertaintyGrowthKmPerSecond = 0.001;
    public const double DefaultStaleAfterSeconds = 30.0;
    public const double DefaultLostAfterSeconds = 120.0;
    private readonly Dictionary<string, TrackSnapshot> tracks = new(StringComparer.Ordinal);

    public int Count => tracks.Count;

    /// <summary>应用一条情报；全知调试来源被拒绝且不改变知识库。</summary>
    public bool Apply(TrackIntel intel)
    {
        ArgumentNullException.ThrowIfNull(intel);
        if (!intel.Provenance.IsPlayerParity || string.IsNullOrWhiteSpace(intel.TrackId))
            return false;

        if (tracks.TryGetValue(intel.TrackId, out var previous))
        {
            if (intel.Provenance.ReceivedAtSeconds < previous.LastUpdatedAtSeconds)
                return false;

            tracks[intel.TrackId] = Merge(previous, intel);
        }
        else
        {
            tracks[intel.TrackId] = CreateSnapshot(intel);
        }

        return true;
    }

    /// <summary>批量应用一封电报；每个 track 仍按自身 provenance 审计。</summary>
    public int Apply(TelegramSemanticEvent message)
    {
        ArgumentNullException.ThrowIfNull(message);
        var accepted = 0;
        foreach (var track in message.Tracks)
            if (Apply(track)) accepted++;
        return accepted;
    }

    public bool TryGet(string trackId, out TrackSnapshot? snapshot)
    {
        if (trackId != null && tracks.TryGetValue(trackId, out var found))
        {
            snapshot = found;
            return true;
        }

        snapshot = null;
        return false;
    }

    public IReadOnlyList<TrackSnapshot> Snapshot(double? nowSeconds = null)
    {
        return tracks.Values
            .Select(track => nowSeconds.HasValue
                ? track with { LifecycleState = LifecycleAt(track, nowSeconds.Value) }
                : track)
            .OrderBy(x => x.TrackId, StringComparer.Ordinal)
            .ToArray();
    }

    public bool TryGetLifecycle(
        string trackId,
        double nowSeconds,
        out TrackLifecycleState lifecycleState)
    {
        lifecycleState = TrackLifecycleState.Unknown;
        if (!TryGet(trackId, out var snapshot) || snapshot == null)
            return false;

        lifecycleState = LifecycleAt(snapshot, nowSeconds);
        return true;
    }

    /// <summary>
    /// 用最后一次合法位置和合法恒定航向外推。当前真实实体位置完全不参与计算。
    /// </summary>
    public bool TryEstimate(
        string trackId,
        double nowSeconds,
        out TrackEstimate? estimate,
        double baseUncertaintyKm = DefaultBaseUncertaintyKm,
        double uncertaintyGrowthKmPerSecond = DefaultUncertaintyGrowthKmPerSecond)
        => TryEstimateAt(
            trackId,
            nowSeconds,
            out estimate,
            baseUncertaintyKm,
            uncertaintyGrowthKmPerSecond);

    /// <summary>
    /// 预测到任意未来决策/击发/落地时刻。
    /// forecastAtSeconds 通常由“现在 + 剩余装填/转炮 + 弹丸飞行时间”组成；
    /// 仍然只从最后一份合法报告外推，绝不重采样真实实体。
    /// </summary>
    public bool TryEstimateAt(
        string trackId,
        double forecastAtSeconds,
        out TrackEstimate? estimate,
        double baseUncertaintyKm = DefaultBaseUncertaintyKm,
        double uncertaintyGrowthKmPerSecond = DefaultUncertaintyGrowthKmPerSecond)
    {
        estimate = null;
        if (!TryGet(trackId, out var snapshot) || snapshot == null)
            return false;

        // A parsed telegram clock (for example 10:00:00) is not in the same
        // domain as Unity/episode seconds. Until a runtime adapter explicitly
        // normalizes it, use receipt time as the conservative session anchor.
        var reportTime = snapshot.ReportedTimeBasis == IntelTimeBasis.SessionSeconds
            && snapshot.ReportedAtSeconds.HasValue
            ? snapshot.ReportedAtSeconds.Value
            : snapshot.LastUpdatedAtSeconds;
        var age = Math.Max(0.0, forecastAtSeconds - reportTime);
        MapPoint? position = snapshot.ReportedPosition;
        if (position.HasValue && snapshot.Motion is { } motion)
        {
            var radians = motion.CourseDegrees * Math.PI / 180.0;
            var distance = motion.SpeedKmPerSecond * age;
            position = new MapPoint(
                position.Value.XKm + Math.Sin(radians) * distance,
                position.Value.YKm + Math.Cos(radians) * distance);
        }

        estimate = new TrackEstimate(
            snapshot.TrackId,
            position,
            snapshot.Motion,
            age,
            Math.Max(0.0, baseUncertaintyKm) + Math.Max(0.0, uncertaintyGrowthKmPerSecond) * age,
            snapshot.LatestProvenance);
        return true;
    }

    public void Clear() => tracks.Clear();

    private static TrackSnapshot CreateSnapshot(TrackIntel intel)
    {
        return new TrackSnapshot(
            intel.TrackId,
            intel.Provenance,
            intel.DisplayName,
            intel.ReportedGrid,
            intel.ReportedPosition,
            intel.ReportedDistanceKm,
            intel.ReportedAtSeconds,
            intel.Motion,
            intel.Route.ToArray(),
            intel.EstimatedArrivalSeconds,
            intel.Provenance.ReceivedAtSeconds,
            lifecycleState: DetermineBaseLifecycle(intel),
            destroyedConfirmed: intel.ReportedDestroyed == true)
        {
            ReportedBearingDegrees = intel.ReportedBearingDegrees,
            ReportedTimeBasis = intel.ReportedTimeBasis,
        };
    }

    private static TrackSnapshot Merge(TrackSnapshot previous, TrackIntel intel)
    {
        var conflicts = previous.Conflicts.ToList();
        AddConflict(conflicts, nameof(TrackIntel.ReportedGrid), previous.ReportedGrid, intel.ReportedGrid,
            previous.LatestProvenance, intel.Provenance);
        AddConflict(conflicts, nameof(TrackIntel.ReportedPosition), previous.ReportedPosition, intel.ReportedPosition,
            previous.LatestProvenance, intel.Provenance);
        AddConflict(conflicts, nameof(TrackIntel.ReportedAtSeconds), previous.ReportedAtSeconds, intel.ReportedAtSeconds,
            previous.LatestProvenance, intel.Provenance);
        AddConflict(conflicts, nameof(TrackIntel.Motion), previous.Motion, intel.Motion,
            previous.LatestProvenance, intel.Provenance);

        var destroyedConfirmed = intel.ReportedDestroyed
            ?? previous.DestroyedConfirmed;
        var merged = new TrackIntel(previous.TrackId, intel.Provenance)
        {
            DisplayName = intel.DisplayName ?? previous.DisplayName,
            ReportedGrid = intel.ReportedGrid ?? previous.ReportedGrid,
            ReportedPosition = intel.ReportedPosition ?? previous.ReportedPosition,
            ReportedDistanceKm = intel.ReportedDistanceKm ?? previous.ReportedDistanceKm,
            ReportedBearingDegrees = intel.ReportedBearingDegrees ?? previous.ReportedBearingDegrees,
            ReportedAtSeconds = intel.ReportedAtSeconds ?? previous.ReportedAtSeconds,
            ReportedTimeBasis = intel.ReportedAtSeconds.HasValue
                ? intel.ReportedTimeBasis
                : previous.ReportedTimeBasis,
            Motion = intel.Motion ?? previous.Motion,
            Route = intel.Route.Count == 0 ? previous.Route : intel.Route.ToArray(),
            EstimatedArrivalSeconds = intel.EstimatedArrivalSeconds ?? previous.EstimatedArrivalSeconds,
            ReportedDestroyed = destroyedConfirmed,
        };
        var snapshot = new TrackSnapshot(
            previous.TrackId,
            intel.Provenance,
            merged.DisplayName,
            merged.ReportedGrid,
            merged.ReportedPosition,
            merged.ReportedDistanceKm,
            merged.ReportedAtSeconds,
            merged.Motion,
            merged.Route,
            merged.EstimatedArrivalSeconds,
            intel.Provenance.ReceivedAtSeconds,
            conflicts,
            DetermineBaseLifecycle(merged, destroyedConfirmed),
            destroyedConfirmed)
        {
            ReportedBearingDegrees = merged.ReportedBearingDegrees,
            ReportedTimeBasis = merged.ReportedTimeBasis,
        };
        return snapshot;
    }

    private static TrackLifecycleState DetermineBaseLifecycle(TrackIntel intel, bool? destroyedOverride = null)
    {
        if (destroyedOverride ?? intel.ReportedDestroyed == true)
            return TrackLifecycleState.DestroyedConfirmed;
        if (intel.ReportedPosition.HasValue && intel.Motion != null)
            return TrackLifecycleState.Tracked;
        if (intel.ReportedPosition.HasValue)
            return TrackLifecycleState.Located;
        if (intel.ReportedGrid != null
            || intel.Motion != null
            || intel.ReportedDistanceKm.HasValue
            || intel.ReportedBearingDegrees.HasValue
            || intel.ReportedAtSeconds.HasValue
            || intel.Route.Count > 0
            || intel.EstimatedArrivalSeconds.HasValue
            || intel.DisplayName != null
            || intel.ReportedDestroyed.HasValue)
            return TrackLifecycleState.Reported;
        return TrackLifecycleState.Unknown;
    }

    private static TrackLifecycleState LifecycleAt(TrackSnapshot snapshot, double nowSeconds)
    {
        if (snapshot.DestroyedConfirmed)
            return TrackLifecycleState.DestroyedConfirmed;

        var reportTime = snapshot.ReportedTimeBasis == IntelTimeBasis.SessionSeconds
            && snapshot.ReportedAtSeconds.HasValue
            ? snapshot.ReportedAtSeconds.Value
            : snapshot.LastUpdatedAtSeconds;
        var age = Math.Max(0.0, nowSeconds - reportTime);
        if (age > DefaultLostAfterSeconds)
            return TrackLifecycleState.Lost;
        if (age > DefaultStaleAfterSeconds)
            return TrackLifecycleState.Stale;
        return snapshot.LifecycleState;
    }

    private static void AddConflict(
        ICollection<IntelConflict> conflicts,
        string field,
        object? previous,
        object? next,
        IntelProvenance previousProvenance,
        IntelProvenance nextProvenance)
    {
        if (next == null || previous == null || Equals(previous, next))
            return;

        conflicts.Add(new IntelConflict(
            field,
            Convert.ToString(previous, System.Globalization.CultureInfo.InvariantCulture) ?? "<null>",
            Convert.ToString(next, System.Globalization.CultureInfo.InvariantCulture) ?? "<null>",
            previousProvenance,
            nextProvenance));
    }
}

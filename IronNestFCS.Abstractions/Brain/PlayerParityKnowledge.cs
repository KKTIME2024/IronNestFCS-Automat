namespace IronNestFCS.Abstractions.Brain;

/// <summary>
/// 只保存玩家可能得到的事实。它没有“从 GameObject 刷新”的入口，
/// 因此隐藏实体位置不能绕过来源审计进入正式 Brain。
/// </summary>
public sealed class PlayerParityKnowledge
{
    private const double DefaultBaseUncertaintyKm = 0.05;
    private const double DefaultUncertaintyGrowthKmPerSecond = 0.001;
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

    public IReadOnlyList<TrackSnapshot> Snapshot()
    {
        return tracks.Values
            .OrderBy(x => x.TrackId, StringComparer.Ordinal)
            .ToArray();
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
    {
        estimate = null;
        if (!TryGet(trackId, out var snapshot) || snapshot == null)
            return false;

        var reportTime = snapshot.ReportedAtSeconds ?? snapshot.LastUpdatedAtSeconds;
        var age = Math.Max(0.0, nowSeconds - reportTime);
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
            intel.ReportedAtSeconds,
            intel.Motion,
            intel.Route.ToArray(),
            intel.EstimatedArrivalSeconds,
            intel.KnownHealth,
            intel.DestroyedConfirmed,
            intel.Provenance.ReceivedAtSeconds);
    }

    private static TrackSnapshot Merge(TrackSnapshot previous, TrackIntel intel)
    {
        return new TrackSnapshot(
            previous.TrackId,
            intel.Provenance,
            intel.DisplayName ?? previous.DisplayName,
            intel.ReportedGrid ?? previous.ReportedGrid,
            intel.ReportedPosition ?? previous.ReportedPosition,
            intel.ReportedAtSeconds ?? previous.ReportedAtSeconds,
            intel.Motion ?? previous.Motion,
            intel.Route.Count == 0 ? previous.Route : intel.Route.ToArray(),
            intel.EstimatedArrivalSeconds ?? previous.EstimatedArrivalSeconds,
            intel.KnownHealth ?? previous.KnownHealth,
            intel.DestroyedConfirmed ?? previous.DestroyedConfirmed,
            intel.Provenance.ReceivedAtSeconds);
    }
}

namespace IronNestFCS.Abstractions.Brain;

/// <summary>证明一条情报从玩家可获得的哪一种渠道产生。</summary>
public enum IntelSourceKind
{
    Telegram,
    MapReveal,
    ScoutRecon,
    OmniscientDebug,
}

/// <summary>
/// 情报来源审计信息。Evidence 只应描述玩家实际看到的证据，不能填充隐藏实体真值。
/// </summary>
public sealed record IntelProvenance(
    IntelSourceKind Kind,
    string SourceId,
    double ReceivedAtSeconds,
    string Evidence)
{
    public bool IsPlayerParity => Kind != IntelSourceKind.OmniscientDebug;
}

/// <summary>地图平面上的公里坐标；不依赖 Unity Vector2，便于离线测试。</summary>
public readonly record struct MapPoint(double XKm, double YKm);

/// <summary>电报或合法侦察明确给出的恒定航向运动事实。</summary>
public sealed record MotionIntel(double CourseDegrees, double SpeedKmPerSecond);

/// <summary>路线情报中的一个路标。</summary>
public sealed record RouteWaypointIntel(
    string WaypointId,
    double? DistanceKm = null,
    double? PassageTimeSeconds = null);

/// <summary>
/// 单次合法情报更新。所有可选字段都代表“文本明确说了这个事实”；null 表示文本没有说。
/// </summary>
public sealed record TrackIntel
{
    public TrackIntel(string trackId, IntelProvenance provenance)
    {
        TrackId = trackId ?? throw new ArgumentNullException(nameof(trackId));
        Provenance = provenance ?? throw new ArgumentNullException(nameof(provenance));
    }

    public string TrackId { get; }
    public IntelProvenance Provenance { get; }
    public string? DisplayName { get; init; }
    public string? ReportedGrid { get; init; }
    public MapPoint? ReportedPosition { get; init; }
    public double? ReportedAtSeconds { get; init; }
    public MotionIntel? Motion { get; init; }
    public IReadOnlyList<RouteWaypointIntel> Route { get; init; } = Array.Empty<RouteWaypointIntel>();
    public double? EstimatedArrivalSeconds { get; init; }
    public int? KnownHealth { get; init; }
    public bool? DestroyedConfirmed { get; init; }
}

/// <summary>电报适配器输出的语义事件；不携带 GameObject、MapEntity 或任何 IL2CPP 引用。</summary>
public sealed record TelegramSemanticEvent(
    string SourceId,
    double ReceivedAtSeconds,
    IReadOnlyList<TrackIntel> Tracks,
    string VisibleText)
{
    public IntelProvenance Provenance => new(
        IntelSourceKind.Telegram,
        SourceId,
        ReceivedAtSeconds,
        VisibleText);
}

/// <summary>知识库向 Brain 暴露的只读目标轨迹快照。</summary>
public sealed record TrackSnapshot
{
    public TrackSnapshot(
        string trackId,
        IntelProvenance latestProvenance,
        string? displayName,
        string? reportedGrid,
        MapPoint? reportedPosition,
        double? reportedAtSeconds,
        MotionIntel? motion,
        IReadOnlyList<RouteWaypointIntel> route,
        double? estimatedArrivalSeconds,
        int? knownHealth,
        bool? destroyedConfirmed,
        double lastUpdatedAtSeconds)
    {
        TrackId = trackId;
        LatestProvenance = latestProvenance;
        DisplayName = displayName;
        ReportedGrid = reportedGrid;
        ReportedPosition = reportedPosition;
        ReportedAtSeconds = reportedAtSeconds;
        Motion = motion;
        Route = route;
        EstimatedArrivalSeconds = estimatedArrivalSeconds;
        KnownHealth = knownHealth;
        DestroyedConfirmed = destroyedConfirmed;
        LastUpdatedAtSeconds = lastUpdatedAtSeconds;
    }

    public string TrackId { get; }
    public IntelProvenance LatestProvenance { get; }
    public string? DisplayName { get; }
    public string? ReportedGrid { get; }
    public MapPoint? ReportedPosition { get; }
    public double? ReportedAtSeconds { get; }
    public MotionIntel? Motion { get; }
    public IReadOnlyList<RouteWaypointIntel> Route { get; }
    public double? EstimatedArrivalSeconds { get; }
    public int? KnownHealth { get; }
    public bool? DestroyedConfirmed { get; }
    public double LastUpdatedAtSeconds { get; }
}

/// <summary>移动目标的合法情报外推结果。</summary>
public sealed record TrackEstimate(
    string TrackId,
    MapPoint? EstimatedPosition,
    MotionIntel? Motion,
    double AgeSeconds,
    double UncertaintyKm,
    IntelProvenance BasedOnProvenance);

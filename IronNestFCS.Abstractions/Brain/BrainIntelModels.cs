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
/// 报文中的时间可能是任务时钟，也可能是钟表时间（例如 10:06:50）。
/// 明确区分两者，避免把绝对钟表秒数和 episode 秒数直接相减。
/// </summary>
public enum IntelTimeBasis
{
    SessionSeconds,
    ClockOfDaySeconds,
    Unknown,
}

/// <summary>生命周期由合法报告和报告年龄决定，不由隐藏实体的存在决定。</summary>
public enum TrackLifecycleState
{
    Unknown,
    Reported,
    Located,
    Tracked,
    Stale,
    Lost,
    DestroyedConfirmed,
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

/// <summary>
/// A visible semantic category that the current parser deliberately does not
/// turn into a trusted structured fact. The original text is retained so a
/// later parser extension can be audited against the exact evidence.
/// </summary>
public sealed record UnsupportedSemantic(string Category, string Evidence, string Reason);

/// <summary>路线情报中的一个路标。</summary>
public sealed record RouteWaypointIntel(
    string WaypointId,
    double? DistanceKm = null,
    double? PassageTimeSeconds = null);

/// <summary>两个合法报告对同一公开 track 的事实不一致时保留的审计记录。</summary>
public sealed record IntelConflict(
    string Field,
    string PreviousValue,
    string NewValue,
    IntelProvenance PreviousProvenance,
    IntelProvenance NewProvenance);

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
    /// <summary>电报明确打印的距离（例如未来拦截行的 5KM）。</summary>
    public double? ReportedDistanceKm { get; init; }
    /// <summary>显式的路线/航向方位；不是从隐藏实体速度反推的字段。</summary>
    public double? ReportedBearingDegrees { get; init; }
    public double? ReportedAtSeconds { get; init; }
    public IntelTimeBasis ReportedTimeBasis { get; init; } = IntelTimeBasis.SessionSeconds;
    public MotionIntel? Motion { get; init; }
    public IReadOnlyList<RouteWaypointIntel> Route { get; init; } = Array.Empty<RouteWaypointIntel>();
    public double? EstimatedArrivalSeconds { get; init; }
    public bool? ReportedDestroyed { get; init; }
}

/// <summary>电报适配器输出的语义事件；不携带 GameObject、MapEntity 或任何 IL2CPP 引用。</summary>
public sealed record TelegramSemanticEvent(
    string SourceId,
    double ReceivedAtSeconds,
    IReadOnlyList<TrackIntel> Tracks,
    string VisibleText)
{
    public IReadOnlyList<UnsupportedSemantic> UnsupportedSemantics { get; init; }
        = Array.Empty<UnsupportedSemantic>();

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
        double? reportedDistanceKm,
        double? reportedAtSeconds,
        MotionIntel? motion,
        IReadOnlyList<RouteWaypointIntel> route,
        double? estimatedArrivalSeconds,
        double lastUpdatedAtSeconds,
        IReadOnlyList<IntelConflict>? conflicts = null,
        TrackLifecycleState lifecycleState = TrackLifecycleState.Unknown,
        bool destroyedConfirmed = false)
    {
        TrackId = trackId;
        LatestProvenance = latestProvenance;
        DisplayName = displayName;
        ReportedGrid = reportedGrid;
        ReportedPosition = reportedPosition;
        ReportedDistanceKm = reportedDistanceKm;
        ReportedAtSeconds = reportedAtSeconds;
        Motion = motion;
        Route = route;
        EstimatedArrivalSeconds = estimatedArrivalSeconds;
        LastUpdatedAtSeconds = lastUpdatedAtSeconds;
        Conflicts = conflicts?.ToArray() ?? Array.Empty<IntelConflict>();
        LifecycleState = lifecycleState;
        DestroyedConfirmed = destroyedConfirmed;
    }

    public string TrackId { get; }
    public IntelProvenance LatestProvenance { get; }
    public string? DisplayName { get; }
    public string? ReportedGrid { get; }
    public MapPoint? ReportedPosition { get; }
    public double? ReportedDistanceKm { get; }
    public double? ReportedBearingDegrees { get; init; }
    public double? ReportedAtSeconds { get; }
    public IntelTimeBasis ReportedTimeBasis { get; init; }
    public MotionIntel? Motion { get; }
    public IReadOnlyList<RouteWaypointIntel> Route { get; }
    public double? EstimatedArrivalSeconds { get; }
    public double LastUpdatedAtSeconds { get; }
    public IReadOnlyList<IntelConflict> Conflicts { get; }
    public TrackLifecycleState LifecycleState { get; init; }
    public bool DestroyedConfirmed { get; }
}

/// <summary>移动目标的合法情报外推结果。</summary>
public sealed record TrackEstimate(
    string TrackId,
    MapPoint? EstimatedPosition,
    MotionIntel? Motion,
    double AgeSeconds,
    double UncertaintyKm,
    IntelProvenance BasedOnProvenance);

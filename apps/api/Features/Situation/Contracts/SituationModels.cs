using System.Text.Json.Serialization;

namespace CsDemoMap.Api.Models;

public static class SituationContractVersions
{
    public const string Scene = "minimap-scene-v1";
    public const string Facts = "situation-facts-v1";
    public const string Narrative = "situation-narrative-v1";
    public const string Analysis = "situation-analysis-v1";
}

public static class SituationDataQualityCodes
{
    public const string MissingFrame = "missing-frame";
    public const string StaleFrame = "stale-frame";
    public const string HistoryTruncated = "history-truncated";
    public const string HistoryFileBoundary = "history-file-boundary";
    public const string HistoryRoundBoundary = "history-round-boundary";
    public const string HistoryGap = "history-gap";
    public const string ClockUnknown = "clock-unknown";
    public const string RosterIncomplete = "roster-incomplete";
    public const string EquipmentUnknown = "equipment-unknown";
    public const string UtilityUnknown = "utility-unknown";
    public const string PositionInvalid = "position-invalid";
    public const string PositionOutsideRadar = "position-outside-radar";
    public const string RegionUnknown = "region-unknown";
    public const string FloorUnknown = "floor-unknown";
    public const string EffectStateIncomplete = "effect-state-incomplete";
    public const string LegacyDefaultAmbiguous = "legacy-default-ambiguous";
    public const string HealthStateConflict = "health-state-conflict";
}

[JsonConverter(typeof(JsonStringEnumConverter<SituationSide>))]
public enum SituationSide
{
    [JsonStringEnumMemberName("T")]
    T,
    [JsonStringEnumMemberName("CT")]
    CT
}

[JsonConverter(typeof(JsonStringEnumConverter<SituationSite>))]
public enum SituationSite
{
    [JsonStringEnumMemberName("A")]
    A,
    [JsonStringEnumMemberName("B")]
    B
}

[JsonConverter(typeof(JsonStringEnumConverter<SituationQualitySeverity>))]
public enum SituationQualitySeverity
{
    [JsonStringEnumMemberName("info")]
    Info,
    [JsonStringEnumMemberName("warning")]
    Warning,
    [JsonStringEnumMemberName("error")]
    Error
}

[JsonConverter(typeof(JsonStringEnumConverter<SituationRoundPhase>))]
public enum SituationRoundPhase
{
    [JsonStringEnumMemberName("warmup")]
    Warmup,
    [JsonStringEnumMemberName("team-intro")]
    TeamIntro,
    [JsonStringEnumMemberName("freeze")]
    Freeze,
    [JsonStringEnumMemberName("live")]
    Live,
    [JsonStringEnumMemberName("post-plant")]
    PostPlant,
    [JsonStringEnumMemberName("ended")]
    Ended,
    [JsonStringEnumMemberName("unknown")]
    Unknown
}

[JsonConverter(typeof(JsonStringEnumConverter<SituationFloor>))]
public enum SituationFloor
{
    [JsonStringEnumMemberName("upper")]
    Upper,
    [JsonStringEnumMemberName("lower")]
    Lower,
    [JsonStringEnumMemberName("unknown")]
    Unknown
}

[JsonConverter(typeof(JsonStringEnumConverter<SituationRegionSource>))]
public enum SituationRegionSource
{
    [JsonStringEnumMemberName("demo-place-name")]
    DemoPlaceName,
    [JsonStringEnumMemberName("geometry")]
    Geometry,
    [JsonStringEnumMemberName("unknown")]
    Unknown
}

[JsonConverter(typeof(JsonStringEnumConverter<SituationWeaponCategory>))]
public enum SituationWeaponCategory
{
    [JsonStringEnumMemberName("pistol")]
    Pistol,
    [JsonStringEnumMemberName("smg")]
    Smg,
    [JsonStringEnumMemberName("rifle")]
    Rifle,
    [JsonStringEnumMemberName("sniper")]
    Sniper,
    [JsonStringEnumMemberName("shotgun")]
    Shotgun,
    [JsonStringEnumMemberName("machinegun")]
    Machinegun,
    [JsonStringEnumMemberName("grenade")]
    Grenade,
    [JsonStringEnumMemberName("knife")]
    Knife,
    [JsonStringEnumMemberName("bomb")]
    Bomb,
    [JsonStringEnumMemberName("other")]
    Other,
    [JsonStringEnumMemberName("unknown")]
    Unknown
}

[JsonConverter(typeof(JsonStringEnumConverter<SituationBombState>))]
public enum SituationBombState
{
    [JsonStringEnumMemberName("carried")]
    Carried,
    [JsonStringEnumMemberName("dropped")]
    Dropped,
    [JsonStringEnumMemberName("planting")]
    Planting,
    [JsonStringEnumMemberName("planted")]
    Planted,
    [JsonStringEnumMemberName("defusing")]
    Defusing,
    [JsonStringEnumMemberName("defused")]
    Defused,
    [JsonStringEnumMemberName("exploded")]
    Exploded,
    [JsonStringEnumMemberName("unknown")]
    Unknown
}

[JsonConverter(typeof(JsonStringEnumConverter<SituationUtilityType>))]
public enum SituationUtilityType
{
    [JsonStringEnumMemberName("flash")]
    Flash,
    [JsonStringEnumMemberName("smoke")]
    Smoke,
    [JsonStringEnumMemberName("he")]
    He,
    [JsonStringEnumMemberName("molotov")]
    Molotov,
    [JsonStringEnumMemberName("incendiary")]
    Incendiary,
    [JsonStringEnumMemberName("decoy")]
    Decoy,
    [JsonStringEnumMemberName("unknown")]
    Unknown
}

[JsonConverter(typeof(JsonStringEnumConverter<SituationEffectType>))]
public enum SituationEffectType
{
    [JsonStringEnumMemberName("smoke")]
    Smoke,
    [JsonStringEnumMemberName("fire")]
    Fire
}

[JsonConverter(typeof(JsonStringEnumConverter<SituationFormation>))]
public enum SituationFormation
{
    [JsonStringEnumMemberName("grouped")]
    Grouped,
    [JsonStringEnumMemberName("split")]
    Split,
    [JsonStringEnumMemberName("spread")]
    Spread,
    [JsonStringEnumMemberName("unknown")]
    Unknown
}

[JsonConverter(typeof(JsonStringEnumConverter<SituationContactRisk>))]
public enum SituationContactRisk
{
    [JsonStringEnumMemberName("low")]
    Low,
    [JsonStringEnumMemberName("medium")]
    Medium,
    [JsonStringEnumMemberName("high")]
    High,
    [JsonStringEnumMemberName("unknown")]
    Unknown
}

[JsonConverter(typeof(JsonStringEnumConverter<SituationIsolatedSide>))]
public enum SituationIsolatedSide
{
    [JsonStringEnumMemberName("T")]
    T,
    [JsonStringEnumMemberName("CT")]
    CT,
    [JsonStringEnumMemberName("both")]
    Both,
    [JsonStringEnumMemberName("none")]
    None,
    [JsonStringEnumMemberName("unknown")]
    Unknown
}

[JsonConverter(typeof(JsonStringEnumConverter<SituationSpatialAdvantage>))]
public enum SituationSpatialAdvantage
{
    [JsonStringEnumMemberName("T")]
    T,
    [JsonStringEnumMemberName("CT")]
    CT,
    [JsonStringEnumMemberName("even")]
    Even,
    [JsonStringEnumMemberName("uncertain")]
    Uncertain
}

[JsonConverter(typeof(JsonStringEnumConverter<SituationConfidence>))]
public enum SituationConfidence
{
    [JsonStringEnumMemberName("low")]
    Low,
    [JsonStringEnumMemberName("medium")]
    Medium,
    [JsonStringEnumMemberName("high")]
    High
}

[JsonConverter(typeof(JsonStringEnumConverter<SituationNarrativeSource>))]
public enum SituationNarrativeSource
{
    [JsonStringEnumMemberName("template")]
    Template,
    [JsonStringEnumMemberName("lora")]
    Lora
}

public sealed record SituationVec2(double X, double Y);

public sealed record SituationVec3(double X, double Y, double Z);

public sealed record SituationDataQuality(
    string Code,
    IReadOnlyList<string> FieldPaths,
    SituationQualitySeverity Severity);

public sealed record SituationEvidence(
    string Id,
    IReadOnlyList<string> SourcePaths,
    string RuleId,
    string TextZh);

public sealed record SituationScore(int? T, int? CT);

public sealed record SituationRound(
    string? Ref,
    int? SegmentId,
    int? Number,
    SituationRoundPhase Phase,
    double? ElapsedSeconds,
    double? RemainingSeconds,
    SituationScore Score,
    string? ClockSource);

public sealed record SituationUtilityCounts(
    int? Flash,
    int? Smoke,
    int? He,
    int? Molotov,
    int? Incendiary,
    int? Decoy);

public sealed record SituationWeaponCounts(
    int? Pistol,
    int? Smg,
    int? Rifle,
    int? Sniper,
    int? Shotgun,
    int? Machinegun,
    int? Grenade,
    int? Knife,
    int? Bomb,
    int? Other,
    int? Unknown);

public sealed record SituationTrajectoryPoint(int Tick, SituationVec3 Position);

public sealed record SituationTrajectory(
    int FromTick,
    int ToTick,
    double CoverageSeconds,
    IReadOnlyList<SituationTrajectoryPoint> Points);

public sealed record SituationPlayer(
    string Slot,
    SituationSide Side,
    SituationVec3? Position,
    SituationFloor Floor,
    string? Region,
    SituationRegionSource RegionSource,
    SituationVec2? Heading,
    SituationVec3? Velocity,
    int? Health,
    int? Armor,
    SituationWeaponCategory WeaponCategory,
    SituationUtilityCounts UtilityCounts,
    SituationTrajectory Trajectory);

public sealed record SituationTeamSummary(
    int? Alive,
    int? TotalHealth,
    int? TotalArmor,
    int? TotalMoney,
    int? EquipmentValue,
    int? HelmetCount,
    int? DefuserCount,
    SituationUtilityCounts UtilityCounts,
    SituationWeaponCounts WeaponCounts);

public sealed record SituationTeams(SituationTeamSummary T, SituationTeamSummary CT);

public sealed record SituationBomb(
    SituationBombState State,
    string? CarrierSlot,
    string? DefuserSlot,
    SituationSite? Site,
    SituationVec3? Position,
    string? Region,
    SituationRegionSource RegionSource,
    double? SecondsToExplosion,
    double? SecondsToDefuse);

public sealed record SituationUtility(
    string Id,
    SituationUtilityType Type,
    SituationSide? Side,
    string? ThrowerSlot,
    int ObservedStartTick,
    SituationVec3? Position,
    SituationTrajectory Trajectory);

public sealed record SituationEffect(
    string Id,
    SituationEffectType Type,
    SituationSide? Side,
    int ObservedStartTick,
    int SampleTick,
    SituationVec3? Position,
    double? Radius,
    IReadOnlyList<SituationVec3>? Area);

public sealed record SituationRegionOccupancy(string Region, int TAlive, int CTAlive);

public sealed record SituationPlayerDistances(
    string Slot,
    double? SiteA,
    double? SiteB,
    double? NearestAlly,
    double? NearestEnemy);

public sealed record SituationGeometry(
    IReadOnlyList<SituationRegionOccupancy> RegionOccupancy,
    IReadOnlyList<SituationPlayerDistances> PlayerDistances);

public sealed record MinimapSceneV1(
    string SchemaVersion,
    string SceneBuilderVersion,
    string GeometryVersion,
    string Source,
    string Map,
    string DemoRef,
    int WindowIndex,
    int RequestedTick,
    int Tick,
    int TickRate,
    SituationRound Round,
    IReadOnlyList<SituationPlayer> Players,
    SituationTeams Teams,
    SituationBomb Bomb,
    IReadOnlyList<SituationUtility> Utilities,
    IReadOnlyList<SituationEffect> Effects,
    SituationGeometry Geometry,
    IReadOnlyList<SituationDataQuality> DataQuality);

public sealed record SituationSideValues<TValue>(TValue T, TValue CT);

public sealed record SituationFactBomb(SituationBombState State, SituationSite? Site);

public sealed record SituationPressure(SituationSide? Side, SituationSite? Site);

public sealed record SituationFactsV1(
    string SchemaVersion,
    string AnalysisRuleVersion,
    SituationSideValues<int?> Alive,
    SituationSideValues<int?> TotalHealth,
    SituationFactBomb Bomb,
    SituationSideValues<SituationFormation> Formation,
    SituationPressure Pressure,
    IReadOnlyList<string>? ContestedRegions,
    SituationContactRisk ContactRisk,
    SituationIsolatedSide IsolatedSide,
    SituationSpatialAdvantage SpatialAdvantage,
    SituationConfidence Confidence,
    IReadOnlyList<SituationEvidence> Evidence,
    IReadOnlyList<SituationDataQuality> DataQuality);

public sealed record SituationHighlight(string TextZh, IReadOnlyList<string> EvidenceIds);

public sealed record SituationNarrativeV1(
    string SchemaVersion,
    IReadOnlyList<SituationHighlight> Highlights,
    string SummaryZh,
    IReadOnlyList<string> Uncertainties);

public sealed record SituationAnalysisVersions(
    string Scene,
    string Builder,
    string Geometry,
    string Facts,
    string Rules,
    string Narrative);

public sealed record SituationModelMetadata(string BaseRevision, string AdapterSha256);

public sealed record SituationAnalysisV1(
    string SchemaVersion,
    int RequestedTick,
    int Tick,
    DateTimeOffset GeneratedAt,
    SituationAnalysisVersions Versions,
    SituationModelMetadata? Model,
    SituationNarrativeSource NarrativeSource,
    SituationFactsV1 Facts,
    SituationNarrativeV1 Narrative);

namespace CsDemoMap.Api.Models;

public sealed record DemoTimeline(
    DemoMetadata Metadata,
    IReadOnlyList<DemoFrame> Frames,
    IReadOnlyList<UtilityTrack> UtilityTracks,
    IReadOnlyList<UtilityEffectTrack> UtilityEffects,
    IReadOnlyList<PlayerUtilityState> PlayerUtilityStates,
    IReadOnlyList<PlayerEquipmentState> PlayerEquipmentStates,
    IReadOnlyList<TimelineEvent> Events,
    IReadOnlyList<RoundResult> RoundResults);

public sealed record DemoMetadata(
    string FileName,
    string MapName,
    int TickRate,
    int SampleRate,
    int TotalTicks,
    double DurationSeconds);

public sealed record DemoFrame(
    int Tick,
    double TimeSeconds,
    IReadOnlyList<PlayerSnapshot> Players,
    RoundSnapshot Round,
    BombSnapshot Bomb,
    IReadOnlyList<MapZoneOccupancy> Zones);

public sealed record RoundSnapshot(
    int Number,
    string Phase,
    int ScoreT,
    int ScoreCT,
    double ElapsedSeconds,
    double RemainingSeconds,
    int ConsecutiveLossesT,
    int ConsecutiveLossesCT);

public sealed record RoundResult(
    int RoundNumber,
    int StartTick,
    int LiveTick,
    int EndTick,
    string WinnerSide,
    string EndReason);

public sealed record BombSnapshot(
    string State,
    string? CarrierId,
    string? DefuserId,
    string? Site,
    string? Region,
    float? X,
    float? Y,
    float? Z,
    double? SecondsToExplosion,
    double? SecondsToDefuse);

public sealed record MapZoneOccupancy(
    string Region,
    int TAlive,
    int CTAlive,
    int TTotal,
    int CTTotal);

public sealed record PlayerSnapshot(
    string Id,
    string Name,
    string Team,
    bool Alive,
    int Health,
    float X,
    float Y,
    float Z,
    float Yaw,
    float VelocityX,
    float VelocityY,
    float VelocityZ,
    string Region,
    string? Weapon,
    int Kills,
    int Deaths);

public sealed record UtilityPoint(
    int Tick,
    double TimeSeconds,
    float X,
    float Y,
    float Z);

public sealed record UtilityTrack(
    string Id,
    string Type,
    string? ThrowerId,
    string? ThrowerName,
    string Team,
    int StartTick,
    int EndTick,
    int? DetonateTick,
    IReadOnlyList<UtilityPoint> Trajectory);

public sealed record UtilityAreaPoint(float X, float Y, float Z);

public sealed record UtilityEffectSample(
    int Tick,
    double TimeSeconds,
    float X,
    float Y,
    float Z,
    float Radius,
    IReadOnlyList<UtilityAreaPoint> Area);

public sealed record UtilityEffectTrack(
    string Id,
    string Type,
    string? ThrowerId,
    string? ThrowerName,
    string Team,
    int StartTick,
    int EndTick,
    IReadOnlyList<UtilityEffectSample> Samples);

public sealed record CarriedUtility(string Type, int Count);

public sealed record PlayerUtilityState(
    int Tick,
    double TimeSeconds,
    string PlayerId,
    IReadOnlyList<CarriedUtility> Items);

public sealed record EquipmentItem(
    string Name,
    string Category,
    int Count,
    int ClipAmmo,
    int ReserveAmmo);

public sealed record PlayerEquipmentState(
    int Tick,
    double TimeSeconds,
    string PlayerId,
    int Money,
    int Armor,
    bool HasHelmet,
    bool HasDefuser,
    int CurrentEquipmentValue,
    int RoundStartEquipmentValue,
    int CashSpentThisRound,
    IReadOnlyList<EquipmentItem> Items);

public sealed record TimelineEvent(
    int Tick,
    double TimeSeconds,
    string Type,
    string Title,
    string? Detail = null);

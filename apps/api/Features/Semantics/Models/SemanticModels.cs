namespace CsDemoMap.Api.Models;

// Separate from the legacy replay/v3 contract. Unknown values stay nullable.
public sealed record RoundRulesObservation(
    int Tick, double GameTime, double RoundStartTime, int RoundTime,
    int RawRound, int TotalRoundsPlayed, int ScoreT, int ScoreCT,
    bool Warmup, bool MatchStarted, bool Freeze, bool TeamIntro,
    int StartCount, int EndCount, bool GamePaused, bool TechnicalTimeout,
    bool WaitingForResume, bool TTimeout, bool CTTimeout, int TotalPausedTicks);

public sealed record RoundSignals(bool Start, bool Live, bool End, string? Winner, string? Reason);

public sealed class RoundAttempt
{
    public required string RoundId { get; init; }
    public int SegmentId { get; init; }
    public int? RoundNumber { get; set; }
    public int StartTick { get; init; }
    public int? LiveTick { get; set; }
    public int? EndTick { get; set; }
    public int ScoreTAtLive { get; set; }
    public int ScoreCTAtLive { get; set; }
    public string Disposition { get; set; } = "pending";
    public string? Winner { get; set; }
    public string? Reason { get; set; }
}

public sealed record SemanticClock(
    double? LiveElapsedSeconds, double? RoundRemainingSeconds,
    double? BombRemainingSeconds, double? DefuseRemainingSeconds,
    bool ClockKnown, string ClockSource, string PauseContext);

public sealed record RosterObservation(
    string Id, string Team, bool? Alive, bool PositionKnown, bool EquipmentKnown);

public sealed record RosterQuality(
    int TMembers, int CTMembers, int LifeKnown, int AliveKnown,
    int AlivePositionKnown, int AliveEquipmentKnown, bool RosterKnown,
    string Quality, IReadOnlyList<string> Reasons);

public sealed record SemanticFrame(
    int Tick, string? RoundId, int? RoundNumber, int SegmentId, string Phase,
    SemanticClock Clock, RosterQuality Roster);

public sealed record SemanticAudit(
    RoundRulesObservation Rules, RoundSignals Signals, string? RoundId,
    int? RoundNumber, int SegmentId, string Phase);

public sealed record SemanticTimeline(
    IReadOnlyList<RoundAttempt> Attempts,
    IReadOnlyList<SemanticFrame> Frames,
    IReadOnlyList<SemanticAudit> Audit);

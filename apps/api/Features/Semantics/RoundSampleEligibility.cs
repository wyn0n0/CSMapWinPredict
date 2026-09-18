using CsDemoMap.Api.Models;

namespace CsDemoMap.Api.Services;

internal sealed record RoundSampleEligibilityResult(
    bool Eligible,
    string? ReasonCode);

/// <summary>
/// The shared schema v4.2/stage-four gate for a semantic round frame.
/// Outcome fields deliberately never enter this contract.
/// </summary>
internal static class RoundSampleEligibility
{
    internal const string UnsupportedMap = "unsupported-map";
    internal const string RoundNotCompleted = "round-not-completed";
    internal const string RoundAttemptMismatch = "round-attempt-mismatch";
    internal const string RoundBoundaryUnknown = "round-boundary-unknown";
    internal const string TickOutsideLiveRange = "tick-outside-live-range";
    internal const string PhaseNotEligible = "phase-not-eligible";
    internal const string RoundUnconfirmed = "round-unconfirmed";
    internal const string SnapshotTickMismatch = "snapshot-tick-mismatch";
    internal const string AliveSnapshotMismatch = "alive-snapshot-mismatch";

    internal static RoundSampleEligibilityResult EvaluateRoundTick(
        string mapName,
        RoundAttempt attempt,
        SemanticFrame frame)
    {
        ArgumentNullException.ThrowIfNull(mapName);
        ArgumentNullException.ThrowIfNull(attempt);
        ArgumentNullException.ThrowIfNull(frame);

        if (!string.Equals(mapName, "de_mirage", StringComparison.Ordinal))
            return Reject(UnsupportedMap);
        if (!string.Equals(attempt.Disposition, "completed", StringComparison.Ordinal))
            return Reject(RoundNotCompleted);
        if (!string.Equals(frame.RoundId, attempt.RoundId, StringComparison.Ordinal))
            return Reject(RoundAttemptMismatch);
        if (attempt.LiveTick is not { } liveTick || attempt.EndTick is not { } endTick ||
            endTick <= liveTick)
            return Reject(RoundBoundaryUnknown);
        if (frame.Tick < liveTick || frame.Tick >= endTick)
            return Reject(TickOutsideLiveRange);
        if (frame.Phase is not ("live" or "post-plant"))
            return Reject(PhaseNotEligible);
        return Accept();
    }

    internal static RoundSampleEligibilityResult Evaluate(
        string mapName,
        RoundAttempt attempt,
        SemanticFrame frame,
        DemoFrame correctedSnapshot)
    {
        ArgumentNullException.ThrowIfNull(correctedSnapshot);
        var roundTick = EvaluateRoundTick(mapName, attempt, frame);
        if (!roundTick.Eligible)
            return roundTick;
        if (correctedSnapshot.Tick != frame.Tick)
            return Reject(SnapshotTickMismatch);

        // Keep this order and these values byte-compatible with schema v4.2.
        if (frame.RoundNumber is null)
            return Reject(RoundUnconfirmed);
        if (string.Equals(frame.Roster.Quality, "unusable", StringComparison.Ordinal))
            return Reject(string.Join('+', frame.Roster.Reasons));
        if (!frame.Clock.ClockKnown)
            return Reject(frame.Clock.ClockSource);
        if (CountCorrectedAlivePlayers(correctedSnapshot) != frame.Roster.AliveKnown)
            return Reject(AliveSnapshotMismatch);
        return Accept();
    }

    internal static int CountCorrectedAlivePlayers(DemoFrame snapshot) => snapshot.Players.Count(
        player => player.Alive && player.Team is "T" or "CT");

    private static RoundSampleEligibilityResult Accept() => new(true, null);

    private static RoundSampleEligibilityResult Reject(string reasonCode) => new(false, reasonCode);
}

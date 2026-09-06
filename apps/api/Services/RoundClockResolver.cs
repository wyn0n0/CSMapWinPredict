using CsDemoMap.Api.Models;

namespace CsDemoMap.Api.Services;

public sealed class RoundClockResolver
{
    private string? roundId;
    private bool unsupportedPauseSeen;
    private int? pausedTicksAtStart;
    private bool enteredLive;

    public SemanticClock Resolve(RoundRulesObservation r, string? id, string phase,
        double? explosion = null, double? defuse = null)
    {
        if (id != roundId)
        {
            roundId = id;
            enteredLive = false;
            unsupportedPauseSeen = false;
            pausedTicksAtStart = r.TotalPausedTicks;
        }
        // Tactical waiting can be armed during a running round (real 9z/faze trace).
        var hardPause = r.GamePaused || r.TechnicalTimeout ||
            (r.WaitingForResume && !r.TTimeout && !r.CTTimeout);
        if (hardPause || (pausedTicksAtStart is not null && r.TotalPausedTicks != pausedTicksAtStart))
            unsupportedPauseSeen = true;
        var elapsedAtBoundary = r.GameTime - r.RoundStartTime;
        if (!enteredLive && phase is "live" or "post-plant")
        {
            enteredLive = true;
            // A newly observed live origin is independent of preceding freeze waits.
            if (!hardPause && double.IsFinite(elapsedAtBoundary) &&
                elapsedAtBoundary >= 0 && elapsedAtBoundary <= 0.125)
            {
                unsupportedPauseSeen = false;
                pausedTicksAtStart = r.TotalPausedTicks;
            }
        }
        var context = unsupportedPauseSeen ? "unverified-hard-pause-or-resume"
            : r.TTimeout || r.CTTimeout ? "tactical-timeout-flag" : "none";
        var elapsed = r.GameTime - r.RoundStartTime;
        var valid = id is not null && phase is "live" or "post-plant" &&
            !unsupportedPauseSeen && double.IsFinite(elapsed) && elapsed >= 0 &&
            r.RoundTime > 0;
        if (!valid)
            return new(null, null, null, null, false,
                unsupportedPauseSeen ? "unsupported-pause" : "not-applicable-or-unknown", context);

        return new(elapsed, phase == "live" ? Math.Max(0, r.RoundTime - elapsed) : null,
            phase == "post-plant" ? FiniteNonnegative(explosion) : null,
            phase == "post-plant" ? FiniteNonnegative(defuse) : null,
            true, "game-time-minus-rule-start", context);
    }

    private static double? FiniteNonnegative(double? value) =>
        value is { } v && double.IsFinite(v) && v >= 0 ? v : null;
}

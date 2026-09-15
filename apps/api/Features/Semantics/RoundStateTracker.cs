using CsDemoMap.Api.Models;

namespace CsDemoMap.Api.Services;

public sealed class RoundStateTracker
{
    private readonly List<RoundAttempt> attempts = [];
    private int serial;
    private int segment;
    private int? lastStartCount;
    private int highestLiveNumber;
    public RoundAttempt? Current { get; private set; }
    public IReadOnlyList<RoundAttempt> Attempts => attempts;
    public string Phase { get; private set; } = "unknown";

    // All signals belong to one command boundary. End is resolved against the old
    // attempt before a simultaneous start can replace it (9z/faze tick 62790).
    public void Observe(RoundRulesObservation r, RoundSignals signals)
    {
        if (signals.End && Current is { EndTick: null } ending)
        {
            ending.EndTick = r.Tick;
            ending.Winner = signals.Winner;
            ending.Reason = signals.Reason;
            ending.Disposition = signals.Winner is "T" or "CT"
                ? ending.LiveTick is not null && ending.RoundNumber is not null &&
                  ending.LiveTick < r.Tick && !r.Warmup && r.TotalRoundsPlayed == ending.RoundNumber &&
                  r.ScoreT == ending.ScoreTAtLive + (signals.Winner == "T" ? 1 : 0) &&
                  r.ScoreCT == ending.ScoreCTAtLive + (signals.Winner == "CT" ? 1 : 0)
                    ? "completed" : "result-unconfirmed"
                : "void";
        }

        var newStart = signals.Start && (Current is null || lastStartCount != r.StartCount);
        if (newStart)
        {
            if (Current is { EndTick: null } replaced)
            {
                replaced.EndTick = r.Tick;
                replaced.Disposition = "void";
                replaced.Reason = "replaced-before-result";
            }
            Current = NewAttempt(r.Tick);
            lastStartCount = r.StartCount;
        }

        var competitive = !r.Warmup && r.MatchStarted && !r.TeamIntro;
        if (signals.Live && competitive && !r.Freeze)
        {
            if (Current is null || Current.EndTick is not null)
                Current = NewAttempt(r.Tick); // Partial demo: start not observed.
            if (Current.LiveTick is null)
            {
                var candidate = r.RawRound + 1;
                if (r.RawRound >= 0 && r.RawRound == r.TotalRoundsPlayed &&
                    r.ScoreT + r.ScoreCT == r.TotalRoundsPlayed)
                {
                    if (candidate < highestLiveNumber || attempts.Any(a => a.SegmentId == segment &&
                        a.Disposition == "completed" && a.RoundNumber == candidate))
                    {
                        // A scoreboard rollback supersedes results at/after its restore
                        // point. Preserve earlier rounds, but do not train on the replaced
                        // result even when it was temporarily scored (pain/astralis).
                        foreach (var superseded in attempts.Where(a => a.SegmentId == segment &&
                            a.Disposition == "completed" && a.RoundNumber >= candidate))
                        {
                            superseded.Disposition = "void";
                            superseded.Reason = "score-rollback:" + superseded.Reason;
                        }
                        segment++;
                        // The attempt's immutable identity remains stable; no frames
                        // from its live phase have been emitted yet.
                        var old = Current;
                        old.Disposition = "void";
                        old.EndTick = r.Tick;
                        old.Reason = "rules-score-reset";
                        Current = NewAttempt(r.Tick);
                        highestLiveNumber = 0;
                    }
                    Current.RoundNumber = candidate;
                    highestLiveNumber = Math.Max(highestLiveNumber, candidate);
                }
                Current.LiveTick = r.Tick;
                Current.ScoreTAtLive = r.ScoreT;
                Current.ScoreCTAtLive = r.ScoreCT;
            }
        }

        Phase = r.Warmup ? "warmup" : r.TeamIntro ? "team-intro"
            : !r.MatchStarted ? "unknown"
            : Current?.EndTick is not null ? "ended"
            : r.Freeze || Current?.LiveTick is null ? "freeze" : "live";
    }

    public void Finish()
    {
        if (Current is { EndTick: null } pending)
        {
            pending.Disposition = "result-missing";
            pending.Reason = "end-of-demo";
        }
    }

    private RoundAttempt NewAttempt(int tick)
    {
        var attempt = new RoundAttempt
        {
            RoundId = $"s{segment}-a{++serial}", SegmentId = segment, StartTick = tick
        };
        attempts.Add(attempt);
        return attempt;
    }
}

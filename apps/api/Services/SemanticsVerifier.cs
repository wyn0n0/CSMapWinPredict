using CsDemoMap.Api.Models;

namespace CsDemoMap.Api.Services;

internal static class SemanticsVerifier
{
    public static void Verify()
    {
        var checks = 0;
        var tracker = new RoundStateTracker();
        var none = new RoundSignals(false, false, false, null, null);
        var start = none with { Start = true };
        var live = none with { Live = true };
        var r = Rules(100, 7);
        tracker.Observe(r, start);
        tracker.Observe(r with { Tick = 200, Freeze = false }, live);
        Check(tracker.Current?.RoundNumber == 8, "rule-based round number");
        var firstId = tracker.Current!.RoundId;
        tracker.Observe(r with { Tick = 62790, StartCount = 15 },
            new(true, false, true, null, "RoundDraw"));
        Check(tracker.Attempts[0].Disposition == "void", "simultaneous draw closes old attempt");
        Check(tracker.Current!.RoundId != firstId && tracker.Phase == "freeze", "new attempt after draw");
        tracker.Observe(r with { Tick = 64982, StartCount = 15, Freeze = false }, live);
        Check(tracker.Current.RoundNumber == 8, "restart does not increment official number");
        tracker.Observe(r with { Tick = 70000, Freeze = false, TotalRoundsPlayed = 8, ScoreT = 8 },
            none with { End = true, Winner = "T", Reason = "TerroristsWin" });
        Check(tracker.Current.Disposition == "completed", "valid result");
        tracker.Observe(r with { Tick = 70000, Freeze = false }, none with { End = true, Winner = "CT" });
        Check(tracker.Current.Winner == "T", "duplicate end is idempotent");
        tracker.Observe(Rules(73912, 8) with { StartCount = 16 }, start);
        tracker.Observe(Rules(75000, 8) with { StartCount = 16, Freeze = false }, live);
        Check(tracker.Current.RoundNumber == 9, "round after restart");
        var count = tracker.Attempts.Count;
        tracker.Observe(Rules(75000, 8) with { StartCount = 16, Freeze = false }, start);
        Check(tracker.Attempts.Count == count, "duplicate start is idempotent");
        tracker.Finish();
        Check(tracker.Current.Disposition == "result-missing", "truncated result excluded");

        var warmup = new RoundStateTracker();
        warmup.Observe(Rules(0, 0) with { Warmup = true, MatchStarted = false }, start);
        warmup.Observe(Rules(64, 0) with { Warmup = true, MatchStarted = false, Freeze = false }, live);
        Check(warmup.Current?.LiveTick is null && warmup.Phase == "warmup", "warmup freeze end excluded");
        var mismatch = new RoundStateTracker();
        mismatch.Observe(Rules(64, 4) with { Freeze = false, ScoreT = 3 }, live);
        Check(mismatch.Current?.RoundNumber is null, "conflicting rules are unknown");
        foreach (var number in new[] { 12, 24, 30 })
        {
            var phase = new RoundStateTracker();
            phase.Observe(Rules(0, number), start);
            phase.Observe(Rules(64, number) with { Freeze = false }, live);
            Check(phase.Current?.RoundNumber == number + 1, "half/overtime numbering");
        }

        var rollback = new RoundStateTracker();
        rollback.Observe(Rules(0, 0), start);
        rollback.Observe(Rules(64, 0) with { Freeze = false }, live);
        rollback.Observe(Rules(128, 0) with { Freeze = false, TotalRoundsPlayed = 1, ScoreT = 1 },
            none with { End = true, Winner = "T" });
        rollback.Observe(Rules(192, 0) with { StartCount = 15 }, start);
        rollback.Observe(Rules(256, 0) with { StartCount = 15, Freeze = false }, live);
        Check(rollback.Current?.SegmentId == 1 && rollback.Current.RoundNumber == 1,
            "replayed completed round gets a new segment");
        Check(rollback.Attempts[0].Disposition == "void", "score rollback invalidates superseded result");
        var conflict = new RoundStateTracker();
        conflict.Observe(Rules(0, 0), start);
        conflict.Observe(Rules(64, 0) with { Freeze = false }, live);
        conflict.Observe(Rules(128, 0) with { Freeze = false, TotalRoundsPlayed = 1, ScoreCT = 1 },
            none with { End = true, Winner = "T" });
        Check(conflict.Current?.Disposition == "result-unconfirmed", "score/winner conflict excluded");
        var clock = new RoundClockResolver();
        var frozen = Rules(50000, 0) with { GameTime = 100, RoundStartTime = 105 };
        Check(clock.Resolve(frozen, "a", "freeze").LiveElapsedSeconds is null, "freeze is not live time");
        clock.Resolve(frozen with { WaitingForResume = true }, "a", "freeze");
        var liveRules = frozen with { Freeze = false, GameTime = 1000, RoundStartTime = 1000 };
        Check(clock.Resolve(liveRules, "a", "live").LiveElapsedSeconds == 0, "long freeze resets live clock");
        Check(clock.Resolve(liveRules with { GameTime = 1005, TTimeout = true }, "a", "live").LiveElapsedSeconds == 5,
            "early tactical timeout flag does not stop clock");
        Check(clock.Resolve(liveRules with { GameTime = 1010, TTimeout = true, WaitingForResume = true }, "a", "live").LiveElapsedSeconds == 10, "tactical waiting while game time advances");
        var planted = clock.Resolve(liveRules, "a", "post-plant", 35, 7);
        Check(planted.RoundRemainingSeconds is null && planted.BombRemainingSeconds == 35 && planted.DefuseRemainingSeconds == 7,
            "separate plant and defuse clocks");
        Check(clock.Resolve(liveRules, "a", "post-plant", 33).DefuseRemainingSeconds is null, "cancel defuse clears timer");
        Check(!clock.Resolve(liveRules with { GamePaused = true }, "a", "live").ClockKnown, "hard pause unknown");
        Check(!clock.Resolve(liveRules, "a", "live").ClockKnown, "unverified resume remains unknown");
        Check(clock.Resolve(liveRules, "b", "live").ClockKnown, "next round resets pause uncertainty");
        Check(!clock.Resolve(liveRules with { TotalPausedTicks = 10 }, "b", "live").ClockKnown, "paused tick change invalidates clock");

        var roster = new RoundRosterTracker();
        var players = Enumerable.Range(1, 10).Select(i => new RosterObservation(i.ToString(), i <= 5 ? "T" : "CT", true, true, true)).ToArray();
        Check(roster.Observe(players).Quality == "usable", "complete roster");
        Check(roster.Observe(players.Skip(1)).Quality == "unusable", "missing alive pawn is unknown");
        roster.RecordDeath("1");
        var dead = roster.Observe(players.Skip(1));
        Check(dead.Quality == "usable" && dead.LifeKnown == 10 && dead.AliveKnown == 9, "confirmed dead persists without pawn");
        var partial = roster.Observe(players.Skip(1).Select(p => p with { EquipmentKnown = false }));
        Check(partial.Quality == "partial" && partial.AliveEquipmentKnown == 0, "missing equipment not zero resource");
        roster.Reset();
        Check(roster.Observe(players.Skip(1)).RosterKnown == false, "nine observed players not full coverage");
        roster.Reset();
        Check(roster.Observe(players).AliveKnown == 10, "round reset clears deaths");

        roster.RecordDeath("1", "freeze");
        Check(roster.Observe(players).AliveKnown == 10, "freeze death followed by live respawn is not persistent");
        Console.WriteLine($"Semantic checks passed: {checks}");
        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
            checks++;
        }
    }

    private static RoundRulesObservation Rules(int tick, int raw) => new(
        tick, tick / 64d, tick / 64d, 115, raw, raw, raw, 0,
        false, true, true, false, 14, 1, false, false, false, false, false, 0);
}

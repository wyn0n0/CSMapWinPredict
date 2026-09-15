using DemoFile;
using CsDemoMap.Api.Models;
using DemoFile.Game.Cs;

namespace CsDemoMap.Api.Services;

// Opt-in observer: legacy replay and schema-v3 exports keep their behavior.
internal sealed class DemoSemanticCollector
{
    private readonly RoundStateTracker rounds = new();
    private readonly RoundClockResolver clock = new();
    private readonly RoundRosterTracker roster = new();
    private readonly List<SemanticFrame> frames = [];
    private readonly List<SemanticAudit> audit = [];
    private readonly List<string> pendingDeaths = [];
    private RoundSignals pending = new(false, false, false, null, null);
    private RoundRulesObservation? rules;
    private string? rosterRound;
    private int lastAuditSecond = -1;
    private string? lastPause;

    public void Start() => pending = pending with { Start = true };
    public void Live() => pending = pending with { Live = true };
    public void End(string? winner, string reason) => pending = pending with
        { End = true, Winner = winner, Reason = reason };
    public void Death(string? id) { if (id is not null) pendingDeaths.Add(id); }

    public bool Command(CsDemoParser demo)
    {
        var g = demo.GameRules;
        rules = new RoundRulesObservation(demo.CurrentDemoTick.Value,
            demo.CurrentGameTime.Value, g.RoundStartTime.Value, g.RoundTime,
            g.RoundStartRoundNumber, g.TotalRoundsPlayed,
            demo.TeamTerrorist.Score, demo.TeamCounterTerrorist.Score,
            g.WarmupPeriod, g.HasMatchStarted, g.FreezePeriod, g.TeamIntroPeriod,
            g.RoundStartCount, g.RoundEndCount, g.GamePaused, g.TechnicalTimeOut,
            g.MatchWaitingForResume, g.TerroristTimeOutActive, g.CTTimeOutActive,
            g.TotalPausedTicks);
        rounds.Observe(rules, pending);
        var changed = rosterRound != rounds.Current?.RoundId;
        if (changed) { roster.Reset(); rosterRound = rounds.Current?.RoundId; }
        if (!changed) foreach (var id in pendingDeaths) roster.RecordDeath(id, rounds.Phase);
        pendingDeaths.Clear();
        var resolved = clock.Resolve(rules, rounds.Current?.RoundId, rounds.Phase);
        var second = rules.Tick / 64;
        if (pending.Start || pending.Live || pending.End || second != lastAuditSecond ||
            resolved.PauseContext != lastPause)
        {
            audit.Add(new(rules, pending, rounds.Current?.RoundId,
                rounds.Current?.RoundNumber, rounds.Current?.SegmentId ?? 0, rounds.Phase));
            lastAuditSecond = second;
            lastPause = resolved.PauseContext;
        }
        pending = new(false, false, false, null, null);
        return changed;
    }

    public void Capture(CsDemoParser demo, DemoFrame frame)
    {
        if (rules is null) return;
        var observations = demo.Players.Select(p =>
        {
            var pawn = p.PlayerPawn;
            return new RosterObservation(p.SteamID.ToString(),
                p.CSTeamNum == CSTeamNumber.Terrorist ? "T" :
                p.CSTeamNum == CSTeamNumber.CounterTerrorist ? "CT" : "other",
                pawn?.IsAlive,
                pawn is not null && float.IsFinite(pawn.Origin.X) &&
                    float.IsFinite(pawn.Origin.Y) && float.IsFinite(pawn.Origin.Z),
                pawn is not null && p.InGameMoneyServices is not null && pawn.ItemServices is not null);
        });
        var phase = rounds.Phase == "live" && frame.Bomb.State is "planted" or "defusing"
            ? "post-plant" : rounds.Phase;
        var sample = new SemanticFrame(frame.Tick, rounds.Current?.RoundId,
            rounds.Current?.RoundNumber, rounds.Current?.SegmentId ?? 0, phase,
            clock.Resolve(rules, rounds.Current?.RoundId, phase,
                frame.Bomb.SecondsToExplosion, frame.Bomb.SecondsToDefuse),
            roster.Observe(observations));
        if (frames.Count > 0 && frames[^1].Tick == frame.Tick) frames[^1] = sample;
        else frames.Add(sample);
    }

    public SemanticTimeline Finish()
    {
        rounds.Finish();
        return new(rounds.Attempts, frames, audit);
    }
}

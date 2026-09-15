using System.Text.Json;
using DemoFile;

namespace CsDemoMap.Cli;

internal static class DemoRosterTrace
{
    private sealed class RosterTraceCompleteException : Exception;

    // A small diagnostic that reads the upstream entities directly, independently
    // of the semantic tracker. IDs stay in this local diagnostic, never in training.
    public static async Task RunAsync(string path, int from, int to)
    {
        var demo = new CsDemoParser();
        var lastTick = -1;
        demo.Source1GameEvents.RoundStart += _ => Emit("start", null);
        demo.Source1GameEvents.RoundFreezeEnd += _ => Emit("live", null);
        demo.Source1GameEvents.PlayerDeath += e => Emit("death", e.Player?.SteamID.ToString());
        demo.OnCommandFinishPersistent += () =>
        {
            var tick = demo.CurrentDemoTick.Value;
            if (tick > to) throw new RosterTraceCompleteException();
            if (tick >= from && tick / 64 != lastTick)
            {
                lastTick = tick / 64;
                Emit("snapshot", null);
            }
        };
        await using var source = File.OpenRead(path);
        try { await DemoFileReader.Create(demo, source).ReadAllAsync(CancellationToken.None); }
        catch (RosterTraceCompleteException) { }
        void Emit(string kind, string? player)
        {
            var tick = demo.CurrentDemoTick.Value;
            if (tick < from || tick > to) return;
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                kind, tick, player, freeze = demo.GameRules.FreezePeriod,
                rawRound = demo.GameRules.RoundStartRoundNumber,
                players = demo.Players.Select(p => new
                {
                    id = p.SteamID.ToString(), side = (int)p.CSTeamNum,
                    alive = p.PlayerPawn?.IsAlive, health = p.PlayerPawn?.Health
                })
            }));
        }
    }
}

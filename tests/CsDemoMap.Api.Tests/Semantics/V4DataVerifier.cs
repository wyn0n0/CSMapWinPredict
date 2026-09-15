using System.Text.Json.Nodes;
using CsDemoMap.Api.Models;

namespace CsDemoMap.Api.Tests;

internal static class V4DataVerifier
{
    public static async Task VerifyAsync()
    {
        var timeline = CreateTimeline(9000);
        var built = new WinFeatureSampleBuilder().Build(timeline);
        Check(built.Samples.Count == 2, "shared builder sample count");
        Check(built.Samples.All(sample => sample.RoundId == "s0-a2" && sample.RoundNumber == 1),
            "shared builder identity");
        Check(built.Targets["s0-a2"] == new WinTrainingTarget(1, 2), "training target separated");
        Check(built.Report.RowCount == 2 && built.Report.ExportedRounds == 1, "shared builder report");
        Check(built.Samples[0].GetType().GetProperty("LabelTWin") is null,
            "inference sample excludes label");
        using var writer = new StringWriter();
        await WinDatasetV4Exporter.WriteTimelineAsync(timeline, "match", writer, CancellationToken.None);
        var rows = writer.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)
            .Select(s => JsonNode.Parse(s)!).ToArray();
        Check(rows.Length == 2, "v4 sample count");
        Check(rows.All(r => (int)r["schemaVersion"]! == 4 && (string)r["roundId"]! == "s0-a2"), "v4 identity");
        Check(rows.Sum(r => (double)r["sampleWeight"]!) == 1, "v4 round weights");
        Check(rows[0]["features"]!["elapsedSeconds"] is null &&
            (double)rows[0]["features"]!["clock"]!["liveElapsedSeconds"]! == 0, "v4 explicit clock");
        Check(JsonNode.DeepEquals(built.Samples[0].Features, rows[0]["features"]),
            "exporter uses shared feature node");
        Check((int)rows[0]["features"]!["t"]!["totalMoney"]! == 1000, "future money excluded");
        Check(!writer.ToString().Contains("playerId") && !writer.ToString().Contains("carrierId"), "no identity features");
        using var changed = new StringWriter();
        await WinDatasetV4Exporter.WriteTimelineAsync(CreateTimeline(99000), "match", changed, CancellationToken.None);
        var firstChanged = JsonNode.Parse(changed.ToString().Split(Environment.NewLine)[0])!;
        Check(JsonNode.DeepEquals(rows[0]["features"], firstChanged["features"]), "future state does not change historical v4 features");

        var semantic = timeline.Semantics!;
        var partial = timeline with { Semantics = semantic with
        {
            Frames = semantic.Frames.Select(s => s with
            {
                Roster = s.Roster with { AliveEquipmentKnown = 1, Quality = "partial" }
            }).ToArray()
        }};
        using var partialWriter = new StringWriter();
        await WinDatasetV4Exporter.WriteTimelineAsync(partial, "match", partialWriter, CancellationToken.None);
        var partialRow = JsonNode.Parse(partialWriter.ToString().Split(Environment.NewLine)[0])!;
        Check(partialRow["features"]!["t"]!["totalMoney"] is null, "partial resources stay null");
        var missingPosition = timeline with
        {
            Frames = timeline.Frames.Select(f => f with
            {
                Players = f.Players.Select(p => p.Team == "T" ? p with { X = float.NaN } : p).ToArray()
            }).ToArray(),
            Semantics = semantic with { Frames = semantic.Frames.Select(f => f with
            {
                Roster = f.Roster with { AlivePositionKnown = 1, Quality = "partial" }
            }).ToArray() }
        };
        using var missingWriter = new StringWriter();
        await WinDatasetV4Exporter.WriteTimelineAsync(missingPosition, "match", missingWriter, CancellationToken.None);
        var missingRow = JsonNode.Parse(missingWriter.ToString().Split(Environment.NewLine)[0])!;
        Check(missingRow["features"]!["baseline"]!["nearestOpponentDistance"] is null,
            "unknown positions yield null distances without nonfinite JSON");
        Console.WriteLine("V4 export checks passed: 15");
        static void Check(bool condition, string message)
        { if (!condition) throw new InvalidOperationException(message); }
    }

    internal static DemoTimeline CreateTimeline(int futureMoney)
    {
        var players = new[]
        {
            new PlayerSnapshot("t", "ignored", "T", true, 100, 100, 200, 0, 0, 0, 0, 0, "A", "rifle", 0, 0),
            new PlayerSnapshot("ct", "ignored", "CT", true, 100, 300, 400, 0, 0, 0, 0, 0, "B", "rifle", 0, 0)
        };
        var frames = new[] { 64, 128 }.Select(t => new DemoFrame(t, t / 64d, players,
            new RoundSnapshot(2, "live", 0, 0, 800, 115, 0, 0),
            new BombSnapshot("carried", "t", null, null, null, 100, 200, 0, null, null), [])).ToArray();
        var quality = new RosterQuality(5, 5, 10, 2, 2, 2, true, "usable", []);
        var semantics = new SemanticTimeline(
            [new RoundAttempt { RoundId = "s0-a2", RoundNumber = 1, StartTick = 32, LiveTick = 64,
                EndTick = 192, Disposition = "completed", Winner = "T" }],
            frames.Select(f => new SemanticFrame(f.Tick, "s0-a2", 1, 0, "live",
                new SemanticClock((f.Tick - 64) / 64d, 115 - (f.Tick - 64) / 64d, null, null,
                    true, "test", "none"), quality)).ToArray(), []);
        return new DemoTimeline(new DemoMetadata("test.dem", "de_mirage", 64, 8, 192, 3), frames,
            [], [], [], [Equipment(0, "t", 99999), Equipment(32, "t", 1000), Equipment(32, "ct", 1000),
                Equipment(128, "t", futureMoney)], [], []) { Semantics = semantics };
        static PlayerEquipmentState Equipment(int tick, string id, int money) => new(
            tick, tick / 64d, id, money, 100, true, false, 4000, 4000, 0, []);
    }
}

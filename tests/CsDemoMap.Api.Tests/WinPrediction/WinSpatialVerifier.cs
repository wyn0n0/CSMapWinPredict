using System.Text.Json;
using System.Text.Json.Nodes;
using CsDemoMap.Api.Models;

namespace CsDemoMap.Api.Tests;

internal static class WinSpatialVerifier
{
    public static async Task VerifyAsync()
    {
        var checks = 0;
        var builder = new WinSpatialFeatureBuilder("de_mirage", new(5, 10));
        var frame = V4DataVerifier.CreateTimeline(9000).Frames[0] with
        {
            Players = [Player("t", "T", 0, 0), Player("t2", "T", 3, 4), Player("t3", "T", 6, 8),
                Player("ct", "CT", 0, 12), Player("ct2", "CT", 0, 20),
                Player("dead", "CT", 0, 0) with { Alive = false }],
            Bomb = new("carried", "t", null, null, null, 0, 0, 0, null, null)
        };
        var f = Build(frame);
        Check(f.T.NearestC4Distance == 0 && f.T.SecondC4Distance == 5, "carried C4 includes carrier at zero");
        Check(f.CT.NearestC4Distance == 12 && f.CT.SecondC4Distance == 20, "ranked C4 distances ignore dead players");
        Check(f.NearestC4DistanceDifference == -12 && f.SecondC4DistanceDifference == -15, "difference is T minus CT");
        Check(f.Carrier.NearestTeammateDistance == 5 && f.Carrier.NearbyTeammateCount == 1, "support radius boundary inclusive; exclude self");
        Check(f.T.MeanNearestTeammateDistance == 5 && f.T.MaxNearestTeammateDistance == 5, "team nearest-neighbor aggregation");
        Check(f.CT.MeanNearestTeammateDistance == 8 && f.T.C4DistanceCount == 3, "support and valid distance count");
        var order = frame with { Players = frame.Players.Reverse().ToArray() };
        Equal(f, Build(order), "player order invariant");
        var renamed = frame with
        {
            Players = frame.Players.Select(p => p with { Id = "new-" + p.Id, Name = "other", Kills = 42 }).ToArray(),
            Bomb = frame.Bomb with { CarrierId = "new-t" }
        };
        Equal(f, Build(renamed), "identity and scoreboard invariant");
        foreach (var state in new[] { "dropped", "planted", "defusing" })
        {
            var changed = Build(frame with { Bomb = frame.Bomb with { State = state, CarrierId = null, X = 0, Y = 10 } });
            Check(changed.BombState == state && changed.BombPositionStatus == "known", "preserve state: " + state);
            Check(changed.CT.NearestC4Distance == 2 && changed.Carrier.Status == "not-applicable" &&
                changed.Carrier.NearbyTeammateCount is null, "actual C4 anchor and non-carrier: " + state);
        }
        Check(Build(frame with { Bomb = frame.Bomb with { State = "planting" } }).Carrier.Status == "available", "planting has carrier support");
        foreach (var state in new[] { "defused", "exploded" })
        {
            var changed = Build(frame with { Bomb = frame.Bomb with { State = state } });
            Check(changed.BombPositionStatus == "not-applicable" && changed.T.NearestC4Distance is null,
                "non-applicable C4 does not reuse stale coordinates: " + state);
        }
        foreach (var state in new[] { "unavailable", "unknown" })
        {
            var changed = Build(frame with { Bomb = frame.Bomb with { State = state } });
            Check(changed.BombPositionStatus == "unknown-state" && changed.T.NearestC4Distance is null,
                "unknown C4 state is distinct from terminal/inapplicable: " + state);
        }
        var missingBomb = Build(frame with { Bomb = frame.Bomb with { X = float.NaN } });
        Check(missingBomb.BombPositionStatus == "missing-position" && missingBomb.T.C4DistanceCount == 0 &&
            missingBomb.NearestC4DistanceDifference is null, "unknown bomb is not zero distance");
        var missingCarrier = Build(frame with { Bomb = frame.Bomb with { CarrierId = "absent" } });
        Check(missingCarrier.BombPositionStatus == "missing-carrier" && missingCarrier.Carrier.Status == "missing-carrier", "missing carrier rejected");
        var partialFrame = frame with { Players = frame.Players.Select(p => p.Id == "t2" ? p with { Z = float.NaN } : p).ToArray() };
        var partial = Build(partialFrame);
        Check(partial.T.PositionStatus == "missing-position" && partial.T.ValidPositionCount == 2 && partial.T.C4DistanceCount == 2,
            "raw invalid position retained as missing, observed valid counts retained");
        Check(partial.T.NearestC4Distance is null && partial.T.SiteANearbyCount is null && partial.T.MeanNearestTeammateDistance is null,
            "partial team does not pretend its subset is complete");
        Check(partial.CT.NearestC4Distance == 12 && partial.Carrier.NearbyTeammateCount is null, "independent complete team and missing support");
        var mismatch = builder.Build(frame, Semantic(frame) with { Roster = Semantic(frame).Roster with { AliveKnown = 6 } });
        Check(mismatch.RosterStatus == "incomplete" && mismatch.T.NearestC4Distance is null, "missing pawn from roster masks aggregates");
        var duplicate = Build(frame with { Players = frame.Players.Append(frame.Players[0]).ToArray() });
        Check(duplicate.RosterStatus == "incomplete", "duplicate player rejected");
        var positionMismatch = builder.Build(frame, Semantic(frame) with { Roster = Semantic(frame).Roster with { AlivePositionKnown = 1 } });
        Check(positionMismatch.T.PositionStatus == "missing-position", "semantic position-count disagreement masks aggregates");
        var solo = Build(frame with { Players = [frame.Players[0]] });
        Check(solo.Carrier.Status == "no-teammates" && solo.Carrier.NearbyTeammateCount == 0 &&
            solo.Carrier.NearestTeammateDistance is null && solo.T.SecondC4Distance is null, "singleton has no second neighbor");
        Check(solo.CT.NearestC4Distance is null && solo.CT.SiteANearbyCount == 0 && solo.T.MeanNearestTeammateDistance is null,
            "empty team and singleton distances are not zero");
        var same = Build(frame with { Players = [frame.Players[0], Player("same", "T", 0, 0)] });
        Check(same.Carrier.NearestTeammateDistance == 0 && same.T.SecondC4Distance == 0, "real zero distance stays zero");
        var geometry = MapFeatureGeometries.Find("de_mirage")!;
        var sites = Build(frame with { Players = [Player("a", "T", (float)geometry.SiteA.X, (float)geometry.SiteA.Y),
            Player("b", "T", (float)geometry.SiteB.X, (float)geometry.SiteB.Y)] });
        Check(sites.T.SiteANearbyCount == 1 && sites.T.SiteBNearbyCount == 1, "fixed world site anchors");
        var high = Build(frame with { Players = frame.Players.Select(p => p with { Z = 500 }).ToArray() });
        Equal(f, high, "XY distance makes no floor or reachability claim");
        foreach (var radius in new[] { 0, -1, double.NaN, double.PositiveInfinity })
            Throws(() => new WinSpatialFeatureBuilder("de_mirage", new(radius, 10)), "invalid radius");
        Throws(() => new WinSpatialFeatureBuilder("de_inferno", new(1, 1)), "unsupported map");
        Throws(() => builder.Build(frame, Semantic(frame) with { Tick = frame.Tick + 1 }), "wrong semantic tick");

        var timeline = V4DataVerifier.CreateTimeline(9000);
        using var spatial = new StringWriter();
        using var source = new StringWriter();
        var count = await WinSpatialDatasetExporter.WriteTimelineAsync(timeline, "match", new(5, 10), spatial, source, CancellationToken.None);
        using var baseline = new StringWriter();
        await WinDatasetV4Exporter.WriteTimelineAsync(timeline, "match", baseline, CancellationToken.None);
        var rows = Rows(spatial);
        var baseRows = Rows(baseline);
        Check(count == 2 && rows.Length == baseRows.Length && Rows(source).Length == count, "one companion and evidence row per v4 row");
        foreach (var key in new[] { "matchId", "roundId", "segmentId", "roundNumber", "tick", "mapName" })
            Check(rows.Zip(baseRows).All(pair => JsonNode.DeepEquals(pair.First[key], pair.Second[key])), "join key: " + key);
        Check(rows.All(row => row["schemaVersion"] is null && row["labelTWin"] is null &&
            (string?)row["contractVersion"] == "win-spatial-v1"), "separate contract, no copied outcome");
        Check(!spatial.ToString().Contains("carrierId") && !spatial.ToString().Contains("playerId"), "IDs excluded from features");
        var future = timeline with
        {
            Frames = timeline.Frames.Select(x => x.Tick == 64 ? x : x with
            { Players = [Player("future", "T", 99999, 99999)], Bomb = x.Bomb with { State = "exploded", X = 99999 } }).ToArray(),
            Semantics = timeline.Semantics! with { Attempts = [new RoundAttempt { RoundId = "s0-a2", RoundNumber = 1,
                StartTick = 32, LiveTick = 64, EndTick = 192, Disposition = "completed", Winner = "CT" }] }
        };
        using var changedRows = new StringWriter();
        await WinSpatialDatasetExporter.WriteTimelineAsync(future, "match", new(5, 10), changedRows, TextWriter.Null, CancellationToken.None);
        Check(JsonNode.DeepEquals(rows[0], Rows(changedRows)[0]), "future positions, C4 and winner cannot change earlier row");
        using var missingSource = new StringWriter();
        using var missingSpatial = new StringWriter();
        var missingTimeline = timeline with
        {
            Frames = timeline.Frames.Select(x => x with { Players = x.Players.Select(p =>
                p.Team == "T" ? p with { X = float.NaN } : p).ToArray() }).ToArray(),
            Semantics = timeline.Semantics! with { Frames = timeline.Semantics.Frames.Select(x =>
                x with { Roster = x.Roster with { AlivePositionKnown = 1 } }).ToArray() }
        };
        await WinSpatialDatasetExporter.WriteTimelineAsync(missingTimeline, "match", new(5, 10), missingSpatial, missingSource, CancellationToken.None);
        Check(Rows(missingSource)[0]["players"]![0]!["x"] is null &&
            Rows(missingSpatial)[0]["features"]!["t"]!["nearestC4Distance"] is null,
            "export uses raw missing positions instead of legacy zero substitution");
        var existingOutput = Path.Combine(Path.GetTempPath(), "win-spatial-existing-" + Guid.NewGuid());
        Directory.CreateDirectory(existingOutput);
        try
        {
            await WinSpatialDatasetExporter.ExportAsync("unused.dem", existingOutput, new(5, 10), CancellationToken.None);
            throw new InvalidOperationException("Existing output was accepted");
        }
        catch (IOException) { Check(!Directory.EnumerateFileSystemEntries(existingOutput).Any(), "existing output rejected without writes"); }
        finally { Directory.Delete(existingOutput); }
        Console.WriteLine($"Spatial v1 checks passed: {checks}");

        WinSpatialFeatures Build(DemoFrame x) => builder.Build(x, Semantic(x));
        void Check(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); checks++; }
        void Equal(object a, object b, string message) => Check(JsonSerializer.Serialize(a) == JsonSerializer.Serialize(b), message);
        void Throws(Action action, string message)
        {
            try { action(); } catch (ArgumentException) { checks++; return; }
            throw new InvalidOperationException(message);
        }
    }

    private static PlayerSnapshot Player(string id, string team, float x, float y) =>
        new(id, "ignored", team, true, 100, x, y, 0, 0, 0, 0, 0, "unknown", null, 0, 0);
    private static SemanticFrame Semantic(DemoFrame frame)
    {
        var alive = frame.Players.Where(p => p.Alive && p.Team is "T" or "CT").ToArray();
        return new(frame.Tick, "s0-a2", 1, 0, "live", new(0, 115, null, null, true, "test", "none"),
            new(5, 5, 10, alive.Length, alive.Count(p => float.IsFinite(p.X) && float.IsFinite(p.Y) && float.IsFinite(p.Z)),
                alive.Length, true, "usable", []));
    }
    private static JsonNode[] Rows(StringWriter writer) => writer.ToString()
        .Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries).Select(s => JsonNode.Parse(s)!).ToArray();
}

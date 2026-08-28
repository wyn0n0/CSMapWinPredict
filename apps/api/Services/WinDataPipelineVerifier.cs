using System.Text.Json;
using CsDemoMap.Api.Models;

namespace CsDemoMap.Api.Services;

internal static class WinDataPipelineVerifier
{
    public static async Task<int> VerifyAsync(CancellationToken cancellationToken)
    {
        var checks = 0;

        Check(DemoParserService.RoundWinnerSide(2) == "T", "winner 2 should map to T");
        Check(DemoParserService.RoundWinnerSide(3) == "CT", "winner 3 should map to CT");
        Check(DemoParserService.RoundWinnerSide(0) is null, "non-playing team must not produce a label");
        Check(DemoParserService.RoundEndReasonName(7) == "BombDefused", "round reason should be named");
        Check(DemoParserService.RoundEndReasonName(99) == "Unknown(99)", "unknown reason should be preserved");

        var timeline = CreateTimeline(futureMoney: 9_000);
        var featureBuilder = new AsOfTickFeatureBuilder(timeline);
        var firstLiveBaseline = featureBuilder.Build(timeline.Frames[1]).Baseline;
        Check(firstLiveBaseline.BombStateChangeCount == 0 && !firstLiveBaseline.BombWasDropped,
            "freeze-time bomb state leaked into the live-round history");
        var features = featureBuilder.Build(timeline.Frames[2]);
        Check(features.T.TotalMoney == 1_000, "future equipment leaked into team features");
        Check(features.Players.Single(player => player.Team == "T").Money == 1_000,
            "future equipment leaked into player features");
        var baseline = featureBuilder.Build(timeline.Frames[3]).Baseline;
        Check(baseline.PreviousBombState == "carried" && baseline.BombStateChangeCount == 1,
            "bomb transition history should include carried to planted");
        Check(baseline.BombWasPlanted && !baseline.BombWasDefusing,
            "bomb state flags should reflect only states seen so far");
        Check(baseline.TPositionDispersion == 0 && baseline.CTPositionDispersion == 0,
            "single-player team dispersion should be zero");
        Check(baseline.NearestOpponentDistance is > 0,
            "nearest opponent distance should be available for Mirage");
        Check(baseline.TMeanDistanceToSiteA is not null && baseline.CTMeanDistanceToSiteB is not null,
            "bombsite distance aggregates should be available for Mirage");
        Check(baseline.EquipmentValueDifference == 0 && baseline.HealthDifference == 20 && baseline.AliveDifference == 0,
            "requested team difference features should be calculated");

        Check(baseline.ScoreDifference == 0 && baseline.LossStreakDifference == 0 && baseline.TotalAlive == 2,
            "round-context aggregates should be calculated");
        Check(baseline.HasExplosionTimer && !baseline.HasDefuseTimer,
            "bomb timer availability flags should follow the current tick");
        Check(baseline.PositionDispersionDifference == 0 &&
              !baseline.TPositionDataMissing && !baseline.CTPositionDataMissing &&
              !baseline.NearestOpponentDistanceMissing,
            "spatial aggregate and missingness features should be calculated");
        Check(baseline.TClosestSiteDistance is > 0 && baseline.CTClosestSiteDistance is > 0 &&
              baseline.SiteAProximityDifference is not null && baseline.SiteBProximityDifference is not null,
            "site proximity derivative features should be available for Mirage");
        Check(baseline.MoneyDifference == 7_000 && baseline.ArmorDifference == 0 &&
              baseline.DefuserCountDifference == -1 && baseline.RifleCountDifference == -1 &&
              baseline.SniperCountDifference == 1,
            "resource difference features should be calculated");
        Check(baseline.TMeanHealth == 80 && baseline.CTMeanHealth == 60 && baseline.IsClutch &&
              baseline.TEquipmentCoverage == 1 && baseline.CTEquipmentCoverage == 1 &&
              baseline.EquipmentCoverageDifference == 0,
            "health, clutch, and equipment-coverage features should be calculated");
        var changedFuture = CreateTimeline(futureMoney: 99_999, futureBombState: "defusing");
        var changedFeatures = new AsOfTickFeatureBuilder(changedFuture).Build(changedFuture.Frames[2]);
        Check(JsonSerializer.Serialize(features) == JsonSerializer.Serialize(changedFeatures),
            "changing future state changed as-of features");

        using var writer = new StringWriter();
        var rowCount = await WinDatasetExporter.WriteTimelineAsync(
            timeline,
            "match-test",
            writer,
            cancellationToken);
        var lines = writer.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Check(rowCount == 3 && lines.Length == 3, "live round was not sampled once per second");

        var weights = new List<double>();
        foreach (var line in lines)
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            Check(root.GetProperty("schemaVersion").GetInt32() == WinDatasetExporter.DatasetSchemaVersion,
                "dataset schema version was not written");
            Check(root.GetProperty("labelTWin").GetInt32() == 1, "T winner did not produce label 1");
            Check(root.GetProperty("tick").GetInt32() < timeline.RoundResults[0].EndTick,
                "round-end frame was exported");
            weights.Add(root.GetProperty("sampleWeight").GetDouble());
            Check(!line.Contains("playerId", StringComparison.OrdinalIgnoreCase),
                "player identifiers were exported");
            Check(!line.Contains("carrierId", StringComparison.OrdinalIgnoreCase),
                "bomb carrier identifiers were exported");
        }
        Check(Math.Abs(weights.Sum() - 1d) < 0.000_001, "sample weights do not sum to one");

        Console.WriteLine($"API data-pipeline checks passed: {checks}");
        return checks;

        void Check(bool condition, string message)
        {
            if (!condition)
                throw new InvalidOperationException(message);
            checks++;
        }
    }

    private static DemoTimeline CreateTimeline(int futureMoney, string futureBombState = "planted")
    {
        var players = new[]
        {
            new PlayerSnapshot(
                "t-player", "ignored-name", "T", true, 80,
                100, 200, 10, 90, 5, 0, 0, "A Ramp", "weapon_ak47", 2, 1),
            new PlayerSnapshot(
                "ct-player", "ignored-name", "CT", true, 60,
                300, 400, 10, 270, 0, 0, 0, "A Site", "weapon_m4a1", 1, 2)
        };
        var frames = new[]
        {
            Frame(0, players, "freeze"),
            Frame(64, players, "live"),
            Frame(128, players, "live"),
            Frame(192, players, "post-plant"),
            Frame(256, players, "ended")
        };
        frames[0] = frames[0] with { Bomb = frames[0].Bomb with { State = "dropped" } };
        frames[3] = frames[3] with { Bomb = frames[3].Bomb with { State = futureBombState } };
        var equipment = new[]
        {
            Equipment(32, "t-player", 1_000, "rifle"),
            Equipment(32, "ct-player", 2_000, "rifle"),
            Equipment(160, "t-player", futureMoney, "sniper")
        };
        return new DemoTimeline(
            new DemoMetadata("test.dem", "de_mirage", 64, 8, 256, 4),
            frames,
            [],
            [],
            [],
            equipment,
            [],
            [new RoundResult(1, 0, 64, 256, "T", "TerroristsWin")]);

        static DemoFrame Frame(int tick, IReadOnlyList<PlayerSnapshot> framePlayers, string phase) => new(
            tick,
            tick / 64d,
            framePlayers,
            new RoundSnapshot(1, phase, 0, 0, tick / 64d, Math.Max(0, 115 - tick / 64d), 0, 0),
            new BombSnapshot(
                phase == "post-plant" ? "planted" : "carried",
                phase == "post-plant" ? null : "t-player",
                null,
                phase == "post-plant" ? "A" : null,
                phase == "post-plant" ? "A Site" : "A Ramp",
                200,
                250,
                10,
                phase == "post-plant" ? 35 : null,
                null),
            [new MapZoneOccupancy("A Site", 1, 1, 1, 1)]);

        static PlayerEquipmentState Equipment(int tick, string playerId, int money, string category) => new(
            tick,
            tick / 64d,
            playerId,
            money,
            100,
            true,
            playerId == "ct-player",
            4_000,
            4_000,
            0,
            [new EquipmentItem($"weapon_{category}", category, 1, 30, 90)]);
    }
}

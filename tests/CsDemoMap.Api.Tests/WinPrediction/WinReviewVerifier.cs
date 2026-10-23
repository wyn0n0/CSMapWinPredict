using System.Text.Json;
using CsDemoMap.Api.Models;
using CsDemoMap.Api.Services;

namespace CsDemoMap.Api.Tests;

internal static class WinReviewVerifier
{
    public static void Verify()
    {
        var timeline = V4DataVerifier.CreateTimeline(1000);
        var semantic = timeline.Semantics!;
        timeline = timeline with
        {
            Semantics = semantic with
            {
                Frames = semantic.Frames.Select(frame => frame.Tick == 128
                    ? frame with { Roster = frame.Roster with { AliveEquipmentKnown = 1, AlivePositionKnown = 1 } }
                    : frame).ToArray(),
                Attempts = [.. semantic.Attempts, new RoundAttempt
                {
                    RoundId = "s1-a1", SegmentId = 1, RoundNumber = 1, StartTick = 192,
                    LiveTick = 256, EndTick = 300, Disposition = "aborted", Winner = "CT"
                }]
            }
        };
        var point = new WinPredictionPoint(64, 1, "s0-a2", 0, 1, "live", .4, .6);
        var points = new[] { point with { Tick = 128, TimeSeconds = 2 }, point,
            point with { Tick = 192, TimeSeconds = 3 }, // End tick must never become a prediction.
            point with { SegmentId = 1 },
            point with { Tick = 256, TimeSeconds = 4, RoundId = "s1-a1", SegmentId = 1 } };
        var review = WinReviewBuilder.Build(timeline, points);
        Check(review.SchemaVersion == 2 && review.Rounds.Count == 2, "review identity");
        var round = review.Rounds[0];
        Check(round.RoundNumber == 1 && round.LiveTick == 64 && round.EndTick == 192,
            "semantic boundaries, not legacy round number");
        Check(round.Points.Select(p => p.Tick).SequenceEqual(new[] { 64, 128 }), "sort, segment and terminal isolation");
        Check(round.Points[0].Quality == new WinPredictionQuality(false, false), "known quality");
        Check(round.Points[1].Quality == new WinPredictionQuality(true, true), "partial quality");
        Check(point.Quality is null && round.Points[0].TWin == point.TWin, "original prediction stays unchanged");
        Check(review.Rounds[1].Points.Count == 0 && review.Rounds[1].Winner is null, "aborted attempt isolation");
        Check(WinReviewBuilder.Build(timeline, []).Rounds.All(r => r.Points.Count == 0),
            "model unavailable preserves rounds");
        var unknown = WinReviewBuilder.Build(timeline with { Semantics = semantic with { Frames = [] } }, [point]);
        Check(unknown.Rounds[0].Points[0].Quality is null, "unknown quality is not complete");
        var json = JsonSerializer.Serialize(review, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        using var payload = JsonDocument.Parse(json);
        Check(payload.RootElement.GetProperty("rounds")[0].GetProperty("points")[0]
            .GetProperty("quality").GetProperty("equipmentMissing").ValueKind == JsonValueKind.False,
            "browser contract");
        Console.WriteLine("Win review checks passed: 10");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}

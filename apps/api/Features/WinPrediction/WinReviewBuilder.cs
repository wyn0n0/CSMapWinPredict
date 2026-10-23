using CsDemoMap.Api.Models;

namespace CsDemoMap.Api.Services;

public sealed record WinReview(int SchemaVersion, IReadOnlyList<WinReviewRound> Rounds);

public sealed record WinReviewRound(
    string RoundId, int SegmentId, int? RoundNumber,
    int StartTick, int LiveTick, int EndTick, string Disposition, string? Winner,
    IReadOnlyList<WinPredictionPoint> Points,
    IReadOnlyList<WinReviewComparison>? Comparisons = null);

public sealed record WinReviewComparison(string Id, IReadOnlyList<TimelineEvent> Events,
    WinPredictionPoint? Before, WinPredictionPoint? After, string Status, double? TDeltaPercentagePoints);

// A small review payload, independent of the heavyweight radar windows and model inputs.
public static class WinReviewBuilder
{
    public static WinReview Build(DemoTimeline timeline, IReadOnlyList<WinPredictionPoint> predictions)
    {
        if (timeline.Semantics is not { } semantics) return new(2, []);
        var frames = semantics.Frames.ToDictionary(frame => frame.Tick);
        foreach (var state in timeline.WinEventStates) frames.TryAdd(state.Frame.Tick, state.Semantic);
        var groups = WinEventSampleBuilder.GroupEvents(timeline).ToLookup(group => group.RoundId);
        var byRound = predictions.ToLookup(point => point.RoundId);
        var rounds = semantics.Attempts
            .Where(attempt => attempt.LiveTick is not null &&
                (attempt.EndTick ?? timeline.Metadata.TotalTicks) > attempt.LiveTick)
            .OrderBy(attempt => attempt.StartTick)
            .Select(attempt =>
            {
                var points = byRound[attempt.RoundId].Where(point =>
                        attempt.Disposition == "completed" && point.SegmentId == attempt.SegmentId &&
                        point.Tick >= attempt.LiveTick && point.Tick < attempt.EndTick)
                    .OrderBy(point => point.Tick)
                    .Select(point => point with
                    {
                        Quality = frames.TryGetValue(point.Tick, out var frame) &&
                            frame.RoundId == point.RoundId && frame.SegmentId == point.SegmentId
                            ? new(frame.Roster.AliveEquipmentKnown < frame.Roster.AliveKnown,
                                frame.Roster.AlivePositionKnown < frame.Roster.AliveKnown)
                            : null
                    }).ToArray();
                var byTick = points.ToDictionary(point => point.Tick);
                var comparisons = groups[attempt.RoundId].Select(group =>
                {
                    var before = group.BeforeTick is { } tick ? byTick.GetValueOrDefault(tick) : null;
                    var after = byTick.GetValueOrDefault(group.AfterTick);
                    if (after?.Triggers?.Contains("event-after") != true) after = null;
                    var status = attempt.Disposition != "completed" ? "round-unavailable"
                        : group.AfterTick >= attempt.EndTick || group.Events.Any(e => e.Type is "bomb-defused" or "bomb-exploded") ? "round-ended"
                        : before is null ? "before-missing"
                        : after is null ? "after-unconfirmed"
                        : before.Quality is null || after.Quality is null ? "quality-unknown"
                        : before.Quality.EquipmentMissing || before.Quality.PositionMissing ||
                          after.Quality.EquipmentMissing || after.Quality.PositionMissing ? "data-incomplete"
                        : "ready";
                    return new WinReviewComparison($"{group.RoundId}-{group.Events[0].Tick}", group.Events,
                        before, after, status, status == "ready" ? (after!.TWin - before!.TWin) * 100 : null);
                }).ToArray();
                return new WinReviewRound(
                attempt.RoundId, attempt.SegmentId, attempt.RoundNumber,
                attempt.StartTick, attempt.LiveTick!.Value,
                attempt.EndTick ?? timeline.Metadata.TotalTicks, attempt.Disposition,
                attempt.Disposition == "completed" ? attempt.Winner : null,
                points.Where(point => point.Triggers is null || point.Triggers.Any(t => t is "regular" or "event-after")).ToArray(),
                comparisons);
            }).ToArray();
        return new(2, rounds);
    }
}

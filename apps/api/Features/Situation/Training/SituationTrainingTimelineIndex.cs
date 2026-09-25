using System.Collections.Frozen;
using CsDemoMap.Api.Models;

namespace CsDemoMap.Api.Services;

internal sealed record SituationTrainingStructuredEvent(int Tick, string Type);

/// <summary>
/// Immutable, match-scoped lookup data for stage-four selection. The index owns no
/// process-wide cache and becomes collectable together with its source Timeline.
/// </summary>
internal sealed class SituationTrainingTimelineIndex
{
    private static readonly IReadOnlyList<SemanticFrame> EmptySemanticFrames =
        Array.AsReadOnly(Array.Empty<SemanticFrame>());
    private static readonly IReadOnlyList<SituationTrainingStructuredEvent> EmptyEvents =
        Array.AsReadOnly(Array.Empty<SituationTrainingStructuredEvent>());

    private readonly FrozenDictionary<int, DemoFrame> framesByTick;
    private readonly FrozenDictionary<string, IReadOnlyList<SemanticFrame>> semanticFramesByRound;
    private readonly FrozenDictionary<string, RoundAttempt> completedAttemptsByRound;
    private readonly FrozenDictionary<string, IReadOnlyList<SituationTrainingStructuredEvent>> eventsByRound;
    private readonly IReadOnlyList<string> completedRoundIds;

    private SituationTrainingTimelineIndex(
        DemoTimeline timeline,
        FrozenDictionary<int, DemoFrame> framesByTick,
        FrozenDictionary<string, IReadOnlyList<SemanticFrame>> semanticFramesByRound,
        FrozenDictionary<string, RoundAttempt> completedAttemptsByRound,
        FrozenDictionary<string, IReadOnlyList<SituationTrainingStructuredEvent>> eventsByRound,
        IReadOnlyList<string> completedRoundIds)
    {
        Timeline = timeline;
        this.framesByTick = framesByTick;
        this.semanticFramesByRound = semanticFramesByRound;
        this.completedAttemptsByRound = completedAttemptsByRound;
        this.eventsByRound = eventsByRound;
        this.completedRoundIds = completedRoundIds;
    }

    internal DemoTimeline Timeline { get; }

    /// <summary>Completed attempts ordered by start tick and then ordinal round ID.</summary>
    internal IReadOnlyList<RoundAttempt> CompletedAttempts => Array.AsReadOnly(completedRoundIds
        .Select(roundId => CloneAttemptWithoutOutcome(completedAttemptsByRound[roundId]))
        .ToArray());

    internal static SituationTrainingTimelineIndex Create(
        DemoTimeline timeline,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(timeline);
        cancellationToken.ThrowIfCancellationRequested();
        if (timeline.Semantics is null)
            throw new InvalidOperationException("Semantic collection is required.");
        if (timeline.Metadata.TickRate <= 0)
            throw new InvalidDataException("Timeline tick rate must be positive.");

        var mutableFrames = new Dictionary<int, DemoFrame>();
        foreach (var frame in timeline.Frames)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!mutableFrames.TryAdd(frame.Tick, frame))
                throw new InvalidDataException($"Timeline contains duplicate frame tick {frame.Tick}.");
        }

        var mutableSemantics = new Dictionary<string, List<SemanticFrame>>(StringComparer.Ordinal);
        foreach (var frame in timeline.Semantics.Frames)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrEmpty(frame.RoundId))
                continue;
            if (!mutableSemantics.TryGetValue(frame.RoundId, out var frames))
            {
                frames = [];
                mutableSemantics.Add(frame.RoundId, frames);
            }
            frames.Add(frame);
        }

        var semanticFrames = new Dictionary<string, IReadOnlyList<SemanticFrame>>(StringComparer.Ordinal);
        foreach (var (roundId, frames) in mutableSemantics)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var ordered = frames.OrderBy(frame => frame.Tick).ToArray();
            if (ordered.Zip(ordered.Skip(1), (left, right) => left.Tick == right.Tick).Any(equal => equal))
                throw new InvalidDataException(
                    $"Timeline contains duplicate semantic tick in round {roundId}.");
            semanticFrames.Add(roundId, Array.AsReadOnly(ordered));
        }

        var completed = timeline.Semantics.Attempts
            .Where(attempt => string.Equals(attempt.Disposition, "completed", StringComparison.Ordinal))
            .OrderBy(attempt => attempt.StartTick)
            .ThenBy(attempt => attempt.RoundId, StringComparer.Ordinal)
            .Select(CloneAttemptWithoutOutcome)
            .ToArray();
        cancellationToken.ThrowIfCancellationRequested();
        var attemptsByRound = new Dictionary<string, RoundAttempt>(StringComparer.Ordinal);
        foreach (var attempt in completed)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(attempt.RoundId))
                throw new InvalidDataException("Completed attempt has no round ID.");
            if (!attemptsByRound.TryAdd(attempt.RoundId, attempt))
                throw new InvalidDataException($"Timeline contains duplicate completed round {attempt.RoundId}.");
        }

        var eventLists = completed.ToDictionary(
            attempt => attempt.RoundId,
            _ => new List<SituationTrainingStructuredEvent>(),
            StringComparer.Ordinal);
        IndexEvents(timeline.Events, completed, eventLists, cancellationToken);
        var roundEvents = eventLists.ToDictionary(
            item => item.Key,
            item => (IReadOnlyList<SituationTrainingStructuredEvent>)Array.AsReadOnly(item.Value
                .OrderBy(value => value.Tick)
                .ThenBy(value => value.Type, StringComparer.Ordinal)
                .ToArray()),
            StringComparer.Ordinal);

        cancellationToken.ThrowIfCancellationRequested();
        return new(
            timeline,
            mutableFrames.ToFrozenDictionary(),
            semanticFrames.ToFrozenDictionary(StringComparer.Ordinal),
            attemptsByRound.ToFrozenDictionary(StringComparer.Ordinal),
            roundEvents.ToFrozenDictionary(StringComparer.Ordinal),
            Array.AsReadOnly(completed.Select(attempt => attempt.RoundId).ToArray()));
    }

    internal bool TryGetFrame(int tick, out DemoFrame frame) =>
        framesByTick.TryGetValue(tick, out frame!);

    internal DemoFrame GetFrame(int tick) => framesByTick.TryGetValue(tick, out var frame)
        ? frame
        : throw new InvalidDataException($"Timeline frame {tick} is missing.");

    internal IReadOnlyList<SemanticFrame> GetSemanticFrames(string roundId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(roundId);
        return semanticFramesByRound.TryGetValue(roundId, out var frames)
            ? frames
            : EmptySemanticFrames;
    }

    internal bool TryGetCompletedAttempt(string roundId, out RoundAttempt attempt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(roundId);
        if (completedAttemptsByRound.TryGetValue(roundId, out var stored))
        {
            attempt = CloneAttemptWithoutOutcome(stored);
            return true;
        }
        attempt = null!;
        return false;
    }

    internal RoundAttempt GetCompletedAttempt(string roundId) =>
        TryGetCompletedAttempt(roundId, out var attempt)
            ? attempt
            : throw new InvalidDataException($"Completed round {roundId} is missing from the Timeline index.");

    internal IReadOnlyList<SituationTrainingStructuredEvent> GetStructuredEvents(string roundId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(roundId);
        return eventsByRound.TryGetValue(roundId, out var events) ? events : EmptyEvents;
    }

    internal IReadOnlyList<int> GetEventTicks(string roundId, string eventType)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventType);
        return GetStructuredEvents(roundId)
            .Where(item => string.Equals(item.Type, eventType, StringComparison.Ordinal))
            .Select(item => item.Tick)
            .ToArray();
    }

    private static void IndexEvents(
        IReadOnlyList<TimelineEvent> events,
        IReadOnlyList<RoundAttempt> attempts,
        IDictionary<string, List<SituationTrainingStructuredEvent>> destination,
        CancellationToken cancellationToken)
    {
        var boundedAttempts = attempts
            .Where(attempt => attempt.LiveTick is not null && attempt.EndTick is not null)
            .Select(attempt => new AttemptBounds(attempt, attempt.LiveTick!.Value, attempt.EndTick!.Value))
            .Where(item => item.LiveTick < item.EndTick)
            .OrderBy(item => item.LiveTick)
            .ThenBy(item => item.Attempt.RoundId, StringComparer.Ordinal)
            .ToArray();
        var orderedEvents = events
            .Select((item, ordinal) => new IndexedEvent(item, ordinal))
            .OrderBy(item => item.Event.Tick)
            .ThenBy(item => item.Ordinal)
            .ToArray();
        cancellationToken.ThrowIfCancellationRequested();
        var active = new List<AttemptBounds>();
        var nextAttempt = 0;
        foreach (var indexed in orderedEvents)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var tick = indexed.Event.Tick;
            while (nextAttempt < boundedAttempts.Length && boundedAttempts[nextAttempt].LiveTick <= tick)
                active.Add(boundedAttempts[nextAttempt++]);
            active.RemoveAll(item => item.EndTick <= tick);
            foreach (var item in active)
                destination[item.Attempt.RoundId].Add(new(tick, indexed.Event.Type));
        }
    }

    private static RoundAttempt CloneAttemptWithoutOutcome(RoundAttempt source) => new()
    {
        RoundId = source.RoundId,
        SegmentId = source.SegmentId,
        RoundNumber = source.RoundNumber,
        StartTick = source.StartTick,
        LiveTick = source.LiveTick,
        EndTick = source.EndTick,
        ScoreTAtLive = source.ScoreTAtLive,
        ScoreCTAtLive = source.ScoreCTAtLive,
        Disposition = source.Disposition,
        Winner = null,
        Reason = null
    };

    private sealed record AttemptBounds(RoundAttempt Attempt, int LiveTick, int EndTick);
    private sealed record IndexedEvent(TimelineEvent Event, int Ordinal);
}

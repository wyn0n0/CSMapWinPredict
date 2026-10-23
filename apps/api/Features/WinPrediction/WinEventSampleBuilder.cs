using CsDemoMap.Api.Models;

namespace CsDemoMap.Api.Services;

public sealed record WinEventSamples(IReadOnlyList<WinInferenceSample> Samples,
    IReadOnlyDictionary<int, string[]> Triggers);

public sealed record WinEventGroup(string RoundId, int SegmentId,
    IReadOnlyList<TimelineEvent> Events, int? BeforeTick)
{
    public int AfterTick => Events[^1].Tick;
}

// Inference only: training exports still call WinFeatureSampleBuilder directly.
public static class WinEventSampleBuilder
{
    public static bool IsKeyEvent(string type) => type is "kill" or "bomb-planted" or
        "bomb-defuse-start" or "bomb-defuse-abort" or "bomb-defused" or "bomb-exploded";

    public static IReadOnlyList<WinEventGroup> GroupEvents(DemoTimeline timeline)
    {
        var result = new List<WinEventGroup>();
        if (timeline.Semantics is not { } semantics) return result;
        foreach (var round in semantics.Attempts.Where(r => r.LiveTick is not null))
        {
            var events = timeline.Events.Where(e => IsKeyEvent(e.Type) && e.Tick >= round.LiveTick &&
                e.Tick >= round.StartTick && e.Tick <= (round.EndTick ?? timeline.Metadata.TotalTicks))
                .OrderBy(e => e.Tick).ToArray();
            var beforeFrames = semantics.Frames.Where(f => f.RoundId == round.RoundId &&
                f.SegmentId == round.SegmentId && f.Tick >= round.LiveTick && f.Phase is "live" or "post-plant")
                .OrderBy(f => f.Tick).ToArray();
            for (var start = 0; start < events.Length;)
            {
                var end = start + 1;
                // Consecutive events within one second form one exchange, including chains.
                while (end < events.Length && events[end].Tick - events[end - 1].Tick <= timeline.Metadata.TickRate) end++;
                var before = beforeFrames.LastOrDefault(f => f.Tick < events[start].Tick);
                result.Add(new(round.RoundId, round.SegmentId, events[start..end],
                    before is not null && events[start].Tick - before.Tick <= timeline.Metadata.TickRate / DemoParserService.SampleRate
                        ? before.Tick : null));
                start = end;
            }
        }
        return result;
    }

    public static bool Confirms(WinEventState state, IEnumerable<TimelineEvent> events)
    {
        var atTick = events.Where(e => e.Tick == state.Frame.Tick && IsKeyEvent(e.Type)).ToArray();
        if (atTick.Length == 0) return false;
        foreach (var death in atTick.Where(e => e.Type == "kill"))
            if (death.SubjectId is null || state.Frame.Players.Any(p => p.Id == death.SubjectId && p.Alive)) return false;
        var bomb = atTick.LastOrDefault(e => e.Type != "kill");
        return bomb?.Type switch
        {
            "bomb-planted" => state.Frame.Bomb.State is "planted" or "defusing",
            "bomb-defuse-start" => state.Frame.Bomb.State == "defusing" &&
                state.Frame.Bomb.DefuserId == bomb.SubjectId && bomb.SubjectId is not null && state.Frame.Bomb.SecondsToDefuse > 0 &&
                state.Frame.Players.Any(p => p.Id == bomb.SubjectId && p.Team == "CT" && p.Alive),
            "bomb-defuse-abort" => state.Frame.Bomb.State == "planted",
            "bomb-defused" or "bomb-exploded" => false, // Outcomes are never 100% model predictions.
            _ => true
        };
    }

    public static WinEventSamples Build(DemoTimeline timeline, CancellationToken token = default)
    {
        var regular = new WinFeatureSampleBuilder().Build(timeline, token).Samples;
        var samples = regular.ToDictionary(s => s.Tick);
        var triggers = regular.ToDictionary(s => s.Tick, _ => new HashSet<string> { "regular" });
        if (timeline.Semantics is not { } semantics || timeline.WinEventStates.Count == 0)
            return new(regular, triggers.ToDictionary(p => p.Key, p => p.Value.ToArray()));

        var requested = new Dictionary<int, HashSet<string>>();
        void Request(int tick, string trigger)
        {
            if (!requested.TryGetValue(tick, out var reasons)) requested[tick] = reasons = [];
            reasons.Add(trigger);
        }
        foreach (var group in GroupEvents(timeline))
            if (group.BeforeTick is { } tick) Request(tick, "event-before");
        var eventStates = timeline.WinEventStates.ToDictionary(s => s.Frame.Tick);
        foreach (var state in eventStates.Values)
            if (Confirms(state, timeline.Events)) Request(state.Frame.Tick, "event-after");

        var frames = timeline.Frames.ToDictionary(f => f.Tick);
        foreach (var round in semantics.Attempts.Where(r => r.Disposition == "completed"))
        {
            var regularStates = semantics.Frames.Where(s =>
                RoundSampleEligibility.EvaluateRoundTick(timeline.Metadata.MapName, round, s).Eligible).ToArray();
            var history = regularStates.Select(s => WinFeatureSampleBuilder.CorrectFrame(frames[s.Tick], s)).ToArray();
            var equipment = timeline.PlayerEquipmentStates.Where(s => s.Tick >= round.StartTick && s.Tick < round.EndTick).ToArray();
            var legacy = new AsOfTickFeatureBuilder(timeline with { Frames = history, PlayerEquipmentStates = equipment });
            var states = regularStates.ToDictionary(s => s.Tick);
            foreach (var pair in requested.Where(p => p.Key >= round.LiveTick && p.Key < round.EndTick))
            {
                token.ThrowIfCancellationRequested();
                if (samples.ContainsKey(pair.Key)) { triggers[pair.Key].UnionWith(pair.Value); continue; }
                var extra = eventStates.GetValueOrDefault(pair.Key);
                var semantic = states.GetValueOrDefault(pair.Key) ?? extra?.Semantic;
                var raw = frames.GetValueOrDefault(pair.Key) ?? extra?.Frame;
                if (semantic is null || raw is null) continue;
                var frame = WinFeatureSampleBuilder.CorrectFrame(raw, semantic);
                if (!RoundSampleEligibility.Evaluate(timeline.Metadata.MapName, round, semantic, frame).Eligible) continue;
                // Keep the 8 Hz history definition. An off-grid query appends only its own
                // current observation; extra events never rewrite regular sample features.
                var builder = frames.ContainsKey(pair.Key) ? legacy : new AsOfTickFeatureBuilder(timeline with
                {
                    Frames = [.. history.Where(f => f.Tick < pair.Key), frame],
                    PlayerEquipmentStates = [.. equipment.Where(e => e.Tick < pair.Key), .. extra!.Equipment]
                });
                samples.Add(pair.Key, new(pair.Key, pair.Key / (double)timeline.Metadata.TickRate,
                    round.RoundId, round.SegmentId, semantic.RoundNumber!.Value, timeline.Metadata.MapName,
                    semantic.Phase, WinFeatureSampleBuilder.BuildFeatureNode(builder, frame, semantic)));
                triggers[pair.Key] = pair.Value;
            }
        }
        return new(samples.Values.OrderBy(s => s.Tick).ToArray(), triggers.ToDictionary(p => p.Key, p => p.Value.Order().ToArray()));
    }
}

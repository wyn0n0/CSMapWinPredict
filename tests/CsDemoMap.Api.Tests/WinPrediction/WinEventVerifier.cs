using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using CsDemoMap.Api.Models;
using CsDemoMap.Api.Services;

namespace CsDemoMap.Api.Tests;

internal static class WinEventVerifier
{
    public static void Verify()
    {
        var timeline = Fixture();
        var legacy = new WinFeatureSampleBuilder().Build(timeline).Samples;
        var hybrid = WinEventSampleBuilder.Build(timeline);
        Check(legacy.All(sample => JsonNode.DeepEquals(sample.Features,
            hybrid.Samples.Single(s => s.Tick == sample.Tick).Features)), "regular feature parity");
        Check(hybrid.Samples.Select(s => s.Tick).Distinct().Count() == hybrid.Samples.Count, "same-tick deduplication");
        Check(hybrid.Triggers[128].Contains("regular") && hybrid.Triggers[128].Contains("event-after"), "regular/event collision keeps both triggers");
        Check(hybrid.Triggers[72].SequenceEqual(new[] { "event-before" }), "before-only evidence separated");
        Check(hybrid.Samples.Any(s => s.Tick == 149) && hybrid.Samples.Any(s => s.Tick == 153), "short defuse tap retained");
        Check(hybrid.Samples.All(s => s.Tick < 256), "terminal tick excluded");
        Check(hybrid.Samples.Single(s => s.Tick == 75).Features["ct"]!["alive"]!.GetValue<int>() == 1, "death state applied");
        var futureChanged = timeline with { WinEventStates = timeline.WinEventStates.Where(s => s.Frame.Tick < 149).ToArray(),
            Events = timeline.Events.Where(e => e.Tick < 149).ToArray() };
        var earlier = WinEventSampleBuilder.Build(futureChanged);
        Check(earlier.Samples.Where(s => s.Tick < 149).All(s => JsonNode.DeepEquals(s.Features,
            hybrid.Samples.Single(p => p.Tick == s.Tick).Features)), "future events never change past features");
        var unconfirmed = timeline with { WinEventStates = timeline.WinEventStates.Select(s => s.Frame.Tick == 149
            ? s with { Frame = s.Frame with { Bomb = s.Frame.Bomb with { State = "planted" } } } : s).ToArray() };
        Check(!WinEventSampleBuilder.Build(unconfirmed).Samples.Any(s => s.Tick == 149), "no stale post-event substitution");
        var groups = WinEventSampleBuilder.GroupEvents(timeline);
        Check(groups[0].Events.Count == 6 && groups[0].BeforeTick == 72, "rapid chain grouped, not credited per kill");
        Check(groups[^1].BeforeTick == 248, "before is actual prior tick");
        var points = hybrid.Samples.Select(s => new WinPredictionPoint(s.Tick, s.DemoTimeSeconds, s.RoundId,
            s.SegmentId, s.RoundNumber, s.Phase, s.Tick < 75 ? .4 : .6, s.Tick < 75 ? .6 : .4,
            Triggers: hybrid.Triggers[s.Tick])).ToArray();
        var review = WinReviewBuilder.Build(timeline, points).Rounds[0];
        Check(review.Points.All(p => p.Tick != 72), "before-only prediction never enters live curve");
        var comparison = review.Comparisons![0];
        Check(comparison.Status == "ready" && Math.Abs(comparison.TDeltaPercentagePoints!.Value - 20) < 1e-9,
            "comparison uses actual before and final group state");
        Check(review.Comparisons[^1].Status == "round-ended" && review.Comparisons[^1].TDeltaPercentagePoints is null,
            "settlement not a model delta");
        var partial = timeline with { Semantics = timeline.Semantics! with { Frames = timeline.Semantics!.Frames.Select(s =>
            s.Tick == 72 ? s with { Roster = s.Roster with { AliveEquipmentKnown = 0 } } : s).ToArray() } };
        Check(WinReviewBuilder.Build(partial, points).Rounds[0].Comparisons![0].Status == "data-incomplete", "missing data suppresses delta");
        var missing = WinReviewBuilder.Build(timeline, points.Where(p => p.Tick != comparison.After!.Tick).ToArray());
        Check(missing.Rounds[0].Comparisons![0].Status == "after-unconfirmed", "no nearest future prediction substitution");
        var noBefore = timeline with { Semantics = timeline.Semantics! with { Frames = timeline.Semantics!.Frames.Where(s => s.Tick != 72).ToArray() } };
        Check(WinReviewBuilder.Build(noBefore, points).Rounds[0].Comparisons![0].Status == "before-missing", "stale before not accepted");
        var wrongRound = timeline with { WinEventStates = timeline.WinEventStates.Select(s => s with
            { Semantic = s.Semantic with { RoundId = "foreign", SegmentId = 1 } }).ToArray() };
        Check(WinEventSampleBuilder.Build(wrongRound).Samples.All(s => s.Tick % 8 == 0), "attempt/segment isolation");
        var replay = DemoWindowSliceBuilder.WithEventStates(timeline);
        var slice = DemoWindowSliceBuilder.Build(replay, 0, review.Points);
        var sidecar = SituationWindowSidecarStore.Build(replay, 0, slice.DataFromTick, slice.DataToTick);
        Check(slice.Window.Frames.Single(f => f.Tick == 75).Players.Count(p => p.Alive) ==
            sidecar.SemanticFrames.Single(f => f.Tick == 75).Roster.AliveKnown, "replay and semantic sidecar stay aligned at death");
        Check(slice.Window.Frames.Single(f => f.Tick == 149).Bomb.State == "defusing" &&
            sidecar.SemanticFrames.Single(f => f.Tick == 149).Tick == 149 && slice.Window.EventStatesIncluded &&
            !timeline.Frames.Any(f => f.Tick == 149),
            "event replay state is added without rewriting original timeline");
        Console.WriteLine("Win event sampling/comparison checks passed: 20");
    }

    public static async Task VerifyRealAsync(string path, int stopTick, string output, CancellationToken token)
    {
        if (Directory.Exists(output) || File.Exists(output)) throw new InvalidOperationException("Output must be new.");
        Directory.CreateDirectory(output);
        var clock = Stopwatch.StartNew();
        async Task<DemoTimeline> Parse(bool events, int limit)
        {
            await using var stream = File.OpenRead(path);
            return await new DemoParserService().ParseAsync(stream, Path.GetFileName(path), token,
                collectSemantics: true, stopAfterTick: limit, collectWinEvents: events);
        }
        var baseline = await Parse(false, stopTick);
        var baselineSeconds = clock.Elapsed.TotalSeconds;
        Console.WriteLine($"Baseline parsed through {stopTick}: {baselineSeconds:F2}s");
        clock.Restart();
        var timeline = await Parse(true, stopTick);
        var eventSeconds = clock.Elapsed.TotalSeconds;
        Check(JsonSerializer.Serialize(baseline.Frames) == JsonSerializer.Serialize(timeline.Frames), "real regular replay parity");
        Check(JsonSerializer.Serialize(baseline.Semantics!.Frames) == JsonSerializer.Serialize(timeline.Semantics!.Frames), "real semantic parity");
        Check(JsonSerializer.Serialize(baseline.PlayerEquipmentStates) == JsonSerializer.Serialize(timeline.PlayerEquipmentStates), "real equipment history parity");
        var original = new WinFeatureSampleBuilder().Build(baseline).Samples;
        var hybrid = WinEventSampleBuilder.Build(timeline);
        Check(original.All(s => JsonNode.DeepEquals(s.Features, hybrid.Samples.Single(p => p.Tick == s.Tick).Features)), "real regular feature parity");
        var prefixEvent = timeline.WinEventStates.FirstOrDefault(s => timeline.Events.Any(e => e.Tick == s.Frame.Tick && e.Type == "bomb-defuse-abort"))
            ?? timeline.WinEventStates.Last(s => s.Frame.Tick < stopTick);
        var prefix = await Parse(true, prefixEvent.Frame.Tick);
        Check(JsonSerializer.Serialize(timeline.WinEventStates.Where(s => s.Frame.Tick <= prefixEvent.Frame.Tick)) ==
            JsonSerializer.Serialize(prefix.WinEventStates), "real future-prefix invariance");
        var audit = new
        {
            fileName = Path.GetFileName(path), stopTick, baselineSeconds, eventSeconds,
            regularPoints = original.Count, inferencePoints = hybrid.Samples.Count,
            curvePoints = hybrid.Triggers.Count(p => p.Value.Any(t => t is "regular" or "event-after")),
            confirmedTypes = timeline.Events.Where(e => WinEventSampleBuilder.IsKeyEvent(e.Type))
                .GroupBy(e => e.Type).ToDictionary(g => g.Key, g => new
                {
                    events = g.Count(), exactStates = g.Count(e => timeline.WinEventStates.Any(s => s.Frame.Tick == e.Tick)),
                    predicted = g.Count(e => hybrid.Triggers.TryGetValue(e.Tick, out var reasons) && reasons.Contains("event-after"))
                }),
            regularHistoryUnchanged = true, eventPrefixUnchanged = true, prefixTick = prefixEvent.Frame.Tick,
            groups = WinEventSampleBuilder.GroupEvents(timeline).Count
        };
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true };
        await File.WriteAllTextAsync(Path.Combine(output, "event-audit.json"), JsonSerializer.Serialize(audit, options), token);
        Console.WriteLine(JsonSerializer.Serialize(audit, options));
    }

    private static DemoTimeline Fixture()
    {
        var original = V4DataVerifier.CreateTimeline(1000);
        original = original with { PlayerEquipmentStates = [.. original.PlayerEquipmentStates,
            original.PlayerEquipmentStates.Single(e => e.PlayerId == "ct") with { PlayerId = "ct2" }] };
        var seed = original.Frames[0] with { Players = [.. original.Frames[0].Players, original.Frames[0].Players[1] with { Id = "ct2" }] };
        var semantic = original.Semantics!.Frames[0];
        DemoFrame Frame(int tick) => seed with { Tick = tick, TimeSeconds = tick / 64d,
            Players = seed.Players.Select(p => p.Id == "ct" && tick >= 75 ? p with { Alive = false, Health = 0 } : p).ToArray(),
            Bomb = seed.Bomb with { State = tick >= 78 ? "planted" : "carried", SecondsToExplosion = tick >= 78 ? 30 : null } };
        SemanticFrame Semantic(int tick) => semantic with { Tick = tick, Phase = tick >= 78 ? "post-plant" : "live",
            Roster = semantic.Roster with { AliveKnown = tick >= 75 ? 2 : 3,
                AlivePositionKnown = tick >= 75 ? 2 : 3, AliveEquipmentKnown = tick >= 75 ? 2 : 3 } };
        var ticks = Enumerable.Range(8, 24).Select(i => i * 8).ToArray();
        var events = new[] { new TimelineEvent(75, 75 / 64d, "kill", "击杀", SubjectId: "ct"),
            new TimelineEvent(75, 75 / 64d, "kill", "同 tick 事件", SubjectId: "ct"),
            new TimelineEvent(78, 78 / 64d, "bomb-planted", "下包"),
            new TimelineEvent(128, 2, "bomb-defuse-abort", "取消拆包"),
            new TimelineEvent(149, 149 / 64d, "bomb-defuse-start", "点包", SubjectId: "ct2"),
            new TimelineEvent(153, 153 / 64d, "bomb-defuse-abort", "松手"),
            new TimelineEvent(256, 4, "bomb-exploded", "爆炸") };
        return original with
        {
            Metadata = original.Metadata with { TotalTicks = 256, DurationSeconds = 4 },
            Frames = ticks.Select(Frame).ToArray(), Events = events,
            Semantics = original.Semantics with
            {
                Frames = ticks.Select(Semantic).ToArray(),
                Attempts = [new RoundAttempt { RoundId = "s0-a2", SegmentId = 0, RoundNumber = 1,
                    StartTick = 32, LiveTick = 64, EndTick = 256, Disposition = "completed", Winner = "T" }]
            },
            WinEventStates = events.Select(e => e.Tick).Distinct().Select(tick => new WinEventState(
                tick == 149 ? Frame(tick) with { Bomb = Frame(tick).Bomb with
                    { State = "defusing", DefuserId = "ct2", SecondsToDefuse = 5 } } : Frame(tick),
                Semantic(tick), original.PlayerEquipmentStates.Where(e => e.Tick == 32).Select(e => e with { Tick = tick, TimeSeconds = tick / 64d }).ToArray())).ToArray()
        };
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}

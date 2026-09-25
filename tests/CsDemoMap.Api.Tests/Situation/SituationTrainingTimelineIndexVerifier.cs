using CsDemoMap.Api.Models;
using CsDemoMap.Api.Services;

namespace CsDemoMap.Api.Tests;

internal static class SituationTrainingTimelineIndexVerifier
{
    internal static async Task VerifyAsync()
    {
        var checks = 0;
        var timeline = CreateTwoRoundTimeline();
        var index = SituationTrainingTimelineIndex.Create(timeline);

        VerifyIndexLookups(timeline, index, ref checks);
        VerifyEquivalentSelection(timeline, index, ref checks);
        checks += await VerifyParallelRoundsAsync(index);
        VerifyCancellation(index, ref checks);
        VerifyDuplicates(timeline, ref checks);
        VerifyCrossRoundIsolation(timeline, ref checks);

        Console.WriteLine($"Stage-four Timeline index checks passed: {checks}");
    }

    private static void VerifyIndexLookups(
        DemoTimeline timeline,
        SituationTrainingTimelineIndex index,
        ref int checks)
    {
        Check(index.CompletedAttempts.Select(item => item.RoundId).SequenceEqual(["s0-a2", "s0-a3"]),
            "completed attempts use stable round order", ref checks);
        Check(index.GetFrame(64).Tick == 64 && index.TryGetFrame(320, out var frame) && frame.Tick == 320,
            "frame lookup is indexed by tick", ref checks);
        Check(index.GetSemanticFrames("s0-a2").Select(item => item.Tick).SequenceEqual([64, 128]) &&
              index.GetSemanticFrames("s0-a3").Select(item => item.Tick).SequenceEqual([320, 384]),
            "semantic members are grouped and tick sorted", ref checks);
        Check(index.GetEventTicks("s0-a2", "kill").SequenceEqual([100]) &&
              index.GetEventTicks("s0-a3", "kill").SequenceEqual([350]) &&
              index.GetStructuredEvents("s0-a2").All(item =>
                  !item.Type.Contains("private", StringComparison.OrdinalIgnoreCase)),
            "structured event ticks are round isolated without event text", ref checks);

        var detached = index.GetCompletedAttempt("s0-a2");
        detached.Winner = "mutated";
        detached.Reason = "mutated";
        Check(index.GetCompletedAttempt("s0-a2").Winner is null &&
              index.GetCompletedAttempt("s0-a2").Reason is null &&
              timeline.Semantics!.Attempts.Single(item => item.RoundId == "s0-a2").Winner == "T",
            "attempt lookup is detached and excludes outcomes", ref checks);
    }

    private static void VerifyEquivalentSelection(
        DemoTimeline timeline,
        SituationTrainingTimelineIndex index,
        ref int checks)
    {
        using var sceneService = new SituationSceneService();
        var selector = new SituationTrainingCandidateSelector(sceneService);
        foreach (var attempt in timeline.Semantics!.Attempts
                     .Where(item => item.Disposition == "completed")
                     .OrderBy(item => item.StartTick))
        {
            var legacy = selector.SelectRound(timeline, "timeline-index-fixture", attempt);
            var indexed = selector.SelectRound(index, "timeline-index-fixture", attempt.RoundId);
            Check(indexed.SelectionResult.CanonicalJson == legacy.CanonicalJson &&
                  indexed.SelectionResult.Sha256 == legacy.Sha256,
                $"legacy and indexed selection match for {attempt.RoundId}", ref checks);
            Check(indexed.Payloads.Count == indexed.SelectionResult.Selection.Samples.Count &&
                  indexed.Payloads.Select(item => item.Tick).SequenceEqual(
                      indexed.SelectionResult.Selection.Samples.Select(item => item.Tick)),
                $"payloads align one-to-one for {attempt.RoundId}", ref checks);
            foreach (var payload in indexed.Payloads)
            {
                var selected = indexed.SelectionResult.Selection.Samples.Single(item => item.Tick == payload.Tick);
                var projected = SituationModelInputProjector.Project(payload.Scene);
                var input = new SituationTrainingInputV1(
                    projected,
                    payload.Facts,
                    payload.Facts.Evidence.Select(item => item.Id)
                        .Distinct(StringComparer.Ordinal)
                        .Order(StringComparer.Ordinal)
                        .ToArray());
                SituationContractValidator.Validate(
                    payload.Facts, payload.Scene, payload.Facts.AnalysisRuleVersion);
                SituationContractValidator.ValidateTemplate(payload.Narrative, payload.Facts);
                SituationTrainingContractJson.ValidateModelBoundary(input, payload.Narrative);
                Check(payload.RoundId == attempt.RoundId &&
                      payload.Phase == selected.Phase &&
                      payload.SemanticPhase == (selected.Phase == SituationTrainingPhase.PostPlant
                          ? "post-plant"
                          : "live") &&
                      payload.SelectionTags.SequenceEqual(selected.SelectionTags) &&
                      payload.SampleWeight == selected.SampleWeight &&
                      payload.WeightNumerator == selected.WeightNumerator &&
                      payload.WeightDenominator == selected.WeightDenominator &&
                      payload.SceneSha256 == SituationCanonicalJson.Sha256(payload.Scene) &&
                      payload.SceneCanonicalJson == SituationCanonicalJson.Serialize(payload.Scene) &&
                      payload.FactsSha256 == SituationCanonicalJson.Sha256(payload.Facts) &&
                      payload.FactsCanonicalJson == SituationCanonicalJson.Serialize(payload.Facts) &&
                      payload.NarrativeSha256 == SituationCanonicalJson.Sha256(payload.Narrative) &&
                      payload.NarrativeCanonicalJson == SituationCanonicalJson.Serialize(payload.Narrative) &&
                      ReferenceEquals(input.Facts, payload.Facts),
                    $"selected build payload is reusable at {attempt.RoundId}:{payload.Tick}", ref checks);
            }
        }
    }

    private static async Task<int> VerifyParallelRoundsAsync(
        SituationTrainingTimelineIndex index)
    {
        using var sceneService = new SituationSceneService();
        var selector = new SituationTrainingCandidateSelector(sceneService);
        var expected = index.CompletedAttempts.ToDictionary(
            attempt => attempt.RoundId,
            attempt => selector.SelectRound(index, "parallel-index-fixture", attempt.RoundId)
                .SelectionResult.Sha256,
            StringComparer.Ordinal);
        var tasks = index.CompletedAttempts.Select(attempt => Task.Run(() =>
            selector.SelectRound(index, "parallel-index-fixture", attempt.RoundId)
                .SelectionResult.Sha256)).ToArray();
        var actual = await Task.WhenAll(tasks);
        if (!actual.SequenceEqual(index.CompletedAttempts.Select(item => expected[item.RoundId])))
            throw new InvalidOperationException(
                "Timeline index check failed: parallel round selection shares the immutable index safely.");
        return 1;
    }

    private static void VerifyCancellation(
        SituationTrainingTimelineIndex index,
        ref int checks)
    {
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        CheckCanceled(() => SituationTrainingTimelineIndex.Create(index.Timeline, canceled.Token),
            "pre-canceled index construction", ref checks);
        using var sceneService = new SituationSceneService();
        var selector = new SituationTrainingCandidateSelector(sceneService);
        CheckCanceled(() => selector.SelectRound(
                index, "canceled-index-fixture", "s0-a2", canceled.Token),
            "pre-canceled indexed selection", ref checks);
        var afterCancellation = selector.SelectRound(
            index, "canceled-index-fixture", "s0-a2", CancellationToken.None);
        Check(afterCancellation.Payloads.Count > 0,
            "cancellation leaves the shared index reusable", ref checks);
    }

    private static void VerifyDuplicates(DemoTimeline timeline, ref int checks)
    {
        CheckThrows(() => SituationTrainingTimelineIndex.Create(timeline with
            {
                Frames = [.. timeline.Frames, timeline.Frames[0]]
            }), "duplicate source tick", ref checks);
        CheckThrows(() => SituationTrainingTimelineIndex.Create(timeline with
            {
                Semantics = timeline.Semantics! with
                {
                    Frames = [.. timeline.Semantics.Frames, timeline.Semantics.Frames[0]]
                }
            }), "duplicate semantic round/tick", ref checks);
        CheckThrows(() => SituationTrainingTimelineIndex.Create(timeline with
            {
                Semantics = timeline.Semantics! with
                {
                    Attempts = [.. timeline.Semantics.Attempts, CloneAttempt(timeline.Semantics.Attempts[0])]
                }
            }), "duplicate completed round", ref checks);

        var sharedTick = timeline.Semantics!.Frames[0] with { RoundId = "s0-a3", RoundNumber = 3 };
        var crossRound = SituationTrainingTimelineIndex.Create(timeline with
        {
            Semantics = timeline.Semantics with
            {
                Frames = [.. timeline.Semantics.Frames, sharedTick]
            }
        });
        Check(crossRound.GetSemanticFrames("s0-a2").Any(item => item.Tick == sharedTick.Tick) &&
              crossRound.GetSemanticFrames("s0-a3").Any(item => item.Tick == sharedTick.Tick),
            "same semantic tick may exist in distinct round groups", ref checks);
    }

    private static void VerifyCrossRoundIsolation(DemoTimeline timeline, ref int checks)
    {
        using var sceneService = new SituationSceneService();
        var selector = new SituationTrainingCandidateSelector(sceneService);
        var baselineIndex = SituationTrainingTimelineIndex.Create(timeline);
        var baseline = selector.SelectRound(
            baselineIndex, "round-isolation-fixture", "s0-a2").SelectionResult.Sha256;
        var changed = timeline with
        {
            Frames = timeline.Frames.Select(frame => frame.Tick < 320
                ? frame
                : frame with
                {
                    Players = frame.Players.Select(player => player with
                    {
                        Health = player.Team == "T" ? 1 : player.Health
                    }).ToArray()
                }).ToArray(),
            Events =
            [
                new(100, 100 / 64d, "kill", "private first", "private first detail"),
                new(321, 321 / 64d, "kill", "private second changed", "private second detail"),
                new(400, 400 / 64d, "future", "private future", "private future detail")
            ]
        };
        var changedIndex = SituationTrainingTimelineIndex.Create(changed);
        var actual = selector.SelectRound(
            changedIndex, "round-isolation-fixture", "s0-a2").SelectionResult.Sha256;
        Check(actual == baseline,
            "later-round snapshots, event text, and event ticks cannot affect an earlier round", ref checks);
    }

    private static DemoTimeline CreateTwoRoundTimeline()
    {
        var first = V4DataVerifier.CreateTimeline(9000);
        var firstAttempt = first.Semantics!.Attempts.Single();
        firstAttempt.Winner = "T";
        firstAttempt.Reason = "private-first-reason";
        var secondAttempt = new RoundAttempt
        {
            RoundId = "s0-a3",
            SegmentId = 0,
            RoundNumber = 3,
            StartTick = 288,
            LiveTick = 320,
            EndTick = 448,
            Disposition = "completed",
            Winner = "CT",
            Reason = "private-second-reason"
        };
        var secondFrames = first.Frames.Select(frame => frame with
        {
            Tick = frame.Tick + 256,
            TimeSeconds = (frame.Tick + 256) / 64d,
            Round = frame.Round with { Number = 3 }
        }).ToArray();
        var secondSemantics = first.Semantics.Frames.Select(frame => frame with
        {
            Tick = frame.Tick + 256,
            RoundId = secondAttempt.RoundId,
            RoundNumber = 3
        }).ToArray();
        return first with
        {
            Metadata = first.Metadata with { TotalTicks = 512, DurationSeconds = 8 },
            Frames = secondFrames.Concat(first.Frames).Reverse().ToArray(),
            Events =
            [
                new(350, 350 / 64d, "kill", "private second", "private second detail"),
                new(100, 100 / 64d, "kill", "private first", "private first detail"),
                new(250, 250 / 64d, "outside", "private gap", "private gap detail")
            ],
            Semantics = new(
                [secondAttempt, firstAttempt],
                secondSemantics.Concat(first.Semantics.Frames).Reverse().ToArray(),
                [])
        };
    }

    private static RoundAttempt CloneAttempt(RoundAttempt source) => new()
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
        Winner = source.Winner,
        Reason = source.Reason
    };

    private static void CheckThrows(Action action, string label, ref int checks)
    {
        try
        {
            action();
        }
        catch (InvalidDataException)
        {
            checks++;
            return;
        }
        throw new InvalidOperationException($"Timeline index check failed: {label}.");
    }

    private static void CheckCanceled(Action action, string label, ref int checks)
    {
        try
        {
            action();
        }
        catch (OperationCanceledException)
        {
            checks++;
            return;
        }
        throw new InvalidOperationException($"Timeline index check failed: {label}.");
    }

    private static void Check(bool condition, string label, ref int checks)
    {
        if (!condition)
            throw new InvalidOperationException($"Timeline index check failed: {label}.");
        checks++;
    }
}

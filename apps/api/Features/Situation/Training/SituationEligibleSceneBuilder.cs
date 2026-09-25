using CsDemoMap.Api.Models;

namespace CsDemoMap.Api.Services;

internal sealed record SituationEligibleSceneBuildResult(
    RoundSampleEligibilityResult Eligibility,
    SituationSceneBuildResult? Scene);

/// <summary>
/// Stage-four bridge from the shared semantic gate to the normal as-of scene pipeline.
/// </summary>
internal sealed class SituationEligibleSceneBuilder
{
    private readonly SituationSceneService sceneService;

    internal SituationEligibleSceneBuilder(SituationSceneService sceneService)
    {
        ArgumentNullException.ThrowIfNull(sceneService);
        this.sceneService = sceneService;
    }

    internal SituationEligibleSceneBuildResult Build(
        DemoTimeline timeline,
        string demoRef,
        int windowIndex,
        RoundAttempt attempt,
        SemanticFrame frame,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(timeline);
        cancellationToken.ThrowIfCancellationRequested();
        var snapshot = timeline.Frames.SingleOrDefault(item => item.Tick == frame.Tick)
            ?? throw new InvalidDataException($"Timeline frame {frame.Tick} is missing.");
        return Build(
            timeline, demoRef, windowIndex, attempt, frame, snapshot, cancellationToken);
    }

    internal SituationEligibleSceneBuildResult Build(
        DemoTimeline timeline,
        string demoRef,
        int windowIndex,
        RoundAttempt attempt,
        SemanticFrame frame,
        DemoFrame snapshot,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(timeline);
        ArgumentNullException.ThrowIfNull(snapshot);
        cancellationToken.ThrowIfCancellationRequested();
        var eligibility = RoundSampleEligibility.Evaluate(
            timeline.Metadata.MapName, attempt, frame, snapshot);
        if (!eligibility.Eligible)
            return new(eligibility, null);

        // BuildFromTimeline is the mandatory boundary: it applies the existing
        // requested-tick history/equipment/utility filters to the complete timeline.
        var scene = sceneService.BuildFromTimeline(
            timeline,
            demoRef,
            windowIndex,
            frame.Tick,
            frame,
            cancellationToken: cancellationToken);
        return new(eligibility, scene);
    }
}

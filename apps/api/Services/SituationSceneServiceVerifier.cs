using CsDemoMap.Api.Models;
using System.Text.Json;

namespace CsDemoMap.Api.Services;

internal static class SituationSceneServiceVerifier
{
    public static async Task VerifyAsync(CancellationToken cancellationToken)
    {
        var checks = 0;
        var timeline = SituationFlowVerifier.BuildBoundaryTimeline(
            reversePlayers: false,
            reverseRelations: false);
        var offlineService = new SituationSceneService();
        var timelineResult = offlineService.BuildFromTimeline(
            timeline,
            "service-fixture",
            1,
            1940,
            cancellationToken: cancellationToken);

        Check(timelineResult.Scene.Tick == 1936,
            "timeline entry resolves the as-of frame", ref checks);
        Check(timelineResult.CanonicalJson == SituationCanonicalJson.Serialize(timelineResult.Scene),
            "service returns canonical JSON", ref checks);
        Check(timelineResult.Sha256 == SituationCanonicalJson.Sha256(timelineResult.Scene),
            "service returns the canonical SHA-256", ref checks);
        Check(timelineResult.Scene.RequestedTick == 1940 &&
                timelineResult.Scene.Tick == 1936 &&
                timelineResult.Scene.DataQuality.Any(item =>
                    item.Code == SituationDataQualityCodes.StaleFrame),
            "requested and actual ticks remain distinct with stale-frame quality", ref checks);
        Check(timelineResult.Scene.Players.All(player =>
                player.Trajectory.CoverageSeconds <= SituationInputAdapter.HistorySeconds &&
                player.Trajectory.Points.All(point =>
                    point.Tick >= 1648 && point.Tick <= timelineResult.Scene.Tick)),
            "history is bounded to the as-of 4.5 second prefix", ref checks);
        Check(timelineResult.Scene.Utilities.All(utility =>
                utility.ObservedStartTick <= timelineResult.Scene.Tick &&
                utility.Trajectory.Points.All(point => point.Tick <= timelineResult.Scene.Tick)) &&
                timelineResult.Scene.Effects.All(effect =>
                    effect.ObservedStartTick <= timelineResult.Scene.Tick &&
                    effect.SampleTick <= timelineResult.Scene.Tick),
            "active utilities and effects contain no future samples", ref checks);
        Check(timelineResult.Scene.SchemaVersion == SituationContractVersions.Scene &&
                timelineResult.Scene.SceneBuilderVersion == SituationSceneBuilder.BuilderVersion &&
                timelineResult.Scene.GeometryVersion == SituationSceneBuilder.GeometryVersion &&
                timelineResult.Scene.Players.All(player => player.Floor == SituationFloor.Unknown),
            "frozen contract, builder, geometry and unknown-floor semantics are preserved", ref checks);

        var firstT = timelineResult.Scene.Players.Single(player => player.Side == SituationSide.T);
        Check(firstT.Position == new SituationVec3(0.630859, 0.33457, 0) &&
                firstT.Velocity == new SituationVec3(0.1, -0.1, 0) &&
                firstT.Heading == new SituationVec2(0, -1),
            "the service retains the single Mirage geometry implementation", ref checks);

        var modelInput = SituationModelInputProjector.Serialize(timelineResult.Scene);
        Check(!ContainsAny(
                timelineResult.CanonicalJson,
                "Private T", "Private CT", "t-id", "ct-id", "private.dem", "winner", "winPrediction") &&
                !ContainsAny(
                    modelInput,
                    "demoRef", "windowIndex", "requestedTick", "private.dem", "winner", "winPrediction"),
            "scene and model input exclude identity, result, path and prediction data", ref checks);

        var unknownTimeline = timeline with
        {
            PlayerEquipmentStates = [],
            Semantics = timeline.Semantics! with
            {
                Frames = timeline.Semantics!.Frames.Select(frame => frame with
                {
                    Clock = frame.Clock with
                    {
                        LiveElapsedSeconds = double.NaN,
                        RoundRemainingSeconds = -1,
                        ClockKnown = false
                    },
                    Roster = frame.Roster with { AliveEquipmentKnown = 0 }
                }).ToArray()
            }
        };
        var unknownResult = offlineService.BuildFromTimeline(
            unknownTimeline,
            "unknown-fixture",
            1,
            1940,
            cancellationToken: cancellationToken);
        Check(unknownResult.Scene.Players.All(player => player.Armor is null) &&
                ContainsQuality(unknownResult.Scene, SituationDataQualityCodes.EquipmentUnknown) &&
                ContainsQuality(unknownResult.Scene, SituationDataQualityCodes.ClockUnknown) &&
                ContainsQuality(unknownResult.Scene, SituationDataQualityCodes.LegacyDefaultAmbiguous) &&
                ContainsQuality(unknownResult.Scene, SituationDataQualityCodes.FloorUnknown),
            "null, unknown and legacy-default quality remain explicit", ref checks);

        var expiredTimeline = timeline with
        {
            UtilityTracks = timeline.UtilityTracks.Select(track =>
                track with { EndTick = timelineResult.Scene.Tick }).ToArray(),
            UtilityEffects = timeline.UtilityEffects.Select(effect =>
                effect with { EndTick = timelineResult.Scene.Tick }).ToArray()
        };
        var expiredResult = offlineService.BuildFromTimeline(
            expiredTimeline,
            "expired-fixture",
            1,
            1940,
            cancellationToken: cancellationToken);
        Check(expiredResult.Scene.Utilities.Count == 0 && expiredResult.Scene.Effects.Count == 0,
            "effect lifetimes use an exclusive end tick", ref checks);

        var futurePerturbed = timeline with
        {
            PlayerUtilityStates = timeline.PlayerUtilityStates.Concat([
                new PlayerUtilityState(2000, 31.25, "t-id", [new("flash", 9)])
            ]).ToArray(),
            PlayerEquipmentStates = timeline.PlayerEquipmentStates.Concat([
                timeline.PlayerEquipmentStates[0] with { Tick = 2000, TimeSeconds = 31.25, Money = 16000 }
            ]).ToArray(),
            Events = [new(2100, 32.8125, "round_end", "Private future winner", "future")],
            RoundResults = [new(8, 1500, 1500, 2100, "CT", "future-result")]
        };
        var futureResult = offlineService.BuildFromTimeline(
            futurePerturbed,
            "service-fixture",
            1,
            1940,
            cancellationToken: cancellationToken);
        Check(futureResult.CanonicalJson == timelineResult.CanonicalJson,
            "future state, events and results do not change an as-of scene", ref checks);

        CheckThrows<ArgumentOutOfRangeException>(
            () => offlineService.BuildFromTimeline(timeline, "invalid", -1, 0),
            "negative window indexes are rejected", ref checks);
        CheckThrows<ArgumentOutOfRangeException>(
            () => offlineService.BuildFromTimeline(timeline, "invalid", 1, -1),
            "negative ticks are rejected", ref checks);
        CheckThrows<ArgumentOutOfRangeException>(
            () => offlineService.BuildFromTimeline(timeline, "invalid", 0, 1940),
            "ticks outside the selected core window are rejected", ref checks);
        CheckThrows<OverflowException>(
            () => offlineService.BuildFromTimeline(timeline, "invalid", int.MaxValue, int.MaxValue),
            "window tick arithmetic overflow is rejected", ref checks);
        CheckThrows<NotSupportedException>(
            () => offlineService.BuildFromTimeline(
                timeline with { Metadata = timeline.Metadata with { MapName = "de_dust2" } },
                "invalid",
                1,
                1940),
            "unsupported maps are rejected before construction", ref checks);
        CheckThrows<InvalidDataException>(
            () => offlineService.BuildFromTimeline(
                timeline with { Metadata = timeline.Metadata with { DurationSeconds = double.NaN } },
                "invalid",
                1,
                1940),
            "invalid demo metadata is rejected", ref checks);
        CheckThrows<InvalidDataException>(
            () => offlineService.BuildFromTimeline(
                timeline with { Frames = timeline.Frames.Where(frame => frame.Tick > 1940).ToArray() },
                "invalid",
                1,
                1940),
            "a core window without an as-of frame is rejected", ref checks);

        var previous = SituationFlowVerifier.BuildWindow(timeline, 0, 0, 2048);
        var current = SituationFlowVerifier.BuildWindow(timeline, 1, 1792, 2560);
        var previousSidecar = SituationWindowSidecarStore.Build(timeline, 0, 0, 2048);
        var currentSidecar = SituationWindowSidecarStore.Build(timeline, 1, 1792, 2560);
        var windowResult = offlineService.BuildFromWindows(
            timeline.Metadata,
            "service-fixture",
            1,
            1940,
            [previous, current],
            [previousSidecar, currentSidecar],
            cancellationToken);
        Check(windowResult.CanonicalJson == timelineResult.CanonicalJson &&
                windowResult.Sha256 == timelineResult.Sha256,
            "timeline and validated window entries share one output pipeline", ref checks);
        Check(ScenesDeepEqual(windowResult.Scene, timelineResult.Scene),
            "timeline and validated window entries are deeply equal", ref checks);
        Check(windowResult.Scene.Players.All(player => player.Trajectory.CoverageSeconds >= 4.4),
            "stored overlap is extended with the previous window", ref checks);

        CheckThrows<InvalidDataException>(
            () => offlineService.BuildFromWindows(
                timeline.Metadata,
                "invalid-window",
                1,
                1940,
                [previous, current with { CoreFromSeconds = 31 }],
                [previousSidecar, currentSidecar]),
            "window core bounds are validated", ref checks);
        CheckThrows<InvalidDataException>(
            () => offlineService.BuildFromWindows(
                timeline.Metadata,
                "conflicting-frame",
                1,
                1940,
                [previous, current with
                {
                    Frames = current.Frames.Select(frame => frame.Tick == 1936
                        ? frame with
                        {
                            Players = frame.Players.Select((player, index) => index == 0
                                ? player with { Health = 99 }
                                : player).ToArray()
                        }
                        : frame).ToArray()
                }],
                [previousSidecar, currentSidecar]),
            "conflicting duplicate frames are rejected", ref checks);
        CheckThrows<InvalidDataException>(
            () => offlineService.BuildFromWindows(
                timeline.Metadata,
                "conflicting-utility-state",
                1,
                1940,
                [previous, current with
                {
                    PlayerUtilityStates = current.PlayerUtilityStates.Select(state =>
                        state.PlayerId == "t-id"
                            ? state with { Items = [new("flash", 2)] }
                            : state).ToArray()
                }],
                [previousSidecar, currentSidecar]),
            "conflicting duplicate inventory states are rejected", ref checks);
        CheckThrows<InvalidDataException>(
            () => offlineService.BuildFromWindows(
                timeline.Metadata,
                "conflicting-equipment-state",
                1,
                1940,
                [previous, current with
                {
                    PlayerEquipmentStates = current.PlayerEquipmentStates.Select(state =>
                        state.PlayerId == "t-id" ? state with { Money = state.Money + 1 } : state).ToArray()
                }],
                [previousSidecar, currentSidecar]),
            "conflicting duplicate equipment states are rejected", ref checks);
        CheckThrows<InvalidDataException>(
            () => offlineService.BuildFromWindows(
                timeline.Metadata,
                "conflicting-effect",
                1,
                1940,
                [previous, current with
                {
                    UtilityEffects = current.UtilityEffects.Select(effect =>
                        effect with { Team = "CT" }).ToArray()
                }],
                [previousSidecar, currentSidecar]),
            "conflicting duplicate effects are rejected", ref checks);
        CheckThrows<InvalidDataException>(
            () => offlineService.BuildFromWindows(
                timeline.Metadata,
                "conflicting-semantic",
                1,
                1940,
                [previous, current],
                [previousSidecar, currentSidecar with
                {
                    SemanticFrames = currentSidecar.SemanticFrames.Select(frame =>
                        frame.Tick == 1936 ? frame with { Phase = "freeze" } : frame).ToArray()
                }]),
            "conflicting duplicate semantic observations are rejected", ref checks);

        var directory = Path.Combine(
            Path.GetTempPath(),
            $"situation-scene-service-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            await SituationFlowVerifier.WriteWindowAsync(directory, previous);
            await SituationFlowVerifier.WriteWindowAsync(directory, current);
            await SituationWindowSidecarStore.WriteAsync(
                directory, previousSidecar, cancellationToken);
            await SituationWindowSidecarStore.WriteAsync(
                directory, currentSidecar, cancellationToken);

            var runtimeService = new SituationSceneService(id => id == "import-fixture"
                ? new("service-fixture", "completed", directory, timeline.Metadata, 2)
                : null);
            var runtimeResult = await runtimeService.BuildFromImportedDemoAsync(
                "import-fixture", 1, 1940, cancellationToken);
            Check(runtimeResult.CanonicalJson == timelineResult.CanonicalJson &&
                    runtimeResult.Sha256 == timelineResult.Sha256,
                "runtime import entry matches the offline timeline entry", ref checks);
            Check(ScenesDeepEqual(runtimeResult.Scene, timelineResult.Scene),
                "runtime import and offline timeline scenes are deeply equal", ref checks);
            checks += await CheckSituationErrorAsync(
                () => runtimeService.BuildFromImportedDemoAsync(
                    "import-fixture", 2, 3840, cancellationToken),
                SituationSceneErrorCode.InvalidWindow,
                "runtime window indexes are checked against the import manifest");

            var missingWindowService = new SituationSceneService(_ =>
                new("service-fixture", "completed", directory, timeline.Metadata, 3));
            checks += await CheckSituationErrorAsync(
                () => missingWindowService.BuildFromImportedDemoAsync(
                    "import-fixture", 2, 3840, cancellationToken),
                SituationSceneErrorCode.CorruptData,
                "missing replay windows are explicit");

            checks += await CheckSituationErrorAsync(
                () => runtimeService.BuildFromImportedDemoAsync(
                    "missing", 1, 1940, cancellationToken),
                SituationSceneErrorCode.ImportNotFound,
                "unknown imports are rejected");

            var pendingService = new SituationSceneService(_ =>
                new("service-fixture", "parsing", directory, null, null));
            checks += await CheckSituationErrorAsync(
                () => pendingService.BuildFromImportedDemoAsync(
                    "import-fixture", 1, 1940, cancellationToken),
                SituationSceneErrorCode.ImportNotReady,
                "incomplete imports are rejected");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }

        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        CheckThrows<OperationCanceledException>(
            () => offlineService.BuildFromTimeline(
                timeline,
                "service-fixture",
                1,
                1940,
                cancellationToken: canceled.Token),
            "offline entry preserves cancellation",
            ref checks);

        Console.WriteLine($"Situation scene service checks passed: {checks}");
    }

    private static bool ContainsQuality(MinimapSceneV1 scene, string code) =>
        scene.DataQuality.Any(item => string.Equals(item.Code, code, StringComparison.Ordinal));

    private static bool ScenesDeepEqual(MinimapSceneV1 first, MinimapSceneV1 second) =>
        JsonElement.DeepEquals(
            JsonSerializer.SerializeToElement(first),
            JsonSerializer.SerializeToElement(second));

    private static bool ContainsAny(string value, params string[] needles) =>
        needles.Any(needle => value.Contains(needle, StringComparison.OrdinalIgnoreCase));

    private static void Check(bool condition, string label, ref int checks)
    {
        if (!condition)
            throw new InvalidOperationException($"Situation scene service check failed: {label}.");
        checks++;
    }

    private static void CheckThrows<T>(Action action, string label, ref int checks)
        where T : Exception
    {
        try
        {
            action();
        }
        catch (T)
        {
            checks++;
            return;
        }

        throw new InvalidOperationException($"Situation scene service check failed: {label}.");
    }

    private static async Task<int> CheckThrowsAsync<T>(
        Func<Task> action,
        string label)
        where T : Exception
    {
        try
        {
            await action();
        }
        catch (T)
        {
            return 1;
        }

        throw new InvalidOperationException($"Situation scene service check failed: {label}.");
    }

    private static async Task<int> CheckSituationErrorAsync(
        Func<Task> action,
        SituationSceneErrorCode expectedCode,
        string label)
    {
        try
        {
            await action();
        }
        catch (SituationSceneException exception) when (exception.Code == expectedCode)
        {
            return 1;
        }

        throw new InvalidOperationException($"Situation scene service check failed: {label}.");
    }
}

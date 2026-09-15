using System.IO.Compression;
using System.Globalization;
using System.Text.Json;
using CsDemoMap.Api.Models;

namespace CsDemoMap.Api.Services;

internal static class SituationFlowVerifier
{
    public static int Verify()
    {
        var checks = 0;
        var timeline = BuildBoundaryTimeline(reversePlayers: false, reverseRelations: false);
        var adapter = new SituationInputAdapter();
        var builder = new SituationSceneBuilder();
        var fullScene = builder.Build(adapter.BuildFromTimeline(timeline, "boundary-fixture", 1, 1940));
        Check(fullScene.Bomb.CarrierSlot is { } carrier &&
                fullScene.Players.Single(player => player.Slot == carrier).Side == SituationSide.T &&
                fullScene.Teams.T.Alive == fullScene.Players.Count(player => player.Side == SituationSide.T) &&
                fullScene.Teams.CT.Alive == fullScene.Players.Count(player => player.Side == SituationSide.CT) &&
                fullScene.Geometry.RegionOccupancy.All(region =>
                    region.TAlive == fullScene.Players.Count(player =>
                        player.Side == SituationSide.T && player.Region == region.Region) &&
                    region.CTAlive == fullScene.Players.Count(player =>
                        player.Side == SituationSide.CT && player.Region == region.Region)),
            "C4, team totals and occupancy share the actual scene players", ref checks);

        var windowTail = builder.Build(adapter.BuildFromTimeline(
            timeline, "window-tail-fixture", 0, 1919));
        var windowHead = builder.Build(adapter.BuildFromTimeline(
            timeline, "window-head-fixture", 1, 1920));
        Check(windowTail.Tick < 1920 && windowHead.Tick >= 1920,
            "adjacent window tail and head stay inside their core ranges", ref checks);

        var fileBoundaryScene = builder.Build(adapter.BuildFromTimeline(
            timeline, "file-boundary-fixture", 0, 1700));
        Check(fileBoundaryScene.DataQuality.Any(item => item.Code == SituationDataQualityCodes.HistoryTruncated) &&
                fileBoundaryScene.DataQuality.Any(item => item.Code == SituationDataQualityCodes.HistoryFileBoundary) &&
                fileBoundaryScene.Players.All(player => player.Trajectory.CoverageSeconds < 4.5),
            "file boundary reports its reason and actual history coverage", ref checks);

        var missingEquipmentTimeline = timeline with
        {
            PlayerEquipmentStates = timeline.PlayerEquipmentStates
                .Where(state => state.PlayerId != "t-id").ToArray()
        };
        var missingEquipmentScene = builder.Build(adapter.BuildFromTimeline(
            missingEquipmentTimeline, "missing-equipment-fixture", 1, 1940));
        var missingT = missingEquipmentScene.Players.Single(player => player.Side == SituationSide.T);
        Check(missingT.Armor is null &&
                missingEquipmentScene.DataQuality.Any(item =>
                    item.Code == SituationDataQualityCodes.EquipmentUnknown &&
                    item.FieldPaths.Contains("/teams/t/totalMoney", StringComparer.Ordinal)),
            "missing equipment stays null with field-level paths", ref checks);

        var previous = BuildWindow(timeline, 0, 0, 2048);
        var current = BuildWindow(timeline, 1, 1792, 2560);
        var previousSidecar = SituationWindowSidecarStore.Build(timeline, 0, 0, 2048);
        var currentSidecar = SituationWindowSidecarStore.Build(timeline, 1, 1792, 2560);
        var mergedInput = new SituationWindowInputAdapter().BuildFromWindows(
            timeline.Metadata, "boundary-fixture", 1, 1940,
            [previous, current], [previousSidecar, currentSidecar]);
        var mergedScene = builder.Build(mergedInput);
        Check(
            SituationCanonicalJson.Serialize(fullScene) == SituationCanonicalJson.Serialize(mergedScene),
            "previous replay window restores the full 4.5 second scene prefix",
            ref checks);
        Check(mergedScene.Players.All(player => player.Trajectory.CoverageSeconds >= 4.4),
            "cross-window trajectory reports actual coverage", ref checks);
        Check(mergedInput.Semantic?.RoundRef == "s0-a8",
            "partial round boundaries merge without changing the active round", ref checks);

        var forbiddenSidecarText = JsonSerializer.Serialize(previousSidecar);
        Check(!ContainsAny(forbiddenSidecarText,
                "winner", "\"reason\":", "disposition", "throwerName", "playerId", "prediction"),
            "sidecar has no result, identity or prediction fields", ref checks);
        CheckThrows(
            () => SituationWindowSidecarStore.ReadAsync(
                    Path.Combine(Path.GetTempPath(), $"missing-situation-sidecar-{Guid.NewGuid():N}"),
                    0, CancellationToken.None)
                .GetAwaiter().GetResult(),
            "missing sidecar is explicit", ref checks);

        var storedDirectory = Path.Combine(
            Path.GetTempPath(), $"situation-window-fixture-{Guid.NewGuid():N}");
        Directory.CreateDirectory(storedDirectory);
        try
        {
            WriteWindowAsync(storedDirectory, previous).GetAwaiter().GetResult();
            WriteWindowAsync(storedDirectory, current).GetAwaiter().GetResult();
            SituationWindowSidecarStore.WriteAsync(
                storedDirectory, previousSidecar, CancellationToken.None).GetAwaiter().GetResult();
            SituationWindowSidecarStore.WriteAsync(
                storedDirectory, currentSidecar, CancellationToken.None).GetAwaiter().GetResult();
            var storedInput = new SituationWindowInputAdapter().BuildFromStoredWindowsAsync(
                storedDirectory, timeline.Metadata, "boundary-fixture", 1, 1940)
                .GetAwaiter().GetResult();
            Check(SituationCanonicalJson.Serialize(builder.Build(storedInput)) ==
                    SituationCanonicalJson.Serialize(fullScene),
                "stored adapter reads the previous Brotli window on demand", ref checks);
        }
        finally
        {
            Directory.Delete(storedDirectory, recursive: true);
        }

        var incompleteDirectory = Path.Combine(
            Path.GetTempPath(), $"situation-window-incomplete-{Guid.NewGuid():N}");
        Directory.CreateDirectory(incompleteDirectory);
        try
        {
            WriteWindowAsync(incompleteDirectory, current).GetAwaiter().GetResult();
            SituationWindowSidecarStore.WriteAsync(
                incompleteDirectory, currentSidecar, CancellationToken.None).GetAwaiter().GetResult();
            CheckThrows(
                () => new SituationWindowInputAdapter().BuildFromStoredWindowsAsync(
                        incompleteDirectory, timeline.Metadata, "boundary-fixture", 1, 1940)
                    .GetAwaiter().GetResult(),
                "required previous replay window is explicit", ref checks);
        }
        finally
        {
            Directory.Delete(incompleteDirectory, recursive: true);
        }

        var conflictingPrevious = previous with
        {
            Frames = previous.Frames.Select(frame => frame.Tick == 1936
                ? frame with
                {
                    Players = frame.Players.Select((player, index) => index == 0
                        ? player with { Health = 99 }
                        : player).ToArray()
                }
                : frame).ToArray()
        };
        CheckThrows(
            () => new SituationWindowInputAdapter().BuildFromWindows(
                timeline.Metadata, "boundary-fixture", 1, 1940,
                [conflictingPrevious, current], [previousSidecar, currentSidecar]),
            "conflicting duplicate frame is rejected", ref checks);

        var firstT = fullScene.Players.Single(player => player.Side == SituationSide.T);
        Check(firstT.Position == new SituationVec3(0.630859, 0.33457, 0),
            "Mirage position formula", ref checks);
        Check(firstT.Velocity == new SituationVec3(0.1, -0.1, 0),
            "Mirage velocity formula", ref checks);
        Check(firstT.Heading == new SituationVec2(0, -1),
            "parser yaw direction formula", ref checks);

        var outsideTimeline = ReplaceFirstT(timeline, player => player with { X = 3000 });
        var outsideScene = builder.Build(adapter.BuildFromTimeline(
            outsideTimeline, "outside-fixture", 1, 1940));
        Check(outsideScene.Players.Single(player => player.Side == SituationSide.T).Position!.X > 1,
            "finite outside-radar coordinate is preserved", ref checks);
        Check(outsideScene.DataQuality.Any(item =>
                item.Code == SituationDataQualityCodes.PositionOutsideRadar &&
                item.FieldPaths.Any(path => path.EndsWith("/position", StringComparison.Ordinal))),
            "outside-radar coordinate has field quality", ref checks);

        var nonFiniteTimeline = ReplaceFirstT(timeline, player => player with { X = float.NaN });
        var nonFiniteScene = builder.Build(adapter.BuildFromTimeline(
            nonFiniteTimeline, "nonfinite-fixture", 1, 1940));
        Check(nonFiniteScene.Players.Single(player => player.Side == SituationSide.T).Position is null,
            "non-finite coordinate becomes null", ref checks);

        var bombCases = new Dictionary<SituationBombState, BombSnapshot>
        {
            [SituationBombState.Dropped] = new(
                "dropped", null, null, null, "Mid", 0, 0, 0, null, null),
            [SituationBombState.Planting] = new(
                "planting", "t-id", null, null, "A Site", 0, 0, 0, null, null),
            [SituationBombState.Planted] = new(
                "planted", null, null, "A", "A Site", 0, 0, 0, 35, null),
            [SituationBombState.Defusing] = new(
                "defusing", null, "ct-id", "A", "A Site", 0, 0, 0, 20, 5)
        };
        foreach (var (expectedState, bomb) in bombCases)
        {
            var stateScene = builder.Build(adapter.BuildFromTimeline(
                ReplaceBomb(timeline, _ => bomb), $"bomb-{expectedState}", 1, 1940));
            SituationContractValidator.Validate(stateScene);
            Check(stateScene.Bomb.State == expectedState,
                $"C4 state {expectedState} is preserved", ref checks);
        }
        var pickupScene = builder.Build(adapter.BuildFromTimeline(
            ReplaceBomb(timeline, frame => frame.Tick < 1900
                ? bombCases[SituationBombState.Dropped]
                : new("carried", "t-id", null, null, "T Spawn", 0, 0, 0, null, null)),
            "bomb-pickup", 1, 1940));
        Check(pickupScene.Bomb.State == SituationBombState.Carried && pickupScene.Bomb.CarrierSlot is not null,
            "C4 pickup uses the current observation", ref checks);

        var lifecycleTimeline = timeline with
        {
            UtilityTracks = [
                .. timeline.UtilityTracks,
                new UtilityTrack(
                    "start-projectile", "flash", "t-id", "Private T", "T",
                    fullScene.Tick, fullScene.Tick + 64, null,
                    [new(fullScene.Tick, fullScene.Tick / 64d, 0, 0, 0)])
            ],
            UtilityEffects = [
                .. timeline.UtilityEffects,
                new UtilityEffectTrack(
                    "start-fire", "fire", "ct-id", "Private CT", "CT",
                    fullScene.Tick, fullScene.Tick + 64,
                    [new(fullScene.Tick, fullScene.Tick / 64d, 0, 0, 0, 96,
                        [new(0, 0, 0), new(32, 0, 0), new(0, 32, 0)])])
            ]
        };
        var lifecycleInput = adapter.BuildFromTimeline(
            lifecycleTimeline, "lifecycle-fixture", 1, 1940);
        var lifecycleScene = builder.Build(lifecycleInput);
        Check(lifecycleScene.Utilities.Any(item => item.Type == SituationUtilityType.Flash &&
                item.ObservedStartTick == lifecycleScene.Tick) &&
                lifecycleScene.Effects.Any(item => item.Type == SituationEffectType.Smoke) &&
                lifecycleScene.Effects.Any(item => item.Type == SituationEffectType.Fire &&
                    item.ObservedStartTick == lifecycleScene.Tick),
            "projectiles, smoke and fire are active at their inclusive start tick", ref checks);

        var artificialInput = adapter.BuildFromTimeline(
            lifecycleTimeline,
            "lifecycle-fixture",
            1,
            1940,
            SceneObservationBoundary.ArtificialPrefixEnd);
        var artificialScene = builder.Build(artificialInput);
        Check(artificialInput.ActiveUtilities.All(item =>
                    item.ObservationBoundary == SceneObservationBoundary.ArtificialPrefixEnd) &&
                artificialInput.ActiveEffects.All(item =>
                    item.ObservationBoundary == SceneObservationBoundary.ArtificialPrefixEnd) &&
                SituationCanonicalJson.Serialize(artificialScene) ==
                    SituationCanonicalJson.Serialize(lifecycleScene),
            "artificial prefix endings preserve active lifecycle provenance and scene output", ref checks);

        var deadCtTimeline = timeline with
        {
            Frames = timeline.Frames.Select(frame => frame with
            {
                Players = frame.Players.Select(player => player.Team == "CT"
                    ? player with { Alive = false, Health = 0 }
                    : player).ToArray()
            }).ToArray()
        };
        var deadCtScene = builder.Build(adapter.BuildFromTimeline(
            deadCtTimeline, "death-fixture", 1, 1940));
        Check(deadCtScene.Teams.CT.Alive == 0 && deadCtScene.Players.All(player => player.Side != SituationSide.CT) &&
                deadCtScene.Geometry.PlayerDistances.Single().NearestAlly is null,
            "death and absent ally geometry use the current frame", ref checks);

        var priorEquipment = timeline.PlayerEquipmentStates.First(state => state.PlayerId == "t-id") with
        {
            Tick = 1400,
            TimeSeconds = 1400 / 64d
        };
        var priorUtility = timeline.PlayerUtilityStates.First(state => state.PlayerId == "t-id") with
        {
            Tick = 1400,
            TimeSeconds = 1400 / 64d
        };
        var priorRoundStateTimeline = timeline with
        {
            PlayerEquipmentStates = [
                priorEquipment,
                .. timeline.PlayerEquipmentStates.Where(state => state.PlayerId != "t-id")
            ],
            PlayerUtilityStates = [
                priorUtility,
                .. timeline.PlayerUtilityStates.Where(state => state.PlayerId != "t-id")
            ]
        };
        var priorRoundStateScene = builder.Build(adapter.BuildFromTimeline(
            priorRoundStateTimeline, "prior-round-state", 1, 1940));
        var priorRoundT = priorRoundStateScene.Players.Single(player => player.Side == SituationSide.T);
        var observedZeroCt = priorRoundStateScene.Players.Single(player => player.Side == SituationSide.CT);
        Check(priorRoundT.Armor is null && UtilityValues(priorRoundT.UtilityCounts).All(value => value is null) &&
                UtilityValues(observedZeroCt.UtilityCounts).All(value => value == 0),
            "prior-round state is not inherited and observed empty inventory remains known zero", ref checks);

        var segmentedTimeline = timeline with
        {
            Semantics = new(
                [
                    new RoundAttempt
                    {
                        RoundId = "s0-a8", SegmentId = 0, RoundNumber = 8,
                        StartTick = 1500, LiveTick = 1500, EndTick = 1839
                    },
                    new RoundAttempt
                    {
                        RoundId = "s1-a8", SegmentId = 1, RoundNumber = 8,
                        StartTick = 1840, LiveTick = 1840
                    }
                ],
                timeline.Semantics!.Frames.Select(frame => frame.Tick < 1840
                    ? frame with { RoundId = "s0-a8", SegmentId = 0 }
                    : frame with { RoundId = "s1-a8", SegmentId = 1 }).ToArray(),
                [])
        };
        var segmentedScene = builder.Build(adapter.BuildFromTimeline(
            segmentedTimeline, "segment-fixture", 1, 1940));
        Check(segmentedScene.Round.SegmentId == 1 && segmentedScene.Players.All(player =>
                player.Trajectory.Points.All(point => point.Tick >= 1840)),
            "semantic segment switch truncates prior-segment history", ref checks);

        var gapTimeline = timeline with
        {
            Frames = timeline.Frames.Where(frame => frame.Tick < 1800 || frame.Tick > 1880).ToArray()
        };
        var gapScene = builder.Build(adapter.BuildFromTimeline(gapTimeline, "gap-fixture", 1, 1940));
        Check(gapScene.DataQuality.Any(item => item.Code == SituationDataQualityCodes.HistoryGap),
            "sampling gap is explicit", ref checks);

        var futurePerturbed = timeline with
        {
            Events = [new(2400, 37.5, "round_end", "Private future winner", "future")],
            RoundResults = [new(8, 1500, 1500, 2400, "CT", "future-result")]
        };
        var perturbedScene = builder.Build(adapter.BuildFromTimeline(
            futurePerturbed, "boundary-fixture", 1, 1940));
        Check(SituationCanonicalJson.Serialize(perturbedScene) == SituationCanonicalJson.Serialize(fullScene),
            "future events and results do not affect the current scene", ref checks);

        var originalCulture = CultureInfo.CurrentCulture;
        var originalUiCulture = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("fr-FR");
            var french = SituationCanonicalJson.Serialize(builder.Build(adapter.BuildFromTimeline(
                timeline, "boundary-fixture", 1, 1940)));
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR");
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("tr-TR");
            var turkish = SituationCanonicalJson.Serialize(builder.Build(adapter.BuildFromTimeline(
                timeline, "boundary-fixture", 1, 1940)));
            Check(french == turkish && french == SituationCanonicalJson.Serialize(fullScene),
                "culture does not change canonical scene", ref checks);
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
            CultureInfo.CurrentUICulture = originalUiCulture;
        }

        var relationA = BuildAnonymousRelationTimeline(reversePlayers: false, reverseRelations: false);
        var relationB = BuildAnonymousRelationTimeline(reversePlayers: true, reverseRelations: true);
        var relationSceneA = builder.Build(adapter.BuildFromTimeline(relationA, "relation-fixture", 0, 650));
        var relationSceneB = builder.Build(adapter.BuildFromTimeline(relationB, "relation-fixture", 0, 650));
        Check(SituationCanonicalJson.Serialize(relationSceneA) == SituationCanonicalJson.Serialize(relationSceneB),
            "anonymous relation graph is stable under input reordering", ref checks);
        Check(relationSceneA.DataQuality.Any(item =>
                item.Code == SituationDataQualityCodes.HistoryRoundBoundary),
            "round boundary reports shortened history", ref checks);

        var modelInput = SituationModelInputProjector.Serialize(fullScene);
        var changedRuntimeScene = fullScene with
        {
            DemoRef = "C:\\private\\different.dem",
            WindowIndex = 999,
            RequestedTick = fullScene.RequestedTick + 1
        };
        Check(modelInput == SituationModelInputProjector.Serialize(changedRuntimeScene),
            "runtime metadata does not affect model input", ref checks);
        Check(!ContainsAny(modelInput,
                "demoRef", "windowIndex", "requestedTick", "\"source\":", ".dem", "manifest", "\"events\":"),
            "model input excludes local and runtime metadata", ref checks);

        SituationContractValidator.Validate(fullScene);
        checks++;
        return checks;
    }

    internal static DemoTimeline BuildBoundaryTimeline(bool reversePlayers, bool reverseRelations)
    {
        var t = new PlayerSnapshot(
            "t-id", "Private T", "T", true, 100,
            0, 0, 0, 90, 512, 512, 0, "T Spawn", "weapon_ak47", 0, 0);
        var ct = new PlayerSnapshot(
            "ct-id", "Private CT", "CT", true, 100,
            -100, -100, 0, 0, -64, 32, 0, "CT Spawn", "weapon_m4a1", 0, 0);
        IReadOnlyList<PlayerSnapshot> players = reversePlayers ? [ct, t] : [t, ct];
        var round = new RoundSnapshot(8, "live", 4, 3, 30, 85, 0, 0);
        var bomb = new BombSnapshot("carried", "t-id", null, null, "T Spawn", 0, 0, 0, null, null);
        var frames = Enumerable.Range(0, 44)
            .Select(index => 1600 + index * 8)
            .Select(tick => new DemoFrame(tick, tick / 64d, players, round, bomb, []))
            .ToArray();
        var points = frames.Select(frame => new UtilityPoint(
            frame.Tick, frame.TimeSeconds, frame.Tick - 1600, 0, 0)).ToArray();
        var track = new UtilityTrack(
            "private-track", "smoke", "t-id", "Private T", "T", 1700, 2100, 2000, points);
        var effect = new UtilityEffectTrack(
            "private-effect", "smoke", "t-id", "Private T", "T", 1800, 2100,
            frames.Where(frame => frame.Tick >= 1800)
                .Select(frame => new UtilityEffectSample(
                    frame.Tick, frame.TimeSeconds, 10, 20, 0, 144, []))
                .ToArray());
        var utilityStates = new[]
        {
            new PlayerUtilityState(1600, 25, "t-id", [new("flash", 1)]),
            new PlayerUtilityState(1600, 25, "ct-id", [])
        };
        var equipment = new[]
        {
            new PlayerEquipmentState(1600, 25, "t-id", 800, 100, true, false, 2700, 2700, 0,
                [new("weapon_ak47", "rifle", 1, 30, 90)]),
            new PlayerEquipmentState(1600, 25, "ct-id", 1000, 100, true, true, 3100, 3100, 0,
                [new("weapon_m4a1", "rifle", 1, 30, 90)])
        };
        var attempt = new RoundAttempt
        {
            RoundId = "s0-a8", SegmentId = 0, RoundNumber = 8, StartTick = 1500,
            LiveTick = 1500, EndTick = 2200
        };
        var semanticFrames = frames.Select(frame => new SemanticFrame(
            frame.Tick, "s0-a8", 8, 0, "live",
            new(30, 85, null, null, true, "fixture", "none"),
            new(5, 5, 10, 2, 2, 2, true, "usable", []))).ToArray();
        return new(
            new("private.dem", "de_mirage", 64, 8, 2560, 40),
            frames,
            reverseRelations ? [track] : [track],
            reverseRelations ? [effect] : [effect],
            utilityStates,
            equipment,
            [],
            [])
        {
            Semantics = new([attempt], semanticFrames, [])
        };
    }

    internal static DemoWindow BuildWindow(DemoTimeline timeline, int index, int fromTick, int toTick) => new(
        index,
        index * DemoImportService.WindowSeconds,
        Math.Min(timeline.Metadata.DurationSeconds, (index + 1) * DemoImportService.WindowSeconds),
        fromTick / (double)timeline.Metadata.TickRate,
        toTick / (double)timeline.Metadata.TickRate,
        0,
        timeline.Frames.Count,
        timeline.Frames.Where(frame => frame.Tick >= fromTick && frame.Tick <= toTick).ToArray(),
        timeline.UtilityTracks,
        timeline.UtilityEffects,
        timeline.PlayerUtilityStates,
        timeline.PlayerEquipmentStates,
        []);

    private static DemoTimeline ReplaceFirstT(
        DemoTimeline timeline,
        Func<PlayerSnapshot, PlayerSnapshot> replace) => timeline with
    {
        Frames = timeline.Frames.Select(frame => frame with
        {
            Players = frame.Players.Select(player => player.Team == "T" ? replace(player) : player).ToArray()
        }).ToArray()
    };

    private static DemoTimeline ReplaceBomb(
        DemoTimeline timeline,
        Func<DemoFrame, BombSnapshot> replace) => timeline with
    {
        Frames = timeline.Frames.Select(frame => frame with { Bomb = replace(frame) }).ToArray()
    };

    private static IReadOnlyList<int?> UtilityValues(SituationUtilityCounts counts) =>
        [counts.Flash, counts.Smoke, counts.He, counts.Molotov, counts.Incendiary, counts.Decoy];

    private static DemoTimeline BuildAnonymousRelationTimeline(bool reversePlayers, bool reverseRelations)
    {
        var first = new PlayerSnapshot(
            "private-a", "Private A", "T", true, 100, 100, 100, 0, 0, 0, 0, 0,
            "T Spawn", "weapon_ak47", 0, 0);
        var second = first with { Id = "private-b", Name = "Private B" };
        var ct = first with
        {
            Id = "private-ct", Name = "Private CT", Team = "CT", Region = "CT Spawn",
            Weapon = "weapon_m4a1", X = -100, Y = -100
        };
        IReadOnlyList<PlayerSnapshot> players = reversePlayers
            ? [ct, second, first]
            : [first, second, ct];
        var round = new RoundSnapshot(1, "live", 0, 0, 10, 105, 0, 0);
        var bomb = new BombSnapshot("unavailable", null, null, null, "unknown", null, null, null, null, null);
        var frames = new[]
        {
            new DemoFrame(640, 10, players, round, bomb, []),
            new DemoFrame(648, 10.125, players, round, bomb, [])
        };
        var trackA = new UtilityTrack(
            reverseRelations ? "private-track-z" : "private-track-a",
            "smoke", "private-a", "Private A", "T", 640, 700, null,
            [new(640, 10, 1, 2, 3), new(648, 10.125, 4, 5, 6)]);
        var trackB = new UtilityTrack(
            reverseRelations ? "private-track-a" : "private-track-b",
            "smoke", "private-b", "Private B", "T", 640, 700, null,
            [new(640, 10, 11, 12, 13), new(648, 10.125, 4, 5, 6)]);
        IReadOnlyList<UtilityTrack> tracks = reverseRelations ? [trackB, trackA] : [trackA, trackB];
        var effectA = new UtilityEffectTrack(
            reverseRelations ? "private-effect-z" : "private-effect-a",
            "smoke", "private-a", "Private A", "T", 640, 700,
            [new(648, 10.125, 20, 30, 0, 100, [])]);
        var effectB = new UtilityEffectTrack(
            reverseRelations ? "private-effect-a" : "private-effect-b",
            "smoke", "private-b", "Private B", "T", 640, 700,
            [new(648, 10.125, 20, 30, 0, 200, [])]);
        IReadOnlyList<UtilityEffectTrack> effects = reverseRelations
            ? [effectB, effectA]
            : [effectA, effectB];
        var states = new[]
        {
            new PlayerUtilityState(640, 10, "private-a", []),
            new PlayerUtilityState(640, 10, "private-b", []),
            new PlayerUtilityState(640, 10, "private-ct", [])
        };
        var equipment = new[]
        {
            Equipment("private-a", "weapon_ak47"),
            Equipment("private-b", "weapon_ak47"),
            Equipment("private-ct", "weapon_m4a1")
        };
        var attempt = new RoundAttempt
        {
            RoundId = "s0-a1", SegmentId = 0, RoundNumber = 1, StartTick = 640, LiveTick = 640
        };
        var semantics = new SemanticTimeline(
            [attempt],
            frames.Select(frame => new SemanticFrame(
                frame.Tick, "s0-a1", 1, 0, "live",
                new(10, 105, null, null, true, "fixture", "none"),
                new(2, 1, 3, 3, 3, 3, true, "usable", []))).ToArray(),
            []);
        return new(
            new("private.dem", "de_mirage", 64, 8, 700, 11),
            frames, tracks, effects, states, equipment, [], [])
        {
            Semantics = semantics
        };
    }

    private static PlayerEquipmentState Equipment(string playerId, string weapon) => new(
        640, 10, playerId, 800, 100, true, false, 2700, 2700, 0,
        [new(weapon, "rifle", 1, 30, 90)]);

    internal static async Task WriteWindowAsync(string directory, DemoWindow window)
    {
        var path = Path.Combine(directory, $"window-{window.Index:D4}.json.br");
        await using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        await using var brotli = new BrotliStream(file, CompressionLevel.Optimal, leaveOpen: false);
        await JsonSerializer.SerializeAsync(brotli, window);
    }

    private static bool ContainsAny(string value, params string[] needles) =>
        needles.Any(needle => value.Contains(needle, StringComparison.OrdinalIgnoreCase));

    private static void Check(bool condition, string label, ref int checks)
    {
        if (!condition)
            throw new InvalidOperationException($"Situation flow check failed: {label}.");
        checks++;
    }

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
        throw new InvalidOperationException($"Situation flow check failed: {label}.");
    }
}

using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using CsDemoMap.Api.Services;

namespace CsDemoMap.Cli;

internal static class DeveloperCommandDispatcher
{
    public static async Task<int> RunAsync(string[] args, CancellationToken cancellationToken)
    {
        if (args is ["--analyze-situation-raycast", var raycastScene, var raycastOutput])
        {
            await SituationRaycastCommands.AnalyzeAsync(raycastScene, raycastOutput, cancellationToken);
            return 0;
        }
        if (args is ["--sample-situation-raycast", var raycastDataset, var raycastSampleOutput])
        {
            await SituationRaycastCommands.SampleAsync(raycastDataset, raycastSampleOutput, cancellationToken);
            return 0;
        }
        if (args is ["--sample-situation-local-peek", var peekDataset, var peekOutput])
        {
            await SituationRaycastCommands.SampleAsync(peekDataset, peekOutput, cancellationToken, localPeek: true);
            return 0;
        }
        if (args is ["--sample-situation-position-prediction", var predictionDataset, var predictionOutput])
        {
            await SituationRaycastCommands.SampleAsync(predictionDataset, predictionOutput, cancellationToken, positionPrediction: true);
            return 0;
        }
        if (args is ["--sample-situation-continuous-slope", var slopeDataset, var slopeOutput])
        {
            await SituationRaycastCommands.SampleAsync(slopeDataset, slopeOutput, cancellationToken, continuousSlope: true);
            return 0;
        }
        if (args is ["--sample-situation-local-peek-3s", var extendedDataset, var extendedOutput])
        {
            await SituationRaycastCommands.SampleAsync(extendedDataset, extendedOutput, cancellationToken, extendedLocalPeek: true);
            return 0;
        }
        if (args is ["--sample-situation-verified-exposure", var exposureDataset, var exposureOutput])
        {
            await SituationRaycastCommands.SampleAsync(exposureDataset, exposureOutput, cancellationToken, verifiedExposure: true);
            return 0;
        }
        if (args is ["--sample-situation-close-exposure", var closeDataset, var closeOutput])
        {
            await SituationRaycastCommands.SampleAsync(closeDataset, closeOutput, cancellationToken, closeExposure: true);
            return 0;
        }
        if (args.Length >= 1 && args[0] == "--serve-situation-review")
        {
            if (args.Length < 5) throw new ArgumentException("Review requires dataset, work and candidates.");
            string? candidates = null;
            string? snapshot = null;
            string? raycastCandidates = null;
            string? peekCandidates = null;
            string? predictionCandidates = null;
            string? slopeCandidates = null;
            string? exposureCandidates = null;
            string? closeCandidates = null;
            int? reviewTarget = null;
            var port = 0;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 3; i < args.Length; i++)
            {
                var option = args[i];
                if (!seen.Add(option) || ++i >= args.Length) throw new ArgumentException("Invalid review option.");
                switch (option)
                {
                    case "--candidates": candidates = args[i]; break;
                    case "--producer-snapshot": snapshot = args[i]; break;
                    case "--raycast-candidates": raycastCandidates = args[i]; break;
                    case "--local-peek-candidates": peekCandidates = args[i]; break;
                    case "--position-prediction-candidates": predictionCandidates = args[i]; break;
                    case "--continuous-slope-candidates": slopeCandidates = args[i]; break;
                    case "--verified-exposure-candidates": exposureCandidates = args[i]; break;
                    case "--close-exposure-candidates": closeCandidates = args[i]; break;
                    case "--review-target": reviewTarget = int.Parse(args[i], CultureInfo.InvariantCulture); break;
                    case "--port": port = int.Parse(args[i], CultureInfo.InvariantCulture); break;
                    default: throw new ArgumentException("Invalid review option.");
                }
            }
            if (candidates is null || port is < 0 or > 65535) throw new ArgumentException("Invalid review options.");
            if (reviewTarget is not null and not 50) throw new ArgumentException("Supported reduced review target is 50.");
            if (new[] { raycastCandidates, peekCandidates, predictionCandidates, slopeCandidates, exposureCandidates, closeCandidates }.Count(x => x is not null) > 1)
                throw new ArgumentException("Choose one review analysis version.");
            var repository = SituationArtifactIO.FindRepositoryRoot(args[1]);
            snapshot ??= Path.Combine(repository, "datasets/situation-stage4-review-source-snapshot-20260919-r1");
            SituationReviewArtifactLoader.ValidateWorkLocation(args[2], args[1], candidates, snapshot);
            IReviewCatalog catalog = await SituationReviewArtifactLoader.OpenAsync(args[1], candidates, snapshot, cancellationToken);
            var derivedCandidates = closeCandidates ?? exposureCandidates ?? slopeCandidates ?? predictionCandidates ?? peekCandidates ?? raycastCandidates;
            if (derivedCandidates is not null)
            {
                SituationReviewArtifactLoader.ValidateWorkLocation(derivedCandidates, args[1], candidates, snapshot, args[2]);
                catalog = await SituationRaycastReviewCatalog.OpenAsync(catalog, derivedCandidates, repository, cancellationToken, peekCandidates is not null, predictionCandidates is not null, slopeCandidates is not null, exposureCandidates is not null, closeCandidates is not null);
            }
            await using var store = await SituationReviewWorkStore.OpenAsync(args[2], catalog.Identity,
                catalog.InitialSelected.Select(s => new ReviewActiveSample(s.ReviewOrdinal, s.Entry.SampleId)).ToArray(),
                catalog.GetCandidateAsync, cancellationToken);
            await SituationReviewConsumerProvenance.CaptureAsync(repository, args[2], store,
                [typeof(DeveloperCommandDispatcher).Assembly.Location, typeof(SituationReviewArtifactLoader).Assembly.Location], cancellationToken);
            SituationReviewReplacementService.VerifyRecoveredState(catalog, store.Snapshot);
            IReviewBackend backend = new SituationReviewDecisionService(catalog, store);
            backend = await SituationReviewScope.OpenAsync(backend, store, args[2], reviewTarget, cancellationToken);
            await using var server = await SituationReviewServer.StartAsync(backend,
                Path.Combine(AppContext.BaseDirectory, "SituationReviewUi"),
                Path.Combine(repository, "apps/web/public/radars/simpleradar/de_mirage.webp"), port, cancellationToken);
            Console.WriteLine($"Situation review ready: {server.Url}");
            Console.WriteLine("Template prelabels only. Human review is required; this workspace is not frozen.");
            await server.WaitForShutdownAsync(cancellationToken);
            return 0;
        }

        if (args is ["--verify-situation-review-consumer", var reviewBase, var reviewCandidates, var historicalSnapshot])
        {
            var catalog = await SituationReviewArtifactLoader.OpenAsync(reviewBase, reviewCandidates, historicalSnapshot, cancellationToken);
            Console.WriteLine($"Review consumer verified: {catalog.InitialSelected.Count} rows; historical sources preserved.");
            return 0;
        }

        if (args is ["--export-situation-review-candidates", var reviewDataset, var reviewOutput])
        {
            await SituationReviewCandidateExporter.ExportAsync(reviewDataset, reviewOutput, cancellationToken);
            Console.WriteLine("Review candidate export complete: train=220 dev=40 test=40.");
            return 0;
        }

        if (args is ["--verify-situation-review-candidates", var candidateDataset, var candidateDirectory])
        {
            var result = await SituationReviewCandidateValidator.VerifyAsync(candidateDataset, candidateDirectory, cancellationToken);
            Console.WriteLine($"Review candidates verified: {result.Manifest.Selected.Count} rows.");
            return 0;
        }

        if (args is ["--inspect-demo", var demoPath])
        {
            await InspectDemoAsync(demoPath, cancellationToken);
            return 0;
        }

        if (args is ["--export-win-data", var inputPath, var outputPath])
        {
            await ExportWinDataAsync(inputPath, outputPath, cancellationToken);
            return 0;
        }

        if (args is ["--export-win-data-v4", var v4Input, var v4Output])
        {
            await WinDatasetV4Exporter.ExportAsync(v4Input, v4Output, cancellationToken);
            return 0;
        }

        if (args is ["--export-situation-samples", var sampleRequest, var sampleOutput])
        {
            await SituationSampleExporter.ExportAsync(sampleRequest, sampleOutput, cancellationToken);
            return 0;
        }

        if (args is ["--rebuild-situation-sidecars", var importDirectory, var sourceDemo, var rebuildOutput])
        {
            await SituationSidecarRebuilder.RebuildAsync(
                importDirectory, sourceDemo, rebuildOutput, cancellationToken);
            return 0;
        }

        if (args is ["--run-situation-stage-two-acceptance", var stageTwoRequest, var stageTwoOutput])
        {
            await SituationStageTwoAcceptance.RunAsync(stageTwoRequest, stageTwoOutput, cancellationToken);
            return 0;
        }

        if (args is ["--run-situation-stage-three-calibration", var calibrationRequest, var calibrationOutput])
        {
            await SituationStageThreeCalibration.RunAsync(
                calibrationRequest, calibrationOutput, cancellationToken);
            return 0;
        }

        if (args is ["--run-situation-stage-three-acceptance", var acceptanceRequest, var acceptanceOutput])
        {
            await SituationStageThreeAcceptance.RunAsync(
                acceptanceRequest, acceptanceOutput, cancellationToken);
            return 0;
        }

        if (args is ["--create-situation-stage-four-split", var demoDirectory, var parentSplit, var stageFourSplit])
        {
            var result = await SituationDatasetSplit.CreateAsync(
                demoDirectory, parentSplit, stageFourSplit, cancellationToken);
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                result.Split.SchemaVersion,
                result.SplitSha256,
                result.Split.TrainDemoCount,
                result.Split.DevDemoCount,
                result.Split.TestDemoCount
            }, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
            return 0;
        }

        if (args is ["--export-situation-training-data", var trainingDemoDirectory, var trainingSplit,
                    var trainingOutput, "--pilot", var pilotLimit])
        {
            var manifest = await SituationTrainingPilotExporter.ExportAsync(
                trainingDemoDirectory,
                trainingSplit,
                trainingOutput,
                int.Parse(pilotLimit, CultureInfo.InvariantCulture),
                cancellationToken);
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                manifest.SchemaVersion,
                manifest.Status,
                manifest.Mode,
                manifest.Purpose,
                manifest.Trainable,
                manifest.SampleLimit,
                Counts = manifest.Counts["train"],
                manifest.SplitSha256,
                manifest.SelectionConfigSha256,
                manifest.InputRepresentationConfigSha256
            }, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
            return 0;
        }

        if (args.Length >= 4 && args[0] == "--export-situation-training-data")
        {
            var resume = false;
            var roundWorkers = 4;
            int? benchmark = null;
            for (var index = 4; index < args.Length; index++)
            {
                if (args[index] == "--resume") resume = true;
                else if (args[index] == "--round-workers" && ++index < args.Length)
                    roundWorkers = int.Parse(args[index], CultureInfo.InvariantCulture);
                else if (args[index] == "--train-benchmark") benchmark = 5;
                else throw new ArgumentException("Unknown full training export option.");
            }
            await SituationTrainingDatasetExporter.ExportAsync(args[1], args[2], args[3], cancellationToken,
                new(roundWorkers, resume, benchmark));
            return 0;
        }

        if (args is ["--trace-roster", var path, var from, var to])
        {
            await DemoRosterTrace.RunAsync(
                path,
                int.Parse(from, CultureInfo.InvariantCulture),
                int.Parse(to, CultureInfo.InvariantCulture));
            return 0;
        }

        Console.Error.WriteLine("Unknown command. See docs/context/RUNBOOK.md.");
        return 2;
    }

    private static async Task InspectDemoAsync(string demoPath, CancellationToken cancellationToken)
    {
        if (!File.Exists(demoPath))
            throw new FileNotFoundException("找不到 demo 文件。", demoPath);

        var stopwatch = Stopwatch.StartNew();
        await using var stream = File.OpenRead(demoPath);
        var timeline = await new DemoParserService().ParseAsync(
            stream,
            Path.GetFileName(demoPath),
            cancellationToken);
        stopwatch.Stop();

        var snapshots = timeline.Frames.SelectMany(frame => frame.Players).ToArray();
        var positioned = snapshots.Where(player =>
            float.IsFinite(player.X) && float.IsFinite(player.Y) && float.IsFinite(player.Z)).ToArray();
        var trajectoryPoints = timeline.UtilityTracks.SelectMany(track => track.Trajectory).ToArray();
        var effectSamples = timeline.UtilityEffects.SelectMany(effect => effect.Samples).ToArray();
        var fireAreaPoints = timeline.UtilityEffects
            .Where(effect => effect.Type == "fire")
            .SelectMany(effect => effect.Samples)
            .SelectMany(sample => sample.Area)
            .ToArray();
        var equipmentStates = timeline.PlayerEquipmentStates.ToArray();
        var roundStates = timeline.Frames.Select(frame => frame.Round).ToArray();
        var bombStates = timeline.Frames.Select(frame => frame.Bomb).ToArray();

        var summary = new
        {
            timeline.Metadata,
            ParseElapsedSeconds = stopwatch.Elapsed.TotalSeconds,
            FrameCount = timeline.Frames.Count,
            SnapshotCount = snapshots.Length,
            PositionedSnapshotCount = positioned.Length,
            MovingSnapshotCount = snapshots.Count(player =>
                player.VelocityX != 0 || player.VelocityY != 0 || player.VelocityZ != 0),
            MaxPlayerSpeed = snapshots.Length == 0 ? 0 : snapshots.Max(player => Math.Sqrt(
                player.VelocityX * player.VelocityX +
                player.VelocityY * player.VelocityY +
                player.VelocityZ * player.VelocityZ)),
            UtilityTracks = timeline.UtilityTracks
                .GroupBy(track => track.Type)
                .ToDictionary(
                    group => group.Key,
                    group => new
                    {
                        Tracks = group.Count(),
                        Points = group.Sum(track => track.Trajectory.Count),
                        LinkedThrowers = group.Count(track => track.ThrowerId is not null)
                    }),
            UtilityEffects = timeline.UtilityEffects
                .GroupBy(effect => effect.Type)
                .ToDictionary(
                    group => group.Key,
                    group => new
                    {
                        Effects = group.Count(),
                        Samples = group.Sum(effect => effect.Samples.Count),
                        AreaPoints = group.SelectMany(effect => effect.Samples).Sum(sample => sample.Area.Count)
                    }),
            PlayerUtilityStateCount = timeline.PlayerUtilityStates.Count,
            PlayerEquipmentStateCount = equipmentStates.Length,
            EquipmentItems = equipmentStates.Sum(state => state.Items.Count),
            Economy = equipmentStates.Length == 0 ? null : new
            {
                MinMoney = equipmentStates.Min(state => state.Money),
                MaxMoney = equipmentStates.Max(state => state.Money),
                MaxEquipmentValue = equipmentStates.Max(state => state.CurrentEquipmentValue),
                Players = equipmentStates.Select(state => state.PlayerId).Distinct().Count()
            },
            Rounds = roundStates.Select(state => state.Number).Distinct().Order().ToArray(),
            RoundPhases = roundStates.GroupBy(state => state.Phase)
                .ToDictionary(group => group.Key, group => group.Count()),
            BombStates = bombStates.GroupBy(state => state.State)
                .ToDictionary(group => group.Key, group => group.Count()),
            BombSites = bombStates.Where(state => state.Site is not null)
                .GroupBy(state => state.Site!)
                .ToDictionary(group => group.Key, group => group.Count()),
            BombCarriers = bombStates.Where(state => state.CarrierId is not null)
                .Select(state => state.CarrierId).Distinct().Count(),
            MapRegions = timeline.Frames.SelectMany(frame => frame.Zones)
                .Select(zone => zone.Region).Distinct().Order().ToArray(),
            PlayersWithUtility = timeline.PlayerUtilityStates
                .Where(state => state.Items.Count > 0)
                .Select(state => state.PlayerId)
                .Distinct()
                .Count(),
            TrajectoryBounds = Bounds(trajectoryPoints.Select(point => (point.X, point.Y, point.Z))),
            EffectBounds = Bounds(effectSamples.Select(point => (point.X, point.Y, point.Z))),
            FireAreaBounds = Bounds(fireAreaPoints.Select(point => (point.X, point.Y, point.Z))),
            Players = snapshots
                .GroupBy(player => new { player.Id, player.Name, player.Team })
                .Select(group => new
                {
                    group.Key.Id,
                    group.Key.Name,
                    group.Key.Team,
                    Snapshots = group.Count()
                })
                .OrderBy(player => player.Team)
                .ThenBy(player => player.Name),
            CoordinateBounds = positioned.Length == 0 ? null : new
            {
                MinX = positioned.Min(player => player.X),
                MaxX = positioned.Max(player => player.X),
                MinY = positioned.Min(player => player.Y),
                MaxY = positioned.Max(player => player.Y),
                MinZ = positioned.Min(player => player.Z),
                MaxZ = positioned.Max(player => player.Z)
            },
            FirstFrame = timeline.Frames.FirstOrDefault(),
            LastFrame = timeline.Frames.LastOrDefault(),
            EventCounts = timeline.Events
                .GroupBy(item => item.Type)
                .ToDictionary(group => group.Key, group => group.Count())
        };

        Console.WriteLine(JsonSerializer.Serialize(summary, new JsonSerializerOptions
        {
            WriteIndented = true
        }));
    }

    private static async Task ExportWinDataAsync(
        string inputPath,
        string outputPath,
        CancellationToken cancellationToken)
    {
        var exporter = new WinDatasetExporter(new DemoParserService());
        var summary = await exporter.ExportAsync(inputPath, outputPath, cancellationToken);
        Console.WriteLine(JsonSerializer.Serialize(summary, new JsonSerializerOptions
        {
            WriteIndented = true
        }));
    }

    private static object? Bounds(IEnumerable<(float X, float Y, float Z)> values)
    {
        var points = values.Where(point =>
            float.IsFinite(point.X) && float.IsFinite(point.Y) && float.IsFinite(point.Z)).ToArray();
        return points.Length == 0 ? null : new
        {
            MinX = points.Min(point => point.X),
            MaxX = points.Max(point => point.X),
            MinY = points.Min(point => point.Y),
            MaxY = points.Max(point => point.Y),
            MinZ = points.Min(point => point.Z),
            MaxZ = points.Max(point => point.Z)
        };
    }
}

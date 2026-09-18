using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using CsDemoMap.Api.Services;

namespace CsDemoMap.Cli;

internal static class DeveloperCommandDispatcher
{
    public static async Task<int> RunAsync(string[] args, CancellationToken cancellationToken)
    {
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

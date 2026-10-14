using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using CsDemoMap.Api.Models;

namespace CsDemoMap.Api.Services;

// An opt-in companion dataset. The default v4 exporter and inference contract stay separate.
public static class WinSpatialDatasetExporter
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static async Task ExportAsync(string input, string output, WinSpatialRadii radii, CancellationToken token)
    {
        radii.Validate();
        var directory = Path.GetFullPath(output);
        if (Directory.Exists(directory) || File.Exists(directory))
            throw new IOException("Spatial output must be a new directory: " + directory);
        var paths = WinDatasetExporter.ResolveDemoPaths(input);
        var baseDirectory = Path.Combine(directory, "base");
        Directory.CreateDirectory(baseDirectory);
        var reports = new List<object>();
        var timings = new List<object>();
        var rows = 0;
        await using (var baseWriter = NewWriter(Path.Combine(baseDirectory, "samples.jsonl")))
        await using (var writer = NewWriter(Path.Combine(directory, "spatial.jsonl")))
        await using (var evidence = NewWriter(Path.Combine(directory, "source.jsonl")))
        {
            foreach (var path in paths)
            {
                token.ThrowIfCancellationRequested();
                await using var source = File.OpenRead(path);
                var matchId = Convert.ToHexStringLower(await SHA256.HashDataAsync(source, token));
                source.Position = 0;
                var watch = Stopwatch.StartNew();
                var timeline = await new DemoParserService().ParseAsync(source, Path.GetFileName(path), token, true);
                var parseSeconds = watch.Elapsed.TotalSeconds;
                if (timeline.Metadata.MapName != "de_mirage")
                    throw new InvalidDataException("Spatial v1 requires de_mirage: " + Path.GetFileName(path));
                watch.Restart();
                var report = await WinDatasetV4Exporter.WriteTimelineAsync(timeline, matchId, baseWriter, token);
                var baseSeconds = watch.Elapsed.TotalSeconds;
                watch.Restart();
                var count = await WriteTimelineAsync(timeline, matchId, radii, writer, evidence, token);
                timings.Add(new { matchId, parseSeconds, baseSeconds, spatialSeconds = watch.Elapsed.TotalSeconds });
                rows += count;
                var semantic = timeline.Semantics!;
                await Save(Path.Combine(baseDirectory, matchId + ".audit.json"),
                    new { timeline.Metadata, semantic.Attempts, semantic.Audit, report }, token);
                reports.Add(new { file = Path.GetFileName(path), matchId, report });
                Console.WriteLine(JsonSerializer.Serialize(new { file = Path.GetFileName(path), rows = count }, JsonOptions));
            }
        }
        await Save(Path.Combine(baseDirectory, "manifest.json"), new
        {
            schemaVersion = 4, semanticVersion = WinFeatureSampleBuilder.SemanticVersion,
            parserVersion = "DemoFile.Game.Cs/0.44.1", sampleStrideTicks = 64,
            status = "complete", createdAtUtc = DateTimeOffset.UtcNow, matches = reports
        }, token);
        var hashes = new Dictionary<string, string>();
        foreach (var path in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).Order())
        {
            await using var stream = File.OpenRead(path);
            hashes.Add(Path.GetRelativePath(directory, path).Replace('\\', '/'),
                Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, token)));
        }
        await Save(Path.Combine(directory, "manifest.json"), new
        {
            contractVersion = WinSpatialFeatureBuilder.ContractVersion, status = "complete",
            baseSchemaVersion = 4, baseSemanticVersion = WinFeatureSampleBuilder.SemanticVersion,
            distanceMetric = "euclidean-xy-world-units", radii,
            siteA = MapFeatureGeometries.Find("de_mirage")!.SiteA,
            siteB = MapFeatureGeometries.Find("de_mirage")!.SiteB,
            rows, matches = reports.Count, timings, files = hashes,
            createdAtUtc = DateTimeOffset.UtcNow
        }, token);
    }

    public static async Task<int> WriteTimelineAsync(DemoTimeline timeline, string matchId, WinSpatialRadii radii,
        TextWriter writer, TextWriter evidence, CancellationToken token)
    {
        var builder = new WinSpatialFeatureBuilder(timeline.Metadata.MapName, radii);
        var selected = new WinFeatureSampleBuilder().Build(timeline, token).Samples;
        var frames = timeline.Frames.ToDictionary(f => f.Tick);
        var semantics = timeline.Semantics!.Frames.ToDictionary(f => f.Tick);
        foreach (var sample in selected)
        {
            token.ThrowIfCancellationRequested();
            var frame = frames[sample.Tick]; // Raw positions, before the legacy v4 finite-coordinate bridge.
            var semantic = semantics[sample.Tick];
            await writer.WriteLineAsync(JsonSerializer.Serialize(new
            {
                contractVersion = WinSpatialFeatureBuilder.ContractVersion, matchId,
                sample.RoundId, sample.SegmentId, sample.RoundNumber, sample.Tick, sample.MapName,
                features = builder.Build(frame, semantic)
            }, JsonOptions).AsMemory(), token);
            // Audit evidence only. These IDs/coordinates are not model features.
            await evidence.WriteLineAsync(JsonSerializer.Serialize(new
            {
                matchId, sample.RoundId, sample.Tick, quality = semantic.Roster,
                players = frame.Players.Where(p => p.Alive && p.Team is "T" or "CT").Select(p => new
                { p.Id, p.Team, x = Finite(p.X), y = Finite(p.Y), z = Finite(p.Z) }),
                bomb = frame.Bomb with { X = Finite(frame.Bomb.X), Y = Finite(frame.Bomb.Y), Z = Finite(frame.Bomb.Z) }
            }, JsonOptions).AsMemory(), token);
        }
        return selected.Count;
    }

    private static float? Finite(float? value) => value.HasValue && float.IsFinite(value.Value) ? value : null;
    private static StreamWriter NewWriter(string path) => new(new FileStream(path, FileMode.CreateNew));
    private static Task Save(string path, object value, CancellationToken token) =>
        File.WriteAllTextAsync(path, JsonSerializer.Serialize(value, JsonOptions), token);
}

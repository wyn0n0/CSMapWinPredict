using System.Security.Cryptography;
using System.Text.Json;
using CsDemoMap.Api.Models;

namespace CsDemoMap.Api.Services;

public static class WinDatasetV4Exporter
{
    public const string SemanticVersion = WinFeatureSampleBuilder.SemanticVersion;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static async Task ExportAsync(string input, string output, CancellationToken token)
    {
        var directory = Path.GetFullPath(output);
        if (Directory.Exists(directory) || File.Exists(directory))
            throw new IOException("v4 output must be a new directory: " + directory);
        var paths = WinDatasetExporter.ResolveDemoPaths(input);
        Directory.CreateDirectory(directory);
        var reports = new List<object>();
        // CreateNew and a completion manifest make interrupted exports identifiable.
        await using var stream = new FileStream(Path.Combine(directory, "samples.jsonl"), FileMode.CreateNew);
        await using var writer = new StreamWriter(stream);
        foreach (var path in paths)
        {
            token.ThrowIfCancellationRequested();
            await using var source = File.OpenRead(path);
            var hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(source, token));
            source.Position = 0;
            var timeline = await new DemoParserService().ParseAsync(source, Path.GetFileName(path), token, true);
            if (timeline.Metadata.MapName != "de_mirage")
            {
                reports.Add(new
                {
                    file = Path.GetFileName(path),
                    matchId = hash,
                    skipped = "unsupported-map",
                    timeline.Metadata.MapName
                });
                Console.WriteLine($"Skipped unsupported map: {Path.GetFileName(path)}");
                continue;
            }
            var report = await WriteTimelineAsync(timeline, hash, writer, token);
            var semantic = timeline.Semantics!;
            await File.WriteAllTextAsync(Path.Combine(directory, hash + ".audit.json"),
                JsonSerializer.Serialize(new { timeline.Metadata, semantic.Attempts, semantic.Audit, report }, JsonOptions), token);
            reports.Add(new { file = Path.GetFileName(path), matchId = hash, report });
            await writer.FlushAsync(token);
            Console.WriteLine(JsonSerializer.Serialize(new { file = Path.GetFileName(path), report }, JsonOptions));
        }
        await writer.FlushAsync(token);
        await File.WriteAllTextAsync(Path.Combine(directory, "manifest.json"),
            JsonSerializer.Serialize(new
            {
                schemaVersion = 4,
                semanticVersion = SemanticVersion,
                parserVersion = "DemoFile.Game.Cs/0.44.1",
                sampleStrideTicks = 64,
                status = "complete",
                createdAtUtc = DateTimeOffset.UtcNow,
                matches = reports
            }, JsonOptions), token);
    }

    public static async Task<object> WriteTimelineAsync(
        DemoTimeline timeline,
        string matchId,
        TextWriter writer,
        CancellationToken token)
    {
        var result = new WinFeatureSampleBuilder().Build(timeline, token);
        foreach (var sample in result.Samples)
        {
            token.ThrowIfCancellationRequested();
            var target = result.Targets[sample.RoundId];
            var row = new
            {
                schemaVersion = 4,
                semanticVersion = SemanticVersion,
                matchId,
                roundId = sample.RoundId,
                segmentId = sample.SegmentId,
                roundNumber = sample.RoundNumber,
                mapName = sample.MapName,
                tick = sample.Tick,
                demoTimeSeconds = sample.DemoTimeSeconds,
                features = sample.Features,
                labelTWin = target.LabelTWin,
                sampleWeight = 1d / target.SampleCount
            };
            await writer.WriteLineAsync(JsonSerializer.Serialize(row, JsonOptions).AsMemory(), token);
        }
        return result.Report;
    }
}

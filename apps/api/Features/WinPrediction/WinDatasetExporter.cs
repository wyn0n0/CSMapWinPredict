using System.Security.Cryptography;
using System.Text.Json;
using CsDemoMap.Api.Models;

namespace CsDemoMap.Api.Services;

public sealed class WinDatasetExporter(DemoParserService parser)
{
    public const int DatasetSchemaVersion = 3;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<WinDatasetExportSummary> ExportAsync(
        string inputPath,
        string outputPath,
        CancellationToken cancellationToken)
    {
        var demoPaths = ResolveDemoPaths(inputPath);
        var fullOutputPath = Path.GetFullPath(outputPath);
        if (File.Exists(fullOutputPath))
            throw new IOException($"输出文件已存在：{fullOutputPath}");

        var outputDirectory = Path.GetDirectoryName(fullOutputPath);
        if (!string.IsNullOrEmpty(outputDirectory))
            Directory.CreateDirectory(outputDirectory);

        var temporaryPath = $"{fullOutputPath}.{Guid.NewGuid():N}.tmp";
        var demoCount = 0;
        var roundCount = 0;
        var rowCount = 0;
        try
        {
            await using (var output = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                128 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (var writer = new StreamWriter(output))
            {
                foreach (var demoPath in demoPaths)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var matchId = await ComputeMatchIdAsync(demoPath, cancellationToken);
                    await using var source = new FileStream(
                        demoPath,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.Read,
                        1024 * 1024,
                        FileOptions.Asynchronous | FileOptions.SequentialScan);
                    var timeline = await parser.ParseAsync(
                        source,
                        Path.GetFileName(demoPath),
                        cancellationToken);
                    var written = await WriteTimelineAsync(
                        timeline,
                        matchId,
                        writer,
                        cancellationToken);
                    demoCount++;
                    roundCount += timeline.RoundResults.Count;
                    rowCount += written;
                }
            }

            File.Move(temporaryPath, fullOutputPath);
        }
        catch
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
            throw;
        }

        return new WinDatasetExportSummary(fullOutputPath, demoCount, roundCount, rowCount);
    }

    public static IReadOnlyList<string> ResolveDemoPaths(string inputPath)
    {
        if (File.Exists(inputPath))
        {
            if (!string.Equals(Path.GetExtension(inputPath), ".dem", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("输入文件必须是 .dem 文件。", nameof(inputPath));
            return [Path.GetFullPath(inputPath)];
        }

        if (!Directory.Exists(inputPath))
            throw new FileNotFoundException("找不到 demo 文件或目录。", inputPath);

        var paths = Directory.EnumerateFiles(inputPath, "*.dem", SearchOption.AllDirectories)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .Select(Path.GetFullPath)
            .ToArray();
        if (paths.Length == 0)
            throw new InvalidOperationException("输入目录中没有 .dem 文件。");
        return paths;
    }

    public static async Task<int> WriteTimelineAsync(
        DemoTimeline timeline,
        string matchId,
        TextWriter writer,
        CancellationToken cancellationToken)
    {
        var featureBuilder = new AsOfTickFeatureBuilder(timeline);
        var rowCount = 0;
        foreach (var result in timeline.RoundResults.OrderBy(item => item.EndTick))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var frames = SelectSampleFrames(timeline, result);
            if (frames.Count == 0)
                continue;

            var sampleWeight = 1d / frames.Count;
            foreach (var frame in frames)
            {
                var row = new WinTrainingRow(
                    DatasetSchemaVersion,
                    matchId,
                    timeline.Metadata.MapName,
                    result.RoundNumber,
                    frame.Tick,
                    frame.TimeSeconds,
                    featureBuilder.Build(frame),
                    result.WinnerSide == "T" ? 1 : 0,
                    sampleWeight);
                var json = JsonSerializer.Serialize(row, JsonOptions);
                await writer.WriteLineAsync(json.AsMemory(), cancellationToken);
                rowCount++;
            }
        }
        return rowCount;
    }

    internal static IReadOnlyList<DemoFrame> SelectSampleFrames(
        DemoTimeline timeline,
        RoundResult result)
    {
        var sampleStride = Math.Max(1, timeline.Metadata.TickRate);
        var lastSampledTick = int.MinValue;
        var frames = new List<DemoFrame>();
        foreach (var frame in timeline.Frames)
        {
            if (frame.Tick < result.LiveTick || frame.Tick >= result.EndTick)
                continue;
            if (frame.Round.Phase is not ("live" or "post-plant"))
                continue;
            if (lastSampledTick != int.MinValue && frame.Tick - lastSampledTick < sampleStride)
                continue;

            frames.Add(frame);
            lastSampledTick = frame.Tick;
        }
        return frames;
    }

    private static async Task<string> ComputeMatchIdAsync(
        string demoPath,
        CancellationToken cancellationToken)
    {
        await using var source = new FileStream(
            demoPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            1024 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = await SHA256.HashDataAsync(source, cancellationToken);
        return Convert.ToHexStringLower(hash);
    }
}

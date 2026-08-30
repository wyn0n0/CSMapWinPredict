using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;
using System.Threading.Channels;
using CsDemoMap.Api.Models;

namespace CsDemoMap.Api.Services;

public sealed class DemoImportService : BackgroundService
{
    public const int SchemaVersion = 2;
    public const int WindowSeconds = 30;
    private const int WindowOverlapSeconds = 2;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly Channel<string> queue = Channel.CreateUnbounded<string>(new UnboundedChannelOptions
    {
        SingleReader = true,
        SingleWriter = false
    });
    private readonly ConcurrentDictionary<string, ImportJob> jobs = new(StringComparer.Ordinal);
    private readonly DemoParserService parser;
    private readonly ILogger<DemoImportService> logger;
    private readonly string storageRoot;

    public DemoImportService(
        DemoParserService parser,
        IWebHostEnvironment environment,
        ILogger<DemoImportService> logger)
    {
        this.parser = parser;
        this.logger = logger;
        storageRoot = Path.Combine(environment.ContentRootPath, "data", "imports");
        Directory.CreateDirectory(storageRoot);
    }

    public async Task<DemoImportAccepted> CreateAsync(
        Stream stream,
        string fileName,
        long fileSizeBytes,
        CancellationToken cancellationToken)
    {
        var id = Guid.NewGuid().ToString("N");
        var directory = Path.Combine(storageRoot, id);
        Directory.CreateDirectory(directory);
        var sourcePath = Path.Combine(directory, "source.dem");

        try
        {
            await using var target = new FileStream(
                sourcePath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                1024 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            await stream.CopyToAsync(target, 1024 * 1024, cancellationToken);
        }
        catch
        {
            Directory.Delete(directory, recursive: true);
            throw;
        }

        var job = new ImportJob(id, Path.GetFileName(fileName), fileSizeBytes, directory, sourcePath, deleteSource: true);
        jobs[id] = job;
        await queue.Writer.WriteAsync(id, cancellationToken);
        return new DemoImportAccepted(id, job.Status);
    }

    public async Task<DemoImportAccepted> CreateFromFileAsync(
        string sourcePath,
        string fileName,
        long fileSizeBytes,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(sourcePath))
            throw new FileNotFoundException("找不到离线 demo 文件。", sourcePath);

        var id = Guid.NewGuid().ToString("N");
        var directory = Path.Combine(storageRoot, id);
        Directory.CreateDirectory(directory);
        var job = new ImportJob(
            id,
            Path.GetFileName(fileName),
            fileSizeBytes,
            directory,
            Path.GetFullPath(sourcePath),
            deleteSource: false);
        jobs[id] = job;
        try
        {
            await queue.Writer.WriteAsync(id, cancellationToken);
        }
        catch
        {
            jobs.TryRemove(id, out _);
            Directory.Delete(directory);
            throw;
        }

        return new DemoImportAccepted(id, job.Status);
    }

    public DemoImportStatus? GetStatus(string id) => jobs.TryGetValue(id, out var job)
        ? job.ToStatus()
        : null;

    public string? GetWindowPath(string id, int index)
    {
        if (!jobs.TryGetValue(id, out var job) || job.Status != "completed" ||
            job.Manifest is null || index < 0 || index >= job.Manifest.WindowCount)
            return null;

        var path = WindowPath(job.Directory, index);
        return File.Exists(path) ? path : null;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var id in queue.Reader.ReadAllAsync(stoppingToken))
        {
            if (!jobs.TryGetValue(id, out var job))
                continue;

            await ProcessAsync(job, stoppingToken);
        }
    }

    private async Task ProcessAsync(ImportJob job, CancellationToken cancellationToken)
    {
        try
        {
            var stopwatch = Stopwatch.StartNew();
            job.Status = "parsing";
            logger.LogInformation("开始解析 demo {DemoId} ({FileName}, {Size} bytes)", job.Id, job.FileName, job.FileSizeBytes);

            await using var source = new FileStream(
                job.SourcePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                1024 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var timeline = await parser.ParseAsync(source, job.FileName, cancellationToken);
            var parseSeconds = stopwatch.Elapsed.TotalSeconds;
            job.Status = "chunking";
            job.Manifest = await WriteWindowsAsync(job, timeline, cancellationToken);
            stopwatch.Stop();
            job.Status = "completed";

            logger.LogInformation(
                "Demo {DemoId} 已完成：{Frames} 帧，{Windows} 个窗口，解析 {ParseSeconds:F2} 秒，总计 {TotalSeconds:F2} 秒",
                job.Id,
                timeline.Frames.Count,
                job.Manifest.WindowCount,
                parseSeconds,
                stopwatch.Elapsed.TotalSeconds);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            job.Error = "服务已停止，解析被取消。";
            job.Status = "failed";
        }
        catch (Exception exception)
        {
            job.Error = exception.Message;
            job.Status = "failed";
            logger.LogError(exception, "Demo {DemoId} 解析失败", job.Id);
        }
        finally
        {
            if (job.DeleteSource)
            {
                try
                {
                    File.Delete(job.SourcePath);
                }
                catch (Exception exception)
                {
                    logger.LogWarning(exception, "无法删除 demo {DemoId} 的上传副本", job.Id);
                }
            }
        }
    }

    private static async Task<DemoManifest> WriteWindowsAsync(
        ImportJob job,
        DemoTimeline timeline,
        CancellationToken cancellationToken)
    {
        var duration = Math.Max(0, timeline.Metadata.DurationSeconds);
        var windowCount = Math.Max(1, (int)Math.Ceiling(duration / WindowSeconds));
        for (var index = 0; index < windowCount; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var coreFrom = index * WindowSeconds;
            var coreTo = Math.Min(duration, (index + 1) * WindowSeconds);
            var dataFrom = Math.Max(0d, coreFrom - WindowOverlapSeconds);
            var dataTo = Math.Min(duration, coreTo + WindowOverlapSeconds);
            var startTick = (int)Math.Floor(dataFrom * timeline.Metadata.TickRate);
            var endTick = (int)Math.Ceiling(dataTo * timeline.Metadata.TickRate);

            var indexedFrames = timeline.Frames
                .Select((frame, frameIndex) => (frame, frameIndex))
                .Where(item => item.frame.TimeSeconds >= dataFrom && item.frame.TimeSeconds <= dataTo)
                .ToArray();
            var frames = indexedFrames.Select(item => item.frame).ToArray();
            var firstFrameIndex = indexedFrames.Length == 0 ? 0 : indexedFrames[0].frameIndex;

            var utilityTracks = timeline.UtilityTracks
                .Where(track => track.EndTick >= startTick && track.StartTick <= endTick)
                .Select(track => track with { Trajectory = SliceUtilityPoints(track.Trajectory, startTick, endTick) })
                .Where(track => track.Trajectory.Count > 0)
                .ToArray();
            var utilityEffects = timeline.UtilityEffects
                .Where(effect => effect.EndTick >= startTick && effect.StartTick <= endTick)
                .Select(effect => effect with { Samples = SliceEffectSamples(effect.Samples, startTick, endTick) })
                .Where(effect => effect.Samples.Count > 0)
                .ToArray();
            var utilityStates = timeline.PlayerUtilityStates
                .GroupBy(state => state.PlayerId)
                .SelectMany(group => SliceStateChanges(group, startTick, endTick))
                .OrderBy(state => state.Tick)
                .ToArray();
            var equipmentStates = timeline.PlayerEquipmentStates
                .GroupBy(state => state.PlayerId)
                .SelectMany(group => SliceEquipmentStateChanges(group, startTick, endTick))
                .OrderBy(state => state.Tick)
                .ToArray();

            var window = new DemoWindow(
                index,
                coreFrom,
                coreTo,
                dataFrom,
                dataTo,
                firstFrameIndex,
                timeline.Frames.Count,
                frames,
                utilityTracks,
                utilityEffects,
                utilityStates,
                equipmentStates);
            await WriteBrotliJsonAsync(WindowPath(job.Directory, index), window, cancellationToken);
        }

        return new DemoManifest(
            job.Id,
            timeline.Metadata,
            timeline.Events,
            timeline.Frames.Count,
            timeline.UtilityTracks.Count,
            timeline.UtilityEffects.Count,
            timeline.PlayerUtilityStates.Count,
            timeline.PlayerEquipmentStates.Count,
            WindowSeconds,
            windowCount,
            SchemaVersion,
            timeline.RoundResults);
    }

    private static IReadOnlyList<UtilityPoint> SliceUtilityPoints(
        IReadOnlyList<UtilityPoint> items,
        int startTick,
        int endTick)
        => SliceSamples(items, startTick, endTick, item => item.Tick);

    private static IReadOnlyList<UtilityEffectSample> SliceEffectSamples(
        IReadOnlyList<UtilityEffectSample> items,
        int startTick,
        int endTick)
        => SliceSamples(items, startTick, endTick, item => item.Tick);

    private static IReadOnlyList<T> SliceSamples<T>(
        IReadOnlyList<T> items,
        int startTick,
        int endTick,
        Func<T, int> getTick)
    {
        if (items.Count == 0)
            return [];

        var startIndex = FindLastAtOrBefore(items, startTick, getTick);
        if (startIndex < 0)
            startIndex = 0;

        var result = new List<T>();
        for (var index = startIndex; index < items.Count; index++)
        {
            var item = items[index];
            result.Add(item);
            if (getTick(item) > endTick)
                break;
        }
        return result;
    }

    private static IEnumerable<PlayerUtilityState> SliceStateChanges(
        IEnumerable<PlayerUtilityState> source,
        int startTick,
        int endTick)
    {
        var states = source.OrderBy(item => item.Tick).ToArray();
        var previous = states.LastOrDefault(item => item.Tick <= startTick);
        if (previous is not null)
            yield return previous;

        foreach (var state in states.Where(item => item.Tick > startTick && item.Tick <= endTick))
            yield return state;
    }

    private static IEnumerable<PlayerEquipmentState> SliceEquipmentStateChanges(
        IEnumerable<PlayerEquipmentState> source,
        int startTick,
        int endTick)
    {
        var states = source.OrderBy(item => item.Tick).ToArray();
        var previous = states.LastOrDefault(item => item.Tick <= startTick);
        if (previous is not null)
            yield return previous;

        foreach (var state in states.Where(item => item.Tick > startTick && item.Tick <= endTick))
            yield return state;
    }

    private static int FindLastAtOrBefore<T>(
        IReadOnlyList<T> items,
        int tick,
        Func<T, int> getTick)
    {
        var low = 0;
        var high = items.Count - 1;
        while (low <= high)
        {
            var middle = low + (high - low) / 2;
            if (getTick(items[middle]) <= tick)
                low = middle + 1;
            else
                high = middle - 1;
        }
        return high;
    }

    private static async Task WriteBrotliJsonAsync(string path, DemoWindow window, CancellationToken cancellationToken)
    {
        await using var file = new FileStream(
            path,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var brotli = new BrotliStream(file, CompressionLevel.Optimal, leaveOpen: false);
        await JsonSerializer.SerializeAsync(brotli, window, JsonOptions, cancellationToken);
    }

    private static string WindowPath(string directory, int index) =>
        Path.Combine(directory, $"window-{index:D4}.json.br");

    private sealed class ImportJob(
        string id,
        string fileName,
        long fileSizeBytes,
        string directory,
        string sourcePath,
        bool deleteSource)
    {
        public string Id { get; } = id;
        public string FileName { get; } = fileName;
        public long FileSizeBytes { get; } = fileSizeBytes;
        public string Directory { get; } = directory;
        public string SourcePath { get; } = sourcePath;
        public bool DeleteSource { get; } = deleteSource;
        public volatile string Status = "queued";
        public string? Error;
        public DemoManifest? Manifest;

        public DemoImportStatus ToStatus() => new(Id, Status, FileName, FileSizeBytes, Error, Manifest);
    }
}

using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;
using System.Threading.Channels;
using CsDemoMap.Api.Models;

namespace CsDemoMap.Api.Services;

public sealed class DemoImportService : BackgroundService
{
    public const int SchemaVersion = 3;
    public const int WindowSeconds = 30;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly Channel<string> queue = Channel.CreateUnbounded<string>(new UnboundedChannelOptions
    {
        SingleReader = true,
        SingleWriter = false
    });
    private readonly ConcurrentDictionary<string, ImportJob> jobs = new(StringComparer.Ordinal);
    private readonly DemoParserService parser;
    private readonly WinTimelinePredictionService predictor;
    private readonly ILogger<DemoImportService> logger;
    private readonly string storageRoot;

    public DemoImportService(
        DemoParserService parser,
        WinTimelinePredictionService predictor,
        IWebHostEnvironment environment,
        ILogger<DemoImportService> logger)
    {
        this.parser = parser;
        this.predictor = predictor;
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

    internal SituationImportedDemoSource? GetSituationSource(string id) =>
        jobs.TryGetValue(id, out var job)
            ? new(job.Id, job.Status, job.Directory, job.Manifest?.Metadata, job.Manifest?.WindowCount)
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
            var timeline = await parser.ParseAsync(
                source,
                job.FileName,
                cancellationToken,
                collectSemantics: true);
            var parseSeconds = stopwatch.Elapsed.TotalSeconds;
            job.Status = "predicting";
            var winPredictions = await predictor.PredictAsync(timeline, cancellationToken);
            job.Status = "chunking";
            job.Manifest = await WriteWindowsAsync(
                job, timeline, winPredictions, cancellationToken);
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
        WinTimelinePredictionResult winPredictions,
        CancellationToken cancellationToken)
    {
        var windowCount = DemoWindowSliceBuilder.GetWindowCount(timeline.Metadata);
        for (var index = 0; index < windowCount; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var slice = DemoWindowSliceBuilder.Build(timeline, index, winPredictions.Points);
            await WriteBrotliJsonAsync(
                WindowPath(job.Directory, index), slice.Window, cancellationToken);
            var situationSidecar = SituationWindowSidecarStore.Build(
                timeline, index, slice.DataFromTick, slice.DataToTick);
            await SituationWindowSidecarStore.WriteAsync(
                job.Directory, situationSidecar, cancellationToken);
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
            timeline.RoundResults,
            winPredictions.Manifest);
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

using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using CsDemoMap.Api.Models;
using Microsoft.Extensions.Logging;
using static CsDemoMap.Api.Services.SituationArtifactIO;

namespace CsDemoMap.Api.Services;

internal static class SituationStageTwoAcceptance
{
    private const string RequestVersion = "situation-stage2-acceptance-request-v1";
    private const string ManifestVersion = "situation-stage2-acceptance-manifest-v1";
    private const string PerformanceVersion = "situation-stage2-performance-v1";
    private const int RequiredIterations = 200;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    public static async Task RunAsync(
        string requestPath,
        string outputPath,
        CancellationToken cancellationToken)
    {
        var requestFullPath = Path.GetFullPath(requestPath);
        var outputFullPath = Path.GetFullPath(outputPath);
        if (Directory.Exists(outputFullPath) || File.Exists(outputFullPath))
            throw new IOException($"Output path already exists: {outputFullPath}");

        var request = await ReadJsonAsync<AcceptanceRequest>(requestFullPath, cancellationToken);
        ValidateRequest(request);
        var requestDirectory = Path.GetDirectoryName(requestFullPath)!;
        var splitPath = ResolvePath(requestDirectory, request.SplitFile);
        var frozenSamplesDirectory = ResolvePath(requestDirectory, request.FrozenSamplesDirectory);
        var split = await SituationFrozenDatasetLoader.LoadAsync(
            splitPath, request.ExpectedSplitSha256, cancellationToken);
        if (split.ValidationFiles.Contains(request.SourceDemoFile))
            throw new InvalidDataException("Stage-two performance input must belong to the training split.");
        if (!split.MatchIds.TryGetValue(request.SourceDemoFile, out var expectedSourceSha256))
            throw new InvalidDataException("Stage-two performance input is absent from the frozen dataset manifest.");

        var sourcePath = Path.GetFullPath(Path.Combine(split.SourceDirectory, request.SourceDemoFile));
        EnsureDirectChild(
            split.SourceDirectory,
            sourcePath,
            "Training demo must be a direct child of the frozen source directory.");
        if (!File.Exists(sourcePath))
            throw new FileNotFoundException("Stage-two training demo is missing.", sourcePath);

        var sourceSha256 = await FileSha256Async(sourcePath, cancellationToken);
        if (!string.Equals(sourceSha256, expectedSourceSha256, StringComparison.Ordinal))
            throw new InvalidDataException("Training demo hash does not match the frozen dataset manifest.");
        if (request.ExpectedSourceSha256 is not null &&
            !string.Equals(sourceSha256, request.ExpectedSourceSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Training demo hash does not match the acceptance request.");

        Directory.CreateDirectory(outputFullPath);
        var createdAt = DateTimeOffset.UtcNow;
        var manifestPath = Path.Combine(outputFullPath, "manifest.json");
        await WriteJsonAsync(manifestPath, new
        {
            schemaVersion = ManifestVersion,
            status = "incomplete",
            createdAtUtc = createdAt,
            completedAtUtc = (DateTimeOffset?)null,
            source = new { fileName = request.SourceDemoFile, role = "training", sha256 = sourceSha256 },
            files = Array.Empty<object>()
        }, cancellationToken);

        try
        {
            SituationSampleDirectoryValidator.Validate(frozenSamplesDirectory);
            await File.WriteAllBytesAsync(
                Path.Combine(outputFullPath, "acceptance-request.json"),
                await File.ReadAllBytesAsync(requestFullPath, cancellationToken),
                cancellationToken);

            await using var source = new FileStream(
                sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var timeline = await new DemoParserService().ParseAsync(
                source, request.SourceDemoFile, cancellationToken, collectSemantics: true);
            if (timeline.Semantics is null)
                throw new InvalidDataException("Training demo parsing did not produce semantic observations.");
            if (!string.Equals(timeline.Metadata.MapName, "de_mirage", StringComparison.OrdinalIgnoreCase))
                throw new NotSupportedException("Stage-two acceptance only supports de_mirage.");

            var demoRef = $"demo-{sourceSha256[..12]}";
            var fixedRequests = SelectRequests(timeline);
            var inputDirectory = Path.Combine(outputFullPath, "benchmark-inputs");
            Directory.CreateDirectory(inputDirectory);
            await WriteRequiredWindowsAsync(
                inputDirectory, timeline, fixedRequests, cancellationToken);

            using var offlineService = new SituationSceneService();
            var requestRecords = new List<FixedRequestRecord>();
            SituationSceneBuildResult? defusingResult = null;
            foreach (var item in fixedRequests)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var result = offlineService.BuildFromTimeline(
                    timeline,
                    demoRef,
                    item.WindowIndex,
                    item.RequestedTick,
                    cancellationToken: cancellationToken);
                requestRecords.Add(new(
                    item.Name,
                    item.Category,
                    item.WindowIndex,
                    item.RequestedTick,
                    result.Scene.Tick,
                    item.CrossWindow,
                    result.Sha256));
                if (item.Name == "defusing")
                    defusingResult = result;
            }

            var defusing = defusingResult
                ?? throw new InvalidDataException("The fixed request list did not contain a defusing scene.");
            ValidateDefusingScene(defusing.Scene);
            await File.WriteAllTextAsync(
                Path.Combine(outputFullPath, "defusing-scene.json"),
                defusing.CanonicalJson,
                new UTF8Encoding(false),
                cancellationToken);

            var requestListArtifact = new
            {
                schemaVersion = "situation-stage2-fixed-requests-v1",
                source = new
                {
                    fileName = request.SourceDemoFile,
                    role = "training",
                    demoRef,
                    sha256 = sourceSha256,
                    splitSha256 = await FileSha256Async(splitPath, cancellationToken)
                },
                versions = Versions(),
                requests = requestRecords
            };
            await WriteJsonAsync(
                Path.Combine(outputFullPath, "fixed-requests.json"),
                requestListArtifact,
                cancellationToken);

            var performance = await MeasureAsync(
                inputDirectory,
                timeline.Metadata,
                demoRef,
                requestRecords,
                request.IterationsPerMode,
                cancellationToken);
            await WriteJsonAsync(
                Path.Combine(outputFullPath, "performance.json"),
                new
                {
                    schemaVersion = PerformanceVersion,
                    source = new
                    {
                        fileName = request.SourceDemoFile,
                        role = "training",
                        demoRef,
                        sha256 = sourceSha256
                    },
                    versions = Versions(),
                    iterationsPerMode = request.IterationsPerMode,
                    threshold = new
                    {
                        mode = "cache-miss-files-prewarmed",
                        metric = "p95TotalMilliseconds",
                        maximumMilliseconds = 100d,
                        passed = performance.WarmMiss.Total.P95 <= 100d
                    },
                    scope = "Only SituationSceneService scene construction is measured. Stage nine must separately verify scene construction plus rule analysis p95 <= 100 ms.",
                    firstFileReadDefinition = "Each measured request uses a fresh service with no result cache and no explicit file pre-read. Operating-system page-cache state is not controlled, so this is not a physical cold-disk benchmark.",
                    fixedRequests = requestRecords,
                    modes = new[] { performance.CacheHit, performance.WarmMiss, performance.FirstFileRead }
                },
                cancellationToken);

            var historyCoverage = defusing.Scene.Players.Count == 0
                ? 0d
                : defusing.Scene.Players.Max(player => player.Trajectory.CoverageSeconds);
            await File.WriteAllTextAsync(
                Path.Combine(outputFullPath, "review.md"),
                BuildReview(defusing, historyCoverage, performance),
                new UTF8Encoding(false),
                cancellationToken);

            if (performance.WarmMiss.Total.P95 > 100d)
                throw new InvalidOperationException(
                    $"Warm file-cache miss p95 {performance.WarmMiss.Total.P95:F2} ms exceeds 100 ms.");

            var files = await InventoryFilesAsync(outputFullPath, cancellationToken, "manifest.json");
            await WriteJsonAsync(manifestPath, new
            {
                schemaVersion = ManifestVersion,
                status = "complete",
                createdAtUtc = createdAt,
                completedAtUtc = DateTimeOffset.UtcNow,
                source = new
                {
                    fileName = request.SourceDemoFile,
                    role = "training",
                    demoRef,
                    sha256 = sourceSha256,
                    splitFile = Path.GetFileName(splitPath),
                    splitSha256 = await FileSha256Async(splitPath, cancellationToken),
                    frozenSamples = Path.GetFileName(frozenSamplesDirectory.TrimEnd(Path.DirectorySeparatorChar)),
                    holdoutUsage = "contract-and-extraction-correctness-only"
                },
                versions = Versions(),
                defusingScene = new
                {
                    requestedTick = defusing.Scene.RequestedTick,
                    actualTick = defusing.Scene.Tick,
                    sha256 = defusing.Sha256
                },
                performanceGatePassed = true,
                files
            }, cancellationToken);

            Console.WriteLine(
                $"Situation stage-two acceptance passed: defusing tick {defusing.Scene.Tick}, " +
                $"warm miss p95 {performance.WarmMiss.Total.P95:F2} ms, output {outputFullPath}");
        }
        catch
        {
            Console.Error.WriteLine($"Situation stage-two acceptance is incomplete: {outputFullPath}");
            throw;
        }
    }

    private static async Task<PerformanceResult> MeasureAsync(
        string inputDirectory,
        DemoMetadata metadata,
        string demoRef,
        IReadOnlyList<FixedRequestRecord> requests,
        int iterations,
        CancellationToken cancellationToken)
    {
        var crossWindow = requests.Where(item => item.CrossWindow).Take(2).ToArray();
        if (crossWindow.Length != 2)
            throw new InvalidDataException("The fixed request list must contain two cross-window requests.");
        var defusing = requests.Single(item => item.Name == "defusing");

        // Run the fresh-service mode first. File creation itself may populate the OS page cache,
        // which is why the report explicitly avoids claiming a physical cold-disk measurement.
        var first = new MeasurementCollector("first-file-read", iterations);
        for (var index = 0; index < iterations; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var logger = new PerformanceLogger();
            using var service = CreateService(logger, maxCacheEntries: 1);
            var request = requests[index % requests.Count];
            await MeasureOneAsync(service, logger, inputDirectory, metadata, demoRef, request, first, cancellationToken);
            first.AddSnapshot(service.GetExecutionSnapshot());
        }

        await PrewarmFilesAsync(inputDirectory, cancellationToken);
        var warmLogger = new PerformanceLogger();
        using var warmService = CreateService(warmLogger, maxCacheEntries: 1);
        var warmStart = warmService.GetExecutionSnapshot();
        var warm = new MeasurementCollector("cache-miss-files-prewarmed", iterations);
        for (var index = 0; index < iterations; index++)
        {
            var request = crossWindow[index % crossWindow.Length];
            await MeasureOneAsync(warmService, warmLogger, inputDirectory, metadata, demoRef, request, warm, cancellationToken);
        }
        warm.SetSnapshotDelta(warmStart, warmService.GetExecutionSnapshot());

        var hitLogger = new PerformanceLogger();
        using var hitService = CreateService(hitLogger, maxCacheEntries: 256);
        _ = await hitService.BuildFromStoredWindowsAsync(
            inputDirectory, metadata, demoRef, defusing.WindowIndex, defusing.RequestedTick, cancellationToken);
        hitLogger.Clear();
        var hitStart = hitService.GetExecutionSnapshot();
        var hit = new MeasurementCollector("result-cache-hit", iterations);
        for (var index = 0; index < iterations; index++)
            await MeasureOneAsync(hitService, hitLogger, inputDirectory, metadata, demoRef, defusing, hit, cancellationToken);
        hit.SetSnapshotDelta(hitStart, hitService.GetExecutionSnapshot());

        var firstResult = first.Complete(expectCacheHit: false, requireTwoWindows: false);
        var warmResult = warm.Complete(expectCacheHit: false, requireTwoWindows: true);
        var hitResult = hit.Complete(expectCacheHit: true, requireTwoWindows: false);
        return new(hitResult, warmResult, firstResult);
    }

    private static SituationSceneService CreateService(PerformanceLogger logger, int maxCacheEntries) =>
        new(
            sourceResolver: null,
            sidecarOverrides: null,
            logger,
            new SituationSceneServiceLimits(maxCacheEntries, 64L * 1024 * 1024, 2, 32));

    private static async Task MeasureOneAsync(
        SituationSceneService service,
        PerformanceLogger logger,
        string directory,
        DemoMetadata metadata,
        string demoRef,
        FixedRequestRecord request,
        MeasurementCollector collector,
        CancellationToken cancellationToken)
    {
        logger.Clear();
        var stopwatch = Stopwatch.StartNew();
        var result = await service.BuildFromStoredWindowsAsync(
            directory,
            metadata,
            demoRef,
            request.WindowIndex,
            request.RequestedTick,
            cancellationToken);
        stopwatch.Stop();
        if (result.Scene.Tick != request.ActualTick || result.Sha256 != request.SceneSha256)
            throw new InvalidDataException("File-backed performance request differs from its frozen offline result.");
        collector.Add(stopwatch.Elapsed.TotalMilliseconds, logger.TakeSingle());
    }

    private static IReadOnlyList<SelectedRequest> SelectRequests(DemoTimeline timeline)
    {
        var ticksPerWindow = checked(DemoImportService.WindowSeconds * timeline.Metadata.TickRate);
        var frames = timeline.Frames.OrderBy(frame => frame.Tick).ToArray();
        var defusing = frames.FirstOrDefault(frame =>
            frame.Bomb.State == "defusing" &&
            frame.Bomb.DefuserId is not null &&
            frame.Bomb.SecondsToDefuse is >= 0d)
            ?? throw new InvalidDataException("The selected training demo contains no usable defusing observation.");

        var crossGroup = frames
            .Where(frame => frame.Tick >= ticksPerWindow)
            .Where(frame => frame.Tick % ticksPerWindow <= timeline.Metadata.TickRate)
            .Where(frame => frame.Round.Phase is "live" or "post-plant")
            .GroupBy(frame => frame.Tick / ticksPerWindow)
            .FirstOrDefault(group => group.Select(frame => frame.Tick).Distinct().Count() >= 2)
            ?? throw new InvalidDataException("The selected training demo contains no cross-window request pair.");
        var crossFrames = crossGroup
            .GroupBy(frame => frame.Tick)
            .Select(group => group.First())
            .OrderBy(frame => frame.Tick)
            .ToArray();
        var planted = frames.FirstOrDefault(frame =>
            frame.Bomb.State == "planted" && frame.Tick != defusing.Tick);
        var regular = planted ?? frames.First(frame =>
            frame.Round.Phase == "live" && frame.Tick != defusing.Tick);

        return
        [
            ToRequest("defusing", "defusing", defusing, false, ticksPerWindow),
            ToRequest("cross-window-a", "cross-window", crossFrames[0], true, ticksPerWindow),
            ToRequest("cross-window-b", "cross-window", crossFrames[^1], true, ticksPerWindow),
            ToRequest("post-plant-or-live", planted is null ? "live" : "planted", regular, false, ticksPerWindow)
        ];
    }

    private static SelectedRequest ToRequest(
        string name,
        string category,
        DemoFrame frame,
        bool crossWindow,
        int ticksPerWindow) =>
        new(name, category, frame.Tick / ticksPerWindow, frame.Tick, crossWindow);

    private static void ValidateDefusingScene(MinimapSceneV1 scene)
    {
        if (scene.Bomb.State != SituationBombState.Defusing ||
            scene.Bomb.DefuserSlot is null ||
            scene.Bomb.SecondsToDefuse is null or < 0d)
            throw new InvalidDataException("The real defusing scene lacks state, defuser, or countdown data.");
        var historyCoverage = scene.Players.Count == 0
            ? 0d
            : scene.Players.Max(player => player.Trajectory.CoverageSeconds);
        if (historyCoverage <= 0d || historyCoverage > 4.5d)
            throw new InvalidDataException("The real defusing scene has invalid history coverage.");
    }

    private static async Task WriteRequiredWindowsAsync(
        string directory,
        DemoTimeline timeline,
        IReadOnlyList<SelectedRequest> requests,
        CancellationToken cancellationToken)
    {
        var indexes = requests
            .SelectMany(item => item.WindowIndex == 0
                ? new[] { 0 }
                : new[] { item.WindowIndex - 1, item.WindowIndex })
            .Distinct()
            .OrderBy(index => index)
            .ToArray();
        foreach (var index in indexes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var slice = DemoWindowSliceBuilder.Build(timeline, index, []);
            await WriteWindowAsync(directory, slice.Window, cancellationToken);
            var sidecar = SituationWindowSidecarStore.Build(
                timeline, index, slice.DataFromTick, slice.DataToTick);
            await SituationWindowSidecarStore.WriteAsync(directory, sidecar, cancellationToken);
        }
    }

    private static async Task WriteWindowAsync(
        string directory,
        DemoWindow window,
        CancellationToken cancellationToken)
    {
        var path = Path.Combine(directory, $"window-{window.Index:D4}.json.br");
        await using var file = new FileStream(
            path, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var brotli = new BrotliStream(file, CompressionLevel.Optimal, leaveOpen: false);
        await JsonSerializer.SerializeAsync(
            brotli, window, new JsonSerializerOptions(JsonSerializerDefaults.Web), cancellationToken);
    }

    private static async Task PrewarmFilesAsync(string directory, CancellationToken cancellationToken)
    {
        foreach (var path in Directory.EnumerateFiles(directory, "*.json.br").Order(StringComparer.Ordinal))
            _ = await File.ReadAllBytesAsync(path, cancellationToken);
    }

    internal static async Task VerifyFrozenSplitAsync(
        string splitPath,
        string expectedSplitSha256,
        CancellationToken cancellationToken)
    {
        _ = await SituationFrozenDatasetLoader.LoadAsync(
            splitPath, expectedSplitSha256, cancellationToken);
    }

    private static void ValidateRequest(AcceptanceRequest request)
    {
        if (request.SchemaVersion != RequestVersion)
            throw new InvalidDataException("Unsupported stage-two acceptance request version.");
        if (string.IsNullOrWhiteSpace(request.SplitFile) ||
            string.IsNullOrWhiteSpace(request.FrozenSamplesDirectory) ||
            string.IsNullOrWhiteSpace(request.SourceDemoFile) ||
            !IsSha256(request.ExpectedSplitSha256) ||
            request.ExpectedSourceSha256 is not null && !IsSha256(request.ExpectedSourceSha256))
            throw new InvalidDataException("Stage-two acceptance request is incomplete.");
        if (request.IterationsPerMode < RequiredIterations)
            throw new InvalidDataException($"Each performance mode requires at least {RequiredIterations} iterations.");
    }

    private static object Versions() => new
    {
        scene = SituationContractVersions.Scene,
        builder = SituationSceneBuilder.BuilderVersion,
        geometry = SituationSceneBuilder.GeometryVersion,
        sidecar = SituationWindowSidecarVersions.V1
    };

    private static string BuildReview(
        SituationSceneBuildResult defusing,
        double historyCoverage,
        PerformanceResult performance)
    {
        var scene = defusing.Scene;
        var clockUnknown = scene.DataQuality.Any(item => item.Code == SituationDataQualityCodes.ClockUnknown);
        return $"""
            # 阶段二真实样例与性能验收

            - 数据侧：训练集；保留集仅用于 r9 冻结契约与提取正确性回归。
            - 真实拆除场景：请求 tick `{scene.RequestedTick}`，实际 tick `{scene.Tick}`，SHA-256 `{defusing.Sha256}`。
            - C4：状态 `{scene.Bomb.State}`，拆除者 `{scene.Bomb.DefuserSlot}`，剩余拆除时间 `{scene.Bomb.SecondsToDefuse:F3}` 秒。
            - 时钟质量：round clock source `{scene.Round.ClockSource ?? "unknown"}`，`clock-unknown` = `{clockUnknown.ToString().ToLowerInvariant()}`。
            - 历史窗口：最大轨迹覆盖 `{historyCoverage:F3}` 秒，上限 4.5 秒。
            - 结果缓存命中：p95 `{performance.CacheHit.Total.P95:F2}` ms。
            - 结果未命中、文件已预热：p95 `{performance.WarmMiss.Total.P95:F2}` ms，门限 `<= 100 ms`，结果 `{(performance.WarmMiss.Total.P95 <= 100d ? "通过" : "失败")}`。
            - 首次文件读取（服务级）：p95 `{performance.FirstFileRead.Total.P95:F2}` ms；操作系统页缓存未受控制，不能解释为物理冷盘耗时。
            - 性能范围只包含 `SituationSceneService` 场景构建。阶段九仍需验证场景构建与规则分析合计 p95 `<= 100 ms`。
            """;
    }

    private static async Task<T> ReadJsonAsync<T>(string path, CancellationToken cancellationToken) =>
        JsonSerializer.Deserialize<T>(await File.ReadAllBytesAsync(path, cancellationToken), JsonOptions)
        ?? throw new InvalidDataException($"JSON file is empty: {Path.GetFileName(path)}");

    private sealed record AcceptanceRequest(
        string SchemaVersion,
        string SplitFile,
        string ExpectedSplitSha256,
        string FrozenSamplesDirectory,
        string SourceDemoFile,
        string? ExpectedSourceSha256,
        int IterationsPerMode);

    private sealed record SelectedRequest(
        string Name,
        string Category,
        int WindowIndex,
        int RequestedTick,
        bool CrossWindow);

    private sealed record FixedRequestRecord(
        string Name,
        string Category,
        int WindowIndex,
        int RequestedTick,
        int ActualTick,
        bool CrossWindow,
        string SceneSha256);


    private sealed record Metric(double P50, double P95, double Maximum);

    private sealed record MemoryMetric(
        long ManagedStartBytes,
        long ManagedEndBytes,
        long ManagedMaximumBytes,
        long WorkingSetStartBytes,
        long WorkingSetEndBytes,
        long WorkingSetMaximumBytes);

    private sealed record CacheMetric(
        long Hits,
        long Misses,
        long BuildExecutions,
        long Evictions,
        int FinalEntries,
        long FinalUtf8Bytes,
        int MaximumEntries,
        long MaximumUtf8Bytes);

    private sealed record ModeResult(
        string Mode,
        int Iterations,
        Metric Total,
        Metric SourceRevision,
        Metric Input,
        Metric BuildValidateSerialize,
        Metric WindowsRead,
        MemoryMetric Memory,
        CacheMetric Cache);

    private sealed record PerformanceResult(
        ModeResult CacheHit,
        ModeResult WarmMiss,
        ModeResult FirstFileRead);

    private sealed record LogMeasurement(
        bool CacheHit,
        int WindowsRead,
        double RevisionMilliseconds,
        double InputMilliseconds,
        double CompletionMilliseconds,
        double TotalMilliseconds);

    private sealed class MeasurementCollector
    {
        private readonly string mode;
        private readonly int expectedIterations;
        private readonly List<double> totals = [];
        private readonly List<double> revisions = [];
        private readonly List<double> inputs = [];
        private readonly List<double> completions = [];
        private readonly List<double> windowsRead = [];
        private readonly List<bool> cacheHits = [];
        private readonly long managedStart = GC.GetTotalMemory(false);
        private readonly long workingSetStart = Environment.WorkingSet;
        private long managedMaximum;
        private long workingSetMaximum;
        private long hits;
        private long misses;
        private long builds;
        private long evictions;
        private int finalEntries;
        private long finalBytes;
        private int maximumEntries;
        private long maximumBytes;

        public MeasurementCollector(string mode, int expectedIterations)
        {
            this.mode = mode;
            this.expectedIterations = expectedIterations;
            managedMaximum = managedStart;
            workingSetMaximum = workingSetStart;
        }

        public void Add(double observedTotalMilliseconds, LogMeasurement measurement)
        {
            totals.Add(Math.Max(observedTotalMilliseconds, measurement.TotalMilliseconds));
            revisions.Add(measurement.RevisionMilliseconds);
            inputs.Add(measurement.InputMilliseconds);
            completions.Add(measurement.CompletionMilliseconds);
            windowsRead.Add(measurement.WindowsRead);
            cacheHits.Add(measurement.CacheHit);
            managedMaximum = Math.Max(managedMaximum, GC.GetTotalMemory(false));
            workingSetMaximum = Math.Max(workingSetMaximum, Environment.WorkingSet);
        }

        public void AddSnapshot(SituationSceneExecutionSnapshot snapshot)
        {
            hits += snapshot.CacheHits;
            misses += snapshot.CacheMisses;
            builds += snapshot.BuildExecutions;
            evictions += snapshot.CacheEvictions;
            finalEntries = snapshot.CacheEntries;
            finalBytes = snapshot.CacheUtf8Bytes;
            maximumEntries = Math.Max(maximumEntries, snapshot.CacheEntries);
            maximumBytes = Math.Max(maximumBytes, snapshot.CacheUtf8Bytes);
        }

        public void SetSnapshotDelta(
            SituationSceneExecutionSnapshot start,
            SituationSceneExecutionSnapshot end)
        {
            hits = end.CacheHits - start.CacheHits;
            misses = end.CacheMisses - start.CacheMisses;
            builds = end.BuildExecutions - start.BuildExecutions;
            evictions = end.CacheEvictions - start.CacheEvictions;
            finalEntries = end.CacheEntries;
            finalBytes = end.CacheUtf8Bytes;
            maximumEntries = Math.Max(maximumEntries, end.CacheEntries);
            maximumBytes = Math.Max(maximumBytes, end.CacheUtf8Bytes);
        }

        public ModeResult Complete(bool expectCacheHit, bool requireTwoWindows)
        {
            if (totals.Count != expectedIterations || cacheHits.Any(value => value != expectCacheHit))
                throw new InvalidDataException($"Performance mode '{mode}' did not preserve its cache state.");
            if (requireTwoWindows && windowsRead.Any(value => value != 2d))
                throw new InvalidDataException($"Performance mode '{mode}' did not exercise two-window reads.");
            return new(
                mode,
                totals.Count,
                Summarize(totals),
                Summarize(revisions),
                Summarize(inputs),
                Summarize(completions),
                Summarize(windowsRead),
                new(
                    managedStart,
                    GC.GetTotalMemory(false),
                    managedMaximum,
                    workingSetStart,
                    Environment.WorkingSet,
                    workingSetMaximum),
                new(hits, misses, builds, evictions, finalEntries, finalBytes, maximumEntries, maximumBytes));
        }

        private static Metric Summarize(IReadOnlyList<double> values)
        {
            var sorted = values.Order().ToArray();
            return new(
                Percentile(sorted, 0.50),
                Percentile(sorted, 0.95),
                sorted[^1]);
        }

        private static double Percentile(IReadOnlyList<double> sorted, double percentile)
        {
            var index = Math.Clamp((int)Math.Ceiling(percentile * sorted.Count) - 1, 0, sorted.Count - 1);
            return sorted[index];
        }
    }

    private sealed class PerformanceLogger : ILogger<SituationSceneService>
    {
        private readonly object sync = new();
        private readonly List<LogMeasurement> measurements = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel == LogLevel.Information;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel != LogLevel.Information ||
                state is not IEnumerable<KeyValuePair<string, object?>> properties)
                return;
            var values = properties.ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal);
            if (!values.ContainsKey("CacheHit"))
                return;
            var measurement = new LogMeasurement(
                Convert.ToBoolean(values["CacheHit"], System.Globalization.CultureInfo.InvariantCulture),
                Convert.ToInt32(values["WindowsRead"], System.Globalization.CultureInfo.InvariantCulture),
                Convert.ToDouble(values["RevisionElapsedMs"], System.Globalization.CultureInfo.InvariantCulture),
                Convert.ToDouble(values["InputElapsedMs"], System.Globalization.CultureInfo.InvariantCulture),
                Convert.ToDouble(values["CompletionElapsedMs"], System.Globalization.CultureInfo.InvariantCulture),
                Convert.ToDouble(values["TotalElapsedMs"], System.Globalization.CultureInfo.InvariantCulture));
            lock (sync)
                measurements.Add(measurement);
        }

        public void Clear()
        {
            lock (sync)
                measurements.Clear();
        }

        public LogMeasurement TakeSingle()
        {
            lock (sync)
            {
                if (measurements.Count != 1)
                    throw new InvalidDataException("A measured scene build must emit exactly one success diagnostic.");
                var result = measurements[0];
                measurements.Clear();
                return result;
            }
        }
    }
}

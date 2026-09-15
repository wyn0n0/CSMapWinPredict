using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using CsDemoMap.Api.Models;

namespace CsDemoMap.Api.Services;

internal sealed record SituationImportedDemoSource(
    string DemoId,
    string Status,
    string Directory,
    DemoMetadata? Metadata,
    int? WindowCount);

internal sealed record SituationSceneBuildResult(
    MinimapSceneV1 Scene,
    string CanonicalJson,
    string Sha256);

internal sealed class SituationSceneService : IDisposable
{
    private readonly Func<string, SituationImportedDemoSource?>? sourceResolver;
    private readonly SituationSidecarOverrideRegistry? sidecarOverrides;
    private readonly ILogger<SituationSceneService>? logger;
    private readonly SituationInputAdapter timelineAdapter = new();
    private readonly SituationWindowInputAdapter windowAdapter = new();
    private readonly SituationSceneBuilder sceneBuilder = new();
    private readonly SituationSceneResultCache resultCache;
    private readonly SituationSceneBuildCoordinator<SituationSceneCacheKey, SituationSceneBuildProduct> coordinator;
    private readonly CancellationTokenRegistration stoppingRegistration;
    private int disposed;

    public SituationSceneService()
        : this(null, null, null, SituationSceneServiceLimits.Default, default)
    {
    }

    public SituationSceneService(
        Func<string, SituationImportedDemoSource?> sourceResolver,
        ILogger<SituationSceneService>? logger = null)
        : this(sourceResolver, null, logger, SituationSceneServiceLimits.Default, default)
    {
    }

    public SituationSceneService(
        Func<string, SituationImportedDemoSource?> sourceResolver,
        SituationSidecarOverrideRegistry? sidecarOverrides,
        ILogger<SituationSceneService>? logger = null)
        : this(sourceResolver, sidecarOverrides, logger, SituationSceneServiceLimits.Default, default)
    {
    }

    internal SituationSceneService(
        Func<string, SituationImportedDemoSource?>? sourceResolver,
        SituationSidecarOverrideRegistry? sidecarOverrides,
        ILogger<SituationSceneService>? logger,
        SituationSceneServiceLimits limits,
        CancellationToken serviceStoppingToken = default)
    {
        ArgumentNullException.ThrowIfNull(limits);
        limits.Validate();
        this.sourceResolver = sourceResolver;
        this.sidecarOverrides = sidecarOverrides;
        this.logger = logger;
        resultCache = new(limits);
        coordinator = new(limits.MaxConcurrentBuilds, limits.MaxQueuedBuilds);
        stoppingRegistration = serviceStoppingToken.Register(coordinator.Dispose);
    }

    public async Task<SituationSceneBuildResult> BuildFromImportedDemoAsync(
        string demoId,
        int windowIndex,
        int requestedTick,
        CancellationToken cancellationToken = default)
    {
        var operation = Stopwatch.StartNew();
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(demoId))
                throw new ArgumentException("Demo ID is required.", nameof(demoId));
            if (sourceResolver is null)
                throw new InvalidOperationException("Imported demo lookup is not configured for this scene service.");

            var source = sourceResolver(demoId.Trim())
                ?? throw new KeyNotFoundException("Imported demo was not found.");
            if (!string.Equals(source.Status, "completed", StringComparison.Ordinal) ||
                source.Metadata is null || source.WindowCount is null)
                throw new InvalidOperationException("Imported demo is not ready for situation scene construction.");
            ValidateRequest(source.Metadata, windowIndex, requestedTick, source.WindowCount);
            var sidecarOverride = sidecarOverrides is null
                ? null
                : await sidecarOverrides.ResolveAsync(
                    source.DemoId, source.Directory, windowIndex, cancellationToken);
            var sidecarDirectory = sidecarOverride?.SidecarDirectory ?? source.Directory;

            return await BuildFileBackedAsync(
                source.Directory,
                sidecarDirectory,
                source.Metadata,
                source.DemoId,
                windowIndex,
                requestedTick,
                sidecarOverride?.Revision ?? "original-sidecars",
                "imported-demo",
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            LogCancellation("imported-demo", windowIndex, requestedTick, operation.Elapsed);
            throw;
        }
        catch (Exception exception)
        {
            var mapped = SituationSceneErrorMapper.Map(
                exception, SituationSceneErrorContext.ImportedDemo);
            LogFailure("imported-demo", windowIndex, requestedTick, mapped, operation.Elapsed);
            throw mapped;
        }
    }

    public SituationSceneBuildResult BuildFromTimeline(
        DemoTimeline timeline,
        string demoRef,
        int windowIndex,
        int requestedTick,
        SceneObservationBoundary observationBoundary = SceneObservationBoundary.CompleteTimeline,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(timeline);
        ValidateRequest(timeline.Metadata, windowIndex, requestedTick);
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var input = timelineAdapter.BuildFromTimeline(
                timeline,
                demoRef,
                windowIndex,
                requestedTick,
                observationBoundary,
                cancellationToken);
            var inputElapsed = stopwatch.Elapsed;
            var result = Complete(input, cancellationToken);
            LogSuccess(
                "timeline", result.Scene, false, 0, TimeSpan.Zero,
                inputElapsed, stopwatch.Elapsed - inputElapsed, stopwatch.Elapsed);
            return result;
        }
        catch (OperationCanceledException)
        {
            LogCancellation("timeline", windowIndex, requestedTick, stopwatch.Elapsed);
            throw;
        }
        catch (Exception exception)
        {
            var mapped = SituationSceneErrorMapper.Map(exception, SituationSceneErrorContext.Timeline);
            LogFailure("timeline", windowIndex, requestedTick, mapped, stopwatch.Elapsed);
            throw;
        }
    }

    public async Task<SituationSceneBuildResult> BuildFromStoredWindowsAsync(
        string directory,
        DemoMetadata metadata,
        string demoRef,
        int windowIndex,
        int requestedTick,
        CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            return await BuildFileBackedAsync(
                directory, directory, metadata, demoRef, windowIndex, requestedTick,
                "direct-sidecars", "stored-windows", cancellationToken);
        }
        catch (OperationCanceledException)
        {
            LogCancellation("stored-windows", windowIndex, requestedTick, stopwatch.Elapsed);
            throw;
        }
        catch (Exception exception)
        {
            var mapped = SituationSceneErrorMapper.Map(
                exception, SituationSceneErrorContext.StoredWindows);
            LogFailure("stored-windows", windowIndex, requestedTick, mapped, stopwatch.Elapsed);
            throw mapped;
        }
    }

    internal async Task<SituationSceneBuildResult> BuildFromStoredWindowsAsync(
        string windowDirectory,
        string sidecarDirectory,
        DemoMetadata metadata,
        string demoRef,
        int windowIndex,
        int requestedTick,
        CancellationToken cancellationToken = default)
        => await BuildFileBackedAsync(
            windowDirectory,
            sidecarDirectory,
            metadata,
            demoRef,
            windowIndex,
            requestedTick,
            "direct-split-sidecars",
            "stored-windows",
            cancellationToken);

    public async Task<SituationSidecarOverride> RegisterSidecarOverrideAsync(
        string demoId,
        string sidecarDirectory,
        CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(demoId))
                throw new ArgumentException("Demo ID is required.", nameof(demoId));
            if (sourceResolver is null || sidecarOverrides is null)
                throw new InvalidOperationException("Situation sidecar overrides are not configured for this service.");
            var source = sourceResolver(demoId.Trim())
                ?? throw new KeyNotFoundException("Imported demo was not found.");
            if (!string.Equals(source.Status, "completed", StringComparison.Ordinal) ||
                source.Metadata is null || source.WindowCount is null)
                throw new InvalidOperationException("Imported demo is not ready for situation sidecar registration.");
            return await sidecarOverrides.RegisterAsync(
                source.DemoId, source.Directory, sidecarDirectory, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            LogCancellation("sidecar-registration", -1, -1, stopwatch.Elapsed);
            throw;
        }
        catch (Exception exception)
        {
            var mapped = SituationSceneErrorMapper.Map(
                exception, SituationSceneErrorContext.SidecarRegistration);
            LogFailure("sidecar-registration", -1, -1, mapped, stopwatch.Elapsed);
            throw mapped;
        }
    }

    public bool UnregisterSidecarOverride(string demoId)
    {
        if (string.IsNullOrWhiteSpace(demoId))
            throw new ArgumentException("Demo ID is required.", nameof(demoId));
        if (sidecarOverrides is null)
            throw new InvalidOperationException("Situation sidecar overrides are not configured for this service.");
        return sidecarOverrides.Unregister(demoId.Trim());
    }

    internal SituationSceneBuildResult BuildFromWindows(
        DemoMetadata metadata,
        string demoRef,
        int windowIndex,
        int requestedTick,
        IReadOnlyList<DemoWindow> windows,
        IReadOnlyList<SituationWindowSidecarV1> sidecars,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateRequest(metadata, windowIndex, requestedTick);
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var input = windowAdapter.BuildFromWindows(
                metadata,
                demoRef,
                windowIndex,
                requestedTick,
                windows,
                sidecars,
                cancellationToken);
            var inputElapsed = stopwatch.Elapsed;
            var result = Complete(input, cancellationToken);
            LogSuccess(
                "windows", result.Scene, false, windows.Count, TimeSpan.Zero,
                inputElapsed, stopwatch.Elapsed - inputElapsed, stopwatch.Elapsed);
            return result;
        }
        catch (OperationCanceledException)
        {
            LogCancellation("windows", windowIndex, requestedTick, stopwatch.Elapsed);
            throw;
        }
        catch (Exception exception)
        {
            var mapped = SituationSceneErrorMapper.Map(
                exception, SituationSceneErrorContext.WindowObjects);
            LogFailure("windows", windowIndex, requestedTick, mapped, stopwatch.Elapsed);
            throw;
        }
    }

    internal SituationSceneExecutionSnapshot GetExecutionSnapshot()
    {
        var cached = resultCache.Snapshot();
        var execution = coordinator.Snapshot();
        return new(
            cached.Entries,
            cached.Utf8Bytes,
            cached.Hits,
            cached.Misses,
            cached.Evictions,
            execution.Executions,
            execution.Active,
            execution.Queued,
            execution.Inflight);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
            return;
        stoppingRegistration.Dispose();
        coordinator.Dispose();
    }

    private async Task<SituationSceneBuildResult> BuildFileBackedAsync(
        string windowDirectory,
        string sidecarDirectory,
        DemoMetadata metadata,
        string demoRef,
        int windowIndex,
        int requestedTick,
        string sourceMarker,
        string diagnosticSource,
        CancellationToken cancellationToken)
    {
        var totalStopwatch = Stopwatch.StartNew();
        cancellationToken.ThrowIfCancellationRequested();
        ValidateRequest(metadata, windowIndex, requestedTick);
        if (string.IsNullOrWhiteSpace(demoRef))
            throw new ArgumentException("Demo reference is required.", nameof(demoRef));

        var revisionStopwatch = Stopwatch.StartNew();
        var sourceRevision = await ComputeSourceRevisionAsync(
            windowDirectory, sidecarDirectory, metadata, windowIndex, sourceMarker, cancellationToken);
        var revisionElapsed = revisionStopwatch.Elapsed;
        var key = new SituationSceneCacheKey(
            sourceRevision,
            demoRef.Trim(),
            windowIndex,
            requestedTick,
            SituationContractVersions.Scene,
            SituationSceneBuilder.BuilderVersion,
            SituationSceneBuilder.GeometryVersion);
        if (resultCache.TryGet(key, out var cached))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = cached.Materialize();
            cancellationToken.ThrowIfCancellationRequested();
            LogSuccess(
                diagnosticSource, result.Scene, true, 0, revisionElapsed,
                TimeSpan.Zero, TimeSpan.Zero, totalStopwatch.Elapsed);
            return result;
        }

        var value = await coordinator.RunAsync(
            key,
            async buildToken =>
            {
                var product = await BuildFromStoredWindowsCoreAsync(
                    windowDirectory,
                    sidecarDirectory,
                    metadata,
                    demoRef,
                    windowIndex,
                    requestedTick,
                    buildToken);
                var finalRevision = await ComputeSourceRevisionAsync(
                    windowDirectory, sidecarDirectory, metadata, windowIndex, sourceMarker, buildToken);
                if (!string.Equals(sourceRevision, finalRevision, StringComparison.Ordinal))
                    throw new InvalidDataException("Situation scene source changed while it was being read.");

                return product;
            },
            cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        var materialized = value.Value.Materialize();
        cancellationToken.ThrowIfCancellationRequested();
        resultCache.Set(key, value.Value);
        LogSuccess(
            diagnosticSource,
            materialized.Scene,
            false,
            value.WindowsRead,
            revisionElapsed,
            value.InputElapsed,
            value.CompletionElapsed,
            totalStopwatch.Elapsed);
        return materialized;
    }

    private async Task<SituationSceneBuildProduct> BuildFromStoredWindowsCoreAsync(
        string windowDirectory,
        string sidecarDirectory,
        DemoMetadata metadata,
        string demoRef,
        int windowIndex,
        int requestedTick,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var stopwatch = Stopwatch.StartNew();
        var storedInput = await windowAdapter.BuildFromStoredWindowsWithDiagnosticsAsync(
            windowDirectory,
            sidecarDirectory,
            metadata,
            demoRef,
            windowIndex,
            requestedTick,
            cancellationToken);
        var inputElapsed = stopwatch.Elapsed;
        cancellationToken.ThrowIfCancellationRequested();
        var result = Complete(storedInput.Input, cancellationToken);
        var completionElapsed = stopwatch.Elapsed - inputElapsed;
        return new(
            new SituationSceneCacheValue(
                result.CanonicalJson,
                result.Sha256,
                Encoding.UTF8.GetByteCount(result.CanonicalJson)),
            storedInput.WindowsRead,
            inputElapsed,
            completionElapsed);
    }

    private static async Task<string> ComputeSourceRevisionAsync(
        string windowDirectory,
        string sidecarDirectory,
        DemoMetadata metadata,
        int windowIndex,
        string sourceMarker,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var descriptor = new StringBuilder()
            .Append(sourceMarker).Append('\n')
            .Append(Path.GetFullPath(windowDirectory)).Append('\n')
            .Append(Path.GetFullPath(sidecarDirectory)).Append('\n')
            .Append(metadata.MapName).Append('|')
            .Append(metadata.TickRate).Append('|')
            .Append(metadata.TotalTicks).Append('|')
            .Append(metadata.DurationSeconds.ToString("R", System.Globalization.CultureInfo.InvariantCulture))
            .Append('\n');
        for (var index = Math.Max(0, windowIndex - 1); index <= windowIndex; index++)
        {
            await AppendFileRevisionAsync(descriptor, Path.Combine(
                Path.GetFullPath(windowDirectory), $"window-{index:D4}.json.br"), cancellationToken);
            await AppendFileRevisionAsync(descriptor, Path.Combine(
                Path.GetFullPath(sidecarDirectory), $"situation-window-{index:D4}.json.br"), cancellationToken);
        }
        cancellationToken.ThrowIfCancellationRequested();
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(descriptor.ToString())));
    }

    private static async Task AppendFileRevisionAsync(
        StringBuilder descriptor,
        string path,
        CancellationToken cancellationToken)
    {
        var file = new FileInfo(path);
        descriptor.Append(Path.GetFileName(path)).Append('|');
        if (file.Exists)
        {
            await using var stream = new FileStream(
                file.FullName,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                128 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            descriptor
                .Append(file.Length)
                .Append('|')
                .Append(Convert.ToHexStringLower(
                    await SHA256.HashDataAsync(stream, cancellationToken)));
        }
        else
            descriptor.Append("missing");
        descriptor.Append('\n');
    }

    private SituationSceneBuildResult Complete(
        SceneInputPrefix input,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var scene = sceneBuilder.Build(input);
        cancellationToken.ThrowIfCancellationRequested();
        SituationContractValidator.Validate(scene);
        cancellationToken.ThrowIfCancellationRequested();
        var canonicalJson = SituationCanonicalJson.Serialize(scene);
        cancellationToken.ThrowIfCancellationRequested();
        var sha256 = Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(canonicalJson)));
        cancellationToken.ThrowIfCancellationRequested();
        return new(scene, canonicalJson, sha256);
    }

    private static void ValidateRequest(
        DemoMetadata metadata,
        int windowIndex,
        int requestedTick,
        int? windowCount = null)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        if (windowIndex < 0)
            throw new ArgumentOutOfRangeException(nameof(windowIndex));
        if (requestedTick < 0)
            throw new ArgumentOutOfRangeException(nameof(requestedTick));
        if (metadata.TickRate <= 0)
            throw new InvalidDataException("Demo tick rate must be positive.");
        if (!double.IsFinite(metadata.DurationSeconds) || metadata.DurationSeconds < 0 || metadata.TotalTicks < 0)
            throw new InvalidDataException("Demo metadata has invalid duration or tick bounds.");
        if (!string.Equals(metadata.MapName, "de_mirage", StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException("Situation scene v1 only supports de_mirage.");
        if (windowCount is <= 0)
            throw new InvalidDataException("Imported demo has an invalid window count.");
        if (windowCount is { } count && windowIndex >= count)
            throw new ArgumentOutOfRangeException(nameof(windowIndex), "Window index is outside the imported demo.");

        var ticksPerWindow = checked(DemoImportService.WindowSeconds * metadata.TickRate);
        var coreStartTick = checked(windowIndex * ticksPerWindow);
        var coreEndTick = checked((windowIndex + 1) * ticksPerWindow);
        if (requestedTick < coreStartTick || requestedTick >= coreEndTick)
            throw new ArgumentOutOfRangeException(
                nameof(requestedTick),
                "Requested tick must belong to the selected core window.");
    }

    private void LogSuccess(
        string source,
        MinimapSceneV1 scene,
        bool cacheHit,
        int windowsRead,
        TimeSpan revisionElapsed,
        TimeSpan inputElapsed,
        TimeSpan completionElapsed,
        TimeSpan totalElapsed)
    {
        logger?.LogInformation(
            "Situation scene build succeeded: source {Source}, window {WindowIndex}, requested tick {RequestedTick}, " +
            "actual tick {Tick}, cache hit {CacheHit}, windows read {WindowsRead}, source revision {RevisionElapsedMs:F2} ms, " +
            "input {InputElapsedMs:F2} ms, build/validate/serialize {CompletionElapsedMs:F2} ms, total {TotalElapsedMs:F2} ms",
            source,
            scene.WindowIndex,
            scene.RequestedTick,
            scene.Tick,
            cacheHit,
            windowsRead,
            revisionElapsed.TotalMilliseconds,
            inputElapsed.TotalMilliseconds,
            completionElapsed.TotalMilliseconds,
            totalElapsed.TotalMilliseconds);
    }

    private void LogFailure(
        string source,
        int windowIndex,
        int requestedTick,
        SituationSceneException exception,
        TimeSpan elapsed)
    {
        logger?.LogWarning(
            "Situation scene build failed: source {Source}, window {WindowIndex}, requested tick {RequestedTick}, " +
            "error code {ErrorCode}, elapsed {ElapsedMs:F2} ms",
            source,
            windowIndex,
            requestedTick,
            exception.Code,
            elapsed.TotalMilliseconds);
    }

    private void LogCancellation(
        string source,
        int windowIndex,
        int requestedTick,
        TimeSpan elapsed)
    {
        logger?.LogDebug(
            "Situation scene build canceled: source {Source}, window {WindowIndex}, requested tick {RequestedTick}, " +
            "elapsed {ElapsedMs:F2} ms",
            source,
            windowIndex,
            requestedTick,
            elapsed.TotalMilliseconds);
    }
}

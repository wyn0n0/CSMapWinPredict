using CsDemoMap.Api.Models;
using Microsoft.Extensions.Logging;

namespace CsDemoMap.Api.Tests;

internal static class SituationSceneDiagnosticsVerifier
{
    public static async Task VerifyAsync(CancellationToken cancellationToken)
    {
        var checks = 0;
        CheckMapping(
            new KeyNotFoundException("private import id"),
            SituationSceneErrorContext.ImportedDemo,
            SituationSceneErrorCode.ImportNotFound,
            ref checks);
        CheckMapping(
            new InvalidOperationException("Imported demo is not ready for situation scene construction."),
            SituationSceneErrorContext.ImportedDemo,
            SituationSceneErrorCode.ImportNotReady,
            ref checks);
        CheckMapping(
            new NotSupportedException("private map"),
            SituationSceneErrorContext.ImportedDemo,
            SituationSceneErrorCode.UnsupportedMap,
            ref checks);
        CheckMapping(
            new ArgumentOutOfRangeException("windowIndex", "private window"),
            SituationSceneErrorContext.ImportedDemo,
            SituationSceneErrorCode.InvalidWindow,
            ref checks);
        CheckMapping(
            new ArgumentOutOfRangeException("requestedTick", "private tick"),
            SituationSceneErrorContext.ImportedDemo,
            SituationSceneErrorCode.InvalidTick,
            ref checks);
        CheckMapping(
            new InvalidDataException("No frame exists at or before the requested tick in the core window."),
            SituationSceneErrorContext.StoredWindows,
            SituationSceneErrorCode.FrameUnavailable,
            ref checks);
        CheckMapping(
            new InvalidDataException("Situation sidecar is missing for window 1 at E:\\private."),
            SituationSceneErrorContext.StoredWindows,
            SituationSceneErrorCode.SidecarMissing,
            ref checks);
        CheckMapping(
            new InvalidDataException("Unsupported situation sidecar version 'private'."),
            SituationSceneErrorContext.StoredWindows,
            SituationSceneErrorCode.VersionMismatch,
            ref checks);
        CheckMapping(
            new InvalidDataException("private corrupt payload at E:\\private"),
            SituationSceneErrorContext.StoredWindows,
            SituationSceneErrorCode.CorruptData,
            ref checks);
        CheckMapping(
            new InvalidDataException("Conflicting duplicate frame 'private-player'."),
            SituationSceneErrorContext.StoredWindows,
            SituationSceneErrorCode.DuplicateConflict,
            ref checks);
        CheckMapping(
            new InvalidDataException("Situation scene source changed while it was being read at E:\\private."),
            SituationSceneErrorContext.StoredWindows,
            SituationSceneErrorCode.SourceMismatch,
            ref checks);
        CheckMapping(
            new SituationServiceBusyException(),
            SituationSceneErrorContext.ImportedDemo,
            SituationSceneErrorCode.ServiceBusy,
            ref checks);

        var cancellation = new OperationCanceledException("private cancellation");
        var cancellationPreserved = false;
        try
        {
            _ = SituationSceneErrorMapper.Map(cancellation, SituationSceneErrorContext.ImportedDemo);
        }
        catch (OperationCanceledException exception) when (ReferenceEquals(exception, cancellation))
        {
            cancellationPreserved = true;
        }
        Check(cancellationPreserved, "the mapper preserves cancellation", ref checks);

        var timeline = SituationFlowVerifier.BuildBoundaryTimeline(
            reversePlayers: false,
            reverseRelations: false);
        var root = Path.Combine(
            Path.GetTempPath(),
            $"situation-scene-diagnostics-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var previous = SituationFlowVerifier.BuildWindow(timeline, 0, 0, 2048);
            var current = SituationFlowVerifier.BuildWindow(timeline, 1, 1792, 2560);
            await SituationFlowVerifier.WriteWindowAsync(root, previous);
            await SituationFlowVerifier.WriteWindowAsync(root, current);
            await SituationWindowSidecarStore.WriteAsync(
                root,
                SituationWindowSidecarStore.Build(timeline, 0, 0, 2048),
                cancellationToken);
            await SituationWindowSidecarStore.WriteAsync(
                root,
                SituationWindowSidecarStore.Build(timeline, 1, 1792, 2560),
                cancellationToken);

            var logger = new CapturingLogger<SituationSceneService>();
            using var service = new SituationSceneService(
                id => id == "private-demo-id"
                    ? new("private-internal-reference", "completed", root, timeline.Metadata, 2)
                    : null,
                logger);
            _ = await service.BuildFromImportedDemoAsync(
                "private-demo-id", 1, 1940, cancellationToken);
            _ = await service.BuildFromImportedDemoAsync(
                "private-demo-id", 1, 1940, cancellationToken);

            var successes = logger.Entries
                .Where(entry => entry.Level == LogLevel.Information && entry.Has("CacheHit"))
                .ToArray();
            Check(successes.Length == 2, "success diagnostics are emitted for misses and hits", ref checks);
            Check(
                successes.Any(entry =>
                    entry.Value<bool>("CacheHit") == false &&
                    entry.Value<int>("WindowsRead") == 2 &&
                    HasNonNegativeTimings(entry)),
                "cache misses report windows and phase timings",
                ref checks);
            Check(
                successes.Any(entry =>
                    entry.Value<bool>("CacheHit") &&
                    entry.Value<int>("WindowsRead") == 0 &&
                    HasNonNegativeTimings(entry)),
                "cache hits report their cache state and total timing",
                ref checks);

            var missing = await CaptureSituationErrorAsync(() =>
                service.BuildFromImportedDemoAsync("private-missing-id", 1, 1940, cancellationToken));
            Check(missing.Code == SituationSceneErrorCode.ImportNotFound,
                "runtime errors expose stable categories", ref checks);
            var failure = logger.Entries.Last(entry => entry.Has("ErrorCode"));
            Check(
                failure.Level == LogLevel.Warning &&
                failure.Value<SituationSceneErrorCode>("ErrorCode") == SituationSceneErrorCode.ImportNotFound &&
                failure.Value<double>("ElapsedMs") >= 0,
                "failure diagnostics contain only the category and elapsed time",
                ref checks);

            using var canceled = new CancellationTokenSource();
            canceled.Cancel();
            await ExpectCanceledAsync(() =>
                service.BuildFromImportedDemoAsync("private-demo-id", 1, 1940, canceled.Token));
            Check(
                logger.Entries.Any(entry =>
                    entry.Level == LogLevel.Debug &&
                    entry.Formatted.Contains("build canceled", StringComparison.Ordinal)),
                "cancellation remains cancellation and is diagnosed separately",
                ref checks);

            var allLogText = string.Join(
                "\n",
                logger.Entries.SelectMany(entry =>
                    entry.Properties.Select(property => $"{property.Key}={property.Value}"))
                    .Concat(logger.Entries.Select(entry => entry.Formatted)));
            Check(
                !ContainsAny(
                    allLogText,
                    root,
                    "private-demo-id",
                    "private-missing-id",
                    "private-internal-reference",
                    "Private T",
                    "Private CT",
                    "minimap-scene-v1"),
                "diagnostics exclude paths, identities, demo references and scene payloads",
                ref checks);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }

        Console.WriteLine($"Situation scene diagnostics checks passed: {checks}");
    }

    private static void CheckMapping(
        Exception source,
        SituationSceneErrorContext context,
        SituationSceneErrorCode expected,
        ref int checks)
    {
        var mapped = SituationSceneErrorMapper.Map(source, context);
        Check(mapped.Code == expected, $"error mapping {expected}", ref checks);
        Check(
            !ContainsAny(mapped.Message, "E:\\private", "private-player", "private import id", "private map", "private tick", "private window"),
            $"safe message {expected}",
            ref checks);
    }

    private static bool HasNonNegativeTimings(LogEntry entry) =>
        entry.Value<double>("RevisionElapsedMs") >= 0 &&
        entry.Value<double>("InputElapsedMs") >= 0 &&
        entry.Value<double>("CompletionElapsedMs") >= 0 &&
        entry.Value<double>("TotalElapsedMs") >= 0;

    private static async Task<SituationSceneException> CaptureSituationErrorAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (SituationSceneException exception)
        {
            return exception;
        }
        throw new InvalidOperationException("Expected a situation scene exception.");
    }

    private static async Task ExpectCanceledAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (OperationCanceledException)
        {
            return;
        }
        throw new InvalidOperationException("Expected cancellation.");
    }

    private static bool ContainsAny(string value, params string[] expected) =>
        expected.Any(item => value.Contains(item, StringComparison.OrdinalIgnoreCase));

    private static void Check(bool condition, string label, ref int checks)
    {
        if (!condition)
            throw new InvalidOperationException($"Situation scene diagnostics check failed: {label}.");
        checks++;
    }

    private sealed record LogEntry(
        LogLevel Level,
        string Formatted,
        IReadOnlyDictionary<string, object?> Properties)
    {
        public bool Has(string name) => Properties.ContainsKey(name);

        public T Value<T>(string name) => Properties.TryGetValue(name, out var value) && value is T typed
            ? typed
            : throw new InvalidOperationException($"Log property '{name}' is missing or has the wrong type.");
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        private readonly List<LogEntry> entries = [];

        public IReadOnlyList<LogEntry> Entries => entries;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var properties = state is IEnumerable<KeyValuePair<string, object?>> values
                ? values.ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal)
                : new Dictionary<string, object?>(StringComparer.Ordinal);
            entries.Add(new(logLevel, formatter(state, exception), properties));
        }
    }
}

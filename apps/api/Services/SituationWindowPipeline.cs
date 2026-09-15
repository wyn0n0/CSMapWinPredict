using System.IO.Compression;
using System.Text.Json;
using CsDemoMap.Api.Models;

namespace CsDemoMap.Api.Services;

internal static class SituationWindowSidecarStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    public static SituationWindowSidecarV1 Build(
        DemoTimeline timeline,
        int windowIndex,
        int dataFromTick,
        int dataToTick)
    {
        ArgumentNullException.ThrowIfNull(timeline);
        if (timeline.Semantics is null)
            throw new InvalidDataException("Semantic timeline is required to create a situation sidecar.");

        var boundaries = timeline.Semantics.Attempts
            .Where(attempt => attempt.StartTick <= dataToTick)
            .Where(attempt => attempt.EndTick is null || attempt.EndTick >= dataFromTick)
            .Select(attempt => new SituationRoundBoundaryV1(
                attempt.RoundId,
                attempt.SegmentId,
                attempt.RoundNumber,
                attempt.StartTick,
                attempt.LiveTick is { } liveTick && liveTick <= dataToTick ? liveTick : null,
                attempt.EndTick is { } endTick && endTick <= dataToTick ? endTick : null))
            .OrderBy(item => item.StartTick)
            .ThenBy(item => item.RoundRef, StringComparer.Ordinal)
            .ToArray();

        var framesInRange = timeline.Semantics.Frames
            .Where(frame => frame.Tick >= dataFromTick && frame.Tick <= dataToTick)
            .ToList();
        var previous = timeline.Semantics.Frames
            .Where(frame => frame.Tick < dataFromTick)
            .OrderBy(frame => frame.Tick)
            .LastOrDefault();
        if (previous is not null)
            framesInRange.Add(previous);

        return new(
            SituationWindowSidecarVersions.V1,
            windowIndex,
            timeline.Metadata.MapName.ToLowerInvariant(),
            timeline.Metadata.TickRate,
            dataFromTick,
            dataToTick,
            boundaries,
            StrictDistinct(framesInRange, frame => frame.Tick, "semantic frame"));
    }

    public static async Task WriteAsync(
        string directory,
        SituationWindowSidecarV1 sidecar,
        CancellationToken cancellationToken)
    {
        var path = GetPath(directory, sidecar.WindowIndex);
        await using var file = new FileStream(
            path, FileMode.Create, FileAccess.Write, FileShare.None, 128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var brotli = new BrotliStream(file, CompressionLevel.Optimal, leaveOpen: false);
        await JsonSerializer.SerializeAsync(brotli, sidecar, JsonOptions, cancellationToken);
    }

    public static async Task<SituationWindowSidecarV1> ReadAsync(
        string directory,
        int windowIndex,
        CancellationToken cancellationToken)
    {
        var path = GetPath(directory, windowIndex);
        if (!File.Exists(path))
            throw new InvalidDataException(
                $"Situation sidecar is missing for window {windowIndex}. Reparse the source demo explicitly to rebuild it.");
        await using var file = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var brotli = new BrotliStream(file, CompressionMode.Decompress, leaveOpen: false);
        var sidecar = await JsonSerializer.DeserializeAsync<SituationWindowSidecarV1>(
            brotli, JsonOptions, cancellationToken)
            ?? throw new InvalidDataException($"Situation sidecar for window {windowIndex} is empty.");
        Validate(sidecar, windowIndex);
        return sidecar;
    }

    public static string GetPath(string directory, int windowIndex) =>
        Path.Combine(Path.GetFullPath(directory), $"situation-window-{windowIndex:D4}.json.br");

    private static void Validate(SituationWindowSidecarV1 sidecar, int expectedWindowIndex)
    {
        if (sidecar.SchemaVersion != SituationWindowSidecarVersions.V1)
            throw new InvalidDataException($"Unsupported situation sidecar version '{sidecar.SchemaVersion}'.");
        if (sidecar.WindowIndex != expectedWindowIndex)
            throw new InvalidDataException("Situation sidecar window index does not match its file name.");
        if (sidecar.TickRate <= 0 || sidecar.DataFromTick < 0 || sidecar.DataToTick < sidecar.DataFromTick)
            throw new InvalidDataException("Situation sidecar has invalid tick bounds.");
        _ = StrictDistinct(sidecar.SemanticFrames, frame => frame.Tick, "semantic frame");
        _ = StrictDistinct(sidecar.RoundBoundaries, item => (item.RoundRef, item.StartTick), "round boundary");
    }

    private static IReadOnlyList<T> StrictDistinct<T, TKey>(
        IEnumerable<T> source,
        Func<T, TKey> keySelector,
        string label) where TKey : notnull => source
        .GroupBy(keySelector)
        .Select(group =>
        {
            var values = group.ToArray();
            var firstJson = JsonSerializer.Serialize(values[0], JsonOptions);
            if (values.Skip(1).Any(value => JsonSerializer.Serialize(value, JsonOptions) != firstJson))
                throw new InvalidDataException($"Conflicting duplicate {label} '{group.Key}'.");
            return values[0];
        })
        .OrderBy(keySelector)
        .ToArray();
}

internal sealed class SituationWindowInputAdapter
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };
    private readonly SituationInputAdapter timelineAdapter = new();

    public async Task<SceneInputPrefix> BuildFromStoredWindowsAsync(
        string directory,
        DemoMetadata metadata,
        string demoRef,
        int windowIndex,
        int requestedTick,
        CancellationToken cancellationToken = default)
        => (await BuildFromStoredWindowsWithDiagnosticsAsync(
            directory,
            directory,
            metadata,
            demoRef,
            windowIndex,
            requestedTick,
            cancellationToken)).Input;

    public async Task<SceneInputPrefix> BuildFromStoredWindowsAsync(
        string windowDirectory,
        string sidecarDirectory,
        DemoMetadata metadata,
        string demoRef,
        int windowIndex,
        int requestedTick,
        CancellationToken cancellationToken = default)
        => (await BuildFromStoredWindowsWithDiagnosticsAsync(
            windowDirectory,
            sidecarDirectory,
            metadata,
            demoRef,
            windowIndex,
            requestedTick,
            cancellationToken)).Input;

    internal async Task<SituationStoredWindowInput> BuildFromStoredWindowsWithDiagnosticsAsync(
        string windowDirectory,
        string sidecarDirectory,
        DemoMetadata metadata,
        string demoRef,
        int windowIndex,
        int requestedTick,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        var current = await ReadWindowAsync(windowDirectory, windowIndex, cancellationToken);
        var currentSidecar = await SituationWindowSidecarStore.ReadAsync(
            sidecarDirectory, windowIndex, cancellationToken);
        ValidatePair(current, currentSidecar, metadata, windowIndex);

        var coreStartTick = checked(windowIndex * DemoImportService.WindowSeconds * metadata.TickRate);
        var coreEndTick = checked((windowIndex + 1) * DemoImportService.WindowSeconds * metadata.TickRate);
        var target = current.Frames
            .Where(frame => frame.Tick >= coreStartTick && frame.Tick < coreEndTick && frame.Tick <= requestedTick)
            .OrderBy(frame => frame.Tick)
            .LastOrDefault()
            ?? throw new InvalidDataException("No frame exists at or before the requested tick in the core window.");
        var requiredHistoryStart = target.Tick - (int)Math.Floor(
            SituationInputAdapter.HistorySeconds * metadata.TickRate);

        var windows = new List<DemoWindow> { current };
        var sidecars = new List<SituationWindowSidecarV1> { currentSidecar };
        if (windowIndex > 0 && requiredHistoryStart < currentSidecar.DataFromTick)
        {
            var previousIndex = windowIndex - 1;
            var previous = await ReadWindowAsync(windowDirectory, previousIndex, cancellationToken);
            var previousSidecar = await SituationWindowSidecarStore.ReadAsync(
                sidecarDirectory, previousIndex, cancellationToken);
            ValidatePair(previous, previousSidecar, metadata, previousIndex);
            windows.Insert(0, previous);
            sidecars.Insert(0, previousSidecar);
        }

        return new(
            timelineAdapter.BuildFromTimeline(
                Merge(metadata, windows, sidecars, cancellationToken),
                demoRef,
                windowIndex,
                requestedTick,
                cancellationToken: cancellationToken),
            windows.Count);
    }

    internal SceneInputPrefix BuildFromWindows(
        DemoMetadata metadata,
        string demoRef,
        int windowIndex,
        int requestedTick,
        IReadOnlyList<DemoWindow> windows,
        IReadOnlyList<SituationWindowSidecarV1> sidecars,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (windows.Count == 0 || windows.Count != sidecars.Count)
            throw new ArgumentException("Each replay window must have exactly one situation sidecar.");
        for (var index = 0; index < windows.Count; index++)
            ValidatePair(windows[index], sidecars[index], metadata, windows[index].Index);
        return timelineAdapter.BuildFromTimeline(
            Merge(metadata, windows, sidecars, cancellationToken),
            demoRef,
            windowIndex,
            requestedTick,
            cancellationToken: cancellationToken);
    }

    private static DemoTimeline Merge(
        DemoMetadata metadata,
        IReadOnlyList<DemoWindow> windows,
        IReadOnlyList<SituationWindowSidecarV1> sidecars,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var frames = StrictDistinct(windows.SelectMany(window => window.Frames), frame => frame.Tick, "frame");
        cancellationToken.ThrowIfCancellationRequested();
        var utilityStates = StrictDistinct(
            windows.SelectMany(window => window.PlayerUtilityStates),
            state => (state.PlayerId, state.Tick), "utility state");
        cancellationToken.ThrowIfCancellationRequested();
        var equipmentStates = StrictDistinct(
            windows.SelectMany(window => window.PlayerEquipmentStates),
            state => (state.PlayerId, state.Tick), "equipment state");
        cancellationToken.ThrowIfCancellationRequested();
        var semanticFrames = StrictDistinct(
            sidecars.SelectMany(sidecar => sidecar.SemanticFrames), frame => frame.Tick, "semantic frame");
        cancellationToken.ThrowIfCancellationRequested();
        var boundaries = MergeRoundBoundaries(
            sidecars.SelectMany(sidecar => sidecar.RoundBoundaries));
        cancellationToken.ThrowIfCancellationRequested();

        var timeline = new DemoTimeline(
            metadata with { FileName = "situation-window-input" },
            frames,
            MergeUtilityTracks(windows.SelectMany(window => window.UtilityTracks)),
            MergeEffectTracks(windows.SelectMany(window => window.UtilityEffects)),
            utilityStates,
            equipmentStates,
            [],
            [])
        {
            Semantics = new(
                boundaries.Select(item => new RoundAttempt
                {
                    RoundId = item.RoundRef,
                    SegmentId = item.SegmentId,
                    RoundNumber = item.RoundNumber,
                    StartTick = item.StartTick,
                    LiveTick = item.LiveTick,
                    EndTick = item.ObservedEndTick
                }).ToArray(),
                semanticFrames,
                [])
        };
        cancellationToken.ThrowIfCancellationRequested();
        return timeline;
    }

    private static IReadOnlyList<UtilityTrack> MergeUtilityTracks(IEnumerable<UtilityTrack> source) => source
        .GroupBy(track => track.Id, StringComparer.Ordinal)
        .Select(group =>
        {
            var items = group.ToArray();
            var first = items[0];
            if (items.Skip(1).Any(item => item with { Trajectory = first.Trajectory } != first))
                throw new InvalidDataException($"Conflicting duplicate utility track '{group.Key}'.");
            return first with
            {
                Trajectory = StrictDistinct(items.SelectMany(item => item.Trajectory), point => point.Tick,
                    $"utility point for {group.Key}")
            };
        })
        .OrderBy(track => track.Id, StringComparer.Ordinal)
        .ToArray();

    private static IReadOnlyList<SituationRoundBoundaryV1> MergeRoundBoundaries(
        IEnumerable<SituationRoundBoundaryV1> source) => source
        .GroupBy(item => (item.RoundRef, item.StartTick))
        .Select(group =>
        {
            var items = group.ToArray();
            var first = items[0];
            if (items.Any(item => item.SegmentId != first.SegmentId))
                throw new InvalidDataException($"Conflicting segment for round boundary '{group.Key}'.");
            return first with
            {
                RoundNumber = MergeOptional(items.Select(item => item.RoundNumber), "round number", group.Key),
                LiveTick = MergeOptional(items.Select(item => item.LiveTick), "live tick", group.Key),
                ObservedEndTick = MergeOptional(
                    items.Select(item => item.ObservedEndTick), "observed end tick", group.Key)
            };
        })
        .OrderBy(item => item.StartTick)
        .ThenBy(item => item.RoundRef, StringComparer.Ordinal)
        .ToArray();

    private static int? MergeOptional(
        IEnumerable<int?> source,
        string label,
        object key)
    {
        var observed = source.Where(value => value is not null).Select(value => value!.Value).Distinct().ToArray();
        if (observed.Length > 1)
            throw new InvalidDataException($"Conflicting {label} for round boundary '{key}'.");
        return observed.Length == 0 ? null : observed[0];
    }

    private static IReadOnlyList<UtilityEffectTrack> MergeEffectTracks(
        IEnumerable<UtilityEffectTrack> source) => source
        .GroupBy(effect => effect.Id, StringComparer.Ordinal)
        .Select(group =>
        {
            var items = group.ToArray();
            var first = items[0];
            if (items.Skip(1).Any(item => item with { Samples = first.Samples } != first))
                throw new InvalidDataException($"Conflicting duplicate utility effect '{group.Key}'.");
            return first with
            {
                Samples = StrictDistinct(items.SelectMany(item => item.Samples), sample => sample.Tick,
                    $"effect sample for {group.Key}")
            };
        })
        .OrderBy(effect => effect.Id, StringComparer.Ordinal)
        .ToArray();

    private static IReadOnlyList<T> StrictDistinct<T, TKey>(
        IEnumerable<T> source,
        Func<T, TKey> keySelector,
        string label) where TKey : notnull => source
        .GroupBy(keySelector)
        .Select(group =>
        {
            var values = group.ToArray();
            var firstJson = JsonSerializer.Serialize(values[0], JsonOptions);
            if (values.Skip(1).Any(value => JsonSerializer.Serialize(value, JsonOptions) != firstJson))
                throw new InvalidDataException($"Conflicting duplicate {label} '{group.Key}'.");
            return values[0];
        })
        .OrderBy(keySelector)
        .ToArray();

    private static void ValidatePair(
        DemoWindow window,
        SituationWindowSidecarV1 sidecar,
        DemoMetadata metadata,
        int expectedIndex)
    {
        if (window.Index != expectedIndex || sidecar.WindowIndex != expectedIndex)
            throw new InvalidDataException("Window and situation sidecar indexes do not match.");
        if (sidecar.SchemaVersion != SituationWindowSidecarVersions.V1)
            throw new InvalidDataException($"Unsupported situation sidecar version '{sidecar.SchemaVersion}'.");
        if (sidecar.TickRate != metadata.TickRate ||
            !string.Equals(sidecar.Map, metadata.MapName, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Window sidecar map or tick rate does not match demo metadata.");

        var expectedCoreFrom = expectedIndex * (double)DemoImportService.WindowSeconds;
        var expectedCoreTo = Math.Min(
            metadata.DurationSeconds,
            (expectedIndex + 1) * (double)DemoImportService.WindowSeconds);
        if (!double.IsFinite(window.CoreFromSeconds) || !double.IsFinite(window.CoreToSeconds) ||
            !double.IsFinite(window.DataFromSeconds) || !double.IsFinite(window.DataToSeconds) ||
            Math.Abs(window.CoreFromSeconds - expectedCoreFrom) > 1e-6 ||
            Math.Abs(window.CoreToSeconds - expectedCoreTo) > 1e-6 ||
            window.DataFromSeconds > window.CoreFromSeconds ||
            window.DataToSeconds < window.CoreToSeconds ||
            window.DataFromSeconds < 0 || window.DataToSeconds < window.DataFromSeconds)
            throw new InvalidDataException("Replay window has invalid core or data bounds.");

        var expectedDataFromTick = checked((int)Math.Floor(window.DataFromSeconds * metadata.TickRate));
        var expectedDataToTick = checked((int)Math.Ceiling(window.DataToSeconds * metadata.TickRate));
        if (sidecar.DataFromTick != expectedDataFromTick || sidecar.DataToTick != expectedDataToTick)
            throw new InvalidDataException("Window sidecar tick bounds do not match the replay window.");
    }

    private static async Task<DemoWindow> ReadWindowAsync(
        string directory,
        int windowIndex,
        CancellationToken cancellationToken)
    {
        var path = Path.Combine(Path.GetFullPath(directory), $"window-{windowIndex:D4}.json.br");
        if (!File.Exists(path))
            throw new InvalidDataException($"Replay window {windowIndex} is missing.");
        await using var file = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var brotli = new BrotliStream(file, CompressionMode.Decompress, leaveOpen: false);
        return await JsonSerializer.DeserializeAsync<DemoWindow>(brotli, JsonOptions, cancellationToken)
            ?? throw new InvalidDataException($"Replay window {windowIndex} is empty.");
    }
}

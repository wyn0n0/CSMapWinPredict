using System.Collections.Concurrent;
using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;
using CsDemoMap.Api.Models;

namespace CsDemoMap.Api.Services;

internal static class SituationSidecarRebuildVersions
{
    public const string V1 = "situation-sidecar-rebuild-v1";
}

internal sealed record SituationSidecarRebuildEntry(
    int WindowIndex,
    string TargetWindowFile,
    string TargetWindowSha256,
    string SidecarFile,
    string SidecarSha256,
    double CoreFromSeconds,
    double CoreToSeconds,
    double DataFromSeconds,
    double DataToSeconds,
    int DataFromTick,
    int DataToTick);

internal sealed record SituationSidecarRebuildManifest(
    string SchemaVersion,
    string Status,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    bool LocalOnly,
    string TargetImportDirectory,
    string SourceDemoPath,
    string SourceDemoSha256,
    string? Map,
    int? TickRate,
    double? DurationSeconds,
    int? FrameCount,
    string OutputDirectory,
    int DemoWindowSchemaVersion,
    string SidecarSchemaVersion,
    int WindowSeconds,
    int WindowCount,
    IReadOnlyList<SituationSidecarRebuildEntry> Windows);

internal sealed record SituationSidecarRebuildValidation(
    SituationSidecarRebuildManifest Manifest,
    string Revision,
    IReadOnlyList<SituationManagedFileStamp> FileStamps);

internal sealed record SituationManagedFileStamp(
    string Path,
    long Length,
    long LastWriteTimeUtcTicks)
{
    public static SituationManagedFileStamp Capture(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists)
            throw new InvalidDataException("A registered situation sidecar file is missing.");
        return new(info.FullName, info.Length, info.LastWriteTimeUtc.Ticks);
    }

    public void EnsureUnchanged()
    {
        var current = Capture(Path);
        if (current.Length != Length || current.LastWriteTimeUtcTicks != LastWriteTimeUtcTicks)
            throw new InvalidDataException("A registered situation sidecar file changed after validation.");
    }
}

internal static partial class SituationSidecarRebuilder
{
    private const string ManifestFileName = "manifest.json";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    [GeneratedRegex("^window-(?<index>[0-9]{4,})\\.json\\.br$", RegexOptions.CultureInvariant)]
    private static partial Regex WindowFilePattern();

    [GeneratedRegex("^situation-window-(?<index>[0-9]{4,})\\.json\\.br$", RegexOptions.CultureInvariant)]
    private static partial Regex SidecarFilePattern();

    public static async Task RebuildAsync(
        string targetImportDirectory,
        string sourceDemoPath,
        string outputDirectory,
        CancellationToken cancellationToken)
    {
        var prepared = await PrepareAsync(
            targetImportDirectory, sourceDemoPath, outputDirectory, cancellationToken);
        try
        {
            await using var source = new FileStream(
                prepared.SourceDemoPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                1024 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var timeline = await new DemoParserService().ParseAsync(
                source,
                Path.GetFileName(prepared.SourceDemoPath),
                cancellationToken,
                collectSemantics: true);
            await PopulateAsync(prepared, timeline, cancellationToken);
        }
        catch
        {
            Console.Error.WriteLine($"Situation sidecar rebuild is incomplete: {prepared.OutputDirectory}");
            throw;
        }
    }

    internal static async Task<SituationSidecarRebuildManifest> RebuildFromTimelineAsync(
        string targetImportDirectory,
        string sourceDemoPath,
        DemoTimeline timeline,
        string outputDirectory,
        CancellationToken cancellationToken,
        Func<int, CancellationToken, ValueTask>? afterWindowWritten = null)
    {
        ArgumentNullException.ThrowIfNull(timeline);
        var prepared = await PrepareAsync(
            targetImportDirectory, sourceDemoPath, outputDirectory, cancellationToken);
        try
        {
            return await PopulateAsync(
                prepared, timeline, cancellationToken, afterWindowWritten);
        }
        catch
        {
            Console.Error.WriteLine($"Situation sidecar rebuild is incomplete: {prepared.OutputDirectory}");
            throw;
        }
    }

    public static async Task<SituationSidecarRebuildValidation> ValidateCompleteAsync(
        string targetImportDirectory,
        string outputDirectory,
        CancellationToken cancellationToken)
    {
        var target = RequireDirectory(targetImportDirectory, nameof(targetImportDirectory));
        var output = RequireDirectory(outputDirectory, nameof(outputDirectory));
        var manifestPath = Path.Combine(output, ManifestFileName);
        var manifest = await ReadManifestAsync(manifestPath, cancellationToken);
        if (manifest.SchemaVersion != SituationSidecarRebuildVersions.V1 ||
            manifest.Status != "complete" ||
            manifest.CompletedAtUtc is null ||
            !manifest.LocalOnly ||
            manifest.DemoWindowSchemaVersion != DemoImportService.SchemaVersion ||
            manifest.SidecarSchemaVersion != SituationWindowSidecarVersions.V1 ||
            manifest.WindowSeconds != DemoImportService.WindowSeconds)
            throw new InvalidDataException("Situation sidecar rebuild manifest is incomplete or unsupported.");
        if (!PathEquals(manifest.TargetImportDirectory, target) ||
            !PathEquals(manifest.OutputDirectory, output))
            throw new InvalidDataException("Situation sidecar rebuild manifest paths do not match this registration.");
        if (manifest.WindowCount <= 0 || manifest.Windows.Count != manifest.WindowCount ||
            !manifest.Windows.Select(item => item.WindowIndex).SequenceEqual(
                Enumerable.Range(0, manifest.WindowCount)))
            throw new InvalidDataException("Situation sidecar rebuild manifest window indexes are invalid.");
        if (!IsSha256(manifest.SourceDemoSha256))
            throw new InvalidDataException("Situation sidecar rebuild source hash is invalid.");
        if (!string.Equals(manifest.Map, "de_mirage", StringComparison.OrdinalIgnoreCase) ||
            manifest.TickRate is null or <= 0 ||
            manifest.DurationSeconds is not { } duration || !double.IsFinite(duration) || duration < 0 ||
            manifest.FrameCount is null or < 0)
            throw new InvalidDataException("Situation sidecar rebuild source metadata is invalid.");

        var discoveredSidecars = DiscoverIndexedFiles(output, SidecarFilePattern());
        if (discoveredSidecars.Count != manifest.WindowCount)
            throw new InvalidDataException("Situation sidecar rebuild directory contains an unexpected sidecar set.");

        var stamps = new List<SituationManagedFileStamp> { SituationManagedFileStamp.Capture(manifestPath) };
        foreach (var entry in manifest.Windows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var expectedWindowName = $"window-{entry.WindowIndex:D4}.json.br";
            var expectedSidecarName = $"situation-window-{entry.WindowIndex:D4}.json.br";
            if (entry.TargetWindowFile != expectedWindowName || entry.SidecarFile != expectedSidecarName)
                throw new InvalidDataException("Situation sidecar rebuild manifest contains an invalid file name.");
            if (!IsSha256(entry.TargetWindowSha256) || !IsSha256(entry.SidecarSha256) ||
                !double.IsFinite(entry.CoreFromSeconds) || !double.IsFinite(entry.CoreToSeconds) ||
                !double.IsFinite(entry.DataFromSeconds) || !double.IsFinite(entry.DataToSeconds) ||
                entry.CoreFromSeconds < 0 || entry.CoreToSeconds < entry.CoreFromSeconds ||
                entry.DataFromSeconds < 0 || entry.DataFromSeconds > entry.CoreFromSeconds ||
                entry.DataToSeconds < entry.CoreToSeconds ||
                entry.DataFromTick < 0 || entry.DataToTick < entry.DataFromTick)
                throw new InvalidDataException("Situation sidecar rebuild manifest contains invalid bounds or hashes.");

            var windowPath = Path.Combine(target, expectedWindowName);
            var sidecarPath = Path.Combine(output, expectedSidecarName);
            if (!File.Exists(windowPath) || !File.Exists(sidecarPath))
                throw new InvalidDataException("Situation sidecar rebuild references a missing file.");
            if (!string.Equals(await FileSha256Async(windowPath, cancellationToken),
                    entry.TargetWindowSha256, StringComparison.Ordinal) ||
                !string.Equals(await FileSha256Async(sidecarPath, cancellationToken),
                    entry.SidecarSha256, StringComparison.Ordinal))
                throw new InvalidDataException("Situation sidecar rebuild file hash mismatch.");

            var sidecar = await SituationWindowSidecarStore.ReadAsync(
                output, entry.WindowIndex, cancellationToken);
            if (sidecar.DataFromTick != entry.DataFromTick || sidecar.DataToTick != entry.DataToTick ||
                sidecar.TickRate != manifest.TickRate ||
                !string.Equals(sidecar.Map, manifest.Map, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Situation sidecar bounds do not match the rebuild manifest.");
            stamps.Add(SituationManagedFileStamp.Capture(windowPath));
            stamps.Add(SituationManagedFileStamp.Capture(sidecarPath));
        }

        var revision = await FileSha256Async(manifestPath, cancellationToken);
        return new(manifest, revision, stamps);
    }

    private static async Task<SituationSidecarRebuildManifest> PopulateAsync(
        PreparedRebuild prepared,
        DemoTimeline timeline,
        CancellationToken cancellationToken,
        Func<int, CancellationToken, ValueTask>? afterWindowWritten = null)
    {
        if (timeline.Semantics is null)
            throw new InvalidDataException("Semantic timeline is required to rebuild situation sidecars.");
        if (!string.Equals(timeline.Metadata.MapName, "de_mirage", StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException("Situation sidecar rebuild v1 only supports de_mirage.");

        var windowCount = DemoWindowSliceBuilder.GetWindowCount(timeline.Metadata);
        var targetWindows = DiscoverIndexedFiles(prepared.TargetImportDirectory, WindowFilePattern());
        if (targetWindows.Count != windowCount ||
            !targetWindows.Keys.SequenceEqual(Enumerable.Range(0, windowCount)))
            throw new InvalidDataException("Target replay windows do not match the source demo window count.");

        var entries = new List<SituationSidecarRebuildEntry>(windowCount);
        for (var index = 0; index < windowCount; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var targetWindowPath = targetWindows[index];
            var targetWindowSha256 = await FileSha256Async(targetWindowPath, cancellationToken);
            var targetWindow = await ReadWindowAsync(targetWindowPath, cancellationToken);
            var expectedSlice = DemoWindowSliceBuilder.Build(timeline, index, []);
            VerifySourceMatch(targetWindow, expectedSlice.Window);

            var sidecar = SituationWindowSidecarStore.Build(
                timeline,
                index,
                expectedSlice.DataFromTick,
                expectedSlice.DataToTick);
            await SituationWindowSidecarStore.WriteAsync(
                prepared.OutputDirectory, sidecar, cancellationToken);
            _ = await SituationWindowSidecarStore.ReadAsync(
                prepared.OutputDirectory, index, cancellationToken);
            var sidecarFile = $"situation-window-{index:D4}.json.br";
            var sidecarPath = Path.Combine(prepared.OutputDirectory, sidecarFile);
            var sidecarSha256 = await FileSha256Async(sidecarPath, cancellationToken);
            entries.Add(new(
                index,
                Path.GetFileName(targetWindowPath),
                targetWindowSha256,
                sidecarFile,
                sidecarSha256,
                expectedSlice.Window.CoreFromSeconds,
                expectedSlice.Window.CoreToSeconds,
                expectedSlice.Window.DataFromSeconds,
                expectedSlice.Window.DataToSeconds,
                expectedSlice.DataFromTick,
                expectedSlice.DataToTick));
            if (afterWindowWritten is not null)
                await afterWindowWritten(index, cancellationToken);
        }

        await VerifyGeneratedFilesAsync(prepared, entries, cancellationToken);
        var complete = prepared.InitialManifest with
        {
            Status = "complete",
            CompletedAtUtc = DateTimeOffset.UtcNow,
            Map = timeline.Metadata.MapName.ToLowerInvariant(),
            TickRate = timeline.Metadata.TickRate,
            DurationSeconds = timeline.Metadata.DurationSeconds,
            FrameCount = timeline.Frames.Count,
            WindowCount = windowCount,
            Windows = entries
        };
        await WriteManifestAsync(
            Path.Combine(prepared.OutputDirectory, ManifestFileName), complete, cancellationToken);
        try
        {
            _ = await ValidateCompleteAsync(
                prepared.TargetImportDirectory, prepared.OutputDirectory, cancellationToken);
        }
        catch
        {
            var incomplete = complete with { Status = "incomplete", CompletedAtUtc = null };
            await WriteManifestAsync(
                Path.Combine(prepared.OutputDirectory, ManifestFileName),
                incomplete,
                CancellationToken.None);
            throw;
        }
        Console.WriteLine(
            $"Situation sidecars rebuilt: {windowCount} windows to {prepared.OutputDirectory}");
        return complete;
    }

    private static async Task<PreparedRebuild> PrepareAsync(
        string targetImportDirectory,
        string sourceDemoPath,
        string outputDirectory,
        CancellationToken cancellationToken)
    {
        var target = RequireDirectory(targetImportDirectory, nameof(targetImportDirectory));
        var source = Path.GetFullPath(sourceDemoPath);
        if (!File.Exists(source))
            throw new FileNotFoundException("Source demo is required to rebuild situation sidecars.", source);
        if (!string.Equals(Path.GetExtension(source), ".dem", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Situation sidecar source must be a .dem file.", nameof(sourceDemoPath));

        var output = Path.GetFullPath(outputDirectory);
        if (Directory.Exists(output) || File.Exists(output))
            throw new IOException($"Output path already exists: {output}");
        if (IsSameOrDescendant(target, output))
            throw new InvalidDataException("Sidecar rebuild output must be outside the target import directory.");

        cancellationToken.ThrowIfCancellationRequested();
        var sourceSha256 = await FileSha256Async(source, cancellationToken);
        Directory.CreateDirectory(output);
        var initial = new SituationSidecarRebuildManifest(
            SituationSidecarRebuildVersions.V1,
            "incomplete",
            DateTimeOffset.UtcNow,
            null,
            true,
            target,
            source,
            sourceSha256,
            null,
            null,
            null,
            null,
            output,
            DemoImportService.SchemaVersion,
            SituationWindowSidecarVersions.V1,
            DemoImportService.WindowSeconds,
            0,
            []);
        await WriteManifestAsync(Path.Combine(output, ManifestFileName), initial, cancellationToken);
        return new(target, source, output, initial);
    }

    private static void VerifySourceMatch(DemoWindow target, DemoWindow expected)
    {
        var targetProjection = ProjectSource(target);
        var expectedProjection = ProjectSource(expected);
        if (!JsonSerializer.SerializeToUtf8Bytes(targetProjection, JsonOptions)
            .AsSpan().SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(expectedProjection, JsonOptions)))
            throw new InvalidDataException(
                $"Source demo does not match target replay window {target.Index}.");
    }

    private static SituationWindowSourceProjection ProjectSource(DemoWindow window) => new(
        window.Index,
        window.CoreFromSeconds,
        window.CoreToSeconds,
        window.DataFromSeconds,
        window.DataToSeconds,
        window.FirstFrameIndex,
        window.TotalFrameCount,
        window.Frames,
        window.UtilityTracks,
        window.UtilityEffects,
        window.PlayerUtilityStates,
        window.PlayerEquipmentStates);

    private static async Task VerifyGeneratedFilesAsync(
        PreparedRebuild prepared,
        IReadOnlyList<SituationSidecarRebuildEntry> entries,
        CancellationToken cancellationToken)
    {
        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var targetWindowPath = Path.Combine(prepared.TargetImportDirectory, entry.TargetWindowFile);
            var sidecarPath = Path.Combine(prepared.OutputDirectory, entry.SidecarFile);
            if (!string.Equals(await FileSha256Async(targetWindowPath, cancellationToken),
                    entry.TargetWindowSha256, StringComparison.Ordinal) ||
                !string.Equals(await FileSha256Async(sidecarPath, cancellationToken),
                    entry.SidecarSha256, StringComparison.Ordinal))
                throw new InvalidDataException("A rebuild file changed before completion.");
            _ = await SituationWindowSidecarStore.ReadAsync(
                prepared.OutputDirectory, entry.WindowIndex, cancellationToken);
        }
    }

    private static IReadOnlyDictionary<int, string> DiscoverIndexedFiles(string directory, Regex pattern)
    {
        var result = new SortedDictionary<int, string>();
        foreach (var path in Directory.EnumerateFiles(directory, "*.json.br", SearchOption.TopDirectoryOnly))
        {
            var match = pattern.Match(Path.GetFileName(path));
            if (!match.Success)
                continue;
            if (!int.TryParse(match.Groups["index"].Value, out var index) ||
                !result.TryAdd(index, Path.GetFullPath(path)))
                throw new InvalidDataException("Duplicate or invalid indexed replay file.");
        }
        return result;
    }

    private static async Task<DemoWindow> ReadWindowAsync(
        string path,
        CancellationToken cancellationToken)
    {
        await using var file = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var brotli = new BrotliStream(file, CompressionMode.Decompress, leaveOpen: false);
        return await JsonSerializer.DeserializeAsync<DemoWindow>(brotli, JsonOptions, cancellationToken)
            ?? throw new InvalidDataException("Replay window is empty.");
    }

    private static async Task<SituationSidecarRebuildManifest> ReadManifestAsync(
        string path,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
            throw new InvalidDataException("Situation sidecar rebuild manifest is missing.");
        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<SituationSidecarRebuildManifest>(
            stream, JsonOptions, cancellationToken)
            ?? throw new InvalidDataException("Situation sidecar rebuild manifest is empty.");
    }

    private static Task WriteManifestAsync(
        string path,
        SituationSidecarRebuildManifest manifest,
        CancellationToken cancellationToken) =>
        SituationArtifactIO.WriteJsonAsync(path, manifest, JsonOptions, cancellationToken);

    internal static Task<string> FileSha256Async(
        string path,
        CancellationToken cancellationToken) =>
        SituationArtifactIO.FileSha256Async(path, cancellationToken);

    private static string RequireDirectory(string path, string parameterName)
    {
        var fullPath = Path.GetFullPath(path);
        if (!Directory.Exists(fullPath))
            throw new DirectoryNotFoundException($"Directory does not exist: {parameterName}");
        return fullPath;
    }

    private static bool IsSameOrDescendant(string root, string candidate)
    {
        var relative = Path.GetRelativePath(root, candidate);
        return relative == "." ||
            (!relative.Equals("..", StringComparison.Ordinal) &&
             !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
             !Path.IsPathRooted(relative));
    }

    private static bool PathEquals(string left, string right) => string.Equals(
        Path.GetFullPath(left),
        Path.GetFullPath(right),
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static bool IsSha256(string value) =>
        value.Length == 64 && value.All(character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private sealed record PreparedRebuild(
        string TargetImportDirectory,
        string SourceDemoPath,
        string OutputDirectory,
        SituationSidecarRebuildManifest InitialManifest);

    private sealed record SituationWindowSourceProjection(
        int Index,
        double CoreFromSeconds,
        double CoreToSeconds,
        double DataFromSeconds,
        double DataToSeconds,
        int FirstFrameIndex,
        int TotalFrameCount,
        IReadOnlyList<DemoFrame> Frames,
        IReadOnlyList<UtilityTrack> UtilityTracks,
        IReadOnlyList<UtilityEffectTrack> UtilityEffects,
        IReadOnlyList<PlayerUtilityState> PlayerUtilityStates,
        IReadOnlyList<PlayerEquipmentState> PlayerEquipmentStates);
}

internal sealed record SituationSidecarOverride(
    string DemoId,
    string TargetImportDirectory,
    string SidecarDirectory,
    string Revision,
    IReadOnlyList<SituationManagedFileStamp> FileStamps,
    SituationSidecarRebuildManifest Manifest)
{
    public void EnsureUnchanged()
    {
        foreach (var stamp in FileStamps)
            stamp.EnsureUnchanged();
    }

    public bool HasChanged()
    {
        try
        {
            foreach (var stamp in FileStamps)
                stamp.EnsureUnchanged();
            return false;
        }
        catch (InvalidDataException)
        {
            return true;
        }
    }

    public async Task<bool> RelevantContentMatchesAsync(
        int windowIndex,
        CancellationToken cancellationToken)
    {
        var manifestPath = Path.Combine(SidecarDirectory, "manifest.json");
        if (!string.Equals(
                await SituationSidecarRebuilder.FileSha256Async(manifestPath, cancellationToken),
                Revision,
                StringComparison.Ordinal))
            return false;

        for (var index = Math.Max(0, windowIndex - 1); index <= windowIndex; index++)
        {
            var entry = Manifest.Windows.SingleOrDefault(item => item.WindowIndex == index);
            if (entry is null)
                return false;
            var windowPath = Path.Combine(TargetImportDirectory, entry.TargetWindowFile);
            var sidecarPath = Path.Combine(SidecarDirectory, entry.SidecarFile);
            if (!string.Equals(
                    await SituationSidecarRebuilder.FileSha256Async(windowPath, cancellationToken),
                    entry.TargetWindowSha256,
                    StringComparison.Ordinal) ||
                !string.Equals(
                    await SituationSidecarRebuilder.FileSha256Async(sidecarPath, cancellationToken),
                    entry.SidecarSha256,
                    StringComparison.Ordinal))
                return false;
        }
        return true;
    }
}

internal sealed class SituationSidecarOverrideRegistry
{
    private readonly ConcurrentDictionary<string, SituationSidecarOverride> registrations =
        new(StringComparer.Ordinal);

    public async Task<SituationSidecarOverride> RegisterAsync(
        string demoId,
        string targetImportDirectory,
        string sidecarDirectory,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(demoId))
            throw new ArgumentException("Demo ID is required.", nameof(demoId));
        var validation = await SituationSidecarRebuilder.ValidateCompleteAsync(
            targetImportDirectory, sidecarDirectory, cancellationToken);
        var registration = new SituationSidecarOverride(
            demoId.Trim(),
            validation.Manifest.TargetImportDirectory,
            validation.Manifest.OutputDirectory,
            validation.Revision,
            validation.FileStamps,
            validation.Manifest);
        registrations[registration.DemoId] = registration;
        return registration;
    }

    public async Task<SituationSidecarOverride?> ResolveAsync(
        string demoId,
        string targetImportDirectory,
        int windowIndex,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!registrations.TryGetValue(demoId, out var registration))
                return null;
            if (!string.Equals(
                    Path.GetFullPath(targetImportDirectory),
                    registration.TargetImportDirectory,
                    OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                throw new InvalidDataException("Registered situation sidecars target a different import directory.");
            if (!registration.HasChanged() &&
                await registration.RelevantContentMatchesAsync(windowIndex, cancellationToken))
                return registration;

            var validation = await SituationSidecarRebuilder.ValidateCompleteAsync(
                registration.TargetImportDirectory,
                registration.SidecarDirectory,
                cancellationToken);
            var refreshed = new SituationSidecarOverride(
                registration.DemoId,
                validation.Manifest.TargetImportDirectory,
                validation.Manifest.OutputDirectory,
                validation.Revision,
                validation.FileStamps,
                validation.Manifest);
            if (registrations.TryUpdate(demoId, refreshed, registration))
                return refreshed;
        }
    }

    public bool Unregister(string demoId) => registrations.TryRemove(demoId, out _);
}

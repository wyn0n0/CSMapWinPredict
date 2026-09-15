using System.Text;
using System.Text.Json;
using CsDemoMap.Api.Models;
using static CsDemoMap.Api.Services.SituationArtifactIO;

namespace CsDemoMap.Api.Services;

internal static class SituationSidecarRebuilderVerifier
{
    public static async Task VerifyAsync(CancellationToken cancellationToken)
    {
        var checks = 0;
        var root = Path.Combine(
            Path.GetTempPath(),
            $"situation-sidecar-rebuild-{Guid.NewGuid():N}");
        var target = Path.Combine(root, "old-import");
        var output = Path.Combine(root, "rebuilt-sidecars");
        var source = Path.Combine(root, "source.dem");
        Directory.CreateDirectory(target);
        await File.WriteAllBytesAsync(
            source,
            Encoding.UTF8.GetBytes("synthetic demo identity fixture"),
            cancellationToken);

        try
        {
            var timeline = SituationFlowVerifier.BuildBoundaryTimeline(
                reversePlayers: false,
                reverseRelations: false);
            var windowCount = DemoWindowSliceBuilder.GetWindowCount(timeline.Metadata);
            for (var index = 0; index < windowCount; index++)
            {
                var slice = DemoWindowSliceBuilder.Build(timeline, index, []);
                await SituationFlowVerifier.WriteWindowAsync(target, slice.Window);
            }
            var originalWindowHashes = await WindowHashesAsync(target, windowCount, cancellationToken);

            var registry = new SituationSidecarOverrideRegistry();
            var service = new SituationSceneService(
                id => id == "fixture-import"
                    ? new("fixture-import", "completed", target, timeline.Metadata, windowCount)
                    : null,
                registry);
            checks += await CheckThrowsAsync<SituationSceneException>(
                () => service.BuildFromImportedDemoAsync(
                    "fixture-import", 1, 1940, cancellationToken),
                "an ordinary query does not rebuild a missing sidecar");

            var manifest = await SituationSidecarRebuilder.RebuildFromTimelineAsync(
                target, source, timeline, output, cancellationToken);
            Check(manifest.Status == "complete" &&
                    manifest.CompletedAtUtc is not null &&
                    manifest.LocalOnly &&
                    manifest.WindowCount == windowCount &&
                    manifest.Windows.Count == windowCount,
                "rebuild manifest is complete and local-only", ref checks);
            Check(manifest.SchemaVersion == SituationSidecarRebuildVersions.V1 &&
                    manifest.DemoWindowSchemaVersion == DemoImportService.SchemaVersion &&
                    manifest.SidecarSchemaVersion == SituationWindowSidecarVersions.V1 &&
                    manifest.WindowSeconds == DemoImportService.WindowSeconds &&
                    manifest.Map == "de_mirage" &&
                    manifest.TickRate == timeline.Metadata.TickRate &&
                    manifest.DurationSeconds == timeline.Metadata.DurationSeconds &&
                    manifest.FrameCount == timeline.Frames.Count,
                "rebuild manifest records all format versions", ref checks);
            Check(manifest.SourceDemoSha256 == await FileSha256Async(source, cancellationToken) &&
                    Path.GetFullPath(manifest.SourceDemoPath) == Path.GetFullPath(source) &&
                    Path.GetFullPath(manifest.TargetImportDirectory) == Path.GetFullPath(target) &&
                    Path.GetFullPath(manifest.OutputDirectory) == Path.GetFullPath(output),
                "rebuild manifest records source and local paths", ref checks);
            Check(manifest.Windows.All(entry =>
                    File.Exists(Path.Combine(target, entry.TargetWindowFile)) &&
                    File.Exists(Path.Combine(output, entry.SidecarFile)) &&
                    entry.TargetWindowSha256.Length == 64 && entry.SidecarSha256.Length == 64),
                "manifest records target and sidecar hashes", ref checks);
            Check((await WindowHashesAsync(target, windowCount, cancellationToken))
                    .SequenceEqual(originalWindowHashes),
                "rebuild leaves every target replay window unchanged", ref checks);

            var validation = await SituationSidecarRebuilder.ValidateCompleteAsync(
                target, output, cancellationToken);
            Check(validation.Revision.Length == 64 &&
                    validation.FileStamps.Count == 1 + windowCount * 2,
                "complete rebuild rereads hashes and captures managed files", ref checks);

            for (var index = 0; index < windowCount; index++)
            {
                var slice = DemoWindowSliceBuilder.Build(timeline, index, []);
                var originalSidecar = SituationWindowSidecarStore.Build(
                    timeline, index, slice.DataFromTick, slice.DataToTick);
                originalSidecar = originalSidecar with
                {
                    SemanticFrames = originalSidecar.SemanticFrames.Select(frame => frame with
                    {
                        Clock = frame.Clock with { ClockKnown = false }
                    }).ToArray()
                };
                await SituationWindowSidecarStore.WriteAsync(target, originalSidecar, cancellationToken);
            }

            var fallback = await service.BuildFromImportedDemoAsync(
                "fixture-import", 1, 1940, cancellationToken);
            Check(ContainsQuality(fallback.Scene, SituationDataQualityCodes.ClockUnknown),
                "unregistered imports read their original sidecars", ref checks);

            var registration = await service.RegisterSidecarOverrideAsync(
                "fixture-import", output, cancellationToken);
            var overridden = await service.BuildFromImportedDemoAsync(
                "fixture-import", 1, 1940, cancellationToken);
            var expected = new SituationSceneService().BuildFromTimeline(
                timeline, "fixture-import", 1, 1940, cancellationToken: cancellationToken);
            Check(registration.SidecarDirectory == Path.GetFullPath(output) &&
                    registration.Revision == validation.Revision &&
                    overridden.CanonicalJson == expected.CanonicalJson &&
                    !ContainsQuality(overridden.Scene, SituationDataQualityCodes.ClockUnknown),
                "an explicitly registered rebuild takes priority", ref checks);
            Check(!ContainsAny(
                    overridden.CanonicalJson,
                    Path.GetFullPath(target),
                    Path.GetFullPath(output),
                    Path.GetFullPath(source),
                    "source.dem"),
                "local rebuild paths never enter a scene", ref checks);

            var corruptedSidecar = Path.Combine(output, "situation-window-0001.json.br");
            var buildsBeforeTimestampChange = service.GetExecutionSnapshot().BuildExecutions;
            File.SetLastWriteTimeUtc(
                corruptedSidecar,
                File.GetLastWriteTimeUtc(corruptedSidecar).AddSeconds(2));
            var revalidated = await service.BuildFromImportedDemoAsync(
                "fixture-import", 1, 1940, cancellationToken);
            Check(revalidated.CanonicalJson == overridden.CanonicalJson &&
                    service.GetExecutionSnapshot().BuildExecutions == buildsBeforeTimestampChange,
                "a metadata-only change is revalidated without invalidating identical content", ref checks);
            var registeredTimestamp = File.GetLastWriteTimeUtc(corruptedSidecar);
            var registeredLength = new FileInfo(corruptedSidecar).Length;
            await File.WriteAllBytesAsync(
                corruptedSidecar, new byte[checked((int)registeredLength)], cancellationToken);
            File.SetLastWriteTimeUtc(corruptedSidecar, registeredTimestamp);
            checks += await CheckThrowsAsync<SituationSceneException>(
                () => service.BuildFromImportedDemoAsync(
                    "fixture-import", 1, 1940, cancellationToken),
                "an equal-length registered rebuild change with a preserved timestamp fails without fallback");
            Check(service.UnregisterSidecarOverride("fixture-import"),
                "an override registration can be removed", ref checks);
            var rolledBack = await service.BuildFromImportedDemoAsync(
                "fixture-import", 1, 1940, cancellationToken);
            Check(ContainsQuality(rolledBack.Scene, SituationDataQualityCodes.ClockUnknown),
                "unregistering restores the original sidecar path", ref checks);

            var completeManifestHash = await FileSha256Async(
                Path.Combine(output, "manifest.json"), cancellationToken);
            checks += await CheckThrowsAsync<IOException>(
                () => SituationSidecarRebuilder.RebuildFromTimelineAsync(
                    target, source, timeline, output, cancellationToken),
                "an existing output directory is never overwritten");
            Check(completeManifestHash == await FileSha256Async(
                    Path.Combine(output, "manifest.json"), cancellationToken),
                "rejected overwrite leaves the completed manifest unchanged", ref checks);

            var mismatchTarget = Path.Combine(root, "mismatch-import");
            var mismatchOutput = Path.Combine(root, "mismatch-output");
            Directory.CreateDirectory(mismatchTarget);
            for (var index = 0; index < windowCount; index++)
            {
                var window = DemoWindowSliceBuilder.Build(timeline, index, []).Window;
                if (index == 1)
                {
                    window = window with
                    {
                        Frames = window.Frames.Select((frame, frameIndex) => frameIndex == 0
                            ? frame with
                            {
                                Players = frame.Players.Select((player, playerIndex) => playerIndex == 0
                                    ? player with { Health = 99 }
                                    : player).ToArray()
                            }
                            : frame).ToArray()
                    };
                }
                await SituationFlowVerifier.WriteWindowAsync(mismatchTarget, window);
            }
            var mismatchHashes = await WindowHashesAsync(
                mismatchTarget, windowCount, cancellationToken);
            checks += await CheckThrowsAsync<InvalidDataException>(
                () => SituationSidecarRebuilder.RebuildFromTimelineAsync(
                    mismatchTarget, source, timeline, mismatchOutput, cancellationToken),
                "source content must match every target window");
            Check(await ReadManifestStatusAsync(mismatchOutput, cancellationToken) == "incomplete" &&
                    (await WindowHashesAsync(mismatchTarget, windowCount, cancellationToken))
                    .SequenceEqual(mismatchHashes),
                "a failed source match remains incomplete and preserves target windows", ref checks);
            checks += await CheckThrowsAsync<InvalidDataException>(
                () => registry.RegisterAsync(
                    "mismatch", mismatchTarget, mismatchOutput, cancellationToken),
                "an incomplete rebuild cannot be registered");

            var missingSourceOutput = Path.Combine(root, "missing-source-output");
            checks += await CheckThrowsAsync<FileNotFoundException>(
                () => SituationSidecarRebuilder.RebuildFromTimelineAsync(
                    target,
                    Path.Combine(root, "missing.dem"),
                    timeline,
                    missingSourceOutput,
                    cancellationToken),
                "the source demo must be explicitly present");
            Check(!Directory.Exists(missingSourceOutput),
                "missing source validation does not create an output directory", ref checks);

            var nestedOutput = Path.Combine(target, "rebuilt-sidecars");
            checks += await CheckThrowsAsync<InvalidDataException>(
                () => SituationSidecarRebuilder.RebuildFromTimelineAsync(
                    target, source, timeline, nestedOutput, cancellationToken),
                "rebuild output cannot modify the target import tree");
            Check(!Directory.Exists(nestedOutput),
                "rejected nested output leaves the import tree unchanged", ref checks);

            using var canceled = new CancellationTokenSource();
            canceled.Cancel();
            var canceledOutput = Path.Combine(root, "canceled-output");
            checks += await CheckThrowsAsync<OperationCanceledException>(
                () => SituationSidecarRebuilder.RebuildFromTimelineAsync(
                    target, source, timeline, canceledOutput, canceled.Token),
                "rebuild honors cancellation before output creation");
            Check(!Directory.Exists(canceledOutput),
                "pre-canceled rebuild does not create a misleading directory", ref checks);

            using var interrupted = new CancellationTokenSource();
            var interruptedOutput = Path.Combine(root, "interrupted-output");
            checks += await CheckThrowsAsync<OperationCanceledException>(
                () => SituationSidecarRebuilder.RebuildFromTimelineAsync(
                    target,
                    source,
                    timeline,
                    interruptedOutput,
                    interrupted.Token,
                    (index, _) =>
                    {
                        if (index == 0)
                            interrupted.Cancel();
                        interrupted.Token.ThrowIfCancellationRequested();
                        return ValueTask.CompletedTask;
                    }),
                "a rebuild interrupted after its first sidecar preserves cancellation");
            Check(await ReadManifestStatusAsync(interruptedOutput, cancellationToken) == "incomplete" &&
                    File.Exists(Path.Combine(interruptedOutput, "situation-window-0000.json.br")),
                "an interrupted rebuild keeps a partial directory visibly incomplete", ref checks);
            checks += await CheckThrowsAsync<InvalidDataException>(
                () => SituationSidecarRebuilder.ValidateCompleteAsync(
                    target, interruptedOutput, cancellationToken),
                "an interrupted rebuild cannot pass complete validation");
            Check((await WindowHashesAsync(target, windowCount, cancellationToken))
                    .SequenceEqual(originalWindowHashes),
                "an interrupted rebuild leaves target replay windows unchanged", ref checks);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }

        Console.WriteLine($"Situation sidecar rebuild checks passed: {checks}");
    }

    private static bool ContainsQuality(MinimapSceneV1 scene, string code) =>
        scene.DataQuality.Any(item => item.Code == code);

    private static bool ContainsAny(string value, params string[] needles) =>
        needles.Any(needle => value.Contains(needle, StringComparison.OrdinalIgnoreCase));

    private static async Task<IReadOnlyList<string>> WindowHashesAsync(
        string directory,
        int windowCount,
        CancellationToken cancellationToken)
    {
        var result = new List<string>(windowCount);
        for (var index = 0; index < windowCount; index++)
            result.Add(await FileSha256Async(
                Path.Combine(directory, $"window-{index:D4}.json.br"), cancellationToken));
        return result;
    }

    private static async Task<string> ReadManifestStatusAsync(
        string directory,
        CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(Path.Combine(directory, "manifest.json"));
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        return document.RootElement.GetProperty("status").GetString()
            ?? throw new InvalidDataException("Fixture manifest status is missing.");
    }

    private static void Check(bool condition, string label, ref int checks)
    {
        if (!condition)
            throw new InvalidOperationException($"Situation sidecar rebuild check failed: {label}.");
        checks++;
    }

    private static async Task<int> CheckThrowsAsync<T>(Func<Task> action, string label)
        where T : Exception
    {
        try
        {
            await action();
        }
        catch (T)
        {
            return 1;
        }
        throw new InvalidOperationException($"Situation sidecar rebuild check failed: {label}.");
    }
}

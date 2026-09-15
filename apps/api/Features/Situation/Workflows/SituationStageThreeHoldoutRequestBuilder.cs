using System.Security.Cryptography;
using System.Text;
using CsDemoMap.Api.Models;
using static CsDemoMap.Api.Services.SituationArtifactIO;

namespace CsDemoMap.Api.Services;

internal static class SituationStageThreeHoldoutRequestBuilder
{
    internal const string RequestVersion = "situation-stage3-holdout-request-v1";
    internal const string SelectionVersion = "situation-stage3-holdout-selection-v1";

    public static async Task<HoldoutRequestBuildResult> BuildAsync(
        SituationFrozenDataset split,
        string splitReference,
        string splitSha256,
        CancellationToken cancellationToken)
    {
        var sceneService = new SituationSceneService();
        var demos = new List<HoldoutDemoRequest>();
        var ordinal = 0;
        foreach (var fileName in split.ValidationFiles.Order(StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            ordinal++;
            var expectedSha256 = split.MatchIds[fileName];
            var sourcePath = Path.Combine(split.SourceDirectory, fileName);
            var actualSha256 = await FileSha256Async(sourcePath, cancellationToken);
            if (actualSha256 != expectedSha256)
                throw new InvalidDataException($"Holdout demo hash mismatch: {fileName}");
            Console.WriteLine($"Stage-three committing holdout ticks {ordinal}/8: {fileName}");
            await using var source = File.OpenRead(sourcePath);
            var timeline = await new DemoParserService().ParseAsync(
                source, fileName, cancellationToken, collectSemantics: true);
            var demoRef = $"demo-{actualSha256[..12]}";
            var frames = timeline.Frames
                .Where(frame => frame.Round.Phase is "live" or "post-plant")
                .OrderBy(frame => frame.Tick)
                .GroupBy(frame => frame.Tick / timeline.Metadata.TickRate)
                .Select(group => group.First())
                .ToArray();
            var prePlant = frames.Where(frame =>
                    frame.Round.Phase == "live" &&
                    frame.Bomb.State is "carried" or "dropped" or "planting")
                .ToArray();
            var postPlant = frames.Where(frame =>
                    frame.Round.Phase == "post-plant" ||
                    frame.Bomb.State is "planted" or "defusing")
                .ToArray();
            var selected = new List<HoldoutTickRequest>
            {
                SelectFirstAcceptable(prePlant, "pre-plant", timeline, demoRef, actualSha256, sceneService,
                    new HashSet<int>(), cancellationToken)
            };
            if (postPlant.Length > 0)
            {
                selected.Add(SelectFirstAcceptable(postPlant, "post-plant", timeline, demoRef, actualSha256,
                    sceneService, selected.Select(item => item.Tick).ToHashSet(), cancellationToken));
            }
            else
            {
                selected.Add(SelectFirstAcceptable(frames, "live-fallback", timeline, demoRef, actualSha256,
                    sceneService, selected.Select(item => item.Tick).ToHashSet(), cancellationToken));
            }
            demos.Add(new(fileName, expectedSha256, selected.OrderBy(item => item.Tick).ToArray()));
        }

        var request = new HoldoutRequest(
            RequestVersion,
            SelectionVersion,
            splitReference.Replace('\\', '/'),
            splitSha256,
            8,
            2,
            demos);
        ValidateRequest(request);
        var canonicalJson = SituationCanonicalJson.Serialize(request);
        return new(request, canonicalJson, Sha256(canonicalJson));
    }

    internal static void ValidateRequest(HoldoutRequest request)
    {
        if (request.SchemaVersion != RequestVersion || request.SelectionVersion != SelectionVersion)
            throw new InvalidDataException("Unsupported stage-three holdout request version.");
        if (string.IsNullOrWhiteSpace(request.SplitFile) ||
            !IsSha256(request.ExpectedSplitSha256))
            throw new InvalidDataException("Holdout split reference is invalid.");
        if (request.DemoCount != 8 || request.SamplesPerDemo != 2 || request.Demos is null ||
            request.Demos.Count != 8 ||
            request.Demos.Select(item => item.FileName).Distinct(StringComparer.OrdinalIgnoreCase).Count() != 8)
            throw new InvalidDataException("Holdout request must contain exactly two ticks for each of eight demos.");
        foreach (var demo in request.Demos)
        {
            if (string.IsNullOrWhiteSpace(demo.FileName) || Path.GetFileName(demo.FileName) != demo.FileName ||
                !IsSha256(demo.ExpectedSha256) || demo.Ticks is null ||
                demo.Ticks.Count != 2 || demo.Ticks.Select(item => item.Tick).Distinct().Count() != 2)
                throw new InvalidDataException("Holdout demo reference is invalid.");
            foreach (var tick in demo.Ticks)
            {
                if (tick.Tick < 0 || tick.Category is not ("pre-plant" or "post-plant" or "live-fallback") ||
                    !IsSha256(tick.SelectionKey) ||
                    tick.SelectionKey != StableKey(demo.ExpectedSha256, tick.Tick))
                    throw new InvalidDataException("Holdout tick commitment is invalid.");
            }
            if (!demo.Ticks.Any(item => item.Category == "pre-plant"))
                throw new InvalidDataException("Each holdout demo requires a pre-plant tick.");
            if (demo.Ticks.Count(item => item.Category is "post-plant" or "live-fallback") != 1)
                throw new InvalidDataException("Each holdout demo requires a post-plant tick or declared fallback.");
        }
    }

    private static HoldoutTickRequest SelectFirstAcceptable(
        IReadOnlyList<DemoFrame> candidates,
        string category,
        DemoTimeline timeline,
        string demoRef,
        string demoSha256,
        SituationSceneService sceneService,
        IReadOnlySet<int> excluded,
        CancellationToken cancellationToken)
    {
        foreach (var frame in candidates
                     .Where(frame => !excluded.Contains(frame.Tick))
                     .OrderBy(frame => StableKey(demoSha256, frame.Tick), StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var windowIndex = frame.Tick / (DemoImportService.WindowSeconds * timeline.Metadata.TickRate);
            var result = sceneService.BuildFromTimeline(
                timeline, demoRef, windowIndex, frame.Tick, cancellationToken: cancellationToken);
            if (result.Scene.Teams.T.Alive is not > 0 || result.Scene.Teams.CT.Alive is not > 0 ||
                result.Scene.DataQuality.Any(item => item.Severity == SituationQualitySeverity.Error))
                continue;
            return new(frame.Tick, category, StableKey(demoSha256, frame.Tick));
        }
        throw new InvalidDataException($"No eligible {category} holdout tick was found for {demoRef}.");
    }

    private static string StableKey(string demoSha256, int tick) => Convert.ToHexStringLower(
        SHA256.HashData(Encoding.UTF8.GetBytes($"{SelectionVersion}:{demoSha256}:{tick}")));

    private static string Sha256(string value) => Convert.ToHexStringLower(
        SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static bool IsLowerHex(char value) => value is >= '0' and <= '9' or >= 'a' and <= 'f';

    private static bool IsSha256(string? value) =>
        value is { Length: 64 } && value.All(IsLowerHex);

    internal sealed record HoldoutRequest(
        string SchemaVersion,
        string SelectionVersion,
        string SplitFile,
        string ExpectedSplitSha256,
        int DemoCount,
        int SamplesPerDemo,
        IReadOnlyList<HoldoutDemoRequest> Demos);

    internal sealed record HoldoutDemoRequest(
        string FileName,
        string ExpectedSha256,
        IReadOnlyList<HoldoutTickRequest> Ticks);

    internal sealed record HoldoutTickRequest(int Tick, string Category, string SelectionKey);

    internal sealed record HoldoutRequestBuildResult(
        HoldoutRequest Request,
        string CanonicalJson,
        string Sha256);
}

using System.Text;
using System.Text.Json;
using CsDemoMap.Api.Models;
using static CsDemoMap.Api.Services.SituationArtifactIO;

namespace CsDemoMap.Api.Services;

internal static class SituationSampleExporter
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    public static async Task ExportAsync(
        string requestPath,
        string outputPath,
        CancellationToken cancellationToken)
    {
        var requestFullPath = Path.GetFullPath(requestPath);
        var outputFullPath = Path.GetFullPath(outputPath);
        if (Directory.Exists(outputFullPath) || File.Exists(outputFullPath))
            throw new IOException($"Output path already exists: {outputFullPath}");

        await using var requestStream = File.OpenRead(requestFullPath);
        var request = await JsonSerializer.DeserializeAsync<SituationSampleRequest>(
            requestStream, JsonOptions, cancellationToken)
            ?? throw new InvalidDataException("Situation sample request is empty.");
        ValidateRequest(request);

        var requestDirectory = Path.GetDirectoryName(requestFullPath)!;
        var splitPath = ResolvePath(requestDirectory, request.SplitFile);
        var split = await SituationFrozenDatasetLoader.LoadAsync(
            splitPath, expectedSplitSha256: null, cancellationToken);
        var splitSha256 = split.SplitSha256;
        Directory.CreateDirectory(outputFullPath);
        var scenesDirectory = Path.Combine(outputFullPath, "scenes");
        Directory.CreateDirectory(scenesDirectory);
        var manifestPath = Path.Combine(outputFullPath, "manifest.json");
        await WriteJsonAsync(manifestPath, new
        {
            status = "incomplete",
            sceneSchemaVersion = SituationContractVersions.Scene,
            sceneBuilderVersion = SituationSceneBuilder.BuilderVersion,
            geometryVersion = SituationSceneBuilder.GeometryVersion
        }, cancellationToken);

        var sceneService = new SituationSceneService();
        var records = new List<SituationSampleRecord>();
        var demoRecords = new List<SituationSampleDemoRecord>();
        try
        {
            foreach (var demo in request.Demos)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var sourcePath = Path.GetFullPath(Path.Combine(split.SourceDirectory, demo.FileName));
                EnsureDirectChild(
                    split.SourceDirectory,
                    sourcePath,
                    "Demo must be a direct child of the frozen source directory.");
                var sourceSha256 = await FileSha256Async(sourcePath, cancellationToken);
                var expectedMatchId = split.MatchIds.GetValueOrDefault(demo.FileName)
                    ?? throw new InvalidDataException($"Demo is missing from the frozen split: {demo.FileName}");
                if (!string.Equals(sourceSha256, expectedMatchId, StringComparison.Ordinal))
                    throw new InvalidDataException($"Demo hash does not match frozen split: {demo.FileName}");
                var isValidation = split.ValidationFiles.Contains(demo.FileName);
                if (demo.Role == "holdout" != isValidation)
                    throw new InvalidDataException($"Demo role does not match frozen split: {demo.FileName}");

                await using var source = File.OpenRead(sourcePath);
                var timeline = await new DemoParserService().ParseAsync(
                    source, demo.FileName, cancellationToken, collectSemantics: true);
                var candidates = SelectCandidates(timeline, demo.SampleCount);
                if (candidates.Count < demo.SampleCount)
                    throw new InvalidDataException(
                        $"Only {candidates.Count} usable scenes found in {demo.FileName}; requested {demo.SampleCount}.");

                var demoRef = $"demo-{sourceSha256[..12]}";
                var demoIndex = demoRecords.Count + 1;
                foreach (var candidate in candidates)
                {
                    var windowIndex = candidate.Tick /
                        (DemoImportService.WindowSeconds * timeline.Metadata.TickRate);
                    var result = sceneService.BuildFromTimeline(
                        timeline,
                        demoRef,
                        windowIndex,
                        candidate.Tick,
                        cancellationToken: cancellationToken);
                    var fileName = $"scene-{demoIndex:D2}-{records.Count + 1:D3}.json";
                    var relativePath = $"scenes/{fileName}";
                    var canonical = result.CanonicalJson;
                    await File.WriteAllTextAsync(
                        Path.Combine(scenesDirectory, fileName), canonical, new UTF8Encoding(false), cancellationToken);
                    records.Add(new(
                        relativePath,
                        demoRef,
                        demo.Role,
                        candidate.Tick,
                        candidate.Categories,
                        result.Sha256));
                }
                demoRecords.Add(new(demoRef, demo.FileName, demo.Role, sourceSha256, candidates.Count));
            }

            await WriteJsonAsync(Path.Combine(outputFullPath, "selection.json"), new
            {
                schemaVersion = "situation-sample-selection-v1",
                splitFile = Path.GetFileName(splitPath),
                splitSha256,
                demos = demoRecords,
                scenes = records
            }, cancellationToken);
            await File.WriteAllTextAsync(
                Path.Combine(outputFullPath, "review.md"),
                BuildReview(records),
                new UTF8Encoding(false),
                cancellationToken);
            await VerifyWrittenScenesAsync(outputFullPath, records, cancellationToken);
            await WriteJsonAsync(manifestPath, new
            {
                status = "complete",
                createdAtUtc = DateTimeOffset.UtcNow,
                sceneSchemaVersion = SituationContractVersions.Scene,
                sceneBuilderVersion = SituationSceneBuilder.BuilderVersion,
                geometryVersion = SituationSceneBuilder.GeometryVersion,
                splitSha256,
                demoCount = demoRecords.Count,
                sceneCount = records.Count,
                scenes = records.Select(record => new { record.Path, record.Sha256 })
            }, cancellationToken);
            Console.WriteLine($"Situation samples exported: {records.Count} scenes to {outputFullPath}");
        }
        catch
        {
            Console.Error.WriteLine($"Situation sample export is incomplete: {outputFullPath}");
            throw;
        }
    }

    private static IReadOnlyList<SceneCandidate> SelectCandidates(DemoTimeline timeline, int count)
    {
        var usable = timeline.Frames
            .Where(frame => frame.Round.Phase is "live" or "post-plant")
            .OrderBy(frame => frame.Tick)
            .Select(frame => new SceneCandidate(frame.Tick, Categories(frame, timeline)))
            .ToArray();
        var selected = new Dictionary<int, SceneCandidate>();
        var categoryOrder = new[]
        {
            "live-opening", "deployment", "contact-proximity", "bomb-dropped",
            "post-plant", "utility-active", "clutch", "window-boundary"
        };
        foreach (var category in categoryOrder)
        {
            foreach (var candidate in EvenlySpaced(usable.Where(item => item.Categories.Contains(category)).ToArray(), 2))
                selected.TryAdd(candidate.Tick, candidate);
        }
        foreach (var candidate in EvenlySpaced(usable, count * 2))
        {
            if (selected.Count >= count)
                break;
            selected.TryAdd(candidate.Tick, candidate);
        }
        return selected.Values.OrderBy(item => item.Tick).Take(count).ToArray();
    }

    private static IReadOnlyList<string> Categories(DemoFrame frame, DemoTimeline timeline)
    {
        var categories = new List<string>();
        var semantic = timeline.Semantics?.Frames.FirstOrDefault(item => item.Tick == frame.Tick);
        var liveElapsed = semantic?.Clock.LiveElapsedSeconds;
        if (frame.Round.Phase == "live" && liveElapsed is >= 0 and <= 5)
            categories.Add("live-opening");
        if (frame.Round.Phase == "live" && liveElapsed is >= 10 and <= 35)
            categories.Add("deployment");
        if (NearestOpponentDistance(frame) is { } distance && distance <= 0.08)
            categories.Add("contact-proximity");
        if (frame.Bomb.State == "dropped")
            categories.Add("bomb-dropped");
        if (frame.Round.Phase == "post-plant" || frame.Bomb.State is "planting" or "planted" or "defusing")
            categories.Add("post-plant");
        if (timeline.UtilityTracks.Any(track => track.StartTick <= frame.Tick && track.EndTick > frame.Tick) ||
            timeline.UtilityEffects.Any(effect => effect.StartTick <= frame.Tick && effect.EndTick > frame.Tick))
            categories.Add("utility-active");
        var tAlive = frame.Players.Count(player => player.Team == "T" && player.Alive);
        var ctAlive = frame.Players.Count(player => player.Team == "CT" && player.Alive);
        if (tAlive > 0 && ctAlive > 0 && (tAlive <= 2 || ctAlive <= 2))
            categories.Add("clutch");
        var windowRemainder = frame.TimeSeconds % DemoImportService.WindowSeconds;
        if (windowRemainder <= 0.25 || windowRemainder >= DemoImportService.WindowSeconds - 0.25)
            categories.Add("window-boundary");
        if (categories.Count == 0)
            categories.Add("regular-live");
        return categories;
    }

    private static double? NearestOpponentDistance(DemoFrame frame)
    {
        var geometry = MapFeatureGeometries.Find("de_mirage")!;
        var t = frame.Players.Where(player => player.Team == "T" && player.Alive)
            .Select(player => geometry.Normalize(player.X, player.Y)).ToArray();
        var ct = frame.Players.Where(player => player.Team == "CT" && player.Alive)
            .Select(player => geometry.Normalize(player.X, player.Y)).ToArray();
        if (t.Length == 0 || ct.Length == 0)
            return null;
        return t.SelectMany(left => ct.Select(right =>
        {
            var dx = left.X - right.X;
            var dy = left.Y - right.Y;
            return Math.Sqrt(dx * dx + dy * dy);
        })).Min();
    }

    private static IEnumerable<SceneCandidate> EvenlySpaced(
        IReadOnlyList<SceneCandidate> candidates,
        int desired)
    {
        if (candidates.Count == 0 || desired <= 0)
            yield break;
        var actual = Math.Min(desired, candidates.Count);
        for (var index = 0; index < actual; index++)
        {
            var position = actual == 1
                ? candidates.Count / 2
                : (int)Math.Round(index * (candidates.Count - 1d) / (actual - 1), MidpointRounding.AwayFromZero);
            yield return candidates[position];
        }
    }

    private static void ValidateRequest(SituationSampleRequest request)
    {
        if (request.SchemaVersion != "situation-sample-request-v1-draft.1")
            throw new InvalidDataException("Unsupported situation sample request version.");
        if (request.Demos.Count != 2 || request.Demos.Select(item => item.Role).ToHashSet().Count != 2 ||
            !request.Demos.Any(item => item.Role == "training") || !request.Demos.Any(item => item.Role == "holdout"))
            throw new InvalidDataException("Request must contain one training and one holdout demo.");
        if (request.Demos.Any(item => item.SampleCount < 15))
            throw new InvalidDataException("Each demo must request at least 15 scenes.");
    }

    private static async Task VerifyWrittenScenesAsync(
        string outputDirectory,
        IReadOnlyList<SituationSampleRecord> records,
        CancellationToken cancellationToken)
    {
        var strictUtf8 = new UTF8Encoding(false, true);
        foreach (var record in records)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = Path.GetFullPath(Path.Combine(outputDirectory, record.Path));
            EnsureDescendant(outputDirectory, path, "Scene output escaped the export directory.");
            var bytes = await File.ReadAllBytesAsync(path, cancellationToken);
            if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
                throw new InvalidDataException($"Scene JSON contains a UTF-8 BOM: {record.Path}");
            var text = strictUtf8.GetString(bytes);
            var scene = JsonSerializer.Deserialize<MinimapSceneV1>(text, JsonOptions)
                ?? throw new InvalidDataException($"Scene JSON is empty: {record.Path}");
            SituationContractValidator.Validate(scene);
            if (!string.Equals(text, SituationCanonicalJson.Serialize(scene), StringComparison.Ordinal))
                throw new InvalidDataException($"Scene JSON is not canonical: {record.Path}");
            var hash = Sha256(bytes);
            if (!string.Equals(hash, record.Sha256, StringComparison.Ordinal))
                throw new InvalidDataException($"Scene JSON hash mismatch: {record.Path}");
        }
    }

    private static string BuildReview(IReadOnlyList<SituationSampleRecord> records)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# MinimapSceneV1 v1 技术复核");
        builder.AppendLine();
        builder.AppendLine("> 每条场景对照真实 Demo 复核；本文件未勾选项不表示已验收。");
        builder.AppendLine();
        builder.AppendLine("| 已复核 | 场景 | 数据侧 | Tick | 覆盖类别 | 坐标/轨迹 | 人数/生命 | C4/效果 | 匿名/因果 | 问题 | ");
        builder.AppendLine("| --- | --- | --- | ---: | --- | --- | --- | --- | --- | --- |");
        foreach (var record in records)
            builder.AppendLine($"| [ ] | `{record.Path}` | {record.Role} | {record.Tick} | {string.Join(", ", record.Categories)} | [ ] | [ ] | [ ] | [ ] |  | ");
        return builder.ToString();
    }

    private sealed record SituationSampleRequest(
        string SchemaVersion,
        string SplitFile,
        IReadOnlyList<SituationDemoRequest> Demos);

    private sealed record SituationDemoRequest(string FileName, string Role, int SampleCount);

    private sealed record SceneCandidate(int Tick, IReadOnlyList<string> Categories);

    private sealed record SituationSampleRecord(
        string Path,
        string DemoRef,
        string Role,
        int Tick,
        IReadOnlyList<string> Categories,
        string Sha256);

    private sealed record SituationSampleDemoRecord(
        string DemoRef,
        string FileName,
        string Role,
        string SourceSha256,
        int SceneCount);
}

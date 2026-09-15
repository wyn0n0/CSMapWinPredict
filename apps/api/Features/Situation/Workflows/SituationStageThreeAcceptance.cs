using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CsDemoMap.Api.Models;
using static CsDemoMap.Api.Services.SituationArtifactIO;

namespace CsDemoMap.Api.Services;

internal static class SituationStageThreeAcceptance
{
    internal const string ManifestVersion = "situation-stage3-acceptance-manifest-v1";
    private const int PerformanceIterations = 200;

    private static readonly JsonSerializerOptions ReadOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public static async Task RunAsync(
        string requestPath,
        string outputPath,
        CancellationToken cancellationToken)
    {
        var requestFullPath = Path.GetFullPath(requestPath);
        var outputFullPath = Path.GetFullPath(outputPath);
        EnsureNewOutput(outputFullPath);

        // This deliberately happens before the request is read or an output directory is created.
        // A missing/unapproved frozen resource therefore cannot observe holdout rule outputs.
        var ruleLoad = SituationAnalysisRuleLoader.LoadFrozen();
        if (ruleLoad.Rules.AnalysisRuleVersion != "situation-analysis-rules-v1")
            throw new InvalidDataException("Stage-three acceptance requires the approved frozen rule version.");

        var request = await ReadJsonAsync<SituationStageThreeHoldoutRequestBuilder.HoldoutRequest>(
            requestFullPath, cancellationToken);
        SituationStageThreeHoldoutRequestBuilder.ValidateRequest(request);
        var canonicalRequest = SituationCanonicalJson.Serialize(request);
        var requestSha256 = Sha256(canonicalRequest);
        await ValidateCalibrationCommitmentAsync(
            requestFullPath, requestSha256, ruleLoad, cancellationToken);
        var repositoryRoot = FindRepositoryRoot(Path.GetDirectoryName(requestFullPath)!);
        var splitPath = ResolveRepositoryPath(repositoryRoot, request.SplitFile, "Holdout split path");
        var split = await SituationStageThreeCalibration.LoadFrozenSplitAsync(
            splitPath, request.ExpectedSplitSha256, cancellationToken);
        ValidateExactHoldoutMembership(request, split);

        Directory.CreateDirectory(outputFullPath);
        Directory.CreateDirectory(Path.Combine(outputFullPath, "scenes"));
        Directory.CreateDirectory(Path.Combine(outputFullPath, "facts"));
        Directory.CreateDirectory(Path.Combine(outputFullPath, "narratives"));
        var manifestPath = Path.Combine(outputFullPath, "manifest.json");
        await WriteJsonAsync(manifestPath, new
        {
            schemaVersion = ManifestVersion,
            status = "incomplete",
            ruleVersion = ruleLoad.Rules.AnalysisRuleVersion,
            ruleConfigSha256 = ruleLoad.Sha256,
            holdoutRequestSha256 = requestSha256
        }, cancellationToken);

        try
        {
            await WriteCanonicalAsync(Path.Combine(outputFullPath, "request.json"),
                canonicalRequest, cancellationToken);
            await WriteCanonicalAsync(Path.Combine(outputFullPath, "rules.json"),
                ruleLoad.CanonicalJson, cancellationToken);
            var analyzer = new SituationDeterministicAnalyzer(ruleLoad);
            var sceneService = new SituationSceneService();
            var samples = new List<AcceptanceSample>();
            DemoTimeline? performanceTimeline = null;
            string? performanceDemoRef = null;
            int performanceTick = 0;
            var demoOrdinal = 0;
            foreach (var demo in request.Demos.OrderBy(item => item.FileName, StringComparer.OrdinalIgnoreCase))
            {
                cancellationToken.ThrowIfCancellationRequested();
                demoOrdinal++;
                Console.WriteLine($"Stage-three acceptance parsing holdout demo {demoOrdinal}/8: {demo.FileName}");
                var sourcePath = Path.Combine(split.SourceDirectory, demo.FileName);
                if (await FileSha256Async(sourcePath, cancellationToken) != demo.ExpectedSha256)
                    throw new InvalidDataException($"Holdout demo hash mismatch: {demo.FileName}");
                await using var source = File.OpenRead(sourcePath);
                var timeline = await new DemoParserService().ParseAsync(
                    source, demo.FileName, cancellationToken, collectSemantics: true);
                var demoRef = $"demo-{demo.ExpectedSha256[..12]}";
                foreach (var committed in demo.Ticks.OrderBy(item => item.Tick))
                {
                    var frame = timeline.Frames.FirstOrDefault(item => item.Tick == committed.Tick)
                        ?? throw new InvalidDataException("Committed holdout tick is absent from its demo.");
                    ValidateCommittedCategory(frame, committed.Category);
                    var windowIndex = committed.Tick /
                                      (DemoImportService.WindowSeconds * timeline.Metadata.TickRate);
                    var sceneResult = sceneService.BuildFromTimeline(
                        timeline, demoRef, windowIndex, committed.Tick,
                        cancellationToken: cancellationToken);
                    if (sceneResult.Scene.DataQuality.Any(item => item.Severity == SituationQualitySeverity.Error))
                        throw new InvalidDataException("Committed holdout scene now contains a blocking quality error.");
                    var analysis = analyzer.Analyze(sceneResult.Scene);
                    samples.Add(new(
                        samples.Count + 1,
                        demoRef,
                        committed.Tick,
                        committed.Category,
                        $"scenes/sample-{samples.Count + 1:D3}.json",
                        $"facts/sample-{samples.Count + 1:D3}.json",
                        $"narratives/sample-{samples.Count + 1:D3}.json",
                        sceneResult,
                        analysis));
                }
                performanceTimeline ??= timeline;
                performanceDemoRef ??= demoRef;
                performanceTick = performanceTimeline == timeline ? demo.Ticks[0].Tick : performanceTick;
            }
            if (samples.Count != 16 || samples.Select(item => item.DemoRef).Distinct().Count() != 8)
                throw new InvalidDataException("Stage-three acceptance requires 16 samples from eight holdout demos.");

            foreach (var sample in samples)
            {
                await WriteCanonicalAsync(Path.Combine(outputFullPath, sample.ScenePath),
                    sample.Scene.CanonicalJson, cancellationToken);
                await WriteCanonicalAsync(Path.Combine(outputFullPath, sample.FactsPath),
                    sample.Analysis.Facts.CanonicalJson, cancellationToken);
                await WriteCanonicalAsync(Path.Combine(outputFullPath, sample.NarrativePath),
                    sample.Analysis.Narrative.CanonicalJson, cancellationToken);
            }
            await WriteJsonAsync(Path.Combine(outputFullPath, "scenes-index.json"), new
            {
                schemaVersion = "situation-stage3-scenes-index-v1",
                role = "holdout",
                splitSha256 = request.ExpectedSplitSha256,
                holdoutRequestSha256 = requestSha256,
                samples = samples.Select(item => new
                {
                    item.Ordinal,
                    item.DemoRef,
                    item.Tick,
                    item.Category,
                    item.ScenePath,
                    item.FactsPath,
                    item.NarrativePath,
                    sceneSha256 = item.Scene.Sha256,
                    factsSha256 = item.Analysis.Facts.Sha256,
                    narrativeSha256 = item.Analysis.Narrative.Sha256
                })
            }, cancellationToken);
            await WriteMarginsAsync(Path.Combine(outputFullPath, "rule-margins.jsonl"),
                samples, cancellationToken);
            await WriteJsonAsync(Path.Combine(outputFullPath, "distribution-report.json"),
                BuildDistribution(samples), cancellationToken);
            await File.WriteAllTextAsync(Path.Combine(outputFullPath, "review.md"),
                BuildReview(samples), new UTF8Encoding(false), cancellationToken);

            var hot = MeasureHot(analyzer, samples[0].Scene.Scene);
            var combined = MeasureCombined(
                analyzer, sceneService, performanceTimeline!, performanceDemoRef!, performanceTick);
            await WriteJsonAsync(Path.Combine(outputFullPath, "performance.json"), new
            {
                schemaVersion = "situation-stage3-performance-v1",
                hot,
                combined,
                gatePassed = hot.GatePassed && combined.GatePassed
            }, cancellationToken);
            if (!hot.GatePassed || !combined.GatePassed)
                throw new InvalidDataException("Stage-three acceptance performance gate failed.");

            await WriteJsonAsync(Path.Combine(outputFullPath, "provenance.json"), new
            {
                schemaVersion = "situation-stage3-provenance-v1",
                baseCommit = TryReadGitHead(repositoryRoot),
                requestSha256,
                splitSha256 = request.ExpectedSplitSha256,
                ruleVersion = ruleLoad.Rules.AnalysisRuleVersion,
                ruleConfigSha256 = ruleLoad.Sha256,
                holdoutRuleOutputsObserved = true,
                firstFormalHoldoutEvaluation = true
            }, cancellationToken);
            await VerifyOutputAsync(outputFullPath, samples, analyzer, cancellationToken);
            var files = await InventoryFilesAsync(outputFullPath, cancellationToken, "manifest.json");
            await WriteJsonAsync(manifestPath, new
            {
                schemaVersion = ManifestVersion,
                status = "complete",
                completedAtUtc = DateTimeOffset.UtcNow,
                role = "holdout",
                demoCount = 8,
                sampleCount = 16,
                splitSha256 = request.ExpectedSplitSha256,
                holdoutRequestSha256 = requestSha256,
                ruleVersion = ruleLoad.Rules.AnalysisRuleVersion,
                ruleConfigSha256 = ruleLoad.Sha256,
                performanceGatePassed = true,
                holdoutRuleOutputsObserved = true,
                files
            }, cancellationToken);
            Console.WriteLine($"Stage-three acceptance complete: 16 holdout scenes to {outputFullPath}");
        }
        catch
        {
            Console.Error.WriteLine($"Stage-three acceptance is incomplete: {outputFullPath}");
            throw;
        }
    }

    private static void ValidateExactHoldoutMembership(
        SituationStageThreeHoldoutRequestBuilder.HoldoutRequest request,
        SituationFrozenDataset split)
    {
        var requested = request.Demos.Select(item => item.FileName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!requested.SetEquals(split.ValidationFiles))
            throw new InvalidDataException("Holdout request membership differs from the frozen eight-demo split.");
        foreach (var demo in request.Demos)
            if (!split.MatchIds.TryGetValue(demo.FileName, out var hash) || hash != demo.ExpectedSha256)
                throw new InvalidDataException("Holdout request and frozen manifest disagree.");
    }

    private static async Task ValidateCalibrationCommitmentAsync(
        string requestPath,
        string requestSha256,
        SituationAnalysisRuleLoadResult ruleLoad,
        CancellationToken cancellationToken)
    {
        var manifestPath = Path.Combine(Path.GetDirectoryName(requestPath)!, "manifest.json");
        if (!File.Exists(manifestPath))
            throw new InvalidDataException("Holdout request must come from a completed training calibration artifact.");
        await using var stream = File.OpenRead(manifestPath);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var root = document.RootElement;
        if (root.GetProperty("status").GetString() != "complete" ||
            root.GetProperty("role").GetString() != "training" ||
            root.GetProperty("holdoutRequestSha256").GetString() != requestSha256 ||
            root.GetProperty("ruleVersion").GetString() != ruleLoad.Rules.AnalysisRuleVersion ||
            root.GetProperty("ruleConfigSha256").GetString() != ruleLoad.Sha256 ||
            root.GetProperty("holdoutRuleOutputsObserved").GetBoolean())
            throw new InvalidDataException("Training calibration holdout commitment is incomplete or invalid.");
    }

    private static void ValidateCommittedCategory(DemoFrame frame, string category)
    {
        var prePlant = frame.Round.Phase == "live" &&
                       frame.Bomb.State is "carried" or "dropped" or "planting";
        var postPlant = frame.Round.Phase == "post-plant" ||
                        frame.Bomb.State is "planted" or "defusing";
        if (category == "pre-plant" && !prePlant ||
            category == "post-plant" && !postPlant ||
            category == "live-fallback" && frame.Round.Phase is not ("live" or "post-plant"))
            throw new InvalidDataException("Committed holdout tick no longer matches its phase category.");
    }

    private static async Task VerifyOutputAsync(
        string outputDirectory,
        IReadOnlyList<AcceptanceSample> samples,
        SituationDeterministicAnalyzer analyzer,
        CancellationToken cancellationToken)
    {
        foreach (var sample in samples)
        {
            var sceneText = await ReadUtf8NoBomAsync(Path.Combine(outputDirectory, sample.ScenePath), cancellationToken);
            var factsText = await ReadUtf8NoBomAsync(Path.Combine(outputDirectory, sample.FactsPath), cancellationToken);
            var narrativeText = await ReadUtf8NoBomAsync(Path.Combine(outputDirectory, sample.NarrativePath), cancellationToken);
            var scene = SituationCanonicalJson.Deserialize<MinimapSceneV1>(sceneText);
            var facts = SituationCanonicalJson.Deserialize<SituationFactsV1>(factsText);
            var narrative = SituationCanonicalJson.Deserialize<SituationNarrativeV1>(narrativeText);
            SituationContractValidator.Validate(scene);
            SituationContractValidator.Validate(facts, scene, analyzer.RuleLoad.Rules.AnalysisRuleVersion);
            SituationContractValidator.ValidateTemplate(narrative, facts);
            if (sceneText != SituationCanonicalJson.Serialize(scene) ||
                factsText != SituationCanonicalJson.Serialize(facts) ||
                narrativeText != SituationCanonicalJson.Serialize(narrative) ||
                SituationCanonicalJson.Sha256(scene) != sample.Scene.Sha256 ||
                SituationCanonicalJson.Sha256(facts) != sample.Analysis.Facts.Sha256 ||
                SituationCanonicalJson.Sha256(narrative) != sample.Analysis.Narrative.Sha256)
                throw new InvalidDataException("Stage-three acceptance artifact failed canonical verification.");
        }
    }

    private static async Task WriteMarginsAsync(
        string path,
        IReadOnlyList<AcceptanceSample> samples,
        CancellationToken cancellationToken)
    {
        var lines = samples.Select(sample => JsonSerializer.Serialize(new
        {
            sample.Ordinal,
            sample.DemoRef,
            sample.Tick,
            sample.Analysis.Facts.Diagnostics.Decisions,
            sample.Analysis.Facts.Diagnostics.Margins,
            sample.Analysis.Facts.Diagnostics.SpatialComponents,
            sample.Analysis.Facts.Diagnostics.SpatialScore,
            sample.Analysis.Facts.Diagnostics.UnknownCount,
            sample.Analysis.Facts.Diagnostics.HasRelevantError,
            sample.Analysis.Facts.Diagnostics.HasRelevantWarning
        }, ReadOptions));
        await File.WriteAllTextAsync(path, string.Join('\n', lines) + "\n",
            new UTF8Encoding(false), cancellationToken);
    }

    private static object BuildDistribution(IReadOnlyList<AcceptanceSample> samples) => new
    {
        schemaVersion = "situation-stage3-distribution-v1",
        role = "holdout",
        sampleCount = samples.Count,
        values = new
        {
            formationT = Counts(samples.Select(item => item.Analysis.Facts.Facts.Formation.T.ToString())),
            formationCT = Counts(samples.Select(item => item.Analysis.Facts.Facts.Formation.CT.ToString())),
            pressureSite = Counts(samples.Select(item => item.Analysis.Facts.Facts.Pressure.Site?.ToString() ?? "none")),
            contactRisk = Counts(samples.Select(item => item.Analysis.Facts.Facts.ContactRisk.ToString())),
            isolatedSide = Counts(samples.Select(item => item.Analysis.Facts.Facts.IsolatedSide.ToString())),
            spatialAdvantage = Counts(samples.Select(item => item.Analysis.Facts.Facts.SpatialAdvantage.ToString())),
            confidence = Counts(samples.Select(item => item.Analysis.Facts.Facts.Confidence.ToString()))
        }
    };

    private static IReadOnlyDictionary<string, int> Counts(IEnumerable<string> values) => values
        .GroupBy(value => value, StringComparer.Ordinal)
        .OrderBy(group => group.Key, StringComparer.Ordinal)
        .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);

    private static string BuildReview(IReadOnlyList<AcceptanceSample> samples)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# 阶段三冻结规则保留集验收");
        builder.AppendLine();
        builder.AppendLine("> 该报告只允许在规则冻结后生成。任一直接事实、身份、未来信息、胜率混淆或明显派生规则错误均阻断阶段四。");
        builder.AppendLine();
        builder.AppendLine("| 已复核 | 样例 | Demo | Tick | 阶段 | 直接事实 | 六类规则 | 置信度 | Evidence | 模板 | 阻断问题 |");
        builder.AppendLine("| --- | --- | --- | ---: | --- | --- | --- | --- | --- | --- | --- |");
        foreach (var sample in samples)
            builder.AppendLine($"| [ ] | `{sample.Ordinal:D3}` | `{sample.DemoRef}` | {sample.Tick} | {sample.Category} | [ ] | [ ] | [ ] | [ ] | [ ] |  |");
        return builder.ToString();
    }

    private static PerformanceResult MeasureHot(
        SituationDeterministicAnalyzer analyzer,
        MinimapSceneV1 scene)
    {
        for (var index = 0; index < 10; index++) analyzer.Analyze(scene);
        return Measure("facts-plus-template-hot", 10, () => analyzer.Analyze(scene));
    }

    private static PerformanceResult MeasureCombined(
        SituationDeterministicAnalyzer analyzer,
        SituationSceneService sceneService,
        DemoTimeline timeline,
        string demoRef,
        int tick)
    {
        var windowIndex = tick / (DemoImportService.WindowSeconds * timeline.Metadata.TickRate);
        for (var index = 0; index < 10; index++)
            analyzer.Analyze(sceneService.BuildFromTimeline(
                timeline, demoRef, windowIndex, tick).Scene);
        return Measure("scene-cache-miss-plus-facts-plus-template", 100, () =>
            analyzer.Analyze(sceneService.BuildFromTimeline(
                timeline, demoRef, windowIndex, tick).Scene));
    }

    private static PerformanceResult Measure(string mode, double gate, Action action)
    {
        var values = new double[PerformanceIterations];
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var index = 0; index < values.Length; index++)
        {
            var stopwatch = Stopwatch.StartNew();
            action();
            stopwatch.Stop();
            values[index] = stopwatch.Elapsed.TotalMilliseconds;
        }
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Array.Sort(values);
        var p95 = Quantile(values, .95);
        return new(mode, PerformanceIterations, Quantile(values, .50), p95, values[^1],
            allocated, allocated / (double)PerformanceIterations, gate, p95 <= gate);
    }

    private static double Quantile(IReadOnlyList<double> ordered, double quantile)
    {
        var index = (int)Math.Ceiling(quantile * ordered.Count) - 1;
        return Math.Round(ordered[Math.Clamp(index, 0, ordered.Count - 1)], 6,
            MidpointRounding.AwayFromZero);
    }

    private static async Task<T> ReadJsonAsync<T>(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        try
        {
            return await JsonSerializer.DeserializeAsync<T>(stream, ReadOptions, cancellationToken)
                ?? throw new InvalidDataException("Stage-three acceptance request is empty.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Stage-three acceptance request is invalid JSON.", exception);
        }
    }

    private static async Task<string> ReadUtf8NoBomAsync(string path, CancellationToken cancellationToken)
    {
        var bytes = await File.ReadAllBytesAsync(path, cancellationToken);
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            throw new InvalidDataException("Stage-three output contains a UTF-8 BOM.");
        return new UTF8Encoding(false, true).GetString(bytes);
    }

    private sealed record AcceptanceSample(
        int Ordinal,
        string DemoRef,
        int Tick,
        string Category,
        string ScenePath,
        string FactsPath,
        string NarrativePath,
        SituationSceneBuildResult Scene,
        SituationDeterministicAnalysisResult Analysis);

    private sealed record PerformanceResult(
        string Mode,
        int Iterations,
        double P50Milliseconds,
        double P95Milliseconds,
        double MaxMilliseconds,
        long AllocatedBytes,
        double AllocatedBytesPerIteration,
        double GateMilliseconds,
        bool GatePassed);
}

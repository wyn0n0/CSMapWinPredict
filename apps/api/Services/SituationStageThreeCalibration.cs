using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CsDemoMap.Api.Models;
using static CsDemoMap.Api.Services.SituationArtifactIO;

namespace CsDemoMap.Api.Services;

internal static class SituationStageThreeCalibration
{
    internal const string RequestVersion = "situation-stage3-calibration-request-v1";
    internal const string ManifestVersion = "situation-stage3-calibration-manifest-v1";
    private const int RequiredDemoCount = 5;
    private const int RequiredSamples = 30;
    private const int RequiredIterations = 200;

    private static readonly JsonSerializerOptions ReadOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    private static readonly IReadOnlyDictionary<string, int> BucketTargets =
        new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["formation.grouped"] = 4,
            ["formation.split"] = 3,
            ["formation.spread"] = 3,
            ["formation.unknown"] = 2,
            ["pressure.A"] = 2,
            ["pressure.B"] = 2,
            ["pressure.none"] = 6,
            ["pressure.unknown"] = 2,
            ["contact.low"] = 4,
            ["contact.medium"] = 4,
            ["contact.high"] = 4,
            ["contact.unknown"] = 2,
            ["isolation.T"] = 2,
            ["isolation.CT"] = 2,
            ["isolation.both"] = 1,
            ["isolation.none"] = 6,
            ["isolation.unknown"] = 2,
            ["spatial.T"] = 3,
            ["spatial.CT"] = 3,
            ["spatial.even"] = 3,
            ["spatial.uncertain"] = 3,
            ["phase.live"] = 12,
            ["phase.post-plant"] = 5,
            ["bomb.dropped"] = 2,
            ["bomb.planted"] = 3,
            ["bomb.defusing"] = 1,
            ["clutch"] = 4,
            ["region-unknown"] = 2,
            ["history-limited"] = 2,
            ["near-threshold"] = 4
        };

    public static async Task RunAsync(
        string requestPath,
        string outputPath,
        CancellationToken cancellationToken)
    {
        var requestFullPath = Path.GetFullPath(requestPath);
        var outputFullPath = Path.GetFullPath(outputPath);
        EnsureNewOutput(outputFullPath);
        var request = await ReadJsonAsync<CalibrationRequest>(requestFullPath, cancellationToken);
        ValidateRequest(request);
        var requestDirectory = Path.GetDirectoryName(requestFullPath)!;
        var splitPath = ResolvePath(requestDirectory, request.SplitFile);
        var split = await LoadFrozenSplitAsync(splitPath, request.ExpectedSplitSha256, cancellationToken);
        var ruleLoad = request.ExpectedRuleVersion == "situation-analysis-rules-v1"
            ? SituationAnalysisRuleLoader.LoadFrozen()
            : SituationAnalysisRuleLoader.LoadCandidate();
        if (ruleLoad.Rules.AnalysisRuleVersion != request.ExpectedRuleVersion ||
            ruleLoad.Sha256 != request.ExpectedRuleConfigSha256)
            throw new InvalidDataException("Calibration request does not match the requested embedded rules.");
        foreach (var demo in request.Demos)
        {
            if (split.ValidationFiles.Contains(demo.FileName))
                throw new InvalidDataException("Calibration request contains a holdout demo.");
            if (!split.MatchIds.TryGetValue(demo.FileName, out var matchId) || matchId != demo.ExpectedSha256)
                throw new InvalidDataException($"Calibration demo is absent from the frozen manifest: {demo.FileName}");
        }

        Directory.CreateDirectory(outputFullPath);
        var scenesDirectory = Path.Combine(outputFullPath, "scenes");
        var factsDirectory = Path.Combine(outputFullPath, "facts");
        var narrativesDirectory = Path.Combine(outputFullPath, "narratives");
        Directory.CreateDirectory(scenesDirectory);
        Directory.CreateDirectory(factsDirectory);
        Directory.CreateDirectory(narrativesDirectory);
        var manifestPath = Path.Combine(outputFullPath, "manifest.json");
        await WriteJsonAsync(manifestPath, new
        {
            schemaVersion = ManifestVersion,
            status = "incomplete",
            ruleVersion = ruleLoad.Rules.AnalysisRuleVersion,
            ruleConfigSha256 = ruleLoad.Sha256
        }, cancellationToken);

        try
        {
            await WriteJsonAsync(Path.Combine(outputFullPath, "request.json"), request, cancellationToken);
            await WriteCanonicalAsync(Path.Combine(outputFullPath, "rules.json"), ruleLoad.CanonicalJson, cancellationToken);
            var analyzer = new SituationDeterministicAnalyzer(ruleLoad);
            var sceneService = new SituationSceneService();
            var selected = new List<SelectedSample>();
            var bucketCounts = new Dictionary<string, int>(StringComparer.Ordinal);
            DemoTimeline? performanceTimeline = null;
            string? performanceDemoRef = null;
            int performanceTick = 0;
            var demoOrdinal = 0;
            foreach (var demo in request.Demos.OrderBy(item => item.FileName, StringComparer.OrdinalIgnoreCase))
            {
                cancellationToken.ThrowIfCancellationRequested();
                demoOrdinal++;
                Console.WriteLine($"Stage-three calibration parsing training demo {demoOrdinal}/{request.Demos.Count}: {demo.FileName}");
                var sourcePath = Path.GetFullPath(Path.Combine(split.SourceDirectory, demo.FileName));
                EnsureDirectChild(
                    split.SourceDirectory,
                    sourcePath,
                    "Demo must be a direct child of the frozen source directory.");
                var actualSha256 = await FileSha256Async(sourcePath, cancellationToken);
                if (actualSha256 != demo.ExpectedSha256)
                    throw new InvalidDataException($"Calibration demo hash mismatch: {demo.FileName}");
                await using var source = File.OpenRead(sourcePath);
                var timeline = await new DemoParserService().ParseAsync(
                    source, demo.FileName, cancellationToken, collectSemantics: true);
                var demoRef = $"demo-{actualSha256[..12]}";
                var candidates = new List<CalibrationCandidate>();
                foreach (var tick in CandidateTicks(
                             timeline, demo.SeedTicks, demo.ExpectedSamples.Select(item => item.Tick).ToArray()))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var windowIndex = tick / (DemoImportService.WindowSeconds * timeline.Metadata.TickRate);
                    var sceneResult = sceneService.BuildFromTimeline(
                        timeline, demoRef, windowIndex, tick, cancellationToken: cancellationToken);
                    var analysis = analyzer.Analyze(sceneResult.Scene);
                    var buckets = Buckets(sceneResult.Scene, analysis, ruleLoad.Rules);
                    candidates.Add(new(
                        demo.FileName,
                        demoRef,
                        actualSha256,
                        tick,
                        demo.SeedTicks.Contains(tick),
                        StableKey(actualSha256, tick),
                        buckets,
                        sceneResult.Scene,
                        sceneResult.CanonicalJson,
                        sceneResult.Sha256,
                        analysis));
                }
                foreach (var expected in demo.ExpectedSamples)
                {
                    var actual = candidates.Single(item => item.Tick == expected.Tick);
                    if (actual.SceneSha256 != expected.SceneSha256)
                        throw new InvalidDataException(
                            $"Pinned calibration scene hash mismatch: {demo.FileName} tick {expected.Tick}");
                }
                var chosen = demo.ExpectedSamples.Count == 0
                    ? SelectForDemo(candidates, demo.SampleCount, bucketCounts)
                    : demo.ExpectedSamples.OrderBy(item => item.Tick)
                        .Select(expected => candidates.Single(item => item.Tick == expected.Tick))
                        .ToArray();
                foreach (var item in chosen)
                {
                    foreach (var bucket in item.Buckets)
                        bucketCounts[bucket] = bucketCounts.GetValueOrDefault(bucket) + 1;
                    selected.Add(ToSelected(item, selected.Count + 1));
                }
                if (performanceTimeline is null)
                {
                    performanceTimeline = timeline;
                    performanceDemoRef = demoRef;
                    performanceTick = chosen[0].Tick;
                }
                Console.WriteLine($"Stage-three calibration selected {chosen.Count} scenes from {demo.FileName}.");
            }

            if (selected.Count != request.TotalSampleCount ||
                selected.Select(item => item.DemoRef).Distinct(StringComparer.Ordinal).Count() < RequiredDemoCount)
                throw new InvalidDataException("Calibration selection did not produce 30 scenes across five demos.");

            foreach (var sample in selected)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await WriteCanonicalAsync(Path.Combine(outputFullPath, sample.ScenePath),
                    sample.SceneCanonicalJson, cancellationToken);
                await WriteCanonicalAsync(Path.Combine(outputFullPath, sample.FactsPath),
                    sample.Analysis.Facts.CanonicalJson, cancellationToken);
                await WriteCanonicalAsync(Path.Combine(outputFullPath, sample.NarrativePath),
                    sample.Analysis.Narrative.CanonicalJson, cancellationToken);
            }

            await WriteJsonAsync(Path.Combine(outputFullPath, "scenes-index.json"), new
            {
                schemaVersion = "situation-stage3-scenes-index-v1",
                splitSha256 = request.ExpectedSplitSha256,
                samples = selected.Select(item => new
                {
                    item.Ordinal,
                    item.ScenePath,
                    item.FactsPath,
                    item.NarrativePath,
                    item.DemoRef,
                    item.Role,
                    item.Tick,
                    item.Seed,
                    item.Buckets,
                    item.SceneSha256,
                    factsSha256 = item.Analysis.Facts.Sha256,
                    narrativeSha256 = item.Analysis.Narrative.Sha256
                })
            }, cancellationToken);

            await WriteMarginsAsync(
                Path.Combine(outputFullPath, "rule-margins.jsonl"), selected, cancellationToken);
            await WriteJsonAsync(Path.Combine(outputFullPath, "distribution-report.json"),
                BuildDistribution(selected, bucketCounts), cancellationToken);
            await WriteJsonAsync(Path.Combine(outputFullPath, "candidate-comparison.json"),
                BuildCandidateComparison(selected, ruleLoad), cancellationToken);
            await File.WriteAllTextAsync(
                Path.Combine(outputFullPath, "review.md"),
                BuildReview(selected), new UTF8Encoding(false), cancellationToken);
            var hotPerformance = MeasurePerformance(analyzer, selected[0].Scene, RequiredIterations);
            var combinedPerformance = MeasureCombinedPerformance(
                analyzer, sceneService, performanceTimeline!, performanceDemoRef!, performanceTick,
                RequiredIterations);
            var performance = new
            {
                schemaVersion = "situation-stage3-performance-v1",
                configurationLoad = "excluded; embedded rules loaded and validated once before measurement",
                hot = hotPerformance,
                combined = combinedPerformance,
                gatePassed = hotPerformance.GatePassed && combinedPerformance.GatePassed
            };
            await WriteJsonAsync(Path.Combine(outputFullPath, "performance.json"), performance, cancellationToken);
            if (!performance.gatePassed)
                throw new InvalidDataException("Stage-three calibration performance gate failed.");

            var repositoryRoot = FindRepositoryRoot(requestDirectory);
            var holdoutRequest = await SituationStageThreeHoldoutRequestBuilder.BuildAsync(
                split,
                Path.GetRelativePath(repositoryRoot, splitPath),
                request.ExpectedSplitSha256,
                cancellationToken);
            await WriteCanonicalAsync(Path.Combine(outputFullPath, "stage3-holdout-request-v1.json"),
                holdoutRequest.CanonicalJson, cancellationToken);

            await WriteJsonAsync(Path.Combine(outputFullPath, "provenance.json"),
                await BuildProvenanceAsync(
                    repositoryRoot, requestFullPath, splitPath, ruleLoad,
                    holdoutRequest.Sha256,
                    cancellationToken), cancellationToken);

            await VerifyOutputAsync(outputFullPath, selected, analyzer, cancellationToken);
            var files = await InventoryFilesAsync(outputFullPath, cancellationToken, "manifest.json");
            await WriteJsonAsync(manifestPath, new
            {
                schemaVersion = ManifestVersion,
                status = "complete",
                completedAtUtc = DateTimeOffset.UtcNow,
                splitSha256 = request.ExpectedSplitSha256,
                ruleVersion = ruleLoad.Rules.AnalysisRuleVersion,
                ruleConfigSha256 = ruleLoad.Sha256,
                sceneSchemaVersion = SituationContractVersions.Scene,
                sceneBuilderVersion = SituationSceneBuilder.BuilderVersion,
                geometryVersion = SituationSceneBuilder.GeometryVersion,
                factsSchemaVersion = SituationContractVersions.Facts,
                narrativeSchemaVersion = SituationContractVersions.Narrative,
                role = "training",
                demoCount = selected.Select(item => item.DemoRef).Distinct(StringComparer.Ordinal).Count(),
                sampleCount = selected.Count,
                performanceGatePassed = performance.gatePassed,
                holdoutRequestSha256 = holdoutRequest.Sha256,
                holdoutRuleOutputsObserved = false,
                files
            }, cancellationToken);
            Console.WriteLine($"Stage-three calibration complete: {selected.Count} training scenes to {outputFullPath}");
        }
        catch
        {
            Console.Error.WriteLine($"Stage-three calibration is incomplete: {outputFullPath}");
            throw;
        }
    }

    internal static void ValidateRequest(CalibrationRequest request)
    {
        if (request.SchemaVersion != RequestVersion)
            throw new InvalidDataException("Unsupported stage-three calibration request version.");
        if (string.IsNullOrWhiteSpace(request.SplitFile) ||
            !IsSha256(request.ExpectedSplitSha256))
            throw new InvalidDataException("Calibration split reference is invalid.");
        if (string.IsNullOrWhiteSpace(request.ExpectedRuleVersion) ||
            !IsSha256(request.ExpectedRuleConfigSha256))
            throw new InvalidDataException("Calibration rule reference is invalid.");
        if (request.TotalSampleCount != RequiredSamples ||
            request.MaxSamplesPerDemo != RequiredSamples / RequiredDemoCount ||
            request.Demos is null || request.Demos.Count != RequiredDemoCount ||
            request.Demos.Any(item => item.SampleCount != request.MaxSamplesPerDemo))
            throw new InvalidDataException("Calibration requires five demos and six samples per demo.");
        if (request.Demos.Select(item => item.FileName).Distinct(StringComparer.OrdinalIgnoreCase).Count() !=
            request.Demos.Count)
            throw new InvalidDataException("Calibration demos must be unique.");
        foreach (var demo in request.Demos)
        {
            if (string.IsNullOrWhiteSpace(demo.FileName) || Path.GetFileName(demo.FileName) != demo.FileName ||
                !IsSha256(demo.ExpectedSha256))
                throw new InvalidDataException("Calibration demo reference is invalid.");
            if (demo.SeedTicks is null || demo.ExpectedSamples is null)
                throw new InvalidDataException("Calibration demo sample lists are missing.");
            if (demo.SeedTicks.Any(tick => tick < 0) ||
                demo.SeedTicks.Distinct().Count() != demo.SeedTicks.Count ||
                demo.SeedTicks.Count > demo.SampleCount)
                throw new InvalidDataException("Calibration seed ticks are invalid.");
            if (demo.ExpectedSamples.Count != demo.SampleCount ||
                demo.ExpectedSamples.Select(item => item.Tick).Distinct().Count() != demo.SampleCount ||
                demo.ExpectedSamples.Any(item => item.Tick < 0 || !IsSha256(item.SceneSha256)))
                throw new InvalidDataException("Calibration request must pin every selected tick and scene hash.");
            if (demo.SeedTicks.Any(tick => demo.ExpectedSamples.All(item => item.Tick != tick)))
                throw new InvalidDataException("Every calibration seed tick must be part of the pinned sample set.");
        }
    }

    private static IReadOnlyList<int> CandidateTicks(
        DemoTimeline timeline,
        IReadOnlyList<int> seedTicks,
        IReadOnlyList<int> expectedTicks)
    {
        var validTicks = timeline.Frames.Select(frame => frame.Tick).ToHashSet();
        if (seedTicks.Concat(expectedTicks).Any(tick => !validTicks.Contains(tick)))
            throw new InvalidDataException("Calibration seed or pinned tick is absent from its demo.");
        if (expectedTicks.Count > 0)
            return expectedTicks.Distinct().Order().ToArray();
        var usable = timeline.Frames
            .Where(frame => frame.Round.Phase is "live" or "post-plant")
            .OrderBy(frame => frame.Tick)
            .GroupBy(frame => frame.Tick / timeline.Metadata.TickRate)
            .Select(group => group.First())
            .ToArray();
        var sampled = EvenlySpaced(usable, 140).Select(frame => frame.Tick);
        var interesting = usable.Where(frame =>
                frame.Bomb.State is "dropped" or "planting" or "planted" or "defusing" ||
                frame.Players.Count(player => player.Team == "T" && player.Alive) <= 2 ||
                frame.Players.Count(player => player.Team == "CT" && player.Alive) <= 2)
            .GroupBy(frame => frame.Round.Number)
            .SelectMany(group => EvenlySpaced(group.ToArray(), 3))
            .Select(frame => frame.Tick);
        return seedTicks.Concat(sampled).Concat(interesting)
            .Distinct()
            .Order()
            .ToArray();
    }

    private static IReadOnlyList<CalibrationCandidate> SelectForDemo(
        IReadOnlyList<CalibrationCandidate> candidates,
        int count,
        IReadOnlyDictionary<string, int> existingCounts)
    {
        var selected = candidates.Where(item => item.Seed)
            .OrderBy(item => item.Tick)
            .Take(count)
            .ToList();
        var counts = new Dictionary<string, int>(existingCounts, StringComparer.Ordinal);
        foreach (var item in selected)
            foreach (var bucket in item.Buckets)
                counts[bucket] = counts.GetValueOrDefault(bucket) + 1;
        while (selected.Count < count)
        {
            var next = candidates
                .Where(item => selected.All(current => current.Tick != item.Tick))
                .OrderByDescending(item => CoverageScore(item.Buckets, counts))
                .ThenBy(item => item.StableKey, StringComparer.Ordinal)
                .FirstOrDefault()
                ?? throw new InvalidDataException("Calibration demo has too few usable candidates.");
            selected.Add(next);
            foreach (var bucket in next.Buckets)
                counts[bucket] = counts.GetValueOrDefault(bucket) + 1;
        }
        return selected.OrderBy(item => item.Tick).ToArray();
    }

    private static int CoverageScore(
        IReadOnlyList<string> buckets,
        IReadOnlyDictionary<string, int> counts) => buckets.Sum(bucket =>
        BucketTargets.TryGetValue(bucket, out var target)
            ? Math.Max(0, target - counts.GetValueOrDefault(bucket))
            : 0);

    private static IReadOnlyList<string> Buckets(
        MinimapSceneV1 scene,
        SituationDeterministicAnalysisResult analysis,
        SituationAnalysisRuleSet rules)
    {
        var facts = analysis.Facts.Facts;
        var values = new HashSet<string>(StringComparer.Ordinal)
        {
            $"formation.{facts.Formation.T.ToString().ToLowerInvariant()}",
            $"formation.{facts.Formation.CT.ToString().ToLowerInvariant()}",
            $"contact.{facts.ContactRisk.ToString().ToLowerInvariant()}",
            $"phase.{RoundPhase(scene.Round.Phase)}",
            $"bomb.{facts.Bomb.State.ToString().ToLowerInvariant()}"
        };
        values.Add(facts.Pressure.Site is { } pressureSite
            ? $"pressure.{pressureSite}"
            : analysis.Facts.Diagnostics.Decisions.GetValueOrDefault("pressure") == "none"
                ? "pressure.none"
                : "pressure.unknown");
        values.Add(facts.IsolatedSide switch
        {
            SituationIsolatedSide.T => "isolation.T",
            SituationIsolatedSide.CT => "isolation.CT",
            _ => $"isolation.{facts.IsolatedSide.ToString().ToLowerInvariant()}"
        });
        values.Add(facts.SpatialAdvantage switch
        {
            SituationSpatialAdvantage.T => "spatial.T",
            SituationSpatialAdvantage.CT => "spatial.CT",
            _ => $"spatial.{facts.SpatialAdvantage.ToString().ToLowerInvariant()}"
        });
        if (facts.Alive.T is > 0 and <= 2 || facts.Alive.CT is > 0 and <= 2)
            values.Add("clutch");
        if (facts.DataQuality.Any(item => item.Code == SituationDataQualityCodes.RegionUnknown))
            values.Add("region-unknown");
        if (facts.DataQuality.Any(item => item.Code is SituationDataQualityCodes.HistoryGap or
                SituationDataQualityCodes.HistoryTruncated or SituationDataQualityCodes.HistoryFileBoundary or
                SituationDataQualityCodes.HistoryRoundBoundary))
            values.Add("history-limited");
        if (analysis.Facts.Diagnostics.Margins.Any(item =>
                item.Decision != "configured-radius" &&
                Math.Abs(item.Margin) <= rules.Confidence.BoundaryBand + rules.ComparisonEpsilon))
            values.Add("near-threshold");
        return values.Order(StringComparer.Ordinal).ToArray();
    }

    private static SelectedSample ToSelected(CalibrationCandidate candidate, int ordinal)
    {
        var stem = $"sample-{ordinal:D3}";
        return new(
            ordinal,
            candidate.DemoFile,
            candidate.DemoRef,
            candidate.SourceSha256,
            "training",
            candidate.Tick,
            candidate.Seed,
            candidate.Buckets,
            $"scenes/{stem}.json",
            $"facts/{stem}.json",
            $"narratives/{stem}.json",
            candidate.Scene,
            candidate.SceneCanonicalJson,
            candidate.SceneSha256,
            candidate.Analysis);
    }

    private static async Task WriteMarginsAsync(
        string path,
        IReadOnlyList<SelectedSample> samples,
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

    private static object BuildDistribution(
        IReadOnlyList<SelectedSample> samples,
        IReadOnlyDictionary<string, int> bucketCounts)
    {
        var margins = samples.SelectMany(sample => sample.Analysis.Facts.Diagnostics.Margins)
            .GroupBy(item => $"{item.Rule}:{item.Metric}", StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group =>
            {
                var values = group.Select(item => item.Value).Order().ToArray();
                var marginValues = group.Select(item => Math.Abs(item.Margin)).Order().ToArray();
                return new
                {
                    metric = group.Key,
                    count = values.Length,
                    min = values[0],
                    p50 = Quantile(values, 0.50),
                    p95 = Quantile(values, 0.95),
                    max = values[^1],
                    nearestBoundary = marginValues[0]
                };
            });
        return new
        {
            schemaVersion = "situation-stage3-distribution-v1",
            sampleCount = samples.Count,
            demoCount = samples.Select(item => item.DemoRef).Distinct(StringComparer.Ordinal).Count(),
            bucketTargets = BucketTargets,
            bucketCounts = bucketCounts.OrderBy(item => item.Key, StringComparer.Ordinal)
                .ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal),
            metrics = margins
        };
    }

    private static object BuildCandidateComparison(
        IReadOnlyList<SelectedSample> samples,
        SituationAnalysisRuleLoadResult current)
    {
        if (current.Rules.AnalysisRuleVersion == "situation-analysis-rules-v1-candidate-1")
        {
            return new
            {
                schemaVersion = "situation-stage3-candidate-comparison-v1",
                baseline = (object?)null,
                current = new { version = current.Rules.AnalysisRuleVersion, sha256 = current.Sha256 },
                changedSampleCount = 0,
                changes = Array.Empty<object>()
            };
        }
        var isFrozen = current.Rules.AnalysisRuleVersion == "situation-analysis-rules-v1";
        var baseline = SituationAnalysisRuleLoader.LoadEmbeddedForVerification(
            isFrozen
                ? "situation-analysis-rules-v1-candidate-2.json"
                : "situation-analysis-rules-v1-candidate-1.json");
        var analyzer = new SituationDeterministicAnalyzer(baseline);
        var changes = samples.Select(sample =>
            {
                var before = analyzer.Analyze(sample.Scene);
                var after = sample.Analysis;
                var fields = new List<string>();
                if (before.Facts.Facts.AnalysisRuleVersion != after.Facts.Facts.AnalysisRuleVersion)
                    fields.Add("analysisRuleVersion");
                if (before.Facts.Facts.Formation != after.Facts.Facts.Formation) fields.Add("formation");
                if (!Equals(before.Facts.Facts.Pressure, after.Facts.Facts.Pressure)) fields.Add("pressure");
                if (!Equals(before.Facts.Facts.ContestedRegions, after.Facts.Facts.ContestedRegions) &&
                    !(before.Facts.Facts.ContestedRegions?.SequenceEqual(after.Facts.Facts.ContestedRegions ?? []) ??
                      after.Facts.Facts.ContestedRegions is null)) fields.Add("contestedRegions");
                if (before.Facts.Facts.ContactRisk != after.Facts.Facts.ContactRisk) fields.Add("contactRisk");
                if (before.Facts.Facts.IsolatedSide != after.Facts.Facts.IsolatedSide) fields.Add("isolatedSide");
                if (before.Facts.Facts.SpatialAdvantage != after.Facts.Facts.SpatialAdvantage) fields.Add("spatialAdvantage");
                if (before.Facts.Facts.Confidence != after.Facts.Facts.Confidence) fields.Add("confidence");
                if (before.Narrative.Sha256 != after.Narrative.Sha256) fields.Add("narrative");
                return new
                {
                    sample.Ordinal,
                    sample.DemoRef,
                    sample.Tick,
                    fields,
                    beforeConfidence = before.Facts.Facts.Confidence,
                    afterConfidence = after.Facts.Facts.Confidence,
                    beforeFactsSha256 = before.Facts.Sha256,
                    afterFactsSha256 = after.Facts.Sha256,
                    beforeNarrativeSha256 = before.Narrative.Sha256,
                    afterNarrativeSha256 = after.Narrative.Sha256
                };
            })
            .Where(item => item.fields.Count > 0)
            .ToArray();
        return new
        {
            schemaVersion = "situation-stage3-candidate-comparison-v1",
            baseline = new { version = baseline.Rules.AnalysisRuleVersion, sha256 = baseline.Sha256 },
            current = new { version = current.Rules.AnalysisRuleVersion, sha256 = current.Sha256 },
            rationale = isFrozen
                ? "Frozen rules are byte-equivalent to the approved candidate-2 except for analysisRuleVersion."
                : "candidate-2 removes legacy-default-ambiguous equipment, velocity and money warnings from deterministic-analysis confidence impact; no thresholds or weights changed.",
            changedSampleCount = changes.Length,
            changes
        };
    }

    private static string BuildReview(IReadOnlyList<SelectedSample> samples)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# 阶段三训练侧规则校准复核");
        builder.AppendLine();
        builder.AppendLine("> 只含训练侧场景。所有字段需对照 scene、规则裕量和模板逐项复核；未勾选不表示通过。");
        builder.AppendLine();
        builder.AppendLine("| 已复核 | 样例 | Demo | Tick | 类别 | 直接事实 | 阵型 | 争夺/压力 | 接触/孤立 | 空间/置信度 | Evidence | 模板 | 问题 |");
        builder.AppendLine("| --- | --- | --- | ---: | --- | --- | --- | --- | --- | --- | --- | --- | --- |");
        foreach (var sample in samples)
            builder.AppendLine($"| [ ] | `{sample.Ordinal:D3}` | `{sample.DemoRef}` | {sample.Tick} | {string.Join(", ", sample.Buckets)} | [ ] | [ ] | [ ] | [ ] | [ ] | [ ] | [ ] |  |");
        builder.AppendLine();
        builder.AppendLine("## 阈值或模板变更记录");
        builder.AppendLine();
        builder.AppendLine("| 候选版本 | 受影响样例 | 变更前 | 变更后 | 理由 | 新工件目录 |");
        builder.AppendLine("| --- | --- | --- | --- | --- | --- |");
        return builder.ToString();
    }

    private static PerformanceReport MeasurePerformance(
        SituationDeterministicAnalyzer analyzer,
        MinimapSceneV1 scene,
        int iterations)
    {
        for (var index = 0; index < 10; index++)
            analyzer.Analyze(scene);
        var elapsed = new double[iterations];
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var index = 0; index < iterations; index++)
        {
            var stopwatch = Stopwatch.StartNew();
            analyzer.Analyze(scene);
            stopwatch.Stop();
            elapsed[index] = stopwatch.Elapsed.TotalMilliseconds;
        }
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Array.Sort(elapsed);
        var p95 = Quantile(elapsed, 0.95);
        return new(
            "situation-stage3-analysis-performance-v1",
            "facts-plus-template-hot",
            iterations,
            Quantile(elapsed, 0.50),
            p95,
            elapsed[^1],
            allocated,
            allocated / (double)iterations,
            10,
            10,
            p95 <= 10);
    }

    private static PerformanceReport MeasureCombinedPerformance(
        SituationDeterministicAnalyzer analyzer,
        SituationSceneService sceneService,
        DemoTimeline timeline,
        string demoRef,
        int tick,
        int iterations)
    {
        var windowIndex = tick / (DemoImportService.WindowSeconds * timeline.Metadata.TickRate);
        for (var index = 0; index < 10; index++)
            analyzer.Analyze(sceneService.BuildFromTimeline(
                timeline, demoRef, windowIndex, tick).Scene);
        var elapsed = new double[iterations];
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var index = 0; index < iterations; index++)
        {
            var stopwatch = Stopwatch.StartNew();
            analyzer.Analyze(sceneService.BuildFromTimeline(
                timeline, demoRef, windowIndex, tick).Scene);
            stopwatch.Stop();
            elapsed[index] = stopwatch.Elapsed.TotalMilliseconds;
        }
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Array.Sort(elapsed);
        var p95 = Quantile(elapsed, 0.95);
        return new(
            "situation-stage3-analysis-performance-v1",
            "scene-cache-miss-plus-facts-plus-template",
            iterations,
            Quantile(elapsed, 0.50),
            p95,
            elapsed[^1],
            allocated,
            allocated / (double)iterations,
            10,
            100,
            p95 <= 100);
    }

    private static async Task<object> BuildProvenanceAsync(
        string repositoryRoot,
        string requestPath,
        string splitPath,
        SituationAnalysisRuleLoadResult ruleLoad,
        string holdoutRequestSha256,
        CancellationToken cancellationToken)
    {
        var relativeInputs = new[]
        {
            "apps/api/CsDemoMap.Api.csproj",
            "apps/api/Models/SituationAnalysisRuleModels.cs",
            "apps/api/Models/SituationModels.cs",
            "apps/api/Services/SituationAnalysisRuleLoader.cs",
            "apps/api/Services/SituationFactsAnalyzer.cs",
            "apps/api/Services/SituationEvidenceBuilder.cs",
            "apps/api/Services/SituationTemplateNarrator.cs",
            "apps/api/Services/SituationContractValidator.cs"
        };
        var inputs = new List<object>();
        foreach (var relative in relativeInputs)
        {
            var fullPath = Path.Combine(repositoryRoot, relative.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(fullPath))
                inputs.Add(new { path = relative, sha256 = await FileSha256Async(fullPath, cancellationToken) });
        }
        return new
        {
            schemaVersion = "situation-stage3-provenance-v1",
            baseCommit = TryReadGitHead(repositoryRoot),
            requestSha256 = await FileSha256Async(requestPath, cancellationToken),
            splitSha256 = await FileSha256Async(splitPath, cancellationToken),
            ruleVersion = ruleLoad.Rules.AnalysisRuleVersion,
            ruleConfigSha256 = ruleLoad.Sha256,
            holdoutRequestSha256,
            holdoutRuleOutputsObserved = false,
            implementationInputs = inputs
        };
    }

    private static async Task VerifyOutputAsync(
        string outputDirectory,
        IReadOnlyList<SelectedSample> samples,
        SituationDeterministicAnalyzer analyzer,
        CancellationToken cancellationToken)
    {
        var strictUtf8 = new UTF8Encoding(false, true);
        foreach (var sample in samples)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sceneText = strictUtf8.GetString(await ReadNoBomAsync(
                Path.Combine(outputDirectory, sample.ScenePath), cancellationToken));
            var factsText = strictUtf8.GetString(await ReadNoBomAsync(
                Path.Combine(outputDirectory, sample.FactsPath), cancellationToken));
            var narrativeText = strictUtf8.GetString(await ReadNoBomAsync(
                Path.Combine(outputDirectory, sample.NarrativePath), cancellationToken));
            var scene = SituationCanonicalJson.Deserialize<MinimapSceneV1>(sceneText);
            var facts = SituationCanonicalJson.Deserialize<SituationFactsV1>(factsText);
            var narrative = SituationCanonicalJson.Deserialize<SituationNarrativeV1>(narrativeText);
            SituationContractValidator.Validate(scene);
            SituationContractValidator.Validate(facts, scene, analyzer.RuleLoad.Rules.AnalysisRuleVersion);
            SituationContractValidator.ValidateTemplate(narrative, facts);
            if (sceneText != SituationCanonicalJson.Serialize(scene) ||
                factsText != SituationCanonicalJson.Serialize(facts) ||
                narrativeText != SituationCanonicalJson.Serialize(narrative) ||
                SituationCanonicalJson.Sha256(scene) != sample.SceneSha256 ||
                SituationCanonicalJson.Sha256(facts) != sample.Analysis.Facts.Sha256 ||
                SituationCanonicalJson.Sha256(narrative) != sample.Analysis.Narrative.Sha256)
                throw new InvalidDataException($"Calibration output verification failed: {sample.Ordinal:D3}");
        }
    }

    internal static Task<SituationFrozenDataset> LoadFrozenSplitAsync(
        string splitPath,
        string expectedSha256,
        CancellationToken cancellationToken) =>
        SituationFrozenDatasetLoader.LoadAsync(splitPath, expectedSha256, cancellationToken);

    private static IEnumerable<DemoFrame> EvenlySpaced(IReadOnlyList<DemoFrame> values, int desired)
    {
        if (values.Count == 0 || desired <= 0)
            yield break;
        var actual = Math.Min(values.Count, desired);
        for (var index = 0; index < actual; index++)
        {
            var position = actual == 1
                ? values.Count / 2
                : (int)Math.Round(index * (values.Count - 1d) / (actual - 1), MidpointRounding.AwayFromZero);
            yield return values[position];
        }
    }

    private static string RoundPhase(SituationRoundPhase phase) => phase switch
    {
        SituationRoundPhase.PostPlant => "post-plant",
        _ => phase.ToString().ToLowerInvariant()
    };

    private static double Quantile(IReadOnlyList<double> ordered, double quantile)
    {
        var index = (int)Math.Ceiling(quantile * ordered.Count) - 1;
        return Math.Round(ordered[Math.Clamp(index, 0, ordered.Count - 1)], 6, MidpointRounding.AwayFromZero);
    }

    private static string StableKey(string demoSha256, int tick) => Convert.ToHexStringLower(
        SHA256.HashData(Encoding.UTF8.GetBytes($"{demoSha256}:{tick}")));

    private static async Task<T> ReadJsonAsync<T>(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        try
        {
            return await JsonSerializer.DeserializeAsync<T>(stream, ReadOptions, cancellationToken)
                ?? throw new InvalidDataException("Stage-three request is empty.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Stage-three request is invalid JSON.", exception);
        }
    }

    private static async Task<byte[]> ReadNoBomAsync(string path, CancellationToken cancellationToken)
    {
        var bytes = await File.ReadAllBytesAsync(path, cancellationToken);
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            throw new InvalidDataException($"Stage-three output contains a UTF-8 BOM: {Path.GetFileName(path)}");
        return bytes;
    }

    private static bool IsSha256(string? value) =>
        SituationArtifactIO.IsSha256(value, lowercaseOnly: true);

    internal sealed record CalibrationRequest(
        string SchemaVersion,
        string SplitFile,
        string ExpectedSplitSha256,
        string ExpectedRuleVersion,
        string ExpectedRuleConfigSha256,
        int TotalSampleCount,
        int MaxSamplesPerDemo,
        IReadOnlyList<CalibrationDemoRequest> Demos);

    internal sealed record CalibrationDemoRequest(
        string FileName,
        string ExpectedSha256,
        int SampleCount,
        IReadOnlyList<int> SeedTicks,
        IReadOnlyList<CalibrationExpectedSample> ExpectedSamples);

    internal sealed record CalibrationExpectedSample(int Tick, string SceneSha256);

    private sealed record CalibrationCandidate(
        string DemoFile,
        string DemoRef,
        string SourceSha256,
        int Tick,
        bool Seed,
        string StableKey,
        IReadOnlyList<string> Buckets,
        MinimapSceneV1 Scene,
        string SceneCanonicalJson,
        string SceneSha256,
        SituationDeterministicAnalysisResult Analysis);

    private sealed record SelectedSample(
        int Ordinal,
        string DemoFile,
        string DemoRef,
        string SourceSha256,
        string Role,
        int Tick,
        bool Seed,
        IReadOnlyList<string> Buckets,
        string ScenePath,
        string FactsPath,
        string NarrativePath,
        MinimapSceneV1 Scene,
        string SceneCanonicalJson,
        string SceneSha256,
        SituationDeterministicAnalysisResult Analysis);

    private sealed record PerformanceReport(
        string SchemaVersion,
        string Mode,
        int Iterations,
        double P50Milliseconds,
        double P95Milliseconds,
        double MaxMilliseconds,
        long AllocatedBytes,
        double AllocatedBytesPerIteration,
        int WarmupIterations,
        double GateMilliseconds,
        bool GatePassed);
}

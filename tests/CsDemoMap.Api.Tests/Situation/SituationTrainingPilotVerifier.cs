using System.Text;
using System.Text.Json;
using CsDemoMap.Api.Models;

namespace CsDemoMap.Api.Tests;

internal static class SituationTrainingPilotVerifier
{
    internal static void Verify()
    {
        var checks = 0;
        VerifyPromptRepresentation(ref checks);
        VerifyRoundRobinAllocation(ref checks);
        VerifyGreedyCoverage(ref checks);
        VerifyPilotManifest(ref checks);
        Console.WriteLine($"Stage-four pilot checks passed: {checks}");
    }

    internal static async Task VerifyArtifactsAsync(
        string firstDirectory,
        string? repeatDirectory,
        CancellationToken cancellationToken)
    {
        var checks = 0;
        var first = await VerifyArtifactAsync(firstDirectory, cancellationToken);
        checks += first.Checks;
        if (repeatDirectory is not null)
        {
            var repeat = await VerifyArtifactAsync(repeatDirectory, cancellationToken);
            checks += repeat.Checks;
            Check(first.FileHashes.Count == repeat.FileHashes.Count &&
                  first.FileHashes.All(item => repeat.FileHashes.TryGetValue(item.Key, out var hash) &&
                                               hash == item.Value),
                "repeat pilot non-manifest file hashes match", ref checks);
            var firstManifest = await SituationArtifactIO.FileSha256Async(
                Path.Combine(Path.GetFullPath(firstDirectory), "manifest.json"), cancellationToken);
            var repeatManifest = await SituationArtifactIO.FileSha256Async(
                Path.Combine(Path.GetFullPath(repeatDirectory), "manifest.json"), cancellationToken);
            Check(firstManifest == repeatManifest, "repeat pilot manifest hash matches", ref checks);
        }
        Console.WriteLine($"Stage-four pilot artifact checks passed: {checks}");
    }

    internal static async Task VerifySourceAsync(string demoPath, CancellationToken cancellationToken)
    {
        var fullPath = Path.GetFullPath(demoPath);
        await using var stream = new FileStream(
            fullPath, FileMode.Open, FileAccess.Read, FileShare.Read,
            1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var timeline = await new DemoParserService().ParseAsync(
            stream, Path.GetFileName(fullPath), cancellationToken, collectSemantics: true);
        using var sceneService = new SituationSceneService();
        var selector = new SituationTrainingCandidateSelector(sceneService);
        var eligibleBuilder = new SituationEligibleSceneBuilder(sceneService);
        var attempts = timeline.Semantics?.Attempts
            .Where(attempt => attempt.Disposition == "completed")
            .OrderBy(attempt => attempt.StartTick)
            .ToArray() ?? throw new InvalidDataException("Training source has no semantic collection.");
        var samples = 0;
        foreach (var attempt in attempts)
        {
            var selection = selector.SelectRound(
                timeline, "source-verification", attempt, cancellationToken).Selection;
            samples += selection.Samples.Count;
            foreach (var sample in selection.Samples)
            {
                var semantic = timeline.Semantics!.Frames.Single(frame =>
                    frame.Tick == sample.Tick && frame.RoundId == attempt.RoundId);
                var scene = eligibleBuilder.Build(
                    timeline,
                    "source-verification",
                    sample.Tick / checked(DemoImportService.WindowSeconds * timeline.Metadata.TickRate),
                    attempt,
                    semantic,
                    cancellationToken).Scene?.Scene
                    ?? throw new InvalidDataException("Selected source sample did not build a scene.");
                var expectedPhase = sample.Phase == SituationTrainingPhase.PostPlant
                    ? SituationRoundPhase.PostPlant
                    : SituationRoundPhase.Live;
                if (scene.Round.Phase != expectedPhase)
                    throw new InvalidDataException(
                        $"Selected source phase mismatch in round {attempt.RoundId} at tick {sample.Tick}.");
            }
        }
        Console.WriteLine(
            $"Stage-four pilot source checks passed: completedRounds={attempts.Length}, selectedSamples={samples}");
    }

    private static void VerifyPromptRepresentation(ref int checks)
    {
        var load = SituationPromptRepresentationLoader.LoadFrozen();
        var renderer = new SituationPromptRenderer(load);
        var record = SituationTrainingContractVerifier.BuildRecord();
        var input = renderer.SerializeModelInput(record.Input);
        var expanded = renderer.Render(record.Input, "expanded-v1");
        var compact = renderer.Render(record.Input, "compact-v1");
        var compactJson = compact.Split('\n')[2];
        Check(load.Config.SelectedVersion == SituationTrainingContractVersions.InputRepresentation &&
              load.Sha256 == SituationArtifactIO.Sha256(load.CanonicalJson),
            "prompt representation config version and hash are frozen", ref checks);
        Check(expanded.Split('\n').Length == 4 && compact.Split('\n').Length == 4,
            "prompt wrappers have a stable four-line shape", ref checks);
        Check(renderer.RestoreCompactInputJson(compactJson) == input,
            "compact input is losslessly reversible", ref checks);
        Check(Encoding.UTF8.GetByteCount(compact) < Encoding.UTF8.GetByteCount(expanded),
            "compact fixture is smaller than expanded", ref checks);
        Check(compactJson.Contains("\"ae\"", StringComparison.Ordinal) &&
              compactJson.Contains("\"ev\"", StringComparison.Ordinal) &&
              compactJson.Contains("\"dq\"", StringComparison.Ordinal),
            "compact retains allowed evidence, evidence, and quality", ref checks);
        CheckThrows(() => SituationPromptRepresentationLoader.ParseForVerification(
                load.CanonicalJson.Replace("\"selectedVersion\":\"compact-v1\"",
                    "\"selectedVersion\":\"expanded-v1\"", StringComparison.Ordinal),
                "drift.json"),
            "selected representation drift is rejected", ref checks);
        CheckThrows(() => SituationPromptRepresentationLoader.ParseForVerification(
                load.CanonicalJson[..^1] + ",\"unexpected\":1}", "unknown.json"),
            "unknown prompt config field is rejected", ref checks);
    }

    private static void VerifyRoundRobinAllocation(ref int checks)
    {
        var members = Enumerable.Range(0, 71)
            .Select(index => new SituationStageFourSplitMember(
                $"demo-{index:D2}.dem",
                SituationArtifactIO.Sha256($"match-{index}"),
                "train"))
            .ToArray();
        var allocation = SituationTrainingPilotAllocator.AllocateQuotas(
            members, SituationArtifactIO.Sha256("split"), 500);
        Check(allocation.Count == 71 && allocation.Sum(item => item.Quota) == 500,
            "round-robin allocates exactly 500 across all 71 train matches", ref checks);
        Check(allocation.Count(item => item.Quota == 8) == 3 &&
              allocation.Count(item => item.Quota == 7) == 68 &&
              allocation.Max(item => item.Quota) <= SituationTrainingPilotAllocator.MaxSamplesPerMatch,
            "round-robin quotas are seven or eight and stay below ten", ref checks);
        var reversed = SituationTrainingPilotAllocator.AllocateQuotas(
            members.Reverse().ToArray(), SituationArtifactIO.Sha256("split"), 500);
        Check(allocation.SequenceEqual(reversed), "round-robin allocation ignores input enumeration order", ref checks);
        CheckThrows(() => SituationTrainingPilotAllocator.AllocateQuotas(
                members, SituationArtifactIO.Sha256("split"), 711),
            "round-robin rejects limits above ten per match", ref checks);
    }

    private static void VerifyGreedyCoverage(ref int checks)
    {
        var matchId = SituationArtifactIO.Sha256("pilot-match");
        var attemptA = Attempt("s0-a1", 0);
        var attemptB = Attempt("s0-a2", 100);
        var candidates = new[]
        {
            Candidate(attemptA, 10, "live-start"),
            Candidate(attemptA, 20, "first-contact"),
            Candidate(attemptA, 30, "formation-split"),
            Candidate(attemptB, 110, "bomb-defusing"),
            Candidate(attemptB, 120, "isolated")
        }.Select(item => item with { MatchId = matchId }).ToArray();
        var priority = new[] { "first-contact", "bomb-defusing", "formation-split", "isolated" };
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        var selected = SituationTrainingPilotAllocator.SelectForMatch(candidates, 4, priority, counts);
        Check(selected.Count == 4 && selected.SelectMany(item => item.Selected.SelectionTags)
                  .Where(priority.Contains).Distinct(StringComparer.Ordinal).Count() == 4,
            "greedy selection covers distinct event and rare categories", ref checks);
        var repeatCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        var repeat = SituationTrainingPilotAllocator.SelectForMatch(
            candidates.Reverse().ToArray(), 4, priority, repeatCounts);
        Check(selected.Select(Key).SequenceEqual(repeat.Select(Key)),
            "greedy selection is stable across input order", ref checks);

        SituationPilotCandidate Candidate(RoundAttempt attempt, int tick, string tag) => new(
            matchId,
            attempt,
            new(tick, SituationTrainingPhase.Live, [tag], 0.5, 1, 2));
        static string Key(SituationPilotCandidate item) => $"{item.Attempt.RoundId}:{item.Selected.Tick}";
    }

    private static void VerifyPilotManifest(ref int checks)
    {
        var versions = Versions();
        var sha = SituationArtifactIO.Sha256("fixture");
        var files = new[]
        {
            "label-stats.json", "provenance.json", "representation-measurements.jsonl",
            "split.json", "train.jsonl"
        }.Select(path => new SituationArtifactFileV1(path, 1, path.EndsWith(".jsonl", StringComparison.Ordinal)
            ? 500 : null, sha)).ToArray();
        var manifest = new SituationTrainingDatasetManifestV1(
            SituationTrainingContractVersions.DatasetManifest,
            SituationArtifactStatus.Complete,
            SituationTrainingExportMode.Pilot,
            "representation-measurement",
            false,
            500,
            sha,
            sha,
            sha,
            sha,
            versions,
            [new(SituationContractVersions.Scene, "schemas/situation/minimap-scene-v1.schema.json", sha)],
            new Dictionary<string, SituationDatasetCountsV1> { ["train"] = new(71, 300, 500) },
            files);
        SituationTrainingManifestValidator.Validate(manifest);
        Check(true, "complete non-trainable pilot manifest is accepted", ref checks);
        CheckThrows(() => SituationTrainingManifestValidator.Validate(manifest with { Trainable = true }),
            "pilot trainable=true is rejected", ref checks);
        CheckThrows(() => SituationTrainingManifestValidator.Validate(manifest with { SampleLimit = 499 }),
            "pilot sample count above limit is rejected", ref checks);
        CheckThrows(() => SituationTrainingManifestValidator.Validate(manifest with
        {
            Counts = new Dictionary<string, SituationDatasetCountsV1>
            {
                ["train"] = new(71, 300, 500), ["dev"] = new(1, 1, 1)
            }
        }), "pilot dev count is rejected", ref checks);
        CheckThrows(() => SituationTrainingManifestValidator.Validate(manifest with
        {
            Files = files.Append(new("dev.jsonl", 1, 1, sha)).OrderBy(item => item.Path, StringComparer.Ordinal).ToArray()
        }), "pilot dev artifact is rejected", ref checks);
    }

    private static async Task<(int Checks, IReadOnlyDictionary<string, string> FileHashes)> VerifyArtifactAsync(
        string directory,
        CancellationToken cancellationToken)
    {
        var checks = 0;
        var root = Path.GetFullPath(directory);
        var manifestPath = Path.Combine(root, "manifest.json");
        var manifestJson = await File.ReadAllTextAsync(manifestPath, cancellationToken);
        var manifest = SituationTrainingContractJson.DeserializeManifest(manifestJson);
        var repositoryRoot = SituationArtifactIO.FindRepositoryRoot(root);
        await SituationTrainingManifestValidator.VerifyAsync(
            repositoryRoot, root, manifest, cancellationToken);
        Check(manifest.Status == SituationArtifactStatus.Complete &&
              manifest.Mode == SituationTrainingExportMode.Pilot &&
              manifest.Purpose == "representation-measurement" &&
              !manifest.Trainable && manifest.SampleLimit == 500,
            "pilot manifest identity is complete and non-trainable", ref checks);
        Check(!File.Exists(Path.Combine(root, "dev.jsonl")) &&
              !File.Exists(Path.Combine(root, "test.jsonl")) &&
              !File.Exists(Path.Combine(root, "review-candidates.jsonl")),
            "pilot omits dev, test, and review candidates", ref checks);

        var records = new List<SituationTrainingRecordV1>();
        foreach (var line in await File.ReadAllLinesAsync(Path.Combine(root, "train.jsonl"), cancellationToken))
            records.Add(SituationTrainingContractJson.DeserializeRecord(line));
        Check(records.Count == 500 && records.All(record => record.Metadata.Split == SituationTrainingSplit.Train),
            "pilot contains exactly 500 train records", ref checks);
        Check(records.Select(record => record.SampleId).Distinct(StringComparer.Ordinal).Count() == 500,
            "pilot sample IDs are unique", ref checks);
        Check(records.GroupBy(record => record.Metadata.MatchRef).Count() == 71 &&
              records.GroupBy(record => record.Metadata.MatchRef).Max(group => group.Count()) <= 10,
            "pilot covers all train matches with at most ten samples each", ref checks);
        Check(records.SequenceEqual(records
                .OrderBy(record => record.Metadata.MatchRef, StringComparer.Ordinal)
                .ThenBy(record => record.Metadata.RoundRef, StringComparer.Ordinal)
                .ThenBy(record => record.Metadata.Tick)
                .ThenBy(record => record.SampleId, StringComparer.Ordinal)),
            "pilot records use stable match/round/tick/sample ordering", ref checks);

        var measurements = (await File.ReadAllLinesAsync(
                Path.Combine(root, "representation-measurements.jsonl"), cancellationToken))
            .Select(SituationCanonicalJson.Deserialize<SituationRepresentationMeasurementV1>)
            .ToArray();
        Check(measurements.Length == 500 &&
              measurements.Select(item => item.SampleId).SequenceEqual(records.Select(item => item.SampleId)),
            "per-sample representation measurements align with train records", ref checks);
        Check(measurements.All(item => item.CompactPrompt.Utf8Bytes < item.ExpandedPrompt.Utf8Bytes),
            "every compact prompt is smaller than its expanded counterpart", ref checks);

        var stats = JsonSerializer.Deserialize<SituationLabelStatsV1>(
            await File.ReadAllTextAsync(Path.Combine(root, "label-stats.json"), cancellationToken),
            new JsonSerializerOptions(JsonSerializerDefaults.Web))
            ?? throw new InvalidDataException("Pilot label stats are empty.");
        var requiredSections = new[]
        {
            "representation.expanded-v1.utf8-bytes",
            "representation.compact-v1.utf8-bytes",
            "disk-volume", "selection-tags", "segment.evidence.utf8-bytes"
        };
        Check(requiredSections.All(name => stats.Sections.Any(section => section.Name == name)) &&
              !stats.ExactTokenizerMeasured &&
              stats.InputRepresentationVersion == SituationTrainingContractVersions.InputRepresentation,
            "pilot stats contain length, disk, segment, coverage, and tokenizer status", ref checks);

        var hashes = manifest.Files.ToDictionary(item => item.Path, item => item.Sha256, StringComparer.Ordinal);
        return (checks, hashes);
    }

    private static RoundAttempt Attempt(string roundId, int startTick) => new()
    {
        RoundId = roundId,
        SegmentId = 0,
        RoundNumber = 1,
        StartTick = startTick,
        LiveTick = startTick,
        EndTick = startTick + 100,
        Disposition = "completed"
    };

    private static SituationTrainingArtifactVersionsV1 Versions() => new(
        SituationTrainingContractVersions.Data,
        SituationTrainingContractVersions.Split,
        SituationTrainingContractVersions.TrainingRecord,
        SituationContractVersions.Scene,
        SituationSceneBuilder.BuilderVersion,
        SituationSceneBuilder.GeometryVersion,
        SituationContractVersions.Facts,
        SituationAnalysisRuleLoader.FrozenAnalysisRuleVersion,
        SituationContractVersions.Narrative,
        WinFeatureSampleBuilder.SemanticVersion,
        SituationTrainingContractVersions.Selection,
        SituationTrainingContractVersions.InputRepresentation,
        SituationTrainingContractVersions.PromptRepresentationConfig,
        SituationTrainingContractVersions.RepresentationMeasurement,
        SituationTrainingContractVersions.ReviewCandidate,
        SituationTrainingContractVersions.ReviewDecision,
        SituationTrainingContractVersions.LabelStats,
        SituationTrainingContractVersions.FrozenReviewLabel,
        SituationTrainingContractVersions.FrozenReviewManifest,
        null);

    private static void Check(bool condition, string label, ref int checks)
    {
        if (!condition)
            throw new InvalidOperationException($"Situation training pilot check failed: {label}.");
        checks++;
    }

    private static void CheckThrows(Action action, string label, ref int checks)
    {
        try
        {
            action();
        }
        catch (Exception exception) when (exception is InvalidDataException or JsonException or ArgumentException)
        {
            checks++;
            return;
        }
        throw new InvalidOperationException($"Situation training pilot check failed: {label}.");
    }
}

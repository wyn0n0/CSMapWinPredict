using CsDemoMap.Api.Models;
using CsDemoMap.Api.Services;

namespace CsDemoMap.Api.Tests;

internal static class SituationTrainingPartialWriterVerifier
{
    internal static async Task VerifyAsync(CancellationToken cancellationToken)
    {
        var repositoryRoot = SituationArtifactIO.FindRepositoryRoot(Environment.CurrentDirectory);
        var fixture = await CreateFixtureAsync(repositoryRoot, cancellationToken);
        var testRoot = Path.Combine(Path.GetTempPath(), $"situation-partial-writer-{Guid.NewGuid():N}");
        Directory.CreateDirectory(testRoot);
        var checks = 0;
        try
        {
            await VerifyCancelBeforeFirstMatchAsync(testRoot, repositoryRoot, fixture, cancellationToken);
            checks++;
            await VerifyCancelDuringMatchAsync(testRoot, repositoryRoot, fixture, cancellationToken);
            checks++;
            foreach (var fault in new[] { "empty-current", "transaction-temporary", "before-record", "partial-record", "commit-temporary" })
            {
                var root = Path.Combine(testRoot, fault);
                await using (var writer = await CreateAsync(root, repositoryRoot, fixture, cancellationToken))
                    await writer.BeginMatchAsync(fixture.Binding.Matches[0], cancellationToken);
                var current = Path.Combine(root, ".partial", "current");
                var transaction = Path.Combine(current, "transaction.json");
                var records = Path.Combine(current, "records.jsonl.partial");
                if (fault is "empty-current" or "transaction-temporary" or "before-record") File.Delete(records);
                if (fault is "empty-current" or "transaction-temporary") File.Delete(transaction);
                if (fault == "transaction-temporary") await File.WriteAllTextAsync(transaction + ".tmp", "{partial", cancellationToken);
                if (fault == "partial-record") await File.WriteAllTextAsync(records, "{partial", cancellationToken);
                if (fault == "commit-temporary") await File.WriteAllTextAsync(Path.Combine(current, "commit.json.tmp"), "{partial", cancellationToken);
                await using var resumed = await SituationTrainingPartialWriter.ResumeAsync(root, repositoryRoot, fixture.Binding, cancellationToken);
                Check(resumed.NextOrdinal == 0, "initialization and partial bytes never become committed progress");
                checks++;
            }
            await VerifyCancelAfterCommitAsync(testRoot, repositoryRoot, fixture, cancellationToken);
            checks++;
            await VerifyCorruptSpoolAsync(testRoot, repositoryRoot, fixture, cancellationToken);
            checks++;
            await VerifyCorruptCheckpointAsync(testRoot, repositoryRoot, fixture, cancellationToken);
            checks++;
            await VerifyExtraFileAsync(testRoot, repositoryRoot, fixture, cancellationToken);
            checks++;
            await VerifyDuplicateResumeAsync(testRoot, repositoryRoot, fixture, cancellationToken);
            checks++;
            await VerifyCompleteResumeRejectedAsync(testRoot, repositoryRoot, fixture, cancellationToken);
            checks++;
            await VerifyBindingDriftRejectedAsync(testRoot, repositoryRoot, fixture, cancellationToken);
            checks++;
            await VerifyInterruptedMatchesFinalHashesAsync(
                testRoot, repositoryRoot, fixture, cancellationToken);
            checks++;
        }
        finally
        {
            if (Directory.Exists(testRoot))
                Directory.Delete(testRoot, recursive: true);
        }
        Console.WriteLine($"Situation training partial writer checks passed: {checks}");
    }

    private static async Task VerifyCancelBeforeFirstMatchAsync(
        string testRoot,
        string repositoryRoot,
        Fixture fixture,
        CancellationToken cancellationToken)
    {
        var root = Path.Combine(testRoot, "cancel-before-first");
        await using (var writer = await CreateAsync(root, repositoryRoot, fixture, cancellationToken))
            Check(writer.NextOrdinal == 0, "new writer starts before the first match");
        await using var resumed = await SituationTrainingPartialWriter.ResumeAsync(
            root, repositoryRoot, fixture.Binding, cancellationToken);
        Check(resumed.NextOrdinal == 0 && resumed.CompletedMatches.Count == 0,
            "cancel before first match resumes without progress");
    }

    private static async Task VerifyCancelDuringMatchAsync(
        string testRoot,
        string repositoryRoot,
        Fixture fixture,
        CancellationToken cancellationToken)
    {
        var root = Path.Combine(testRoot, "cancel-during-match");
        await using (var writer = await CreateAsync(root, repositoryRoot, fixture, cancellationToken))
        {
            await writer.BeginMatchAsync(fixture.Binding.Matches[0], cancellationToken);
            await writer.WriteRecordAsync(RecordFor(fixture.Binding.Matches[0]), cancellationToken);
        }
        await using var resumed = await SituationTrainingPartialWriter.ResumeAsync(
            root, repositoryRoot, fixture.Binding, cancellationToken);
        Check(resumed.NextOrdinal == 0 && resumed.CompletedMatches.Count == 0,
            "an uncommitted match is not progress");
        Check(Directory.GetDirectories(
                Path.Combine(root, ".partial", "diagnostics", "matches"), "*", SearchOption.TopDirectoryOnly)
            .Length == 1, "an interrupted match is retained as diagnostics");
    }

    private static async Task VerifyCancelAfterCommitAsync(
        string testRoot,
        string repositoryRoot,
        Fixture fixture,
        CancellationToken cancellationToken)
    {
        var root = Path.Combine(testRoot, "cancel-after-commit");
        await using (var writer = await CreateAsync(root, repositoryRoot, fixture, cancellationToken))
            await CommitAsync(writer, fixture.Binding.Matches[0], cancellationToken);
        await using var resumed = await SituationTrainingPartialWriter.ResumeAsync(
            root, repositoryRoot, fixture.Binding, cancellationToken);
        Check(resumed.NextOrdinal == 1 && resumed.CompletedMatches.Count == 1,
            "a committed match survives process exit");
        await ThrowsAsync(
            () => resumed.BeginMatchAsync(fixture.Binding.Matches[0], cancellationToken),
            "duplicate committed match is rejected");
    }

    private static async Task VerifyCorruptSpoolAsync(
        string testRoot,
        string repositoryRoot,
        Fixture fixture,
        CancellationToken cancellationToken)
    {
        var root = Path.Combine(testRoot, "corrupt-spool");
        await using (var writer = await CreateAsync(root, repositoryRoot, fixture, cancellationToken))
            await CommitAsync(writer, fixture.Binding.Matches[0], cancellationToken);
        var spool = Path.Combine(root, fixture.Binding.Matches[0].Split.ToString().ToLowerInvariant());
        _ = spool;
        var recordPath = Directory.GetFiles(
            Path.Combine(root, ".partial", "spool"), "records.jsonl", SearchOption.AllDirectories).Single();
        await using (var stream = new FileStream(recordPath, FileMode.Open, FileAccess.Write, FileShare.None))
            stream.SetLength(stream.Length - 1);
        await ThrowsAsync(
            () => ResumeAndDisposeAsync(root, repositoryRoot, fixture.Binding, cancellationToken),
            "truncated committed spool is rejected");
    }

    private static async Task VerifyCorruptCheckpointAsync(
        string testRoot,
        string repositoryRoot,
        Fixture fixture,
        CancellationToken cancellationToken)
    {
        var root = Path.Combine(testRoot, "corrupt-checkpoint");
        await using (var writer = await CreateAsync(root, repositoryRoot, fixture, cancellationToken)) { }
        await File.WriteAllTextAsync(
            Path.Combine(root, ".partial", "checkpoint.json"), "{\"schemaVersion\":", cancellationToken);
        await ThrowsAsync(
            () => ResumeAndDisposeAsync(root, repositoryRoot, fixture.Binding, cancellationToken),
            "corrupt checkpoint JSON is rejected");
    }

    private static async Task VerifyExtraFileAsync(
        string testRoot,
        string repositoryRoot,
        Fixture fixture,
        CancellationToken cancellationToken)
    {
        var root = Path.Combine(testRoot, "extra-file");
        await using (var writer = await CreateAsync(root, repositoryRoot, fixture, cancellationToken)) { }
        await File.WriteAllTextAsync(Path.Combine(root, "unexpected.txt"), "unexpected", cancellationToken);
        await ThrowsAsync(
            () => ResumeAndDisposeAsync(root, repositoryRoot, fixture.Binding, cancellationToken),
            "unknown output file is rejected");
    }

    private static async Task VerifyDuplicateResumeAsync(
        string testRoot,
        string repositoryRoot,
        Fixture fixture,
        CancellationToken cancellationToken)
    {
        var root = Path.Combine(testRoot, "duplicate-resume");
        await using (var writer = await CreateAsync(root, repositoryRoot, fixture, cancellationToken)) { }
        await using var first = await SituationTrainingPartialWriter.ResumeAsync(
            root, repositoryRoot, fixture.Binding, cancellationToken);
        await ThrowsAsync(
            () => ResumeAndDisposeAsync(root, repositoryRoot, fixture.Binding, cancellationToken),
            "concurrent duplicate resume is rejected by the exclusive lock");
    }

    private static async Task VerifyCompleteResumeRejectedAsync(
        string testRoot,
        string repositoryRoot,
        Fixture fixture,
        CancellationToken cancellationToken)
    {
        var root = Path.Combine(testRoot, "complete-resume");
        await using (var writer = await CreateAsync(root, repositoryRoot, fixture, cancellationToken)) { }
        var complete = fixture.Manifest with
        {
            Status = SituationArtifactStatus.Complete,
            Trainable = true
        };
        await SituationArtifactIO.WriteJsonAsync(
            Path.Combine(root, "manifest.json"), complete, cancellationToken);
        await ThrowsAsync(
            () => ResumeAndDisposeAsync(root, repositoryRoot, fixture.Binding, cancellationToken),
            "complete output cannot be resumed");
    }

    private static async Task VerifyBindingDriftRejectedAsync(
        string testRoot,
        string repositoryRoot,
        Fixture fixture,
        CancellationToken cancellationToken)
    {
        var root = Path.Combine(testRoot, "binding-drift");
        await using (var writer = await CreateAsync(root, repositoryRoot, fixture, cancellationToken)) { }
        var drifted = fixture.Binding with
        {
            ParentManifestSha256 = SituationArtifactIO.Sha256("drifted-parent")
        };
        await ThrowsAsync(
            () => ResumeAndDisposeAsync(root, repositoryRoot, drifted, cancellationToken),
            "split-parent or configuration drift is rejected");

        var sourceDrift = fixture.Binding with
        {
            SourceFiles = fixture.Binding.SourceFiles.Select((item, index) =>
                index == 0 ? item with { Sha256 = new string('0', 64) } : item).ToArray()
        };
        await ThrowsAsync(
            async () =>
            {
                await using var ignored = await SituationTrainingPartialWriter.CreateNewAsync(
                    Path.Combine(testRoot, "source-drift"), repositoryRoot, sourceDrift,
                    fixture.Manifest, cancellationToken);
            },
            "source file hash drift is rejected");

        var configDrift = fixture.Binding with
        {
            SelectionConfigSha256 = SituationArtifactIO.Sha256("drifted-selection")
        };
        var configManifest = fixture.Manifest with
        {
            SelectionConfigSha256 = configDrift.SelectionConfigSha256
        };
        await ThrowsAsync(
            async () =>
            {
                await using var ignored = await SituationTrainingPartialWriter.CreateNewAsync(
                    Path.Combine(testRoot, "config-drift"), repositoryRoot, configDrift,
                    configManifest, cancellationToken);
            },
            "selection configuration drift is rejected");
    }

    private static async Task VerifyInterruptedMatchesFinalHashesAsync(
        string testRoot,
        string repositoryRoot,
        Fixture fixture,
        CancellationToken cancellationToken)
    {
        var uninterruptedRoot = Path.Combine(testRoot, "uninterrupted");
        await using (var writer = await CreateAsync(
                         uninterruptedRoot, repositoryRoot, fixture, cancellationToken))
        {
            foreach (var match in fixture.Binding.Matches)
                await CommitAsync(writer, match, cancellationToken);
            await writer.MergeAsync(cancellationToken);
            await writer.PublishAsync(cancellationToken);
            await writer.CleanupPublishedWorkspaceAsync(cancellationToken);
        }

        var resumedRoot = Path.Combine(testRoot, "resumed");
        await using (var writer = await CreateAsync(resumedRoot, repositoryRoot, fixture, cancellationToken))
            await CommitAsync(writer, fixture.Binding.Matches[0], cancellationToken);
        await using (var interrupted = await SituationTrainingPartialWriter.ResumeAsync(
                         resumedRoot, repositoryRoot, fixture.Binding, cancellationToken))
        {
            await interrupted.BeginMatchAsync(fixture.Binding.Matches[1], cancellationToken);
            await interrupted.WriteRecordAsync(
                RecordFor(fixture.Binding.Matches[1]), cancellationToken);
        }
        await using (var resumed = await SituationTrainingPartialWriter.ResumeAsync(
                         resumedRoot, repositoryRoot, fixture.Binding, cancellationToken))
        {
            Check(resumed.NextOrdinal == 1, "only the interrupted match is replayed");
            for (var index = resumed.NextOrdinal; index < fixture.Binding.Matches.Count; index++)
                await CommitAsync(resumed, fixture.Binding.Matches[index], cancellationToken);
            await resumed.MergeAsync(cancellationToken);
            await resumed.PublishAsync(cancellationToken);
            await resumed.CleanupPublishedWorkspaceAsync(cancellationToken);
        }

        foreach (var split in new[] { "train", "dev", "test" })
        {
            var first = await SituationArtifactIO.FileSha256Async(
                Path.Combine(uninterruptedRoot, $"{split}.jsonl"), cancellationToken);
            var repeat = await SituationArtifactIO.FileSha256Async(
                Path.Combine(resumedRoot, $"{split}.jsonl"), cancellationToken);
            Check(first == repeat, $"{split} hash matches uninterrupted export");
        }
        Check(!Directory.Exists(Path.Combine(uninterruptedRoot, ".partial")) &&
              !Directory.Exists(Path.Combine(resumedRoot, ".partial")),
            "explicit successful cleanup removes only writer-owned workspace");
    }

    private static async Task<Fixture> CreateFixtureAsync(
        string repositoryRoot,
        CancellationToken cancellationToken)
    {
        var matches = Enumerable.Range(0, 3)
            .Select(index => "match-" + SituationArtifactIO.Sha256($"partial-writer-match-{index}"))
            .Order(StringComparer.Ordinal)
            .Select((matchRef, index) => new SituationTrainingPlannedMatchV1(
                index,
                matchRef,
                index switch
                {
                    0 => SituationTrainingSplit.Train,
                    1 => SituationTrainingSplit.Dev,
                    _ => SituationTrainingSplit.Test
                }))
            .ToArray();
        const string planVersion = "situation-training-sample-plan-test-v1";
        var schemas = await SituationTrainingSchemaRegistry.LoadAsync(repositoryRoot, cancellationToken);
        var sourcePaths = new[]
        {
            "apps/api/Features/Situation/Training/Models/SituationTrainingModels.cs",
            "apps/api/Features/Situation/Training/SituationPromptRepresentation.cs",
            "apps/api/Features/Situation/Training/SituationTrainingCandidateSelector.cs",
            "apps/api/Features/Situation/Training/SituationTrainingContractJson.cs",
            "apps/api/Features/Situation/Workflows/SituationTrainingCheckpoint.cs",
            "apps/api/Features/Situation/Workflows/SituationTrainingPartialWriter.cs",
            "apps/api/Features/Situation/Workflows/SituationTrainingResumeValidator.cs"
        };
        var sourceFiles = new List<SituationProvenanceSourceFileV1>();
        foreach (var relative in sourcePaths.Order(StringComparer.Ordinal))
            sourceFiles.Add(new(relative, await SituationArtifactIO.FileSha256Async(
                SituationArtifactIO.ResolveRepositoryPath(repositoryRoot, relative, "Writer fixture source"),
                cancellationToken)));
        var versions = Versions();
        var splitSha = SituationArtifactIO.Sha256("partial-writer-split");
        var parentSha = SituationArtifactIO.Sha256("partial-writer-parent");
        var binding = new SituationTrainingWriterBindingV1(
            splitSha,
            parentSha,
            SituationTrainingSelectionLoader.LoadFrozen().Sha256,
            SituationPromptRepresentationLoader.LoadFrozen().Sha256,
            versions,
            schemas,
            sourceFiles,
            SituationTrainingExportMode.Full,
            "full-training-dataset",
            planVersion,
            SituationTrainingPartialWriter.ComputeSamplePlanSha256(planVersion, matches),
            matches,
            []);
        var counts = new Dictionary<string, SituationDatasetCountsV1>(StringComparer.Ordinal)
        {
            ["dev"] = new(0, 0, 0),
            ["test"] = new(0, 0, 0),
            ["train"] = new(0, 0, 0)
        };
        var manifest = new SituationTrainingDatasetManifestV1(
            SituationTrainingContractVersions.DatasetManifest,
            SituationArtifactStatus.Incomplete,
            SituationTrainingExportMode.Full,
            binding.Purpose,
            false,
            null,
            splitSha,
            parentSha,
            binding.SelectionConfigSha256,
            binding.PromptConfigSha256,
            versions,
            schemas,
            counts,
            []);
        return new(binding, manifest);
    }

    private static Task<SituationTrainingPartialWriter> CreateAsync(
        string root,
        string repositoryRoot,
        Fixture fixture,
        CancellationToken cancellationToken) =>
        SituationTrainingPartialWriter.CreateNewAsync(
            root, repositoryRoot, fixture.Binding, fixture.Manifest, cancellationToken);

    private static async Task CommitAsync(
        SituationTrainingPartialWriter writer,
        SituationTrainingPlannedMatchV1 match,
        CancellationToken cancellationToken)
    {
        await writer.BeginMatchAsync(match, cancellationToken);
        await writer.WriteRecordAsync(RecordFor(match), cancellationToken);
        await writer.CommitMatchAsync(
            SituationTrainingPartialWriter.CreateStatisticsSnapshot(new
            {
                matches = 1,
                samples = 1,
                split = match.Split.ToString().ToLowerInvariant()
            }),
            cancellationToken);
    }

    private static SituationTrainingRecordV1 RecordFor(SituationTrainingPlannedMatchV1 match)
    {
        var source = SituationTrainingContractVerifier.BuildRecord();
        return source with
        {
            SampleId = "sample-" + SituationArtifactIO.Sha256($"{match.MatchRef}|{source.Metadata.Tick}"),
            Metadata = source.Metadata with
            {
                Split = match.Split,
                MatchRef = match.MatchRef
            }
        };
    }

    private static async Task ResumeAndDisposeAsync(
        string root,
        string repositoryRoot,
        SituationTrainingWriterBindingV1 binding,
        CancellationToken cancellationToken)
    {
        await using var writer = await SituationTrainingPartialWriter.ResumeAsync(
            root, repositoryRoot, binding, cancellationToken);
    }

    private static async Task ThrowsAsync(Func<Task> action, string label)
    {
        try
        {
            await action();
        }
        catch (Exception exception) when (exception is InvalidDataException or InvalidOperationException or
                                          IOException or System.Text.Json.JsonException)
        {
            return;
        }
        throw new InvalidOperationException($"Situation partial writer check failed: {label}.");
    }

    private static void Check(bool condition, string label)
    {
        if (!condition)
            throw new InvalidOperationException($"Situation partial writer check failed: {label}.");
    }

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

    private sealed record Fixture(
        SituationTrainingWriterBindingV1 Binding,
        SituationTrainingDatasetManifestV1 Manifest);
}

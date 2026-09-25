using System.Diagnostics;
using System.Text;
using CsDemoMap.Api.Models;

namespace CsDemoMap.Api.Services;

internal sealed record SituationTrainingMatchCheckpointData(
    IReadOnlyList<SituationTrainingRoundSelectionV1> Selections,
    IReadOnlyList<SituationTrainingProvenanceRoundMappingV1> RoundMappings);

internal sealed record SituationTrainingMatchProduct(
    IReadOnlyList<SituationTrainingRecordV1> Records,
    SituationTrainingMatchCheckpointData CheckpointData,
    double ParseSeconds,
    double IndexSeconds,
    double SelectionSeconds,
    double RecordSeconds);

internal sealed record SituationTrainingExportOptions(
    int RoundWorkers = 4,
    bool Resume = false,
    int? TrainingBenchmarkMatches = null,
    Action<string, int>? Boundary = null);

/// <summary>One match transaction at a time; completed matches never require reparsing.</summary>
internal static class SituationTrainingDatasetExporter
{
    private const string PlanVersion = "situation-training-full-plan-v1";
    private static readonly string[] Files =
        ["dev.jsonl", "label-stats.json", "provenance.json", "split.json", "test.jsonl", "train.jsonl"];

    internal static async Task<SituationTrainingDatasetManifestV1> ExportAsync(
        string demoDirectory, string splitPath, string outputDirectory,
        CancellationToken cancellationToken, SituationTrainingExportOptions? options = null,
        Func<SituationStageFourSplitMember, SituationTrainingSplit, string, CancellationToken,
            Task<SituationTrainingMatchProduct>>? testProcessor = null)
    {
        options ??= new();
        if (options.RoundWorkers is < 1 or > 8 ||
            options.TrainingBenchmarkMatches is not (null or 5))
            throw new InvalidDataException("Round workers must be 1..8; train benchmark size is exactly five.");
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(outputDirectory));
        var demoRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(demoDirectory));
        if (!options.Resume)
        {
            SituationArtifactIO.EnsureNewOutput(root);
            SituationArtifactIO.EnsureNewOutput(root + ".writer-recovery");
        }
        var repository = SituationArtifactIO.FindRepositoryRoot(splitPath);
        var split = await SituationDatasetSplit.LoadApprovedAsync(splitPath, cancellationToken);
        if (testProcessor is null)
        {
            if ((new DirectoryInfo(demoRoot).Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Demo directory cannot be a reparse point.");
            SituationTrainingPilotExporter.ValidateDemoMembership(demoRoot, split.Split);
        }
        var schemas = await SituationTrainingSchemaRegistry.LoadAsync(repository, cancellationToken);
        var selection = SituationTrainingSelectionLoader.LoadFrozen();
        var prompt = SituationPromptRepresentationLoader.LoadFrozen();
        var parentManifest = SituationArtifactIO.ResolveRepositoryPath(repository,
            split.Split.ParentManifestReference, "Parent manifest");
        var parentSplit = SituationArtifactIO.ResolveRepositoryPath(repository,
            split.Split.ParentSplitReference, "Parent split");
        if (await SituationArtifactIO.FileSha256Async(parentManifest, cancellationToken) != split.Split.ParentManifestSha256 ||
            await SituationArtifactIO.FileSha256Async(parentSplit, cancellationToken) != split.Split.ParentSplitSha256)
            throw new InvalidDataException("Frozen parent split or manifest content changed.");
        var provenance = await SituationTrainingProvenanceBuilder.CreateAsync(
            repository, split, SituationAnalysisRuleLoader.LoadFrozen().Sha256, selection.Sha256,
            prompt.Sha256, schemas, SourcePaths(repository).Concat(new[]
            {
                split.Split.ParentManifestReference, split.Split.ParentSplitReference,
                "situation-implementation/situation-stage4-split-v1.json"
            }), cancellationToken);
        var versions = SituationTrainingPilotExporter.CreateArtifactVersions();
        var identity = SituationTrainingRecordBuilder.CreateIdentityVersions(split.SplitSha256);
        var members = split.Split.Train.Select(member => (Member: member, Split: SituationTrainingSplit.Train))
            .Concat(split.Split.Dev.Select(member => (Member: member, Split: SituationTrainingSplit.Dev)))
            .Concat(split.Split.Test.Select(member => (Member: member, Split: SituationTrainingSplit.Test)))
            .Select(item => (item.Member, item.Split, MatchRef: SituationTrainingContractJson.CreateIdentity(
                item.Member.MatchId, "s0-a1", 0, identity).MatchRef))
            .OrderBy(item => item.MatchRef, StringComparer.Ordinal).ToArray();
        if (options.TrainingBenchmarkMatches is { } benchmarkCount)
            members = members.Where(item => item.Split == SituationTrainingSplit.Train).Take(benchmarkCount).ToArray();
        if (options.Resume && testProcessor is null)
        {
            foreach (var item in members)
            {
                var path = Path.Combine(demoRoot, item.Member.FileName);
                SituationTrainingPilotExporter.ValidateDirectFile(demoRoot, path, item.Member.FileName);
                if (await SituationArtifactIO.FileSha256Async(path, cancellationToken) != item.Member.MatchId)
                    throw new InvalidDataException("Resume Demo content differs from the frozen split.");
            }
        }
        var plan = members.Select((item, ordinal) =>
            new SituationTrainingPlannedMatchV1(ordinal, item.MatchRef, item.Split)).ToArray();
        var binding = new SituationTrainingWriterBindingV1(
            split.SplitSha256, split.Split.ParentManifestSha256, selection.Sha256, prompt.Sha256,
            versions, schemas, provenance.SourceFiles, SituationTrainingExportMode.Full,
            options.TrainingBenchmarkMatches is null ? "template-prelabel-training" : "train-infrastructure-benchmark",
            PlanVersion, SituationTrainingPartialWriter.ComputeSamplePlanSha256(PlanVersion, plan), plan,
            ["label-stats.json", "provenance.json", "split.json"]);
        var incomplete = new SituationTrainingDatasetManifestV1(
            SituationTrainingContractVersions.DatasetManifest, SituationArtifactStatus.Incomplete,
            SituationTrainingExportMode.Full, binding.Purpose, false, null,
            split.SplitSha256, split.Split.ParentManifestSha256, selection.Sha256, prompt.Sha256,
            versions, schemas, new Dictionary<string, SituationDatasetCountsV1>
            {
                ["dev"] = new(0, 0, 0), ["test"] = new(0, 0, 0), ["train"] = new(0, 0, 0)
            }, []);
        var stopwatch = Stopwatch.StartNew();
        await using var writer = options.Resume
            ? await SituationTrainingPartialWriter.ResumeAsync(root, repository, binding, cancellationToken)
            : await SituationTrainingPartialWriter.CreateNewAsync(root, repository, binding, incomplete, cancellationToken);
        if (File.Exists(Path.Combine(root, "manifest.json.tmp")))
        {
            // An interrupted atomic replacement is uncommitted. Re-publish the
            // exact existing incomplete bytes to consume its declared temporary.
            var manifestText = await File.ReadAllTextAsync(Path.Combine(root, "manifest.json"), cancellationToken);
            await SituationArtifactIO.WriteTextAtomicAsync(Path.Combine(root, "manifest.json"), manifestText, cancellationToken);
        }
        var stats = new SituationTrainingDatasetStatistics(split.SplitSha256, prompt.Config.SelectedVersion, prompt.Sha256);
        var mappings = new List<SituationTrainingProvenanceRoundMappingV1>();
        // Checkpoint keeps small selector/mapping deltas; record-derived statistics
        // are reconstructed from already validated committed spools, never from Demo.
        foreach (var completed in writer.CompletedMatches)
        {
            var data = SituationTrainingResumeValidator.Deserialize<SituationTrainingMatchCheckpointData>(
                completed.Statistics.CanonicalJson, "Match checkpoint data");
            var matchStats = NewStats();
            matchStats.RegisterMatch(completed.Split, completed.MatchRef);
            foreach (var item in data.Selections) matchStats.AddSelection(completed.Split, completed.MatchRef, item);
            using var reader = new StreamReader(Path.Combine(root, completed.Spool.Path), new UTF8Encoding(false, true));
            while (await reader.ReadLineAsync(cancellationToken) is { } line)
                matchStats.AddRecord(SituationTrainingContractJson.DeserializeRecord(line));
            stats.Merge(matchStats);
            mappings.AddRange(data.RoundMappings);
        }
        for (var ordinal = writer.NextOrdinal; ordinal < plan.Length; ordinal++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            options.Boundary?.Invoke("before-match", ordinal);
            await writer.BeginMatchAsync(plan[ordinal], cancellationToken);
            var member = members[ordinal];
            var product = testProcessor is null
                ? await ProcessMatchAsync(demoRoot, member.Member, member.Split, split.SplitSha256,
                    options.RoundWorkers, cancellationToken)
                : await testProcessor(member.Member, member.Split, split.SplitSha256, cancellationToken);
            var matchStats = NewStats();
            matchStats.RegisterMatch(member.Split, member.MatchRef);
            var writeWatch = Stopwatch.StartNew();
            foreach (var selected in product.CheckpointData.Selections)
                matchStats.AddSelection(member.Split, member.MatchRef, selected);
            foreach (var record in product.Records)
            {
                await writer.WriteRecordAsync(record, cancellationToken);
                matchStats.AddRecord(record);
                options.Boundary?.Invoke("record", ordinal);
            }
            _ = matchStats.CreateSnapshot();
            options.Boundary?.Invoke("before-commit", ordinal);
            await writer.CommitMatchAsync(
                SituationTrainingPartialWriter.CreateStatisticsSnapshot(product.CheckpointData), cancellationToken);
            stats.Merge(matchStats);
            mappings.AddRange(product.CheckpointData.RoundMappings);
            var writeSeconds = writeWatch.Elapsed.TotalSeconds;
            options.Boundary?.Invoke("after-commit", ordinal);
            Console.WriteLine($"Full match {ordinal + 1}/{plan.Length}: {product.Records.Count} records; " +
                $"parse={product.ParseSeconds:F3}s index={product.IndexSeconds:F3}s " +
                $"selection={product.SelectionSeconds:F3}s record={product.RecordSeconds:F3}s writeCommit={writeSeconds:F3}s; " +
                $"elapsed={stopwatch.Elapsed.TotalSeconds:F3}s peakBytes={Process.GetCurrentProcess().PeakWorkingSet64}.");
        }
        if (options.TrainingBenchmarkMatches is not null)
        {
            Console.WriteLine($"Train benchmark committed {plan.Length} matches; intentionally incomplete; " +
                $"matchWorkers=1 roundWorkers={options.RoundWorkers} elapsed={stopwatch.Elapsed.TotalSeconds:F3}s " +
                $"gc={GC.CollectionCount(0)}/{GC.CollectionCount(1)}/{GC.CollectionCount(2)}.");
            return incomplete;
        }
        if (writer.Phase == SituationTrainingWriterPhase.Writing)
            await writer.MergeAsync(cancellationToken);
        options.Boundary?.Invoke("merged", plan.Length);
        await writer.PublishAsync(cancellationToken);
        options.Boundary?.Invoke("published", plan.Length);
        // Preserve the complete writer workspace beside the artifact before final
        // metadata publication; restore it on explicit resume if publication fails.
        await writer.CleanupPublishedWorkspaceAsync(cancellationToken);
        options.Boundary?.Invoke("cleaned", plan.Length);
        var snapshot = stats.CreateSnapshot();
        var splitText = await File.ReadAllTextAsync(splitPath, cancellationToken);
        await SituationArtifactIO.WriteTextAtomicAsync(Path.Combine(root, "split.json"), splitText, cancellationToken);
        await SituationArtifactIO.WriteJsonAsync(Path.Combine(root, "label-stats.json"), snapshot.LabelStats, cancellationToken);
        provenance = provenance with { RoundMappings = mappings.OrderBy(item => item.MatchRef, StringComparer.Ordinal)
            .ThenBy(item => item.RoundRef, StringComparer.Ordinal).ToArray() };
        SituationTrainingProvenanceBuilder.Validate(provenance);
        await SituationTrainingProvenanceBuilder.VerifyCurrentRepositoryStateAsync(repository, provenance, cancellationToken);
        await SituationArtifactIO.WriteJsonAsync(Path.Combine(root, "provenance.json"), provenance, cancellationToken);
        var inventory = new List<SituationArtifactFileV1>();
        foreach (var file in Files)
        {
            var path = Path.Combine(root, file);
            long? rows = file.EndsWith(".jsonl", StringComparison.Ordinal)
                ? writer.SplitRows[Path.GetFileNameWithoutExtension(file)] : null;
            inventory.Add(new(file, new FileInfo(path).Length, rows,
                await SituationArtifactIO.FileSha256Async(path, cancellationToken)));
        }
        var complete = incomplete with
        {
            Status = SituationArtifactStatus.Complete, Trainable = true,
            Counts = snapshot.Counts, Files = inventory
        };
        var readbackWatch = Stopwatch.StartNew();
        await SituationTrainingDatasetValidator.VerifyAsync(repository, root, cancellationToken,
            preparedManifest: complete);
        options.Boundary?.Invoke("validated", plan.Length);
        await SituationArtifactIO.WriteJsonAsync(Path.Combine(root, "manifest.json"), complete, cancellationToken);
        Console.WriteLine($"Full export complete: {snapshot.Counts.Values.Sum(item => item.Samples)} samples; " +
            $"readback={readbackWatch.Elapsed.TotalSeconds:F3}s elapsed={stopwatch.Elapsed.TotalSeconds:F3}s " +
            $"peakBytes={Process.GetCurrentProcess().PeakWorkingSet64}.");
        return complete;

        SituationTrainingDatasetStatistics NewStats() => new(split.SplitSha256, prompt.Config.SelectedVersion, prompt.Sha256);
    }

    internal static async Task<SituationTrainingMatchProduct> ProcessMatchAsync(
        string demoRoot, SituationStageFourSplitMember member, SituationTrainingSplit split,
        string splitSha256, int roundWorkers, CancellationToken cancellationToken)
    {
        var path = Path.Combine(demoRoot, member.FileName);
        SituationTrainingPilotExporter.ValidateDirectFile(demoRoot, path, member.FileName);
        // Keep one read-only handle across hashing and parse to prevent replacement.
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var digest = Convert.ToHexStringLower(await System.Security.Cryptography.SHA256.HashDataAsync(stream, cancellationToken));
        if (digest != member.MatchId) throw new InvalidDataException("Demo content differs from the frozen split.");
        stream.Position = 0;
        var watch = Stopwatch.StartNew();
        var timeline = await new DemoParserService().ParseAsync(stream, member.FileName, cancellationToken, collectSemantics: true);
        var parse = watch.Elapsed.TotalSeconds;
        watch.Restart();
        var index = SituationTrainingTimelineIndex.Create(timeline, cancellationToken);
        var indexSeconds = watch.Elapsed.TotalSeconds;
        return await BuildMatchAsync(index, member, split, splitSha256, roundWorkers, parse, indexSeconds, cancellationToken);
    }

    internal static async Task<SituationTrainingMatchProduct> BuildMatchAsync(
        SituationTrainingTimelineIndex index, SituationStageFourSplitMember member, SituationTrainingSplit split,
        string splitSha256, int roundWorkers, double parseSeconds, double indexSeconds, CancellationToken cancellationToken)
    {
        using var service = new SituationSceneService();
        var selector = new SituationTrainingCandidateSelector(service);
        var attempts = index.CompletedAttempts;
        var results = new SituationTrainingRoundSelectionWithPayloads[attempts.Count];
        var watch = Stopwatch.StartNew();
        await Parallel.ForEachAsync(Enumerable.Range(0, attempts.Count), new ParallelOptions
        {
            MaxDegreeOfParallelism = roundWorkers, CancellationToken = cancellationToken
        }, (ordinal, token) =>
        {
            results[ordinal] = selector.SelectRound(index, member.MatchId, attempts[ordinal].RoundId, token);
            return ValueTask.CompletedTask;
        });
        var selectionSeconds = watch.Elapsed.TotalSeconds;
        watch.Restart();
        var records = results.SelectMany(item => item.Payloads)
            .Select(payload => SituationTrainingRecordBuilder.Build(member, payload, split, splitSha256))
            .OrderBy(record => record.Metadata.RoundRef, StringComparer.Ordinal)
            .ThenBy(record => record.Metadata.Tick).ThenBy(record => record.SampleId, StringComparer.Ordinal).ToArray();
        var versions = SituationTrainingRecordBuilder.CreateIdentityVersions(splitSha256);
        var mappings = attempts.Select(attempt =>
        {
            var identity = SituationTrainingContractJson.CreateIdentity(member.MatchId, attempt.RoundId, 0, versions);
            return new SituationTrainingProvenanceRoundMappingV1(identity.MatchRef, identity.RoundRef, attempt.RoundId);
        }).OrderBy(item => item.RoundRef, StringComparer.Ordinal).ToArray();
        return new(records, new(results.Select(item => item.SelectionResult.Selection).ToArray(), mappings),
            parseSeconds, indexSeconds, selectionSeconds, watch.Elapsed.TotalSeconds);
    }

    internal static string[] SourcePaths(string repository) =>
        new[] { "apps/api", "apps/cli" }.SelectMany(directory =>
            Directory.EnumerateFiles(Path.Combine(repository, directory), "*", SearchOption.AllDirectories))
            .Select(path => Path.GetRelativePath(repository, path).Replace('\\', '/'))
            .Where(path => (path.StartsWith("apps/api/", StringComparison.Ordinal) ||
                            path.StartsWith("apps/cli/", StringComparison.Ordinal)) &&
                           !path.Contains("/bin/", StringComparison.Ordinal) && !path.Contains("/obj/", StringComparison.Ordinal) &&
                           (path.EndsWith(".cs", StringComparison.Ordinal) || path.EndsWith(".csproj", StringComparison.Ordinal) ||
                            path.EndsWith(".json", StringComparison.Ordinal)))
            .Order(StringComparer.Ordinal).ToArray();
}

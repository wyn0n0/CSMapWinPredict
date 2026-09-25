using System.Text;
using System.Text.Json;
using CsDemoMap.Api.Models;
using CsDemoMap.Api.Services;

namespace CsDemoMap.Api.Tests;

internal static class SituationTrainingDatasetValidatorVerifier
{
    internal static async Task VerifyAsync(CancellationToken cancellationToken)
    {
        var repositoryRoot = SituationArtifactIO.FindRepositoryRoot(Environment.CurrentDirectory);
        var testRoot = Path.Combine(Path.GetTempPath(), $"situation-dataset-validator-{Guid.NewGuid():N}");
        Directory.CreateDirectory(testRoot);
        var checks = 0;
        try
        {
            VerifyIncrementalStatistics(ref checks);
            checks += await VerifyDirtyProvenanceAsync(repositoryRoot, cancellationToken);
            var baseline = Path.Combine(testRoot, "baseline");
            await CreateArtifactAsync(repositoryRoot, baseline, cancellationToken);
            var result = await SituationTrainingDatasetValidator.VerifyAsync(
                repositoryRoot, baseline, cancellationToken);
            Check(result.JsonlRows == 4, "complete full fixture validates by streaming readback", ref checks);

            checks += await VerifyMutationAsync(testRoot, baseline, "corrupt-line", async root =>
            {
                await File.WriteAllTextAsync(Path.Combine(root, "dev.jsonl"), "{broken}\n", Utf8NoBom, cancellationToken);
                await RefreshManifestAsync(root, cancellationToken);
            }, repositoryRoot, cancellationToken);
            checks += await VerifyMutationAsync(testRoot, baseline, "bom", async root =>
            {
                var path = Path.Combine(root, "train.jsonl");
                var original = await File.ReadAllBytesAsync(path, cancellationToken);
                await File.WriteAllBytesAsync(path, Encoding.UTF8.Preamble.ToArray().Concat(original).ToArray(),
                    cancellationToken);
                await RefreshManifestAsync(root, cancellationToken);
            }, repositoryRoot, cancellationToken);
            checks += await VerifyMutationAsync(testRoot, baseline, "crlf", async root =>
            {
                var path = Path.Combine(root, "train.jsonl");
                var text = await File.ReadAllTextAsync(path, StrictUtf8, cancellationToken);
                await File.WriteAllTextAsync(path, text.Replace("\n", "\r\n", StringComparison.Ordinal),
                    Utf8NoBom, cancellationToken);
                await RefreshManifestAsync(root, cancellationToken);
            }, repositoryRoot, cancellationToken);
            checks += await VerifyMutationAsync(testRoot, baseline, "multiline", async root =>
            {
                var path = Path.Combine(root, "dev.jsonl");
                var text = await File.ReadAllTextAsync(path, StrictUtf8, cancellationToken);
                await File.WriteAllTextAsync(path, text.Insert(1, "\n"), Utf8NoBom, cancellationToken);
                await RefreshManifestAsync(root, cancellationToken);
            }, repositoryRoot, cancellationToken);
            checks += await VerifyMutationAsync(testRoot, baseline, "sorting", async root =>
            {
                var path = Path.Combine(root, "train.jsonl");
                var lines = await File.ReadAllLinesAsync(path, StrictUtf8, cancellationToken);
                Array.Reverse(lines);
                await WriteLinesAsync(path, lines, cancellationToken);
                await RefreshManifestAsync(root, cancellationToken);
            }, repositoryRoot, cancellationToken);
            checks += await VerifyMutationAsync(testRoot, baseline, "duplicate-sample", async root =>
            {
                var train = SituationTrainingContractJson.DeserializeRecord(
                    (await File.ReadAllLinesAsync(Path.Combine(root, "train.jsonl"), StrictUtf8, cancellationToken))[0]);
                await RewriteSingleRecordAsync(root, "dev.jsonl",
                    record => record with { SampleId = train.SampleId }, cancellationToken);
                await RefreshManifestAsync(root, cancellationToken);
            }, repositoryRoot, cancellationToken);
            checks += await VerifyMutationAsync(testRoot, baseline, "cross-split-match", async root =>
            {
                var train = SituationTrainingContractJson.DeserializeRecord(
                    (await File.ReadAllLinesAsync(Path.Combine(root, "train.jsonl"), StrictUtf8, cancellationToken))[0]);
                await RewriteSingleRecordAsync(root, "dev.jsonl",
                    record => record with
                    {
                        Metadata = record.Metadata with { MatchRef = train.Metadata.MatchRef }
                    }, cancellationToken);
                await RefreshManifestAsync(root, cancellationToken);
            }, repositoryRoot, cancellationToken);
            checks += await VerifyMutationAsync(testRoot, baseline, "cross-split-round", async root =>
            {
                var train = SituationTrainingContractJson.DeserializeRecord(
                    (await File.ReadAllLinesAsync(Path.Combine(root, "train.jsonl"), StrictUtf8, cancellationToken))[0]);
                await RewriteSingleRecordAsync(root, "dev.jsonl",
                    record => record with
                    {
                        Metadata = record.Metadata with { RoundRef = train.Metadata.RoundRef }
                    }, cancellationToken);
                await RefreshManifestAsync(root, cancellationToken);
            }, repositoryRoot, cancellationToken);
            checks += await VerifyMutationAsync(testRoot, baseline, "cross-split-scene", async root =>
            {
                var train = SituationTrainingContractJson.DeserializeRecord(
                    (await File.ReadAllLinesAsync(Path.Combine(root, "train.jsonl"), StrictUtf8, cancellationToken))[0]);
                await RewriteSingleRecordAsync(root, "dev.jsonl",
                    record => record with
                    {
                        Metadata = record.Metadata with { SourceSceneSha256 = train.Metadata.SourceSceneSha256 }
                    }, cancellationToken);
                await RefreshManifestAsync(root, cancellationToken);
            }, repositoryRoot, cancellationToken);
            checks += await VerifyMutationAsync(testRoot, baseline, "weight", async root =>
            {
                var path = Path.Combine(root, "train.jsonl");
                var records = (await File.ReadAllLinesAsync(path, StrictUtf8, cancellationToken))
                    .Select(SituationTrainingContractJson.DeserializeRecord).ToArray();
                records[1] = records[1] with
                {
                    Metadata = records[1].Metadata with
                    {
                        SampleWeight = 1,
                        WeightDenominator = 1
                    }
                };
                await WriteLinesAsync(path, records.Select(SituationTrainingContractJson.SerializeLine), cancellationToken);
                await RefreshManifestAsync(root, cancellationToken);
            }, repositoryRoot, cancellationToken);
            checks += await VerifyMutationAsync(testRoot, baseline, "manifest-extra", async root =>
            {
                await File.WriteAllTextAsync(Path.Combine(root, "extra.json"), "{}", Utf8NoBom, cancellationToken);
            }, repositoryRoot, cancellationToken);
            checks += await VerifyMutationAsync(testRoot, baseline, "manifest-missing", root =>
            {
                File.Delete(Path.Combine(root, "provenance.json"));
                return Task.CompletedTask;
            }, repositoryRoot, cancellationToken);
            checks += await VerifyMutationAsync(testRoot, baseline, "manifest-modified", async root =>
            {
                await File.AppendAllTextAsync(Path.Combine(root, "label-stats.json"), " ", Utf8NoBom, cancellationToken);
            }, repositoryRoot, cancellationToken);
        }
        finally
        {
            if (Directory.Exists(testRoot))
                Directory.Delete(testRoot, recursive: true);
        }
        Console.WriteLine($"Stage-four dataset statistics/provenance/validator checks passed: {checks}");
    }

    private static void VerifyIncrementalStatistics(ref int checks)
    {
        var sha = SituationArtifactIO.Sha256("statistics-split");
        var configSha = SituationArtifactIO.Sha256("prompt-config");
        var records = new[]
        {
            CreateRecord(SituationTrainingContractVerifier.BuildRecord(), SituationTrainingSplit.Train,
                SituationArtifactIO.Sha256("match-a"), "s0-a1", 640, 2, "scene-a-1"),
            CreateRecord(SituationTrainingContractVerifier.BuildRecord(), SituationTrainingSplit.Train,
                SituationArtifactIO.Sha256("match-a"), "s0-a1", 641, 2, "scene-a-2"),
            CreateRecord(SituationTrainingContractVerifier.BuildRecord(), SituationTrainingSplit.Dev,
                SituationArtifactIO.Sha256("match-b"), "s0-a1", 640, 1, "scene-b-1")
        };
        var matchA = records[0].Metadata.MatchRef;
        var matchB = records[2].Metadata.MatchRef;
        var selectionA = Selection("s0-a1", 2, 1);
        var selectionB = Selection("s0-a1", 1, 0);

        var batch = new SituationTrainingDatasetStatistics(sha, "compact-v1", configSha);
        foreach (var record in records) batch.AddRecord(record);
        batch.AddSelection(SituationTrainingSplit.Train, matchA, selectionA);
        batch.AddSelection(SituationTrainingSplit.Dev, matchB, selectionB);

        var first = new SituationTrainingDatasetStatistics(sha, "compact-v1", configSha);
        first.AddRecord(records[0]);
        first.AddRecord(records[1]);
        first.AddSelection(SituationTrainingSplit.Train, matchA, selectionA);
        var second = new SituationTrainingDatasetStatistics(sha, "compact-v1", configSha);
        second.AddRecord(records[2]);
        second.AddSelection(SituationTrainingSplit.Dev, matchB, selectionB);
        var merged = new SituationTrainingDatasetStatistics(sha, "compact-v1", configSha);
        merged.Merge(second);
        merged.Merge(first);
        Check(SituationCanonicalJson.Serialize(batch.CreateSnapshot()) ==
              SituationCanonicalJson.Serialize(merged.CreateSnapshot()),
            "per-match merge order equals batch normalized statistics", ref checks);
    }

    private static async Task<int> VerifyDirtyProvenanceAsync(
        string repositoryRoot,
        CancellationToken cancellationToken)
    {
        var marker = Path.Combine(repositoryRoot, $".situation-provenance-dirty-{Guid.NewGuid():N}");
        try
        {
            await File.WriteAllTextAsync(marker, "dirty", Utf8NoBom, cancellationToken);
            var splitPath = Path.Combine(repositoryRoot, "situation-implementation", "situation-stage4-split-v1.json");
            var split = await SituationDatasetSplit.LoadApprovedAsync(splitPath, cancellationToken);
            var schemas = await SituationTrainingSchemaRegistry.LoadAsync(repositoryRoot, cancellationToken);
            var rules = SituationAnalysisRuleLoader.LoadFrozen();
            var selection = SituationTrainingSelectionLoader.LoadFrozen();
            var prompt = SituationPromptRepresentationLoader.LoadFrozen();
            var document = await SituationTrainingProvenanceBuilder.CreateAsync(
                repositoryRoot, split, rules.Sha256, selection.Sha256, prompt.Sha256, schemas,
                SourcePaths, cancellationToken);
            if (!(document.GitDirty && document.SourceFiles.All(item =>
                    item.Sha256 == SituationArtifactIO.FileSha256Async(
                        SituationArtifactIO.ResolveRepositoryPath(repositoryRoot, item.Path, "test source"),
                        cancellationToken).GetAwaiter().GetResult())))
                throw new InvalidOperationException(
                    "Check failed: dirty provenance records gitDirty and actual working-tree source hashes");
            return 1;
        }
        finally
        {
            if (File.Exists(marker)) File.Delete(marker);
        }
    }

    private static async Task CreateArtifactAsync(
        string repositoryRoot,
        string root,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(root);
        var splitSource = Path.Combine(repositoryRoot, "situation-implementation", "situation-stage4-split-v1.json");
        var split = await SituationDatasetSplit.LoadApprovedAsync(splitSource, cancellationToken);
        File.Copy(splitSource, Path.Combine(root, "split.json"));
        var schemas = await SituationTrainingSchemaRegistry.LoadAsync(repositoryRoot, cancellationToken);
        var rules = SituationAnalysisRuleLoader.LoadFrozen();
        var selection = SituationTrainingSelectionLoader.LoadFrozen();
        var prompt = SituationPromptRepresentationLoader.LoadFrozen();
        var roundMappings = new[]
        {
            RoundMapping(split.Split.Train[0], split.SplitSha256),
            RoundMapping(split.Split.Dev[0], split.SplitSha256),
            RoundMapping(split.Split.Test[0], split.SplitSha256)
        };
        var provenance = await SituationTrainingProvenanceBuilder.CreateAsync(
            repositoryRoot, split, rules.Sha256, selection.Sha256, prompt.Sha256, schemas,
            SourcePaths, roundMappings, cancellationToken);

        var baseRecord = SituationTrainingContractVerifier.BuildRecord();
        var train = new[]
        {
            CreateRecord(baseRecord, SituationTrainingSplit.Train, split.Split.Train[0].MatchId,
                "s0-a1", 640, 2, "train-scene-1"),
            CreateRecord(baseRecord, SituationTrainingSplit.Train, split.Split.Train[0].MatchId,
                "s0-a1", 641, 2, "train-scene-2")
        };
        var dev = new[]
        {
            CreateRecord(baseRecord, SituationTrainingSplit.Dev, split.Split.Dev[0].MatchId,
                "s0-a1", 640, 1, "dev-scene-1")
        };
        var test = new[]
        {
            CreateRecord(baseRecord, SituationTrainingSplit.Test, split.Split.Test[0].MatchId,
                "s0-a1", 640, 1, "test-scene-1")
        };
        await WriteLinesAsync(Path.Combine(root, "train.jsonl"),
            train.Select(SituationTrainingContractJson.SerializeLine), cancellationToken);
        await WriteLinesAsync(Path.Combine(root, "dev.jsonl"),
            dev.Select(SituationTrainingContractJson.SerializeLine), cancellationToken);
        await WriteLinesAsync(Path.Combine(root, "test.jsonl"),
            test.Select(SituationTrainingContractJson.SerializeLine), cancellationToken);

        var stats = new SituationTrainingDatasetStatistics(
            split.SplitSha256, prompt.Config.SelectedVersion, prompt.Sha256);
        foreach (var member in split.Split.Train)
            stats.RegisterMatch(SituationTrainingSplit.Train, MatchRef(member.MatchId, split.SplitSha256));
        foreach (var member in split.Split.Dev)
            stats.RegisterMatch(SituationTrainingSplit.Dev, MatchRef(member.MatchId, split.SplitSha256));
        foreach (var member in split.Split.Test)
            stats.RegisterMatch(SituationTrainingSplit.Test, MatchRef(member.MatchId, split.SplitSha256));
        foreach (var record in train.Concat(dev).Concat(test)) stats.AddRecord(record);
        var snapshot = stats.CreateSnapshot();
        await SituationArtifactIO.WriteJsonAsync(
            Path.Combine(root, "label-stats.json"), snapshot.LabelStats, cancellationToken);
        await SituationArtifactIO.WriteJsonAsync(
            Path.Combine(root, "provenance.json"), provenance, cancellationToken);

        var manifest = new SituationTrainingDatasetManifestV1(
            SituationTrainingContractVersions.DatasetManifest,
            SituationArtifactStatus.Complete,
            SituationTrainingExportMode.Full,
            "template-prelabel-training",
            true,
            null,
            split.SplitSha256,
            split.Split.ParentManifestSha256,
            selection.Sha256,
            prompt.Sha256,
            ArtifactVersions(),
            schemas,
            snapshot.Counts,
            await InventoryAsync(root, cancellationToken));
        await SituationArtifactIO.WriteJsonAsync(Path.Combine(root, "manifest.json"), manifest, cancellationToken);
    }

    private static async Task<int> VerifyMutationAsync(
        string testRoot,
        string baseline,
        string name,
        Func<string, Task> mutate,
        string repositoryRoot,
        CancellationToken cancellationToken)
    {
        var root = Path.Combine(testRoot, name);
        CopyDirectory(baseline, root);
        await mutate(root);
        await CheckThrowsAsync(
            () => SituationTrainingDatasetValidator.VerifyAsync(repositoryRoot, root, cancellationToken),
            $"validator rejects {name}");
        return 1;
    }

    private static async Task RewriteSingleRecordAsync(
        string root,
        string fileName,
        Func<SituationTrainingRecordV1, SituationTrainingRecordV1> mutate,
        CancellationToken cancellationToken)
    {
        var path = Path.Combine(root, fileName);
        var record = SituationTrainingContractJson.DeserializeRecord(
            (await File.ReadAllLinesAsync(path, StrictUtf8, cancellationToken)).Single());
        await WriteLinesAsync(path, [SituationTrainingContractJson.SerializeLine(mutate(record))], cancellationToken);
    }

    private static async Task RefreshManifestAsync(string root, CancellationToken cancellationToken)
    {
        var path = Path.Combine(root, "manifest.json");
        var manifest = SituationTrainingContractJson.DeserializeManifest(
            await File.ReadAllTextAsync(path, StrictUtf8, cancellationToken));
        await SituationArtifactIO.WriteJsonAsync(
            path, manifest with { Files = await InventoryAsync(root, cancellationToken) }, cancellationToken);
    }

    private static async Task<IReadOnlyList<SituationArtifactFileV1>> InventoryAsync(
        string root,
        CancellationToken cancellationToken)
    {
        var result = new List<SituationArtifactFileV1>();
        foreach (var name in new[]
                 {
                     "dev.jsonl", "label-stats.json", "provenance.json", "split.json", "test.jsonl", "train.jsonl"
                 })
        {
            var path = Path.Combine(root, name);
            long? rows = name.EndsWith(".jsonl", StringComparison.Ordinal)
                ? (await File.ReadAllBytesAsync(path, cancellationToken)).LongCount(value => value == (byte)'\n')
                : null;
            result.Add(new(
                name,
                new FileInfo(path).Length,
                rows,
                await SituationArtifactIO.FileSha256Async(path, cancellationToken)));
        }
        return result;
    }

    private static SituationTrainingRecordV1 CreateRecord(
        SituationTrainingRecordV1 template,
        SituationTrainingSplit split,
        string matchId,
        string roundId,
        int tick,
        int denominator,
        string sourceSceneSeed)
    {
        var versions = IdentityVersions(SituationDatasetSplit.FrozenStageFourSplitSha256);
        var identity = SituationTrainingContractJson.CreateIdentity(matchId, roundId, tick, versions);
        var scene = template.Input.Scene with { Tick = tick };
        var record = template with
        {
            SampleId = identity.SampleId,
            Metadata = template.Metadata with
            {
                Split = split,
                MatchRef = identity.MatchRef,
                RoundRef = identity.RoundRef,
                Tick = tick,
                SampleWeight = 1d / denominator,
                WeightNumerator = 1,
                WeightDenominator = denominator,
                SourceSceneSha256 = SituationArtifactIO.Sha256(sourceSceneSeed),
                ModelInputSha256 = SituationCanonicalJson.Sha256(scene)
            },
            Input = template.Input with { Scene = scene }
        };
        record = record with { Metadata = record.Metadata with
        {
            SourceSceneSha256 = SituationCanonicalJson.Sha256(
                SituationTrainingRecordBuilder.RestoreSourceScene(record, matchId, roundId))
        } };
        SituationTrainingContractJson.Validate(record);
        return record;
    }

    private static string MatchRef(string matchId, string splitSha256) =>
        SituationTrainingContractJson.CreateIdentity(
            matchId, "s0-a1", 0, IdentityVersions(splitSha256)).MatchRef;

    private static SituationTrainingProvenanceRoundMappingV1 RoundMapping(
        SituationStageFourSplitMember member,
        string splitSha256)
    {
        var identity = SituationTrainingContractJson.CreateIdentity(
            member.MatchId, "s0-a1", 0, IdentityVersions(splitSha256));
        return new(identity.MatchRef, identity.RoundRef, "s0-a1");
    }

    private static SituationTrainingRoundSelectionV1 Selection(string roundId, int selected, int removed) => new(
        SituationTrainingContractVersions.Selection,
        SituationArtifactIO.Sha256("selection"),
        roundId,
        [],
        new Dictionary<string, SituationTrainingSelectionCategoryStats>
        {
            ["live-start"] = new(selected + removed, selected, 0, removed, 0),
            ["bomb-planted"] = new(0, 0, 0, 0, 1)
        },
        new Dictionary<string, int> { ["tick-outside-live-range"] = 2 },
        new Dictionary<string, int> { ["no-bomb-plant"] = 1 });

    private static SituationTrainingArtifactVersionsV1 ArtifactVersions() => new(
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

    private static SituationTrainingIdentityVersions IdentityVersions(string splitSha256) => new(
        SituationTrainingContractVersions.Data,
        splitSha256,
        SituationContractVersions.Scene,
        SituationSceneBuilder.BuilderVersion,
        SituationSceneBuilder.GeometryVersion,
        SituationContractVersions.Facts,
        SituationAnalysisRuleLoader.FrozenAnalysisRuleVersion,
        SituationContractVersions.Narrative,
        WinFeatureSampleBuilder.SemanticVersion,
        SituationTrainingContractVersions.Selection,
        SituationTrainingContractVersions.InputRepresentation);

    private static async Task WriteLinesAsync(
        string path,
        IEnumerable<string> lines,
        CancellationToken cancellationToken)
    {
        await File.WriteAllTextAsync(path, string.Join('\n', lines) + "\n", Utf8NoBom, cancellationToken);
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source))
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)));
        foreach (var directory in Directory.EnumerateDirectories(source))
            CopyDirectory(directory, Path.Combine(destination, Path.GetFileName(directory)));
    }

    private static async Task CheckThrowsAsync(
        Func<Task> action,
        string description)
    {
        try
        {
            await action();
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or JsonException)
        {
            return;
        }
        throw new InvalidOperationException($"Expected failure: {description}");
    }

    private static void Check(bool condition, string description, ref int checks)
    {
        if (!condition) throw new InvalidOperationException($"Check failed: {description}");
        checks++;
    }

    private static readonly UTF8Encoding Utf8NoBom = new(false);
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly string[] SourcePaths =
    [
        "apps/api/Features/Situation/Training/Models/SituationTrainingModels.cs",
        "apps/api/Features/Situation/Training/SituationTrainingCandidateSelector.cs",
        "apps/api/Features/Situation/Training/SituationTrainingContractJson.cs",
        "apps/api/Features/Situation/Workflows/SituationTrainingDatasetStatistics.cs",
        "apps/api/Features/Situation/Workflows/SituationTrainingDatasetValidator.cs",
        "apps/api/Features/Situation/Workflows/SituationTrainingProvenanceBuilder.cs"
    ];
}

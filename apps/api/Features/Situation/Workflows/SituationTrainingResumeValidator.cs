using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using CsDemoMap.Api.Models;

namespace CsDemoMap.Api.Services;

internal static partial class SituationTrainingResumeValidator
{
    private static readonly JsonSerializerOptions CheckpointJson = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = true
    };

    private static readonly IReadOnlySet<string> RequiredSourcePaths = new HashSet<string>(
        [
            "apps/api/Features/Situation/Training/Models/SituationTrainingModels.cs",
            "apps/api/Features/Situation/Training/SituationPromptRepresentation.cs",
            "apps/api/Features/Situation/Training/SituationTrainingCandidateSelector.cs",
            "apps/api/Features/Situation/Training/SituationTrainingContractJson.cs",
            "apps/api/Features/Situation/Workflows/SituationTrainingCheckpoint.cs",
            "apps/api/Features/Situation/Workflows/SituationTrainingPartialWriter.cs",
            "apps/api/Features/Situation/Workflows/SituationTrainingResumeValidator.cs"
        ],
        StringComparer.Ordinal);

    internal static string Serialize<T>(T value) => JsonSerializer.Serialize(value, CheckpointJson);

    internal static T Deserialize<T>(string json, string description)
    {
        try
        {
            SituationTrainingContractJson.RejectDuplicateProperties(json, description);
            SituationTrainingContractJson.RequireCompleteShape<T>(json, description);
            return JsonSerializer.Deserialize<T>(json, CheckpointJson)
                ?? throw new InvalidDataException($"{description} is empty.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                $"{description} JSON is invalid or contains unknown or missing fields.", exception);
        }
    }

    internal static string ComputeBindingSha256(SituationTrainingWriterBindingV1 binding) =>
        SituationCanonicalJson.Sha256(binding);

    internal static string ComputeSamplePlanSha256(
        string samplePlanVersion,
        IReadOnlyList<SituationTrainingPlannedMatchV1> matches) =>
        SituationCanonicalJson.Sha256(new { samplePlanVersion, matches });

    internal static void ValidateBinding(SituationTrainingWriterBindingV1 binding)
    {
        ArgumentNullException.ThrowIfNull(binding);
        if (!SituationArtifactIO.IsSha256(binding.SplitSha256, lowercaseOnly: true) ||
            !SituationArtifactIO.IsSha256(binding.ParentManifestSha256, lowercaseOnly: true) ||
            !SituationArtifactIO.IsSha256(binding.SelectionConfigSha256, lowercaseOnly: true) ||
            !SituationArtifactIO.IsSha256(binding.PromptConfigSha256, lowercaseOnly: true))
            throw new InvalidDataException("Training writer binding contains an invalid configuration hash.");
        if (!Enum.IsDefined(binding.OutputMode) || string.IsNullOrWhiteSpace(binding.Purpose) ||
            !SemanticVersion().IsMatch(binding.SamplePlanVersion))
            throw new InvalidDataException("Training writer binding mode, purpose, or sample plan version is invalid.");
        if (binding.Versions is null || binding.SchemaFiles is null || binding.SourceFiles is null ||
            binding.Matches is null || binding.AllowedArtifactPaths is null)
            throw new InvalidDataException("Training writer binding is incomplete.");

        var schemas = binding.SchemaFiles;
        if (schemas.Count == 0 ||
            !schemas.SequenceEqual(schemas.OrderBy(item => item.SchemaVersion, StringComparer.Ordinal)) ||
            schemas.Select(item => item.SchemaVersion).Distinct(StringComparer.Ordinal).Count() != schemas.Count ||
            schemas.Select(item => item.Path).Distinct(StringComparer.Ordinal).Count() != schemas.Count ||
            schemas.Any(item => !IsCanonicalRelativePath(item.Path) ||
                                !SituationArtifactIO.IsSha256(item.Sha256, lowercaseOnly: true)))
            throw new InvalidDataException("Training writer schema bindings are invalid or unstable.");

        var sourceFiles = binding.SourceFiles;
        if (sourceFiles.Count == 0 ||
            !sourceFiles.SequenceEqual(sourceFiles.OrderBy(item => item.Path, StringComparer.Ordinal)) ||
            sourceFiles.Select(item => item.Path).Distinct(StringComparer.Ordinal).Count() != sourceFiles.Count ||
            sourceFiles.Any(item => !IsCanonicalRelativePath(item.Path) ||
                                    !SituationArtifactIO.IsSha256(item.Sha256, lowercaseOnly: true)) ||
            !RequiredSourcePaths.IsSubsetOf(sourceFiles.Select(item => item.Path).ToHashSet(StringComparer.Ordinal)))
            throw new InvalidDataException("Training writer source bindings are invalid or unstable.");

        var matches = binding.Matches;
        if (matches.Count == 0 || matches.Where((item, index) => item.Ordinal != index).Any() ||
            !matches.SequenceEqual(matches.OrderBy(item => item.MatchRef, StringComparer.Ordinal)) ||
            matches.Any(item => !IsMatchRef(item.MatchRef) || !Enum.IsDefined(item.Split)) ||
            matches.Select(item => item.MatchRef).Distinct(StringComparer.Ordinal).Count() != matches.Count)
            throw new InvalidDataException("Training writer sample plan is invalid or not in stable match order.");
        if (binding.SamplePlanSha256 != ComputeSamplePlanSha256(binding.SamplePlanVersion, matches))
            throw new InvalidDataException("Training writer sample plan SHA-256 is invalid.");

        var allowed = binding.AllowedArtifactPaths;
        if (!allowed.SequenceEqual(allowed.Order(StringComparer.Ordinal)) ||
            allowed.Distinct(StringComparer.Ordinal).Count() != allowed.Count ||
            allowed.Any(path => !IsCanonicalRelativePath(path) || IsReservedPath(path)))
            throw new InvalidDataException("Training writer allowed artifact paths are invalid or unstable.");
    }

    internal static async Task ValidateRuntimeBindingAsync(
        string repositoryRoot,
        SituationTrainingWriterBindingV1 binding,
        CancellationToken cancellationToken)
    {
        ValidateBinding(binding);
        var root = Path.GetFullPath(repositoryRoot);
        if (!Directory.Exists(root))
            throw new DirectoryNotFoundException("Repository root does not exist.");
        await SituationTrainingSchemaRegistry.VerifyAsync(root, binding.SchemaFiles, cancellationToken);
        var allSchemas = await SituationTrainingSchemaRegistry.LoadAsync(root, cancellationToken);
        if (!binding.SchemaFiles.SequenceEqual(allSchemas))
            throw new InvalidDataException("Training writer binding does not include the complete schema registry.");
        if (SituationTrainingSelectionLoader.LoadFrozen().Sha256 != binding.SelectionConfigSha256)
            throw new InvalidDataException("Training selection configuration drifted since checkpoint creation.");
        if (SituationPromptRepresentationLoader.LoadFrozen().Sha256 != binding.PromptConfigSha256)
            throw new InvalidDataException("Training prompt configuration drifted since checkpoint creation.");
        foreach (var source in binding.SourceFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = SituationArtifactIO.ResolveRepositoryPath(root, source.Path, "Checkpoint source file");
            if (!File.Exists(path))
                throw new FileNotFoundException("Checkpoint source file is missing.", path);
            if (await SituationArtifactIO.FileSha256Async(path, cancellationToken) != source.Sha256)
                throw new InvalidDataException($"Checkpoint source file drifted: {source.Path}");
        }
    }

    internal static void ValidateManifestBinding(
        SituationTrainingDatasetManifestV1 manifest,
        SituationTrainingWriterBindingV1 binding,
        bool requireIncomplete)
    {
        SituationTrainingManifestValidator.Validate(manifest);
        if ((requireIncomplete && manifest.Status != SituationArtifactStatus.Incomplete) ||
            manifest.Mode != binding.OutputMode ||
            manifest.Purpose != binding.Purpose ||
            manifest.SplitSha256 != binding.SplitSha256 ||
            manifest.ParentManifestSha256 != binding.ParentManifestSha256 ||
            manifest.SelectionConfigSha256 != binding.SelectionConfigSha256 ||
            manifest.InputRepresentationConfigSha256 != binding.PromptConfigSha256 ||
            manifest.Versions != binding.Versions ||
            !manifest.SchemaFiles.SequenceEqual(binding.SchemaFiles))
            throw new InvalidDataException("Training manifest does not match the writer checkpoint binding.");
    }

    internal static void ValidateCheckpoint(
        SituationTrainingCheckpointV1 checkpoint,
        SituationTrainingWriterBindingV1 expectedBinding,
        string incompleteManifestSha256)
    {
        if (checkpoint.SchemaVersion != SituationTrainingCheckpointVersions.Checkpoint ||
            checkpoint.Status != SituationArtifactStatus.Incomplete ||
            !Enum.IsDefined(checkpoint.Phase))
            throw new InvalidDataException("Training checkpoint identity or status is invalid.");
        ValidateBinding(checkpoint.Binding);
        var expectedHash = ComputeBindingSha256(expectedBinding);
        if (checkpoint.BindingSha256 != expectedHash ||
            ComputeBindingSha256(checkpoint.Binding) != expectedHash)
            throw new InvalidDataException("Training checkpoint binding differs from the explicit resume binding.");
        if (checkpoint.IncompleteManifestSha256 != incompleteManifestSha256)
            throw new InvalidDataException("Incomplete manifest changed since checkpoint creation.");
        if (checkpoint.NextOrdinal < 0 || checkpoint.NextOrdinal > checkpoint.Binding.Matches.Count ||
            checkpoint.NextAttempt < 1 || checkpoint.NextMergeAttempt < 1)
            throw new InvalidDataException("Training checkpoint progress counters are invalid.");
        if (checkpoint.SplitRows is null ||
            !checkpoint.SplitRows.Keys.Order(StringComparer.Ordinal)
                .SequenceEqual(["dev", "test", "train"], StringComparer.Ordinal) ||
            checkpoint.SplitRows.Values.Any(value => value < 0))
            throw new InvalidDataException("Training checkpoint split row totals are invalid.");
        if (checkpoint.CompletedMatches is null || checkpoint.MergedFiles is null ||
            checkpoint.CompletedMatches.Count != checkpoint.NextOrdinal)
            throw new InvalidDataException("Training checkpoint completion list is invalid.");

        long train = 0, dev = 0, test = 0;
        for (var index = 0; index < checkpoint.CompletedMatches.Count; index++)
        {
            var completed = checkpoint.CompletedMatches[index];
            var planned = checkpoint.Binding.Matches[index];
            if (completed.Ordinal != index || completed.Ordinal != planned.Ordinal ||
                completed.MatchRef != planned.MatchRef || completed.Split != planned.Split)
                throw new InvalidDataException("Training checkpoint completed matches are not a stable plan prefix.");
            ValidateSpool(completed.Spool, completed, checkpoint.BindingSha256);
            ValidateStatistics(completed.Statistics);
            switch (completed.Split)
            {
                case SituationTrainingSplit.Train: train += completed.Spool.Rows; break;
                case SituationTrainingSplit.Dev: dev += completed.Spool.Rows; break;
                case SituationTrainingSplit.Test: test += completed.Spool.Rows; break;
            }
        }
        if (checkpoint.SplitRows["train"] != train || checkpoint.SplitRows["dev"] != dev ||
            checkpoint.SplitRows["test"] != test)
            throw new InvalidDataException("Training checkpoint split row totals do not match committed spools.");

        if (checkpoint.Phase == SituationTrainingWriterPhase.Writing && checkpoint.MergedFiles.Count != 0 ||
            checkpoint.Phase != SituationTrainingWriterPhase.Writing && checkpoint.NextOrdinal != checkpoint.Binding.Matches.Count ||
            checkpoint.Phase != SituationTrainingWriterPhase.Writing && checkpoint.MergedFiles.Count != 3)
            throw new InvalidDataException("Training checkpoint phase does not match merge progress.");
        if (checkpoint.MergedFiles.Count > 0)
        {
            var expectedSplits = new[]
            {
                SituationTrainingSplit.Train, SituationTrainingSplit.Dev, SituationTrainingSplit.Test
            };
            if (!checkpoint.MergedFiles.Select(item => item.Split).SequenceEqual(expectedSplits) ||
                checkpoint.MergedFiles.Any(item => item.Bytes < 0 || item.Rows < 0 ||
                    !SituationArtifactIO.IsSha256(item.Sha256, lowercaseOnly: true) ||
                    item.PartialPath != $".partial/merge/prepared/{SplitName(item.Split)}.jsonl.partial" ||
                    item.FinalPath != $"{SplitName(item.Split)}.jsonl"))
                throw new InvalidDataException("Training checkpoint merged file metadata is invalid.");
        }
    }

    internal static void ValidateStatistics(SituationTrainingStatisticsSnapshotV1 snapshot)
    {
        if (snapshot.SchemaVersion != SituationTrainingCheckpointVersions.Statistics ||
            string.IsNullOrWhiteSpace(snapshot.CanonicalJson) ||
            !SituationArtifactIO.IsSha256(snapshot.Sha256, lowercaseOnly: true) ||
            SituationArtifactIO.Sha256(snapshot.CanonicalJson) != snapshot.Sha256)
            throw new InvalidDataException("Training match statistics snapshot is invalid.");
        using var document = JsonDocument.Parse(snapshot.CanonicalJson);
        if (document.RootElement.ValueKind != JsonValueKind.Object ||
            SituationCanonicalJson.Serialize(document.RootElement) != snapshot.CanonicalJson)
            throw new InvalidDataException("Training match statistics snapshot is not a canonical JSON object.");
    }

    internal static string SplitName(SituationTrainingSplit split) => split switch
    {
        SituationTrainingSplit.Train => "train",
        SituationTrainingSplit.Dev => "dev",
        SituationTrainingSplit.Test => "test",
        _ => throw new InvalidDataException("Training split is invalid.")
    };

    internal static string SpoolDirectory(SituationTrainingPlannedMatchV1 match) =>
        $".partial/spool/{SplitName(match.Split)}/{match.Ordinal:D4}-{match.MatchRef}";

    internal static bool IsCanonicalRelativePath(string? path) =>
        !string.IsNullOrWhiteSpace(path) && !Path.IsPathRooted(path) && !path.Contains('\\') &&
        path.Split('/').All(segment => segment.Length > 0 && segment is not "." and not "..");

    internal static bool IsMatchRef(string? value) =>
        value is { Length: 70 } && value.StartsWith("match-", StringComparison.Ordinal) &&
        SituationArtifactIO.IsSha256(value[6..], lowercaseOnly: true);

    private static bool IsReservedPath(string path) =>
        path is "manifest.json" or "train.jsonl" or "dev.jsonl" or "test.jsonl" ||
        path.StartsWith(".partial/", StringComparison.Ordinal);

    private static void ValidateSpool(
        SituationTrainingSpoolFileV1 spool,
        SituationTrainingCompletedMatchV1 completed,
        string bindingSha256)
    {
        var planned = new SituationTrainingPlannedMatchV1(completed.Ordinal, completed.MatchRef, completed.Split);
        var expectedPath = $"{SpoolDirectory(planned)}/records.jsonl";
        if (spool.Path != expectedPath || spool.Bytes < 0 || spool.Rows < 0 ||
            !SituationArtifactIO.IsSha256(spool.Sha256, lowercaseOnly: true) ||
            !SituationArtifactIO.IsSha256(bindingSha256, lowercaseOnly: true))
            throw new InvalidDataException("Training committed spool metadata is invalid.");
    }

    [GeneratedRegex("^[a-z][a-z0-9-]*(?:\\.[a-z0-9][a-z0-9-]*)*$", RegexOptions.CultureInvariant)]
    private static partial Regex SemanticVersion();
}

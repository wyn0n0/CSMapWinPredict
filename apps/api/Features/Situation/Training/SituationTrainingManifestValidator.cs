using CsDemoMap.Api.Models;

namespace CsDemoMap.Api.Services;

internal static class SituationTrainingManifestValidator
{
    internal static void Validate(SituationTrainingDatasetManifestV1 manifest)
    {
        var errors = new List<string>();
        Require(manifest.SchemaVersion == SituationTrainingContractVersions.DatasetManifest,
            "manifest schemaVersion mismatch", errors);
        Require(Enum.IsDefined(manifest.Status), "manifest status is invalid", errors);
        Require(!string.IsNullOrWhiteSpace(manifest.Purpose), "manifest purpose is empty", errors);
        Require(manifest.Trainable == (manifest.Status == SituationArtifactStatus.Complete),
            "manifest trainable flag does not match status", errors);
        Require(IsSha256(manifest.SplitSha256), "manifest splitSha256 is invalid", errors);
        Require(IsSha256(manifest.ParentManifestSha256), "manifest parentManifestSha256 is invalid", errors);

        if (manifest.Versions is null)
        {
            errors.Add("manifest versions are missing");
        }
        else
        {
            var versions = manifest.Versions;
            Require(versions.Data == SituationTrainingContractVersions.Data, "data version mismatch", errors);
            Require(versions.Split == SituationTrainingContractVersions.Split, "split version mismatch", errors);
            Require(versions.TrainingRecord == SituationTrainingContractVersions.TrainingRecord,
                "training record version mismatch", errors);
            Require(versions.Scene == SituationContractVersions.Scene, "scene version mismatch", errors);
            Require(versions.SceneBuilder == SituationSceneBuilder.BuilderVersion, "scene builder version mismatch", errors);
            Require(versions.Geometry == SituationSceneBuilder.GeometryVersion, "geometry version mismatch", errors);
            Require(versions.Facts == SituationContractVersions.Facts, "Facts version mismatch", errors);
            Require(versions.AnalysisRules == SituationAnalysisRuleLoader.FrozenAnalysisRuleVersion,
                "analysis rules version mismatch", errors);
            Require(versions.Narrative == SituationContractVersions.Narrative, "Narrative version mismatch", errors);
            Require(versions.SemanticEligibility == WinFeatureSampleBuilder.SemanticVersion,
                "semantic eligibility version mismatch", errors);
            Require(versions.Selection == SituationTrainingContractVersions.Selection,
                "selection version mismatch", errors);
            Require(versions.InputRepresentation == SituationTrainingContractVersions.InputRepresentation,
                "input representation version mismatch", errors);
            Require(versions.ReviewCandidate == SituationTrainingContractVersions.ReviewCandidate,
                "review candidate version mismatch", errors);
            Require(versions.ReviewDecision == SituationTrainingContractVersions.ReviewDecision,
                "review decision version mismatch", errors);
            Require(versions.LabelStats == SituationTrainingContractVersions.LabelStats,
                "label stats version mismatch", errors);
            Require(versions.FrozenReviewLabel == SituationTrainingContractVersions.FrozenReviewLabel,
                "frozen review label version mismatch", errors);
            Require(versions.FrozenReviewManifest == SituationTrainingContractVersions.FrozenReviewManifest,
                "frozen review manifest version mismatch", errors);
            Require(versions.Review is null || IsSemanticVersion(versions.Review),
                "review version is invalid", errors);
        }

        ValidateSchemaFiles(manifest.SchemaFiles, errors);
        ValidateCounts(manifest.Counts, errors);
        ValidateFiles(manifest.Files, errors);
        Throw(errors);
    }

    internal static async Task VerifyAsync(
        string repositoryRoot,
        string artifactRoot,
        SituationTrainingDatasetManifestV1 manifest,
        CancellationToken cancellationToken)
    {
        Validate(manifest);
        await SituationTrainingSchemaRegistry.VerifyAsync(
            repositoryRoot, manifest.SchemaFiles, cancellationToken);

        foreach (var entry in manifest.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = SituationArtifactIO.ResolveRepositoryPath(
                artifactRoot, entry.Path, "Training artifact file");
            if (!File.Exists(path))
                throw new FileNotFoundException("Training artifact listed by the manifest is missing.", path);
            var info = new FileInfo(path);
            if (info.Length != entry.Bytes)
                throw new InvalidDataException($"Training artifact byte length mismatch: {entry.Path}");
            var sha256 = await SituationArtifactIO.FileSha256Async(path, cancellationToken);
            if (sha256 != entry.Sha256)
                throw new InvalidDataException($"Training artifact SHA-256 mismatch: {entry.Path}");
            if (entry.Rows is { } expectedRows)
            {
                var actualRows = await CountNonEmptyLinesAsync(path, cancellationToken);
                if (actualRows != expectedRows)
                    throw new InvalidDataException($"Training artifact row count mismatch: {entry.Path}");
            }
        }
    }

    private static void ValidateSchemaFiles(
        IReadOnlyList<SituationSchemaFileReferenceV1>? schemaFiles,
        ICollection<string> errors)
    {
        if (schemaFiles is null || schemaFiles.Count == 0)
        {
            errors.Add("manifest schemaFiles are missing");
            return;
        }
        Require(schemaFiles.SequenceEqual(schemaFiles.OrderBy(item => item.SchemaVersion, StringComparer.Ordinal)),
            "manifest schemaFiles are not ordinal-sorted", errors);
        Require(schemaFiles.Select(item => item.SchemaVersion).Distinct(StringComparer.Ordinal).Count() == schemaFiles.Count,
            "manifest schemaFiles contain duplicate versions", errors);
        Require(schemaFiles.Select(item => item.Path).Distinct(StringComparer.Ordinal).Count() == schemaFiles.Count,
            "manifest schemaFiles contain duplicate paths", errors);
        foreach (var item in schemaFiles)
        {
            Require(IsSemanticVersion(item.SchemaVersion), "schema file version is invalid", errors);
            Require(item.Path.StartsWith("schemas/situation/", StringComparison.Ordinal) &&
                    item.Path.EndsWith(".schema.json", StringComparison.Ordinal) &&
                    !item.Path.Contains('\\'),
                "schema file path is invalid", errors);
            Require(IsSha256(item.Sha256), "schema file SHA-256 is invalid", errors);
        }
    }

    private static void ValidateCounts(
        IReadOnlyDictionary<string, SituationDatasetCountsV1>? counts,
        ICollection<string> errors)
    {
        if (counts is null)
        {
            errors.Add("manifest counts are missing");
            return;
        }
        var expected = new[] { "dev", "test", "train" };
        Require(counts.Keys.Order(StringComparer.Ordinal).SequenceEqual(expected, StringComparer.Ordinal),
            "manifest counts must contain exactly train, dev, and test", errors);
        foreach (var count in counts.Values)
        {
            Require(count.Matches >= 0 && count.Rounds >= 0 && count.Samples >= 0,
                "manifest count is negative", errors);
            Require(count.Samples >= count.Rounds, "manifest has fewer samples than rounds", errors);
        }
    }

    private static void ValidateFiles(
        IReadOnlyList<SituationArtifactFileV1>? files,
        ICollection<string> errors)
    {
        if (files is null)
        {
            errors.Add("manifest files are missing");
            return;
        }
        Require(files.SequenceEqual(files.OrderBy(item => item.Path, StringComparer.Ordinal)),
            "manifest files are not ordinal-sorted", errors);
        Require(files.Select(item => item.Path).Distinct(StringComparer.Ordinal).Count() == files.Count,
            "manifest files contain duplicate paths", errors);
        foreach (var file in files)
        {
            Require(IsCanonicalRelativePath(file.Path), "manifest file path is invalid", errors);
            Require(file.Bytes >= 0, "manifest file byte length is negative", errors);
            Require(file.Rows is null or >= 0, "manifest file row count is negative", errors);
            Require(IsSha256(file.Sha256), "manifest file SHA-256 is invalid", errors);
        }
    }

    private static bool IsCanonicalRelativePath(string? path) =>
        !string.IsNullOrWhiteSpace(path) &&
        !Path.IsPathRooted(path) &&
        !path.Contains('\\') &&
        path.Split('/').All(segment => segment.Length > 0 && segment is not "." and not "..");

    private static bool IsSha256(string? value) =>
        SituationArtifactIO.IsSha256(value, lowercaseOnly: true);

    private static bool IsSemanticVersion(string? value) => value is not null &&
        System.Text.RegularExpressions.Regex.IsMatch(
            value,
            "^[a-z][a-z0-9-]*(?:\\.[a-z0-9][a-z0-9-]*)*$",
            System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    private static async Task<long> CountNonEmptyLinesAsync(string path, CancellationToken cancellationToken)
    {
        long count = 0;
        using var reader = new StreamReader(path);
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (line.Length > 0)
                count++;
        }
        return count;
    }

    private static void Require(bool condition, string message, ICollection<string> errors)
    {
        if (!condition)
            errors.Add(message);
    }

    private static void Throw(IReadOnlyCollection<string> errors)
    {
        if (errors.Count > 0)
            throw new InvalidDataException(string.Join(Environment.NewLine, errors));
    }
}

using System.Text;
using System.Text.Json;
using CsDemoMap.Api.Models;

namespace CsDemoMap.Api.Services;

internal static class SituationTrainingSchemaRegistry
{
    private const string Draft202012 = "https://json-schema.org/draft/2020-12/schema";

    private static readonly IReadOnlyList<SchemaDefinition> Definitions =
    [
        new(SituationContractVersions.Scene, "minimap-scene-v1.schema.json"),
        new(SituationContractVersions.Facts, "situation-facts-v1.schema.json"),
        new(SituationContractVersions.Narrative, "situation-narrative-v1.schema.json"),
        new(SituationTrainingContractVersions.Split, "situation-stage4-split-v1.schema.json"),
        new(SituationTrainingContractVersions.Selection, "situation-training-selection-v1.schema.json"),
        new(SituationTrainingContractVersions.PromptRepresentationConfig,
            "situation-prompt-representation-config-v1.schema.json"),
        new(SituationTrainingContractVersions.RepresentationMeasurement,
            "situation-representation-measurement-v1.schema.json"),
        new(SituationTrainingContractVersions.TrainingRecord, "situation-training-record-v1.schema.json"),
        new(SituationTrainingContractVersions.DatasetManifest, "situation-training-manifest-v1.schema.json"),
        new(SituationTrainingContractVersions.LabelStats, "situation-label-stats-v1.schema.json"),
        new(SituationTrainingContractVersions.ReviewCandidate, "situation-review-candidate-v1.schema.json"),
        new(SituationTrainingContractVersions.ReviewDecision, "situation-review-decision-v1.schema.json"),
        new(SituationTrainingContractVersions.FrozenReviewLabel, "situation-frozen-review-label-v1.schema.json"),
        new(SituationTrainingContractVersions.FrozenReviewManifest,
            "situation-frozen-review-manifest-v1.schema.json")
    ];

    internal static async Task<IReadOnlyList<SituationSchemaFileReferenceV1>> LoadAsync(
        string repositoryRoot,
        CancellationToken cancellationToken)
    {
        var references = new List<SituationSchemaFileReferenceV1>();
        foreach (var definition in Definitions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relativePath = $"schemas/situation/{definition.FileName}";
            var path = SituationArtifactIO.ResolveRepositoryPath(repositoryRoot, relativePath, "Situation schema");
            var bytes = await File.ReadAllBytesAsync(path, cancellationToken);
            if (bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble))
                throw new InvalidDataException($"Situation schema uses a UTF-8 BOM: {definition.FileName}");
            var json = Encoding.UTF8.GetString(bytes);
            SituationTrainingContractJson.RejectDuplicateProperties(json, $"Schema {definition.FileName}");
            using var document = JsonDocument.Parse(bytes);
            var root = document.RootElement;
            if (root.GetProperty("$schema").GetString() != Draft202012 ||
                root.GetProperty("type").GetString() != "object" ||
                root.GetProperty("additionalProperties").ValueKind != JsonValueKind.False ||
                !root.GetProperty("$id").GetString()!.EndsWith('/' + definition.FileName,
                    StringComparison.Ordinal) ||
                root.GetProperty("properties").GetProperty("schemaVersion")
                    .GetProperty("const").GetString() != definition.SchemaVersion)
                throw new InvalidDataException($"Situation schema identity or strict object boundary is invalid: {definition.FileName}");
            references.Add(new(
                definition.SchemaVersion,
                relativePath,
                SituationArtifactIO.Sha256(bytes)));
        }
        return references.OrderBy(item => item.SchemaVersion, StringComparer.Ordinal).ToArray();
    }

    internal static async Task VerifyAsync(
        string repositoryRoot,
        IReadOnlyList<SituationSchemaFileReferenceV1> references,
        CancellationToken cancellationToken)
    {
        var expected = await LoadAsync(repositoryRoot, cancellationToken);
        if (references.Count != expected.Count ||
            !references.SequenceEqual(expected))
            throw new InvalidDataException("Situation Schema versions, paths, or SHA-256 values do not match.");
    }

    private sealed record SchemaDefinition(string SchemaVersion, string FileName);
}

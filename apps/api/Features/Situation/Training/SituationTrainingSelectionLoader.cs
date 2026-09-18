using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using CsDemoMap.Api.Models;

namespace CsDemoMap.Api.Services;

internal static class SituationTrainingSelectionLoader
{
    internal const string FileName = "situation-training-selection-v1.json";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    private static readonly string[] ExpectedCore = ["live-start", "deployment-complete", "round-tail"];
    private static readonly string[] ExpectedEvents =
    [
        "first-contact", "first-damage", "first-casualty", "bomb-dropped", "bomb-picked-up",
        "bomb-planting-or-planted", "bomb-defusing", "clutch-2vN", "clutch-1vN"
    ];
    private static readonly string[] ExpectedRare =
        ["post-plant", "formation-split", "isolated", "high-contact-risk"];
    private static readonly string[] ExpectedTieBreakers = ["earlier-tick", "stable-candidate-id"];

    internal static SituationTrainingSelectionLoadResult LoadFrozen()
    {
        var assembly = typeof(SituationTrainingSelectionLoader).Assembly;
        var suffix = $".SituationTraining.{FileName}";
        var resources = assembly.GetManifestResourceNames()
            .Where(name => name.EndsWith(suffix, StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (resources.Length != 1)
            throw new InvalidDataException("Embedded situation training selection configuration is unavailable.");
        using var stream = assembly.GetManifestResourceStream(resources[0])
            ?? throw new InvalidDataException("Embedded situation training selection configuration is unavailable.");
        using var reader = new StreamReader(stream, detectEncodingFromByteOrderMarks: true);
        return ParseForVerification(reader.ReadToEnd(), resources[0]);
    }

    internal static SituationTrainingSelectionLoadResult ParseForVerification(string json, string resourceName)
    {
        SituationTrainingSelectionConfigV1 config;
        try
        {
            SituationTrainingContractJson.RejectDuplicateProperties(json, "Selection configuration");
            SituationTrainingContractJson.RequireCompleteShape<SituationTrainingSelectionConfigV1>(
                json, "Selection configuration");
            config = JsonSerializer.Deserialize<SituationTrainingSelectionConfigV1>(json, JsonOptions)
                ?? throw new InvalidDataException("Selection configuration is empty.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Selection configuration is invalid JSON.", exception);
        }
        Validate(config);
        var canonical = SituationCanonicalJson.Serialize(config);
        return new(config, canonical, SituationArtifactIO.Sha256(canonical), resourceName);
    }

    internal static void Validate(SituationTrainingSelectionConfigV1 config)
    {
        var errors = new List<string>();
        Require(config.SchemaVersion == SituationTrainingContractVersions.Selection,
            "schemaVersion mismatch", errors);
        Require(config.AlgorithmVersion == SituationTrainingContractVersions.Selection,
            "algorithmVersion mismatch", errors);
        Require(config.MaxSamplesPerRound == 16, "maxSamplesPerRound must be 16", errors);
        Require(config.EventMappingToleranceSeconds == 1,
            "eventMappingToleranceSeconds must be one second", errors);
        if (config.DeploymentComplete is null)
        {
            errors.Add("deploymentComplete is missing");
        }
        else
        {
            Require(config.DeploymentComplete.MinimumLiveElapsedSeconds == 10,
                "deployment minimum live time mismatch", errors);
            Require(config.DeploymentComplete.MinimumMovedPlayerRatio == 0.8,
                "deployment moved-player ratio mismatch", errors);
            Require(config.DeploymentComplete.MinimumNormalizedMovement == 0.08,
                "deployment movement threshold mismatch", errors);
            Require(config.DeploymentComplete.StableSeconds == 1,
                "deployment stability duration mismatch", errors);
        }
        Require(config.CorePriority?.SequenceEqual(ExpectedCore, StringComparer.Ordinal) == true,
            "core priority mismatch", errors);
        Require(config.EventPriority?.SequenceEqual(ExpectedEvents, StringComparer.Ordinal) == true,
            "event priority mismatch", errors);
        Require(config.RarePriority?.SequenceEqual(ExpectedRare, StringComparer.Ordinal) == true,
            "rare priority mismatch", errors);
        Require(config.CoverageStrategy == "replace-lower-priority-without-duplication",
            "coverage strategy mismatch", errors);
        Require(!config.AllowPhysicalSampleDuplication,
            "physical sample duplication must remain disabled", errors);
        Require(config.FillStrategy == "maximize-minimum-tick-distance", "fill strategy mismatch", errors);
        Require(config.FillTieBreakers?.SequenceEqual(ExpectedTieBreakers, StringComparer.Ordinal) == true,
            "fill tie-breakers mismatch", errors);
        var allTags = (config.CorePriority ?? []).Concat(config.EventPriority ?? []).Concat(config.RarePriority ?? [])
            .ToArray();
        Require(allTags.Length == allTags.Distinct(StringComparer.Ordinal).Count(),
            "selection categories contain duplicates", errors);
        Require(allTags.All(value => Regex.IsMatch(value, "^[A-Za-z0-9]+(?:-[A-Za-z0-9]+)*$",
                RegexOptions.CultureInvariant)),
            "selection category is invalid", errors);
        if (errors.Count > 0)
            throw new InvalidDataException(string.Join(Environment.NewLine, errors));
    }

    private static void Require(bool condition, string error, ICollection<string> errors)
    {
        if (!condition)
            errors.Add(error);
    }
}

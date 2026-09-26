using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using CsDemoMap.Api.Models;

namespace CsDemoMap.Api.Services;

internal static class SituationAnalysisRuleLoader
{
    internal const string ConfigSchemaVersion = "situation-analysis-rule-config-v1";
    internal const string FrozenAnalysisRuleVersion = "situation-analysis-rules-v1";
    internal const string CandidateFileName = "situation-analysis-rules-v1-candidate-2.json";
    internal const string FrozenFileName = "situation-analysis-rules-v1.json";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    private static readonly IReadOnlySet<string> KnownQualityCodes = new HashSet<string>(StringComparer.Ordinal)
    {
        SituationDataQualityCodes.MissingFrame,
        SituationDataQualityCodes.StaleFrame,
        SituationDataQualityCodes.HistoryTruncated,
        SituationDataQualityCodes.HistoryFileBoundary,
        SituationDataQualityCodes.HistoryRoundBoundary,
        SituationDataQualityCodes.HistoryGap,
        SituationDataQualityCodes.ClockUnknown,
        SituationDataQualityCodes.RosterIncomplete,
        SituationDataQualityCodes.EquipmentUnknown,
        SituationDataQualityCodes.UtilityUnknown,
        SituationDataQualityCodes.PositionInvalid,
        SituationDataQualityCodes.PositionOutsideRadar,
        SituationDataQualityCodes.RegionUnknown,
        SituationDataQualityCodes.FloorUnknown,
        SituationDataQualityCodes.EffectStateIncomplete,
        SituationDataQualityCodes.LegacyDefaultAmbiguous,
        SituationDataQualityCodes.HealthStateConflict
    };

    private static readonly IReadOnlySet<string> KnownRuleScopes = new HashSet<string>(StringComparer.Ordinal)
    {
        "formation", "contested", "pressure", "contact", "isolation", "spatial"
    };

    private static readonly IReadOnlySet<string> KnownTemplateIds = new HashSet<string>(StringComparer.Ordinal)
    {
        "bomb.state", "spatial-advantage", "contact-risk", "pressure.side",
        "contested-regions", "isolated-side", "alive.t", "alive.ct",
        "total-health.t", "total-health.ct", "formation.t", "formation.ct",
        "input-quality", "confidence"
    };

    public static SituationAnalysisRuleLoadResult LoadCandidate() => LoadEmbedded(CandidateFileName);

    public static SituationAnalysisRuleLoadResult LoadFrozen() => LoadEmbedded(FrozenFileName);

    public static SituationAnalysisRuleLoadResult LoadRaycast() => LoadEmbedded("situation-analysis-rules-v2-raycast-1.json");

    internal static SituationAnalysisRuleLoadResult LoadEmbeddedForVerification(string fileName)
    {
        if (!Regex.IsMatch(fileName, "^situation-analysis-rules-v1-candidate-[1-9][0-9]*\\.json$",
                RegexOptions.CultureInvariant))
            throw new InvalidDataException("Only embedded candidate rule history may be loaded for comparison.");
        return LoadEmbedded(fileName);
    }

    internal static SituationAnalysisRuleLoadResult ParseForVerification(string json, string resourceName)
    {
        SituationAnalysisRuleSet rules;
        try
        {
            RejectDuplicateProperties(json);
            rules = JsonSerializer.Deserialize<SituationAnalysisRuleSet>(json, JsonOptions)
                ?? throw new InvalidDataException("Situation rule configuration is empty.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Situation rule configuration is invalid JSON.", exception);
        }

        Validate(rules);
        var canonical = SituationCanonicalJson.Serialize(rules);
        return new(rules, canonical, SituationCanonicalJson.Sha256(rules), resourceName);
    }

    private static SituationAnalysisRuleLoadResult LoadEmbedded(string fileName)
    {
        var assembly = typeof(SituationAnalysisRuleLoader).Assembly;
        var suffix = $".SituationRules.{fileName}";
        var matches = assembly.GetManifestResourceNames()
            .Where(name => name.EndsWith(suffix, StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (matches.Length != 1)
            throw new InvalidDataException($"Embedded situation rule configuration is unavailable: {fileName}");
        using var stream = assembly.GetManifestResourceStream(matches[0])
            ?? throw new InvalidDataException($"Embedded situation rule configuration is unavailable: {fileName}");
        using var reader = new StreamReader(stream, detectEncodingFromByteOrderMarks: true);
        return ParseForVerification(reader.ReadToEnd(), matches[0]);
    }

    internal static void Validate(SituationAnalysisRuleSet rules)
    {
        var errors = new List<string>();
        Require(rules.SchemaVersion == ConfigSchemaVersion, "schemaVersion mismatch", errors);
        Require(IsSemanticVersion(rules.AnalysisRuleVersion), "analysisRuleVersion is invalid", errors);
        Require(rules.Map == "de_mirage", "map must be de_mirage", errors);
        Require(rules.SceneSchemaVersion == SituationContractVersions.Scene,
            "sceneSchemaVersion mismatch", errors);
        Require(rules.SceneBuilderVersion == SituationSceneBuilder.BuilderVersion,
            "sceneBuilderVersion mismatch", errors);
        Require(rules.GeometryVersion == SituationSceneBuilder.GeometryVersion,
            "geometryVersion mismatch", errors);
        Require(rules.FactsSchemaVersion == SituationContractVersions.Facts,
            "factsSchemaVersion mismatch", errors);
        Require(IsPositive(rules.ComparisonEpsilon) && rules.ComparisonEpsilon < 0.000001,
            "comparisonEpsilon must be positive and below canonical precision", errors);
        if (rules.Formation is null) errors.Add("formation is missing");
        else ValidateFormation(rules.Formation, errors);
        if (rules.Pressure is null) errors.Add("pressure is missing");
        else ValidatePressure(rules.Pressure, errors);
        if (rules.Contact is null) errors.Add("contact is missing");
        else ValidateContact(rules.Contact, errors);
        if (rules.Visibility is { } visibility)
        {
            Require(IsSemanticVersion(visibility.AssetVersion), "visibility assetVersion is invalid", errors);
            Require(Regex.IsMatch(visibility.AssetSha256 ?? "", "^[0-9a-f]{64}$"), "visibility asset hash is invalid", errors);
            Require(visibility.SampleHeights is { Length: > 0 and <= 5 } &&
                visibility.SampleHeights.All(h => double.IsFinite(h) && h is >= 16 and <= 72) &&
                visibility.SampleHeights.SequenceEqual(visibility.SampleHeights.Distinct().Order()),
                "visibility sample heights are invalid", errors);
            Require(double.IsFinite(visibility.EndpointEpsilon) && visibility.EndpointEpsilon is > 0 and <= 0.25,
                "visibility endpoint tolerance is invalid", errors);
            Require(rules.AnalysisRuleVersion != FrozenAnalysisRuleVersion,
                "frozen v1 cannot enable visibility", errors);
        }
        if (rules.AnalysisRuleVersion == "situation-analysis-rules-v2-raycast-1")
            Require(rules.Visibility is not null, "raycast rules require visibility", errors);
        if (rules.Isolation is null) errors.Add("isolation is missing");
        else ValidateIsolation(rules.Isolation, errors);
        if (rules.Spatial is null) errors.Add("spatial is missing");
        else ValidateSpatial(rules.Spatial, errors);
        if (rules.Confidence is null)
        {
            errors.Add("confidence is missing");
        }
        else
        {
            Require(IsPositive(rules.Confidence.BoundaryBand), "confidence boundaryBand must be positive", errors);
            Require(rules.Confidence.LowUnknownCount >= 2,
                "confidence lowUnknownCount must be at least two", errors);
        }

        Require(rules.QualityImpacts is not null, "qualityImpacts is missing", errors);
        if (rules.QualityImpacts is not null)
        {
            foreach (var entry in rules.QualityImpacts)
            {
                Require(KnownQualityCodes.Contains(entry.Key),
                    $"qualityImpacts contains unknown code {entry.Key}", errors);
                Require(entry.Value is not null && entry.Value.Count > 0,
                    $"qualityImpacts {entry.Key} is empty", errors);
                if (entry.Value is not null)
                {
                    Require(entry.Value.Distinct(StringComparer.Ordinal).Count() == entry.Value.Count,
                        $"qualityImpacts {entry.Key} contains duplicates", errors);
                    Require(entry.Value.All(KnownRuleScopes.Contains),
                        $"qualityImpacts {entry.Key} contains an unknown scope", errors);
                }
            }
        }

        Require(rules.TemplatePriority is not null && rules.TemplatePriority.Count >= 2,
            "templatePriority must contain at least two items", errors);
        if (rules.TemplatePriority is not null)
        {
            Require(rules.TemplatePriority.Distinct(StringComparer.Ordinal).Count() == rules.TemplatePriority.Count,
                "templatePriority contains duplicates", errors);
            Require(rules.TemplatePriority.All(KnownTemplateIds.Contains),
                "templatePriority contains an unknown evidence ID", errors);
            Require(rules.TemplatePriority.Contains("confidence", StringComparer.Ordinal) &&
                    rules.TemplatePriority.Contains("input-quality", StringComparer.Ordinal),
                "templatePriority must contain confidence and input-quality fallbacks", errors);
        }

        if (errors.Count > 0)
            throw new InvalidDataException(string.Join(Environment.NewLine, errors));
    }

    private static void ValidateFormation(SituationFormationRuleSet value, ICollection<string> errors)
    {
        Require(IsUnitDistance(value.LinkDistance), "formation linkDistance is invalid", errors);
        Require(IsRatio(value.GroupedCoverageRatio), "formation groupedCoverageRatio is invalid", errors);
        Require(IsUnitDistance(value.GroupedMaxDiameter), "formation groupedMaxDiameter is invalid", errors);
        Require(value.GroupedMaxDiameter >= value.LinkDistance,
            "formation groupedMaxDiameter is below linkDistance", errors);
        Require(value.SplitMinClusterSize is >= 2 and <= 5,
            "formation splitMinClusterSize is invalid", errors);
        Require(IsRatio(value.SplitCoverageRatio), "formation splitCoverageRatio is invalid", errors);
        Require(IsUnitDistance(value.SplitMinCentroidDistance),
            "formation splitMinCentroidDistance is invalid", errors);
        Require(value.SplitMinCentroidDistance > value.LinkDistance,
            "formation splitMinCentroidDistance must exceed linkDistance", errors);
    }

    private static void ValidatePressure(SituationPressureRuleSet value, ICollection<string> errors)
    {
        Require(IsUnitDistance(value.SiteRadius), "pressure siteRadius is invalid", errors);
        Require(value.MinPlayers is >= 2 and <= 5, "pressure minPlayers is invalid", errors);
        Require(IsPositive(value.MinHistorySeconds) && value.MinHistorySeconds <= 4.5,
            "pressure minHistorySeconds is invalid", errors);
        Require(IsUnitDistance(value.MinApproachDelta), "pressure minApproachDelta is invalid", errors);
    }

    private static void ValidateContact(SituationContactRuleSet value, ICollection<string> errors)
    {
        Require(IsUnitDistance(value.HighDistance), "contact highDistance is invalid", errors);
        Require(IsUnitDistance(value.MediumDistance), "contact mediumDistance is invalid", errors);
        Require(IsUnitDistance(value.CueDistance), "contact cueDistance is invalid", errors);
        Require(value.HighDistance < value.MediumDistance && value.MediumDistance < value.CueDistance,
            "contact distance thresholds must be strictly increasing", errors);
        Require(double.IsFinite(value.FacingDotMin) && value.FacingDotMin is >= -1 and <= 1,
            "contact facingDotMin is invalid", errors);
        Require(IsPositive(value.MinHistorySeconds) && value.MinHistorySeconds <= 4.5,
            "contact minHistorySeconds is invalid", errors);
        Require(IsUnitDistance(value.MinClosingDelta), "contact minClosingDelta is invalid", errors);
    }

    private static void ValidateIsolation(SituationIsolationRuleSet value, ICollection<string> errors)
    {
        Require(IsUnitDistance(value.AllyDistanceMin), "isolation allyDistanceMin is invalid", errors);
        Require(IsUnitDistance(value.EnemyCloserMargin), "isolation enemyCloserMargin is invalid", errors);
        Require(value.EnemyCloserMargin < value.AllyDistanceMin,
            "isolation enemyCloserMargin must be below allyDistanceMin", errors);
    }

    private static void ValidateSpatial(SituationSpatialRuleSet value, ICollection<string> errors)
    {
        Require(IsUnitDistance(value.LocalRadius), "spatial localRadius is invalid", errors);
        Require(IsPositive(value.AlivePerPlayer), "spatial alivePerPlayer is invalid", errors);
        Require(IsPositive(value.AliveMaxContribution), "spatial aliveMaxContribution is invalid", errors);
        Require(IsPositive(value.HealthPerHundred), "spatial healthPerHundred is invalid", errors);
        Require(IsPositive(value.HealthMaxContribution), "spatial healthMaxContribution is invalid", errors);
        Require(IsPositive(value.LocalPerPlayer), "spatial localPerPlayer is invalid", errors);
        Require(IsPositive(value.LocalMaxContribution), "spatial localMaxContribution is invalid", errors);
        Require(IsNonNegative(value.PressureContribution), "spatial pressureContribution is invalid", errors);
        Require(IsNonNegative(value.PlantedBombContribution), "spatial plantedBombContribution is invalid", errors);
        Require(IsNonNegative(value.DefusingBombContribution), "spatial defusingBombContribution is invalid", errors);
        Require(IsNonNegative(value.IsolationContribution), "spatial isolationContribution is invalid", errors);
        Require(value.RequiredComponents is >= 1 and <= 6, "spatial requiredComponents is invalid", errors);
        Require(IsPositive(value.DecisiveScore), "spatial decisiveScore is invalid", errors);
        Require(IsNonNegative(value.EvenScore) && value.EvenScore < value.DecisiveScore,
            "spatial evenScore must be non-negative and below decisiveScore", errors);
    }

    private static bool IsSemanticVersion(string value) =>
        !string.IsNullOrWhiteSpace(value) &&
        Regex.IsMatch(value, "^[a-z][a-z0-9-]*(?:\\.[a-z][a-z0-9-]*)*$", RegexOptions.CultureInvariant);

    private static bool IsPositive(double value) => double.IsFinite(value) && value > 0;

    private static bool IsNonNegative(double value) => double.IsFinite(value) && value >= 0;

    private static void RejectDuplicateProperties(string json)
    {
        using var document = JsonDocument.Parse(json);
        Visit(document.RootElement, "$" );

        static void Visit(JsonElement element, string path)
        {
            if (element.ValueKind == JsonValueKind.Object)
            {
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var property in element.EnumerateObject())
                {
                    if (!names.Add(property.Name))
                        throw new InvalidDataException($"Situation rule configuration contains duplicate property {path}.{property.Name}.");
                    Visit(property.Value, $"{path}.{property.Name}");
                }
            }
            else if (element.ValueKind == JsonValueKind.Array)
            {
                var index = 0;
                foreach (var item in element.EnumerateArray())
                    Visit(item, $"{path}[{index++}]");
            }
        }
    }

    private static bool IsRatio(double value) => double.IsFinite(value) && value is > 0 and <= 1;

    private static bool IsUnitDistance(double value) => double.IsFinite(value) && value is > 0 and <= 1.5;

    private static void Require(bool condition, string message, ICollection<string> errors)
    {
        if (!condition)
            errors.Add(message);
    }
}

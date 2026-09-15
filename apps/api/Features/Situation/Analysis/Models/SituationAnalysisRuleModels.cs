namespace CsDemoMap.Api.Models;

internal sealed record SituationAnalysisRuleSet(
    string SchemaVersion,
    string AnalysisRuleVersion,
    string Map,
    string SceneSchemaVersion,
    string SceneBuilderVersion,
    string GeometryVersion,
    string FactsSchemaVersion,
    double ComparisonEpsilon,
    SituationFormationRuleSet Formation,
    SituationPressureRuleSet Pressure,
    SituationContactRuleSet Contact,
    SituationIsolationRuleSet Isolation,
    SituationSpatialRuleSet Spatial,
    SituationConfidenceRuleSet Confidence,
    IReadOnlyDictionary<string, IReadOnlyList<string>> QualityImpacts,
    IReadOnlyList<string> TemplatePriority);

internal sealed record SituationFormationRuleSet(
    double LinkDistance,
    double GroupedCoverageRatio,
    double GroupedMaxDiameter,
    int SplitMinClusterSize,
    double SplitCoverageRatio,
    double SplitMinCentroidDistance);

internal sealed record SituationPressureRuleSet(
    double SiteRadius,
    int MinPlayers,
    double MinHistorySeconds,
    double MinApproachDelta);

internal sealed record SituationContactRuleSet(
    double HighDistance,
    double MediumDistance,
    double CueDistance,
    double FacingDotMin,
    double MinHistorySeconds,
    double MinClosingDelta);

internal sealed record SituationIsolationRuleSet(
    double AllyDistanceMin,
    double EnemyCloserMargin);

internal sealed record SituationSpatialRuleSet(
    double LocalRadius,
    double AlivePerPlayer,
    double AliveMaxContribution,
    double HealthPerHundred,
    double HealthMaxContribution,
    double LocalPerPlayer,
    double LocalMaxContribution,
    double PressureContribution,
    double PlantedBombContribution,
    double DefusingBombContribution,
    double IsolationContribution,
    int RequiredComponents,
    double DecisiveScore,
    double EvenScore);

internal sealed record SituationConfidenceRuleSet(
    double BoundaryBand,
    int LowUnknownCount);

internal sealed record SituationAnalysisRuleLoadResult(
    SituationAnalysisRuleSet Rules,
    string CanonicalJson,
    string Sha256,
    string ResourceName);

internal sealed record SituationRuleMargin(
    string Rule,
    string Metric,
    double Value,
    double Threshold,
    double Margin,
    string Decision);

internal sealed record SituationRuleDiagnostics(
    IReadOnlyList<SituationRuleMargin> Margins,
    IReadOnlyDictionary<string, string> Decisions,
    IReadOnlyDictionary<string, double> SpatialComponents,
    double? SpatialScore,
    int UnknownCount,
    bool HasRelevantError,
    bool HasRelevantWarning);

internal sealed record SituationFactsAnalysisResult(
    SituationFactsV1 Facts,
    string CanonicalJson,
    string Sha256,
    SituationRuleDiagnostics Diagnostics);

internal sealed record SituationNarrativeAnalysisResult(
    SituationNarrativeV1 Narrative,
    string CanonicalJson,
    string Sha256);

internal sealed record SituationDeterministicAnalysisResult(
    SituationFactsAnalysisResult Facts,
    SituationNarrativeAnalysisResult Narrative,
    string RuleConfigSha256);

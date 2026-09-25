using System.Text.Json.Serialization;
using CsDemoMap.Api.Services;

namespace CsDemoMap.Api.Models;

internal static class SituationTrainingContractVersions
{
    internal const string Data = "situation-training-data-v1";
    internal const string Split = "situation-stage4-split-v1";
    internal const string TrainingRecord = "situation-training-record-v1";
    internal const string Selection = "situation-training-selection-v1";
    internal const string InputRepresentation = "compact-v1";
    internal const string PromptRepresentationConfig = "situation-prompt-representation-config-v1";
    internal const string RepresentationMeasurement = "situation-representation-measurement-v1";
    internal const string DatasetManifest = "situation-training-manifest-v1";
    internal const string LabelStats = "situation-label-stats-v1";
    internal const string ReviewCandidate = "situation-review-candidate-v1";
    internal const string ReviewDecision = "situation-review-decision-v1";
    internal const string FrozenReviewManifest = "situation-frozen-review-manifest-v1";
    internal const string FrozenReviewLabel = "situation-frozen-review-label-v1";
    internal const string Provenance = "situation-training-provenance-v1";
}

[JsonConverter(typeof(JsonStringEnumConverter<SituationTrainingSplit>))]
internal enum SituationTrainingSplit
{
    [JsonStringEnumMemberName("train")]
    Train,
    [JsonStringEnumMemberName("dev")]
    Dev,
    [JsonStringEnumMemberName("test")]
    Test
}

[JsonConverter(typeof(JsonStringEnumConverter<SituationTrainingPhase>))]
internal enum SituationTrainingPhase
{
    [JsonStringEnumMemberName("live")]
    Live,
    [JsonStringEnumMemberName("post-plant")]
    PostPlant
}

[JsonConverter(typeof(JsonStringEnumConverter<SituationTrainingLabelSource>))]
internal enum SituationTrainingLabelSource
{
    [JsonStringEnumMemberName("template-prelabel")]
    TemplatePrelabel,
    [JsonStringEnumMemberName("human-approved")]
    HumanApproved,
    [JsonStringEnumMemberName("human-modified")]
    HumanModified
}

[JsonConverter(typeof(JsonStringEnumConverter<SituationTrainingReviewStatus>))]
internal enum SituationTrainingReviewStatus
{
    [JsonStringEnumMemberName("unreviewed")]
    Unreviewed,
    [JsonStringEnumMemberName("approved")]
    Approved,
    [JsonStringEnumMemberName("modified")]
    Modified,
    [JsonStringEnumMemberName("rejected")]
    Rejected
}

[JsonConverter(typeof(JsonStringEnumConverter<SituationArtifactStatus>))]
internal enum SituationArtifactStatus
{
    [JsonStringEnumMemberName("incomplete")]
    Incomplete,
    [JsonStringEnumMemberName("complete")]
    Complete
}

[JsonConverter(typeof(JsonStringEnumConverter<SituationTrainingExportMode>))]
internal enum SituationTrainingExportMode
{
    [JsonStringEnumMemberName("pilot")]
    Pilot,
    [JsonStringEnumMemberName("full")]
    Full
}

[JsonConverter(typeof(JsonStringEnumConverter<SituationReviewDecisionKind>))]
internal enum SituationReviewDecisionKind
{
    [JsonStringEnumMemberName("approved")]
    Approved,
    [JsonStringEnumMemberName("modified")]
    Modified,
    [JsonStringEnumMemberName("rejected")]
    Rejected
}

internal sealed record SituationStageFourSplitMember(
    string FileName,
    string MatchId,
    string Role);

internal sealed record SituationStageFourSplitDocument(
    string SchemaVersion,
    string ParentSplitReference,
    string ParentSplitSha256,
    string ParentManifestReference,
    string ParentManifestSha256,
    string ParentSemanticVersion,
    string AlgorithmVersion,
    string DomainSeparator,
    int Seed,
    string HashAlgorithm,
    string Ordering,
    int SourceDemoCount,
    int TrainDemoCount,
    int DevDemoCount,
    int TestDemoCount,
    IReadOnlyList<SituationStageFourSplitMember> Train,
    IReadOnlyList<SituationStageFourSplitMember> Dev,
    IReadOnlyList<SituationStageFourSplitMember> Test);

internal sealed record SituationStageFourSplitResult(
    SituationStageFourSplitDocument Split,
    string SplitSha256);

internal sealed record SituationTrainingIdentityVersions(
    string DataVersion,
    string SplitSha256,
    string SceneSchemaVersion,
    string SceneBuilderVersion,
    string GeometryVersion,
    string FactsSchemaVersion,
    string AnalysisRuleVersion,
    string NarrativeSchemaVersion,
    string SemanticEligibilityVersion,
    string SelectionVersion,
    string InputRepresentationVersion);

internal sealed record SituationTrainingIdentityV1(
    string SampleId,
    string MatchRef,
    string RoundRef);

internal sealed record SituationTrainingMetadataV1(
    [property: JsonRequired] SituationTrainingSplit Split,
    [property: JsonRequired] string MatchRef,
    [property: JsonRequired] string RoundRef,
    [property: JsonRequired] int Tick,
    [property: JsonRequired] SituationTrainingPhase Phase,
    [property: JsonRequired] IReadOnlyList<string> SelectionTags,
    [property: JsonRequired] double SampleWeight,
    [property: JsonRequired] int WeightNumerator,
    [property: JsonRequired] int WeightDenominator,
    [property: JsonRequired] string SourceSceneSha256,
    [property: JsonRequired] string ModelInputSha256,
    [property: JsonRequired] string FactsSha256,
    [property: JsonRequired] string PrelabelSha256);

internal sealed record SituationTrainingInputV1(
    [property: JsonRequired] SituationModelInputV1 Scene,
    [property: JsonRequired] SituationFactsV1 Facts,
    [property: JsonRequired] IReadOnlyList<string> AllowedEvidenceIds);

internal sealed record SituationTrainingRecordV1(
    [property: JsonRequired] string SchemaVersion,
    [property: JsonRequired] string SampleId,
    [property: JsonRequired] SituationTrainingMetadataV1 Metadata,
    [property: JsonRequired] SituationTrainingInputV1 Input,
    [property: JsonRequired] SituationNarrativeV1 Output,
    [property: JsonRequired] SituationTrainingLabelSource LabelSource,
    [property: JsonRequired] SituationTrainingReviewStatus ReviewStatus);

internal sealed record SituationPromptPairV1(
    SituationTrainingInputV1 Input,
    SituationNarrativeV1 Output);

internal sealed record SituationDeploymentSelectionRuleV1(
    double MinimumLiveElapsedSeconds,
    double MinimumMovedPlayerRatio,
    double MinimumNormalizedMovement,
    double StableSeconds);

internal sealed record SituationRoundTailSelectionRuleV1(
    double MaximumRoundRemainingSeconds,
    double MaximumBombRemainingSeconds);

internal sealed record SituationClutchSelectionRuleV1(
    int SideAlive,
    int MinimumOpponentAlive);

internal sealed record SituationTrainingSelectionConfigV1(
    string SchemaVersion,
    string AlgorithmVersion,
    int MaxSamplesPerRound,
    double EventMappingToleranceSeconds,
    SituationDeploymentSelectionRuleV1 DeploymentComplete,
    SituationRoundTailSelectionRuleV1 RoundTail,
    SituationClutchSelectionRuleV1 Clutch2vN,
    SituationClutchSelectionRuleV1 Clutch1vN,
    string PostPlantPreference,
    IReadOnlyList<string> CorePriority,
    IReadOnlyList<string> EventPriority,
    IReadOnlyList<string> RarePriority,
    string CoverageStrategy,
    bool AllowPhysicalSampleDuplication,
    string FillStrategy,
    IReadOnlyList<string> FillTieBreakers);

internal sealed record SituationTrainingSelectionLoadResult(
    SituationTrainingSelectionConfigV1 Config,
    string CanonicalJson,
    string Sha256,
    string ResourceName);

internal sealed record SituationPromptTemplateV1(
    string Version,
    string SystemText,
    string InputPrefix,
    string OutputPrefix);

internal sealed record SituationPromptRepresentationConfigV1(
    string SchemaVersion,
    string SelectedVersion,
    int CandidateMaxUtf8Bytes,
    int CandidateMaxUnicodeCharacters,
    string PercentileMethod,
    SituationPromptTemplateV1 Expanded,
    SituationPromptTemplateV1 Compact,
    IReadOnlyDictionary<string, string> ShortKeys);

internal sealed record SituationPromptRepresentationLoadResult(
    SituationPromptRepresentationConfigV1 Config,
    string CanonicalJson,
    string Sha256,
    string ResourceName);

internal sealed record SituationTrainingArtifactVersionsV1(
    [property: JsonRequired] string Data,
    [property: JsonRequired] string Split,
    [property: JsonRequired] string TrainingRecord,
    [property: JsonRequired] string Scene,
    [property: JsonRequired] string SceneBuilder,
    [property: JsonRequired] string Geometry,
    [property: JsonRequired] string Facts,
    [property: JsonRequired] string AnalysisRules,
    [property: JsonRequired] string Narrative,
    [property: JsonRequired] string SemanticEligibility,
    [property: JsonRequired] string Selection,
    [property: JsonRequired] string InputRepresentation,
    [property: JsonRequired] string InputRepresentationConfig,
    [property: JsonRequired] string RepresentationMeasurement,
    [property: JsonRequired] string ReviewCandidate,
    [property: JsonRequired] string ReviewDecision,
    [property: JsonRequired] string LabelStats,
    [property: JsonRequired] string FrozenReviewLabel,
    [property: JsonRequired] string FrozenReviewManifest,
    [property: JsonRequired] string? Review);

internal sealed record SituationSchemaFileReferenceV1(
    [property: JsonRequired] string SchemaVersion,
    [property: JsonRequired] string Path,
    [property: JsonRequired] string Sha256);

internal sealed record SituationArtifactFileV1(
    [property: JsonRequired] string Path,
    [property: JsonRequired] long Bytes,
    [property: JsonRequired] long? Rows,
    [property: JsonRequired] string Sha256);

internal sealed record SituationDatasetCountsV1(
    [property: JsonRequired] int Matches,
    [property: JsonRequired] int Rounds,
    [property: JsonRequired] int Samples);

internal sealed record SituationTrainingDatasetManifestV1(
    [property: JsonRequired] string SchemaVersion,
    [property: JsonRequired] SituationArtifactStatus Status,
    [property: JsonRequired] SituationTrainingExportMode Mode,
    [property: JsonRequired] string Purpose,
    [property: JsonRequired] bool Trainable,
    [property: JsonRequired] int? SampleLimit,
    [property: JsonRequired] string SplitSha256,
    [property: JsonRequired] string ParentManifestSha256,
    [property: JsonRequired] string SelectionConfigSha256,
    [property: JsonRequired] string InputRepresentationConfigSha256,
    [property: JsonRequired] SituationTrainingArtifactVersionsV1 Versions,
    [property: JsonRequired] IReadOnlyList<SituationSchemaFileReferenceV1> SchemaFiles,
    [property: JsonRequired] IReadOnlyDictionary<string, SituationDatasetCountsV1> Counts,
    [property: JsonRequired] IReadOnlyList<SituationArtifactFileV1> Files);

internal sealed record SituationLabelStatMetricV1(string Name, double Value);

internal sealed record SituationLabelStatSectionV1(
    string Name,
    IReadOnlyList<SituationLabelStatMetricV1> Metrics);

internal sealed record SituationLabelStatsV1(
    string SchemaVersion,
    string DatasetSchemaVersion,
    string SplitSha256,
    string InputRepresentationVersion,
    string InputRepresentationConfigSha256,
    bool ExactTokenizerMeasured,
    IReadOnlyList<SituationLabelStatSectionV1> Sections);

internal sealed record SituationRepresentationDimensionsV1(
    int Utf8Bytes,
    int UnicodeCharacters,
    int MaxLineLength,
    int MaxNestedArrayLength,
    bool ExceedsCandidateLimit);

internal sealed record SituationRepresentationMeasurementV1(
    string SchemaVersion,
    string SampleId,
    SituationRepresentationDimensionsV1 StructuredRecord,
    SituationRepresentationDimensionsV1 ModelInput,
    SituationRepresentationDimensionsV1 ExpandedPrompt,
    SituationRepresentationDimensionsV1 CompactPrompt,
    int SceneUtf8Bytes,
    int FactsUtf8Bytes,
    int EvidenceUtf8Bytes,
    int LabelUtf8Bytes);

internal sealed record SituationProvenanceSourceFileV1(
    string Path,
    string Sha256);

internal sealed record SituationReviewCandidateV1(
    string SchemaVersion,
    int ReviewOrdinal,
    string SampleId,
    SituationTrainingSplit Split,
    string MatchRef,
    string RoundRef,
    IReadOnlyList<string> SelectionTags,
    SituationTrainingInputV1 Input,
    SituationNarrativeV1 Candidate,
    string RecordSha256,
    string CandidateSha256);

internal sealed record SituationReviewEvaluationsV1(
    [property: JsonRequired] bool FactsCorrect,
    [property: JsonRequired] bool FocusReasonable,
    [property: JsonRequired] bool SummaryAccurateUseful,
    [property: JsonRequired] bool Hallucination);

internal sealed record SituationReviewDecisionV1(
    [property: JsonRequired] string SchemaVersion,
    [property: JsonRequired] string DatasetSha256,
    [property: JsonRequired] string CandidateSha256,
    [property: JsonRequired] string SampleId,
    [property: JsonRequired] int SampleRevision,
    [property: JsonRequired] SituationReviewDecisionKind Decision,
    [property: JsonRequired] SituationReviewEvaluationsV1 Evaluations,
    [property: JsonRequired] IReadOnlyList<string> IssueFields,
    [property: JsonRequired] IReadOnlyList<string> IssueCodes,
    [property: JsonRequired] string? Note,
    [property: JsonRequired] SituationNarrativeV1? FinalNarrative,
    [property: JsonRequired] string? FinalNarrativeSha256,
    [property: JsonRequired] string? RejectReasonCode,
    [property: JsonRequired] bool BlockingIssue,
    [property: JsonRequired] DateTimeOffset ReviewedAtUtc,
    [property: JsonRequired] string DecisionSha256);

internal sealed record SituationFrozenReviewLabelV1(
    string SchemaVersion,
    string SampleId,
    SituationNarrativeV1 Narrative,
    SituationTrainingLabelSource LabelSource,
    string ReviewVersion,
    IReadOnlyList<string> AllowedUse,
    int? RecommendedSftRepeat);

internal sealed record SituationFrozenReviewManifestV1(
    string SchemaVersion,
    SituationArtifactStatus Status,
    string ReviewVersion,
    string DatasetSha256,
    string ReviewCandidateSha256,
    IReadOnlyList<SituationSchemaFileReferenceV1> SchemaFiles,
    IReadOnlyDictionary<string, int> Counts,
    IReadOnlyList<SituationArtifactFileV1> Files);

internal sealed record SituationProvenanceDemoMappingV1(
    string FileName,
    string MatchRef,
    string MatchId);

internal sealed record SituationTrainingProvenanceV1(
    string SchemaVersion,
    string SplitSha256,
    string AnalysisRulesSha256,
    string SelectionConfigSha256,
    string InputRepresentationConfigSha256,
    string? SourceRevision,
    IReadOnlyList<SituationProvenanceSourceFileV1> SourceFiles,
    IReadOnlyList<SituationProvenanceDemoMappingV1> DemoMappings);

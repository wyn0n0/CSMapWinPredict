using System.Text.Json.Serialization;
using CsDemoMap.Api.Models;

namespace CsDemoMap.Api.Services;

internal static class SituationTrainingCheckpointVersions
{
    internal const string Checkpoint = "situation-training-checkpoint-v1";
    internal const string Transaction = "situation-training-match-transaction-v1";
    internal const string Commit = "situation-training-match-commit-v1";
    internal const string Merge = "situation-training-merge-v1";
    internal const string Statistics = "situation-training-match-statistics-v1";
}

[JsonConverter(typeof(JsonStringEnumConverter<SituationTrainingWriterPhase>))]
internal enum SituationTrainingWriterPhase
{
    [JsonStringEnumMemberName("writing")]
    Writing,
    [JsonStringEnumMemberName("merged")]
    Merged,
    [JsonStringEnumMemberName("published")]
    Published
}

internal sealed record SituationTrainingPlannedMatchV1(
    int Ordinal,
    string MatchRef,
    SituationTrainingSplit Split);

internal sealed record SituationTrainingWriterBindingV1(
    string SplitSha256,
    string ParentManifestSha256,
    string SelectionConfigSha256,
    string PromptConfigSha256,
    SituationTrainingArtifactVersionsV1 Versions,
    IReadOnlyList<SituationSchemaFileReferenceV1> SchemaFiles,
    IReadOnlyList<SituationProvenanceSourceFileV1> SourceFiles,
    SituationTrainingExportMode OutputMode,
    string Purpose,
    string SamplePlanVersion,
    string SamplePlanSha256,
    IReadOnlyList<SituationTrainingPlannedMatchV1> Matches,
    IReadOnlyList<string> AllowedArtifactPaths);

internal sealed record SituationTrainingStatisticsSnapshotV1(
    string SchemaVersion,
    string CanonicalJson,
    string Sha256);

internal sealed record SituationTrainingSpoolFileV1(
    string Path,
    long Bytes,
    long Rows,
    string Sha256);

internal sealed record SituationTrainingCompletedMatchV1(
    int Ordinal,
    string MatchRef,
    SituationTrainingSplit Split,
    SituationTrainingSpoolFileV1 Spool,
    SituationTrainingStatisticsSnapshotV1 Statistics);

internal sealed record SituationTrainingMergedFileV1(
    SituationTrainingSplit Split,
    string PartialPath,
    string FinalPath,
    long Bytes,
    long Rows,
    string Sha256);

internal sealed record SituationTrainingCheckpointV1(
    string SchemaVersion,
    SituationArtifactStatus Status,
    SituationTrainingWriterPhase Phase,
    SituationTrainingWriterBindingV1 Binding,
    string BindingSha256,
    string IncompleteManifestSha256,
    int NextOrdinal,
    int NextAttempt,
    int NextMergeAttempt,
    IReadOnlyDictionary<string, long> SplitRows,
    IReadOnlyList<SituationTrainingCompletedMatchV1> CompletedMatches,
    IReadOnlyList<SituationTrainingMergedFileV1> MergedFiles);

internal sealed record SituationTrainingMatchTransactionV1(
    string SchemaVersion,
    string BindingSha256,
    int Ordinal,
    int Attempt,
    string MatchRef,
    SituationTrainingSplit Split);

internal sealed record SituationTrainingMatchCommitV1(
    string SchemaVersion,
    string BindingSha256,
    int Ordinal,
    int Attempt,
    string MatchRef,
    SituationTrainingSplit Split,
    SituationTrainingSpoolFileV1 Spool,
    SituationTrainingStatisticsSnapshotV1 Statistics);

internal sealed record SituationTrainingMergeV1(
    string SchemaVersion,
    string BindingSha256,
    int Attempt,
    IReadOnlyList<SituationTrainingMergedFileV1> Files);

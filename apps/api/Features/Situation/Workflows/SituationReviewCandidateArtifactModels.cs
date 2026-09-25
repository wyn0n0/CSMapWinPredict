using CsDemoMap.Api.Models;

namespace CsDemoMap.Api.Services;

internal sealed record SituationReviewCandidateManifestV1(
    string SchemaVersion, string Status, string BaseManifestSha256, string PolicySha256,
    IReadOnlyList<ReviewSelectedEntry> Selected,
    IReadOnlyDictionary<string, IReadOnlyList<string>> Backups,
    IReadOnlyList<ReviewPoolEntry> SourceIndex,
    IReadOnlyList<ReviewRoundException> RoundExceptions,
    IReadOnlyList<ReviewQuotaShortfall> QuotaShortfalls,
    IReadOnlyList<SituationArtifactFileV1> Files);

internal sealed record SituationReviewCandidateSplitStatisticsV1(
    int Samples, int Matches, int Rounds,
    IReadOnlyDictionary<string, int> MatchCounts,
    IReadOnlyDictionary<string, int> CategoryCounts,
    IReadOnlyDictionary<string, int> AvailableCategoryCounts);

internal sealed record SituationReviewCandidateSelectionStatisticsV1(
    IReadOnlyList<ReviewQuotaShortfall> QuotaShortfalls,
    IReadOnlyList<ReviewRoundException> RoundExceptions);

internal sealed record SituationReviewCandidateStatisticsV1(
    string SchemaVersion, string BaseManifestSha256, string PolicySha256,
    IReadOnlyDictionary<string, SituationReviewCandidateSplitStatisticsV1> Splits,
    SituationReviewCandidateSelectionStatisticsV1 ReviewSelection);

internal sealed record SituationReviewCandidateProvenanceV1(
    string SchemaVersion, string BaseManifestSha256, string BaseProvenanceSha256,
    string SplitSha256, string PolicySha256, string HistoricalValidation,
    string GitHead, bool GitDirty, string PolicyVersion,
    IReadOnlyList<SituationArtifactFileV1> BaseFiles,
    IReadOnlyList<SituationProvenanceSourceFileV1> ConsumerFiles);

internal sealed record SituationReviewCandidateBase(
    string Root, string RepositoryRoot, SituationTrainingDatasetManifestV1 Manifest,
    SituationTrainingProvenanceDocumentV1 Provenance);

internal sealed record SituationReviewCandidateValidationResult(
    SituationReviewCandidateManifestV1 Manifest, SituationReviewCandidateStatisticsV1 Statistics);

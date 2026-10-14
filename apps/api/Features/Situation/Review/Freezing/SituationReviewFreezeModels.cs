using CsDemoMap.Api.Models;

namespace CsDemoMap.Api.Services;

// Historical v1 stays pinned. v2 adds scope, analysis identity and approval state.
internal sealed record ReviewFreezeManifest(string SchemaVersion, string Status, string ReviewStatus,
    string ReviewVersion, string BaseDatasetSha256, ReviewWorkspaceIdentity Identity,
    int Target, long WorkspaceRevision, IReadOnlyDictionary<string, int> Counts,
    string ContentSha256, string? ApprovedContentSha256, IReadOnlyList<SituationArtifactFileV1> Files);
internal sealed record ReviewFreezeResult(int ReviewOrdinal, long WorkspaceRevision, bool Effective,
    SituationReviewDecisionV1 Decision, IReadOnlyList<ReviewReplacementAudit> Replacements);
internal sealed record ReviewFreezeHistory(long Revision, SituationReviewDecisionV1 Decision);
internal sealed record ReviewFreezePayload(string ReviewVersion, int Target, long WorkspaceRevision,
    IReadOnlyDictionary<string, int> Counts, IReadOnlyDictionary<string, string> Documents);
internal sealed record ReviewLabelExample(SituationTrainingInputV1 Input, SituationFrozenReviewLabelV1 Label);

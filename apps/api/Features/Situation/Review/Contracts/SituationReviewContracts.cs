using System.Text.Json;
using System.Text.Json.Serialization;
using CsDemoMap.Api.Models;

namespace CsDemoMap.Api.Services;

internal sealed record ReviewWorkspaceIdentity(string SchemaVersion, string DatasetSha256,
    string CandidateManifestSha256, string CandidateFileSha256, string SplitSha256,
    string PolicySha256, IReadOnlyList<SituationProvenanceSourceFileV1> SchemaFiles, string ReviewDraft);
internal sealed record ReviewActiveSample(int ReviewOrdinal, string SampleId);
internal sealed record ReviewReplacementAudit(string OldSampleId, string NewSampleId, int ReviewOrdinal,
    string RejectedDecisionSha256, string PriorityCategory, long WorkspaceRevision,
    string CandidateSha256, DateTimeOffset ReplacedAtUtc);
internal sealed record ReviewWorkSnapshot(long WorkspaceRevision, bool BlockingIssue,
    IReadOnlyList<ReviewActiveSample> Active, IReadOnlyDictionary<string, SituationReviewDecisionV1> Decisions,
    IReadOnlyList<ReviewReplacementAudit> Replacements);
internal sealed record ReviewCommitReceipt(string RequestId, string RequestSha256, string SampleId,
    int SampleRevision, string? DecisionSha256, long WorkspaceRevision, string? NewSampleId);

internal interface IReviewWorkStore : IAsyncDisposable
{
    ReviewWorkspaceIdentity Identity { get; }
    ReviewWorkSnapshot Snapshot { get; }
    ReviewCommitReceipt? FindReceipt(string requestId, string requestSha256);
    Task<ReviewCommitReceipt> SaveDecisionAsync(string requestId, string requestSha256,
        int expectedRevision, SituationReviewDecisionV1 decision, CancellationToken cancellationToken);
    Task<ReviewCommitReceipt> ReplaceAsync(string requestId, string requestSha256,
        long expectedWorkspaceRevision, ReviewReplacementAudit replacement, CancellationToken cancellationToken);
}

internal interface IReviewCatalog
{
    ReviewWorkspaceIdentity Identity { get; }
    IReadOnlyList<ReviewSelectedEntry> InitialSelected { get; }
    IReadOnlyList<ReviewPoolEntry> Pool { get; }
    IReadOnlyDictionary<string, IReadOnlyList<string>> Backups { get; }
    ReviewSelectionPolicy Policy { get; }
    Task<SituationReviewCandidateV1> GetCandidateAsync(string sampleId, int reviewOrdinal, CancellationToken cancellationToken);
}

internal sealed record ReviewEvaluationsInput(bool? FactsCorrect, bool? FocusReasonable,
    bool? SummaryAccurateUseful, bool? Hallucination);
internal sealed record ReviewSaveRequest(
    [property: JsonRequired] string SampleId,
    [property: JsonRequired] int ExpectedRevision,
    [property: JsonRequired] string RequestId,
    [property: JsonRequired] string Decision,
    [property: JsonRequired] ReviewEvaluationsInput Evaluations,
    [property: JsonRequired] IReadOnlyList<string> IssueFields,
    [property: JsonRequired] IReadOnlyList<string> IssueCodes,
    [property: JsonRequired] string? Note,
    [property: JsonRequired] string? RejectReasonCode,
    [property: JsonRequired] SituationNarrativeV1? EditedNarrative);
internal sealed record ReviewValidateRequest([property: JsonRequired] string SampleId,
    [property: JsonRequired] SituationNarrativeV1 Narrative);
internal sealed record ReviewReplaceRequest([property: JsonRequired] string SampleId,
    [property: JsonRequired] long ExpectedWorkspaceRevision, [property: JsonRequired] string RequestId);
internal sealed record ReviewValidationResult(bool Valid, IReadOnlyList<string> Errors);
internal sealed record ReviewSplitProgress(int Total, int Completed, int Rejected, int Unreviewed);
internal sealed record ReviewProgress(int Total, int Completed, int Rejected, int Unreviewed,
    int Operations, long WorkspaceRevision, bool BlockingIssue, IReadOnlyDictionary<string, ReviewSplitProgress> Splits);
internal sealed record ReviewSession(string DatasetSha256, string CandidateManifestSha256, string ReviewDraft,
    ReviewProgress Progress, IReadOnlyList<string> Categories, string? Token = null);
internal sealed record ReviewSampleSummary(string SampleId, int ReviewOrdinal, string Split,
    IReadOnlyList<string> Categories, string Decision, int SampleRevision, bool BlockingIssue);
internal sealed record ReviewSampleDetail(SituationReviewCandidateV1 Candidate, IReadOnlyList<string> Categories,
    SituationReviewDecisionV1? Decision, int SampleRevision, long WorkspaceRevision, bool BlockingIssue);
internal sealed record ReviewSaveResponse(string SampleId, int SampleRevision, string? DecisionSha256,
    long WorkspaceRevision, ReviewProgress Progress, string? NewSampleId = null);

internal interface IReviewBackend
{
    ReviewSession GetSession();
    IReadOnlyList<ReviewSampleSummary> GetSamples();
    Task<ReviewSampleDetail> GetSampleAsync(string sampleId, CancellationToken cancellationToken);
    Task<ReviewValidationResult> ValidateAsync(ReviewValidateRequest request, CancellationToken cancellationToken);
    Task<ReviewSaveResponse> SaveAsync(ReviewSaveRequest request, CancellationToken cancellationToken);
    Task<ReviewSaveResponse> ReplaceAsync(ReviewReplaceRequest request, CancellationToken cancellationToken);
}

internal sealed class ReviewException(string code, int statusCode = 400) : Exception(code)
{
    internal string Code { get; } = code;
    internal int StatusCode { get; } = statusCode;
}

internal static class SituationReviewJson
{
    internal static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        PropertyNameCaseInsensitive = false,
        NumberHandling = JsonNumberHandling.Strict
    };
    internal static T Deserialize<T>(string json)
    {
        SituationTrainingContractJson.RejectDuplicateProperties(json, "Review payload");
        return JsonSerializer.Deserialize<T>(json, Options) ?? throw new ReviewException("invalid-request");
    }
}

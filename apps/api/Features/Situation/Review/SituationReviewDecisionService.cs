using System.Text.RegularExpressions;
using CsDemoMap.Api.Models;

namespace CsDemoMap.Api.Services;

internal sealed class SituationReviewDecisionService(IReviewCatalog catalog, IReviewWorkStore store) : IReviewBackend
{
    private readonly SemaphoreSlim mutation = new(1, 1);
    private readonly Dictionary<string, ReviewPoolEntry> pool = catalog.Pool.ToDictionary(e => e.SampleId, StringComparer.Ordinal);
    private static readonly HashSet<string> BlockingCodes = new(["facts-incorrect", "identity-leak", "future-information", "unknown-evidence"], StringComparer.Ordinal);
    private static readonly HashSet<string> ReasonCodes = new(["narrative-unhelpful", "focus-unreasonable", "hallucination",
        "facts-incorrect", "identity-leak", "future-information", "unknown-evidence", "other"], StringComparer.Ordinal);

    public ReviewSession GetSession() => new(catalog.Identity.DatasetSha256, catalog.Identity.CandidateManifestSha256,
        catalog.Identity.ReviewDraft, Progress(store.Snapshot), catalog.Policy.CategoryPriority);

    public IReadOnlyList<ReviewSampleSummary> GetSamples()
    {
        var state = store.Snapshot;
        return state.Active.OrderBy(a => a.ReviewOrdinal).Select(a =>
        {
            var entry = RequireEntry(a.SampleId);
            state.Decisions.TryGetValue(a.SampleId, out var decision);
            return new ReviewSampleSummary(a.SampleId, a.ReviewOrdinal, SituationReviewSelectionPolicy.SplitName(entry.Split),
                entry.Categories, DecisionName(decision), decision?.SampleRevision ?? 0, decision?.BlockingIssue ?? false);
        }).ToArray();
    }

    public async Task<ReviewSampleDetail> GetSampleAsync(string sampleId, CancellationToken cancellationToken)
    {
        var state = store.Snapshot;
        var active = RequireActive(state, sampleId);
        var candidate = await catalog.GetCandidateAsync(sampleId, active.ReviewOrdinal, cancellationToken);
        state.Decisions.TryGetValue(sampleId, out var decision);
        return new(candidate, RequireEntry(sampleId).Categories, decision, decision?.SampleRevision ?? 0,
            state.WorkspaceRevision, state.BlockingIssue);
    }

    public async Task<ReviewValidationResult> ValidateAsync(ReviewValidateRequest request, CancellationToken cancellationToken)
    {
        if (request is null || request.Narrative is null) throw new ReviewException("invalid-request");
        var active = RequireActive(store.Snapshot, request.SampleId);
        var candidate = await catalog.GetCandidateAsync(request.SampleId, active.ReviewOrdinal, cancellationToken);
        return ValidateNarrative(candidate, request.Narrative);
    }

    public async Task<ReviewSaveResponse> SaveAsync(ReviewSaveRequest request, CancellationToken cancellationToken)
    {
        if (request is null) throw new ReviewException("invalid-request");
        RequireRequestId(request.RequestId);
        var requestHash = SituationCanonicalJson.Sha256(request);
        await mutation.WaitAsync(cancellationToken);
        try
        {
            var replay = store.FindReceipt(request.RequestId, requestHash);
            if (replay is not null) return Response(replay);
            var state = store.Snapshot;
            var active = RequireActive(state, request.SampleId);
            var oldRevision = state.Decisions.GetValueOrDefault(request.SampleId)?.SampleRevision ?? 0;
            if (oldRevision != request.ExpectedRevision) throw new ReviewException("revision-conflict", 409);
            var candidate = await catalog.GetCandidateAsync(request.SampleId, active.ReviewOrdinal, cancellationToken);
            var decision = CreateDecision(request, candidate, catalog.Identity.DatasetSha256, oldRevision + 1);
            if (state.BlockingIssue && decision.Decision != SituationReviewDecisionKind.Rejected)
                throw new ReviewException("blocking-issue", 409);
            var receipt = await store.SaveDecisionAsync(request.RequestId, requestHash, oldRevision, decision, cancellationToken);
            return Response(receipt);
        }
        finally { mutation.Release(); }
    }

    public async Task<ReviewSaveResponse> ReplaceAsync(ReviewReplaceRequest request, CancellationToken cancellationToken)
    {
        if (request is null) throw new ReviewException("invalid-request");
        RequireRequestId(request.RequestId);
        var requestHash = SituationCanonicalJson.Sha256(request);
        await mutation.WaitAsync(cancellationToken);
        try
        {
            var replay = store.FindReceipt(request.RequestId, requestHash);
            if (replay is not null) return Response(replay);
            var state = store.Snapshot;
            RequireActive(state, request.SampleId);
            if (request.ExpectedWorkspaceRevision != state.WorkspaceRevision) throw new ReviewException("workspace-conflict", 409);
            if (state.BlockingIssue) throw new ReviewException("blocking-issue", 409);
            var audit = await SituationReviewReplacementService.PlanAsync(catalog, state, request.SampleId, cancellationToken);
            var receipt = await store.ReplaceAsync(request.RequestId, requestHash, state.WorkspaceRevision, audit, cancellationToken);
            return Response(receipt);
        }
        finally { mutation.Release(); }
    }

    internal static SituationReviewDecisionV1 CreateDecision(ReviewSaveRequest request, SituationReviewCandidateV1 candidate,
        string datasetHash, int revision)
    {
        if (request.Evaluations is not { FactsCorrect: not null, FocusReasonable: not null, SummaryAccurateUseful: not null, Hallucination: not null } e ||
            request.IssueCodes is null || request.IssueFields is null || request.ExpectedRevision < 0 ||
            request.IssueCodes.Any(c => c is null || !ReasonCodes.Contains(c)) || request.IssueFields.Any(f => f is null) ||
            request.Note?.Length > 1000) throw new ReviewException("invalid-request");
        var kind = request.Decision switch
        {
            "approved" => SituationReviewDecisionKind.Approved,
            "modified" => SituationReviewDecisionKind.Modified,
            "rejected" => SituationReviewDecisionKind.Rejected,
            _ => throw new ReviewException("invalid-request")
        };
        if (request.Note is not null && (Regex.IsMatch(request.Note, "[0-9]{17}", RegexOptions.CultureInvariant) ||
            request.Note.Contains(".dem", StringComparison.OrdinalIgnoreCase) || request.Note.Contains("STEAM_", StringComparison.OrdinalIgnoreCase)))
            throw new ReviewException("invalid-request");
        if (kind == SituationReviewDecisionKind.Rejected)
        {
            if (string.IsNullOrWhiteSpace(request.Note) || request.RejectReasonCode is null || !ReasonCodes.Contains(request.RejectReasonCode) || request.EditedNarrative is not null)
                throw new ReviewException("invalid-request");
        }
        else if (request.RejectReasonCode is not null) throw new ReviewException("invalid-request");
        var issues = request.IssueCodes.Concat(e.FactsCorrect == false ? ["facts-incorrect"] : Array.Empty<string>())
            .Concat(request.RejectReasonCode is not null && BlockingCodes.Contains(request.RejectReasonCode) ? [request.RejectReasonCode] : Array.Empty<string>())
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var blocking = issues.Any(BlockingCodes.Contains);
        if (blocking && kind != SituationReviewDecisionKind.Rejected) throw new ReviewException("blocking-issue", 409);
        if (kind == SituationReviewDecisionKind.Approved && request.EditedNarrative is not null) throw new ReviewException("invalid-request");
        if (kind == SituationReviewDecisionKind.Modified && (request.EditedNarrative is null || !ValidateNarrative(candidate, request.EditedNarrative).Valid))
            throw new ReviewException("narrative-invalid");
        var narrative = kind == SituationReviewDecisionKind.Modified ? request.EditedNarrative : null;
        var decision = new SituationReviewDecisionV1(SituationTrainingContractVersions.ReviewDecision,
            datasetHash, candidate.CandidateSha256, candidate.SampleId, revision, kind,
            new(e.FactsCorrect.Value, e.FocusReasonable.Value, e.SummaryAccurateUseful.Value, e.Hallucination.Value),
            request.IssueFields.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(), issues,
            request.Note?.Trim(), narrative,
            kind == SituationReviewDecisionKind.Rejected ? null : narrative is null ? candidate.CandidateSha256 : SituationCanonicalJson.Sha256(narrative),
            request.RejectReasonCode, blocking, DateTimeOffset.UtcNow, "");
        decision = SituationTrainingContractJson.WithDecisionHash(decision);
        try { SituationTrainingContractJson.Validate(decision, candidate); }
        catch (Exception error) when (error is InvalidDataException or ArgumentException or NullReferenceException)
        { throw new ReviewException("narrative-invalid"); }
        return decision;
    }

    private static ReviewValidationResult ValidateNarrative(SituationReviewCandidateV1 candidate, SituationNarrativeV1 narrative)
    {
        try
        {
            SituationContractValidator.Validate(narrative, candidate.Input.AllowedEvidenceIds.ToHashSet(StringComparer.Ordinal));
            SituationTrainingContractJson.ValidateModelBoundary(candidate.Input, narrative);
            return new(true, []);
        }
        catch (Exception error) when (error is InvalidDataException or ArgumentException or NullReferenceException)
        {
            var unknown = narrative.Highlights?.Any(h => h?.EvidenceIds?.Any(id => !candidate.Input.AllowedEvidenceIds.Contains(id)) == true) == true;
            return new(false, [unknown ? "unknown-evidence" : "narrative-invalid"]);
        }
    }

    private ReviewSaveResponse Response(ReviewCommitReceipt receipt) => new(receipt.SampleId, receipt.SampleRevision,
        receipt.DecisionSha256, receipt.WorkspaceRevision, Progress(store.Snapshot), receipt.NewSampleId);

    private ReviewProgress Progress(ReviewWorkSnapshot state)
    {
        var splits = new Dictionary<string, ReviewSplitProgress>(StringComparer.Ordinal);
        foreach (var split in SituationReviewSelectionPolicy.SplitOrder)
        {
            var samples = state.Active.Where(a => SituationReviewSelectionPolicy.SplitName(RequireEntry(a.SampleId).Split) == split).ToArray();
            var decisions = samples.Select(a => state.Decisions.GetValueOrDefault(a.SampleId)).ToArray();
            splits[split] = new(samples.Length, decisions.Count(d => d is not null && !d.BlockingIssue && d.Decision != SituationReviewDecisionKind.Rejected),
                decisions.Count(d => d?.Decision == SituationReviewDecisionKind.Rejected), decisions.Count(d => d is null));
        }
        return new(state.Active.Count, splits.Values.Sum(p => p.Completed), splits.Values.Sum(p => p.Rejected),
            splits.Values.Sum(p => p.Unreviewed), checked((int)state.WorkspaceRevision), state.WorkspaceRevision, state.BlockingIssue, splits);
    }

    private ReviewPoolEntry RequireEntry(string id) => pool.TryGetValue(id, out var entry) ? entry : throw new ReviewException("artifact-mismatch");
    private static ReviewActiveSample RequireActive(ReviewWorkSnapshot state, string id) =>
        state.Active.SingleOrDefault(a => a.SampleId == id) ?? throw new ReviewException("unknown-sample", 404);
    private static void RequireRequestId(string id)
    {
        if (!Guid.TryParseExact(id, "D", out _)) throw new ReviewException("invalid-request");
    }
    private static string DecisionName(SituationReviewDecisionV1? decision) => decision?.Decision switch
    {
        SituationReviewDecisionKind.Approved => "approved", SituationReviewDecisionKind.Modified => "modified",
        SituationReviewDecisionKind.Rejected => "rejected", _ => "unreviewed"
    };
}

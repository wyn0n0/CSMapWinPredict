using CsDemoMap.Api.Models;

namespace CsDemoMap.Api.Services;

internal static class SituationReviewReplacementService
{
    internal static void VerifyRecoveredState(IReviewCatalog catalog, ReviewWorkSnapshot state)
    {
        var byId = catalog.Pool.ToDictionary(e => e.SampleId, StringComparer.Ordinal);
        var selected = catalog.InitialSelected.ToArray();
        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (var audit in state.Replacements)
        {
            var old = selected.SingleOrDefault(s => s.Entry.SampleId == audit.OldSampleId);
            if (old is null || old.ReviewOrdinal != audit.ReviewOrdinal || !byId.TryGetValue(audit.NewSampleId, out var next) ||
                !state.Decisions.TryGetValue(audit.OldSampleId, out var rejection) || rejection.Decision != SituationReviewDecisionKind.Rejected ||
                rejection.BlockingIssue || rejection.DecisionSha256 != audit.RejectedDecisionSha256 || next.PrelabelSha256 != audit.CandidateSha256)
                throw new ReviewException("artifact-mismatch");
            var category = catalog.Policy.CategoryPriority.FirstOrDefault(c => old.Entry.Categories.Contains(c, StringComparer.Ordinal)) ?? "any";
            if (category != audit.PriorityCategory) throw new ReviewException("artifact-mismatch");
            used.Add(audit.OldSampleId);
            var backups = catalog.Backups.ToDictionary(k => k.Key,
                k => (IReadOnlyList<string>)k.Value.Where(id => !used.Contains(id)).ToArray(), StringComparer.Ordinal);
            if (SituationReviewCandidateSelector.ValidateReplacement(new(selected, backups, [], []), catalog.Pool,
                audit.OldSampleId, category, next, catalog.Policy).Count != 0) throw new ReviewException("artifact-mismatch");
            selected = selected.Select(s => s.Entry.SampleId == audit.OldSampleId ? new ReviewSelectedEntry(s.ReviewOrdinal, next) : s).ToArray();
            used.Add(audit.NewSampleId);
        }
        if (!SituationReviewCandidateReader.Equal(selected.Select(s => new ReviewActiveSample(s.ReviewOrdinal, s.Entry.SampleId)).OrderBy(s => s.ReviewOrdinal).ToArray(),
            state.Active.OrderBy(s => s.ReviewOrdinal).ToArray())) throw new ReviewException("artifact-mismatch");
    }

    internal static async Task<ReviewReplacementAudit> PlanAsync(IReviewCatalog catalog, ReviewWorkSnapshot state,
        string oldId, CancellationToken cancellationToken)
    {
        if (state.BlockingIssue) throw new ReviewException("blocking-issue", 409);
        if (!state.Decisions.TryGetValue(oldId, out var rejected) || rejected.Decision != SituationReviewDecisionKind.Rejected || rejected.BlockingIssue)
            throw new ReviewException("invalid-request");
        var byId = catalog.Pool.ToDictionary(e => e.SampleId, StringComparer.Ordinal);
        var old = state.Active.SingleOrDefault(a => a.SampleId == oldId) ?? throw new ReviewException("unknown-sample", 404);
        var oldEntry = byId[oldId];
        var category = catalog.Policy.CategoryPriority.FirstOrDefault(c => oldEntry.Categories.Contains(c, StringComparer.Ordinal)) ?? "any";
        // A previously reviewed/rejected or replaced sample must never silently re-enter the active queue.
        var used = state.Decisions.Keys.Concat(state.Replacements.SelectMany(r => new[] { r.OldSampleId, r.NewSampleId })).ToHashSet(StringComparer.Ordinal);
        var backups = catalog.Backups.ToDictionary(kv => kv.Key,
            kv => (IReadOnlyList<string>)kv.Value.Where(id => !used.Contains(id)).ToArray(), StringComparer.Ordinal);
        var key = SituationReviewSelectionPolicy.SplitName(oldEntry.Split) + "/" + category;
        if (!backups.TryGetValue(key, out var ordered)) throw new ReviewException("no-valid-replacement", 409);
        var selected = state.Active.Select(a => new ReviewSelectedEntry(a.ReviewOrdinal, byId[a.SampleId])).ToArray();
        var plan = new ReviewSelectionPlan(selected, backups, [], []);
        foreach (var id in ordered)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!byId.TryGetValue(id, out var replacement)) throw new ReviewException("artifact-mismatch");
            if (SituationReviewCandidateSelector.ValidateReplacementConstraints(selected.Select(s => s.Entry).ToArray(), oldId, replacement, catalog.Policy).Count != 0) continue;
            if (SituationReviewCandidateSelector.ValidateReplacement(plan, catalog.Pool, oldId, category, replacement, catalog.Policy).Count != 0)
                throw new ReviewException("artifact-mismatch");
            var candidate = await catalog.GetCandidateAsync(id, old.ReviewOrdinal, cancellationToken);
            return new(oldId, id, old.ReviewOrdinal, rejected.DecisionSha256, category, state.WorkspaceRevision + 1,
                candidate.CandidateSha256, DateTimeOffset.UtcNow);
        }
        throw new ReviewException("no-valid-replacement", 409);
    }
}

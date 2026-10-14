using CsDemoMap.Api.Models;

namespace CsDemoMap.Api.Services;

internal static class SituationReviewFreezeBuilder
{
    internal static async Task<ReviewFreezePayload> BuildAsync(IReviewCatalog catalog, IReviewWorkStore store,
        IReviewBackend backend, IReadOnlyList<ReviewFreezeHistory> history, CancellationToken ct)
    {
        var state = store.Snapshot;
        SituationReviewReplacementService.VerifyRecoveredState(catalog, state);
        var rows = backend.GetSamples();
        var target = rows.Count;
        var expected = target switch
        {
            50 => new[] { 36, 7, 7 }, 300 => new[] { 220, 40, 40 },
            _ => throw new ReviewException("freeze-target-mismatch")
        };
        var splits = new[] { "train", "dev", "test" };
        var counts = splits.ToDictionary(s => s, s => rows.Count(r => r.Split == s), StringComparer.Ordinal);
        if (!splits.Select(s => counts[s]).SequenceEqual(expected) || rows.Select(r => r.SampleId).Distinct().Count() != target ||
            !rows.Select(r => r.ReviewOrdinal).SequenceEqual(Enumerable.Range(1, target)))
            throw new ReviewException("freeze-target-mismatch");
        if (state.BlockingIssue || state.Decisions.Values.Any(d => d.BlockingIssue) || history.Any(h => h.Decision.BlockingIssue))
            throw new ReviewException("blocking-issue");
        if (rows.Any(r => r.Decision is not ("approved" or "modified"))) throw new ReviewException("review-incomplete");
        var revisionHash = SituationCanonicalJson.Sha256(new { catalog.Identity, target,
            ordinals = rows.Select(r => state.Active.Single(a => a.SampleId == r.SampleId).ReviewOrdinal).ToArray(),
            decisions = history.Select(h => new { h.Revision, h.Decision.DecisionSha256 }).ToArray(), state.Replacements });
        var version = catalog.Identity.ReviewDraft.Replace("-draft", "", StringComparison.Ordinal) + $"-scope{target}-v1-" + revisionHash[..16];
        var entries = catalog.Pool.ToDictionary(e => e.SampleId, StringComparer.Ordinal);
        var candidates = new List<SituationReviewCandidateV1>();
        var labels = new Dictionary<string, List<SituationFrozenReviewLabelV1>>(StringComparer.Ordinal);
        foreach (var split in splits) labels.Add(split, []);
        var distances = new List<int>();
        foreach (var row in rows)
        {
            var active = state.Active.Single(a => a.SampleId == row.SampleId);
            var candidate = await catalog.GetCandidateAsync(row.SampleId, active.ReviewOrdinal, ct);
            var decision = state.Decisions[row.SampleId];
            SituationTrainingContractJson.Validate(decision, candidate);
            if (decision.DatasetSha256 != catalog.Identity.DatasetSha256 || candidate.Split != entries[row.SampleId].Split ||
                row.Split != SituationReviewSelectionPolicy.SplitName(candidate.Split)) throw new ReviewException("artifact-mismatch");
            // Human fidelity judgments remain authoritative; failed judgments cannot become gold labels.
            if (decision.Evaluations is not { FactsCorrect: true, FocusReasonable: true, SummaryAccurateUseful: true, Hallucination: false })
                throw new ReviewException("review-evaluation-failed");
            var narrative = decision.Decision == SituationReviewDecisionKind.Approved ? candidate.Candidate : decision.FinalNarrative!;
            if (decision.Decision == SituationReviewDecisionKind.Approved)
                SituationContractValidator.ValidateTemplate(narrative, candidate.Input.Facts);
            var label = new SituationFrozenReviewLabelV1(SituationTrainingContractVersions.FrozenReviewLabel,
                candidate.SampleId, narrative, decision.Decision == SituationReviewDecisionKind.Approved
                    ? SituationTrainingLabelSource.HumanApproved : SituationTrainingLabelSource.HumanModified,
                version, row.Split switch { "train" => ["sft"], "dev" => ["checkpoint-selection", "evaluation", "prompt-selection"],
                    _ => ["final-evaluation"] }, row.Split == "train" ? 5 : null);
            SituationTrainingContractJson.ValidateFrozenReviewLabel(label, candidate.Split, candidate);
            if (SituationCanonicalJson.Sha256(narrative) != decision.FinalNarrativeSha256) throw new ReviewException("artifact-mismatch");
            labels[row.Split].Add(label);
            candidates.Add(candidate);
            distances.Add(EditDistance(candidate.Candidate.SummaryZh, narrative.SummaryZh));
        }
        var ordinals = state.Active.ToDictionary(a => a.SampleId, a => a.ReviewOrdinal, StringComparer.Ordinal);
        foreach (var r in state.Replacements) ordinals[r.OldSampleId] = r.ReviewOrdinal;
        var selectedIds = rows.Select(r => r.SampleId).ToHashSet(StringComparer.Ordinal);
        var results = new List<ReviewFreezeResult>();
        foreach (var item in history)
        {
            var decision = item.Decision;
            if (!ordinals.TryGetValue(decision.SampleId, out var ordinal)) throw new ReviewException("artifact-mismatch");
            var candidate = await catalog.GetCandidateAsync(decision.SampleId, ordinal, ct);
            SituationTrainingContractJson.Validate(decision, candidate);
            if (decision.DatasetSha256 != catalog.Identity.DatasetSha256) throw new ReviewException("artifact-mismatch");
            results.Add(new(ordinal, item.Revision, selectedIds.Contains(decision.SampleId) &&
                state.Decisions[decision.SampleId].DecisionSha256 == decision.DecisionSha256, decision,
                state.Replacements.Where(r => r.OldSampleId == decision.SampleId || r.NewSampleId == decision.SampleId).ToArray()));
        }
        if (results.Count(r => r.Effective) != target || history.Select(h => h.Revision).Distinct().Count() != history.Count)
            throw new ReviewException("artifact-mismatch");
        var effective = results.Where(r => r.Effective).Select(r => r.Decision).ToArray();
        object Summary(IReadOnlyList<SituationReviewDecisionV1> values) => new
        {
            approved = values.Count(d => d.Decision == SituationReviewDecisionKind.Approved),
            modified = values.Count(d => d.Decision == SituationReviewDecisionKind.Modified),
            rejected = values.Count(d => d.Decision == SituationReviewDecisionKind.Rejected),
            factsCorrect = values.Count(d => d.Evaluations.FactsCorrect), focusReasonable = values.Count(d => d.Evaluations.FocusReasonable),
            summaryAccurateUseful = values.Count(d => d.Evaluations.SummaryAccurateUseful), hallucination = values.Count(d => d.Evaluations.Hallucination),
            blockingIssues = values.Count(d => d.BlockingIssue)
        };
        var coverage = splits.ToDictionary(s => s, s => new
        {
            samples = counts[s], matches = candidates.Where(c => SituationReviewSelectionPolicy.SplitName(c.Split) == s).Select(c => c.MatchRef).Distinct().Count(),
            categories = catalog.Policy.CategoryPriority.ToDictionary(c => c, c => rows.Count(r => r.Split == s && r.Categories.Contains(c))),
            originalQuotaShortfalls = catalog.Policy.Splits[s].Quotas.ToDictionary(q => q.Key,
                q => Math.Max(0, q.Value - rows.Count(r => r.Split == s && r.Categories.Contains(q.Key))))
        });
        var documents = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["review-inputs.jsonl"] = Lines(candidates.OrderBy(c => c.ReviewOrdinal)),
            ["review-results.jsonl"] = Lines(results.OrderBy(r => r.ReviewOrdinal).ThenBy(r => r.WorkspaceRevision)),
            ["review-stats.json"] = SituationCanonicalJson.Serialize(new
            {
                schemaVersion = "situation-frozen-review-stats-v1", target, counts, effective = Summary(effective),
                history = Summary(history.Select(h => h.Decision).ToArray()), replacements = state.Replacements.Count,
                coverage, summaryEditDistance = new { metric = "Levenshtein UTF-16 code units", total = distances.Sum(), maximum = distances.Max() },
                categoryBasis = "Original v1 sampling categories; reduced scope does not claim original 300-row quotas.",
                testIsolation = "test: final-evaluation only; never SFT, prompt/hyperparameter/checkpoint selection."
            })
        };
        foreach (var split in splits) documents.Add(split + "-labels.jsonl", Lines(labels[split]));
        return new(version, target, state.WorkspaceRevision, counts, documents);
    }

    internal static string Lines<T>(IEnumerable<T> rows) => string.Concat(rows.Select(r => SituationCanonicalJson.Serialize(r) + "\n"));

    private static int EditDistance(string a, string b)
    {
        var previous = Enumerable.Range(0, b.Length + 1).ToArray();
        for (var i = 1; i <= a.Length; i++)
        {
            var next = new int[b.Length + 1]; next[0] = i;
            for (var j = 1; j <= b.Length; j++) next[j] = Math.Min(Math.Min(previous[j] + 1, next[j - 1] + 1),
                previous[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
            previous = next;
        }
        return previous[b.Length];
    }
}

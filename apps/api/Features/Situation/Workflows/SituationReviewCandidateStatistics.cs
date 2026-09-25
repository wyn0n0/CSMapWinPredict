using CsDemoMap.Api.Models;

namespace CsDemoMap.Api.Services;

internal static class SituationReviewCandidateStatistics
{
    internal static SituationReviewCandidateStatisticsV1 Create(IReadOnlyList<ReviewPoolEntry> pool, ReviewSelectionPlan plan, ReviewSelectionPolicy policy)
    {
        var result = new SortedDictionary<string, SituationReviewCandidateSplitStatisticsV1>(StringComparer.Ordinal);
        foreach (var split in SituationReviewSelectionPolicy.SplitOrder)
        {
            var available = pool.Where(e => SituationReviewSelectionPolicy.SplitName(e.Split) == split).ToArray();
            var chosen = plan.Selected.Select(s => s.Entry).Where(e => SituationReviewSelectionPolicy.SplitName(e.Split) == split).ToArray();
            result.Add(split, new(chosen.Length, chosen.Select(e => e.MatchRef).Distinct().Count(), chosen.Select(e => e.RoundRef).Distinct().Count(),
                new SortedDictionary<string, int>(chosen.GroupBy(e => e.MatchRef).ToDictionary(g => g.Key, g => g.Count()), StringComparer.Ordinal),
                Counts(chosen, policy), Counts(available, policy)));
        }
        return new("situation-review-candidate-stats-v1", SituationReviewSelectionPolicy.ApprovedDatasetSha256,
            SituationReviewSelectionPolicy.Hash(policy), result, new(plan.QuotaShortfalls, plan.RoundExceptions));
    }

    internal static void ValidatePlan(IReadOnlyList<ReviewPoolEntry> pool, ReviewSelectionPlan plan, ReviewSelectionPolicy policy, bool requireComplete)
    {
        var source = pool.ToDictionary(e => e.SampleId, StringComparer.Ordinal);
        var chosen = plan.Selected.Select(s => s.Entry).ToArray();
        if (chosen.Select(e => e.SampleId).Distinct().Count() != chosen.Length ||
            chosen.Select(e => e.SourceSceneSha256).Distinct().Count() != chosen.Length ||
            chosen.Select(e => e.ModelInputSha256).Distinct().Count() != chosen.Length)
            throw new InvalidDataException("Review sample/source-scene/model-input uniqueness violated.");
        var ordered = chosen.OrderBy(e => Array.IndexOf(SituationReviewSelectionPolicy.SplitOrder, SituationReviewSelectionPolicy.SplitName(e.Split)))
            .ThenBy(e => e.MatchRef, StringComparer.Ordinal).ThenBy(e => e.RoundRef, StringComparer.Ordinal).ThenBy(e => e.Tick).ThenBy(e => e.SampleId, StringComparer.Ordinal).ToArray();
        for (var i = 0; i < plan.Selected.Count; i++)
        {
            var selected = plan.Selected[i];
            if (selected.ReviewOrdinal != i + 1 || selected.Entry.SampleId != ordered[i].SampleId ||
                !source.TryGetValue(selected.Entry.SampleId, out var original) || !SituationReviewCandidateReader.Equal(selected.Entry, original))
                throw new InvalidDataException("Review ordinal/order/source-index mismatch.");
        }
        var exceptions = new List<ReviewRoundException>();
        foreach (var round in chosen.GroupBy(e => (e.Split, e.MatchRef, e.RoundRef)))
        {
            if (round.Count() <= policy.MaxPerRound) continue;
            if (round.Count() != policy.ExceptionalMaxPerRound || !DistinctEventsPossible(round.ToArray()))
                throw new InvalidDataException("Review round cap or distinct-event exception violated.");
            var recorded = plan.RoundExceptions.SingleOrDefault(e => e.Split == SituationReviewSelectionPolicy.SplitName(round.Key.Split) && e.MatchRef == round.Key.MatchRef && e.RoundRef == round.Key.RoundRef);
            if (recorded is null || !recorded.SampleIds.Order(StringComparer.Ordinal).SequenceEqual(round.Select(e => e.SampleId).Order(StringComparer.Ordinal)) ||
                recorded.DistinctEventCategories.Count != 3 || recorded.DistinctEventCategories.Distinct().Count() != 3 ||
                recorded.DistinctEventCategories.Any(e => !policy.EventCategories.Contains(e)) ||
                !DistinctEventsPossible(round.Select(e => e with { EventCategories = e.EventCategories.Intersect(recorded.DistinctEventCategories).ToArray() }).ToArray()))
                throw new InvalidDataException("Review round exception description mismatch.");
            exceptions.Add(recorded);
        }
        if (exceptions.Count != plan.RoundExceptions.Count) throw new InvalidDataException("Unexpected review round exception.");
        var unmet = new HashSet<string>(StringComparer.Ordinal);
        foreach (var split in SituationReviewSelectionPolicy.SplitOrder)
        {
            var target = policy.Splits[split];
            var rows = chosen.Where(e => SituationReviewSelectionPolicy.SplitName(e.Split) == split).ToArray();
            var all = pool.Where(e => SituationReviewSelectionPolicy.SplitName(e.Split) == split).ToArray();
            var matches = rows.GroupBy(e => e.MatchRef).ToDictionary(g => g.Key, g => g.Count());
            if (rows.Length > target.Samples || matches.Values.Any(n => n > target.MaxPerMatch) ||
                (requireComplete && (rows.Length != target.Samples || matches.Count != target.Matches || matches.Values.Any(n => n < target.MinPerMatch) ||
                                    !matches.Keys.Order(StringComparer.Ordinal).SequenceEqual(all.Select(e => e.MatchRef).Distinct().Order(StringComparer.Ordinal)))))
                throw new InvalidDataException($"Review split {split} count/match constraints failed: {rows.Length}/{target.Samples} samples, {matches.Count}/{target.Matches} matches.");
            void RequireShortfall(string category, int goal, int available, int selected)
            {
                unmet.Add(split + "/" + category);
                var value = plan.QuotaShortfalls.SingleOrDefault(q => q.Split == split && q.Category == category);
                if (value is null || value.Target != goal || value.Available != available || value.Selected != selected ||
                    value.ReasonCode is not ("raw-pool-insufficient" or "constraint-combination-infeasible" or "greedy-search-exhausted" or "match-membership-mismatch") ||
                    (available < goal && value.ReasonCode != "raw-pool-insufficient" && category != "match-count") ||
                    value.AlternativeCategories.Distinct().Count() != value.AlternativeCategories.Count ||
                    value.AlternativeCategories.Any(c => !policy.CategoryPriority.Contains(c) || !rows.Any(e => e.Categories.Contains(c))))
                    throw new InvalidDataException("Review structural shortfall missing/inaccurate: " + split + "/" + category);
            }
            if (rows.Length < target.Samples) RequireShortfall("any", target.Samples, all.Length, rows.Length);
            var allMatches = all.Select(e => e.MatchRef).Distinct().ToArray();
            if (allMatches.Length != target.Matches) RequireShortfall("match-count", target.Matches, allMatches.Length, matches.Count);
            foreach (var match in allMatches)
            {
                var n = rows.Count(e => e.MatchRef == match);
                if (n < target.MinPerMatch) RequireShortfall("match/" + match, target.MinPerMatch, all.Count(e => e.MatchRef == match), n);
            }
            foreach (var quota in target.Quotas)
            {
                var selectedCount = rows.Count(e => e.Categories.Contains(quota.Key));
                if (selectedCount >= quota.Value) continue;
                RequireShortfall(quota.Key, quota.Value, all.Count(e => e.Categories.Contains(quota.Key)), selectedCount);
            }
        }
        if (plan.QuotaShortfalls.Any(q => !unmet.Contains(q.Split + "/" + q.Category)) || plan.QuotaShortfalls.Count != unmet.Count)
            throw new InvalidDataException("Unexpected/duplicate review quota shortfall.");
        if (requireComplete && plan.QuotaShortfalls.Count != 0) throw new InvalidDataException("Review quota shortfall blocks completion: " + string.Join(", ", plan.QuotaShortfalls.Select(q => q.Split + "/" + q.Category + ":" + q.Selected + "/" + q.Target)));
        ValidateBackups(pool, plan, policy);
    }

    private static void ValidateBackups(IReadOnlyList<ReviewPoolEntry> pool, ReviewSelectionPlan plan, ReviewSelectionPolicy policy)
    {
        var selected = plan.Selected.Select(s => s.Entry.SampleId).ToHashSet(StringComparer.Ordinal);
        var expectedKeys = new List<string>();
        foreach (var split in SituationReviewSelectionPolicy.SplitOrder)
        foreach (var category in policy.CategoryPriority.Append("any"))
        {
            var key = split + "/" + category;
            expectedKeys.Add(key);
            var chosen = plan.Selected.Select(s => s.Entry).Where(e => SituationReviewSelectionPolicy.SplitName(e.Split) == split).ToArray();
            var unmet = policy.Splits[split].Quotas.Where(q => chosen.Count(e => e.Categories.Contains(q.Key)) < q.Value).Select(q => q.Key).ToHashSet(StringComparer.Ordinal);
            var expected = pool.Where(e => SituationReviewSelectionPolicy.SplitName(e.Split) == split && !selected.Contains(e.SampleId) && (category == "any" || e.Categories.Contains(category)))
                .OrderByDescending(e => e.Categories.Count(unmet.Contains))
                .ThenBy(e => chosen.Count(s => s.MatchRef == e.MatchRef))
                .ThenBy(e => chosen.Count(s => s.MatchRef == e.MatchRef && s.RoundRef == e.RoundRef))
                .ThenBy(e => SituationReviewSelectionPolicy.StableKey(e, policy), StringComparer.Ordinal).ThenBy(e => e.SampleId, StringComparer.Ordinal).Select(e => e.SampleId);
            if (!plan.Backups.TryGetValue(key, out var actual) || !actual.SequenceEqual(expected))
                throw new InvalidDataException("Review backup order/membership mismatch: " + key);
        }
        if (!plan.Backups.Keys.Order(StringComparer.Ordinal).SequenceEqual(expectedKeys.Order(StringComparer.Ordinal)))
            throw new InvalidDataException("Review backup categories differ from policy.");
    }
    private static IReadOnlyDictionary<string, int> Counts(IEnumerable<ReviewPoolEntry> rows, ReviewSelectionPolicy policy) =>
        new SortedDictionary<string, int>(policy.CategoryPriority.ToDictionary(c => c, c => rows.Count(e => e.Categories.Contains(c))), StringComparer.Ordinal);
    private static bool DistinctEventsPossible(IReadOnlyList<ReviewPoolEntry> rows)
    {
        bool Search(int index, HashSet<string> used)
        {
            if (index == rows.Count) return true;
            foreach (var category in rows[index].EventCategories)
            {
                if (!used.Add(category)) continue;
                if (Search(index + 1, used)) return true;
                used.Remove(category);
            }
            return false;
        }
        return Search(0, new(StringComparer.Ordinal));
    }
}

using CsDemoMap.Api.Models;

namespace CsDemoMap.Api.Services;

/// <summary>Pure, deterministic selection. No payload, identity, clock or file access.</summary>
internal static class SituationReviewCandidateSelector
{
    internal static ReviewSelectionPlan Select(IReadOnlyList<ReviewPoolEntry> pool, ReviewSelectionPolicy policy)
    {
        ValidatePolicy(policy);
        var all = new List<ReviewPoolEntry>();
        var shortfalls = new List<ReviewQuotaShortfall>();
        var backups = new SortedDictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (var split in SituationReviewSelectionPolicy.SplitOrder)
        {
            if (!policy.Splits.TryGetValue(split, out var spec)) continue;
            var entries = pool.Where(e => Name(e) == split)
                .OrderBy(e => SituationReviewSelectionPolicy.StableKey(e, policy), StringComparer.Ordinal)
                .ThenBy(e => e.SampleId, StringComparer.Ordinal).ThenBy(e => e.RecordSha256, StringComparer.Ordinal)
                .ThenBy(e => e.MatchRef, StringComparer.Ordinal).ThenBy(e => e.RoundRef, StringComparer.Ordinal)
                .ThenBy(e => e.Tick).ThenBy(e => e.LineNumber)
                .ThenBy(e => SituationCanonicalJson.Sha256(e), StringComparer.Ordinal).ToArray();
            var matches = entries.Select(e => e.MatchRef).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
            var chosen = Greedy(entries, all, matches, spec, policy);
            var rawQuotaLowerBound = spec.Quotas.Sum(q => Math.Max(0, q.Value - entries.Count(e => Has(e, q.Key))));
            // Exhaustive repair on bounded synthetic pools also gives an honest infeasibility certificate.
            var exhaustive = false;
            if (Penalty(chosen, matches, spec) > rawQuotaLowerBound && entries.Length <= 24)
                chosen = ExactRepair(entries, all, chosen, matches, spec, policy, out exhaustive);
            else if (Penalty(chosen, matches, spec) > rawQuotaLowerBound)
                chosen = SwapRepair(entries, all, chosen, matches, spec, policy);
            // Exhaustion is only conditional on previous splits. Conflicting earlier choices
            // could be changed by a global solver, which this split-local search is not.
            var previousConflict = entries.Any(e => all.Any(p => p.SampleId == e.SampleId ||
                p.SourceSceneSha256 == e.SourceSceneSha256 || p.ModelInputSha256 == e.ModelInputSha256));
            string Reason(int available, int target) => available < target ? "raw-pool-insufficient" :
                exhaustive && !previousConflict ? "constraint-combination-infeasible" : "greedy-search-exhausted";
            var alternatives = policy.CategoryPriority.Where(c => chosen.Any(e => Has(e, c))).ToArray();
            if (chosen.Count < spec.Samples)
                shortfalls.Add(new(split, "any", spec.Samples, entries.Length, chosen.Count, Reason(entries.Length, spec.Samples), alternatives));
            if (matches.Length != spec.Matches)
                shortfalls.Add(new(split, "match-count", spec.Matches, matches.Length,
                    chosen.Select(e => e.MatchRef).Distinct(StringComparer.Ordinal).Count(), "match-membership-mismatch", alternatives));
            foreach (var match in matches)
            {
                var count = chosen.Count(e => e.MatchRef == match);
                var available = entries.Count(e => e.MatchRef == match);
                if (count < spec.MinPerMatch)
                    shortfalls.Add(new(split, "match/" + match, spec.MinPerMatch, available, count, Reason(available, spec.MinPerMatch), alternatives));
            }
            foreach (var category in policy.CategoryPriority.Where(spec.Quotas.ContainsKey))
            {
                var count = chosen.Count(e => Has(e, category));
                var available = entries.Count(e => Has(e, category));
                if (count < spec.Quotas[category])
                    shortfalls.Add(new(split, category, spec.Quotas[category], available, count,
                        Reason(available, spec.Quotas[category]), alternatives.Where(c => c != category).ToArray()));
            }
            all.AddRange(chosen);
            var ids = chosen.Select(e => e.SampleId).ToHashSet(StringComparer.Ordinal);
            var remaining = Rank(entries.Where(e => !ids.Contains(e.SampleId)), chosen, spec, policy)
                .DistinctBy(e => e.SampleId, StringComparer.Ordinal).ToArray();
            backups.Add(split + "/any", remaining.Select(e => e.SampleId).ToArray());
            foreach (var category in policy.CategoryPriority)
                backups.Add(split + "/" + category, remaining.Where(e => Has(e, category)).Select(e => e.SampleId).ToArray());
        }
        // An ID selected in an earlier/later split must never reappear as a backup.
        var selectedIds = all.Select(e => e.SampleId).ToHashSet(StringComparer.Ordinal);
        foreach (var key in backups.Keys.ToArray()) backups[key] = backups[key].Where(id => !selectedIds.Contains(id)).ToArray();
        var ordered = all.OrderBy(e => Array.IndexOf(SituationReviewSelectionPolicy.SplitOrder, Name(e)))
            .ThenBy(e => e.MatchRef, StringComparer.Ordinal).ThenBy(e => e.RoundRef, StringComparer.Ordinal)
            .ThenBy(e => e.Tick).ThenBy(e => e.SampleId, StringComparer.Ordinal).ToArray();
        var exceptions = ordered.GroupBy(e => (e.Split, e.MatchRef, e.RoundRef))
            .Where(g => g.Count() > policy.MaxPerRound)
            .Select(g => new ReviewRoundException(Name(g.First()), g.Key.MatchRef, g.Key.RoundRef,
                g.Select(e => e.SampleId).ToArray(), EventAssignment(g.ToArray(), policy)!)).ToArray();
        return new(ordered.Select((e, i) => new ReviewSelectedEntry(i + 1, e)).ToArray(), backups, shortfalls, exceptions);
    }

    /// <summary>Checks combination constraints only; does not authorize a backup-order choice.</summary>
    internal static IReadOnlyList<string> ValidateReplacementConstraints(IReadOnlyList<ReviewPoolEntry> selected,
        string removedSampleId, ReviewPoolEntry replacement, ReviewSelectionPolicy policy)
    {
        var old = selected.Where(e => e.SampleId == removedSampleId).ToArray();
        if (old.Length != 1) return ["replacement-source-not-unique"];
        if (old[0].Split != replacement.Split) return ["replacement-split-mismatch"];
        var result = selected.Where(e => e.SampleId != removedSampleId).Append(replacement).ToArray();
        var errors = ValidateSelection(result, policy).ToList();
        // Existing objective shortfalls may not be made worse by a replacement.
        var spec = policy.Splits[Name(replacement)];
        foreach (var q in spec.Quotas)
            if (Math.Min(q.Value, result.Count(e => e.Split == replacement.Split && Has(e, q.Key))) <
                Math.Min(q.Value, selected.Count(e => e.Split == replacement.Split && Has(e, q.Key))))
                errors.Add("replacement-reduces-coverage/" + q.Key);
        return errors;
    }

    /// <summary>
    /// The first feasible backup is the earliest ID in the frozen split/category list whose
    /// source row satisfies structural constraints and does not worsen any existing quota.
    /// The supplied plan represents the current selected state. Unknown/ambiguous backup
    /// references are errors, not permission to skip an unverifiable earlier row.
    /// </summary>
    internal static IReadOnlyList<string> ValidateReplacement(ReviewSelectionPlan plan,
        IReadOnlyList<ReviewPoolEntry> pool, string removedSampleId, string priorityCategory,
        ReviewPoolEntry replacement, ReviewSelectionPolicy policy)
    {
        var selected = plan.Selected.Select(e => e.Entry).ToArray();
        var old = selected.Where(e => e.SampleId == removedSampleId).ToArray();
        if (old.Length != 1) return ["replacement-source-not-unique"];
        if (priorityCategory != "any" && (!policy.CategoryPriority.Contains(priorityCategory, StringComparer.Ordinal) || !Has(old[0], priorityCategory)))
            return ["replacement-priority-category-mismatch"];
        if (!plan.Backups.TryGetValue(Name(old[0]) + "/" + priorityCategory, out var ids) || !ids.Contains(replacement.SampleId, StringComparer.Ordinal))
            return ["replacement-not-in-backups"];
        var sourceRows = pool.Where(e => e.SampleId == replacement.SampleId).ToArray();
        if (sourceRows.Length != 1 || SituationCanonicalJson.Sha256(sourceRows[0]) != SituationCanonicalJson.Sha256(replacement))
            return ["replacement-source-mismatch"];
        var constraints = ValidateReplacementConstraints(selected, removedSampleId, replacement, policy);
        if (constraints.Count > 0) return constraints;
        var byId = pool.GroupBy(e => e.SampleId, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.Ordinal);
        foreach (var id in ids)
        {
            if (!byId.TryGetValue(id, out var rows) || rows.Length != 1) return ["replacement-backup-source-not-unique"];
            var candidate = rows[0];
            if (candidate.Split != old[0].Split || (priorityCategory != "any" && !Has(candidate, priorityCategory)))
                return ["replacement-backup-category-mismatch"];
            if (selected.Any(e => e.SampleId == candidate.SampleId)) continue;
            if (ValidateReplacementConstraints(selected, removedSampleId, candidate, policy).Count != 0) continue;
            return candidate.SampleId == replacement.SampleId ? [] : ["replacement-skips-feasible-backup"];
        }
        return ["replacement-no-feasible-backup"];
    }

    internal static IReadOnlyList<string> ValidateSelection(IReadOnlyList<ReviewPoolEntry> selected, ReviewSelectionPolicy policy)
    {
        var errors = new List<string>();
        if (selected.Select(e => e.SampleId).Distinct(StringComparer.Ordinal).Count() != selected.Count) errors.Add("duplicate-sample-id");
        if (selected.Select(e => e.SourceSceneSha256).Distinct(StringComparer.Ordinal).Count() != selected.Count) errors.Add("duplicate-scene-hash");
        if (selected.Select(e => e.ModelInputSha256).Distinct(StringComparer.Ordinal).Count() != selected.Count) errors.Add("duplicate-input-hash");
        foreach (var pair in policy.Splits)
        {
            var rows = selected.Where(e => Name(e) == pair.Key).ToArray();
            if (rows.Length != pair.Value.Samples) errors.Add(pair.Key + "/sample-count");
            var groups = rows.GroupBy(e => e.MatchRef, StringComparer.Ordinal).ToArray();
            if (groups.Length != pair.Value.Matches) errors.Add(pair.Key + "/match-count");
            if (groups.Any(g => g.Count() < pair.Value.MinPerMatch || g.Count() > pair.Value.MaxPerMatch)) errors.Add(pair.Key + "/match-quota");
            if (rows.GroupBy(e => (e.MatchRef, e.RoundRef)).Any(g => g.Count() > policy.ExceptionalMaxPerRound ||
                (g.Count() > policy.MaxPerRound && EventAssignment(g.ToArray(), policy) is null))) errors.Add(pair.Key + "/round-quota");
        }
        if (selected.Any(e => !policy.Splits.ContainsKey(Name(e)))) errors.Add("unexpected-split");
        return errors;
    }

    private static List<ReviewPoolEntry> Greedy(ReviewPoolEntry[] entries, List<ReviewPoolEntry> previous,
        string[] matches, ReviewSplitPolicy spec, ReviewSelectionPolicy policy)
    {
        var chosen = new List<ReviewPoolEntry>();
        while (chosen.Count < spec.Samples)
        {
            var next = Rank(entries, chosen, spec, policy).FirstOrDefault(e =>
                CanAdd(e, chosen, previous, spec, policy) &&
                chosen.Count + 1 + matches.Sum(m => Math.Max(0, spec.MinPerMatch - chosen.Count(x => x.MatchRef == m) - (e.MatchRef == m ? 1 : 0))) <= spec.Samples);
            if (next is null) break;
            chosen.Add(next);
        }
        return chosen;
    }

    private static IEnumerable<ReviewPoolEntry> Rank(IEnumerable<ReviewPoolEntry> entries,
        IReadOnlyList<ReviewPoolEntry> selected, ReviewSplitPolicy spec, ReviewSelectionPolicy policy)
    {
        var unmet = spec.Quotas.Where(q => selected.Count(e => Has(e, q.Key)) < q.Value).Select(q => q.Key).ToHashSet(StringComparer.Ordinal);
        var matches = selected.GroupBy(e => e.MatchRef).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        var rounds = selected.GroupBy(e => (e.MatchRef, e.RoundRef)).ToDictionary(g => g.Key, g => g.Count());
        return entries.OrderByDescending(e => e.Categories.Distinct(StringComparer.Ordinal).Count(unmet.Contains))
            .ThenBy(e => matches.GetValueOrDefault(e.MatchRef)).ThenBy(e => rounds.GetValueOrDefault((e.MatchRef, e.RoundRef)))
            .ThenBy(e => SituationReviewSelectionPolicy.StableKey(e, policy), StringComparer.Ordinal)
            .ThenBy(e => e.SampleId, StringComparer.Ordinal);
    }

    private static bool CanAdd(ReviewPoolEntry entry, IReadOnlyList<ReviewPoolEntry> chosen,
        IReadOnlyList<ReviewPoolEntry> previous, ReviewSplitPolicy spec, ReviewSelectionPolicy policy)
    {
        if (chosen.Count >= spec.Samples || chosen.Count(e => e.MatchRef == entry.MatchRef) >= spec.MaxPerMatch ||
            chosen.Concat(previous).Any(e => e.SampleId == entry.SampleId || e.SourceSceneSha256 == entry.SourceSceneSha256 || e.ModelInputSha256 == entry.ModelInputSha256)) return false;
        var round = chosen.Where(e => e.MatchRef == entry.MatchRef && e.RoundRef == entry.RoundRef).Append(entry).ToArray();
        return round.Length <= policy.MaxPerRound || (round.Length <= policy.ExceptionalMaxPerRound && EventAssignment(round, policy) is not null);
    }

    // A third row requires an injective assignment: each of the three rows witnesses a different allowed key event.
    internal static IReadOnlyList<string>? EventAssignment(IReadOnlyList<ReviewPoolEntry> round, ReviewSelectionPolicy policy)
    {
        var rows = round.OrderBy(e => e.SampleId, StringComparer.Ordinal).ToArray();
        var events = new List<string>();
        bool Assign(int i)
        {
            if (i == rows.Length) return true;
            foreach (var ev in policy.EventCategories.Where(e => rows[i].EventCategories.Contains(e, StringComparer.Ordinal) && !events.Contains(e, StringComparer.Ordinal)))
            {
                events.Add(ev);
                if (Assign(i + 1)) return true;
                events.RemoveAt(events.Count - 1);
            }
            return false;
        }
        return Assign(0) ? events.Order(StringComparer.Ordinal).ToArray() : null;
    }

    private static long Penalty(IReadOnlyList<ReviewPoolEntry> selected, string[] matches, ReviewSplitPolicy spec) =>
        1_000_000L * (spec.Samples - selected.Count + matches.Sum(m => Math.Max(0, spec.MinPerMatch - selected.Count(e => e.MatchRef == m)))) +
        spec.Quotas.Sum(q => Math.Max(0, q.Value - selected.Count(e => Has(e, q.Key))));

    private static List<ReviewPoolEntry> ExactRepair(ReviewPoolEntry[] entries, List<ReviewPoolEntry> previous,
        List<ReviewPoolEntry> initial, string[] matches, ReviewSplitPolicy spec, ReviewSelectionPolicy policy, out bool exhaustive)
    {
        var best = initial.ToList();
        var bestScore = Penalty(best, matches, spec);
        var current = new List<ReviewPoolEntry>();
        var nodes = 0;
        var exhausted = false;
        void Search(int index)
        {
            if (bestScore == 0 || exhausted) return;
            if (++nodes > 1_000_000) { exhausted = true; return; }
            var score = Penalty(current, matches, spec);
            if (score < bestScore) { best = current.ToList(); bestScore = score; }
            if (index == entries.Length || current.Count == spec.Samples) return;
            if (CanAdd(entries[index], current, previous, spec, policy))
            {
                current.Add(entries[index]); Search(index + 1); current.RemoveAt(current.Count - 1);
            }
            Search(index + 1);
        }
        Search(0);
        exhaustive = !exhausted && bestScore != 0;
        return best;
    }

    private static List<ReviewPoolEntry> SwapRepair(ReviewPoolEntry[] entries, List<ReviewPoolEntry> previous,
        List<ReviewPoolEntry> chosen, string[] matches, ReviewSplitPolicy spec, ReviewSelectionPolicy policy)
    {
        // Strict improvement bounds termination. A local optimum is explicitly a search failure, never a proof.
        for (var pass = 0; pass < spec.Samples; pass++)
        {
            var score = Penalty(chosen, matches, spec);
            if (score == 0) break;
            List<ReviewPoolEntry>? improved = null;
            foreach (var outgoing in chosen.OrderBy(e => e.SampleId, StringComparer.Ordinal).ToArray())
            {
                var rest = chosen.Where(e => e.SampleId != outgoing.SampleId).ToList();
                foreach (var incoming in Rank(entries, rest, spec, policy))
                {
                    if (!CanAdd(incoming, rest, previous, spec, policy)) continue;
                    rest.Add(incoming);
                    if (Penalty(rest, matches, spec) < score) { improved = rest; break; }
                    rest.RemoveAt(rest.Count - 1);
                }
                if (improved is not null) break;
            }
            if (improved is null) break;
            chosen = improved;
            // A swap can unlock an additional slot through a duplicate hash/round constraint.
            while (chosen.Count < spec.Samples)
            {
                var next = Rank(entries, chosen, spec, policy).FirstOrDefault(e => CanAdd(e, chosen, previous, spec, policy));
                if (next is null) break;
                chosen.Add(next);
            }
        }
        return chosen;
    }

    private static bool Has(ReviewPoolEntry e, string category) => e.Categories.Contains(category, StringComparer.Ordinal);
    private static string Name(ReviewPoolEntry e) => SituationReviewSelectionPolicy.SplitName(e.Split);
    private static void ValidatePolicy(ReviewSelectionPolicy policy)
    {
        if (policy.MaxPerRound < 1 || policy.ExceptionalMaxPerRound < policy.MaxPerRound || policy.ExceptionalMaxPerRound > 3 ||
            policy.CategoryPriority.Distinct(StringComparer.Ordinal).Count() != policy.CategoryPriority.Count ||
            !policy.TieBreaker.SequenceEqual(new[] { "unmet-category-count-desc", "match-count-asc", "round-count-asc", "stable-sha256-asc", "sample-id-ordinal-asc" }) ||
            policy.Splits.Any(p => !SituationReviewSelectionPolicy.SplitOrder.Contains(p.Key, StringComparer.Ordinal) || p.Value.Samples < 1 ||
                p.Value.Matches < 1 || p.Value.MinPerMatch < 1 || p.Value.MaxPerMatch < p.Value.MinPerMatch ||
                p.Value.Quotas.Any(q => q.Value < 1 || !policy.CategoryPriority.Contains(q.Key, StringComparer.Ordinal))))
            throw new InvalidDataException("Invalid review selection policy.");
    }
}

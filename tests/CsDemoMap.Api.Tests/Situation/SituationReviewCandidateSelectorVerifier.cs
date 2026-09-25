using System.Globalization;
using CsDemoMap.Api.Models;
using CsDemoMap.Api.Services;

namespace CsDemoMap.Api.Tests;

internal static class SituationReviewCandidateSelectorVerifier
{
    internal static void Verify()
    {
        var policy = Policy(2, 1, 2, 2, new Dictionary<string, int> { ["x"] = 1, ["y"] = 1, ["z"] = 1, ["negative"] = 1 });
        var seeds = Enumerable.Range(0, 3).Select(i => Row("trap-" + i)).OrderBy(e => SituationReviewSelectionPolicy.StableKey(e, policy), StringComparer.Ordinal).ToArray();
        // The first equal-score row blocks the two-row solution. Only rows 1+2 cover all four labels.
        ReviewPoolEntry[] trap = [seeds[0] with { Categories = ["x", "y"] }, seeds[1] with { Categories = ["x", "z"] }, seeds[2] with { Categories = ["y", "negative"] }];
        var plan = SituationReviewCandidateSelector.Select(trap, policy);
        Check(plan.QuotaShortfalls.Count == 0 && plan.Selected.Select(e => e.Entry.SampleId).ToHashSet().SetEquals(new[] { seeds[1].SampleId, seeds[2].SampleId }), "feasible greedy trap is repaired");
        Check(SituationReviewCandidateSelector.ValidateSelection(plan.Selected.Select(e => e.Entry).ToArray(), policy).Count == 0, "independent constraints");
        var signature = SituationCanonicalJson.Sha256(plan);
        var originalCulture = CultureInfo.CurrentCulture;
        var originalUi = CultureInfo.CurrentUICulture;
        try
        {
            foreach (var culture in new[] { "en-US", "tr-TR", "zh-CN" })
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
                CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(culture);
                foreach (var input in new[] { trap, trap.Reverse().ToArray(), new[] { trap[1], trap[0], trap[2] } })
                    Check(SituationCanonicalJson.Sha256(SituationReviewCandidateSelector.Select(input, policy)) == signature, "shuffle/culture deterministic plan");
            }
        }
        finally { CultureInfo.CurrentCulture = originalCulture; CultureInfo.CurrentUICulture = originalUi; }
        Check(plan.Backups["train/any"].SequenceEqual(new[] { seeds[0].SampleId }) &&
            plan.Backups["train/x"].SequenceEqual(plan.Backups["train/y"]), "unselected multi-category backup order");
        var replacementErrors = SituationReviewCandidateSelector.ValidateReplacementConstraints(plan.Selected.Select(e => e.Entry).ToArray(), seeds[1].SampleId, trap[0], policy);
        Check(replacementErrors.Contains("replacement-reduces-coverage/z"), "replacement cannot reduce quotas");
        Check(SituationReviewCandidateSelector.ValidateReplacementConstraints(plan.Selected.Select(e => e.Entry).ToArray(), seeds[1].SampleId, trap[0] with { Split = SituationTrainingSplit.Test }, policy).Contains("replacement-split-mismatch"), "replacement split boundary");

        var capacityPolicy = Policy(4, 2, 2, 2, new Dictionary<string, int> { ["x"] = 2 });
        var capacityPool = Enumerable.Range(0, 8).Select(i => Row("capacity-" + i) with { MatchRef = i < 6 ? "a" : "b", Categories = i < 6 ? ["x"] : [] }).ToArray();
        var capacity = SituationReviewCandidateSelector.Select(capacityPool, capacityPolicy);
        Check(capacity.Selected.Count == 4 && capacity.Selected.GroupBy(e => e.Entry.MatchRef).All(g => g.Count() == 2), "match minimum capacity reserved");
        Check(capacity.QuotaShortfalls.Count == 0, "match ceiling and quotas coexist");

        var roundPolicy = Policy(3, 1, 3, 3, new Dictionary<string, int>());
        var sameRound = Enumerable.Range(0, 3).Select(i => Row("round-" + i) with { RoundRef = "r", EventCategories = new[] { "event-" + i } }).ToArray();
        var round = SituationReviewCandidateSelector.Select(sameRound, roundPolicy);
        Check(round.Selected.Count == 3 && round.RoundExceptions.Count == 1 && round.RoundExceptions[0].DistinctEventCategories.Count == 3, "third row has three distinct event witnesses");
        var noEvents = SituationReviewCandidateSelector.Select(sameRound.Select(e => e with { EventCategories = [] }).ToArray(), roundPolicy);
        Check(noEvents.Selected.Count == 2 && noEvents.QuotaShortfalls.Any(q => q.ReasonCode == "constraint-combination-infeasible"), "round cap proven infeasible");
        var sharedEvents = SituationReviewCandidateSelector.Select(sameRound.Select(e => e with { EventCategories = ["event-0", "event-1"] }).ToArray(), roundPolicy);
        Check(sharedEvents.Selected.Count == 2, "event union without injective witnesses is insufficient");
        var largeBlocked = Enumerable.Range(0, 25).Select(i => Row("large-" + i) with { RoundRef = "one-round" }).ToArray();
        var unresolved = SituationReviewCandidateSelector.Select(largeBlocked, roundPolicy);
        Check(unresolved.QuotaShortfalls.Any(q => q.ReasonCode == "greedy-search-exhausted") &&
            unresolved.QuotaShortfalls.All(q => q.ReasonCode != "constraint-combination-infeasible"), "bounded search never claims objective infeasibility");

        // Independent enumeration checks feasibility, without using selector constraint/score helpers.
        for (var scenario = 0; scenario < 16; scenario++)
        {
            var oraclePolicy = Policy(2, 1, 2, 2, new Dictionary<string, int> { ["x"] = 1, ["negative"] = 1 });
            var oraclePool = Enumerable.Range(0, 6).Select(i => Row("oracle-" + i) with
            {
                Categories = new[] { (i + scenario) % 3 == 0 ? "x" : "", ((i * 3) ^ scenario) % 4 == 0 ? "negative" : "" }.Where(x => x.Length > 0).ToArray(),
                SourceSceneSha256 = "oracle-scene-" + ((i + scenario) % 4)
            }).ToArray();
            var feasible = (from a in oraclePool from b in oraclePool
                where a.SampleId != b.SampleId && a.SourceSceneSha256 != b.SourceSceneSha256
                select new[] { a, b }).Any(pair => pair.Any(e => e.Categories.Contains("x")) && pair.Any(e => e.Categories.Contains("negative")));
            var actual = SituationReviewCandidateSelector.Select(oraclePool, oraclePolicy);
            Check((actual.QuotaShortfalls.Count == 0) == feasible, "selector agrees with independent pair oracle");
        }

        foreach (var kind in new[] { "id", "scene", "input" })
        {
            var first = Row("unique-0");
            var second = Row("unique-1");
            second = kind switch { "id" => second with { SampleId = first.SampleId }, "scene" => second with { SourceSceneSha256 = first.SourceSceneSha256 }, _ => second with { ModelInputSha256 = first.ModelInputSha256 } };
            var duplicate = SituationReviewCandidateSelector.Select(new[] { first, second }, Policy(2, 1, 2, 2, new Dictionary<string, int>()));
            Check(duplicate.Selected.Count == 1 && duplicate.QuotaShortfalls.Any(q => q.ReasonCode == "constraint-combination-infeasible"), "duplicate " + kind + " excluded");
        }
        var missing = SituationReviewCandidateSelector.Select(new[] { Row("missing") }, Policy(1, 1, 1, 1, new Dictionary<string, int> { ["negative"] = 1 }));
        Check(missing.QuotaShortfalls.Single().ReasonCode == "raw-pool-insufficient" && missing.QuotaShortfalls.Single().Available == 0, "raw negative shortage explicit");

        var splitPolicy = Policy(1, 1, 1, 1, new Dictionary<string, int>());
        splitPolicy = splitPolicy with { Splits = new Dictionary<string, ReviewSplitPolicy>(splitPolicy.Splits) { ["dev"] = splitPolicy.Splits["train"], ["test"] = splitPolicy.Splits["train"] } };
        var splitPool = new[] { Row("test") with { Split = SituationTrainingSplit.Test }, Row("dev") with { Split = SituationTrainingSplit.Dev }, Row("train") };
        var splits = SituationReviewCandidateSelector.Select(splitPool, splitPolicy);
        Check(splits.Selected.Select(e => e.ReviewOrdinal).SequenceEqual(new[] { 1, 2, 3 }) && splits.Selected.Select(e => e.Entry.Split).SequenceEqual(new[] { SituationTrainingSplit.Train, SituationTrainingSplit.Dev, SituationTrainingSplit.Test }), "global ordinal and split order");

        var crossPolicy = Policy(1, 1, 1, 1, new Dictionary<string, int>());
        crossPolicy = crossPolicy with { Splits = new Dictionary<string, ReviewSplitPolicy>(crossPolicy.Splits) { ["dev"] = crossPolicy.Splits["train"] } };
        var crossTrain = new[] { Row("cross-a"), Row("cross-b") }.OrderBy(e => SituationReviewSelectionPolicy.StableKey(e, crossPolicy), StringComparer.Ordinal).ToArray();
        var crossDev = Row("cross-dev") with { Split = SituationTrainingSplit.Dev, SourceSceneSha256 = crossTrain[0].SourceSceneSha256 };
        var cross = SituationReviewCandidateSelector.Select(new[] { crossTrain[0], crossTrain[1], crossDev }, crossPolicy);
        Check(cross.QuotaShortfalls.Any(q => q.Split == "dev" && q.ReasonCode == "greedy-search-exhausted") &&
            cross.QuotaShortfalls.All(q => q.ReasonCode != "constraint-combination-infeasible"), "split-local proof cannot claim global infeasibility");
        Check(SituationReviewCandidateSelector.ValidateSelection(new[] { crossTrain[1], crossDev }, crossPolicy).Count == 0, "cross-split counterexample is globally feasible");

        var backupPolicy = Policy(2, 2, 1, 1, new Dictionary<string, int> { ["x"] = 2 });
        var original = Row("replace-original") with { Categories = ["x"] };
        var retained = Row("replace-retained") with { MatchRef = "b", Categories = ["x"] };
        var badBackup = Row("replace-bad") with { MatchRef = "b", Categories = ["x"] };
        var firstBackup = Row("replace-first") with { Categories = ["x"] };
        var laterBackup = Row("replace-later") with { Categories = ["x"] };
        var outsider = Row("replace-outsider") with { Categories = ["x"] };
        ReviewPoolEntry[] replacementPool = [original, retained, badBackup, firstBackup, laterBackup, outsider];
        var backupPlan = new ReviewSelectionPlan([new(1, original), new(2, retained)],
            new Dictionary<string, IReadOnlyList<string>> { ["train/x"] = [badBackup.SampleId, firstBackup.SampleId, laterBackup.SampleId] }, [], []);
        Check(SituationReviewCandidateSelector.ValidateReplacement(backupPlan, replacementPool, original.SampleId, "x", firstBackup, backupPolicy).Count == 0, "first feasible backup skips structurally infeasible earlier row");
        Check(SituationReviewCandidateSelector.ValidateReplacement(backupPlan, replacementPool, original.SampleId, "x", laterBackup, backupPolicy).Contains("replacement-skips-feasible-backup"), "cannot skip first feasible backup");
        Check(SituationReviewCandidateSelector.ValidateReplacement(backupPlan, replacementPool, original.SampleId, "x", outsider, backupPolicy).Contains("replacement-not-in-backups"), "cannot choose source outside frozen backups");
        Check(SituationReviewCandidateSelector.ValidateReplacement(backupPlan, replacementPool, original.SampleId, "x", firstBackup with { Tick = 123 }, backupPolicy).Contains("replacement-source-mismatch"), "replacement row must match actual source");
        Console.WriteLine("Stage-four review selector checks passed.");
    }

    private static ReviewSelectionPolicy Policy(int count, int matches, int minimum, int maximum, IReadOnlyDictionary<string, int> quotas) =>
        new("synthetic", "synthetic", "situation-review-order-v1", 2, 3, [], [], ["event-0", "event-1", "event-2"],
            ["x", "y", "z", "negative"], ["unmet-category-count-desc", "match-count-asc", "round-count-asc", "stable-sha256-asc", "sample-id-ordinal-asc"],
            new Dictionary<string, ReviewSplitPolicy> { ["train"] = new(count, matches, minimum, maximum, quotas) });

    private static ReviewPoolEntry Row(string id) => new(id, SituationTrainingSplit.Train, "a", id, 1,
        "scene-" + id, "input-" + id, "facts-" + id, "prelabel-" + id, "record-" + id, 1, [], []);
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Review selector: " + message);
    }
}

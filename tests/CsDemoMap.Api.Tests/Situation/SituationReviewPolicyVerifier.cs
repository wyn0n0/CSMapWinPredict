using CsDemoMap.Api.Models;
using CsDemoMap.Api.Services;

namespace CsDemoMap.Api.Tests;

internal static class SituationReviewPolicyVerifier
{
    internal static void Verify()
    {
        var checks = 0;
        var policy = SituationReviewSelectionPolicy.LoadFrozen();
        Check(SituationReviewSelectionPolicy.Hash(policy) == SituationReviewSelectionPolicy.FrozenPolicySha256, "frozen policy hash");
        Check(policy.Splits["train"].Samples == 220 && policy.Splits["dev"].Samples == 40 && policy.Splits["test"].Samples == 40, "split uses");
        var record = SituationTrainingContractVerifier.BuildRecord();
        var ordinaryFacts = record.Input.Facts with
        {
            Alive = new(5, 5), Formation = new(SituationFormation.Grouped, SituationFormation.Spread),
            IsolatedSide = SituationIsolatedSide.None, ContactRisk = SituationContactRisk.Low,
            Confidence = SituationConfidence.High, DataQuality = []
        };
        record = record with { Metadata = record.Metadata with { Phase = SituationTrainingPhase.Live, SelectionTags = ["live-start"] },
            Input = record.Input with { Facts = ordinaryFacts } };
        var ordinary = SituationReviewSelectionPolicy.Describe(record, 1, policy);
        Check(ordinary.Categories.SequenceEqual(new[] { "grouped-spread", "high-confidence", "low-medium-contact", "none-isolation", "ordinary-live" }), "ordinary negatives");
        var rare = record with
        {
            Metadata = record.Metadata with { Phase = SituationTrainingPhase.PostPlant, SelectionTags = ["bomb-defusing", "clutch-1vN"] },
            Input = record.Input with { Facts = ordinaryFacts with
            {
                Alive = new(1, 3), Formation = new(SituationFormation.Split, SituationFormation.Grouped),
                IsolatedSide = SituationIsolatedSide.T, ContactRisk = SituationContactRisk.High,
                Confidence = SituationConfidence.Low
            } }
        };
        var positive = SituationReviewSelectionPolicy.Describe(rare, 2, policy);
        Check(positive.Categories.SequenceEqual(new[] { "c4-transition", "clutch", "formation-split", "high-contact", "isolated", "low-quality", "post-plant" }), "multilabel categories");
        Check(positive.EventCategories.SequenceEqual(new[] { "bomb-defusing" }), "only defined event categories");
        var unreliable = rare with { Input = rare.Input with { Facts = rare.Input.Facts with
            { DataQuality = [new("position-invalid", ["/players"], SituationQualitySeverity.Warning)] } } };
        Check(!SituationReviewSelectionPolicy.Describe(unreliable, 3, policy).Categories.Contains("isolated"), "unreliable isolation excluded");
        var two = rare with { Input = rare.Input with { Facts = rare.Input.Facts with { Alive = new(2, 3) } } };
        Check(SituationReviewSelectionPolicy.Describe(two, 4, policy).Categories.Contains("clutch"), "2v3 clutch");
        var even = two with { Input = two.Input with { Facts = two.Input.Facts with { Alive = new(2, 2) } } };
        Check(!SituationReviewSelectionPolicy.Describe(even, 5, policy).Categories.Contains("clutch"), "2v2 excluded");
        var unknown = two with { Input = two.Input with { Facts = two.Input.Facts with { Alive = new(null, 3) } } };
        Check(!SituationReviewSelectionPolicy.Describe(unknown, 6, policy).Categories.Contains("clutch"), "unknown alive not guessed");
        var tagged = record with { Metadata = record.Metadata with { SelectionTags = ["clutch-1vN", "high-contact-risk", "isolated"] } };
        Check(!SituationReviewSelectionPolicy.Describe(tagged, 7, policy).Categories.Contains("clutch"), "Facts determine state categories");
        Check(SituationReviewSelectionPolicy.StableKey(ordinary, policy) == SituationReviewSelectionPolicy.StableKey(ordinary with { LineNumber = 999 }, policy), "order independent of source location");
        Console.WriteLine($"Review policy checks passed: {checks}");
        void Check(bool value, string description)
        {
            if (!value) throw new InvalidOperationException("Review policy check failed: " + description);
            checks++;
        }
    }
}

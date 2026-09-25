using System.Text.Json;
using System.Text.Json.Serialization;
using CsDemoMap.Api.Models;

namespace CsDemoMap.Api.Services;

internal static class SituationReviewSelectionPolicy
{
    internal const string FileName = "situation-review-selection-v1.json";
    internal const string ApprovedDatasetSha256 = "e60e8bf001ea55b1932fba7bed83468ee104cac8516525fe539409edaae8a3f9";
    internal const string FrozenPolicySha256 = "5bfb76866f902fc7851b98d437acb0115e846e02a834442d4a1ddbf394e2ae7f";
    internal static readonly string[] SplitOrder = ["train", "dev", "test"];
    internal static readonly JsonSerializerOptions StrictJson = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    internal static ReviewSelectionPolicy LoadFrozen()
    {
        var assembly = typeof(SituationReviewSelectionPolicy).Assembly;
        using var stream = assembly.GetManifestResourceStream("CsDemoMap.Api.SituationReview." + FileName)
            ?? throw new InvalidDataException("Review selection resource is missing.");
        using var reader = new StreamReader(stream);
        var json = reader.ReadToEnd();
        SituationTrainingContractJson.RejectDuplicateProperties(json, "Review policy");
        SituationTrainingContractJson.RequireCompleteShape<ReviewSelectionPolicy>(json, "Review policy");
        var policy = JsonSerializer.Deserialize<ReviewSelectionPolicy>(json, StrictJson)
            ?? throw new InvalidDataException("Review policy is empty.");
        Validate(policy);
        if (Hash(policy) != FrozenPolicySha256)
            throw new InvalidDataException("Review selection policy differs from its frozen hash.");
        return policy;
    }

    internal static void Validate(ReviewSelectionPolicy policy)
    {
        if (policy.SchemaVersion != "situation-review-selection-v1" ||
            policy.AlgorithmVersion != "situation-review-greedy-v1" ||
            policy.StableHashDomain != "situation-review-order-v1" ||
            policy.MaxPerRound != 2 || policy.ExceptionalMaxPerRound != 3 ||
            !policy.Splits.Keys.Order(StringComparer.Ordinal).SequenceEqual(new[] { "dev", "test", "train" }) ||
            !policy.TieBreaker.SequenceEqual(new[] { "unmet-category-count-desc", "match-count-asc", "round-count-asc", "stable-sha256-asc", "sample-id-ordinal-asc" }))
            throw new InvalidDataException("Review policy identity or ordering is invalid.");
        foreach (var item in policy.Splits)
        {
            var value = item.Value;
            if (value.Samples <= 0 || value.Matches <= 0 || value.MinPerMatch <= 0 || value.MaxPerMatch < value.MinPerMatch ||
                value.Samples < value.Matches * value.MinPerMatch || value.Samples > value.Matches * value.MaxPerMatch ||
                value.Quotas.Any(q => !policy.CategoryPriority.Contains(q.Key, StringComparer.Ordinal) || q.Value <= 0 || q.Value > value.Samples))
                throw new InvalidDataException("Review split policy is invalid.");
        }
    }

    internal static string Hash(ReviewSelectionPolicy policy) => SituationCanonicalJson.Sha256(policy);
    internal static string SplitName(SituationTrainingSplit split) => split switch
    {
        SituationTrainingSplit.Train => "train", SituationTrainingSplit.Dev => "dev",
        SituationTrainingSplit.Test => "test", _ => throw new InvalidDataException("Invalid review split.")
    };
    internal static string StableKey(ReviewPoolEntry entry, ReviewSelectionPolicy policy) =>
        SituationArtifactIO.Sha256(policy.StableHashDomain + "|" + entry.SampleId);

    internal static ReviewPoolEntry Describe(SituationTrainingRecordV1 record, long lineNumber, ReviewSelectionPolicy policy)
    {
        var facts = record.Input.Facts;
        var metadata = record.Metadata;
        var quality = facts.DataQuality.Select(q => q.Code).ToHashSet(StringComparer.Ordinal);
        var tags = new List<string>();
        if (metadata.Phase == SituationTrainingPhase.PostPlant) tags.Add("post-plant");
        var t = facts.Alive.T;
        var ct = facts.Alive.CT;
        if ((t == 1 && ct >= 2) || (ct == 1 && t >= 2) || (t == 2 && ct >= 3) || (ct == 2 && t >= 3)) tags.Add("clutch");
        if (facts.Formation.T == SituationFormation.Split || facts.Formation.CT == SituationFormation.Split) tags.Add("formation-split");
        if (facts.IsolatedSide is SituationIsolatedSide.T or SituationIsolatedSide.CT or SituationIsolatedSide.Both &&
            !policy.IsolationBlockingQualityCodes.Any(quality.Contains)) tags.Add("isolated");
        if (facts.ContactRisk == SituationContactRisk.High) tags.Add("high-contact");
        var events = metadata.SelectionTags.Intersect(policy.EventCategories, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        if (events.Any(e => e.StartsWith("bomb-", StringComparison.Ordinal))) tags.Add("c4-transition");
        if (facts.Confidence == SituationConfidence.Low || policy.RelevantQualityCodes.Any(quality.Contains)) tags.Add("low-quality");
        var lowMedium = facts.ContactRisk is SituationContactRisk.Low or SituationContactRisk.Medium;
        if (metadata.Phase == SituationTrainingPhase.Live && lowMedium && facts.IsolatedSide == SituationIsolatedSide.None) tags.Add("ordinary-live");
        if (lowMedium) tags.Add("low-medium-contact");
        if (facts.IsolatedSide == SituationIsolatedSide.None) tags.Add("none-isolation");
        if (facts.Formation.T is SituationFormation.Grouped or SituationFormation.Spread &&
            facts.Formation.CT is SituationFormation.Grouped or SituationFormation.Spread) tags.Add("grouped-spread");
        if (facts.Confidence == SituationConfidence.High) tags.Add("high-confidence");
        return new(record.SampleId, metadata.Split, metadata.MatchRef, metadata.RoundRef, metadata.Tick,
            metadata.SourceSceneSha256, metadata.ModelInputSha256, metadata.FactsSha256, metadata.PrelabelSha256,
            SituationCanonicalJson.Sha256(record), lineNumber, tags.Order(StringComparer.Ordinal).ToArray(), events);
    }
}

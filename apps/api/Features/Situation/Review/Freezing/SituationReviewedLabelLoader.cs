using CsDemoMap.Api.Models;

namespace CsDemoMap.Api.Services;

// Training callers receive the reviewed input together with its label. They must
// not join v8 narratives to the historical v1 facts by sampleId alone.
internal static class SituationReviewedLabelLoader
{
    internal static async Task<IReadOnlyList<ReviewLabelExample>> LoadAsync(string output, IReviewCatalog catalog,
        string baseDatasetSha256, SituationTrainingSplit split, string purpose, string expectedAnalysisRuleVersion, CancellationToken ct)
    {
        var allowed = split switch
        {
            SituationTrainingSplit.Train => new[] { "sft" },
            SituationTrainingSplit.Dev => ["checkpoint-selection", "evaluation", "prompt-selection"],
            SituationTrainingSplit.Test => ["final-evaluation"], _ => []
        };
        if (!allowed.Contains(purpose, StringComparer.Ordinal)) throw new ReviewException("label-use-forbidden");
        var manifest = await SituationReviewFreezer.VerifyAsync(output, catalog, baseDatasetSha256, ct);
        if (manifest.ReviewStatus != "frozen") throw new ReviewException("review-not-approved");
        var inputs = (await File.ReadAllLinesAsync(Path.Combine(output, "review-inputs.jsonl"), ct))
            .Select(SituationReviewFreezer.ReadStrict<SituationReviewCandidateV1>).ToDictionary(c => c.SampleId, StringComparer.Ordinal);
        var name = SituationReviewSelectionPolicy.SplitName(split) + "-labels.jsonl";
        var rows = new List<ReviewLabelExample>();
        foreach (var line in await File.ReadAllLinesAsync(Path.Combine(output, name), ct))
        {
            var label = SituationReviewFreezer.ReadStrict<SituationFrozenReviewLabelV1>(line);
            if (!inputs.TryGetValue(label.SampleId, out var candidate) || candidate.Split != split ||
                candidate.Input.Facts.AnalysisRuleVersion != expectedAnalysisRuleVersion || label.ReviewVersion != manifest.ReviewVersion)
                throw new ReviewException("label-input-mismatch");
            SituationTrainingContractJson.ValidateFrozenReviewLabel(label, split, candidate);
            if (!label.AllowedUse.Contains(purpose, StringComparer.Ordinal)) throw new ReviewException("label-use-forbidden");
            rows.Add(new(candidate.Input, label));
        }
        return rows;
    }
}

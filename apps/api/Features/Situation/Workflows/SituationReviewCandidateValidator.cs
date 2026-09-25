using CsDemoMap.Api.Models;

namespace CsDemoMap.Api.Services;

internal static class SituationReviewCandidateValidator
{
    internal static async Task<SituationReviewCandidateValidationResult> VerifyAsync(string datasetRoot, string outputRoot, CancellationToken cancellationToken)
    {
        var source = await SituationReviewCandidateReader.OpenApprovedBaseAsync(datasetRoot, cancellationToken);
        var policy = SituationReviewSelectionPolicy.LoadFrozen();
        var pool = await SituationReviewCandidateReader.ReadPoolAsync(source, policy, cancellationToken);
        return await VerifyCoreAsync(source, outputRoot, policy, pool, null, cancellationToken);
    }

    internal static Task<SituationReviewCandidateValidationResult> VerifyPreparedAsync(SituationReviewCandidateBase source, string outputRoot,
        ReviewSelectionPolicy policy, IReadOnlyList<ReviewPoolEntry> pool, SituationReviewCandidateManifestV1 prepared, CancellationToken cancellationToken) =>
        VerifyCoreAsync(source, outputRoot, policy, pool, prepared, cancellationToken);

    private static async Task<SituationReviewCandidateValidationResult> VerifyCoreAsync(SituationReviewCandidateBase source, string outputRoot,
        ReviewSelectionPolicy policy, IReadOnlyList<ReviewPoolEntry> pool, SituationReviewCandidateManifestV1? prepared, CancellationToken cancellationToken)
    {
        SituationReviewCandidateReader.RequireFiles(outputRoot, ["manifest.json", "provenance.json", "review-candidates.jsonl", "review-stats.json"]);
        var disk = await SituationReviewCandidateReader.ReadJsonAsync<SituationReviewCandidateManifestV1>(Path.Combine(outputRoot, "manifest.json"), cancellationToken);
        if (prepared is not null && (disk.Status != "incomplete" || !SituationReviewCandidateReader.Equal(disk with { Status = "complete" }, prepared)))
            throw new InvalidDataException("Prepared review manifest differs from incomplete disk manifest.");
        var manifest = prepared ?? disk;
        if (manifest.SchemaVersion != "situation-review-candidate-manifest-v1" || manifest.Status != "complete" ||
            manifest.BaseManifestSha256 != SituationReviewSelectionPolicy.ApprovedDatasetSha256 || manifest.PolicySha256 != SituationReviewSelectionPolicy.Hash(policy) ||
            !SituationReviewCandidateReader.Equal(manifest.SourceIndex, pool.OrderBy(e => e.SampleId, StringComparer.Ordinal).ToArray()))
            throw new InvalidDataException("Review manifest identity/source index mismatch.");
        var plan = new ReviewSelectionPlan(manifest.Selected, manifest.Backups, manifest.QuotaShortfalls, manifest.RoundExceptions);
        SituationReviewCandidateStatistics.ValidatePlan(pool, plan, policy, requireComplete: true);
        var inventory = await SituationArtifactIO.InventoryFilesAsync(outputRoot, cancellationToken, "manifest.json");
        var expectedFiles = inventory.Select(f => new SituationArtifactFileV1(f.Path, f.Bytes, f.Path == "review-candidates.jsonl" ? manifest.Selected.Count : null, f.Sha256)).ToArray();
        if (!SituationReviewCandidateReader.Equal(manifest.Files, expectedFiles)) throw new InvalidDataException("Review manifest file inventory mismatch.");
        var statistics = SituationReviewCandidateStatistics.Create(pool, plan, policy);
        var storedStats = await SituationReviewCandidateReader.ReadJsonAsync<SituationReviewCandidateStatisticsV1>(Path.Combine(outputRoot, "review-stats.json"), cancellationToken);
        if (!SituationReviewCandidateReader.Equal(statistics, storedStats)) throw new InvalidDataException("Review statistics mismatch.");
        var provenance = await SituationReviewCandidateReader.ReadJsonAsync<SituationReviewCandidateProvenanceV1>(Path.Combine(outputRoot, "provenance.json"), cancellationToken);
        await SituationReviewCandidateProvenance.VerifyAsync(source, policy, provenance, cancellationToken);
        var records = await SituationReviewCandidateReader.MaterializeAsync(source, manifest.Selected, policy, cancellationToken);
        var count = 0;
        await foreach (var line in SituationReviewCandidateReader.ReadLinesAsync(Path.Combine(outputRoot, "review-candidates.jsonl"), cancellationToken))
        {
            if (count >= manifest.Selected.Count) throw new InvalidDataException("Extra review candidate row.");
            var selected = manifest.Selected[count++];
            var candidate = SituationTrainingContractJson.DeserializeReviewCandidate(line);
            var expected = SituationReviewCandidateReader.Candidate(selected, records[selected.Entry.SampleId]);
            if (line != SituationCanonicalJson.Serialize(candidate) || !SituationReviewCandidateReader.Equal(candidate, expected))
                throw new InvalidDataException("Review candidate payload/hash/source/order mismatch.");
        }
        if (count != manifest.Selected.Count) throw new InvalidDataException("Review candidate rows missing.");
        // Protect the interval between pool reading, payload reading and publication.
        await SituationReviewCandidateReader.OpenApprovedBaseAsync(source.Root, cancellationToken);
        await SituationReviewCandidateProvenance.VerifyAsync(source, policy, provenance, cancellationToken);
        return new(manifest, statistics);
    }
}

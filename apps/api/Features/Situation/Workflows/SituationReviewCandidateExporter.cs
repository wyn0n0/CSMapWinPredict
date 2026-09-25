using System.Text;
using CsDemoMap.Api.Models;

namespace CsDemoMap.Api.Services;

internal static class SituationReviewCandidateExporter
{
    internal static async Task<SituationReviewCandidateValidationResult> ExportAsync(string datasetRoot, string outputRoot, CancellationToken cancellationToken)
    {
        SituationArtifactIO.EnsureNewOutput(outputRoot);
        var basePrefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(datasetRoot)) + Path.DirectorySeparatorChar;
        if (Path.GetFullPath(outputRoot).StartsWith(basePrefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Review output cannot be inside the immutable base dataset.");
        var source = await SituationReviewCandidateReader.OpenApprovedBaseAsync(datasetRoot, cancellationToken);
        var policy = SituationReviewSelectionPolicy.LoadFrozen();
        var pool = await SituationReviewCandidateReader.ReadPoolAsync(source, policy, cancellationToken);
        var plan = SituationReviewCandidateSelector.Select(pool, policy);
        var provenance = await SituationReviewCandidateProvenance.CaptureAsync(source, policy, cancellationToken);
        return await PublishAsync(outputRoot, pool, plan, policy, provenance,
            token => SituationReviewCandidateReader.MaterializeAsync(source, plan.Selected, policy, token),
            (manifest, token) => SituationReviewCandidateValidator.VerifyPreparedAsync(source, outputRoot, policy, pool, manifest, token), cancellationToken);
    }

    // The production entry always constructs these inputs from the pinned base. This seam permits
    // isolated write/cancel/publication tests without a CLI flag that accepts a different base.
    internal static async Task<SituationReviewCandidateValidationResult> PublishAsync(string outputRoot, IReadOnlyList<ReviewPoolEntry> pool,
        ReviewSelectionPlan plan, ReviewSelectionPolicy policy, SituationReviewCandidateProvenanceV1 provenance,
        Func<CancellationToken, Task<IReadOnlyDictionary<string, SituationTrainingRecordV1>>> materialize,
        Func<SituationReviewCandidateManifestV1, CancellationToken, Task<SituationReviewCandidateValidationResult>> verify,
        CancellationToken cancellationToken)
    {
        outputRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(outputRoot));
        var parent = Path.GetDirectoryName(outputRoot) ?? throw new InvalidDataException("Review output has no parent directory.");
        Directory.CreateDirectory(parent);
        // CreateNew is an inter-process ownership claim; DeleteOnClose only removes this
        // process's handle-owned lock, never a competing publisher's file.
        await using var ownership = new FileStream(outputRoot + ".review-publish.lock", FileMode.CreateNew,
            FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
        SituationArtifactIO.EnsureNewOutput(outputRoot);
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(outputRoot);
        var manifest = new SituationReviewCandidateManifestV1("situation-review-candidate-manifest-v1", "incomplete",
            SituationReviewSelectionPolicy.ApprovedDatasetSha256, SituationReviewSelectionPolicy.Hash(policy), plan.Selected, plan.Backups,
            pool.OrderBy(e => e.SampleId, StringComparer.Ordinal).ToArray(), plan.RoundExceptions, plan.QuotaShortfalls, []);
        await WriteAsync(Path.Combine(outputRoot, "manifest.json"), manifest, cancellationToken);
        var statistics = SituationReviewCandidateStatistics.Create(pool, plan, policy);
        await WriteAsync(Path.Combine(outputRoot, "review-stats.json"), statistics, cancellationToken);
        await WriteAsync(Path.Combine(outputRoot, "provenance.json"), provenance, cancellationToken);
        SituationReviewCandidateStatistics.ValidatePlan(pool, plan, policy, requireComplete: false);
        var records = await materialize(cancellationToken);
        await using (var stream = new FileStream(Path.Combine(outputRoot, "review-candidates.jsonl"), FileMode.CreateNew, FileAccess.Write, FileShare.None))
        await using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
        {
            foreach (var selected in plan.Selected)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var candidate = SituationReviewCandidateReader.Candidate(selected, records[selected.Entry.SampleId]);
                SituationTrainingContractJson.Validate(candidate);
                await writer.WriteAsync((SituationCanonicalJson.Serialize(candidate) + "\n").AsMemory(), cancellationToken);
            }
            await writer.FlushAsync(cancellationToken);
            stream.Flush(flushToDisk: true);
        }
        var files = await SituationArtifactIO.InventoryFilesAsync(outputRoot, cancellationToken, "manifest.json");
        manifest = manifest with { Files = files.Select(f => new SituationArtifactFileV1(f.Path, f.Bytes, f.Path == "review-candidates.jsonl" ? plan.Selected.Count : null, f.Sha256)).ToArray() };
        await WriteAsync(Path.Combine(outputRoot, "manifest.json"), manifest, cancellationToken);
        SituationReviewCandidateStatistics.ValidatePlan(pool, plan, policy, requireComplete: true);
        var complete = manifest with { Status = "complete" };
        var validated = await verify(complete, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        await WriteAsync(Path.Combine(outputRoot, "manifest.json"), complete, cancellationToken);
        return validated;
    }

    internal static Task WriteAsync<T>(string path, T value, CancellationToken cancellationToken) =>
        SituationArtifactIO.WriteCanonicalAsync(path, SituationCanonicalJson.Serialize(value), cancellationToken);
}

using CsDemoMap.Api.Services;

namespace CsDemoMap.Api.Tests;

internal static class SituationRaycastReviewVerifier
{
    internal static async Task VerifyAsync(CancellationToken ct)
    {
        var source = new SituationReviewWorkflowVerifier.SyntheticReviewCatalog();
        var root = Path.Combine(Path.GetTempPath(), "raycast-review-" + Guid.NewGuid().ToString("N"));
        var artifact = Path.Combine(root, "artifact");
        var work = Path.Combine(root, "work");
        var repo = SituationArtifactIO.FindRepositoryRoot(AppContext.BaseDirectory);
        var catalog = await SituationRaycastReviewCatalog.OpenAsync(source, artifact, repo, ct);
        var id = catalog.InitialSelected[0].Entry.SampleId;
        var initial = catalog.InitialSelected.Select(s => new ReviewActiveSample(s.ReviewOrdinal, s.Entry.SampleId)).ToArray();
        void Check(bool ok) { if (!ok) throw new InvalidDataException("Raycast review integration failed."); }
        Check(catalog.Identity.DatasetSha256 != source.Identity.DatasetSha256);
        await using (var store = await SituationReviewWorkStore.OpenAsync(work, catalog.Identity, initial, catalog.GetCandidateAsync, ct))
        {
            var backend = new SituationReviewDecisionService(catalog, store);
            var detail = await backend.GetSampleAsync(id, ct);
            Check(detail.Candidate.Input.Facts.AnalysisRuleVersion == "situation-analysis-rules-v2-raycast-1");
            await backend.SaveAsync(new(id, 0, Guid.NewGuid().ToString("D"), "rejected", new(true,true,true,false), [], [],
                "合成替换测试", "narrative-unhelpful", null), ct);
            await backend.ReplaceAsync(new(id, store.Snapshot.WorkspaceRevision, Guid.NewGuid().ToString("D")), ct);
            var next = store.Snapshot.Active.Single().SampleId;
            Check((await backend.GetSampleAsync(next, ct)).Candidate.SchemaVersion == SituationRaycastReviewCatalog.CandidateSchema);
            await backend.SaveAsync(new(next,0,Guid.NewGuid().ToString("D"),"approved",new(true,true,true,false),[],[],null,null,null),ct);
        }
        catalog = await SituationRaycastReviewCatalog.OpenAsync(source, artifact, repo, ct);
        await using (var recovered = await SituationReviewWorkStore.OpenAsync(work, catalog.Identity, initial, catalog.GetCandidateAsync, ct))
        {
            SituationReviewReplacementService.VerifyRecoveredState(catalog, recovered.Snapshot);
            Check(recovered.Snapshot.Decisions.Count == 2 && recovered.Snapshot.Replacements.Count == 1);
        }
        try
        {
            await using var mixed = await SituationReviewWorkStore.OpenAsync(work,source.Identity,initial,source.GetCandidateAsync,ct);
            throw new InvalidDataException("Old workspace identity was accepted.");
        }
        catch (ReviewException) { }
        await File.AppendAllTextAsync(Path.Combine(artifact,"review-candidates.jsonl")," ",ct);
        try { await SituationRaycastReviewCatalog.OpenAsync(source, artifact, repo, ct); throw new InvalidDataException("Tampered candidates were accepted."); }
        catch (ReviewException) { }
        Console.WriteLine("Raycast review integration passed: derivation, replacement, save, recovery, version isolation, tamper rejection.");
    }
}

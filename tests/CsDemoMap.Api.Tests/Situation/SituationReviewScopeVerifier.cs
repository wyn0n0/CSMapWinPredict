using CsDemoMap.Api.Models;
using CsDemoMap.Api.Services;

namespace CsDemoMap.Api.Tests;

internal static class SituationReviewScopeVerifier
{
    internal static async Task VerifyAsync(CancellationToken ct)
    {
        var rows = Enumerable.Range(1, 300).Select(i => new ReviewSampleSummary("sample-" + SituationArtifactIO.Sha256(i.ToString()),
            i, i <= 220 ? "train" : i <= 260 ? "dev" : "test", ["category-" + i % 12],
            i == 16 ? "approved" : "unreviewed", i == 16 ? 1 : 0, false)).ToArray();
        var selected = SituationReviewScope.Select(rows, new HashSet<int> { 250 });
        void Check(bool value) { if (!value) throw new InvalidDataException("Review scope assertion failed."); }
        Check(selected.Count == 50 && selected.Count(s => s.Split == "train") == 36 &&
            selected.Count(s => s.Split == "dev") == 7 && selected.Count(s => s.Split == "test") == 7);
        Check(selected.Any(s => s.ReviewOrdinal == 16) && selected.Any(s => s.ReviewOrdinal == 250));
        Check(SituationReviewCandidateReader.Equal(selected, SituationReviewScope.Select(rows.Reverse().ToArray(), new HashSet<int> { 250 })));
        try { SituationReviewScope.Select(rows.Select(r => r with { Decision = "approved" }).ToArray(), new HashSet<int>()); throw new Exception("Existing progress was dropped."); }
        catch (ReviewException e) when (e.Code == "review-scope-cannot-retain-progress") { }
        var root = Path.Combine(Path.GetTempPath(), "review-scope-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        await using var store = new ScopeStore(rows);
        var backend = new ScopeBackend(rows);
        var scoped = await SituationReviewScope.OpenAsync(backend, store, root, 50, ct);
        Check(scoped.GetSession().Progress is { Total: 50, Completed: 1, Unreviewed: 49 });
        Check(scoped.GetSamples().Select(s => s.ReviewOrdinal).SequenceEqual(Enumerable.Range(1, 50)));
        var restored = await SituationReviewScope.OpenAsync(backend, store, root, null, ct);
        Check(SituationReviewCandidateReader.Equal(scoped.GetSamples(), restored.GetSamples()));
        var hidden = rows.First(r => !scoped.GetSamples().Any(s => s.SampleId == r.SampleId));
        try { await scoped.ValidateAsync(new(hidden.SampleId, null!), ct); throw new Exception("Out-of-scope mutation accepted."); }
        catch (ReviewException e) when (e.Code == "outside-review-scope") { }
        var file = Path.Combine(root, "review-scope.json");
        await File.WriteAllTextAsync(file, (await File.ReadAllTextAsync(file, ct)).Replace("\"target\":50", "\"target\":49"), ct);
        try { await SituationReviewScope.OpenAsync(backend, store, root, null, ct); throw new Exception("Tampered scope accepted."); }
        catch (ReviewException e) when (e.Code == "review-scope-mismatch") { }
        Console.WriteLine("Review scope checks passed: 50 rows, 36/7/7 splits, progress retention, deterministic selection, restore, scope boundary and tamper rejection.");
    }

    private sealed class ScopeStore(ReviewSampleSummary[] rows) : IReviewWorkStore
    {
        public ReviewWorkspaceIdentity Identity => new SituationReviewWorkflowVerifier.SyntheticReviewCatalog().Identity;
        public ReviewWorkSnapshot Snapshot => new(1, false, rows.Select(r => new ReviewActiveSample(r.ReviewOrdinal, r.SampleId)).ToArray(),
            new Dictionary<string, SituationReviewDecisionV1>(), []);
        public ReviewCommitReceipt? FindReceipt(string id, string hash) => null;
        public Task<ReviewCommitReceipt> SaveDecisionAsync(string id, string hash, int revision, SituationReviewDecisionV1 decision, CancellationToken ct) => throw new NotSupportedException();
        public Task<ReviewCommitReceipt> ReplaceAsync(string id, string hash, long revision, ReviewReplacementAudit replacement, CancellationToken ct) => throw new NotSupportedException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class ScopeBackend(ReviewSampleSummary[] rows) : IReviewBackend
    {
        public ReviewSession GetSession() => new("dataset", "manifest", "test", new(300,1,0,299,1,1,false,new Dictionary<string,ReviewSplitProgress>()), []);
        public IReadOnlyList<ReviewSampleSummary> GetSamples() => rows;
        public Task<ReviewSampleDetail> GetSampleAsync(string id, CancellationToken ct) => throw new NotSupportedException();
        public Task<ReviewValidationResult> ValidateAsync(ReviewValidateRequest request, CancellationToken ct) => throw new NotSupportedException();
        public Task<ReviewSaveResponse> SaveAsync(ReviewSaveRequest request, CancellationToken ct) => throw new NotSupportedException();
        public Task<ReviewSaveResponse> ReplaceAsync(ReviewReplaceRequest request, CancellationToken ct) => throw new NotSupportedException();
    }
}

using CsDemoMap.Api.Models;
using CsDemoMap.Api.Services;

namespace CsDemoMap.Api.Tests;

internal static class SituationReviewWorkflowVerifier
{
    internal static async Task VerifyAsync(CancellationToken ct)
    {
        var catalog = new SyntheticReviewCatalog();
        var root = Path.Combine(Path.GetTempPath(), "review-workflow-" + Guid.NewGuid().ToString("N"));
        var initial = catalog.InitialSelected.Select(s => new ReviewActiveSample(s.ReviewOrdinal, s.Entry.SampleId)).ToArray();
        var checks = 0;
        void Check(bool value) { if (!value) throw new InvalidDataException("Review workflow assertion failed."); checks++; }
        async Task Fails(Func<Task> action, string code)
        {
            try { await action(); } catch (ReviewException e) when (e.Code == code) { checks++; return; }
            throw new InvalidDataException("Expected review failure: " + code);
        }
        var id = initial[0].SampleId;
        ReviewSaveRequest Request(string sample, int revision, string decision = "approved") => new(sample, revision,
            Guid.NewGuid().ToString("D"), decision, new(true, true, true, false), [], [],
            decision == "rejected" ? "摘要重点不足" : null, decision == "rejected" ? "narrative-unhelpful" : null, null);
        try
        {
            await using (var store = await SituationReviewWorkStore.OpenAsync(root, catalog.Identity, initial, catalog.GetCandidateAsync, ct))
            {
                SituationReviewReplacementService.VerifyRecoveredState(catalog, store.Snapshot); checks++;
                var repository = SituationArtifactIO.FindRepositoryRoot(AppContext.BaseDirectory);
                await SituationReviewConsumerProvenance.CaptureAsync(repository, root, store,
                    [typeof(SituationReviewDecisionService).Assembly.Location], ct);
                var backend = new SituationReviewDecisionService(catalog, store);
                Check(backend.GetSession().Progress.Unreviewed == 1);
                var detail = await backend.GetSampleAsync(id, ct);
                var narrative = detail.Candidate.Candidate;
                await Fails(() => backend.SaveAsync(Request(id, 0) with { Evaluations = new(null, true, true, false) }, ct), "invalid-request");
                await Fails(() => backend.SaveAsync(Request(id, 0) with { Evaluations = new(false, true, true, false) }, ct), "blocking-issue");
                await Fails(() => backend.SaveAsync(Request(id, 0, "modified") with { EditedNarrative = narrative }, ct), "narrative-invalid");
                var unknown = narrative with { Highlights = narrative.Highlights.Select(h => h with { EvidenceIds = new[] { "unknown-evidence" } }).ToArray() };
                var validation = await backend.ValidateAsync(new(id, unknown), ct);
                Check(!validation.Valid && validation.Errors.Contains("unknown-evidence"));
                await Fails(() => backend.SaveAsync(Request(id, 0, "modified") with { EditedNarrative = unknown }, ct), "narrative-invalid");
                var forbidden = narrative with { SummaryZh = "建议进攻，会赢。" };
                Check(!(await backend.ValidateAsync(new(id, forbidden), ct)).Valid);
                var approved = Request(id, 0);
                var result = await backend.SaveAsync(approved, ct);
                await SituationReviewConsumerProvenance.CaptureAsync(repository, root, store,
                    [typeof(SituationReviewDecisionService).Assembly.Location], ct);
                var provenance = SituationReviewJson.Deserialize<SituationReviewConsumerProvenance.Document>(await File.ReadAllTextAsync(Path.Combine(root, "consumer-provenance.json"), ct));
                Check(provenance.Sessions.Count == 2 && provenance.Sessions[1].Session.StartingWorkspaceRevision == 1);
                Check(result.SampleRevision == 1 && result.Progress.Completed == 1);
                var replay = await backend.SaveAsync(approved, ct);
                Check(replay.DecisionSha256 == result.DecisionSha256 && replay.Progress.Operations == 1);
                await Fails(() => backend.SaveAsync(approved with { Note = "不同内容" }, ct), "request-conflict");
                await Fails(() => backend.SaveAsync(Request(id, 0), ct), "revision-conflict");
                var edited = narrative with { SummaryZh = narrative.SummaryZh + " 当前空间状态需结合证据解读。" };
                Check((await backend.ValidateAsync(new(id, edited), ct)).Valid);
                result = await backend.SaveAsync(Request(id, 1, "modified") with { EditedNarrative = edited }, ct);
                Check(result.SampleRevision == 2 && store.Snapshot.Decisions[id].FinalNarrative is not null);
                await Fails(() => backend.SaveAsync(Request(id, 2, "rejected") with { Note = " " }, ct), "invalid-request");
                await backend.SaveAsync(Request(id, 2, "rejected"), ct);
                Check(backend.GetSession().Progress.Completed == 0 && backend.GetSession().Progress.Rejected == 1);
                await Fails(() => backend.ReplaceAsync(new(id, 2, Guid.NewGuid().ToString("D")), ct), "workspace-conflict");
                var replacementRequest = new ReviewReplaceRequest(id, 3, Guid.NewGuid().ToString("D"));
                var replaced = await backend.ReplaceAsync(replacementRequest, ct);
                Check(replaced.NewSampleId == catalog.Pool[1].SampleId && replaced.Progress.Unreviewed == 1);
                Check((await backend.ReplaceAsync(replacementRequest, ct)).NewSampleId == replaced.NewSampleId);
                await Fails(() => backend.GetSampleAsync(id, ct), "unknown-sample");
                var nextId = replaced.NewSampleId!;
                await backend.SaveAsync(Request(nextId, 0, "rejected"), ct);
                var second = await backend.ReplaceAsync(new(nextId, 5, Guid.NewGuid().ToString("D")), ct);
                Check(second.NewSampleId == catalog.Pool[2].SampleId && store.Snapshot.Replacements.Count == 2);
                // Blocking reports persist without inserting an illegal final narrative.
                await backend.SaveAsync(Request(second.NewSampleId!, 0, "rejected") with
                { RejectReasonCode = "unknown-evidence", IssueCodes = ["unknown-evidence"] }, ct);
                Check(backend.GetSession().Progress.BlockingIssue);
                await Fails(() => backend.SaveAsync(Request(second.NewSampleId!, 1), ct), "blocking-issue");
                await Fails(() => backend.ReplaceAsync(new(second.NewSampleId!, 7, Guid.NewGuid().ToString("D")), ct), "blocking-issue");
            }
            await using (var store = await SituationReviewWorkStore.OpenAsync(root, catalog.Identity, initial, catalog.GetCandidateAsync, ct))
            {
                var backend = new SituationReviewDecisionService(catalog, store);
                Check(backend.GetSession().Progress.BlockingIssue && store.Snapshot.WorkspaceRevision == 7);
                Check(store.Snapshot.Decisions.Count == 3 && backend.GetSamples().Single().SampleId == catalog.Pool[2].SampleId);
                SituationReviewReplacementService.VerifyRecoveredState(catalog, store.Snapshot); checks++;
                var bad = store.Snapshot with { Replacements = store.Snapshot.Replacements.Select(r => r with { PriorityCategory = "invalid" }).ToArray() };
                await Fails(() => { SituationReviewReplacementService.VerifyRecoveredState(catalog, bad); return Task.CompletedTask; }, "artifact-mismatch");
            }
            SituationReviewArtifactLoader.ValidateWorkLocation(Path.Combine(root, "independent"), Path.Combine(root, "source")); checks++;
            await Fails(() => { SituationReviewArtifactLoader.ValidateWorkLocation(root, Path.Combine(root, "source")); return Task.CompletedTask; }, "artifact-mismatch");
            Console.WriteLine($"Situation review workflow: {checks} checks passed.");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    internal sealed class SyntheticReviewCatalog : IReviewCatalog
    {
        private readonly Dictionary<string, SituationReviewCandidateV1> candidates = new(StringComparer.Ordinal);
        public ReviewWorkspaceIdentity Identity { get; }
        public IReadOnlyList<ReviewSelectedEntry> InitialSelected { get; }
        public IReadOnlyList<ReviewPoolEntry> Pool { get; }
        public IReadOnlyDictionary<string, IReadOnlyList<string>> Backups { get; }
        public ReviewSelectionPolicy Policy { get; }
        internal SyntheticReviewCatalog()
        {
            string Hash(string value) => SituationArtifactIO.Sha256(value);
            var record = SituationTrainingContractVerifier.BuildRecord();
            var frozen = SituationReviewSelectionPolicy.LoadFrozen();
            Policy = frozen with { Splits = new Dictionary<string, ReviewSplitPolicy>
            {
                ["train"] = new(1, 1, 1, 1, new Dictionary<string, int>()),
                ["dev"] = new(0, 0, 0, 0, new Dictionary<string, int>()),
                ["test"] = new(0, 0, 0, 0, new Dictionary<string, int>())
            } };
            var entries = new List<ReviewPoolEntry>();
            for (var i = 0; i < 3; i++)
            {
                var row = record with { SampleId = "sample-" + Hash("workflow-sample-" + i) };
                var entry = SituationReviewSelectionPolicy.Describe(row, i + 1, Policy) with
                { SourceSceneSha256 = Hash("scene-" + i), ModelInputSha256 = Hash("input-" + i) };
                entries.Add(entry);
                candidates.Add(row.SampleId, SituationReviewCandidateReader.Candidate(new(1, entry), row));
            }
            Pool = entries;
            InitialSelected = [new(1, entries[0])];
            Backups = Policy.CategoryPriority.Append("any").ToDictionary(c => "train/" + c,
                _ => (IReadOnlyList<string>)entries.Skip(1).Select(e => e.SampleId).ToArray(), StringComparer.Ordinal);
            Identity = new("situation-review-workspace-v1", Hash("dataset"), Hash("manifest"), Hash("candidate-file"),
                Hash("split"), SituationReviewSelectionPolicy.Hash(Policy), [], "synthetic-test-only");
        }
        public Task<SituationReviewCandidateV1> GetCandidateAsync(string sampleId, int reviewOrdinal, CancellationToken cancellationToken) =>
            Task.FromResult(candidates[sampleId] with { ReviewOrdinal = reviewOrdinal });
    }
}

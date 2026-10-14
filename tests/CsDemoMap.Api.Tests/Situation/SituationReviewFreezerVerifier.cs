using CsDemoMap.Api.Models;
using CsDemoMap.Api.Services;

namespace CsDemoMap.Api.Tests;

internal static class SituationReviewFreezerVerifier
{
    internal static async Task VerifyAsync(CancellationToken ct)
    {
        var root = Path.Combine(Path.GetTempPath(), "review-freezer-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var catalog = new Catalog();
        var work = Path.Combine(root, "work");
        var checks = 0;
        void Check(bool value) { if (!value) throw new InvalidDataException("Freezer assertion failed."); checks++; }
        async Task Fails(Func<Task> action, string? code = null)
        {
            try { await action(); }
            catch (ReviewException e) when (code is null || e.Code == code) { checks++; return; }
            catch (InvalidDataException) when (code is null) { checks++; return; }
            catch (IOException) when (code is null) { checks++; return; }
            throw new InvalidDataException("Expected freezer rejection: " + code);
        }
        Task<ReviewFreezeManifest> Export(string name, string? approval = null, Action<string>? fault = null) =>
            SituationReviewFreezer.ExportAsync(catalog, work, Path.Combine(root, name), catalog.Identity.DatasetSha256,
                new Dictionary<string, string>(), approval, ct, fault);
        Task<ReviewFreezeManifest> Verify(string name) => SituationReviewFreezer.VerifyAsync(Path.Combine(root, name), catalog, catalog.Identity.DatasetSha256, ct);
        ReviewSaveRequest Request(string id, int revision = 0) => new(id, revision, Guid.NewGuid().ToString("D"),
            "approved", new(true, true, true, false), [], [], null, null, null);
        try
        {
            await using (var store = await Open(work))
            {
                IReviewBackend backend = new SituationReviewDecisionService(catalog, store);
                backend = await SituationReviewScope.OpenAsync(backend, store, work, 50, ct);
                await Fails(() => SituationReviewFreezer.BuildAsync(catalog, store, work, ct), "review-incomplete");
                await Fails(() => Export("locked"), "workspace-locked");
                var rows = backend.GetSamples();
                var replaced = rows.First(r => r.Split == "train").SampleId;
                await backend.SaveAsync(Request(replaced) with { Decision = "rejected", Note = "摘要重点不足", RejectReasonCode = "narrative-unhelpful" }, ct);
                await Fails(() => SituationReviewFreezer.BuildAsync(catalog, store, work, ct), "review-incomplete");
                await backend.ReplaceAsync(new(replaced, store.Snapshot.WorkspaceRevision, Guid.NewGuid().ToString("D")), ct);
                rows = backend.GetSamples();
                foreach (var row in rows)
                {
                    var request = Request(row.SampleId);
                    if (row == rows[1])
                    {
                        var detail = await backend.GetSampleAsync(row.SampleId, ct);
                        request = request with { Decision = "modified", EditedNarrative = detail.Candidate.Candidate with
                            { SummaryZh = detail.Candidate.Candidate.SummaryZh + " 当前空间状态需结合证据解读。" } };
                    }
                    await backend.SaveAsync(request, ct);
                }
                await backend.SaveAsync(Request(rows[0].SampleId, 1), ct);
                var payload = await SituationReviewFreezer.BuildAsync(catalog, store, work, ct);
                Check(payload.Target == 50 && payload.Counts["train"] == 36 && payload.Counts["dev"] == 7 && payload.Counts["test"] == 7);
                Check(payload.Documents["review-results.jsonl"].Split('\n', StringSplitOptions.RemoveEmptyEntries).Length == 52);
            }
            var first = await Export("pending");
            Check(first is { Status: "complete", ReviewStatus: "awaiting-approval", Target: 50 });
            Check((await Verify("pending")).ContentSha256 == first.ContentSha256);
            var repeat = await Export("repeat");
            Check(repeat.ContentSha256 == first.ContentSha256 && repeat.ReviewVersion == first.ReviewVersion);
            await Fails(() => Export("pending"));
            await Fails(() => Export("bad-approval", new string('0', 64)), "approval-content-mismatch");
            await Fails(() => Verify("bad-approval"), "review-artifact-incomplete");
            var rule = SituationAnalysisRuleLoader.FrozenAnalysisRuleVersion;
            Task<IReadOnlyList<ReviewLabelExample>> Load(string name, SituationTrainingSplit split, string purpose, string? version = null) =>
                SituationReviewedLabelLoader.LoadAsync(Path.Combine(root, name), catalog, catalog.Identity.DatasetSha256, split, purpose, version ?? rule, ct);
            await Fails(() => Load("pending", SituationTrainingSplit.Train, "sft"), "review-not-approved");
            var frozen = await Export("frozen", first.ContentSha256);
            Check(frozen.ReviewStatus == "frozen" && frozen.ContentSha256 == first.ContentSha256);
            var train = await Load("frozen", SituationTrainingSplit.Train, "sft");
            Check(train.Count == 36 && train.All(r => r.Label.RecommendedSftRepeat == 5));
            Check((await Load("frozen", SituationTrainingSplit.Dev, "prompt-selection")).Count == 7);
            Check((await Load("frozen", SituationTrainingSplit.Test, "final-evaluation")).Count == 7);
            await Fails(() => Load("frozen", SituationTrainingSplit.Dev, "sft"), "label-use-forbidden");
            await Fails(() => Load("frozen", SituationTrainingSplit.Test, "sft"), "label-use-forbidden");
            await Fails(() => Load("frozen", SituationTrainingSplit.Test, "prompt-selection"), "label-use-forbidden");
            await Fails(() => Load("frozen", SituationTrainingSplit.Test, "checkpoint-selection"), "label-use-forbidden");
            await Fails(() => Load("frozen", SituationTrainingSplit.Train, "sft", "wrong-rules"), "label-input-mismatch");
            foreach (var point in new[] { "manifest", "files", "readback", "commit" })
            {
                await Fails(() => Export("fault-" + point, fault: step => { if (step == point) throw new IOException("injected"); }));
                await Fails(() => Verify("fault-" + point), "review-artifact-incomplete");
            }
            var labelPath = Path.Combine(root, "repeat", "test-labels.jsonl");
            var original = await File.ReadAllTextAsync(labelPath, ct);
            await File.WriteAllTextAsync(labelPath, original.Replace("final-evaluation", "sft", StringComparison.Ordinal), ct);
            await Fails(() => Verify("repeat"), "artifact-mismatch");
            // Rehashing a tampered label inventory still cannot bypass journal/label reconstruction.
            var changedFiles = await SituationReviewFreezer.InventoryAsync(Path.Combine(root, "repeat"), ct);
            var tampered = repeat with { Files = changedFiles, ContentSha256 = SituationReviewFreezer.ContentHash(repeat, changedFiles) };
            await File.WriteAllTextAsync(Path.Combine(root, "repeat", "manifest.json"), SituationCanonicalJson.Serialize(tampered), ct);
            await Fails(() => Verify("repeat"), "artifact-mismatch");
            var manifestText = await File.ReadAllTextAsync(Path.Combine(root, "pending", "manifest.json"), ct);
            await File.WriteAllTextAsync(Path.Combine(root, "pending", "extra.json"), "{}", ct);
            await Fails(() => Verify("pending"), "artifact-mismatch");
            // Full 300-row path is independently exercised, without a scope file.
            work = Path.Combine(root, "full-work");
            await using (var store = await Open(work))
            {
                var backend = new SituationReviewDecisionService(catalog, store);
                foreach (var row in backend.GetSamples()) await backend.SaveAsync(Request(row.SampleId), ct);
            }
            var full = await Export("full");
            Check(full.Target == 300 && full.Counts["train"] == 220 && full.Counts["dev"] == 40 && full.Counts["test"] == 40);
            await using (var store = await Open(work))
            {
                var backend = new SituationReviewDecisionService(catalog, store);
                var row = backend.GetSamples()[0];
                await backend.SaveAsync(Request(row.SampleId, 1) with { Evaluations = new(true, false, true, false) }, ct);
                await Fails(() => SituationReviewFreezer.BuildAsync(catalog, store, work, ct), "review-evaluation-failed");
                await backend.SaveAsync(Request(row.SampleId, 2) with { Decision = "rejected", Evaluations = new(false, true, true, false),
                    Note = "事实存在问题", RejectReasonCode = "facts-incorrect" }, ct);
            }
            await Fails(() => Export("blocked"), "blocking-issue");
            Check((await Verify("full")).Target == 300); // frozen snapshot survives later workspace edits
            Console.WriteLine($"Review freezer checks passed: {checks} (50/300, history/replacement, approval, isolation, tamper, failures).");
        }
        finally { Directory.Delete(root, true); }

        Task<IReviewWorkStore> Open(string path) => SituationReviewWorkStore.OpenAsync(path, catalog.Identity,
            catalog.InitialSelected.Select(s => new ReviewActiveSample(s.ReviewOrdinal, s.Entry.SampleId)).ToArray(), catalog.GetCandidateAsync, ct);
    }

    private sealed class Catalog : IReviewCatalog
    {
        public ReviewWorkspaceIdentity Identity { get; }
        public IReadOnlyList<ReviewSelectedEntry> InitialSelected { get; }
        public IReadOnlyList<ReviewPoolEntry> Pool { get; }
        public IReadOnlyDictionary<string, IReadOnlyList<string>> Backups { get; }
        public ReviewSelectionPolicy Policy { get; }
        private readonly Dictionary<string, SituationReviewCandidateV1> candidates = new(StringComparer.Ordinal);
        internal Catalog()
        {
            string Hash(string value) => SituationArtifactIO.Sha256(value);
            var record = SituationTrainingContractVerifier.BuildRecord();
            Policy = SituationReviewSelectionPolicy.LoadFrozen() with { Splits = new Dictionary<string, ReviewSplitPolicy>
            {
                ["train"] = new(220, 1, 1, 220, new Dictionary<string, int>()),
                ["dev"] = new(40, 1, 1, 40, new Dictionary<string, int>()), ["test"] = new(40, 1, 1, 40, new Dictionary<string, int>())
            } };
            var pool = new List<ReviewPoolEntry>();
            for (var i = 1; i <= 302; i++)
            {
                var split = i <= 220 || i > 300 ? SituationTrainingSplit.Train : i <= 260 ? SituationTrainingSplit.Dev : SituationTrainingSplit.Test;
                var row = record with { SampleId = "sample-" + Hash("freeze-" + i), Metadata = record.Metadata with
                    { Split = split, MatchRef = "match-" + Hash(split.ToString()), RoundRef = "round-" + Hash("round-" + i) } };
                var entry = SituationReviewSelectionPolicy.Describe(row, i, Policy) with
                    { SourceSceneSha256 = Hash("scene-" + i), ModelInputSha256 = Hash("input-" + i) };
                pool.Add(entry);
                candidates.Add(row.SampleId, SituationReviewCandidateReader.Candidate(new(Math.Min(i, 300), entry), row));
            }
            Pool = pool;
            InitialSelected = pool.Take(300).Select((e, i) => new ReviewSelectedEntry(i + 1, e)).ToArray();
            Backups = Policy.CategoryPriority.Append("any").ToDictionary(c => "train/" + c,
                _ => (IReadOnlyList<string>)pool.Skip(300).Select(e => e.SampleId).ToArray(), StringComparer.Ordinal);
            Identity = new("situation-review-workspace-v1", Hash("dataset"), Hash("manifest"), Hash("candidates"), Hash("split"),
                SituationReviewSelectionPolicy.Hash(Policy), [], "situation-narrative-review-synthetic-draft");
        }
        public Task<SituationReviewCandidateV1> GetCandidateAsync(string id, int ordinal, CancellationToken ct) =>
            Task.FromResult(candidates[id] with { ReviewOrdinal = ordinal });
    }
}

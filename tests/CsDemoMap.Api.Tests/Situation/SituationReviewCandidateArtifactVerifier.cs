using System.Text;
using CsDemoMap.Api.Models;
using CsDemoMap.Api.Services;

namespace CsDemoMap.Api.Tests;

internal static class SituationReviewCandidateArtifactVerifier
{
    internal static async Task VerifyAsync(CancellationToken cancellationToken)
    {
        var root = Path.Combine(Path.GetTempPath(), "review-artifacts-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var checks = 0;
        try
        {
            var frozen = SituationReviewSelectionPolicy.LoadFrozen();
            var policy = frozen with { Splits = new Dictionary<string, ReviewSplitPolicy>
            {
                ["train"] = new(1, 1, 1, 1, new Dictionary<string, int>()),
                ["dev"] = new(0, 0, 0, 0, new Dictionary<string, int>()),
                ["test"] = new(0, 0, 0, 0, new Dictionary<string, int>())
            } };
            var record = SituationTrainingContractVerifier.BuildRecord();
            var entry = SituationReviewSelectionPolicy.Describe(record, 1, policy);
            ReviewPoolEntry[] pool = [entry];
            var backups = new Dictionary<string, IReadOnlyList<string>>();
            foreach (var split in SituationReviewSelectionPolicy.SplitOrder)
            foreach (var category in policy.CategoryPriority.Append("any")) backups[split + "/" + category] = [];
            var plan = new ReviewSelectionPlan([new(1, entry)], backups, [], []);
            SituationReviewCandidateStatistics.ValidatePlan(pool, plan, policy, true);
            checks++;
            Reject(() => SituationReviewCandidateStatistics.ValidatePlan(pool, plan with { Selected = [new(2, entry)] }, policy, true), ref checks);
            Reject(() => SituationReviewCandidateStatistics.ValidatePlan(pool, plan with { Selected = [new(1, entry with { RecordSha256 = new('a', 64) })] }, policy, true), ref checks);
            Reject(() => SituationReviewCandidateStatistics.ValidatePlan(pool, plan with { Selected = [new(1, entry), new(2, entry)] }, policy, true), ref checks);
            Reject(() => SituationReviewCandidateStatistics.ValidatePlan(pool, plan with { Selected = [] }, policy, true), ref checks);
            var wrongBackups = new Dictionary<string, IReadOnlyList<string>>(backups) { ["train/any"] = [entry.SampleId] };
            Reject(() => SituationReviewCandidateStatistics.ValidatePlan(pool, plan with { Backups = wrongBackups }, policy, true), ref checks);
            var wrongQuota = policy with { Splits = new Dictionary<string, ReviewSplitPolicy>(policy.Splits) { ["train"] = policy.Splits["train"] with { Quotas = new Dictionary<string, int> { ["post-plant"] = 1 } } } };
            Reject(() => SituationReviewCandidateStatistics.ValidatePlan(pool, plan, wrongQuota, true), ref checks);
            Reject(() => SituationArtifactIO.ResolveRepositoryPath(root, "../escape", "test"), ref checks);
            var eventPool = Enumerable.Range(0, 3).Select(i => entry with
            {
                SampleId = "sample-" + SituationArtifactIO.Sha256("event" + i), Tick = i,
                SourceSceneSha256 = SituationArtifactIO.Sha256("scene" + i), ModelInputSha256 = SituationArtifactIO.Sha256("input" + i),
                EventCategories = [policy.EventCategories[i], policy.EventCategories[3]]
            }).ToArray();
            var eventPolicy = policy with { Splits = new Dictionary<string, ReviewSplitPolicy>(policy.Splits) { ["train"] = new(3, 1, 3, 3, new Dictionary<string, int>()) } };
            var eventPlan = new ReviewSelectionPlan(eventPool.Select((e, i) => new ReviewSelectedEntry(i + 1, e)).ToArray(), backups, [],
                [new("train", entry.MatchRef, entry.RoundRef, eventPool.Select(e => e.SampleId).ToArray(), policy.EventCategories.Take(3).ToArray())]);
            SituationReviewCandidateStatistics.ValidatePlan(eventPool, eventPlan, eventPolicy, true); checks++;
            Reject(() => SituationReviewCandidateStatistics.ValidatePlan(eventPool, eventPlan with { RoundExceptions = [] }, eventPolicy, true), ref checks);
            var impossible = eventPlan with { RoundExceptions = [eventPlan.RoundExceptions[0] with { DistinctEventCategories = policy.EventCategories.Skip(3).Take(3).ToArray() }] };
            Reject(() => SituationReviewCandidateStatistics.ValidatePlan(eventPool, impossible, eventPolicy, true), ref checks);

            var candidate = SituationReviewCandidateReader.Candidate(plan.Selected[0], record);
            SituationTrainingContractJson.Validate(candidate);
            Reject(() => SituationTrainingContractJson.Validate(candidate with { CandidateSha256 = new('a', 64) }), ref checks);
            var jsonl = Path.Combine(root, "lines.jsonl");
            foreach (var text in new[] { "{}\r\n", "\uFEFF{}\n", "{}", "\n" })
            {
                await File.WriteAllTextAsync(jsonl, text, new UTF8Encoding(false), cancellationToken);
                await RejectAsync(async () => { await foreach (var _ in SituationReviewCandidateReader.ReadLinesAsync(jsonl, cancellationToken)) { } });
                checks++;
            }
            await File.WriteAllBytesAsync(jsonl, [0xff, 0x0a], cancellationToken);
            await RejectAsync(async () => { await foreach (var _ in SituationReviewCandidateReader.ReadLinesAsync(jsonl, cancellationToken)) { } });
            checks++;

            var provenance = new SituationReviewCandidateProvenanceV1("situation-review-candidate-provenance-v1", SituationReviewSelectionPolicy.ApprovedDatasetSha256,
                SituationReviewCandidateReader.BaseProvenanceSha256, SituationReviewCandidateReader.BaseSplitSha256, SituationReviewSelectionPolicy.Hash(policy),
                SituationReviewCandidateProvenance.HistoricalValidation, new('0', 40), true, policy.SchemaVersion, [], []);
            Task<IReadOnlyDictionary<string, SituationTrainingRecordV1>> Records(CancellationToken _) => Task.FromResult<IReadOnlyDictionary<string, SituationTrainingRecordV1>>(new Dictionary<string, SituationTrainingRecordV1> { [record.SampleId] = record });
            async Task<SituationReviewCandidateValidationResult> Verify(SituationReviewCandidateManifestV1 complete, CancellationToken token)
            {
                var incomplete = await SituationReviewCandidateReader.ReadJsonAsync<SituationReviewCandidateManifestV1>(Path.Combine(root, "success", "manifest.json"), token);
                if (incomplete.Status != "incomplete") throw new Exception("Complete was published before verification.");
                SituationReviewCandidateReader.RequireFiles(Path.Combine(root, "success"), ["manifest.json", "provenance.json", "review-candidates.jsonl", "review-stats.json"]);
                return new(complete, SituationReviewCandidateStatistics.Create(pool, plan, policy));
            }
            var success = Path.Combine(root, "success");
            var locked = Path.Combine(root, "locked");
            await using (var owner = new FileStream(locked + ".review-publish.lock", FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose))
            {
                await RejectAsync(() => SituationReviewCandidateExporter.PublishAsync(locked, pool, plan, policy, provenance, Records, Verify, cancellationToken));
                if (Directory.Exists(locked)) throw new Exception("Competing publisher created output.");
                checks++;
            }
            await SituationReviewCandidateExporter.PublishAsync(success, pool, plan, policy, provenance, Records, Verify, cancellationToken);
            var completed = await SituationReviewCandidateReader.ReadJsonAsync<SituationReviewCandidateManifestV1>(Path.Combine(success, "manifest.json"), cancellationToken);
            if (completed.Status != "complete" || completed.Files.Count != 3) throw new Exception("Publication failed.");
            checks++;
            await RejectAsync(() => SituationReviewCandidateExporter.PublishAsync(success, pool, plan, policy, provenance, Records, Verify, cancellationToken)); checks++;
            await File.WriteAllTextAsync(Path.Combine(success, "extra"), "x", cancellationToken);
            Reject(() => SituationReviewCandidateReader.RequireFiles(success, ["manifest.json", "provenance.json", "review-candidates.jsonl", "review-stats.json"]), ref checks);
            await RejectAsync(() => SituationReviewCandidateReader.RequireHashAsync(Path.Combine(success, "review-stats.json"), new('0', 64), cancellationToken)); checks++;

            foreach (var failure in new[] { "cancel", "disk", "readback" })
            {
                var output = Path.Combine(root, failure);
                using var cancel = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                Task<IReadOnlyDictionary<string, SituationTrainingRecordV1>> FailMaterialize(CancellationToken token)
                {
                    if (failure == "cancel") { cancel.Cancel(); token.ThrowIfCancellationRequested(); }
                    if (failure == "disk") Directory.CreateDirectory(Path.Combine(output, "review-candidates.jsonl"));
                    return Records(token);
                }
                await RejectAsync(() => SituationReviewCandidateExporter.PublishAsync(output, pool, plan, policy, provenance, FailMaterialize,
                    (_, _) => throw new InvalidDataException("Injected readback failure."), cancel.Token));
                var failed = await SituationReviewCandidateReader.ReadJsonAsync<SituationReviewCandidateManifestV1>(Path.Combine(output, "manifest.json"), cancellationToken);
                if (failed.Status != "incomplete") throw new Exception("Failure published complete.");
                checks++;
            }
            var wrongBase = Path.Combine(root, "wrong-base");
            Directory.CreateDirectory(wrongBase);
            await File.WriteAllTextAsync(Path.Combine(wrongBase, "manifest.json"), "{}", cancellationToken);
            await RejectAsync(() => SituationReviewCandidateReader.OpenApprovedBaseAsync(wrongBase, cancellationToken)); checks++;
            Console.WriteLine($"Situation review artifact checks passed: {checks}.");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static void Reject(Action action, ref int checks)
    {
        try { action(); }
        catch (Exception e) when (e is InvalidDataException or IOException or ArgumentException) { checks++; return; }
        throw new Exception("Expected artifact rejection.");
    }
    private static async Task RejectAsync(Func<Task> action)
    {
        try { await action(); }
        catch (Exception e) when (e is InvalidDataException or IOException or ArgumentException or OperationCanceledException or UnauthorizedAccessException) { return; }
        throw new Exception("Expected artifact rejection.");
    }
}

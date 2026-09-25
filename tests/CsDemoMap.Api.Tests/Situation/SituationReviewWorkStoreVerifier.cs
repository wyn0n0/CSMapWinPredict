using CsDemoMap.Api.Models;
using CsDemoMap.Api.Services;

namespace CsDemoMap.Api.Tests;

internal static class SituationReviewWorkStoreVerifier
{
    public static async Task VerifyAsync(CancellationToken cancellationToken)
    {
        var root = Path.Combine(Path.GetTempPath(), "situation-review-store-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var checks = 0;
        var record = SituationTrainingContractVerifier.BuildRecord();
        var candidate = new SituationReviewCandidateV1(SituationTrainingContractVersions.ReviewCandidate,
            1, record.SampleId, record.Metadata.Split, record.Metadata.MatchRef, record.Metadata.RoundRef,
            record.Metadata.SelectionTags, record.Input, record.Output,
            SituationArtifactIO.Sha256(SituationTrainingContractJson.SerializeLine(record)), SituationCanonicalJson.Sha256(record.Output));
        var backup = candidate with { SampleId = "sample-" + Hash("backup") };
        var identity = new ReviewWorkspaceIdentity("review-workspace-v1", Hash("dataset"), Hash("manifest"),
            Hash("candidates"), Hash("split"), Hash("policy"), [], "synthetic-test-only");
        ReviewActiveSample[] initial = [new(1, candidate.SampleId)];
        Task<SituationReviewCandidateV1> Resolve(string id, int ordinal, CancellationToken ct) =>
            Task.FromResult((id == candidate.SampleId ? candidate : id == backup.SampleId ? backup :
                throw new ReviewException("unknown-sample", 404)) with { ReviewOrdinal = ordinal });
        Task<IReviewWorkStore> Open(string path, Action<ReviewStorageFaultPoint>? fault = null) =>
            SituationReviewWorkStore.OpenAsync(path, identity, initial, Resolve, cancellationToken, fault);
        SituationReviewDecisionV1 Decision(int revision, bool reject = false, bool blocked = false) =>
            SituationTrainingContractJson.WithDecisionHash(new(SituationTrainingContractVersions.ReviewDecision,
                identity.DatasetSha256, candidate.CandidateSha256, candidate.SampleId, revision,
                reject ? SituationReviewDecisionKind.Rejected : SituationReviewDecisionKind.Approved,
                new(!blocked, true, true, false), [], blocked ? ["facts-incorrect"] : [],
                reject ? "Synthetic rejection for storage verification." : null, null,
                reject ? null : candidate.CandidateSha256, reject ? "summary-unusable" : null,
                blocked, DateTimeOffset.UtcNow, ""));
        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidDataException("Review storage check failed: " + message);
            checks++;
        }
        async Task Fails(Func<Task> action, string message)
        {
            try { await action(); }
            catch (Exception e) when (e is ReviewException or IOException or InvalidDataException or System.Text.Json.JsonException)
            { checks++; return; }
            throw new InvalidDataException("Review storage expected failure: " + message);
        }
        try
        {
            var path = Path.Combine(root, "normal");
            var request = Guid.NewGuid().ToString();
            var approved = Decision(1);
            await using (var store = await Open(path))
            {
                Check(store.Snapshot.WorkspaceRevision == 0, "new workspace revision");
                await Fails(async () => { await using var other = await Open(path); }, "exclusive process lock");
                var receipt = await store.SaveDecisionAsync(request, Hash("approve"), 0, approved, cancellationToken);
                Check(receipt.WorkspaceRevision == 1 && receipt.SampleRevision == 1, "first durable save");
                Check(await store.SaveDecisionAsync(request, Hash("approve"), 0, approved, cancellationToken) == receipt,
                    "idempotent retry");
                await Fails(() => store.SaveDecisionAsync(request, Hash("different"), 0, approved, cancellationToken), "request hash conflict");
                await Fails(() => store.SaveDecisionAsync(Guid.NewGuid().ToString(), Hash("stale"), 0, approved, cancellationToken), "stale revision");
                await Fails(() => store.SaveDecisionAsync("../escape", Hash("id"), 1, Decision(2), cancellationToken), "request path rejected");
                var snapshot = store.Snapshot;
                ((IDictionary<string, SituationReviewDecisionV1>)snapshot.Decisions).Clear();
                Check(store.Snapshot.Decisions.Count == 1, "snapshot detached from store");
                var attempts = await Task.WhenAll(Enumerable.Range(0, 2).Select(async n =>
                {
                    try { await store.SaveDecisionAsync(Guid.NewGuid().ToString(), Hash("concurrent" + n), 1, Decision(2, true), cancellationToken); return true; }
                    catch (ReviewException e) when (e.StatusCode == 409) { return false; }
                }));
                Check(attempts.Count(x => x) == 1, "concurrent stale revision cannot overwrite");
                var rejected = store.Snapshot.Decisions[candidate.SampleId];
                var replacement = new ReviewReplacementAudit(candidate.SampleId, backup.SampleId, 1,
                    rejected.DecisionSha256, "synthetic-category", 3, backup.CandidateSha256, DateTimeOffset.UtcNow);
                await store.ReplaceAsync(Guid.NewGuid().ToString(), Hash("replace"), 2, replacement, cancellationToken);
                Check(store.Snapshot.Active.Single().SampleId == backup.SampleId && store.Snapshot.Decisions.Count == 1,
                    "replacement preserves rejected decision and new sample is unreviewed");
                await Fails(() => store.SaveDecisionAsync(Guid.NewGuid().ToString(), Hash("inactive"), 2, Decision(3), cancellationToken), "inactive rejected cannot reenter");
            }
            await File.WriteAllTextAsync(Path.Combine(path, "index.json"), "corrupt cache", cancellationToken);
            await File.WriteAllTextAsync(Path.Combine(path, "decisions", "orphan.tmp"), "partial", cancellationToken);
            await using (var store = await Open(path))
            {
                Check(store.Snapshot.WorkspaceRevision == 3 && store.Snapshot.Replacements.Count == 1, "restart reconstructs replacement chain");
                Check(store.FindReceipt(request, Hash("approve"))?.SampleRevision == 1, "restart restores original receipt");
                Check(store.Snapshot.Decisions[candidate.SampleId].SampleRevision == 2, "revision history preserved");
            }
            await Fails(async () => { await using var mismatch = await SituationReviewWorkStore.OpenAsync(path,
                identity with { CandidateFileSha256 = Hash("other") }, initial, Resolve, cancellationToken); }, "identity mismatch");
            await File.WriteAllTextAsync(Path.Combine(path, "decisions", candidate.SampleId + ".json"), "{}", cancellationToken);
            await Fails(async () => { await using var corrupt = await Open(path); }, "committed corruption cannot be healed from index or history");

            var modifiedPath = Path.Combine(root, "modified");
            await using (var store = await Open(modifiedPath))
            {
                var narrative = candidate.Candidate with { SummaryZh = candidate.Candidate.SummaryZh + "。" };
                var modified = SituationTrainingContractJson.WithDecisionHash(Decision(1) with
                {
                    Decision = SituationReviewDecisionKind.Modified, FinalNarrative = narrative,
                    FinalNarrativeSha256 = SituationCanonicalJson.Sha256(narrative)
                });
                await store.SaveDecisionAsync(Guid.NewGuid().ToString(), Hash("modified"), 0, modified, cancellationToken);
                Check(store.Snapshot.Decisions[candidate.SampleId].FinalNarrativeSha256 != candidate.CandidateSha256,
                    "modified narrative separately hash bound");
            }
            await using (var store = await Open(modifiedPath))
                Check(store.Snapshot.Decisions[candidate.SampleId].Decision == SituationReviewDecisionKind.Modified, "modified narrative validates on restart");
            await File.WriteAllTextAsync(Path.Combine(modifiedPath, "transactions", "00000000000000000001.commit"),
                "{\"revision\":1,\"transactionSha256\":\"" + Hash("wrong") + "\"}", cancellationToken);
            await Fails(async () => { await using var store = await Open(modifiedPath); }, "committed transaction hash mismatch");

            foreach (var point in Enum.GetValues<ReviewStorageFaultPoint>())
            {
                var failurePath = Path.Combine(root, "fault-" + point);
                var fired = false;
                var armed = false;
                var id = Guid.NewGuid().ToString();
                var decision = Decision(1);
                await using (var store = await Open(failurePath, p =>
                {
                    if (armed && !fired && p == point) { fired = true; throw new IOException("simulated disk interruption"); }
                }))
                {
                    armed = true;
                    if (point == ReviewStorageFaultPoint.Index)
                        await store.SaveDecisionAsync(id, Hash("fault"), 0, decision, cancellationToken);
                    else await Fails(() => store.SaveDecisionAsync(id, Hash("fault"), 0, decision, cancellationToken), "fault " + point);
                    Check(fired, "injection reached " + point);
                }
                await using var recovered = await Open(failurePath);
                var committed = point is not (ReviewStorageFaultPoint.Write or ReviewStorageFaultPoint.Rename);
                Check(recovered.Snapshot.WorkspaceRevision == (committed ? 1 : 0), "recovery boundary " + point);
                var saved = await recovered.SaveDecisionAsync(id, Hash("fault"), 0, decision, cancellationToken);
                Check(saved.WorkspaceRevision == 1 && recovered.Snapshot.Decisions.Count == 1, "retry exactly once " + point);
            }
            foreach (var point in new[] { ReviewStorageFaultPoint.Intent, ReviewStorageFaultPoint.Readback, ReviewStorageFaultPoint.Commit })
            {
                var replacementPath = Path.Combine(root, "replace-fault-" + point);
                var armed = false;
                var id = Guid.NewGuid().ToString();
                await using (var store = await Open(replacementPath, p => { if (armed && p == point) throw new IOException("replacement interruption"); }))
                {
                    var rejected = Decision(1, true);
                    await store.SaveDecisionAsync(Guid.NewGuid().ToString(), Hash("reject"), 0, rejected, cancellationToken);
                    armed = true;
                    await Fails(() => store.ReplaceAsync(id, Hash("replacement-fault"), 1,
                        new(candidate.SampleId, backup.SampleId, 1, rejected.DecisionSha256, "synthetic-category", 2,
                            backup.CandidateSha256, DateTimeOffset.UtcNow), cancellationToken), "replacement fault " + point);
                }
                await using var recovered = await Open(replacementPath);
                Check(recovered.Snapshot.WorkspaceRevision == 2 && recovered.Snapshot.Active.Single().SampleId == backup.SampleId &&
                    recovered.Snapshot.Replacements.Count == 1 && recovered.FindReceipt(id, Hash("replacement-fault")) is not null,
                    "atomic replacement recovery " + point);
            }
            foreach (var point in new[] { ReviewStorageFaultPoint.Write, ReviewStorageFaultPoint.Rename })
            foreach (var occurrence in new[] { 2, 3 })
            {
                var changePath = Path.Combine(root, $"change-{point}-{occurrence}");
                var armed = false;
                var seen = 0;
                var id = Guid.NewGuid().ToString();
                await using (var store = await Open(changePath, p =>
                {
                    if (armed && p == point && ++seen == occurrence) throw new IOException("simulated disk full or atomic rename failure");
                }))
                {
                    var first = await store.SaveDecisionAsync(Guid.NewGuid().ToString(), Hash("confirmed"), 0, Decision(1), cancellationToken);
                    armed = true;
                    await Fails(() => store.SaveDecisionAsync(id, Hash("change"), 1, Decision(2, true), cancellationToken), "change failure " + point + occurrence);
                    Check(first.SampleRevision == 1, "confirmed revision before interrupted change");
                }
                await using var recovered = await Open(changePath);
                Check(recovered.Snapshot.Decisions[candidate.SampleId].SampleRevision == 2 &&
                    recovered.FindReceipt(id, Hash("change"))?.WorkspaceRevision == 2,
                    "change replay after disk/rename failure " + point + occurrence);
            }
            foreach (var point in new[] { ReviewStorageFaultPoint.Intent, ReviewStorageFaultPoint.Decision, ReviewStorageFaultPoint.Commit })
            {
                var sameProcessPath = Path.Combine(root, "same-process-" + point);
                var once = false;
                await using var store = await Open(sameProcessPath, p =>
                {
                    if (!once && p == point) { once = true; throw new IOException("lost response"); }
                });
                var id = Guid.NewGuid().ToString();
                var decision = Decision(1);
                await Fails(() => store.SaveDecisionAsync(id, Hash("same-process"), 0, decision, cancellationToken), "same-process interrupted save " + point);
                var receipt = await store.SaveDecisionAsync(id, Hash("same-process"), 0, decision, cancellationToken);
                Check(receipt.WorkspaceRevision == 1 && store.Snapshot.WorkspaceRevision == 1 &&
                    store.FindReceipt(id, Hash("same-process")) == receipt, "same-process retry recovers before revision check " + point);
            }
            var ambiguousPath = Path.Combine(root, "ambiguous");
            await using (var store = await Open(ambiguousPath, p =>
            {
                if (p == ReviewStorageFaultPoint.Intent) throw new IOException("intent only");
            }))
                await Fails(() => store.SaveDecisionAsync(Guid.NewGuid().ToString(), Hash("ambiguous"), 0, Decision(1), cancellationToken), "pending intent");
            await File.WriteAllTextAsync(Path.Combine(ambiguousPath, "decisions", candidate.SampleId + ".json"), "{}", cancellationToken);
            await Fails(async () => { await using var store = await Open(ambiguousPath); }, "pending intent cannot overwrite unexplained corruption");
            var blockedPath = Path.Combine(root, "blocked");
            await using (var store = await Open(blockedPath))
            {
                var blocked = Decision(1, true, true);
                await store.SaveDecisionAsync(Guid.NewGuid().ToString(), Hash("blocked"), 0, blocked, cancellationToken);
                await Fails(() => store.SaveDecisionAsync(Guid.NewGuid().ToString(), Hash("unblock"), 1, Decision(2), cancellationToken), "sticky blocking approval rejected");
                await Fails(() => store.ReplaceAsync(Guid.NewGuid().ToString(), Hash("blocked-replace"), 1,
                    new(candidate.SampleId, backup.SampleId, 1, blocked.DecisionSha256, "synthetic-category", 2,
                        backup.CandidateSha256, DateTimeOffset.UtcNow), cancellationToken), "blocking replacement rejected");
                await store.SaveDecisionAsync(Guid.NewGuid().ToString(), Hash("reject-again"), 1, Decision(2, true), cancellationToken);
            }
            await using (var recovered = await Open(blockedPath))
                Check(recovered.Snapshot.BlockingIssue && !recovered.Snapshot.Decisions[candidate.SampleId].BlockingIssue, "blocking survives later nonblocking rejection and restart");
        }
        finally { Directory.Delete(root, recursive: true); }
        Console.WriteLine($"Situation review work store checks passed: {checks}");
    }

    private static string Hash(string value) => SituationArtifactIO.Sha256(value);
}

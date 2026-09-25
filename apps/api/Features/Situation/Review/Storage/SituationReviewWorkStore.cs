using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using CsDemoMap.Api.Models;

namespace CsDemoMap.Api.Services;

// Injection happens after the named durable boundary, except write/rename which happen before it.
internal enum ReviewStorageFaultPoint { Write, Rename, Intent, Decision, Readback, Commit, Index }

internal sealed class SituationReviewWorkStore : IReviewWorkStore
{
    private sealed record Workspace([property: JsonRequired] ReviewWorkspaceIdentity Identity,
        [property: JsonRequired] IReadOnlyList<ReviewActiveSample> Initial);
    private sealed record Transaction([property: JsonRequired] long Revision,
        [property: JsonRequired] string PreviousSha256, [property: JsonRequired] string? PreviousDecisionSha256,
        [property: JsonRequired] SituationReviewDecisionV1? Decision,
        [property: JsonRequired] ReviewReplacementAudit? Replacement,
        [property: JsonRequired] ReviewCommitReceipt Receipt);
    private sealed record Commit([property: JsonRequired] long Revision, [property: JsonRequired] string TransactionSha256);
    private readonly string root;
    private readonly FileStream processLock;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Func<string, int, CancellationToken, Task<SituationReviewCandidateV1>> resolveCandidate;
    private readonly Action<ReviewStorageFaultPoint>? inject;
    private readonly Workspace workspace;
    private ReviewWorkSnapshot snapshot = new(0, false, [], new Dictionary<string, SituationReviewDecisionV1>(), []);
    private Dictionary<string, ReviewCommitReceipt> receipts = new(StringComparer.Ordinal);
    private string lastTransactionHash = new('0', 64);
    private bool recoveryRequired;
    private bool disposed;

    private SituationReviewWorkStore(string root, FileStream processLock, Workspace workspace,
        Func<string, int, CancellationToken, Task<SituationReviewCandidateV1>> resolveCandidate,
        Action<ReviewStorageFaultPoint>? inject)
    {
        this.root = root; this.processLock = processLock; this.workspace = workspace;
        this.resolveCandidate = resolveCandidate; this.inject = inject;
    }

    public ReviewWorkspaceIdentity Identity => Clone(workspace.Identity);
    public ReviewWorkSnapshot Snapshot => Clone(Volatile.Read(ref snapshot));

    internal static async Task<IReviewWorkStore> OpenAsync(string root, ReviewWorkspaceIdentity identity,
        IReadOnlyList<ReviewActiveSample> initial,
        Func<string, int, CancellationToken, Task<SituationReviewCandidateV1>> resolveCandidate,
        CancellationToken cancellationToken, Action<ReviewStorageFaultPoint>? faultInjector = null)
    {
        root = Path.GetFullPath(root);
        EnsureSafePath(root);
        ValidateInitial(initial);
        var exists = Directory.Exists(root);
        foreach (var name in new[] { ".lock", "workspace.json", "decisions", "transactions", "index.json" })
            EnsureSafePath(Path.Combine(root, name));
        if (exists && !File.Exists(Path.Combine(root, "workspace.json")))
            throw new ReviewException("workspace-identity-mismatch", 409);
        Directory.CreateDirectory(root);
        FileStream processLock;
        try { processLock = new FileStream(Path.Combine(root, ".lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException) { throw new ReviewException("workspace-locked", 409); }
        try
        {
            var expected = new Workspace(Clone(identity), Clone(initial.ToArray()));
            Workspace persisted;
            if (exists)
            {
                persisted = Read<Workspace>(Path.Combine(root, "workspace.json"));
                Require(Json(persisted) == Json(expected), "workspace-identity-mismatch", 409);
            }
            else
            {
                persisted = expected;
                await AtomicAsync(Path.Combine(root, "workspace.json"), Json(persisted), cancellationToken, null);
            }
            Directory.CreateDirectory(Path.Combine(root, "decisions"));
            Directory.CreateDirectory(Path.Combine(root, "transactions"));
            var store = new SituationReviewWorkStore(root, processLock, persisted, resolveCandidate, faultInjector);
            await store.RecoverAsync(cancellationToken);
            return store;
        }
        catch { await processLock.DisposeAsync(); throw; }
    }

    public ReviewCommitReceipt? FindReceipt(string requestId, string requestSha256)
    {
        ValidateRequest(requestId, requestSha256);
        gate.Wait();
        try { ThrowIfDisposed(); return Receipt(requestId, requestSha256); }
        finally { gate.Release(); }
    }

    private ReviewCommitReceipt? Receipt(string id, string hash)
    {
        if (!receipts.TryGetValue(id, out var receipt)) return null;
        Require(receipt.RequestSha256 == hash, "request-conflict", 409);
        return receipt;
    }

    public async Task<ReviewCommitReceipt> SaveDecisionAsync(string requestId, string requestSha256,
        int expectedRevision, SituationReviewDecisionV1 decision, CancellationToken cancellationToken)
    {
        ValidateRequest(requestId, requestSha256);
        decision = Clone(decision);
        await gate.WaitAsync(cancellationToken);
        try
        {
            ThrowIfDisposed();
            if (recoveryRequired) await RecoverAsync(cancellationToken);
            if (Receipt(requestId, requestSha256) is { } previous) return previous;
            var state = snapshot;
            var active = state.Active.SingleOrDefault(x => x.SampleId == decision.SampleId);
            Require(active is not null, "unknown-sample", 404);
            state.Decisions.TryGetValue(decision.SampleId, out var old);
            Require(expectedRevision == (old?.SampleRevision ?? 0) && decision.SampleRevision == expectedRevision + 1,
                "revision-conflict", 409);
            await ValidateDecisionAsync(decision, active!.ReviewOrdinal, state.BlockingIssue, cancellationToken);
            var receipt = new ReviewCommitReceipt(requestId, requestSha256, decision.SampleId,
                decision.SampleRevision, decision.DecisionSha256, state.WorkspaceRevision + 1, null);
            var transaction = new Transaction(receipt.WorkspaceRevision, lastTransactionHash,
                old?.DecisionSha256, decision, null, receipt);
            return await ExecuteAsync(transaction, cancellationToken);
        }
        finally { gate.Release(); }
    }

    public async Task<ReviewCommitReceipt> ReplaceAsync(string requestId, string requestSha256,
        long expectedWorkspaceRevision, ReviewReplacementAudit replacement, CancellationToken cancellationToken)
    {
        ValidateRequest(requestId, requestSha256);
        await gate.WaitAsync(cancellationToken);
        try
        {
            ThrowIfDisposed();
            if (recoveryRequired) await RecoverAsync(cancellationToken);
            if (Receipt(requestId, requestSha256) is { } previous) return previous;
            Require(expectedWorkspaceRevision == snapshot.WorkspaceRevision, "revision-conflict", 409);
            await ValidateReplacementAsync(replacement, snapshot, cancellationToken);
            var old = snapshot.Decisions[replacement.OldSampleId];
            var receipt = new ReviewCommitReceipt(requestId, requestSha256, replacement.OldSampleId,
                old.SampleRevision, old.DecisionSha256, replacement.WorkspaceRevision, replacement.NewSampleId);
            return await ExecuteAsync(new Transaction(receipt.WorkspaceRevision, lastTransactionHash,
                old.DecisionSha256, null, replacement, receipt), cancellationToken);
        }
        finally { gate.Release(); }
    }

    private async Task<ReviewCommitReceipt> ExecuteAsync(Transaction transaction, CancellationToken ct)
    {
        recoveryRequired = true;
        await AtomicAsync(TransactionPath(transaction.Revision), Json(transaction), ct, inject);
        inject?.Invoke(ReviewStorageFaultPoint.Intent);
        if (transaction.Decision is { } decision)
        {
            await AtomicAsync(DecisionPath(decision.SampleId), Json(decision), ct, inject);
            inject?.Invoke(ReviewStorageFaultPoint.Decision);
            VerifyDecisionFile(decision);
        }
        inject?.Invoke(ReviewStorageFaultPoint.Readback);
        await WriteCommitAsync(transaction, ct);
        inject?.Invoke(ReviewStorageFaultPoint.Commit);
        VerifyCommit(transaction);
        Publish(transaction);
        recoveryRequired = false;
        await WriteIndexAsync(ct);
        return transaction.Receipt;
    }

    private async Task RecoverAsync(CancellationToken ct)
    {
        // Build privately: concurrent readers must never observe a partially replayed history,
        // and a corrupt tail must not replace the last complete in-memory snapshot.
        var recovered = new SituationReviewWorkStore(root, processLock, workspace, resolveCandidate, null);
        await recovered.RecoverCoreAsync(ct);
        receipts = recovered.receipts;
        lastTransactionHash = recovered.lastTransactionHash;
        Volatile.Write(ref snapshot, recovered.snapshot);
        recoveryRequired = false;
    }

    private async Task RecoverCoreAsync(CancellationToken ct)
    {
        EnsureSafePath(root);
        foreach (var path in Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories)) EnsureSafePath(path);
        foreach (var path in Directory.EnumerateFileSystemEntries(Path.Combine(root, "transactions")))
            Require(!Directory.Exists(path) && (path.EndsWith(".json", StringComparison.Ordinal) ||
                path.EndsWith(".commit", StringComparison.Ordinal) || path.EndsWith(".tmp", StringComparison.Ordinal)), "storage-corrupt", 503);
        Require(!Directory.EnumerateDirectories(Path.Combine(root, "decisions")).Any(), "storage-corrupt", 503);
        snapshot = new(0, false, workspace.Initial.ToArray(), new Dictionary<string, SituationReviewDecisionV1>(), []);
        receipts = new(StringComparer.Ordinal);
        lastTransactionHash = new('0', 64);
        var paths = Directory.GetFiles(Path.Combine(root, "transactions"), "*.json").Order(StringComparer.Ordinal).ToArray();
        Transaction? pending = null;
        foreach (var path in paths)
        {
            ct.ThrowIfCancellationRequested();
            var tx = Read<Transaction>(path);
            Require(path == TransactionPath(snapshot.WorkspaceRevision + 1) && tx.Revision == snapshot.WorkspaceRevision + 1 &&
                tx.PreviousSha256 == lastTransactionHash && pending is null, "storage-corrupt", 503);
            await ValidateTransactionAsync(tx, ct);
            if (File.Exists(CommitPath(tx.Revision))) VerifyCommit(tx);
            else pending = tx;
            if (pending is null) Publish(tx);
        }
        // Committed decisions must be present and exact. Only the final uncommitted intent may explain a changed file.
        foreach (var decision in snapshot.Decisions.Values)
        {
            if (pending?.Decision?.SampleId == decision.SampleId) continue;
            VerifyDecisionFile(decision);
        }
        var allowed = snapshot.Decisions.Keys.ToHashSet(StringComparer.Ordinal);
        if (pending?.Decision is { } pendingDecision) allowed.Add(pendingDecision.SampleId);
        foreach (var path in Directory.GetFiles(Path.Combine(root, "decisions")))
        {
            if (path.EndsWith(".tmp", StringComparison.Ordinal)) continue;
            Require(Path.GetExtension(path) == ".json" && allowed.Contains(Path.GetFileNameWithoutExtension(path)), "storage-corrupt", 503);
        }
        foreach (var path in Directory.GetFiles(Path.Combine(root, "transactions"), "*.commit"))
            Require(long.TryParse(Path.GetFileNameWithoutExtension(path), out var revision) && revision >= 1 &&
                revision <= snapshot.WorkspaceRevision && path == CommitPath(revision), "storage-corrupt", 503);
        if (pending is not null)
        {
            if (pending.Decision is { } decision)
            {
                var path = DecisionPath(decision.SampleId);
                if (File.Exists(path))
                {
                    var actual = Read<SituationReviewDecisionV1>(path);
                    SituationTrainingContractJson.Validate(actual);
                    Require(actual.DecisionSha256 == decision.DecisionSha256 ||
                        (pending.PreviousDecisionSha256 is not null && actual.DecisionSha256 == pending.PreviousDecisionSha256), "storage-corrupt", 503);
                }
                else Require(pending.PreviousDecisionSha256 is null, "storage-corrupt", 503);
                await AtomicAsync(path, Json(decision), ct, null);
                VerifyDecisionFile(decision);
            }
            await WriteCommitAsync(pending, ct, recovery: true);
            VerifyCommit(pending);
            Publish(pending);
        }
        recoveryRequired = false;
        await WriteIndexAsync(ct);
    }

    private async Task ValidateTransactionAsync(Transaction tx, CancellationToken ct)
    {
        ValidateRequest(tx.Receipt.RequestId, tx.Receipt.RequestSha256);
        Require(!receipts.ContainsKey(tx.Receipt.RequestId) && tx.Receipt.WorkspaceRevision == tx.Revision &&
            (tx.Decision is null) != (tx.Replacement is null), "storage-corrupt", 503);
        if (tx.Decision is { } d)
        {
            var active = snapshot.Active.SingleOrDefault(x => x.SampleId == d.SampleId);
            Require(active is not null, "storage-corrupt", 503);
            snapshot.Decisions.TryGetValue(d.SampleId, out var old);
            Require(tx.PreviousDecisionSha256 == old?.DecisionSha256 && d.SampleRevision == (old?.SampleRevision ?? 0) + 1 &&
                tx.Receipt.SampleId == d.SampleId && tx.Receipt.SampleRevision == d.SampleRevision &&
                tx.Receipt.DecisionSha256 == d.DecisionSha256 && tx.Receipt.NewSampleId is null, "storage-corrupt", 503);
            await ValidateDecisionAsync(d, active!.ReviewOrdinal, snapshot.BlockingIssue, ct);
        }
        else
        {
            var r = tx.Replacement!;
            await ValidateReplacementAsync(r, snapshot, ct);
            var old = snapshot.Decisions[r.OldSampleId];
            Require(tx.PreviousDecisionSha256 == old.DecisionSha256 && tx.Receipt.SampleId == r.OldSampleId &&
                tx.Receipt.SampleRevision == old.SampleRevision && tx.Receipt.DecisionSha256 == old.DecisionSha256 &&
                tx.Receipt.NewSampleId == r.NewSampleId, "storage-corrupt", 503);
        }
    }

    private async Task ValidateDecisionAsync(SituationReviewDecisionV1 decision, int ordinal, bool blocked, CancellationToken ct)
    {
        ValidateSampleId(decision.SampleId);
        Require(decision.DatasetSha256 == workspace.Identity.DatasetSha256, "workspace-identity-mismatch", 409);
        var candidate = await CandidateAsync(decision.SampleId, ordinal, ct);
        SituationTrainingContractJson.Validate(decision, candidate);
        Require(!(blocked || decision.BlockingIssue) || decision.Decision == SituationReviewDecisionKind.Rejected, "workspace-blocked", 409);
        Require(decision.Decision != SituationReviewDecisionKind.Rejected || !string.IsNullOrWhiteSpace(decision.Note), "invalid-decision");
        Require(decision.Decision != SituationReviewDecisionKind.Modified || decision.FinalNarrativeSha256 != decision.CandidateSha256, "invalid-decision");
    }

    private async Task ValidateReplacementAsync(ReviewReplacementAudit r, ReviewWorkSnapshot state, CancellationToken ct)
    {
        ValidateSampleId(r.OldSampleId); ValidateSampleId(r.NewSampleId);
        Require(!state.BlockingIssue, "workspace-blocked", 409);
        Require(r.WorkspaceRevision == state.WorkspaceRevision + 1, "revision-conflict", 409);
        Require(state.Active.Any(x => x.SampleId == r.OldSampleId && x.ReviewOrdinal == r.ReviewOrdinal) &&
            state.Decisions.TryGetValue(r.OldSampleId, out var old) && old.Decision == SituationReviewDecisionKind.Rejected &&
            !old.BlockingIssue && old.DecisionSha256 == r.RejectedDecisionSha256, "invalid-replacement");
        Require(!workspace.Initial.Any(x => x.SampleId == r.NewSampleId) && !state.Decisions.ContainsKey(r.NewSampleId) &&
            !state.Replacements.Any(x => x.NewSampleId == r.NewSampleId || x.OldSampleId == r.NewSampleId) &&
            !string.IsNullOrWhiteSpace(r.PriorityCategory) && r.ReplacedAtUtc.Offset == TimeSpan.Zero, "invalid-replacement");
        var oldCandidate = await CandidateAsync(r.OldSampleId, r.ReviewOrdinal, ct);
        var candidate = await CandidateAsync(r.NewSampleId, r.ReviewOrdinal, ct);
        Require(candidate.CandidateSha256 == r.CandidateSha256 && candidate.Split == oldCandidate.Split, "invalid-replacement");
    }

    private async Task<SituationReviewCandidateV1> CandidateAsync(string id, int ordinal, CancellationToken ct)
    {
        var candidate = await resolveCandidate(id, ordinal, ct);
        SituationTrainingContractJson.Validate(candidate);
        Require(candidate.SampleId == id && candidate.ReviewOrdinal == ordinal, "invalid-candidate");
        return candidate;
    }

    private void Publish(Transaction tx)
    {
        var decisions = snapshot.Decisions.ToDictionary(x => x.Key, x => x.Value, StringComparer.Ordinal);
        var active = snapshot.Active.ToArray();
        var replacements = snapshot.Replacements.ToList();
        if (tx.Decision is { } decision) decisions[decision.SampleId] = decision;
        if (tx.Replacement is { } replacement)
        {
            active = active.Select(x => x.SampleId == replacement.OldSampleId ? new ReviewActiveSample(x.ReviewOrdinal, replacement.NewSampleId) : x).ToArray();
            replacements.Add(replacement);
        }
        receipts.Add(tx.Receipt.RequestId, tx.Receipt);
        lastTransactionHash = Hash(tx);
        Volatile.Write(ref snapshot, new(tx.Revision, snapshot.BlockingIssue || tx.Decision?.BlockingIssue == true,
            active, decisions, replacements));
    }

    private async Task WriteIndexAsync(CancellationToken ct)
    {
        try
        {
            inject?.Invoke(ReviewStorageFaultPoint.Index);
            await AtomicAsync(Path.Combine(root, "index.json"), Json(snapshot), ct, null);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or OperationCanceledException) { /* Cache is dispensable. */ }
    }
    private Task WriteCommitAsync(Transaction tx, CancellationToken ct, bool recovery = false) =>
        AtomicAsync(CommitPath(tx.Revision), Json(new Commit(tx.Revision, Hash(tx))), ct, recovery ? null : inject);
    private void VerifyCommit(Transaction tx) => Require(Read<Commit>(CommitPath(tx.Revision)) == new Commit(tx.Revision, Hash(tx)), "storage-corrupt", 503);
    private void VerifyDecisionFile(SituationReviewDecisionV1 expected)
    {
        var actual = Read<SituationReviewDecisionV1>(DecisionPath(expected.SampleId));
        SituationTrainingContractJson.Validate(actual);
        Require(Json(actual) == Json(expected), "storage-corrupt", 503);
    }
    private string DecisionPath(string id) { ValidateSampleId(id); return Path.Combine(root, "decisions", id + ".json"); }
    private string TransactionPath(long revision) => Path.Combine(root, "transactions", revision.ToString("D20", System.Globalization.CultureInfo.InvariantCulture) + ".json");
    private string CommitPath(long revision) => Path.Combine(root, "transactions", revision.ToString("D20", System.Globalization.CultureInfo.InvariantCulture) + ".commit");
    private static string Json<T>(T value) => JsonSerializer.Serialize(value, SituationReviewJson.Options);
    private static T Clone<T>(T value) => SituationReviewJson.Deserialize<T>(Json(value));
    private static string Hash<T>(T value) => SituationArtifactIO.Sha256(Json(value));
    private static T Read<T>(string path) => SituationReviewJson.Deserialize<T>(File.ReadAllText(path));
    private static void ValidateSampleId(string id) => Require(id is not null && Regex.IsMatch(id, "^sample-[0-9a-f]{64}$", RegexOptions.CultureInvariant), "invalid-sample");
    private static void ValidateRequest(string id, string hash) => Require(Guid.TryParseExact(id, "D", out var guid) && guid.ToString("D") == id && hash is not null && Regex.IsMatch(hash, "^[0-9a-f]{64}$", RegexOptions.CultureInvariant), "invalid-request");
    private static void ValidateInitial(IReadOnlyList<ReviewActiveSample> initial)
    {
        Require(initial.Count > 0 && initial.Select(x => x.SampleId).Distinct(StringComparer.Ordinal).Count() == initial.Count &&
            initial.Select(x => x.ReviewOrdinal).Order().SequenceEqual(Enumerable.Range(1, initial.Count)), "invalid-workspace");
        foreach (var item in initial) ValidateSampleId(item.SampleId);
    }
    private static void Require(bool condition, string code, int status = 400) { if (!condition) throw new ReviewException(code, status); }
    private static void EnsureSafePath(string path)
    {
        for (string? current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
            if (File.Exists(current) || Directory.Exists(current))
                Require((File.GetAttributes(current) & FileAttributes.ReparsePoint) == 0, "unsafe-workspace", 409);
    }
    private static async Task AtomicAsync(string path, string contents, CancellationToken ct, Action<ReviewStorageFaultPoint>? inject)
    {
        EnsureSafePath(path);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        inject?.Invoke(ReviewStorageFaultPoint.Write);
        await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            65536, FileOptions.Asynchronous | FileOptions.WriteThrough))
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(contents);
            await stream.WriteAsync(bytes, ct);
            await stream.FlushAsync(ct);
            stream.Flush(flushToDisk: true);
        }
        ct.ThrowIfCancellationRequested();
        inject?.Invoke(ReviewStorageFaultPoint.Rename);
        if (File.Exists(path)) File.Replace(temporary, path, null);
        else File.Move(temporary, path);
    }
    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(disposed, this);
    public async ValueTask DisposeAsync()
    {
        await gate.WaitAsync();
        try { if (!disposed) { disposed = true; await processLock.DisposeAsync(); } }
        finally { gate.Release(); }
    }
}

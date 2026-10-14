using System.Text;
using System.Text.Json;
using CsDemoMap.Api.Models;

namespace CsDemoMap.Api.Services;

internal static class SituationReviewFreezer
{
    internal const string Schema = "situation-frozen-review-manifest-v2";
    internal static async Task<ReviewFreezeManifest> ExportAsync(IReviewCatalog catalog, string work, string output,
        string baseDatasetSha256, IReadOnlyDictionary<string, string> provenance, string? approvedContentSha256,
        CancellationToken ct, Action<string>? fault = null)
    {
        SituationReviewArtifactLoader.ValidateWorkLocation(output, work);
        SituationArtifactIO.EnsureNewOutput(output);
        if (!Directory.Exists(work)) throw new ReviewException("workspace-missing");
        // The same process lock as the review server prevents concurrent mutation.
        await using var store = await OpenStoreAsync(catalog, work, ct);
        var payload = await BuildAsync(catalog, store, work, ct);
        Directory.CreateDirectory(output);
        var manifest = new ReviewFreezeManifest(Schema, "incomplete", "awaiting-approval", payload.ReviewVersion,
            baseDatasetSha256, catalog.Identity, payload.Target, payload.WorkspaceRevision, payload.Counts, "", null, []);
        await WriteNewAsync(Path.Combine(output, "manifest.json"), SituationCanonicalJson.Serialize(manifest), ct);
        fault?.Invoke("manifest");
        foreach (var (name, content) in payload.Documents) await WriteNewAsync(Path.Combine(output, name), content, ct);
        foreach (var (name, content) in provenance)
        {
            if (!name.StartsWith("provenance/", StringComparison.Ordinal)) throw new ReviewException("artifact-mismatch");
            await WriteNewAsync(SafePath(output, name), content, ct);
        }
        foreach (var path in AuditPaths(work))
        {
            var relative = Path.GetRelativePath(work, path).Replace('\\', '/');
            await WriteNewAsync(SafePath(output, "audit/" + relative), await File.ReadAllTextAsync(path, ct), ct);
        }
        fault?.Invoke("files");
        var files = await InventoryAsync(output, ct);
        var contentHash = ContentHash(manifest, files);
        if (approvedContentSha256 is not null && approvedContentSha256 != contentHash)
            throw new ReviewException("approval-content-mismatch");
        manifest = manifest with { Files = files, ContentSha256 = contentHash,
            ReviewStatus = approvedContentSha256 is null ? "awaiting-approval" : "frozen", ApprovedContentSha256 = approvedContentSha256 };
        // Full independent journal replay and output reconstruction precede the completion marker.
        await VerifyPayloadAsync(output, manifest, catalog, ct);
        fault?.Invoke("readback");
        manifest = manifest with { Status = "complete" };
        var temporary = Path.Combine(output, "manifest.json.tmp");
        await WriteNewAsync(temporary, SituationCanonicalJson.Serialize(manifest), ct);
        fault?.Invoke("commit");
        File.Replace(temporary, Path.Combine(output, "manifest.json"), null);
        return manifest;
    }

    internal static async Task<ReviewFreezeManifest> VerifyAsync(string output, IReviewCatalog catalog,
        string baseDatasetSha256, CancellationToken ct)
    {
        var manifest = await ReadManifestAsync(output, ct);
        if (manifest.BaseDatasetSha256 != baseDatasetSha256) throw new ReviewException("artifact-mismatch");
        await VerifyPayloadAsync(output, manifest, catalog, ct);
        return manifest;
    }

    internal static async Task<ReviewFreezeManifest> ReadManifestAsync(string output, CancellationToken ct)
    {
        var text = await File.ReadAllTextAsync(SafePath(output, "manifest.json"), ct);
        var manifest = ReadStrict<ReviewFreezeManifest>(text);
        if (manifest.Status != "complete") throw new ReviewException("review-artifact-incomplete");
        await VerifyInventoryAsync(output, manifest, ct);
        return manifest;
    }

    private static async Task VerifyInventoryAsync(string output, ReviewFreezeManifest manifest, CancellationToken ct)
    {
        var files = await InventoryAsync(output, ct);
        if (manifest.SchemaVersion != Schema || !SituationReviewCandidateReader.Equal(files, manifest.Files) ||
            manifest.ContentSha256 != ContentHash(manifest, files) ||
            manifest.ReviewStatus is not ("awaiting-approval" or "frozen") ||
            (manifest.ReviewStatus == "frozen" ? manifest.ApprovedContentSha256 != manifest.ContentSha256 : manifest.ApprovedContentSha256 is not null))
            throw new ReviewException("artifact-mismatch");
    }

    private static async Task VerifyPayloadAsync(string output, ReviewFreezeManifest manifest, IReviewCatalog catalog, CancellationToken ct)
    {
        await VerifyInventoryAsync(output, manifest, ct);
        if (!SituationReviewCandidateReader.Equal(manifest.Identity, catalog.Identity)) throw new ReviewException("artifact-mismatch");
        var temporary = Path.Combine(Path.GetTempPath(), "situation-freeze-verify-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        try
        {
            foreach (var file in manifest.Files.Where(f => f.Path.StartsWith("audit/", StringComparison.Ordinal)))
            {
                var destination = SafePath(temporary, file.Path[6..]);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(SafePath(output, file.Path), destination);
            }
            await using var store = await OpenStoreAsync(catalog, temporary, ct);
            var payload = await BuildAsync(catalog, store, temporary, ct);
            if (manifest.Target != payload.Target || manifest.WorkspaceRevision != payload.WorkspaceRevision ||
                manifest.ReviewVersion != payload.ReviewVersion || !SituationReviewCandidateReader.Equal(manifest.Counts, payload.Counts))
                throw new ReviewException("artifact-mismatch");
            foreach (var (name, text) in payload.Documents)
                if (await File.ReadAllTextAsync(SafePath(output, name), ct) != text) throw new ReviewException("artifact-mismatch");
            var expectedNames = payload.Documents.Keys.Concat(AuditPaths(temporary).Select(p => "audit/" +
                Path.GetRelativePath(temporary, p).Replace('\\', '/'))).ToHashSet(StringComparer.Ordinal);
            foreach (var file in manifest.Files)
                if (!expectedNames.Contains(file.Path) && !file.Path.StartsWith("provenance/", StringComparison.Ordinal))
                    throw new ReviewException("artifact-mismatch");
        }
        finally { Directory.Delete(temporary, true); }
    }

    private static Task<IReviewWorkStore> OpenStoreAsync(IReviewCatalog catalog, string work, CancellationToken ct) =>
        SituationReviewWorkStore.OpenAsync(work, catalog.Identity,
            catalog.InitialSelected.Select(s => new ReviewActiveSample(s.ReviewOrdinal, s.Entry.SampleId)).ToArray(), catalog.GetCandidateAsync, ct);

    internal static async Task<ReviewFreezePayload> BuildAsync(IReviewCatalog catalog, IReviewWorkStore store, string work, CancellationToken ct)
    {
        var consumerPath = Path.Combine(work, "consumer-provenance.json");
        if (File.Exists(consumerPath))
        {
            var consumer = ReadStrict<SituationReviewConsumerProvenance.Document>(await File.ReadAllTextAsync(consumerPath, ct));
            var previous = new string('0', 64);
            if (consumer.SchemaVersion != "situation-review-consumer-provenance-v1") throw new ReviewException("artifact-mismatch");
            foreach (var entry in consumer.Sessions)
            {
                if (entry.Session.PreviousSha256 != previous || SituationCanonicalJson.Sha256(entry.Session) != entry.Sha256 ||
                    entry.Session.DatasetSha256 != catalog.Identity.DatasetSha256 ||
                    entry.Session.CandidateManifestSha256 != catalog.Identity.CandidateManifestSha256 ||
                    entry.Session.StartingWorkspaceRevision > store.Snapshot.WorkspaceRevision)
                    throw new ReviewException("artifact-mismatch");
                previous = entry.Sha256;
            }
        }
        IReviewBackend backend = new SituationReviewDecisionService(catalog, store);
        backend = await SituationReviewScope.OpenAsync(backend, store, work, null, ct);
        var history = new List<ReviewFreezeHistory>();
        foreach (var path in Directory.GetFiles(Path.Combine(work, "transactions"), "*.json").Order(StringComparer.Ordinal))
        {
            using var json = JsonDocument.Parse(await File.ReadAllTextAsync(path, ct));
            var transaction = json.RootElement;
            if (transaction.GetProperty("decision") is { ValueKind: JsonValueKind.Object } decision)
                history.Add(new(transaction.GetProperty("revision").GetInt64(), ReadStrict<SituationReviewDecisionV1>(decision.GetRawText())));
        }
        return await SituationReviewFreezeBuilder.BuildAsync(catalog, store, backend, history, ct);
    }

    private static IEnumerable<string> AuditPaths(string work)
    {
        SituationReviewArtifactLoader.EnsureNoLinks(work);
        foreach (var path in Directory.EnumerateFileSystemEntries(work, "*", SearchOption.AllDirectories))
            SituationReviewArtifactLoader.EnsureNoLinks(path);
        foreach (var path in Directory.GetFiles(work, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            var name = Path.GetRelativePath(work, path).Replace('\\', '/');
            if (name is ".lock" or "index.json") continue;
            if (name.EndsWith(".tmp", StringComparison.Ordinal)) throw new ReviewException("workspace-uncommitted");
            if (name is not ("workspace.json" or "review-scope.json" or "consumer-provenance.json") &&
                !name.StartsWith("decisions/", StringComparison.Ordinal) && !name.StartsWith("transactions/", StringComparison.Ordinal))
                throw new ReviewException("artifact-mismatch");
            yield return path;
        }
    }

    internal static string ContentHash(ReviewFreezeManifest manifest, IReadOnlyList<SituationArtifactFileV1> files) =>
        SituationCanonicalJson.Sha256(new { manifest.SchemaVersion, manifest.ReviewVersion, manifest.BaseDatasetSha256,
            manifest.Identity, manifest.Target, manifest.WorkspaceRevision, manifest.Counts, files });

    internal static async Task<IReadOnlyList<SituationArtifactFileV1>> InventoryAsync(string root, CancellationToken ct)
    {
        SituationReviewArtifactLoader.EnsureNoLinks(root);
        foreach (var path in Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories))
            SituationReviewArtifactLoader.EnsureNoLinks(path);
        var files = new List<SituationArtifactFileV1>();
        foreach (var path in Directory.GetFiles(root, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
            if (relative == "manifest.json") continue;
            long? lines = relative.EndsWith(".jsonl", StringComparison.Ordinal) ? (await File.ReadAllLinesAsync(path, ct)).LongLength : null;
            files.Add(new(relative, new FileInfo(path).Length, lines, await SituationArtifactIO.FileSha256Async(path, ct)));
        }
        return files.OrderBy(f => f.Path, StringComparer.Ordinal).ToArray();
    }

    internal static T ReadStrict<T>(string text)
    {
        try
        {
            SituationTrainingContractJson.RequireCompleteShape<T>(text, "Frozen review");
            return SituationReviewJson.Deserialize<T>(text);
        }
        catch (JsonException) { throw new ReviewException("artifact-mismatch"); }
    }

    internal static string SafePath(string root, string relative)
    {
        var path = SituationArtifactIO.ResolveRepositoryPath(root, relative, "Frozen review");
        SituationReviewArtifactLoader.EnsureNoLinks(path);
        return path;
    }

    internal static async Task WriteNewAsync(string path, string text, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        await stream.WriteAsync(Encoding.UTF8.GetBytes(text), ct);
        stream.Flush(true);
    }
}

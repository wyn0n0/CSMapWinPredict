using System.Text;
using CsDemoMap.Api.Models;

namespace CsDemoMap.Api.Services;

internal static class SituationReviewConsumerProvenance
{
    internal sealed record Session(string SessionId, DateTimeOffset StartedAtUtc, long StartingWorkspaceRevision,
        string DatasetSha256, string CandidateManifestSha256, IReadOnlyList<SituationProvenanceSourceFileV1> Files,
        IReadOnlyList<SituationProvenanceSourceFileV1> Binaries, string PreviousSha256);
    internal sealed record Entry(Session Session, string Sha256);
    internal sealed record Document(string SchemaVersion, IReadOnlyList<Entry> Sessions);

    // Called while holding the work store's process lock and before accepting requests.
    internal static async Task CaptureAsync(string repository, string work, IReviewWorkStore store,
        IReadOnlyList<string> binaries, CancellationToken ct)
    {
        var paths = Directory.EnumerateFiles(Path.Combine(repository, "apps/api/Features/Situation/Review"), "*", SearchOption.AllDirectories)
            .Concat(Directory.EnumerateFiles(Path.Combine(repository, "apps/cli/SituationReview"), "*", SearchOption.AllDirectories))
            .Concat(Directory.EnumerateFiles(Path.Combine(repository, "apps/cli/SituationReviewUi"), "*", SearchOption.AllDirectories))
            .Concat(Directory.EnumerateFiles(Path.Combine(repository, "schemas/situation"), "situation-review-*.schema.json"))
            .Concat(new[] { "apps/cli/DeveloperCommandDispatcher.cs", "apps/cli/Program.cs", "apps/cli/CsDemoMap.Cli.csproj", "apps/api/CsDemoMap.Api.csproj" }.Select(p => Path.Combine(repository, p)))
            .Order(StringComparer.Ordinal).ToArray();
        var files = new List<SituationProvenanceSourceFileV1>();
        foreach (var path in paths)
        {
            SituationReviewArtifactLoader.EnsureNoLinks(path);
            files.Add(new(Path.GetRelativePath(repository, path).Replace('\\', '/'), await SituationArtifactIO.FileSha256Async(path, ct)));
        }
        var assemblies = new List<SituationProvenanceSourceFileV1>();
        foreach (var path in binaries.Order(StringComparer.Ordinal))
            assemblies.Add(new(Path.GetFileName(path), await SituationArtifactIO.FileSha256Async(path, ct)));
        var destination = Path.Combine(work, "consumer-provenance.json");
        var entries = new List<Entry>();
        var previous = new string('0', 64);
        if (File.Exists(destination))
        {
            SituationReviewArtifactLoader.EnsureNoLinks(destination);
            var document = SituationReviewJson.Deserialize<Document>(await File.ReadAllTextAsync(destination, ct));
            if (document.SchemaVersion != "situation-review-consumer-provenance-v1") throw new ReviewException("artifact-mismatch");
            foreach (var entry in document.Sessions)
            {
                if (entry.Session.PreviousSha256 != previous || SituationCanonicalJson.Sha256(entry.Session) != entry.Sha256 ||
                    entry.Session.DatasetSha256 != store.Identity.DatasetSha256 || entry.Session.CandidateManifestSha256 != store.Identity.CandidateManifestSha256 ||
                    entry.Session.StartingWorkspaceRevision > store.Snapshot.WorkspaceRevision) throw new ReviewException("artifact-mismatch");
                entries.Add(entry);
                previous = entry.Sha256;
            }
        }
        var session = new Session(Guid.NewGuid().ToString("D"), DateTimeOffset.UtcNow, store.Snapshot.WorkspaceRevision,
            store.Identity.DatasetSha256, store.Identity.CandidateManifestSha256, files, assemblies, previous);
        entries.Add(new(session, SituationCanonicalJson.Sha256(session)));
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        var text = SituationCanonicalJson.Serialize(new Document("situation-review-consumer-provenance-v1", entries));
        await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            await stream.WriteAsync(Encoding.UTF8.GetBytes(text), ct);
            stream.Flush(true);
        }
        if (File.Exists(destination)) File.Replace(temporary, destination, null); else File.Move(temporary, destination);
        if (await File.ReadAllTextAsync(destination, ct) != text) throw new ReviewException("storage-failed", 503);
    }
}

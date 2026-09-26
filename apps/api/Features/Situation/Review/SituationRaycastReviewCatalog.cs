using System.Collections.Concurrent;
using CsDemoMap.Api.Models;

namespace CsDemoMap.Api.Services;

// Keeps the approved sampling plan; only facts/evidence/narrative are rederived.
// RecordSha256 and sample IDs intentionally identify the immutable source record.
internal sealed class SituationRaycastReviewCatalog : IReviewCatalog
{
    internal const string CandidateSchema = "situation-review-candidate-v2-raycast";
    private readonly IReviewCatalog source;
    private readonly SituationDeterministicAnalyzer analyzer;
    private readonly ConcurrentDictionary<string, SituationReviewCandidateV1> cache = new(StringComparer.Ordinal);
    public ReviewWorkspaceIdentity Identity { get; private set; } = null!;
    public IReadOnlyList<ReviewSelectedEntry> InitialSelected => source.InitialSelected.Select(s => new ReviewSelectedEntry(s.ReviewOrdinal, Update(s.Entry))).ToArray();
    public IReadOnlyList<ReviewPoolEntry> Pool => source.Pool.Select(Update).ToArray();
    public IReadOnlyDictionary<string, IReadOnlyList<string>> Backups => source.Backups;
    public ReviewSelectionPolicy Policy => source.Policy;

    private SituationRaycastReviewCatalog(IReviewCatalog source, SituationDeterministicAnalyzer analyzer)
    { this.source = source; this.analyzer = analyzer; }

    private ReviewPoolEntry Update(ReviewPoolEntry entry) => cache.TryGetValue(entry.SampleId, out var candidate)
        ? entry with { FactsSha256 = SituationCanonicalJson.Sha256(candidate.Input.Facts), PrelabelSha256 = candidate.CandidateSha256 }
        : entry;

    internal static async Task<SituationRaycastReviewCatalog> OpenAsync(IReviewCatalog source, string output,
        string repository, CancellationToken ct)
    {
        var mesh = Path.Combine(AppContext.BaseDirectory, "Geometry", "de_mirage.mesh");
        if (!File.Exists(mesh)) throw new InvalidDataException("Raycast review requires the pinned collision mesh.");
        var analyzer = SituationDeterministicAnalyzer.CreateRaycast(mesh); // verifies mesh SHA-256
        var result = new SituationRaycastReviewCatalog(source, analyzer);
        var rows = new List<string>();
        foreach (var item in source.InitialSelected.OrderBy(s => s.ReviewOrdinal))
            rows.Add(SituationCanonicalJson.Serialize(await result.GetCandidateAsync(item.Entry.SampleId, item.ReviewOrdinal, ct)));
        var jsonl = string.Join("\n", rows) + "\n";
        var schemaPath = "schemas/situation/situation-review-candidate-v2-raycast.schema.json";
        var schema = new SituationProvenanceSourceFileV1(schemaPath,
            await SituationArtifactIO.FileSha256Async(Path.Combine(repository, schemaPath), ct));
        var manifest = SituationCanonicalJson.Serialize(new {
            schemaVersion = "situation-raycast-review-manifest-v1", status = "complete",
            source = source.Identity, rulesSha256 = analyzer.RuleLoad.Sha256,
            asset = analyzer.RuleLoad.Rules.Visibility, candidateSchema = schema,
            candidateFileSha256 = SituationArtifactIO.Sha256(jsonl), count = rows.Count,
            selection = "Original v1 selected times and backup categories; not a new raycast quota sample.",
            recordHash = "RecordSha256 identifies the original v1 source record; derived input is bound by this manifest.",
            limitations = "Static mesh; approximate body heights; historical map version not certified."
        });
        SituationReviewArtifactLoader.EnsureNoLinks(output);
        if (Directory.Exists(output))
        {
            SituationReviewCandidateReader.RequireFiles(output, ["manifest.json", "review-candidates.jsonl"]);
            if (await File.ReadAllTextAsync(Path.Combine(output, "manifest.json"), ct) != manifest ||
                await File.ReadAllTextAsync(Path.Combine(output, "review-candidates.jsonl"), ct) != jsonl)
                throw new ReviewException("artifact-mismatch");
        }
        else
        {
            SituationArtifactIO.EnsureNewOutput(output);
            Directory.CreateDirectory(output);
            // An interrupted export has no complete manifest and cannot be reopened.
            await WriteNewAsync(Path.Combine(output, "review-candidates.jsonl"), jsonl, ct);
            await WriteNewAsync(Path.Combine(output, "manifest.json"), manifest, ct);
        }
        var derived = SituationArtifactIO.Sha256(manifest);
        result.Identity = source.Identity with {
            DatasetSha256 = derived, CandidateManifestSha256 = derived,
            CandidateFileSha256 = SituationArtifactIO.Sha256(jsonl),
            SchemaFiles = source.Identity.SchemaFiles.Append(schema).ToArray(),
            ReviewDraft = "situation-narrative-review-v2-raycast-draft"
        };
        return result;
    }

    private static async Task WriteNewAsync(string path, string text, CancellationToken ct)
    {
        await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        await stream.WriteAsync(System.Text.Encoding.UTF8.GetBytes(text), ct);
        stream.Flush(true);
    }

    public async Task<SituationReviewCandidateV1> GetCandidateAsync(string sampleId, int reviewOrdinal, CancellationToken ct)
    {
        if (!cache.TryGetValue(sampleId, out var candidate))
        {
            var original = await source.GetCandidateAsync(sampleId, reviewOrdinal, ct);
            candidate = Derive(original, analyzer);
            cache.TryAdd(sampleId, candidate);
        }
        return candidate with { ReviewOrdinal = reviewOrdinal };
    }

    internal static SituationReviewCandidateV1 Derive(SituationReviewCandidateV1 original, SituationDeterministicAnalyzer analyzer)
    {
        SituationTrainingContractJson.Validate(original);
        var s = original.Input.Scene;
        var scene = new MinimapSceneV1(s.SchemaVersion, s.SceneBuilderVersion, s.GeometryVersion,
            "structured", s.Map, "model-input-validation", 0, s.Tick, s.Tick, s.TickRate,
            s.Round, s.Players, s.Teams, s.Bomb, s.Utilities, s.Effects, s.Geometry, s.DataQuality);
        var analysis = analyzer.Analyze(scene);
        var candidate = original with {
            SchemaVersion = CandidateSchema,
            Input = original.Input with { Facts = analysis.Facts.Facts,
                AllowedEvidenceIds = analysis.Facts.Facts.Evidence.Select(e => e.Id).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray() },
            Candidate = analysis.Narrative.Narrative, CandidateSha256 = analysis.Narrative.Sha256
        };
        SituationTrainingContractJson.Validate(candidate);
        return candidate;
    }
}

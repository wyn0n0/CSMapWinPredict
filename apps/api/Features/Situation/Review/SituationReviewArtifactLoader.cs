using CsDemoMap.Api.Models;

namespace CsDemoMap.Api.Services;

// A consumer of the pinned historical artifact, not a replacement for producer verification.
internal sealed class SituationReviewArtifactLoader : IReviewCatalog
{
    internal const string CandidateManifestHash = "9e0e1b4f58321041c208f5d6be716242f7340bef362a6ef43f407b929c8325f4";
    internal const string CandidateFileHash = "3588795dbd713cb9ff684b59776539c74c540a89641fb61883efe8d3a42565c7";
    internal const string CandidateProvenanceHash = "e50d52a44b925d5114b12c17fe66414b7fb485348c6ad94c320b19e488faedcc";
    private readonly SituationReviewCandidateBase source;
    private readonly Dictionary<string, SituationReviewCandidateV1> cache;
    private readonly Dictionary<string, ReviewPoolEntry> poolById;
    private readonly SemaphoreSlim materialization = new(1, 1);
    public ReviewWorkspaceIdentity Identity { get; }
    public IReadOnlyList<ReviewSelectedEntry> InitialSelected { get; }
    public IReadOnlyList<ReviewPoolEntry> Pool { get; }
    public IReadOnlyDictionary<string, IReadOnlyList<string>> Backups { get; }
    public ReviewSelectionPolicy Policy { get; }

    private SituationReviewArtifactLoader(SituationReviewCandidateBase source,
        SituationReviewCandidateManifestV1 manifest, ReviewSelectionPolicy policy,
        Dictionary<string, SituationReviewCandidateV1> candidates)
    {
        this.source = source;
        cache = candidates;
        Pool = manifest.SourceIndex;
        poolById = Pool.ToDictionary(e => e.SampleId, StringComparer.Ordinal);
        InitialSelected = manifest.Selected;
        Backups = manifest.Backups;
        Policy = policy;
        Identity = new("situation-review-workspace-v1", manifest.BaseManifestSha256,
            CandidateManifestHash, CandidateFileHash, SituationReviewCandidateReader.BaseSplitSha256,
            manifest.PolicySha256, source.Manifest.SchemaFiles.Select(f => new SituationProvenanceSourceFileV1(f.Path, f.Sha256))
                .Append(new("schemas/situation/situation-review-work-v1.schema.json", SituationArtifactIO.Sha256(
                    File.ReadAllBytes(Path.Combine(source.RepositoryRoot, "schemas/situation/situation-review-work-v1.schema.json"))))).ToArray(),
            "situation-narrative-review-v1-draft");
    }

    internal static async Task<SituationReviewArtifactLoader> OpenAsync(string datasetRoot, string candidateRoot,
        string producerSnapshot, CancellationToken cancellationToken)
    {
        EnsureNoLinks(datasetRoot);
        EnsureNoLinks(candidateRoot);
        EnsureNoLinks(producerSnapshot);
        // Complete base bytes and unchanged semantic producer files are still verified normally.
        var source = await OpenHistoricalBaseAsync(datasetRoot, producerSnapshot, cancellationToken);
        await VerifyHistoricalSourcesAsync(source.RepositoryRoot, producerSnapshot, cancellationToken);
        SituationReviewCandidateReader.RequireFiles(candidateRoot,
            ["manifest.json", "provenance.json", "review-candidates.jsonl", "review-stats.json"]);
        await SituationReviewCandidateReader.RequireHashAsync(Path.Combine(candidateRoot, "manifest.json"), CandidateManifestHash, cancellationToken);
        await SituationReviewCandidateReader.RequireHashAsync(Path.Combine(candidateRoot, "provenance.json"), CandidateProvenanceHash, cancellationToken);
        await SituationReviewCandidateReader.RequireHashAsync(Path.Combine(candidateRoot, "review-candidates.jsonl"), CandidateFileHash, cancellationToken);
        var manifest = await SituationReviewCandidateReader.ReadJsonAsync<SituationReviewCandidateManifestV1>(Path.Combine(candidateRoot, "manifest.json"), cancellationToken);
        var policy = SituationReviewSelectionPolicy.LoadFrozen();
        if (manifest.Status != "complete" || manifest.BaseManifestSha256 != SituationReviewSelectionPolicy.ApprovedDatasetSha256 ||
            manifest.PolicySha256 != SituationReviewSelectionPolicy.Hash(policy)) throw new ReviewException("artifact-mismatch");
        var inventory = await SituationArtifactIO.InventoryFilesAsync(candidateRoot, cancellationToken, "manifest.json");
        var expected = inventory.Select(f => new SituationArtifactFileV1(f.Path, f.Bytes,
            f.Path == "review-candidates.jsonl" ? manifest.Selected.Count : null, f.Sha256)).ToArray();
        if (!SituationReviewCandidateReader.Equal(expected, manifest.Files)) throw new ReviewException("artifact-mismatch");
        var pool = await SituationReviewCandidateReader.ReadPoolAsync(source, policy, cancellationToken);
        if (!SituationReviewCandidateReader.Equal(pool.OrderBy(p => p.SampleId, StringComparer.Ordinal).ToArray(), manifest.SourceIndex))
            throw new ReviewException("artifact-mismatch");
        var plan = new ReviewSelectionPlan(manifest.Selected, manifest.Backups, manifest.QuotaShortfalls, manifest.RoundExceptions);
        SituationReviewCandidateStatistics.ValidatePlan(pool, plan, policy, true);
        var stats = await SituationReviewCandidateReader.ReadJsonAsync<SituationReviewCandidateStatisticsV1>(Path.Combine(candidateRoot, "review-stats.json"), cancellationToken);
        if (!SituationReviewCandidateReader.Equal(stats, SituationReviewCandidateStatistics.Create(pool, plan, policy)))
            throw new ReviewException("artifact-mismatch");
        var records = await SituationReviewCandidateReader.MaterializeAsync(source, manifest.Selected, policy, cancellationToken);
        var candidates = new Dictionary<string, SituationReviewCandidateV1>(StringComparer.Ordinal);
        var position = 0;
        await foreach (var line in SituationReviewCandidateReader.ReadLinesAsync(Path.Combine(candidateRoot, "review-candidates.jsonl"), cancellationToken))
        {
            if (position >= manifest.Selected.Count) throw new ReviewException("artifact-mismatch");
            var selected = manifest.Selected[position++];
            var candidate = SituationTrainingContractJson.DeserializeReviewCandidate(line);
            var rebuilt = SituationReviewCandidateReader.Candidate(selected, records[selected.Entry.SampleId]);
            if (line != SituationCanonicalJson.Serialize(candidate) || !SituationReviewCandidateReader.Equal(candidate, rebuilt))
                throw new ReviewException("artifact-mismatch");
            candidates.Add(candidate.SampleId, candidate);
        }
        if (position != manifest.Selected.Count) throw new ReviewException("artifact-mismatch");
        // Detect changes during verification without rerunning every semantic analysis.
        foreach (var file in source.Manifest.Files)
            await SituationReviewCandidateReader.RequireHashAsync(Path.Combine(source.Root, file.Path), file.Sha256, cancellationToken);
        foreach (var file in manifest.Files)
            await SituationReviewCandidateReader.RequireHashAsync(Path.Combine(candidateRoot, file.Path), file.Sha256, cancellationToken);
        await SituationReviewCandidateReader.RequireHashAsync(Path.Combine(candidateRoot, "manifest.json"), CandidateManifestHash, cancellationToken);
        return new(source, manifest, policy, candidates);
    }

    internal static async Task VerifyHistoricalSourcesAsync(string repositoryRoot, string snapshotRoot, CancellationToken cancellationToken)
    {
        EnsureNoLinks(snapshotRoot);
        var provenancePath = Path.Combine(snapshotRoot, "producer-provenance.json");
        EnsureNoLinks(provenancePath);
        await SituationReviewCandidateReader.RequireHashAsync(provenancePath, CandidateProvenanceHash, cancellationToken);
        var provenance = await SituationReviewCandidateReader.ReadJsonAsync<SituationReviewCandidateProvenanceV1>(provenancePath, cancellationToken);
        foreach (var file in provenance.ConsumerFiles)
        {
            var historicalPath = SituationArtifactIO.ResolveRepositoryPath(snapshotRoot, file.Path, "Historical source");
            EnsureNoLinks(historicalPath);
            await SituationReviewCandidateReader.RequireHashAsync(historicalPath, file.Sha256, cancellationToken);
            // Only these two composition points are allowed to change. New review files have a separate provenance.
            if (file.Path is "apps/api/CsDemoMap.Api.csproj" or "apps/cli/DeveloperCommandDispatcher.cs") continue;
            var currentPath = SituationArtifactIO.ResolveRepositoryPath(repositoryRoot, file.Path, "Current semantic source");
            EnsureNoLinks(currentPath);
            await SituationReviewCandidateReader.RequireHashAsync(currentPath, file.Sha256, cancellationToken);
        }
    }

    private static async Task<SituationReviewCandidateBase> OpenHistoricalBaseAsync(string root, string snapshot, CancellationToken ct)
    {
        root = Path.GetFullPath(root);
        var repository = SituationArtifactIO.FindRepositoryRoot(root);
        await SituationReviewCandidateReader.RequireHashAsync(Path.Combine(root, "manifest.json"), SituationReviewSelectionPolicy.ApprovedDatasetSha256, ct);
        await SituationReviewCandidateReader.RequireHashAsync(Path.Combine(root, "provenance.json"), SituationReviewCandidateReader.BaseProvenanceSha256, ct);
        await SituationReviewCandidateReader.RequireHashAsync(Path.Combine(root, "split.json"), SituationReviewCandidateReader.BaseSplitSha256, ct);
        var manifest = SituationTrainingContractJson.DeserializeManifest(await SituationReviewCandidateReader.ReadTextAsync(Path.Combine(root, "manifest.json"), ct));
        if (manifest.Status != SituationArtifactStatus.Complete || manifest.Mode != SituationTrainingExportMode.Full || !manifest.Trainable ||
            manifest.SplitSha256 != SituationReviewCandidateReader.BaseSplitSha256) throw new ReviewException("artifact-mismatch");
        SituationReviewCandidateReader.RequireFiles(root, ["dev.jsonl", "label-stats.json", "manifest.json", "provenance.json", "split.json", "test.jsonl", "train.jsonl"]);
        foreach (var file in manifest.Files)
        {
            var path = SituationArtifactIO.ResolveRepositoryPath(root, file.Path, "Base artifact");
            if (new FileInfo(path).Length != file.Bytes) throw new ReviewException("artifact-mismatch");
            await SituationReviewCandidateReader.RequireHashAsync(path, file.Sha256, ct);
        }
        await SituationTrainingSchemaRegistry.VerifyAsync(repository, manifest.SchemaFiles, ct);
        var provenance = await SituationReviewCandidateReader.ReadJsonAsync<SituationTrainingProvenanceDocumentV1>(Path.Combine(root, "provenance.json"), ct);
        SituationTrainingProvenanceBuilder.Validate(provenance);
        if (provenance.AnalysisRulesSha256 != SituationAnalysisRuleLoader.LoadFrozen().Sha256 ||
            provenance.SelectionConfigSha256 != SituationTrainingSelectionLoader.LoadFrozen().Sha256 ||
            provenance.InputRepresentationConfigSha256 != SituationPromptRepresentationLoader.LoadFrozen().Sha256)
            throw new ReviewException("artifact-mismatch");
        foreach (var file in provenance.SourceFiles)
        {
            // These two files already differed at the approved step-seven handoff (pinned provenance).
            if (file.Path is "apps/api/CsDemoMap.Api.csproj" or "apps/cli/DeveloperCommandDispatcher.cs") continue;
            // These two step-eight composition changes have exact, independently hash-verified historical copies.
            var sourceRoot = file.Path is "apps/cli/CsDemoMap.Cli.csproj" or "apps/cli/Program.cs" ? snapshot : repository;
            var sourcePath = SituationArtifactIO.ResolveRepositoryPath(sourceRoot, file.Path, "Historical base source");
            EnsureNoLinks(sourcePath);
            await SituationReviewCandidateReader.RequireHashAsync(sourcePath, file.Sha256, ct);
        }
        return new(root, repository, manifest, provenance);
    }

    public async Task<SituationReviewCandidateV1> GetCandidateAsync(string sampleId, int reviewOrdinal, CancellationToken cancellationToken)
    {
        if (!poolById.TryGetValue(sampleId, out var entry)) throw new ReviewException("unknown-sample", 404);
        await materialization.WaitAsync(cancellationToken);
        try
        {
            if (cache.TryGetValue(sampleId, out var found)) return found with { ReviewOrdinal = reviewOrdinal };
            // Source records are verified by their frozen hash, not their line offset alone.
            var selected = new ReviewSelectedEntry(reviewOrdinal, entry);
            var records = await SituationReviewCandidateReader.MaterializeAsync(source, [selected], Policy, cancellationToken);
            var candidate = SituationReviewCandidateReader.Candidate(selected, records[sampleId]);
            cache.Add(sampleId, candidate);
            return candidate;
        }
        finally { materialization.Release(); }
    }

    internal static void EnsureNoLinks(string path)
    {
        var full = Path.GetFullPath(path);
        for (string? current = full; current is not null; current = Path.GetDirectoryName(current))
            if ((Directory.Exists(current) || File.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new ReviewException("artifact-mismatch");
    }

    internal static void ValidateWorkLocation(string work, params string[] inputs)
    {
        EnsureNoLinks(work);
        var workRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(work));
        foreach (var input in inputs)
        {
            var inputRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(input));
            if (string.Equals(workRoot, inputRoot, StringComparison.OrdinalIgnoreCase) ||
                workRoot.StartsWith(inputRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                inputRoot.StartsWith(workRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new ReviewException("artifact-mismatch");
        }
    }
}

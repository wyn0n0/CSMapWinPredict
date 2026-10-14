using CsDemoMap.Api.Services;

namespace CsDemoMap.Cli;

internal static class SituationReviewFreezeCommands
{
    internal static async Task RunAsync(string[] args, CancellationToken ct)
    {
        var verify = args[0] == "--verify-situation-review";
        var optionStart = verify ? 3 : 4;
        if (args.Length < optionStart) throw new ArgumentException("Explicit dataset, work and new output paths are required.");
        var dataset = Path.GetFullPath(args[1]);
        var repository = SituationArtifactIO.FindRepositoryRoot(dataset);
        var candidateRoot = Path.Combine(repository, "datasets/situation-stage4-review-candidates-20260919-r1");
        var snapshot = Path.Combine(repository, "datasets/situation-stage4-review-source-snapshot-20260919-r1");
        string? derived = null, analysisOption = null, approval = null;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = optionStart; i < args.Length; i++)
        {
            var option = args[i];
            if (!seen.Add(option) || ++i >= args.Length) throw new ArgumentException("Invalid freeze option.");
            switch (option)
            {
                case "--candidates": candidateRoot = args[i]; break;
                case "--producer-snapshot": snapshot = args[i]; break;
                case "--approved-content-sha256" when !verify: approval = args[i]; break;
                case "--raycast-candidates": case "--local-peek-candidates": case "--position-prediction-candidates":
                case "--continuous-slope-candidates": case "--verified-exposure-candidates": case "--close-exposure-candidates":
                    if (derived is not null) throw new ArgumentException("Choose one analysis version.");
                    derived = args[i]; analysisOption = option; break;
                default: throw new ArgumentException("Invalid freeze option.");
            }
        }
        var output = Path.GetFullPath(verify ? args[2] : args[3]);
        var inputs = new[] { dataset, candidateRoot, snapshot }.Concat(derived is null ? [] : new[] { derived }).ToArray();
        SituationReviewArtifactLoader.ValidateWorkLocation(output, inputs);
        if (!verify)
        {
            SituationReviewArtifactLoader.ValidateWorkLocation(args[2], inputs);
            SituationReviewArtifactLoader.ValidateWorkLocation(output, args[2]);
            SituationArtifactIO.EnsureNewOutput(output);
        }
        IReviewCatalog catalog = await SituationReviewArtifactLoader.OpenAsync(dataset, candidateRoot, snapshot, ct);
        if (derived is not null)
        {
            if (!Directory.Exists(derived)) throw new ReviewException("derived-artifact-missing");
            catalog = await SituationRaycastReviewCatalog.OpenAsync(catalog, derived, repository, ct,
                analysisOption == "--local-peek-candidates", analysisOption == "--position-prediction-candidates",
                analysisOption == "--continuous-slope-candidates", analysisOption == "--verified-exposure-candidates",
                analysisOption == "--close-exposure-candidates");
        }
        var baseHash = await SituationArtifactIO.FileSha256Async(Path.Combine(dataset, "manifest.json"), ct);
        ReviewFreezeManifest manifest;
        if (verify) manifest = await SituationReviewFreezer.VerifyAsync(output, catalog, baseHash, ct);
        else
        {
            var provenance = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["provenance/base-manifest.json"] = await File.ReadAllTextAsync(Path.Combine(dataset, "manifest.json"), ct),
                ["provenance/candidate-manifest.json"] = await File.ReadAllTextAsync(Path.Combine(candidateRoot, "manifest.json"), ct)
            };
            if (derived is not null) provenance.Add("provenance/derived-manifest.json", await File.ReadAllTextAsync(Path.Combine(derived, "manifest.json"), ct));
            var sources = Directory.GetFiles(Path.Combine(repository, "apps/api/Features/Situation"), "*", SearchOption.AllDirectories)
                .Concat(Directory.GetFiles(Path.Combine(repository, "apps/cli/SituationReview"), "*.cs"))
                .Concat(Directory.GetFiles(Path.Combine(repository, "schemas/situation"), "*.json"))
                .Append(Path.Combine(repository, "apps/cli/DeveloperCommandDispatcher.cs")).Order(StringComparer.Ordinal);
            var hashes = new List<CsDemoMap.Api.Models.SituationProvenanceSourceFileV1>();
            foreach (var path in sources)
                hashes.Add(new(Path.GetRelativePath(repository, path).Replace('\\', '/'), await SituationArtifactIO.FileSha256Async(path, ct)));
            provenance.Add("provenance/freezer-sources.json", SituationCanonicalJson.Serialize(hashes));
            manifest = await SituationReviewFreezer.ExportAsync(catalog, args[2], output, baseHash, provenance, approval, ct);
        }
        Console.WriteLine($"Review artifact verified: status={manifest.Status}; review={manifest.ReviewStatus}; rows={manifest.Target}; revision={manifest.WorkspaceRevision}.");
        Console.WriteLine($"Content SHA-256: {manifest.ContentSha256}");
        Console.WriteLine("Labels: train=SFT (repeat 5); dev=selection/evaluation; test=final-evaluation only.");
    }
}

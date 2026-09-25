using System.Diagnostics;
using CsDemoMap.Api.Models;

namespace CsDemoMap.Api.Services;

internal static class SituationReviewCandidateProvenance
{
    internal const string HistoricalValidation = "approved-base-full-semantic-readback-reused;selected-records-recomputed-v1";

    internal static async Task<SituationReviewCandidateProvenanceV1> CaptureAsync(SituationReviewCandidateBase source, ReviewSelectionPolicy policy, CancellationToken cancellationToken)
    {
        var beforeHead = SituationArtifactIO.TryReadGitHead(source.RepositoryRoot) ?? throw new InvalidDataException("Git HEAD unavailable.");
        var beforeStatus = await GitStatusAsync(source.RepositoryRoot, cancellationToken);
        var files = await HashConsumersAsync(source.RepositoryRoot, cancellationToken);
        var repeated = await HashConsumersAsync(source.RepositoryRoot, cancellationToken);
        if (!files.SequenceEqual(repeated) || beforeHead != SituationArtifactIO.TryReadGitHead(source.RepositoryRoot) || beforeStatus != await GitStatusAsync(source.RepositoryRoot, cancellationToken))
            throw new IOException("Review consumer sources changed during provenance capture.");
        return new("situation-review-candidate-provenance-v1", SituationReviewSelectionPolicy.ApprovedDatasetSha256,
            SituationReviewCandidateReader.BaseProvenanceSha256, SituationReviewCandidateReader.BaseSplitSha256,
            SituationReviewSelectionPolicy.Hash(policy), HistoricalValidation, beforeHead, beforeStatus.Length != 0,
            policy.SchemaVersion, source.Manifest.Files, files);
    }

    internal static async Task VerifyAsync(SituationReviewCandidateBase source, ReviewSelectionPolicy policy,
        SituationReviewCandidateProvenanceV1 provenance, CancellationToken cancellationToken)
    {
        if (provenance.SchemaVersion != "situation-review-candidate-provenance-v1" ||
            provenance.BaseManifestSha256 != SituationReviewSelectionPolicy.ApprovedDatasetSha256 ||
            provenance.BaseProvenanceSha256 != SituationReviewCandidateReader.BaseProvenanceSha256 ||
            provenance.SplitSha256 != SituationReviewCandidateReader.BaseSplitSha256 ||
            provenance.PolicySha256 != SituationReviewSelectionPolicy.Hash(policy) || provenance.PolicyVersion != policy.SchemaVersion ||
            provenance.HistoricalValidation != HistoricalValidation || !SituationReviewCandidateReader.Equal(provenance.BaseFiles, source.Manifest.Files) ||
            provenance.GitHead != SituationArtifactIO.TryReadGitHead(source.RepositoryRoot) ||
            provenance.GitDirty != ((await GitStatusAsync(source.RepositoryRoot, cancellationToken)).Length != 0) ||
            !provenance.ConsumerFiles.SequenceEqual(await HashConsumersAsync(source.RepositoryRoot, cancellationToken)))
            throw new InvalidDataException("Review consumer/historical provenance mismatch.");
    }

    private static async Task<IReadOnlyList<SituationProvenanceSourceFileV1>> HashConsumersAsync(string repositoryRoot, CancellationToken cancellationToken)
    {
        var paths = Directory.EnumerateFiles(Path.Combine(repositoryRoot, "apps/api/Features/Situation"), "*", SearchOption.AllDirectories)
            .Where(p => p.EndsWith(".cs", StringComparison.Ordinal) || p.EndsWith(".json", StringComparison.Ordinal))
            .Concat(Directory.EnumerateFiles(Path.Combine(repositoryRoot, "schemas/situation"), "*.schema.json"))
            .Concat(new[] { Path.Combine(repositoryRoot, "apps/api/CsDemoMap.Api.csproj"), Path.Combine(repositoryRoot, "apps/cli/DeveloperCommandDispatcher.cs") })
            .Select(p => Path.GetRelativePath(repositoryRoot, p).Replace('\\', '/')).Order(StringComparer.Ordinal).ToArray();
        var files = new List<SituationProvenanceSourceFileV1>();
        foreach (var path in paths)
            files.Add(new(path, await SituationArtifactIO.FileSha256Async(SituationArtifactIO.ResolveRepositoryPath(repositoryRoot, path, "Review consumer"), cancellationToken)));
        return files;
    }

    private static async Task<string> GitStatusAsync(string repositoryRoot, CancellationToken cancellationToken)
    {
        using var process = new Process { StartInfo = new ProcessStartInfo("git")
        {
            WorkingDirectory = repositoryRoot, UseShellExecute = false, RedirectStandardOutput = true,
            RedirectStandardError = true, CreateNoWindow = true
        } };
        process.StartInfo.ArgumentList.Add("status");
        process.StartInfo.ArgumentList.Add("--porcelain=v1");
        process.StartInfo.ArgumentList.Add("--untracked-files=all");
        process.Start();
        var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var error = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        _ = await error;
        if (process.ExitCode != 0) throw new IOException("Unable to inspect review repository state.");
        return await output;
    }
}

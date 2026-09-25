using CsDemoMap.Api.Services;

namespace CsDemoMap.Api.Tests;

internal static class SituationReviewConsumerVerifier
{
    public static async Task VerifyAsync(CancellationToken cancellationToken)
    {
        var repository = SituationArtifactIO.FindRepositoryRoot(Environment.CurrentDirectory);
        var source = Path.Combine(repository, "datasets", "situation-stage4-review-source-snapshot-20260919-r1");
        var provenancePath = Path.Combine(source, "producer-provenance.json");
        var provenance = await SituationReviewCandidateReader.ReadJsonAsync<SituationReviewCandidateProvenanceV1>(
            provenancePath, cancellationToken);
        var temporary = Path.Combine(Path.GetTempPath(), "situation-review-consumer-" + Guid.NewGuid().ToString("N"));
        var checks = 0;
        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidDataException("Review consumer check failed: " + message);
            checks++;
        }
        async Task Fails(Func<Task> action, string message)
        {
            try { await action(); }
            catch (Exception exception) when (exception is ReviewException or IOException or InvalidDataException or System.Text.Json.JsonException)
            { checks++; return; }
            throw new InvalidDataException("Review consumer expected failure: " + message);
        }
        void CopySource(string target)
        {
            Directory.CreateDirectory(target);
            File.Copy(provenancePath, Path.Combine(target, "producer-provenance.json"));
            // Only pinned source files: never copy base JSONL, candidate payloads, or private worktree diagnostics.
            foreach (var entry in provenance.ConsumerFiles)
            {
                var destination = SituationArtifactIO.ResolveRepositoryPath(target, entry.Path, "Test source copy");
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(SituationArtifactIO.ResolveRepositoryPath(source, entry.Path, "Pinned source"), destination);
            }
        }
        Task Verify(string snapshot) => SituationReviewArtifactLoader.VerifyHistoricalSourcesAsync(repository, snapshot, cancellationToken);
        try
        {
            Check(provenance.ConsumerFiles.Count == 83, "historical producer inventory remains the pinned 83 files");
            var reviewRoot = Path.Combine(repository, "apps/api/Features/Situation/Review");
            var newSources = Directory.EnumerateFiles(reviewRoot, "*.cs", SearchOption.AllDirectories)
                .Select(path => Path.GetRelativePath(repository, path).Replace('\\', '/')).ToArray();
            var historicalPaths = provenance.ConsumerFiles.Select(file => file.Path).ToHashSet(StringComparer.Ordinal);
            Check(newSources.Length > 0 && newSources.All(path => !historicalPaths.Contains(path)),
                "current review consumers are independent from historical producer inventory");
            await Verify(source);
            checks++;

            var copy = Path.Combine(temporary, "snapshot");
            CopySource(copy);
            await Verify(copy);
            checks++;
            var syntheticRepository = Path.Combine(temporary, "repository");
            CopySource(syntheticRepository);
            var syntheticReview = Path.Combine(syntheticRepository, "apps/api/Features/Situation/Review/ExtraConsumer.cs");
            Directory.CreateDirectory(Path.GetDirectoryName(syntheticReview)!);
            await File.WriteAllTextAsync(syntheticReview, "// Additional consumer absent from the pinned producer list.\n", cancellationToken);
            await SituationReviewArtifactLoader.VerifyHistoricalSourcesAsync(syntheticRepository, copy, cancellationToken);
            checks++;

            var sourceFile = provenance.ConsumerFiles.First(file => file.Path.EndsWith(".cs", StringComparison.Ordinal));
            var schemaFile = provenance.ConsumerFiles.First(file => file.Path.EndsWith(".schema.json", StringComparison.Ordinal));
            foreach (var entry in new[] { sourceFile, schemaFile })
            {
                var target = SituationArtifactIO.ResolveRepositoryPath(copy, entry.Path, "Test mutation");
                var original = await File.ReadAllBytesAsync(target, cancellationToken);
                try
                {
                    await File.AppendAllTextAsync(target, "\n ", cancellationToken);
                    await Fails(() => Verify(copy), "historical source hash change: " + entry.Path);
                    File.Delete(target);
                    await Fails(() => Verify(copy), "missing historical source: " + entry.Path);
                }
                finally { await File.WriteAllBytesAsync(target, original, cancellationToken); }
            }
            var copiedProvenance = Path.Combine(copy, "producer-provenance.json");
            var provenanceBytes = await File.ReadAllBytesAsync(copiedProvenance, cancellationToken);
            try
            {
                await File.AppendAllTextAsync(copiedProvenance, "\n", cancellationToken);
                await Fails(() => Verify(copy), "provenance remains bound to exact approved bytes");
                File.Delete(copiedProvenance);
                await Fails(() => Verify(copy), "missing historical provenance");
            }
            finally { await File.WriteAllBytesAsync(copiedProvenance, provenanceBytes, cancellationToken); }
            // Altering the current semantic producer is also rejected; adding consumers was accepted above.
            var currentSemantic = SituationArtifactIO.ResolveRepositoryPath(syntheticRepository, sourceFile.Path, "Synthetic current source");
            await File.AppendAllTextAsync(currentSemantic, "\n// semantic producer changed\n", cancellationToken);
            await Fails(() => SituationReviewArtifactLoader.VerifyHistoricalSourcesAsync(syntheticRepository, copy, cancellationToken),
                "current semantic producer must still match frozen producer");

            var input = Path.Combine(temporary, "input");
            var candidates = Path.Combine(temporary, "candidates");
            var work = Path.Combine(temporary, "work");
            SituationReviewArtifactLoader.ValidateWorkLocation(work, input, candidates, copy);
            checks++;
            SituationReviewArtifactLoader.ValidateWorkLocation(input + "-review", input);
            checks++;
            foreach (var pair in new[]
            {
                (Work: input, Input: input),
                (Work: Path.Combine(input, "work"), Input: input),
                (Work: temporary, Input: input),
                (Work: Path.Combine(input, ".", "nested", ".."), Input: input),
                (Work: input + Path.DirectorySeparatorChar, Input: input)
            })
                await Fails(() =>
                {
                    SituationReviewArtifactLoader.ValidateWorkLocation(pair.Work, pair.Input);
                    return Task.CompletedTask;
                }, "work equals, contains, or is nested within protected input");
            await Verify(source);
            checks++;
        }
        finally
        {
            // The only recursive cleanup target is our freshly generated, fully resolved temp subtree.
            var resolved = Path.GetFullPath(temporary);
            var tempRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())) + Path.DirectorySeparatorChar;
            if (!resolved.StartsWith(tempRoot, StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFileName(resolved).StartsWith("situation-review-consumer-", StringComparison.Ordinal))
                throw new InvalidDataException("Unsafe consumer test cleanup target.");
            if (Directory.Exists(resolved)) Directory.Delete(resolved, recursive: true);
        }
        Console.WriteLine($"Situation review consumer checks passed: {checks}");
    }
}

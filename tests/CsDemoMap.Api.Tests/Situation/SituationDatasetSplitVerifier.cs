using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CsDemoMap.Api.Models;

namespace CsDemoMap.Api.Tests;

internal static class SituationDatasetSplitVerifier
{
    public static async Task VerifyAsync(CancellationToken cancellationToken)
    {
        var fixture = await SplitFixture.CreateAsync(cancellationToken);
        var checks = 0;
        try
        {
            var first = await fixture.CreateSplitAsync("stage4-a.json", cancellationToken);
            ValidateHappyPath(first, fixture);
            checks++;

            var second = await fixture.CreateSplitAsync("stage4-b.json", cancellationToken);
            if (first.SplitSha256 != second.SplitSha256 ||
                !File.ReadAllBytes(fixture.Output("stage4-a.json"))
                    .SequenceEqual(File.ReadAllBytes(fixture.Output("stage4-b.json"))))
                throw new InvalidOperationException("Stage-four split output is not byte deterministic.");
            checks++;

            var originalCulture = CultureInfo.CurrentCulture;
            var originalUiCulture = CultureInfo.CurrentUICulture;
            try
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR");
                CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("tr-TR");
                var culture = await fixture.CreateSplitAsync("stage4-culture.json", cancellationToken);
                if (culture.SplitSha256 != first.SplitSha256)
                    throw new InvalidOperationException("Stage-four split depends on current culture.");
            }
            finally
            {
                CultureInfo.CurrentCulture = originalCulture;
                CultureInfo.CurrentUICulture = originalUiCulture;
            }
            checks++;

            checks += await ExpectFailureAsync<IOException>(
                () => fixture.CreateSplitAsync("stage4-a.json", cancellationToken),
                "existing output must not be overwritten");

            checks += await ExpectFailureAsync<InvalidDataException>(
                () => SituationDatasetSplit.CreateAsync(
                    fixture.DemoDirectory,
                    fixture.ParentSplitPath,
                    fixture.Output("bad-parent-hash.json"),
                    new string('f', 64),
                    cancellationToken),
                "parent split hash mismatch must fail");

            await fixture.WriteManifestAsync(status: "incomplete", cancellationToken: cancellationToken);
            checks += await fixture.ExpectCreateFailureAsync(
                "manifest-incomplete.json", "incomplete manifest must fail", cancellationToken);
            await fixture.ResetMetadataAsync(cancellationToken);

            await fixture.WriteManifestAsync(
                semanticVersion: "mirage-semantics-v4.1",
                cancellationToken: cancellationToken);
            checks += await fixture.ExpectCreateFailureAsync(
                "semantic-mismatch.json", "semantic version mismatch must fail", cancellationToken);
            await fixture.ResetMetadataAsync(cancellationToken);

            var missing = Path.Combine(fixture.DemoDirectory, fixture.Entries[^1].FileName);
            var missingBackup = Path.Combine(fixture.Root, "missing-backup.dem");
            File.Move(missing, missingBackup);
            checks += await fixture.ExpectCreateFailureAsync(
                "missing-demo.json", "missing Demo must fail", cancellationToken);
            File.Move(missingBackup, missing);

            var extra = Path.Combine(fixture.DemoDirectory, "extra.dem");
            await File.WriteAllTextAsync(extra, "extra", cancellationToken);
            checks += await fixture.ExpectCreateFailureAsync(
                "extra-demo.json", "extra Demo must fail", cancellationToken);
            File.Delete(extra);

            await File.AppendAllTextAsync(missing, "changed", cancellationToken);
            checks += await fixture.ExpectCreateFailureAsync(
                "changed-demo.json", "changed Demo hash must fail", cancellationToken);
            await fixture.RestoreDemoAsync(fixture.Entries[^1], cancellationToken);

            var duplicateCase = fixture.Entries.ToArray();
            duplicateCase[^1] = duplicateCase[0] with
            {
                FileName = duplicateCase[0].FileName.ToUpperInvariant(),
                MatchId = duplicateCase[^1].MatchId
            };
            await fixture.WriteManifestAsync(entries: duplicateCase, cancellationToken: cancellationToken);
            checks += await fixture.ExpectCreateFailureAsync(
                "duplicate-case.json", "case-insensitive duplicate names must fail", cancellationToken);
            await fixture.ResetMetadataAsync(cancellationToken);

            var traversal = fixture.Entries.ToArray();
            traversal[^1] = traversal[^1] with { FileName = "../escaped.dem" };
            await fixture.WriteManifestAsync(entries: traversal, cancellationToken: cancellationToken);
            checks += await fixture.ExpectCreateFailureAsync(
                "path-traversal.json", "path traversal must fail", cancellationToken);
            await fixture.ResetMetadataAsync(cancellationToken);

            var nonDemo = fixture.Entries.ToArray();
            nonDemo[^1] = nonDemo[^1] with { FileName = "not-a-demo.txt" };
            await fixture.WriteManifestAsync(entries: nonDemo, cancellationToken: cancellationToken);
            checks += await fixture.ExpectCreateFailureAsync(
                "non-demo.json", "non-.dem membership must fail", cancellationToken);
            await fixture.ResetMetadataAsync(cancellationToken);

            var duplicateMatch = fixture.Entries.ToArray();
            duplicateMatch[^1] = duplicateMatch[^1] with { MatchId = duplicateMatch[0].MatchId };
            await fixture.WriteManifestAsync(entries: duplicateMatch, cancellationToken: cancellationToken);
            checks += await fixture.ExpectCreateFailureAsync(
                "duplicate-match.json", "many-to-one match IDs must fail", cancellationToken);
            await fixture.ResetMetadataAsync(cancellationToken);

            var mismatchedValidation = fixture.Validation.ToArray();
            mismatchedValidation[^1] = mismatchedValidation[^1] with { MatchId = new string('a', 64) };
            await fixture.WriteParentSplitAsync(
                validation: mismatchedValidation,
                cancellationToken: cancellationToken);
            checks += await fixture.ExpectCreateFailureAsync(
                "validation-mismatch.json", "parent validation mapping mismatch must fail", cancellationToken);
            await fixture.ResetMetadataAsync(cancellationToken);

            await fixture.WriteParentSplitAsync(
                sourceCount: 86,
                cancellationToken: cancellationToken);
            checks += await fixture.ExpectCreateFailureAsync(
                "count-mismatch.json", "parent 87/79/8 count mismatch must fail", cancellationToken);
            await fixture.ResetMetadataAsync(cancellationToken);

            checks += await VerifySymbolicLinkEscapeAsync(fixture, cancellationToken);
        }
        finally
        {
            Directory.Delete(fixture.Root, recursive: true);
        }

        Console.WriteLine($"Situation stage-four split checks passed: {checks}");
    }

    private static void ValidateHappyPath(
        SituationStageFourSplitResult result,
        SplitFixture fixture)
    {
        var split = result.Split;
        if (split.SchemaVersion != SituationDatasetSplit.SchemaVersion ||
            split.Train.Count != 71 || split.Dev.Count != 8 || split.Test.Count != 8 ||
            split.Train.Any(member => member.Role != "train") ||
            split.Dev.Any(member => member.Role != "dev") ||
            split.Test.Any(member => member.Role != "test"))
            throw new InvalidOperationException("Stage-four split counts or roles are invalid.");

        var expectedTest = fixture.Validation.Select(entry => entry.FileName)
            .Order(StringComparer.Ordinal).ToArray();
        if (!split.Test.Select(member => member.FileName).SequenceEqual(expectedTest))
            throw new InvalidOperationException("Parent validation members were not mapped exactly to test.");

        var validationNames = fixture.Validation.Select(entry => entry.FileName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var expectedDev = fixture.Entries
            .Where(entry => !validationNames.Contains(entry.FileName))
            .Select(entry => new
            {
                entry.FileName,
                Digest = Sha256($"{SituationDatasetSplit.DomainSeparator}|{SituationDatasetSplit.Seed}|{entry.MatchId}")
            })
            .OrderBy(item => item.Digest, StringComparer.Ordinal)
            .ThenBy(item => item.FileName, StringComparer.Ordinal)
            .Take(8)
            .Select(item => item.FileName)
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (!split.Dev.Select(member => member.FileName).SequenceEqual(expectedDev))
            throw new InvalidOperationException("Dev membership does not follow the frozen digest algorithm.");

        var all = split.Train.Concat(split.Dev).Concat(split.Test).ToArray();
        if (all.Select(member => member.FileName).Distinct(StringComparer.OrdinalIgnoreCase).Count() != 87 ||
            all.Select(member => member.MatchId).Distinct(StringComparer.Ordinal).Count() != 87)
            throw new InvalidOperationException("Stage-four split membership overlaps.");

        var outputText = File.ReadAllText(fixture.Output("stage4-a.json"));
        if (outputText.Contains(fixture.Root, StringComparison.OrdinalIgnoreCase) ||
            outputText.Contains("sourceDirectory", StringComparison.Ordinal))
            throw new InvalidOperationException("Stage-four split leaked an absolute input path.");
    }

    private static async Task<int> VerifySymbolicLinkEscapeAsync(
        SplitFixture fixture,
        CancellationToken cancellationToken)
    {
        var link = Path.Combine(fixture.DatasetDirectory, "split-link.json");
        try
        {
            File.CreateSymbolicLink(link, fixture.ParentSplitPath);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            Console.WriteLine("Situation stage-four split symlink check skipped: symbolic links are unavailable.");
            return 0;
        }

        try
        {
            return await ExpectFailureAsync<InvalidDataException>(
                async () =>
                {
                    var hash = await SituationArtifactIO.FileSha256Async(link, cancellationToken);
                    await SituationDatasetSplit.CreateAsync(
                        fixture.DemoDirectory,
                        link,
                        fixture.Output("split-link-output.json"),
                        hash,
                        cancellationToken);
                },
                "symbolic-link parent split must fail");
        }
        finally
        {
            File.Delete(link);
        }
    }

    private static async Task<int> ExpectFailureAsync<TException>(Func<Task> action, string label)
        where TException : Exception
    {
        try
        {
            await action();
        }
        catch (TException)
        {
            return 1;
        }
        throw new InvalidOperationException($"Situation stage-four split check failed: {label}.");
    }

    private static string Sha256(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    internal sealed record FixtureEntry(string FileName, string MatchId, string Content);

    private sealed class SplitFixture
    {
        private SplitFixture(
            string root,
            string demoDirectory,
            string datasetDirectory,
            string parentSplitPath,
            FixtureEntry[] entries)
        {
            Root = root;
            DemoDirectory = demoDirectory;
            DatasetDirectory = datasetDirectory;
            ParentSplitPath = parentSplitPath;
            Entries = entries;
            Validation = entries.Take(8).ToArray();
        }

        internal string Root { get; }
        internal string DemoDirectory { get; }
        internal string DatasetDirectory { get; }
        internal string ParentSplitPath { get; }
        internal FixtureEntry[] Entries { get; }
        internal FixtureEntry[] Validation { get; }

        internal static async Task<SplitFixture> CreateAsync(CancellationToken cancellationToken)
        {
            var root = Path.Combine(Path.GetTempPath(), $"situation-stage4-split-{Guid.NewGuid():N}");
            var demoDirectory = Path.Combine(root, "demos");
            var datasetDirectory = Path.Combine(root, "dataset");
            Directory.CreateDirectory(demoDirectory);
            Directory.CreateDirectory(datasetDirectory);
            var entries = Enumerable.Range(0, 87)
                .Select(index =>
                {
                    var content = $"synthetic-demo-{index:D3}";
                    return new FixtureEntry($"demo-{index:D3}.dem", Sha256(content), content);
                })
                .ToArray();
            foreach (var entry in entries)
                await File.WriteAllTextAsync(
                    Path.Combine(demoDirectory, entry.FileName),
                    entry.Content,
                    cancellationToken);
            var fixture = new SplitFixture(
                root,
                demoDirectory,
                datasetDirectory,
                Path.Combine(datasetDirectory, "parent-split.json"),
                entries);
            await fixture.ResetMetadataAsync(cancellationToken);
            return fixture;
        }

        internal string Output(string fileName) => Path.Combine(Root, fileName);

        internal async Task<SituationStageFourSplitResult> CreateSplitAsync(
            string outputName,
            CancellationToken cancellationToken)
        {
            var parentHash = await SituationArtifactIO.FileSha256Async(
                ParentSplitPath,
                cancellationToken);
            return await SituationDatasetSplit.CreateAsync(
                DemoDirectory,
                ParentSplitPath,
                Output(outputName),
                parentHash,
                cancellationToken);
        }

        internal async Task<int> ExpectCreateFailureAsync(
            string outputName,
            string label,
            CancellationToken cancellationToken)
        {
            var output = Output(outputName);
            var result = await ExpectFailureAsync<InvalidDataException>(
                () => CreateSplitAsync(outputName, cancellationToken),
                label);
            if (File.Exists(output))
                throw new InvalidOperationException("A rejected split unexpectedly created its output file.");
            return result;
        }

        internal async Task ResetMetadataAsync(CancellationToken cancellationToken)
        {
            await WriteManifestAsync(cancellationToken: cancellationToken);
            await WriteParentSplitAsync(cancellationToken: cancellationToken);
        }

        internal async Task RestoreDemoAsync(FixtureEntry entry, CancellationToken cancellationToken) =>
            await File.WriteAllTextAsync(
                Path.Combine(DemoDirectory, entry.FileName),
                entry.Content,
                cancellationToken);

        internal Task WriteManifestAsync(
            string status = "complete",
            string semanticVersion = SituationDatasetSplit.ParentSemanticVersion,
            IReadOnlyList<FixtureEntry>? entries = null,
            CancellationToken cancellationToken = default) =>
            SituationArtifactIO.WriteJsonAsync(
                Path.Combine(DatasetDirectory, "manifest.json"),
                new
                {
                    schemaVersion = 4,
                    semanticVersion,
                    status,
                    matches = (entries ?? Entries).Select(entry => new
                    {
                        file = entry.FileName,
                        entry.MatchId,
                        report = new { rowCount = 1 }
                    })
                },
                cancellationToken);

        internal Task WriteParentSplitAsync(
            int sourceCount = 87,
            int trainingCount = 79,
            int validationCount = 8,
            IReadOnlyList<FixtureEntry>? validation = null,
            CancellationToken cancellationToken = default) =>
            SituationArtifactIO.WriteJsonAsync(
                ParentSplitPath,
                new
                {
                    sourceDirectory = "Z:\\stale-path-that-must-not-be-used",
                    sourceDemoCount = sourceCount,
                    trainingDemoCount = trainingCount,
                    validationDemoCount = validationCount,
                    randomSeed = 42,
                    selectionMethod = "synthetic frozen parent split",
                    validation = (validation ?? Validation).Select(entry => new
                    {
                        entry.FileName,
                        entry.MatchId
                    })
                },
                cancellationToken);
    }
}

using static CsDemoMap.Api.Services.SituationArtifactIO;

namespace CsDemoMap.Api.Services;

internal static class SituationStageTwoAcceptanceVerifier
{
    public static async Task VerifyAsync(CancellationToken cancellationToken)
    {
        var checks = 0;
        var root = Path.Combine(
            Path.GetTempPath(), $"situation-stage-two-split-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var manifestEntries = Enumerable.Range(1, 87)
                .Select(index => new
                {
                    file = $"demo-{index:D2}.dem",
                    matchId = index.ToString("x64", System.Globalization.CultureInfo.InvariantCulture)
                })
                .ToArray();
            await WriteJsonAsync(Path.Combine(root, "manifest.json"), new
            {
                status = "complete",
                matches = manifestEntries
            }, cancellationToken);

            var validation = manifestEntries.Take(8)
                .Select(item => new { fileName = item.file, item.matchId })
                .ToArray();
            var splitPath = Path.Combine(root, "split.json");
            await WriteSplitAsync(splitPath, root, validation, cancellationToken);
            var splitSha256 = await FileSha256Async(splitPath, cancellationToken);
            var frozen = await SituationFrozenDatasetLoader.LoadAsync(
                splitPath, splitSha256, cancellationToken);
            var inventory = await InventoryFilesAsync(root, cancellationToken, "manifest.json");
            if (frozen.TrainingFiles.Count != 79 || frozen.ValidationFiles.Count != 8 ||
                frozen.MatchIds.Count != 87 || frozen.SplitSha256 != splitSha256 ||
                frozen.ManifestSha256 != await FileSha256Async(
                    Path.Combine(root, "manifest.json"), cancellationToken) ||
                inventory.Any(file => file.Path == "manifest.json"))
                throw new InvalidOperationException("Shared situation artifact infrastructure check failed.");
            await SituationStageTwoAcceptance.VerifyFrozenSplitAsync(
                splitPath, splitSha256, cancellationToken);
            checks++;

            checks += await ExpectInvalidAsync(
                () => SituationStageTwoAcceptance.VerifyFrozenSplitAsync(
                    splitPath, new string('f', 64), cancellationToken),
                "a split hash outside the frozen request must be rejected");

            var duplicateValidation = validation.ToArray();
            duplicateValidation[^1] = duplicateValidation[0];
            await WriteSplitAsync(splitPath, root, duplicateValidation, cancellationToken);
            var duplicateSplitSha256 = await FileSha256Async(splitPath, cancellationToken);
            checks += await ExpectInvalidAsync(
                () => SituationStageTwoAcceptance.VerifyFrozenSplitAsync(
                    splitPath, duplicateSplitSha256, cancellationToken),
                "validation membership must contain eight unique demos");

            var mismatchedValidation = validation.ToArray();
            mismatchedValidation[^1] = new
            {
                fileName = mismatchedValidation[^1].fileName,
                matchId = new string('a', 64)
            };
            await WriteSplitAsync(splitPath, root, mismatchedValidation, cancellationToken);
            var mismatchedSplitSha256 = await FileSha256Async(splitPath, cancellationToken);
            checks += await ExpectInvalidAsync(
                () => SituationStageTwoAcceptance.VerifyFrozenSplitAsync(
                    splitPath, mismatchedSplitSha256, cancellationToken),
                "validation hashes must match the dataset manifest");

            await WriteJsonAsync(Path.Combine(root, "manifest.json"), new
            {
                status = "complete",
                matches = manifestEntries.Take(86)
            }, cancellationToken);
            await WriteSplitAsync(splitPath, root, validation, cancellationToken);
            var incompleteManifestSplitSha256 = await FileSha256Async(splitPath, cancellationToken);
            checks += await ExpectInvalidAsync(
                () => SituationStageTwoAcceptance.VerifyFrozenSplitAsync(
                    splitPath, incompleteManifestSplitSha256, cancellationToken),
                "the manifest must contain all 87 unique demos");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }

        Console.WriteLine($"Situation stage-two frozen-split checks passed: {checks}");
    }

    private static Task WriteSplitAsync(
        string path,
        string sourceDirectory,
        object validation,
        CancellationToken cancellationToken) => WriteJsonAsync(path, new
        {
            sourceDirectory,
            sourceDemoCount = 87,
            trainingDemoCount = 79,
            validationDemoCount = 8,
            validation
        }, cancellationToken);

    private static async Task<int> ExpectInvalidAsync(Func<Task> action, string label)
    {
        try
        {
            await action();
        }
        catch (InvalidDataException)
        {
            return 1;
        }
        throw new InvalidOperationException($"Situation stage-two split check failed: {label}.");
    }
}

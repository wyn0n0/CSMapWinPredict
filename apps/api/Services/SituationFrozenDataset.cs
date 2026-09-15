using System.Text.Json;

namespace CsDemoMap.Api.Services;

internal sealed record SituationFrozenDataset(
    string DatasetDirectory,
    string SourceDirectory,
    IReadOnlySet<string> TrainingFiles,
    IReadOnlySet<string> ValidationFiles,
    IReadOnlyDictionary<string, string> MatchIds,
    string SplitSha256,
    string ManifestSha256);

internal static class SituationFrozenDatasetLoader
{
    private const int SourceDemoCount = 87;
    private const int TrainingDemoCount = 79;
    private const int ValidationDemoCount = 8;

    internal static async Task<SituationFrozenDataset> LoadAsync(
        string splitPath,
        string? expectedSplitSha256,
        CancellationToken cancellationToken)
    {
        var splitFullPath = Path.GetFullPath(splitPath);
        var splitBytes = await File.ReadAllBytesAsync(splitFullPath, cancellationToken);
        var splitSha256 = SituationArtifactIO.Sha256(splitBytes);
        if (expectedSplitSha256 is not null &&
            !string.Equals(splitSha256, expectedSplitSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Frozen split hash does not match the request.");

        using var split = JsonDocument.Parse(splitBytes);
        var splitRoot = split.RootElement;
        if (splitRoot.GetProperty("sourceDemoCount").GetInt32() != SourceDemoCount ||
            splitRoot.GetProperty("trainingDemoCount").GetInt32() != TrainingDemoCount ||
            splitRoot.GetProperty("validationDemoCount").GetInt32() != ValidationDemoCount)
            throw new InvalidDataException("Situation artifacts require the frozen 79/8 split.");

        var sourceDirectoryValue = splitRoot.GetProperty("sourceDirectory").GetString();
        if (string.IsNullOrWhiteSpace(sourceDirectoryValue))
            throw new InvalidDataException("Frozen split sourceDirectory is missing.");
        var sourceDirectory = Path.GetFullPath(sourceDirectoryValue);
        var validationEntries = splitRoot.GetProperty("validation").EnumerateArray()
            .Select(item => new DatasetEntry(
                item.GetProperty("fileName").GetString(),
                item.GetProperty("matchId").GetString()))
            .ToArray();
        ValidateEntries(validationEntries, ValidationDemoCount, "Frozen split must contain eight unique validation demos.");
        var validationFiles = validationEntries.Select(item => item.FileName!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var datasetDirectory = Path.GetDirectoryName(splitFullPath)
            ?? throw new InvalidDataException("Frozen split directory is missing.");
        var manifestPath = Path.Combine(datasetDirectory, "manifest.json");
        var manifestBytes = await File.ReadAllBytesAsync(manifestPath, cancellationToken);
        using var manifest = JsonDocument.Parse(manifestBytes);
        if (manifest.RootElement.GetProperty("status").GetString() != "complete")
            throw new InvalidDataException("Frozen dataset manifest is incomplete.");
        var manifestEntries = manifest.RootElement.GetProperty("matches").EnumerateArray()
            .Select(item => new DatasetEntry(
                item.GetProperty("file").GetString(),
                item.GetProperty("matchId").GetString()))
            .ToArray();
        ValidateEntries(manifestEntries, SourceDemoCount, "Frozen dataset manifest must contain 87 unique demos.");
        var matchIds = manifestEntries.ToDictionary(
            item => item.FileName!,
            item => item.MatchId!,
            StringComparer.OrdinalIgnoreCase);
        if (matchIds.Count - validationFiles.Count != TrainingDemoCount || validationEntries.Any(item =>
                !matchIds.TryGetValue(item.FileName!, out var matchId) ||
                !string.Equals(matchId, item.MatchId, StringComparison.Ordinal)))
            throw new InvalidDataException("Frozen split and dataset manifest membership do not match.");
        var trainingFiles = matchIds.Keys.Where(file => !validationFiles.Contains(file))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return new(
            datasetDirectory,
            sourceDirectory,
            trainingFiles,
            validationFiles,
            matchIds,
            splitSha256,
            SituationArtifactIO.Sha256(manifestBytes));
    }

    private static void ValidateEntries(
        IReadOnlyList<DatasetEntry> entries,
        int expectedCount,
        string error)
    {
        if (entries.Count != expectedCount ||
            entries.Any(item => string.IsNullOrWhiteSpace(item.FileName) ||
                                Path.GetFileName(item.FileName) != item.FileName ||
                                !SituationArtifactIO.IsSha256(item.MatchId)) ||
            entries.Select(item => item.FileName!).Distinct(StringComparer.OrdinalIgnoreCase).Count() != expectedCount ||
            entries.Select(item => item.MatchId!).Distinct(StringComparer.Ordinal).Count() != expectedCount)
            throw new InvalidDataException(error);
    }

    private sealed record DatasetEntry(string? FileName, string? MatchId);
}

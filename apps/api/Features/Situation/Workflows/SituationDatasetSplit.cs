using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CsDemoMap.Api.Models;

namespace CsDemoMap.Api.Services;

internal static class SituationDatasetSplit
{
    internal const string SchemaVersion = SituationTrainingContractVersions.Split;
    internal const string AlgorithmVersion = SituationTrainingContractVersions.Split;
    internal const string DomainSeparator = "situation-stage4-dev-v1";
    internal const int Seed = 42;
    internal const string ParentSemanticVersion = "mirage-semantics-v4.2";
    internal const string FrozenParentSplitSha256 =
        "b176d96c063921f6a1a8718c700fb6d193903693539e4d1b447e37fca4aa43d8";

    private const int SourceDemoCount = 87;
    private const int ParentTrainingDemoCount = 79;
    private const int ParentValidationDemoCount = 8;
    private const int TrainDemoCount = 71;
    private const int DevDemoCount = 8;
    private const int TestDemoCount = 8;
    private static readonly UTF8Encoding Utf8NoBom = new(false);
    private static readonly JsonSerializerOptions OutputJsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    internal static Task<SituationStageFourSplitResult> CreateAsync(
        string demoDirectory,
        string frozenParentSplitPath,
        string outputPath,
        CancellationToken cancellationToken) =>
        CreateAsync(
            demoDirectory,
            frozenParentSplitPath,
            outputPath,
            FrozenParentSplitSha256,
            cancellationToken);

    internal static async Task<SituationStageFourSplitResult> CreateAsync(
        string demoDirectory,
        string frozenParentSplitPath,
        string outputPath,
        string expectedParentSplitSha256,
        CancellationToken cancellationToken)
    {
        if (!SituationArtifactIO.IsSha256(expectedParentSplitSha256, lowercaseOnly: true))
            throw new ArgumentException("Expected parent split SHA-256 is invalid.", nameof(expectedParentSplitSha256));

        var demoDirectoryPath = NormalizeExistingDirectory(demoDirectory, "Demo directory");
        var parentSplitPath = NormalizeExistingFile(frozenParentSplitPath, "Frozen parent split");
        var outputFilePath = NormalizeNewOutputFile(outputPath);
        RejectReparsePoint(new DirectoryInfo(demoDirectoryPath), "Demo directory");
        RejectReparsePoint(new FileInfo(parentSplitPath), "Frozen parent split");

        var parentSplitBytes = await File.ReadAllBytesAsync(parentSplitPath, cancellationToken);
        var parentSplitSha256 = SituationArtifactIO.Sha256(parentSplitBytes);
        if (!string.Equals(parentSplitSha256, expectedParentSplitSha256, StringComparison.Ordinal))
            throw new InvalidDataException("Frozen parent split SHA-256 does not match the approved baseline.");
        var parentSplit = ParseParentSplit(parentSplitBytes);

        var parentDirectory = Path.GetDirectoryName(parentSplitPath)
            ?? throw new InvalidDataException("Frozen parent split directory is missing.");
        var manifestPath = Path.Combine(parentDirectory, "manifest.json");
        if (!File.Exists(manifestPath))
            throw new InvalidDataException("The schema v4.2 manifest adjacent to the parent split is missing.");
        RejectReparsePoint(new FileInfo(manifestPath), "Parent manifest");
        var manifestBytes = await File.ReadAllBytesAsync(manifestPath, cancellationToken);
        var manifestSha256 = SituationArtifactIO.Sha256(manifestBytes);
        var manifestEntries = ParseManifest(manifestBytes);

        ValidateParentMembership(parentSplit, manifestEntries);
        await ValidateDemoDirectoryAsync(demoDirectoryPath, manifestEntries, cancellationToken);

        var testNames = parentSplit.Validation
            .Select(entry => entry.FileName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var parentTraining = manifestEntries
            .Where(entry => !testNames.Contains(entry.FileName))
            .ToArray();
        var devNames = parentTraining
            .Select(entry => new
            {
                Entry = entry,
                Digest = SituationArtifactIO.Sha256(
                    $"{DomainSeparator}|{Seed}|{entry.MatchId}")
            })
            .OrderBy(item => item.Digest, StringComparer.Ordinal)
            .ThenBy(item => item.Entry.FileName, StringComparer.Ordinal)
            .Take(DevDemoCount)
            .Select(item => item.Entry.FileName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var train = Members(parentTraining.Where(entry => !devNames.Contains(entry.FileName)), "train");
        var dev = Members(parentTraining.Where(entry => devNames.Contains(entry.FileName)), "dev");
        var test = Members(parentSplit.Validation, "test");
        ValidateStageFourMembership(train, dev, test);

        var repositoryRoot = TryFindRepositoryRoot(parentSplitPath);
        var document = new SituationStageFourSplitDocument(
            SchemaVersion,
            SafeReference(repositoryRoot, parentSplitPath),
            parentSplitSha256,
            SafeReference(repositoryRoot, manifestPath),
            manifestSha256,
            ParentSemanticVersion,
            AlgorithmVersion,
            DomainSeparator,
            Seed,
            "SHA-256",
            "dev: selection digest ordinal, fileName ordinal; members: fileName ordinal",
            SourceDemoCount,
            TrainDemoCount,
            DevDemoCount,
            TestDemoCount,
            train,
            dev,
            test);
        var json = JsonSerializer.Serialize(document, OutputJsonOptions);
        await WriteNewTextAtomicAsync(outputFilePath, json, cancellationToken);

        var writtenBytes = await File.ReadAllBytesAsync(outputFilePath, cancellationToken);
        var written = JsonSerializer.Deserialize<SituationStageFourSplitDocument>(
            writtenBytes,
            OutputJsonOptions) ?? throw new InvalidDataException("Written stage-four split is empty.");
        ValidateDocument(written, parentSplitSha256, manifestSha256);
        return new(written, SituationArtifactIO.Sha256(writtenBytes));
    }

    private static ParentSplit ParseParentSplit(ReadOnlyMemory<byte> bytes)
    {
        using var document = JsonDocument.Parse(bytes);
        var root = document.RootElement;
        RequireObject(root, "Frozen parent split");
        RequireUniqueProperties(root, "Frozen parent split");
        if (ReadRequiredInt(root, "sourceDemoCount") != SourceDemoCount ||
            ReadRequiredInt(root, "trainingDemoCount") != ParentTrainingDemoCount ||
            ReadRequiredInt(root, "validationDemoCount") != ParentValidationDemoCount)
            throw new InvalidDataException("Frozen parent split must contain the approved 87/79/8 counts.");
        if (ReadRequiredInt(root, "randomSeed") != Seed)
            throw new InvalidDataException("Frozen parent split seed does not match the approved baseline.");
        _ = ReadRequiredString(root, "sourceDirectory");
        _ = ReadRequiredString(root, "selectionMethod");

        var validation = ReadRequiredArray(root, "validation")
            .EnumerateArray()
            .Select((item, index) => ParseEntry(item, $"parent validation member {index + 1}"))
            .ToArray();
        ValidateEntries(
            validation,
            ParentValidationDemoCount,
            "Frozen parent split must contain eight unique validation members.");
        return new(validation);
    }

    private static DatasetEntry[] ParseManifest(ReadOnlyMemory<byte> bytes)
    {
        using var document = JsonDocument.Parse(bytes);
        var root = document.RootElement;
        RequireObject(root, "Parent manifest");
        RequireUniqueProperties(root, "Parent manifest");
        if (ReadRequiredInt(root, "schemaVersion") != 4 ||
            !string.Equals(
                ReadRequiredString(root, "semanticVersion"),
                ParentSemanticVersion,
                StringComparison.Ordinal))
            throw new InvalidDataException("Parent manifest must use schema v4.2 semantics.");
        if (!string.Equals(ReadRequiredString(root, "status"), "complete", StringComparison.Ordinal))
            throw new InvalidDataException("Parent manifest must have status=complete.");
        var matches = ReadRequiredArray(root, "matches")
            .EnumerateArray()
            .Select((item, index) => ParseManifestEntry(item, index + 1))
            .ToArray();
        ValidateEntries(
            matches,
            SourceDemoCount,
            "Parent manifest must contain 87 unique Demo files and match IDs.");
        return matches;
    }

    private static DatasetEntry ParseManifestEntry(JsonElement item, int index)
    {
        RequireObject(item, $"manifest match {index}");
        RequireUniqueProperties(item, $"manifest match {index}");
        return new(
            ReadRequiredString(item, "file"),
            ReadRequiredString(item, "matchId"));
    }

    private static DatasetEntry ParseEntry(JsonElement item, string description)
    {
        RequireObject(item, description);
        RequireUniqueProperties(item, description);
        return new(
            ReadRequiredString(item, "fileName"),
            ReadRequiredString(item, "matchId"));
    }

    private static void ValidateParentMembership(
        ParentSplit parentSplit,
        IReadOnlyList<DatasetEntry> manifestEntries)
    {
        var manifestByFile = manifestEntries.ToDictionary(
            entry => entry.FileName,
            StringComparer.OrdinalIgnoreCase);
        foreach (var validation in parentSplit.Validation)
        {
            if (!manifestByFile.TryGetValue(validation.FileName, out var manifest) ||
                !string.Equals(validation.FileName, manifest.FileName, StringComparison.Ordinal) ||
                !string.Equals(validation.MatchId, manifest.MatchId, StringComparison.Ordinal))
                throw new InvalidDataException("Parent split validation members do not match the manifest.");
        }
        if (manifestEntries.Count - parentSplit.Validation.Count != ParentTrainingDemoCount)
            throw new InvalidDataException("Parent split training membership is not exactly 79 demos.");
    }

    private static async Task ValidateDemoDirectoryAsync(
        string demoDirectory,
        IReadOnlyList<DatasetEntry> manifestEntries,
        CancellationToken cancellationToken)
    {
        var demoFiles = Directory.EnumerateFiles(demoDirectory, "*", SearchOption.TopDirectoryOnly)
            .Where(path => string.Equals(Path.GetExtension(path), ".dem", StringComparison.OrdinalIgnoreCase))
            .Select(path => new FileInfo(path))
            .ToArray();
        if (demoFiles.Length != SourceDemoCount ||
            demoFiles.Select(file => file.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != SourceDemoCount)
            throw new InvalidDataException("Demo directory must contain exactly 87 case-insensitively unique .dem files.");

        var actualByName = demoFiles.ToDictionary(file => file.Name, StringComparer.OrdinalIgnoreCase);
        var manifestNames = manifestEntries.Select(entry => entry.FileName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (actualByName.Keys.Any(name => !manifestNames.Contains(name)) ||
            manifestEntries.Any(entry => !actualByName.ContainsKey(entry.FileName)))
            throw new InvalidDataException("Demo directory contains missing or extra .dem files.");

        foreach (var entry in manifestEntries.OrderBy(item => item.FileName, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var file = actualByName[entry.FileName];
            if (!string.Equals(file.Name, entry.FileName, StringComparison.Ordinal))
                throw new InvalidDataException("Demo filename casing differs from the frozen manifest.");
            EnsureResolvedDirectChild(demoDirectory, file);
            var sha256 = await SituationArtifactIO.FileSha256Async(file.FullName, cancellationToken);
            if (!string.Equals(sha256, entry.MatchId, StringComparison.Ordinal))
                throw new InvalidDataException("A Demo SHA-256 does not match its frozen match ID.");
        }
    }

    private static void ValidateEntries(
        IReadOnlyList<DatasetEntry> entries,
        int expectedCount,
        string error)
    {
        if (entries.Count != expectedCount || entries.Any(entry =>
                string.IsNullOrWhiteSpace(entry.FileName) ||
                !string.Equals(Path.GetFileName(entry.FileName), entry.FileName, StringComparison.Ordinal) ||
                !string.Equals(Path.GetExtension(entry.FileName), ".dem", StringComparison.OrdinalIgnoreCase) ||
                !SituationArtifactIO.IsSha256(entry.MatchId, lowercaseOnly: true)) ||
            entries.Select(entry => entry.FileName)
                .Distinct(StringComparer.OrdinalIgnoreCase).Count() != expectedCount ||
            entries.Select(entry => entry.MatchId)
                .Distinct(StringComparer.Ordinal).Count() != expectedCount)
            throw new InvalidDataException(error);
    }

    private static IReadOnlyList<SituationStageFourSplitMember> Members(
        IEnumerable<DatasetEntry> entries,
        string role) => entries
        .OrderBy(entry => entry.FileName, StringComparer.Ordinal)
        .Select(entry => new SituationStageFourSplitMember(entry.FileName, entry.MatchId, role))
        .ToArray();

    private static void ValidateStageFourMembership(
        IReadOnlyList<SituationStageFourSplitMember> train,
        IReadOnlyList<SituationStageFourSplitMember> dev,
        IReadOnlyList<SituationStageFourSplitMember> test)
    {
        if (train.Count != TrainDemoCount || dev.Count != DevDemoCount || test.Count != TestDemoCount)
            throw new InvalidDataException("Stage-four split must contain exactly 71 train, 8 dev, and 8 test demos.");
        var all = train.Concat(dev).Concat(test).ToArray();
        if (all.Length != SourceDemoCount ||
            all.Select(member => member.FileName).Distinct(StringComparer.OrdinalIgnoreCase).Count() != SourceDemoCount ||
            all.Select(member => member.MatchId).Distinct(StringComparer.Ordinal).Count() != SourceDemoCount ||
            train.Any(member => member.Role != "train") ||
            dev.Any(member => member.Role != "dev") ||
            test.Any(member => member.Role != "test") ||
            !IsOrdinalSorted(train) || !IsOrdinalSorted(dev) || !IsOrdinalSorted(test))
            throw new InvalidDataException("Stage-four split membership is invalid or non-deterministic.");
    }

    private static void ValidateDocument(
        SituationStageFourSplitDocument document,
        string parentSplitSha256,
        string manifestSha256)
    {
        if (document.SchemaVersion != SchemaVersion ||
            document.AlgorithmVersion != AlgorithmVersion ||
            document.DomainSeparator != DomainSeparator ||
            document.Seed != Seed ||
            document.HashAlgorithm != "SHA-256" ||
            document.ParentSemanticVersion != ParentSemanticVersion ||
            document.ParentSplitSha256 != parentSplitSha256 ||
            document.ParentManifestSha256 != manifestSha256 ||
            document.SourceDemoCount != SourceDemoCount ||
            document.TrainDemoCount != TrainDemoCount ||
            document.DevDemoCount != DevDemoCount ||
            document.TestDemoCount != TestDemoCount ||
            Path.IsPathRooted(document.ParentSplitReference) ||
            Path.IsPathRooted(document.ParentManifestReference))
            throw new InvalidDataException("Written stage-four split metadata is invalid.");
        ValidateStageFourMembership(document.Train, document.Dev, document.Test);
    }

    private static bool IsOrdinalSorted(IReadOnlyList<SituationStageFourSplitMember> members) =>
        members.Select(member => member.FileName)
            .SequenceEqual(members.Select(member => member.FileName).Order(StringComparer.Ordinal));

    private static string NormalizeExistingDirectory(string path, string description)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException($"{description} is required.");
        var fullPath = Path.GetFullPath(path);
        if (!Directory.Exists(fullPath))
            throw new DirectoryNotFoundException($"{description} does not exist.");
        return Path.TrimEndingDirectorySeparator(fullPath);
    }

    private static string NormalizeExistingFile(string path, string description)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException($"{description} is required.");
        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
            throw new FileNotFoundException($"{description} does not exist.");
        return fullPath;
    }

    private static string NormalizeNewOutputFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("Output path is required.", nameof(path));
        var fullPath = Path.GetFullPath(path);
        if (Directory.Exists(fullPath) || File.Exists(fullPath))
            throw new IOException("Stage-four split output already exists.");
        var parent = Path.GetDirectoryName(fullPath);
        if (parent is null || !Directory.Exists(parent))
            throw new DirectoryNotFoundException("Stage-four split output directory does not exist.");
        return fullPath;
    }

    private static void RejectReparsePoint(FileSystemInfo info, string description)
    {
        info.Refresh();
        if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException($"{description} cannot be a symbolic link or reparse point.");
    }

    private static void EnsureResolvedDirectChild(string demoDirectory, FileInfo file)
    {
        SituationArtifactIO.EnsureDirectChild(
            demoDirectory,
            file.FullName,
            "Every Demo must be a direct child of the explicit Demo directory.");
        if ((file.Attributes & FileAttributes.ReparsePoint) == 0)
            return;
        var target = file.ResolveLinkTarget(returnFinalTarget: true)
            ?? throw new InvalidDataException("A Demo symbolic link target cannot be resolved.");
        SituationArtifactIO.EnsureDirectChild(
            demoDirectory,
            target.FullName,
            "A Demo symbolic link escapes the explicit Demo directory.");
    }

    private static string? TryFindRepositoryRoot(string start)
    {
        try
        {
            return SituationArtifactIO.FindRepositoryRoot(start);
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }
    }

    private static string SafeReference(string? repositoryRoot, string path)
    {
        if (repositoryRoot is not null)
        {
            var relative = Path.GetRelativePath(repositoryRoot, path);
            if (relative != ".." &&
                !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
                !Path.IsPathRooted(relative))
                return relative.Replace('\\', '/');
        }
        return Path.GetFileName(path);
    }

    private static async Task WriteNewTextAtomicAsync(
        string path,
        string value,
        CancellationToken cancellationToken)
    {
        var temporary = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = new FileStream(
                             temporary,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             64 * 1024,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            await using (var writer = new StreamWriter(stream, Utf8NoBom))
            {
                await writer.WriteAsync(value.AsMemory(), cancellationToken);
                await writer.FlushAsync(cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }
            File.Move(temporary, path, overwrite: false);
        }
        catch
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
            throw;
        }
    }

    private static void RequireObject(JsonElement value, string description)
    {
        if (value.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException($"{description} must be a JSON object.");
    }

    private static void RequireUniqueProperties(JsonElement value, string description)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
        {
            if (!names.Add(property.Name))
                throw new InvalidDataException($"{description} contains a duplicate JSON property.");
        }
    }

    private static string ReadRequiredString(JsonElement value, string name)
    {
        if (!value.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.String)
            throw new InvalidDataException($"Required string property is missing: {name}.");
        var result = property.GetString();
        if (string.IsNullOrWhiteSpace(result))
            throw new InvalidDataException($"Required string property is empty: {name}.");
        return result;
    }

    private static int ReadRequiredInt(JsonElement value, string name)
    {
        if (!value.TryGetProperty(name, out var property) || !property.TryGetInt32(out var result))
            throw new InvalidDataException($"Required integer property is missing: {name}.");
        return result;
    }

    private static JsonElement ReadRequiredArray(JsonElement value, string name)
    {
        if (!value.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException($"Required array property is missing: {name}.");
        return property;
    }

    private sealed record ParentSplit(IReadOnlyList<DatasetEntry> Validation);

    private sealed record DatasetEntry(string FileName, string MatchId);
}

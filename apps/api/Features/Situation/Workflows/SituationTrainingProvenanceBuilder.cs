using System.Diagnostics;
using System.Text.RegularExpressions;
using CsDemoMap.Api.Models;

namespace CsDemoMap.Api.Services;

internal sealed record SituationTrainingProvenanceDocumentV1(
    string SchemaVersion,
    string GitHead,
    bool GitDirty,
    string SplitSha256,
    string ParentSplitSha256,
    string ParentManifestReference,
    string ParentManifestSha256,
    string AnalysisRulesSha256,
    string SelectionConfigSha256,
    string InputRepresentationVersion,
    string InputRepresentationConfigSha256,
    IReadOnlyList<SituationSchemaFileReferenceV1> SchemaFiles,
    IReadOnlyList<SituationProvenanceSourceFileV1> SourceFiles,
    IReadOnlyList<SituationTrainingProvenanceRoundMappingV1> RoundMappings,
    IReadOnlyList<SituationProvenanceDemoMappingV1> DemoMappings);

internal sealed record SituationTrainingProvenanceRoundMappingV1(
    string MatchRef,
    string RoundRef,
    string SemanticRoundId);

/// <summary>
/// Captures the actual working-tree bytes used by an export. Git HEAD is context,
/// not a substitute for content hashes when the tree is dirty.
/// </summary>
internal static class SituationTrainingProvenanceBuilder
{
    internal static async Task<SituationTrainingProvenanceDocumentV1> CreateAsync(
        string repositoryRoot,
        SituationStageFourSplitResult split,
        string analysisRulesSha256,
        string selectionConfigSha256,
        string inputRepresentationConfigSha256,
        IReadOnlyList<SituationSchemaFileReferenceV1> schemaFiles,
        IEnumerable<string> relevantSourcePaths,
        CancellationToken cancellationToken) => await CreateAsync(
        repositoryRoot,
        split,
        analysisRulesSha256,
        selectionConfigSha256,
        inputRepresentationConfigSha256,
        schemaFiles,
        relevantSourcePaths,
        [],
        cancellationToken);

    internal static async Task<SituationTrainingProvenanceDocumentV1> CreateAsync(
        string repositoryRoot,
        SituationStageFourSplitResult split,
        string analysisRulesSha256,
        string selectionConfigSha256,
        string inputRepresentationConfigSha256,
        IReadOnlyList<SituationSchemaFileReferenceV1> schemaFiles,
        IEnumerable<string> relevantSourcePaths,
        IEnumerable<SituationTrainingProvenanceRoundMappingV1> roundMappings,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(split);
        ArgumentNullException.ThrowIfNull(schemaFiles);
        ArgumentNullException.ThrowIfNull(relevantSourcePaths);
        ArgumentNullException.ThrowIfNull(roundMappings);
        repositoryRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(repositoryRoot));
        ValidateHash(split.SplitSha256, "split");
        ValidateHash(split.Split.ParentSplitSha256, "parent split");
        ValidateHash(split.Split.ParentManifestSha256, "parent manifest");
        ValidateHash(analysisRulesSha256, "analysis rules");
        ValidateHash(selectionConfigSha256, "selection config");
        ValidateHash(inputRepresentationConfigSha256, "input representation config");
        if (SituationAnalysisRuleLoader.LoadFrozen().Sha256 != analysisRulesSha256 ||
            SituationTrainingSelectionLoader.LoadFrozen().Sha256 != selectionConfigSha256 ||
            SituationPromptRepresentationLoader.LoadFrozen().Sha256 != inputRepresentationConfigSha256)
            throw new InvalidDataException("Provenance configuration hashes do not match the frozen resources.");
        if (Path.IsPathRooted(split.Split.ParentManifestReference) ||
            split.Split.ParentManifestReference.Contains('\\'))
            throw new InvalidDataException("Parent manifest reference must be a portable relative path.");

        await SituationTrainingSchemaRegistry.VerifyAsync(repositoryRoot, schemaFiles, cancellationToken);
        var paths = relevantSourcePaths
            .Select(NormalizeRelativePath)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (paths.Length == 0)
            throw new InvalidDataException("Provenance requires at least one relevant source file.");

        var beforeHead = ReadRequiredGitHead(repositoryRoot);
        var beforeStatus = await ReadGitStatusAsync(repositoryRoot, cancellationToken);
        var firstHashes = await HashFilesAsync(repositoryRoot, paths, cancellationToken);
        var secondHashes = await HashFilesAsync(repositoryRoot, paths, cancellationToken);
        var afterStatus = await ReadGitStatusAsync(repositoryRoot, cancellationToken);
        var afterHead = ReadRequiredGitHead(repositoryRoot);
        if (beforeHead != afterHead || beforeStatus != afterStatus || !firstHashes.SequenceEqual(secondHashes))
            throw new IOException("Repository state changed while provenance was being captured.");

        var identityVersions = CreateIdentityVersions(split.SplitSha256);
        var mappings = split.Split.Train.Concat(split.Split.Dev).Concat(split.Split.Test)
            .OrderBy(member => member.FileName, StringComparer.Ordinal)
            .Select(member =>
            {
                if (Path.IsPathRooted(member.FileName) || Path.GetFileName(member.FileName) != member.FileName)
                    throw new InvalidDataException("Split member file name is not a direct file name.");
                return new SituationProvenanceDemoMappingV1(
                    member.FileName,
                    SituationTrainingContractJson.CreateIdentity(
                        member.MatchId, "s0-a1", 0, identityVersions).MatchRef,
                    member.MatchId);
            })
            .ToArray();
        if (mappings.Select(item => item.FileName).Distinct(StringComparer.Ordinal).Count() != mappings.Length ||
            mappings.Select(item => item.MatchRef).Distinct(StringComparer.Ordinal).Count() != mappings.Length)
            throw new InvalidDataException("Provenance demo mappings are not unique.");

        var document = new SituationTrainingProvenanceDocumentV1(
            SituationTrainingContractVersions.Provenance,
            beforeHead,
            beforeStatus.Length != 0,
            split.SplitSha256,
            split.Split.ParentSplitSha256,
            split.Split.ParentManifestReference.Replace('\\', '/'),
            split.Split.ParentManifestSha256,
            analysisRulesSha256,
            selectionConfigSha256,
            SituationTrainingContractVersions.InputRepresentation,
            inputRepresentationConfigSha256,
            schemaFiles.OrderBy(item => item.SchemaVersion, StringComparer.Ordinal).ToArray(),
            firstHashes,
            roundMappings.OrderBy(item => item.MatchRef, StringComparer.Ordinal)
                .ThenBy(item => item.RoundRef, StringComparer.Ordinal).ToArray(),
            mappings);
        Validate(document);
        return document;
    }

    internal static void Validate(SituationTrainingProvenanceDocumentV1 document)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (document.SchemaVersion != SituationTrainingContractVersions.Provenance)
            throw new InvalidDataException("Provenance schema version is invalid.");
        if (document.GitHead.Length != 40 || !document.GitHead.All(IsLowerHex))
            throw new InvalidDataException("Provenance Git HEAD is invalid.");
        ValidateHash(document.SplitSha256, "split");
        ValidateHash(document.ParentSplitSha256, "parent split");
        ValidateHash(document.ParentManifestSha256, "parent manifest");
        ValidateHash(document.AnalysisRulesSha256, "analysis rules");
        ValidateHash(document.SelectionConfigSha256, "selection config");
        ValidateHash(document.InputRepresentationConfigSha256, "input representation config");
        if (document.InputRepresentationVersion != SituationTrainingContractVersions.InputRepresentation)
            throw new InvalidDataException("Provenance input representation version is invalid.");
        if (!IsPortableRelativePath(document.ParentManifestReference))
            throw new InvalidDataException("Provenance parent manifest reference is invalid.");
        if (document.SchemaFiles.Count == 0 ||
            !document.SchemaFiles.SequenceEqual(
                document.SchemaFiles.OrderBy(item => item.SchemaVersion, StringComparer.Ordinal)))
            throw new InvalidDataException("Provenance Schema references are missing or unsorted.");
        if (document.SourceFiles.Count == 0 ||
            !document.SourceFiles.SequenceEqual(document.SourceFiles.OrderBy(item => item.Path, StringComparer.Ordinal)) ||
            document.SourceFiles.Select(item => item.Path).Distinct(StringComparer.Ordinal).Count() !=
            document.SourceFiles.Count)
            throw new InvalidDataException("Provenance source files are missing, duplicated, or unsorted.");
        foreach (var item in document.SourceFiles)
        {
            if (!IsPortableRelativePath(item.Path))
                throw new InvalidDataException("Provenance source file path is invalid.");
            ValidateHash(item.Sha256, "source file");
        }
        if (!document.DemoMappings.SequenceEqual(
                document.DemoMappings.OrderBy(item => item.FileName, StringComparer.Ordinal)) ||
            document.DemoMappings.Select(item => item.FileName).Distinct(StringComparer.Ordinal).Count() !=
            document.DemoMappings.Count ||
            document.DemoMappings.Select(item => item.MatchRef).Distinct(StringComparer.Ordinal).Count() !=
            document.DemoMappings.Count ||
            document.DemoMappings.Any(item => Path.IsPathRooted(item.FileName) ||
                                              Path.GetFileName(item.FileName) != item.FileName))
            throw new InvalidDataException("Provenance demo mappings are invalid, duplicated, or unsorted.");
        if (!document.RoundMappings.SequenceEqual(document.RoundMappings
                .OrderBy(item => item.MatchRef, StringComparer.Ordinal)
                .ThenBy(item => item.RoundRef, StringComparer.Ordinal)) ||
            document.RoundMappings.Select(item => item.RoundRef).Distinct(StringComparer.Ordinal).Count() !=
            document.RoundMappings.Count)
            throw new InvalidDataException("Provenance round mappings are duplicated or unsorted.");
        var demosByRef = document.DemoMappings.ToDictionary(item => item.MatchRef, StringComparer.Ordinal);
        var identityVersions = CreateIdentityVersions(document.SplitSha256);
        foreach (var item in document.RoundMappings)
        {
            if (!demosByRef.TryGetValue(item.MatchRef, out var demo) ||
                !Regex.IsMatch(item.SemanticRoundId, "^s[0-9]+-a[1-9][0-9]*$", RegexOptions.CultureInvariant))
                throw new InvalidDataException("Provenance round mapping source identity is invalid.");
            var expected = SituationTrainingContractJson.CreateIdentity(
                demo.MatchId, item.SemanticRoundId, 0, identityVersions);
            if (expected.MatchRef != item.MatchRef || expected.RoundRef != item.RoundRef)
                throw new InvalidDataException("Provenance round mapping hash identity is invalid.");
        }
    }

    internal static async Task VerifyCurrentRepositoryStateAsync(
        string repositoryRoot,
        SituationTrainingProvenanceDocumentV1 document,
        CancellationToken cancellationToken)
    {
        Validate(document);
        repositoryRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(repositoryRoot));
        if (ReadRequiredGitHead(repositoryRoot) != document.GitHead)
            throw new InvalidDataException("Provenance Git HEAD differs from the current repository.");
        var status = await ReadGitStatusAsync(repositoryRoot, cancellationToken);
        if ((status.Length != 0) != document.GitDirty)
            throw new InvalidDataException("Provenance gitDirty differs from the current repository.");
        var actual = await HashFilesAsync(
            repositoryRoot,
            document.SourceFiles.Select(item => item.Path).ToArray(),
            cancellationToken);
        if (!actual.SequenceEqual(document.SourceFiles))
            throw new InvalidDataException("Provenance source bytes differ from the current repository.");
    }

    private static async Task<SituationProvenanceSourceFileV1[]> HashFilesAsync(
        string repositoryRoot,
        IReadOnlyList<string> paths,
        CancellationToken cancellationToken)
    {
        var result = new List<SituationProvenanceSourceFileV1>(paths.Count);
        foreach (var relative in paths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = SituationArtifactIO.ResolveRepositoryPath(repositoryRoot, relative, "Provenance source file");
            if (!File.Exists(path))
                throw new FileNotFoundException("Provenance source file is missing.", path);
            result.Add(new(relative, await SituationArtifactIO.FileSha256Async(path, cancellationToken)));
        }
        return result.ToArray();
    }

    private static async Task<string> ReadGitStatusAsync(
        string repositoryRoot,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = repositoryRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("status");
        startInfo.ArgumentList.Add("--porcelain=v1");
        startInfo.ArgumentList.Add("--untracked-files=all");
        using var process = Process.Start(startInfo)
            ?? throw new IOException("Git status could not be started for provenance capture.");
        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        var output = await outputTask;
        _ = await errorTask;
        if (process.ExitCode != 0)
            throw new IOException("Git status failed during provenance capture.");
        return output.Replace("\r\n", "\n", StringComparison.Ordinal);
    }

    private static string ReadRequiredGitHead(string repositoryRoot)
    {
        var value = SituationArtifactIO.TryReadGitHead(repositoryRoot);
        if (value is not { Length: 40 } || !value.All(character =>
                character is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F'))
            throw new InvalidDataException("Git HEAD is unavailable or invalid for provenance capture.");
        return value.ToLowerInvariant();
    }

    private static string NormalizeRelativePath(string value)
    {
        var normalized = value.Replace('\\', '/');
        if (!IsPortableRelativePath(normalized))
            throw new InvalidDataException("Provenance source paths must be portable repository-relative paths.");
        return normalized;
    }

    private static bool IsPortableRelativePath(string value) =>
        !string.IsNullOrWhiteSpace(value) &&
        !Path.IsPathRooted(value) &&
        !value.Contains('\\') &&
        value.Split('/').All(segment => segment.Length > 0 && segment is not "." and not "..");

    private static void ValidateHash(string value, string description)
    {
        if (!SituationArtifactIO.IsSha256(value, lowercaseOnly: true))
            throw new InvalidDataException($"Provenance {description} SHA-256 is invalid.");
    }

    private static bool IsLowerHex(char value) => value is >= '0' and <= '9' or >= 'a' and <= 'f';

    private static SituationTrainingIdentityVersions CreateIdentityVersions(string splitSha256) => new(
        SituationTrainingContractVersions.Data,
        splitSha256,
        SituationContractVersions.Scene,
        SituationSceneBuilder.BuilderVersion,
        SituationSceneBuilder.GeometryVersion,
        SituationContractVersions.Facts,
        SituationAnalysisRuleLoader.FrozenAnalysisRuleVersion,
        SituationContractVersions.Narrative,
        WinFeatureSampleBuilder.SemanticVersion,
        SituationTrainingContractVersions.Selection,
        SituationTrainingContractVersions.InputRepresentation);
}

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CsDemoMap.Api.Services;

internal sealed record SituationArtifactFile(string Path, long Bytes, string Sha256);

internal static class SituationArtifactIO
{
    private static readonly UTF8Encoding Utf8NoBom = new(false);
    private static readonly JsonSerializerOptions DefaultJsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    internal static string ResolvePath(string baseDirectory, string path) =>
        Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(baseDirectory, path));

    internal static string ResolveRepositoryPath(
        string repositoryRoot,
        string relativePath,
        string description)
    {
        if (Path.IsPathRooted(relativePath))
            throw new InvalidDataException($"{description} must be repository-relative.");
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(repositoryRoot));
        var result = Path.GetFullPath(Path.Combine(
            root,
            relativePath.Replace('/', Path.DirectorySeparatorChar)));
        var prefix = root + Path.DirectorySeparatorChar;
        if (!result.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"{description} escapes the repository.");
        return result;
    }

    internal static void EnsureNewOutput(string path)
    {
        if (Directory.Exists(path) || File.Exists(path))
            throw new IOException($"Output path already exists: {path}");
    }

    internal static void EnsureDirectChild(string directory, string path, string message)
    {
        var expectedParent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        var actualParentValue = Path.GetDirectoryName(Path.GetFullPath(path));
        var actualParent = actualParentValue is null
            ? null
            : Path.TrimEndingDirectorySeparator(actualParentValue);
        if (!string.Equals(expectedParent, actualParent, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(message);
    }

    internal static void EnsureDescendant(string root, string path, string message)
    {
        var relative = Path.GetRelativePath(Path.GetFullPath(root), Path.GetFullPath(path));
        if (relative == ".." ||
            relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
            Path.IsPathRooted(relative))
            throw new InvalidDataException(message);
    }

    internal static bool IsSha256(string? value, bool lowercaseOnly = false) =>
        value is { Length: 64 } && value.All(character => lowercaseOnly
            ? character is >= '0' and <= '9' or >= 'a' and <= 'f'
            : character is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F');

    internal static string Sha256(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    internal static string Sha256(ReadOnlySpan<byte> value) =>
        Convert.ToHexStringLower(SHA256.HashData(value));

    internal static async Task<string> FileSha256Async(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            1024 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken));
    }

    internal static Task WriteJsonAsync<T>(
        string path,
        T value,
        JsonSerializerOptions options,
        CancellationToken cancellationToken) =>
        WriteTextAtomicAsync(path, JsonSerializer.Serialize(value, options), cancellationToken);

    internal static Task WriteJsonAsync<T>(
        string path,
        T value,
        CancellationToken cancellationToken) =>
        WriteJsonAsync(path, value, DefaultJsonOptions, cancellationToken);

    internal static Task WriteCanonicalAsync(
        string path,
        string value,
        CancellationToken cancellationToken) =>
        WriteTextAtomicAsync(path, value, cancellationToken);

    internal static async Task WriteTextAtomicAsync(
        string path,
        string value,
        CancellationToken cancellationToken)
    {
        var temporary = path + ".tmp";
        await File.WriteAllTextAsync(temporary, value, Utf8NoBom, cancellationToken);
        File.Move(temporary, path, true);
    }

    internal static async Task<IReadOnlyList<SituationArtifactFile>> InventoryFilesAsync(
        string root,
        CancellationToken cancellationToken,
        params string[] excludedRelativePaths)
    {
        var excluded = excludedRelativePaths
            .Select(path => path.Replace('\\', '/'))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var files = new List<SituationArtifactFile>();
        foreach (var path in Directory.GetFiles(root, "*", SearchOption.AllDirectories)
                     .Order(StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
            if (excluded.Contains(relative))
                continue;
            files.Add(new(
                relative,
                new FileInfo(path).Length,
                await FileSha256Async(path, cancellationToken)));
        }
        return files;
    }

    internal static string FindRepositoryRoot(string start)
    {
        var current = new DirectoryInfo(Path.GetFullPath(start));
        while (current is not null)
        {
            var git = Path.Combine(current.FullName, ".git");
            if ((Directory.Exists(git) || File.Exists(git)) &&
                File.Exists(Path.Combine(current.FullName, "apps", "api", "CsDemoMap.Api.csproj")))
                return current.FullName;
            current = current.Parent;
        }
        throw new DirectoryNotFoundException("Repository root was not found.");
    }

    internal static string? TryReadGitHead(string repositoryRoot)
    {
        var git = ResolveGitDirectory(repositoryRoot);
        if (git is null)
            return null;
        var headPath = Path.Combine(git, "HEAD");
        if (!File.Exists(headPath))
            return null;
        var head = File.ReadAllText(headPath).Trim();
        if (!head.StartsWith("ref: ", StringComparison.Ordinal))
            return head.Length == 40 ? head : null;
        var referenceName = head[5..];
        foreach (var directory in GitReferenceDirectories(git))
        {
            var referencePath = Path.Combine(
                directory,
                referenceName.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(referencePath))
                return File.ReadAllText(referencePath).Trim();
            var packedRefs = Path.Combine(directory, "packed-refs");
            if (!File.Exists(packedRefs))
                continue;
            var packed = File.ReadLines(packedRefs)
                .Select(line => line.Split(' ', 2))
                .Where(parts => parts.Length == 2 && parts[1] == referenceName)
                .Select(parts => parts[0])
                .FirstOrDefault();
            if (packed is not null)
                return packed;
        }
        return null;
    }

    private static string? ResolveGitDirectory(string repositoryRoot)
    {
        var git = Path.Combine(repositoryRoot, ".git");
        if (Directory.Exists(git))
            return git;
        if (!File.Exists(git))
            return null;
        var pointer = File.ReadAllText(git).Trim();
        const string prefix = "gitdir:";
        if (!pointer.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return null;
        var path = pointer[prefix.Length..].Trim();
        return Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(repositoryRoot, path));
    }

    private static IEnumerable<string> GitReferenceDirectories(string gitDirectory)
    {
        yield return gitDirectory;
        var commonDirectoryPath = Path.Combine(gitDirectory, "commondir");
        if (!File.Exists(commonDirectoryPath))
            yield break;
        var commonDirectory = File.ReadAllText(commonDirectoryPath).Trim();
        if (commonDirectory.Length == 0)
            yield break;
        var resolved = Path.GetFullPath(Path.IsPathRooted(commonDirectory)
            ? commonDirectory
            : Path.Combine(gitDirectory, commonDirectory));
        if (!string.Equals(resolved, gitDirectory, StringComparison.OrdinalIgnoreCase))
            yield return resolved;
    }
}

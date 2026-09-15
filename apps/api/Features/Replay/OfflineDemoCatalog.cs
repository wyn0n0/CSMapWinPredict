using CsDemoMap.Api.Models;

namespace CsDemoMap.Api.Services;

public sealed class OfflineDemoCatalog
{
    public const string ConfigurationKey = "OfflineDemos:RootPath";

    private readonly string rootPath;

    public OfflineDemoCatalog(
        IConfiguration configuration,
        IWebHostEnvironment environment)
    {
        var configuredPath = configuration[ConfigurationKey];
        rootPath = string.IsNullOrWhiteSpace(configuredPath)
            ? FindDefaultRoot(environment.ContentRootPath)
            : ResolveConfiguredRoot(configuredPath, environment.ContentRootPath);
        Directory.CreateDirectory(rootPath);
    }

    public OfflineDemoCatalogResponse List()
    {
        var files = new DirectoryInfo(rootPath)
            .EnumerateFiles("*.dem", SearchOption.TopDirectoryOnly)
            .Where(file => (file.Attributes & FileAttributes.ReparsePoint) == 0)
            .OrderBy(file => file.Name, StringComparer.OrdinalIgnoreCase)
            .Select(ToOfflineFile)
            .ToArray();
        return new OfflineDemoCatalogResponse(rootPath, files.Length, files);
    }

    public OfflineDemoSelection Resolve(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
            throw new ArgumentException("必须提供 fileName。", nameof(fileName));

        var normalizedName = fileName.Trim();
        if (!string.Equals(normalizedName, Path.GetFileName(normalizedName), StringComparison.Ordinal) ||
            normalizedName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new ArgumentException("fileName 只能是离线目录中的文件名，不能包含路径。", nameof(fileName));

        if (!string.Equals(Path.GetExtension(normalizedName), ".dem", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("只接受 .dem 文件。", nameof(fileName));

        var candidatePath = Path.GetFullPath(Path.Combine(rootPath, normalizedName));
        var rootPrefix = Path.TrimEndingDirectorySeparator(rootPath) + Path.DirectorySeparatorChar;
        if (!candidatePath.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("文件路径不在允许的离线目录中。", nameof(fileName));

        var file = new FileInfo(candidatePath);
        if (!file.Exists)
            throw new FileNotFoundException("离线目录中找不到指定 demo。", normalizedName);
        if ((file.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new ArgumentException("不允许通过符号链接或目录联接导入 demo。", nameof(fileName));

        return new OfflineDemoSelection(file.FullName, ToOfflineFile(file));
    }

    private static OfflineDemoFile ToOfflineFile(FileInfo file) =>
        new(file.Name, file.Length, file.LastWriteTimeUtc);

    private static string ResolveConfiguredRoot(string configuredPath, string contentRoot)
    {
        var expanded = Environment.ExpandEnvironmentVariables(configuredPath.Trim());
        return Path.GetFullPath(
            Path.IsPathRooted(expanded)
                ? expanded
                : Path.Combine(contentRoot, expanded));
    }

    private static string FindDefaultRoot(string contentRoot)
    {
        var current = new DirectoryInfo(Path.GetFullPath(contentRoot));
        while (current is not null)
        {
            if (Directory.Exists(Path.Combine(current.FullName, ".git")) &&
                Directory.Exists(Path.Combine(current.FullName, "apps")))
                return Path.Combine(current.FullName, "data", "mirage");

            current = current.Parent;
        }

        return Path.Combine(Path.GetFullPath(contentRoot), "data", "mirage");
    }
}

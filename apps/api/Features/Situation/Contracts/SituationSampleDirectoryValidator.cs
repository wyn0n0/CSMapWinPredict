using System.Text.Json;
using CsDemoMap.Api.Models;

namespace CsDemoMap.Api.Services;

internal static class SituationSampleDirectoryValidator
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    public static int Validate(string sampleDirectory)
    {
        var resolvedSampleDirectory = ResolvePath(sampleDirectory);
        var sceneDirectory = Path.Combine(resolvedSampleDirectory, "scenes");
        var files = Directory.GetFiles(sceneDirectory, "*.json").Order(StringComparer.Ordinal).ToArray();
        if (files.Length == 0)
            throw new InvalidDataException("Sample directory contains no scene JSON files.");

        foreach (var file in files)
        {
            var sample = JsonSerializer.Deserialize<MinimapSceneV1>(File.ReadAllText(file), JsonOptions)
                ?? throw new InvalidDataException($"Scene is empty: {file}");
            SituationContractValidator.Validate(sample);
        }

        return files.Length;
    }

    private static string ResolvePath(string path)
    {
        var direct = Path.GetFullPath(path);
        if (Directory.Exists(direct) || File.Exists(direct))
            return direct;
        if (Path.IsPathRooted(path))
            return direct;

        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            var candidate = Path.GetFullPath(Path.Combine(current.FullName, path));
            if (Directory.Exists(candidate) || File.Exists(candidate))
                return candidate;
            current = current.Parent;
        }
        return direct;
    }
}

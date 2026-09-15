using CsDemoMap.Api.Models;

namespace CsDemoMap.Api.Services;

internal sealed class SituationEvidenceBuilder
{
    private readonly List<SituationEvidence> evidence = [];
    private readonly HashSet<string> ids = new(StringComparer.Ordinal);

    public void Add(string id, IEnumerable<string> sourcePaths, string ruleId, string textZh)
    {
        if (!ids.Add(id))
            throw new InvalidDataException($"Duplicate situation evidence ID: {id}");
        var paths = sourcePaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (paths.Length == 0)
            throw new InvalidDataException($"Situation evidence has no source path: {id}");
        evidence.Add(new(id, paths, ruleId, textZh));
    }

    public IReadOnlyList<SituationEvidence> Build() => evidence.ToArray();
}

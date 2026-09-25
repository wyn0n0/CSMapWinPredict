using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using CsDemoMap.Api.Models;

namespace CsDemoMap.Api.Services;

internal static class SituationPromptRepresentationLoader
{
    internal const string FileName = "situation-prompt-representation-v1.json";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    internal static SituationPromptRepresentationLoadResult LoadFrozen()
    {
        var assembly = typeof(SituationPromptRepresentationLoader).Assembly;
        var suffix = $".SituationTraining.{FileName}";
        var matches = assembly.GetManifestResourceNames()
            .Where(name => name.EndsWith(suffix, StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (matches.Length != 1)
            throw new InvalidDataException($"Embedded prompt representation configuration is unavailable: {FileName}");
        using var stream = assembly.GetManifestResourceStream(matches[0])
            ?? throw new InvalidDataException($"Embedded prompt representation configuration is unavailable: {FileName}");
        using var reader = new StreamReader(stream, detectEncodingFromByteOrderMarks: true);
        return ParseForVerification(reader.ReadToEnd(), matches[0]);
    }

    internal static SituationPromptRepresentationLoadResult ParseForVerification(
        string json,
        string resourceName)
    {
        try
        {
            SituationTrainingContractJson.RejectDuplicateProperties(json, "Prompt representation configuration");
            SituationTrainingContractJson.RequireCompleteShape<SituationPromptRepresentationConfigV1>(
                json, "Prompt representation configuration");
            var config = JsonSerializer.Deserialize<SituationPromptRepresentationConfigV1>(json, JsonOptions)
                ?? throw new InvalidDataException("Prompt representation configuration is empty.");
            Validate(config);
            var canonical = SituationCanonicalJson.Serialize(config);
            return new(config, canonical, SituationArtifactIO.Sha256(canonical), resourceName);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                "Prompt representation configuration is invalid or contains unknown fields.", exception);
        }
    }

    internal static void Validate(SituationPromptRepresentationConfigV1 config)
    {
        var errors = new List<string>();
        Require(config.SchemaVersion == SituationTrainingContractVersions.PromptRepresentationConfig,
            "schemaVersion mismatch", errors);
        Require(config.Expanded is not null && config.Expanded.Version == "expanded-v1",
            "expanded representation version mismatch", errors);
        Require(config.Compact is not null && config.Compact.Version == "compact-v1",
            "compact representation version mismatch", errors);
        Require(config.SelectedVersion is "expanded-v1" or "compact-v1",
            "selectedVersion is invalid", errors);
        Require(config.SelectedVersion == SituationTrainingContractVersions.InputRepresentation,
            "selectedVersion differs from the frozen input representation", errors);
        Require(config.CandidateMaxUtf8Bytes > 0 && config.CandidateMaxUnicodeCharacters > 0,
            "candidate limits must be positive", errors);
        Require(config.PercentileMethod == "nearest-rank", "percentileMethod is unsupported", errors);
        ValidateTemplate(config.Expanded, "expanded", errors);
        ValidateTemplate(config.Compact, "compact", errors);

        if (config.ShortKeys is null || config.ShortKeys.Count == 0)
        {
            errors.Add("shortKeys are missing");
        }
        else
        {
            Require(config.ShortKeys.Keys.All(key => Regex.IsMatch(
                    key, "^[A-Za-z][A-Za-z0-9]*$", RegexOptions.CultureInvariant)),
                "shortKeys contain an invalid source key", errors);
            Require(config.ShortKeys.Values.All(value => Regex.IsMatch(
                    value, "^[a-z][a-z0-9]?$", RegexOptions.CultureInvariant)),
                "shortKeys contain an invalid compact key", errors);
            Require(config.ShortKeys.Values.Distinct(StringComparer.Ordinal).Count() == config.ShortKeys.Count,
                "shortKeys compact values are not unique", errors);
            foreach (var required in new[]
                     {
                         "scene", "facts", "allowedEvidenceIds", "alive", "totalHealth", "bomb",
                         "evidence", "textZh", "dataQuality"
                     })
                Require(config.ShortKeys.ContainsKey(required), $"shortKeys omit required key {required}", errors);
        }
        if (errors.Count > 0)
            throw new InvalidDataException(string.Join(Environment.NewLine, errors));
    }

    private static void ValidateTemplate(
        SituationPromptTemplateV1? template,
        string label,
        ICollection<string> errors)
    {
        if (template is null)
        {
            errors.Add($"{label} template is missing");
            return;
        }
        Require(!string.IsNullOrWhiteSpace(template.SystemText), $"{label} systemText is empty", errors);
        Require(!string.IsNullOrWhiteSpace(template.InputPrefix) &&
                !template.InputPrefix.Contains('\r') && !template.InputPrefix.Contains('\n'),
            $"{label} inputPrefix is invalid", errors);
        Require(!string.IsNullOrWhiteSpace(template.OutputPrefix) &&
                !template.OutputPrefix.Contains('\r') && !template.OutputPrefix.Contains('\n'),
            $"{label} outputPrefix is invalid", errors);
    }

    private static void Require(bool condition, string message, ICollection<string> errors)
    {
        if (!condition)
            errors.Add(message);
    }
}

internal sealed class SituationPromptRenderer
{
    private readonly SituationPromptRepresentationLoadResult load;
    private readonly IReadOnlyDictionary<string, string> reverseKeys;

    internal SituationPromptRenderer()
        : this(SituationPromptRepresentationLoader.LoadFrozen())
    {
    }

    internal SituationPromptRenderer(SituationPromptRepresentationLoadResult load)
    {
        ArgumentNullException.ThrowIfNull(load);
        SituationPromptRepresentationLoader.Validate(load.Config);
        this.load = load;
        reverseKeys = load.Config.ShortKeys.ToDictionary(item => item.Value, item => item.Key,
            StringComparer.Ordinal);
    }

    internal SituationPromptRepresentationLoadResult Load => load;

    internal string SerializeModelInput(SituationTrainingInputV1 input)
    {
        ArgumentNullException.ThrowIfNull(input);
        return SituationCanonicalJson.Serialize(input);
    }

    internal string Render(SituationTrainingInputV1 input, string version)
    {
        ArgumentNullException.ThrowIfNull(input);
        var template = version switch
        {
            "expanded-v1" => load.Config.Expanded,
            "compact-v1" => load.Config.Compact,
            _ => throw new InvalidDataException($"Unknown prompt representation version {version}.")
        };
        var inputJson = SerializeModelInput(input);
        var represented = version == "compact-v1" ? MapJson(inputJson, load.Config.ShortKeys) : inputJson;
        return string.Join('\n', template.SystemText, template.InputPrefix, represented, template.OutputPrefix);
    }

    internal string RestoreCompactInputJson(string compactJson) => MapJson(compactJson, reverseKeys);

    private static string MapJson(string json, IReadOnlyDictionary<string, string> keyMap)
    {
        using var document = JsonDocument.Parse(json);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = false }))
            Write(writer, document.RootElement, keyMap);
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void Write(
        Utf8JsonWriter writer,
        JsonElement element,
        IReadOnlyDictionary<string, string> keyMap)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
            {
                var properties = element.EnumerateObject()
                    .Select(property => new
                    {
                        Name = keyMap.TryGetValue(property.Name, out var mapped) ? mapped : property.Name,
                        Value = property.Value
                    })
                    .OrderBy(property => property.Name, StringComparer.Ordinal)
                    .ToArray();
                if (properties.Select(property => property.Name).Distinct(StringComparer.Ordinal).Count() !=
                    properties.Length)
                    throw new InvalidDataException("Prompt short-key mapping creates an object key collision.");
                writer.WriteStartObject();
                foreach (var property in properties)
                {
                    writer.WritePropertyName(property.Name);
                    Write(writer, property.Value, keyMap);
                }
                writer.WriteEndObject();
                break;
            }
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                    Write(writer, item, keyMap);
                writer.WriteEndArray();
                break;
            default:
                element.WriteTo(writer);
                break;
        }
    }
}

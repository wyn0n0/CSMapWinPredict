using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using CsDemoMap.Api.Models;
using static CsDemoMap.Api.Services.SituationArtifactIO;

namespace CsDemoMap.Api.Tests;

internal static class SituationStageThreeAcceptanceVerifier
{
    private const string SplitSha256 =
        "b176d96c063921f6a1a8718c700fb6d193903693539e4d1b447e37fca4aa43d8";

    private static readonly JsonSerializerOptions StrictOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public static async Task VerifyAsync(string repositoryRoot, CancellationToken cancellationToken)
    {
        var load = SituationAnalysisRuleLoader.LoadFrozen();
        var artifact = FindLatestCompleteArtifact(repositoryRoot, load);
        var analyzer = new SituationDeterministicAnalyzer(load);
        using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(
            Path.Combine(artifact, "manifest.json"), cancellationToken));
        var root = manifest.RootElement;
        Check(root.GetProperty("schemaVersion").GetString() == SituationStageThreeAcceptance.ManifestVersion,
            "manifest version");
        Check(root.GetProperty("status").GetString() == "complete" &&
              root.GetProperty("role").GetString() == "holdout", "complete holdout role");
        Check(root.GetProperty("demoCount").GetInt32() == 8 &&
              root.GetProperty("sampleCount").GetInt32() == 16, "holdout size");
        Check(root.GetProperty("splitSha256").GetString() == SplitSha256, "split binding");
        Check(root.GetProperty("ruleVersion").GetString() == load.Rules.AnalysisRuleVersion &&
              root.GetProperty("ruleConfigSha256").GetString() == load.Sha256, "frozen rule binding");
        Check(root.GetProperty("performanceGatePassed").GetBoolean(), "performance gate");
        Check(root.GetProperty("holdoutRuleOutputsObserved").GetBoolean(), "holdout observation marker");

        var inventory = root.GetProperty("files").EnumerateArray().ToArray();
        foreach (var item in inventory)
        {
            var relative = item.GetProperty("path").GetString()!;
            var fullPath = ResolveArtifactPath(artifact, relative);
            Check(File.Exists(fullPath), $"inventory file exists: {relative}");
            Check(new FileInfo(fullPath).Length == item.GetProperty("bytes").GetInt64(),
                $"inventory length: {relative}");
            Check(await FileSha256Async(fullPath, cancellationToken) ==
                  item.GetProperty("sha256").GetString(), $"inventory hash: {relative}");
        }
        var actualFiles = Directory.GetFiles(artifact, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(artifact, path).Replace('\\', '/'))
            .Where(path => path != "manifest.json")
            .Order(StringComparer.Ordinal)
            .ToArray();
        Check(actualFiles.SequenceEqual(inventory.Select(item => item.GetProperty("path").GetString()!)
            .Order(StringComparer.Ordinal)), "inventory completeness");

        var rulesText = await ReadUtf8NoBomAsync(Path.Combine(artifact, "rules.json"), cancellationToken);
        Check(rulesText == load.CanonicalJson && Sha256(rulesText) == load.Sha256,
            "embedded frozen rules match artifact");
        var requestText = await ReadUtf8NoBomAsync(Path.Combine(artifact, "request.json"), cancellationToken);
        var request = JsonSerializer.Deserialize<SituationStageThreeHoldoutRequestBuilder.HoldoutRequest>(
                          requestText, StrictOptions)
                      ?? throw new InvalidDataException("Holdout request is empty.");
        SituationStageThreeHoldoutRequestBuilder.ValidateRequest(request);
        Check(requestText == SituationCanonicalJson.Serialize(request), "canonical holdout request");
        var requestSha256 = Sha256(requestText);
        Check(requestSha256 == root.GetProperty("holdoutRequestSha256").GetString(),
            "holdout request commitment");
        Check(request.ExpectedSplitSha256 == SplitSha256, "request split binding");

        var splitPath = ResolveRepositoryPath(repositoryRoot, request.SplitFile);
        var split = await SituationStageThreeCalibration.LoadFrozenSplitAsync(
            splitPath, request.ExpectedSplitSha256, cancellationToken);
        Check(request.Demos.Select(item => item.FileName).ToHashSet(StringComparer.OrdinalIgnoreCase)
            .SetEquals(split.ValidationFiles), "exact eight-demo membership");
        Check(request.Demos.All(item => split.MatchIds.TryGetValue(item.FileName, out var hash) &&
                                        hash == item.ExpectedSha256), "holdout demo hashes");

        using var index = JsonDocument.Parse(await File.ReadAllTextAsync(
            Path.Combine(artifact, "scenes-index.json"), cancellationToken));
        var indexRoot = index.RootElement;
        Check(indexRoot.GetProperty("role").GetString() == "holdout" &&
              indexRoot.GetProperty("splitSha256").GetString() == SplitSha256 &&
              indexRoot.GetProperty("holdoutRequestSha256").GetString() == requestSha256,
            "index bindings");
        var samples = indexRoot.GetProperty("samples").EnumerateArray().ToArray();
        Check(samples.Length == 16 && samples.Select(item => item.GetProperty("demoRef").GetString())
            .Distinct(StringComparer.Ordinal).Count() == 8, "index size and diversity");
        var committed = request.Demos.SelectMany(demo => demo.Ticks.Select(tick => new
            {
                DemoRef = $"demo-{demo.ExpectedSha256[..12]}",
                tick.Tick,
                tick.Category
            }))
            .ToDictionary(item => (item.DemoRef, item.Tick));
        foreach (var sample in samples)
        {
            var demoRef = sample.GetProperty("demoRef").GetString()!;
            var tick = sample.GetProperty("tick").GetInt32();
            if (!committed.TryGetValue((demoRef, tick), out var commitment))
                throw new InvalidDataException(
                    "Stage-three acceptance check failed: sample matches committed tick");
            Check(commitment.Category == sample.GetProperty("category").GetString(),
                "sample matches committed category");
            var sceneText = await ReadUtf8NoBomAsync(
                ResolveArtifactPath(artifact, sample.GetProperty("scenePath").GetString()!), cancellationToken);
            var factsText = await ReadUtf8NoBomAsync(
                ResolveArtifactPath(artifact, sample.GetProperty("factsPath").GetString()!), cancellationToken);
            var narrativeText = await ReadUtf8NoBomAsync(
                ResolveArtifactPath(artifact, sample.GetProperty("narrativePath").GetString()!), cancellationToken);
            var scene = SituationCanonicalJson.Deserialize<MinimapSceneV1>(sceneText);
            var facts = SituationCanonicalJson.Deserialize<SituationFactsV1>(factsText);
            var narrative = SituationCanonicalJson.Deserialize<SituationNarrativeV1>(narrativeText);
            SituationContractValidator.Validate(scene);
            SituationContractValidator.Validate(facts, scene, load.Rules.AnalysisRuleVersion);
            SituationContractValidator.ValidateTemplate(narrative, facts);
            Check(sceneText == SituationCanonicalJson.Serialize(scene) &&
                  factsText == SituationCanonicalJson.Serialize(facts) &&
                  narrativeText == SituationCanonicalJson.Serialize(narrative),
                "canonical holdout payloads");
            Check(SituationCanonicalJson.Sha256(scene) == sample.GetProperty("sceneSha256").GetString() &&
                  SituationCanonicalJson.Sha256(facts) == sample.GetProperty("factsSha256").GetString() &&
                  SituationCanonicalJson.Sha256(narrative) == sample.GetProperty("narrativeSha256").GetString(),
                "indexed holdout hashes");
            Check(facts.Alive.T == scene.Teams.T.Alive && facts.Alive.CT == scene.Teams.CT.Alive &&
                  facts.TotalHealth.T == scene.Teams.T.TotalHealth &&
                  facts.TotalHealth.CT == scene.Teams.CT.TotalHealth &&
                  facts.Bomb.State == scene.Bomb.State && facts.Bomb.Site == scene.Bomb.Site,
                "direct fact fidelity");
            Check(CategoryMatches(scene, commitment.Category), "committed phase fidelity");
            var repeat = analyzer.Analyze(scene);
            Check(repeat.Facts.Sha256 == SituationCanonicalJson.Sha256(facts) &&
                  repeat.Narrative.Sha256 == SituationCanonicalJson.Sha256(narrative),
                "deterministic holdout replay");
            Check(!ContainsForbidden(factsText + narrativeText),
                "identity, future, win-probability and advice scan");
        }

        using var performance = JsonDocument.Parse(await File.ReadAllTextAsync(
            Path.Combine(artifact, "performance.json"), cancellationToken));
        Check(performance.RootElement.GetProperty("gatePassed").GetBoolean() &&
              performance.RootElement.GetProperty("hot").GetProperty("iterations").GetInt32() >= 200 &&
              performance.RootElement.GetProperty("hot").GetProperty("p95Milliseconds").GetDouble() <= 10 &&
              performance.RootElement.GetProperty("combined").GetProperty("iterations").GetInt32() >= 200 &&
              performance.RootElement.GetProperty("combined").GetProperty("p95Milliseconds").GetDouble() <= 100,
            "recorded performance thresholds");
        using var distribution = JsonDocument.Parse(await File.ReadAllTextAsync(
            Path.Combine(artifact, "distribution-report.json"), cancellationToken));
        Check(distribution.RootElement.GetProperty("role").GetString() == "holdout" &&
              distribution.RootElement.GetProperty("sampleCount").GetInt32() == 16,
            "holdout distribution report");
        Console.WriteLine("Situation stage-three acceptance artifact checks passed: 16 scenes across 8 holdout demos.");
    }

    private static bool CategoryMatches(MinimapSceneV1 scene, string category)
    {
        var prePlant = scene.Round.Phase == SituationRoundPhase.Live &&
                       scene.Bomb.State is SituationBombState.Carried or SituationBombState.Dropped or
                           SituationBombState.Planting;
        var postPlant = scene.Round.Phase == SituationRoundPhase.PostPlant ||
                        scene.Bomb.State is SituationBombState.Planted or SituationBombState.Defusing;
        return category switch
        {
            "pre-plant" => prePlant,
            "post-plant" => postPlant,
            "live-fallback" => scene.Round.Phase is SituationRoundPhase.Live or SituationRoundPhase.PostPlant,
            _ => false
        };
    }

    private static string FindLatestCompleteArtifact(string repositoryRoot, SituationAnalysisRuleLoadResult load)
    {
        foreach (var directory in Directory.GetDirectories(Path.Combine(repositoryRoot, "datasets"),
                     "situation-stage3-acceptance-*", SearchOption.TopDirectoryOnly)
                 .OrderDescending(StringComparer.Ordinal))
        {
            var manifestPath = Path.Combine(directory, "manifest.json");
            if (!File.Exists(manifestPath)) continue;
            try
            {
                using var manifest = JsonDocument.Parse(File.ReadAllText(manifestPath));
                var root = manifest.RootElement;
                if (root.GetProperty("status").GetString() == "complete" &&
                    root.GetProperty("ruleVersion").GetString() == load.Rules.AnalysisRuleVersion &&
                    root.GetProperty("ruleConfigSha256").GetString() == load.Sha256)
                    return directory;
            }
            catch (JsonException)
            {
                // Incomplete or corrupt older artifacts are intentionally not selected.
            }
        }
        throw new DirectoryNotFoundException("A complete acceptance artifact for the frozen rules is missing.");
    }

    private static string ResolveArtifactPath(string artifact, string relative)
    {
        var root = Path.GetFullPath(artifact).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var path = Path.GetFullPath(Path.Combine(artifact, relative.Replace('/', Path.DirectorySeparatorChar)));
        Check(path.StartsWith(root, StringComparison.OrdinalIgnoreCase), "artifact path containment");
        return path;
    }

    private static string ResolveRepositoryPath(string repositoryRoot, string relative)
    {
        Check(!Path.IsPathRooted(relative), "repository-relative split path");
        var root = Path.GetFullPath(repositoryRoot).TrimEnd(Path.DirectorySeparatorChar) +
                   Path.DirectorySeparatorChar;
        var path = Path.GetFullPath(Path.Combine(repositoryRoot,
            relative.Replace('/', Path.DirectorySeparatorChar)));
        Check(path.StartsWith(root, StringComparison.OrdinalIgnoreCase), "split path containment");
        return path;
    }

    private static bool ContainsForbidden(string text) =>
        Regex.IsMatch(text, "(?:CT|T)[1-5]", RegexOptions.CultureInvariant) ||
        new[] { "steam", ".dem", "胜率", "winner", "将会", "会赢", "建议", "应该", "应当", "%" }
            .Any(term => text.Contains(term, StringComparison.OrdinalIgnoreCase));

    private static async Task<string> ReadUtf8NoBomAsync(string path, CancellationToken cancellationToken)
    {
        var bytes = await File.ReadAllBytesAsync(path, cancellationToken);
        Check(bytes.Length < 3 || bytes[0] != 0xef || bytes[1] != 0xbb || bytes[2] != 0xbf,
            "UTF-8 without BOM");
        return Encoding.UTF8.GetString(bytes);
    }

    private static void Check(bool value, string name)
    {
        if (!value) throw new InvalidDataException($"Stage-three acceptance check failed: {name}");
    }
}

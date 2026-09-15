using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using CsDemoMap.Api.Models;
using static CsDemoMap.Api.Services.SituationArtifactIO;

namespace CsDemoMap.Api.Tests;

internal static class SituationStageThreeCalibrationVerifier
{
    private static readonly JsonSerializerOptions StrictOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public static async Task VerifyAsync(string repositoryRoot, CancellationToken cancellationToken)
    {
        foreach (var load in new[]
                 {
                     SituationAnalysisRuleLoader.LoadCandidate(),
                     SituationAnalysisRuleLoader.LoadFrozen()
                 })
        {
        var artifact = FindLatestCompleteArtifact(repositoryRoot, load);
        var analyzer = new SituationDeterministicAnalyzer(load);
        var manifestPath = Path.Combine(artifact, "manifest.json");
        using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(manifestPath, cancellationToken));
        var root = manifest.RootElement;
        Check(root.GetProperty("schemaVersion").GetString() == SituationStageThreeCalibration.ManifestVersion,
            "calibration manifest version");
        Check(root.GetProperty("status").GetString() == "complete", "calibration manifest complete");
        Check(root.GetProperty("role").GetString() == "training", "calibration role");
        Check(root.GetProperty("demoCount").GetInt32() == 5 && root.GetProperty("sampleCount").GetInt32() == 30,
            "calibration size");
        Check(root.GetProperty("ruleVersion").GetString() == load.Rules.AnalysisRuleVersion &&
              root.GetProperty("ruleConfigSha256").GetString() == load.Sha256,
            "calibration rule binding");
        Check(root.GetProperty("splitSha256").GetString() ==
              "b176d96c063921f6a1a8718c700fb6d193903693539e4d1b447e37fca4aa43d8",
            "calibration split binding");
        Check(root.GetProperty("performanceGatePassed").GetBoolean(), "calibration performance gate");
        Check(!root.GetProperty("holdoutRuleOutputsObserved").GetBoolean(),
            "holdout rule outputs remain unobserved");

        var inventory = root.GetProperty("files").EnumerateArray().ToArray();
        foreach (var item in inventory)
        {
            var relative = item.GetProperty("path").GetString()!;
            var fullPath = Path.GetFullPath(Path.Combine(artifact,
                relative.Replace('/', Path.DirectorySeparatorChar)));
            Check(fullPath.StartsWith(Path.GetFullPath(artifact) + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase), "inventory path containment");
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

        var indexText = await File.ReadAllTextAsync(Path.Combine(artifact, "scenes-index.json"), cancellationToken);
        using var index = JsonDocument.Parse(indexText);
        var samples = index.RootElement.GetProperty("samples").EnumerateArray().ToArray();
        Check(samples.Length == 30, "calibration index size");
        Check(samples.Select(item => item.GetProperty("demoRef").GetString())
            .Distinct(StringComparer.Ordinal).Count() == 5, "calibration index demo diversity");
        foreach (var sample in samples)
        {
            var sceneText = await File.ReadAllTextAsync(Path.Combine(artifact,
                sample.GetProperty("scenePath").GetString()!.Replace('/', Path.DirectorySeparatorChar)), cancellationToken);
            var factsText = await File.ReadAllTextAsync(Path.Combine(artifact,
                sample.GetProperty("factsPath").GetString()!.Replace('/', Path.DirectorySeparatorChar)), cancellationToken);
            var narrativeText = await File.ReadAllTextAsync(Path.Combine(artifact,
                sample.GetProperty("narrativePath").GetString()!.Replace('/', Path.DirectorySeparatorChar)), cancellationToken);
            var scene = SituationCanonicalJson.Deserialize<MinimapSceneV1>(sceneText);
            var facts = SituationCanonicalJson.Deserialize<SituationFactsV1>(factsText);
            var narrative = SituationCanonicalJson.Deserialize<SituationNarrativeV1>(narrativeText);
            SituationContractValidator.Validate(scene);
            SituationContractValidator.Validate(facts, scene, load.Rules.AnalysisRuleVersion);
            SituationContractValidator.ValidateTemplate(narrative, facts);
            Check(sceneText == SituationCanonicalJson.Serialize(scene) &&
                  factsText == SituationCanonicalJson.Serialize(facts) &&
                  narrativeText == SituationCanonicalJson.Serialize(narrative),
                "canonical calibration payloads");
            Check(SituationCanonicalJson.Sha256(scene) == sample.GetProperty("sceneSha256").GetString() &&
                  SituationCanonicalJson.Sha256(facts) == sample.GetProperty("factsSha256").GetString() &&
                  SituationCanonicalJson.Sha256(narrative) == sample.GetProperty("narrativeSha256").GetString(),
                "indexed calibration hashes");
            var repeat = analyzer.Analyze(scene);
            Check(repeat.Facts.Sha256 == SituationCanonicalJson.Sha256(facts) &&
                  repeat.Narrative.Sha256 == SituationCanonicalJson.Sha256(narrative),
                "calibration deterministic replay");
            Check(!ContainsSensitiveNarrative(narrativeText), "calibration narrative identity and advice scan");
        }

        var artifactRequestPath = Path.Combine(artifact, "stage3-holdout-request-v1.json");
        var trackedRequestPath = Path.Combine(repositoryRoot, "situation-implementation", "stage3-holdout-request-v1.json");
        var artifactRequest = await ReadHoldoutAsync(artifactRequestPath, cancellationToken);
        var trackedRequest = await ReadHoldoutAsync(trackedRequestPath, cancellationToken);
        SituationStageThreeHoldoutRequestBuilder.ValidateRequest(artifactRequest);
        SituationStageThreeHoldoutRequestBuilder.ValidateRequest(trackedRequest);
        var canonicalRequest = SituationCanonicalJson.Serialize(artifactRequest);
        Check(canonicalRequest == SituationCanonicalJson.Serialize(trackedRequest),
            "tracked and committed holdout requests agree");
        Check(Sha256(canonicalRequest) == root.GetProperty("holdoutRequestSha256").GetString(),
            "holdout request commitment hash");
        CheckThrows(() => SituationStageThreeHoldoutRequestBuilder.ValidateRequest(
            trackedRequest with
            {
                Demos = trackedRequest.Demos.Select((item, indexValue) => indexValue == 0
                    ? item with { Ticks = [item.Ticks[0], item.Ticks[0]] }
                    : item).ToArray()
            }), "duplicate holdout tick rejected");
        CheckThrows(() => SituationStageThreeCalibration.ValidateRequest(
            new SituationStageThreeCalibration.CalibrationRequest(
                SituationStageThreeCalibration.RequestVersion,
                "split.json", new string('0', 64), load.Rules.AnalysisRuleVersion, load.Sha256,
                30, 6,
                Enumerable.Range(0, 5).Select(_ =>
                    new SituationStageThreeCalibration.CalibrationDemoRequest(
                        "duplicate.dem", new string('0', 64), 6, [],
                        Enumerable.Range(0, 6).Select(indexValue =>
                            new SituationStageThreeCalibration.CalibrationExpectedSample(
                                indexValue, new string('0', 64))).ToArray())).ToArray())),
            "duplicate calibration demo rejected");
        Console.WriteLine($"Situation stage-three calibration artifact checks passed for {load.Rules.AnalysisRuleVersion}: 30 scenes, 8-demo holdout commitment.");
        }
    }

    private static string FindLatestCompleteArtifact(
        string repositoryRoot,
        SituationAnalysisRuleLoadResult load)
    {
        var datasets = Path.Combine(repositoryRoot, "datasets");
        foreach (var directory in Directory.GetDirectories(
                     datasets, "situation-stage3-calibration-*", SearchOption.TopDirectoryOnly)
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
                // An incomplete/corrupt older artifact is intentionally not selected.
            }
        }
        throw new DirectoryNotFoundException(
            $"A complete calibration artifact for {load.Rules.AnalysisRuleVersion} is missing.");
    }

    private static async Task<SituationStageThreeHoldoutRequestBuilder.HoldoutRequest> ReadHoldoutAsync(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<SituationStageThreeHoldoutRequestBuilder.HoldoutRequest>(
                   stream, StrictOptions, cancellationToken)
               ?? throw new InvalidDataException("Holdout request is empty.");
    }

    private static bool ContainsSensitiveNarrative(string text) =>
        Regex.IsMatch(text, "(?:CT|T)[1-5]", RegexOptions.CultureInvariant) ||
        new[] { ".dem", "steam", "胜率", "winner", "将会", "会赢", "建议", "应该", "应当", "%" }
            .Any(term => text.Contains(term, StringComparison.OrdinalIgnoreCase));

    private static void Check(bool value, string name)
    {
        if (!value) throw new InvalidDataException($"Stage-three calibration check failed: {name}");
    }

    private static void CheckThrows(Action action, string name)
    {
        try
        {
            action();
        }
        catch (InvalidDataException)
        {
            return;
        }
        throw new InvalidDataException($"Stage-three calibration check failed: {name}");
    }
}

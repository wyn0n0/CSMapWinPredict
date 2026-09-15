using System.Text.Json;
using CsDemoMap.Api.Models;

namespace CsDemoMap.Api.Tests;

internal static class SituationStageThreeAutomaticVerifier
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    public static async Task VerifyAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await SituationStageTwoAutomaticVerifier.VerifyAsync(cancellationToken);
        SituationRuleVerifier.Verify();

        var sampleDirectory = FindRepositoryPath(
            Path.Combine("datasets", "situation-v1-review-20260914-r9"));
        var sceneDirectory = Path.Combine(sampleDirectory, "scenes");
        var files = Directory.GetFiles(sceneDirectory, "*.json")
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (files.Length != 32)
            throw new InvalidDataException("Stage-three regression requires all 32 frozen r9 scenes.");

        var analyzer = new SituationDeterministicAnalyzer(SituationAnalysisRuleLoader.LoadFrozen());
        var checks = 0;
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var text = await File.ReadAllTextAsync(file, cancellationToken);
            var scene = JsonSerializer.Deserialize<MinimapSceneV1>(text, JsonOptions)
                ?? throw new InvalidDataException($"Frozen scene is empty: {Path.GetFileName(file)}");
            SituationContractValidator.Validate(scene);
            var result = analyzer.Analyze(scene);
            SituationContractValidator.Validate(
                result.Facts.Facts, scene, analyzer.RuleLoad.Rules.AnalysisRuleVersion);
            SituationContractValidator.ValidateTemplate(result.Narrative.Narrative, result.Facts.Facts);
            if (result.Facts.Facts.Alive.T != scene.Teams.T.Alive ||
                result.Facts.Facts.Alive.CT != scene.Teams.CT.Alive ||
                result.Facts.Facts.TotalHealth.T != scene.Teams.T.TotalHealth ||
                result.Facts.Facts.TotalHealth.CT != scene.Teams.CT.TotalHealth ||
                result.Facts.Facts.Bomb.State != scene.Bomb.State ||
                result.Facts.Facts.Bomb.Site != scene.Bomb.Site)
                throw new InvalidDataException($"Direct facts differ in {Path.GetFileName(file)}.");
            var repeat = analyzer.Analyze(scene);
            if (repeat.Facts.Sha256 != result.Facts.Sha256 ||
                repeat.Narrative.Sha256 != result.Narrative.Sha256)
                throw new InvalidDataException($"Analysis is not deterministic for {Path.GetFileName(file)}.");
            var publicText = result.Facts.CanonicalJson + result.Narrative.CanonicalJson;
            if (ContainsForbidden(publicText))
                throw new InvalidDataException($"Analysis contains forbidden content for {Path.GetFileName(file)}.");
            checks++;
        }
        var repositoryRoot = SituationArtifactIO.FindRepositoryRoot(Directory.GetCurrentDirectory());
        await SituationStageThreeCalibrationVerifier.VerifyAsync(repositoryRoot, cancellationToken);
        await SituationStageThreeAcceptanceVerifier.VerifyAsync(repositoryRoot, cancellationToken);
        Console.WriteLine($"Situation stage-three frozen-scene checks passed: {checks}");
        Console.WriteLine("Situation stage-three automatic verification suites passed: 5");
    }

    private static string FindRepositoryPath(string relativePath)
    {
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            var current = new DirectoryInfo(Path.GetFullPath(start));
            while (current is not null)
            {
                var candidate = Path.Combine(current.FullName, relativePath);
                if (Directory.Exists(candidate) || File.Exists(candidate))
                    return candidate;
                current = current.Parent;
            }
        }
        throw new DirectoryNotFoundException($"Repository path was not found: {relativePath}");
    }

    private static bool ContainsForbidden(string text) => new[]
        { "steam", ".dem", "胜率", "将会", "会赢", "建议", "应该", "应当", "%", "winner" }
        .Any(term => text.Contains(term, StringComparison.OrdinalIgnoreCase));
}

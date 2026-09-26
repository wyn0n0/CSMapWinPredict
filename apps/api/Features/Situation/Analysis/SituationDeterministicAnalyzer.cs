using CsDemoMap.Api.Models;

namespace CsDemoMap.Api.Services;

internal sealed class SituationDeterministicAnalyzer
{
    private readonly SituationAnalysisRuleLoadResult ruleLoad;
    private readonly SituationFactsAnalyzer factsAnalyzer;
    private readonly SituationTemplateNarrator narrator;

    public SituationDeterministicAnalyzer(SituationAnalysisRuleLoadResult ruleLoad, ISituationVisibilityQuery? visibility = null)
    {
        ArgumentNullException.ThrowIfNull(ruleLoad);
        SituationAnalysisRuleLoader.Validate(ruleLoad.Rules);
        this.ruleLoad = ruleLoad;
        factsAnalyzer = new(visibility);
        narrator = new(ruleLoad.Rules);
    }

    public static SituationDeterministicAnalyzer CreateCandidate() =>
        new(SituationAnalysisRuleLoader.LoadCandidate());

    public static SituationDeterministicAnalyzer CreateFrozen() =>
        new(SituationAnalysisRuleLoader.LoadFrozen());

    public static SituationDeterministicAnalyzer CreateRaycast(string? meshPath = null)
        => CreateWithMesh(SituationAnalysisRuleLoader.LoadRaycast(), meshPath);

    public static SituationDeterministicAnalyzer CreateLocalPeek(string? meshPath = null)
        => CreateWithMesh(SituationAnalysisRuleLoader.LoadLocalPeek(), meshPath);

    public static SituationDeterministicAnalyzer CreatePositionPrediction(string? meshPath = null)
        => CreateWithMesh(SituationAnalysisRuleLoader.LoadPositionPrediction(), meshPath);

    private static SituationDeterministicAnalyzer CreateWithMesh(SituationAnalysisRuleLoadResult load, string? meshPath)
    {
        var rules = load.Rules.Visibility!;
        meshPath ??= Path.Combine(AppContext.BaseDirectory, "Geometry", "de_mirage.mesh");
        // Missing assets are unknown; a present but corrupt/mismatched asset fails explicitly.
        var mesh = File.Exists(meshPath) ? SituationCollisionMesh.Load(meshPath, rules.AssetSha256) : null;
        return new(load, new SituationVisibilityQuery(mesh, rules));
    }

    public SituationAnalysisRuleLoadResult RuleLoad => ruleLoad;

    public SituationDeterministicAnalysisResult Analyze(MinimapSceneV1 scene)
    {
        var facts = factsAnalyzer.Analyze(scene, ruleLoad.Rules);
        var narrative = narrator.Create(facts.Facts);
        return new(facts, narrative, ruleLoad.Sha256);
    }
}

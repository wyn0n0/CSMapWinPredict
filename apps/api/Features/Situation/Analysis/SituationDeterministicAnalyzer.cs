using CsDemoMap.Api.Models;

namespace CsDemoMap.Api.Services;

internal sealed class SituationDeterministicAnalyzer
{
    private readonly SituationAnalysisRuleLoadResult ruleLoad;
    private readonly SituationFactsAnalyzer factsAnalyzer = new();
    private readonly SituationTemplateNarrator narrator;

    public SituationDeterministicAnalyzer(SituationAnalysisRuleLoadResult ruleLoad)
    {
        ArgumentNullException.ThrowIfNull(ruleLoad);
        SituationAnalysisRuleLoader.Validate(ruleLoad.Rules);
        this.ruleLoad = ruleLoad;
        narrator = new(ruleLoad.Rules);
    }

    public static SituationDeterministicAnalyzer CreateCandidate() =>
        new(SituationAnalysisRuleLoader.LoadCandidate());

    public static SituationDeterministicAnalyzer CreateFrozen() =>
        new(SituationAnalysisRuleLoader.LoadFrozen());

    public SituationAnalysisRuleLoadResult RuleLoad => ruleLoad;

    public SituationDeterministicAnalysisResult Analyze(MinimapSceneV1 scene)
    {
        var facts = factsAnalyzer.Analyze(scene, ruleLoad.Rules);
        var narrative = narrator.Create(facts.Facts);
        return new(facts, narrative, ruleLoad.Sha256);
    }
}

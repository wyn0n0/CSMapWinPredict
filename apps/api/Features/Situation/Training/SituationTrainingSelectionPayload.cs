using CsDemoMap.Api.Models;

namespace CsDemoMap.Api.Services;

internal sealed record SituationTrainingSelectionPayload(
    string RoundId,
    int Tick,
    string SemanticPhase,
    SituationTrainingPhase Phase,
    IReadOnlyList<string> SelectionTags,
    double SampleWeight,
    int WeightNumerator,
    int WeightDenominator,
    MinimapSceneV1 Scene,
    string SceneCanonicalJson,
    string SceneSha256,
    SituationFactsV1 Facts,
    string FactsCanonicalJson,
    string FactsSha256,
    SituationNarrativeV1 Narrative,
    string NarrativeCanonicalJson,
    string NarrativeSha256);

internal sealed record SituationTrainingRoundSelectionWithPayloads(
    SituationTrainingRoundSelectionResult SelectionResult,
    IReadOnlyList<SituationTrainingSelectionPayload> Payloads);

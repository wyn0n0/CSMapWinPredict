using CsDemoMap.Api.Models;

namespace CsDemoMap.Api.Services;

internal static class SituationTrainingRecordBuilder
{
    internal static MinimapSceneV1 RestoreSourceScene(
        SituationTrainingRecordV1 record, string matchId, string semanticRoundId)
    {
        var scene = record.Input.Scene;
        return new(scene.SchemaVersion, scene.SceneBuilderVersion, scene.GeometryVersion,
            "structured", scene.Map, matchId,
            scene.Tick / checked(DemoImportService.WindowSeconds * scene.TickRate), scene.Tick, scene.Tick,
            scene.TickRate, scene.Round with { Ref = semanticRoundId }, scene.Players, scene.Teams,
            scene.Bomb, scene.Utilities, scene.Effects, scene.Geometry, scene.DataQuality);
    }

    internal static SituationTrainingRecordV1 Build(
        SituationStageFourSplitMember member,
        SituationTrainingSelectionPayload payload,
        SituationTrainingSplit split,
        string splitSha256)
    {
        if (payload.SceneSha256 != SituationCanonicalJson.Sha256(payload.Scene) ||
            payload.FactsSha256 != SituationCanonicalJson.Sha256(payload.Facts) ||
            payload.NarrativeSha256 != SituationCanonicalJson.Sha256(payload.Narrative) ||
            payload.Scene.Tick != payload.Tick ||
            payload.SceneCanonicalJson != SituationCanonicalJson.Serialize(payload.Scene) ||
            payload.FactsCanonicalJson != SituationCanonicalJson.Serialize(payload.Facts) ||
            payload.NarrativeCanonicalJson != SituationCanonicalJson.Serialize(payload.Narrative))
            throw new InvalidDataException("Selected payload content or canonical hash changed.");
        SituationContractValidator.Validate(payload.Facts, payload.Scene, payload.Facts.AnalysisRuleVersion);
        SituationContractValidator.ValidateTemplate(payload.Narrative, payload.Facts);
        var modelScene = SituationModelInputProjector.Project(payload.Scene);
        var input = new SituationTrainingInputV1(modelScene, payload.Facts,
            payload.Facts.Evidence.Select(item => item.Id).Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal).ToArray());
        var identity = SituationTrainingContractJson.CreateIdentity(
            member.MatchId, payload.RoundId, payload.Tick, CreateIdentityVersions(splitSha256));
        var record = new SituationTrainingRecordV1(
            SituationTrainingContractVersions.TrainingRecord, identity.SampleId,
            new(split, identity.MatchRef, identity.RoundRef, payload.Tick, payload.Phase,
                payload.SelectionTags, payload.SampleWeight, payload.WeightNumerator, payload.WeightDenominator,
                payload.SceneSha256, SituationCanonicalJson.Sha256(modelScene), payload.FactsSha256,
                payload.NarrativeSha256), input, payload.Narrative,
            SituationTrainingLabelSource.TemplatePrelabel, SituationTrainingReviewStatus.Unreviewed);
        SituationTrainingContractJson.Validate(record);
        return record;
    }

    internal static SituationTrainingRecordV1 Build(
        DemoTimeline timeline,
        SituationStageFourSplitMember member,
        RoundAttempt attempt,
        SituationTrainingSelectedTick selected,
        SituationTrainingSplit split,
        string splitSha256,
        SituationEligibleSceneBuilder eligibleBuilder,
        SituationDeterministicAnalyzer analyzer,
        CancellationToken cancellationToken)
    {
        var semantic = timeline.Semantics!.Frames.Single(frame =>
            frame.Tick == selected.Tick &&
            string.Equals(frame.RoundId, attempt.RoundId, StringComparison.Ordinal));
        var windowIndex = semantic.Tick /
            checked(DemoImportService.WindowSeconds * timeline.Metadata.TickRate);
        var built = eligibleBuilder.Build(
            timeline,
            member.MatchId,
            windowIndex,
            attempt,
            semantic,
            cancellationToken);
        if (!built.Eligibility.Eligible || built.Scene is null)
            throw new InvalidDataException("A selected training tick is no longer eligible.");

        var analysis = analyzer.Analyze(built.Scene.Scene);
        var modelScene = SituationModelInputProjector.Project(built.Scene.Scene);
        var allowedEvidence = analysis.Facts.Facts.Evidence
            .Select(item => item.Id)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var input = new SituationTrainingInputV1(modelScene, analysis.Facts.Facts, allowedEvidence);
        var identity = SituationTrainingContractJson.CreateIdentity(
            member.MatchId,
            attempt.RoundId,
            selected.Tick,
            CreateIdentityVersions(splitSha256));
        var metadata = new SituationTrainingMetadataV1(
            split,
            identity.MatchRef,
            identity.RoundRef,
            selected.Tick,
            selected.Phase,
            selected.SelectionTags,
            selected.SampleWeight,
            selected.WeightNumerator,
            selected.WeightDenominator,
            built.Scene.Sha256,
            SituationCanonicalJson.Sha256(modelScene),
            analysis.Facts.Sha256,
            analysis.Narrative.Sha256);
        var record = new SituationTrainingRecordV1(
            SituationTrainingContractVersions.TrainingRecord,
            identity.SampleId,
            metadata,
            input,
            analysis.Narrative.Narrative,
            SituationTrainingLabelSource.TemplatePrelabel,
            SituationTrainingReviewStatus.Unreviewed);
        SituationTrainingContractJson.Validate(record);
        return record;
    }

    internal static SituationTrainingIdentityVersions CreateIdentityVersions(string splitSha256) => new(
        SituationTrainingContractVersions.Data,
        splitSha256,
        SituationContractVersions.Scene,
        SituationSceneBuilder.BuilderVersion,
        SituationSceneBuilder.GeometryVersion,
        SituationContractVersions.Facts,
        SituationAnalysisRuleLoader.FrozenAnalysisRuleVersion,
        SituationContractVersions.Narrative,
        WinFeatureSampleBuilder.SemanticVersion,
        SituationTrainingContractVersions.Selection,
        SituationTrainingContractVersions.InputRepresentation);
}

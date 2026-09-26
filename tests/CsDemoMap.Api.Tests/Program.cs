using System.Globalization;
using CsDemoMap.Api.Services;
using CsDemoMap.Api.Tests;

if (args is ["--verify-situation-raycast"])
{
    SituationVisibilityVerifier.Verify();
    await SituationRaycastReviewVerifier.VerifyAsync(CancellationToken.None);
    return 0;
}

if (args is ["--serve-situation-review-synthetic", var syntheticWork])
{
    SituationArtifactIO.EnsureNewOutput(syntheticWork);
    var catalog = new SituationReviewWorkflowVerifier.SyntheticReviewCatalog();
    await using var store = await SituationReviewWorkStore.OpenAsync(syntheticWork, catalog.Identity,
        catalog.InitialSelected.Select(s => new ReviewActiveSample(s.ReviewOrdinal, s.Entry.SampleId)).ToArray(), catalog.GetCandidateAsync, CancellationToken.None);
    var repository = SituationArtifactIO.FindRepositoryRoot(AppContext.BaseDirectory);
    await SituationReviewConsumerProvenance.CaptureAsync(repository, syntheticWork, store,
        [typeof(SituationReviewDecisionService).Assembly.Location, typeof(CsDemoMap.Cli.SituationReviewServer).Assembly.Location], CancellationToken.None);
    await using var server = await CsDemoMap.Cli.SituationReviewServer.StartAsync(new SituationReviewDecisionService(catalog, store),
        Path.Combine(repository, "apps/cli/SituationReviewUi"), Path.Combine(repository, "apps/web/public/radars/simpleradar/de_mirage.webp"));
    Console.WriteLine($"SYNTHETIC REVIEW ONLY: {server.Url}");
    await server.WaitForShutdownAsync();
    return 0;
}

if (args is ["--verify-situation-review-automatic"])
{
    await SituationReviewWorkStoreVerifier.VerifyAsync(CancellationToken.None);
    await SituationReviewServerVerifier.VerifyAsync(CancellationToken.None);
    await SituationReviewWorkflowVerifier.VerifyAsync(CancellationToken.None);
    await SituationReviewConsumerVerifier.VerifyAsync(CancellationToken.None);
    return 0;
}

if (args is ["--verify-situation-review-candidates-automatic"])
{
    SituationReviewPolicyVerifier.Verify();
    SituationReviewCandidateSelectorVerifier.Verify();
    await SituationReviewCandidateArtifactVerifier.VerifyAsync(CancellationToken.None);
    return 0;
}

if (args is ["--verify-situation-review-artifacts"])
{
    await SituationReviewCandidateArtifactVerifier.VerifyAsync(CancellationToken.None);
    return 0;
}

if (args is ["--verify-situation-review-policy"])
{
    SituationReviewPolicyVerifier.Verify();
    return 0;
}

if (args is ["--verify-situation-review-selector"])
{
    SituationReviewCandidateSelectorVerifier.Verify();
    return 0;
}

if (args is ["--verify-win-data-pipeline"])
{
    await WinDataPipelineVerifier.VerifyAsync(CancellationToken.None);
    return 0;
}

if (args is ["--verify-semantics"])
{
    SemanticsVerifier.Verify();
    await V4DataVerifier.VerifyAsync();
    return 0;
}

if (args is ["--verify-win-inference"])
{
    await WinInferenceVerifier.VerifyAsync();
    return 0;
}

if (args is ["--verify-situation-contracts"])
{
    SituationContractVerifier.Verify();
    return 0;
}

if (args is ["--verify-situation-contracts", var sampleDirectory])
{
    SituationContractVerifier.Verify(sampleDirectory);
    return 0;
}

if (args.Length >= 3 && args[0] == "--verify-situation-source")
{
    var ticks = args[2..].Select(value => int.Parse(value, CultureInfo.InvariantCulture)).ToArray();
    await SituationSourceVerifier.VerifyAsync(args[1], ticks, CancellationToken.None);
    return 0;
}

if (args is ["--verify-situation-service"])
{
    await SituationSceneServiceVerifier.VerifyAsync(CancellationToken.None);
    return 0;
}

if (args is ["--verify-situation-sidecar-rebuild"])
{
    await SituationSidecarRebuilderVerifier.VerifyAsync(CancellationToken.None);
    return 0;
}

if (args is ["--verify-situation-cache"])
{
    await SituationSceneExecutionVerifier.VerifyAsync(CancellationToken.None);
    return 0;
}

if (args is ["--verify-situation-diagnostics"])
{
    await SituationSceneDiagnosticsVerifier.VerifyAsync(CancellationToken.None);
    return 0;
}

if (args is ["--verify-situation-stage-two-automatic"])
{
    await SituationStageTwoAutomaticVerifier.VerifyAsync(CancellationToken.None);
    return 0;
}

if (args is ["--verify-situation-rules"])
{
    SituationRuleVerifier.Verify();
    return 0;
}

if (args is ["--verify-situation-stage-three-automatic"])
{
    await SituationStageThreeAutomaticVerifier.VerifyAsync(CancellationToken.None);
    return 0;
}

if (args is ["--verify-situation-stage-four-split"])
{
    await SituationDatasetSplitVerifier.VerifyAsync(CancellationToken.None);
    return 0;
}

if (args is ["--verify-situation-stage-four-contracts"])
{
    await SituationTrainingContractVerifier.VerifyAsync(CancellationToken.None);
    return 0;
}

if (args is ["--verify-situation-stage-four-eligibility"])
{
    await RoundSampleEligibilityVerifier.VerifyAsync(CancellationToken.None);
    return 0;
}

if (args is ["--verify-situation-stage-four-selection"])
{
    SituationTrainingCandidateSelectorVerifier.Verify();
    return 0;
}

if (args is ["--verify-situation-stage-six-index"])
{
    await SituationTrainingTimelineIndexVerifier.VerifyAsync();
    return 0;
}
if (args is ["--verify-situation-stage-six-integration"])
{
    await SituationTrainingDatasetExporterVerifier.VerifyAsync(CancellationToken.None);
    return 0;
}
if (args is ["--verify-situation-stage-six-real-recovery", var realDemo, var realSplit, var benchmarkRoot, var recoveryRoot])
{
    await SituationTrainingDatasetExporterVerifier.VerifyRealRecoveryAsync(
        realDemo, realSplit, benchmarkRoot, recoveryRoot, CancellationToken.None);
    return 0;
}
if (args is ["--verify-situation-stage-six-index-benchmark", var indexDemo, var indexSplit, var indexOutput])
{
    await SituationTrainingDatasetExporterVerifier.VerifyIndexBenchmarkAsync(
        indexDemo, indexSplit, indexOutput, CancellationToken.None);
    return 0;
}
if (args is ["--verify-situation-training-dataset", var datasetRoot])
{
    var result = await SituationTrainingDatasetValidator.VerifyAsync(
        SituationArtifactIO.FindRepositoryRoot(Environment.CurrentDirectory), datasetRoot, CancellationToken.None);
    Console.WriteLine($"Full dataset verified: {result.JsonlRows} rows.");
    return 0;
}
if (args is ["--verify-situation-stage-six-automatic"])
{
    await SituationTrainingTimelineIndexVerifier.VerifyAsync();
    await SituationTrainingPartialWriterVerifier.VerifyAsync(CancellationToken.None);
    await SituationTrainingDatasetValidatorVerifier.VerifyAsync(CancellationToken.None);
    await SituationTrainingDatasetExporterVerifier.VerifyAsync(CancellationToken.None);
    return 0;
}
if (args is ["--verify-situation-stage-six-writer"])
{
    await SituationTrainingPartialWriterVerifier.VerifyAsync(CancellationToken.None);
    return 0;
}
if (args is ["--verify-situation-stage-six-validator"])
{
    await SituationTrainingDatasetValidatorVerifier.VerifyAsync(CancellationToken.None);
    return 0;
}
if (args is ["--verify-situation-stage-four-pilot"])
{
    SituationTrainingPilotVerifier.Verify();
    return 0;
}

if (args is ["--verify-situation-training-pilot", var pilotDirectory])
{
    await SituationTrainingPilotVerifier.VerifyArtifactsAsync(
        pilotDirectory, null, CancellationToken.None);
    return 0;
}

if (args is ["--verify-situation-training-pilot", var firstPilotDirectory, var repeatPilotDirectory])
{
    await SituationTrainingPilotVerifier.VerifyArtifactsAsync(
        firstPilotDirectory, repeatPilotDirectory, CancellationToken.None);
    return 0;
}

if (args is ["--verify-situation-training-source", var trainingSourceDemo])
{
    await SituationTrainingPilotVerifier.VerifySourceAsync(
        trainingSourceDemo, CancellationToken.None);
    return 0;
}

if (args is ["--verify-demo-prefix", var demoPath, var stopTick])
{
    await SemanticPrefixVerifier.VerifyAsync(
        demoPath,
        int.Parse(stopTick, CultureInfo.InvariantCulture),
        CancellationToken.None);
    return 0;
}

Console.Error.WriteLine("Unknown verification command. See docs/context/RUNBOOK.md.");
return 2;

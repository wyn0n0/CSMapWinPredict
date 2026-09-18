using System.Globalization;
using CsDemoMap.Api.Services;
using CsDemoMap.Api.Tests;

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

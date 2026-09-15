namespace CsDemoMap.Api.Services;

internal static class SituationStageTwoAutomaticVerifier
{
    public static async Task VerifyAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        SituationContractVerifier.Verify();
        await SituationSceneServiceVerifier.VerifyAsync(cancellationToken);
        await SituationSceneExecutionVerifier.VerifyAsync(cancellationToken);
        await SituationSidecarRebuilderVerifier.VerifyAsync(cancellationToken);
        await SituationSceneDiagnosticsVerifier.VerifyAsync(cancellationToken);
        await SituationStageTwoAcceptanceVerifier.VerifyAsync(cancellationToken);
        Console.WriteLine("Situation stage-two automatic verification suites passed: 6");
    }
}

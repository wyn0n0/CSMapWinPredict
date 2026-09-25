using CsDemoMap.Cli;

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    cancellation.Cancel();
};
try
{
    return await DeveloperCommandDispatcher.RunAsync(args, cancellation.Token);
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine("CLI operation canceled; incomplete export state is preserved.");
    return 130;
}
catch (CsDemoMap.Api.Services.ReviewException error)
{
    Console.Error.WriteLine($"Review operation failed ({error.Code}).");
    return 1;
}
catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException or InvalidOperationException)
{
    Console.Error.WriteLine($"CLI operation failed ({error.GetType().Name}); incomplete export state is preserved.");
    return 1;
}

using System.Diagnostics;
using System.Text;
using System.Text.Json;
using CsDemoMap.Api.Models;
using Microsoft.Extensions.Options;

namespace CsDemoMap.Api.Services;

public sealed class WinInferenceOptions
{
    public const string SectionName = "WinInference";

    public bool Enabled { get; set; } = true;
    public string PythonExecutable { get; set; } = "python";
    public string ScriptPath { get; set; } = "tools/win_inference_service.py";
    public string ModelDirectory { get; set; } =
        "models/win-baseline-v4-holdout-68-5-20260906";
    public int StartupTimeoutSeconds { get; set; } = 30;
    public int RequestTimeoutSeconds { get; set; } = 15;
    public int ShutdownTimeoutSeconds { get; set; } = 3;
    public int MaxBatchSize { get; set; } = 512;
    public int MaxRequestBytes { get; set; } = 4 * 1024 * 1024;
}

public sealed class WinInferenceException : Exception
{
    public WinInferenceException(string message) : base(message)
    {
    }

    public WinInferenceException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public sealed class WinInferenceClient : IHostedService, IAsyncDisposable
{
    public const int SchemaVersion = 4;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly WinInferenceOptions options;
    private readonly ILogger<WinInferenceClient> logger;
    private readonly SemaphoreSlim requestGate = new(1, 1);
    private readonly object stateGate = new();
    private readonly Queue<string> stderrTail = new();
    private Process? process;
    private Task? stderrTask;
    private CancellationTokenSource? stderrCancellation;
    private long nextRequestId;
    private bool stopping;
    private WinModelStatus status = EmptyStatus("stopped", null);

    public WinInferenceClient(
        IOptions<WinInferenceOptions> options,
        ILogger<WinInferenceClient> logger)
    {
        this.options = options.Value;
        this.logger = logger;
    }

    public int MaxBatchSize => options.MaxBatchSize;

    public WinModelStatus GetStatus()
    {
        lock (stateGate)
            return status;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!options.Enabled)
        {
            SetStatus(EmptyStatus("disabled", "WinInference is disabled by configuration."));
            return;
        }

        SetStatus(EmptyStatus("starting", null));
        try
        {
            ValidateOptions();
            var scriptPath = ResolveExistingPath(options.ScriptPath, directory: false);
            var modelDirectory = ResolveExistingPath(options.ModelDirectory, directory: true);
            var startInfo = new ProcessStartInfo
            {
                FileName = options.PythonExecutable,
                WorkingDirectory = Directory.GetParent(Path.GetDirectoryName(scriptPath)!)!.FullName,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardInputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                StandardOutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                StandardErrorEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)
            };
            startInfo.ArgumentList.Add(scriptPath);
            startInfo.ArgumentList.Add("--model-dir");
            startInfo.ArgumentList.Add(modelDirectory);
            startInfo.ArgumentList.Add("--max-batch-size");
            startInfo.ArgumentList.Add(options.MaxBatchSize.ToString());
            startInfo.ArgumentList.Add("--max-request-bytes");
            startInfo.ArgumentList.Add(options.MaxRequestBytes.ToString());
            startInfo.Environment["PYTHONUNBUFFERED"] = "1";

            var startedProcess = new Process
            {
                StartInfo = startInfo,
                EnableRaisingEvents = true
            };
            startedProcess.Exited += (_, _) => HandleUnexpectedExit(startedProcess);
            if (!startedProcess.Start())
                throw new WinInferenceException("Python inference process did not start.");

            process = startedProcess;
            stderrCancellation = new CancellationTokenSource();
            stderrTask = DrainStandardErrorAsync(startedProcess, stderrCancellation.Token);

            var result = await SendRequestAsync(
                "health",
                new { },
                TimeSpan.FromSeconds(options.StartupTimeoutSeconds),
                cancellationToken);
            var ready = ParseHealth(result);
            SetStatus(ready);
            logger.LogInformation(
                "胜率模型已就绪：{Model}, {SemanticVersion}, {FeatureCount} features",
                ready.SelectedModel,
                ready.SemanticVersion,
                ready.FeatureCount);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await TerminateProcessAsync();
            SetStatus(EmptyStatus("stopped", "API startup was cancelled."));
        }
        catch (Exception exception)
        {
            var detail = WithStderr(exception.Message);
            SetStatus(EmptyStatus("unavailable", detail));
            logger.LogError(exception, "无法启动胜率推理服务：{Detail}", detail);
            await TerminateProcessAsync();
        }
    }

    public async Task<IReadOnlyList<WinProbability>> PredictAsync(
        IReadOnlyList<WinInferenceSample> samples,
        CancellationToken cancellationToken)
    {
        if (samples.Count == 0)
            return Array.Empty<WinProbability>();
        if (samples.Count > options.MaxBatchSize)
            throw new ArgumentOutOfRangeException(
                nameof(samples),
                $"A prediction batch cannot exceed {options.MaxBatchSize} samples.");
        if (!GetStatus().Ready)
            throw new WinInferenceException(
                $"Win inference is unavailable: {GetStatus().Error ?? GetStatus().Status}");

        JsonElement result;
        try
        {
            result = await SendRequestAsync(
                "predict",
                new
                {
                    schemaVersion = SchemaVersion,
                    semanticVersion = WinFeatureSampleBuilder.SemanticVersion,
                    samples
                },
                TimeSpan.FromSeconds(options.RequestTimeoutSeconds),
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            await MarkUnavailableAsync(exception);
            throw;
        }

        try
        {
            var values = result.GetProperty("predictions");
            if (values.ValueKind != JsonValueKind.Array || values.GetArrayLength() != samples.Count)
                throw new WinInferenceException("Python returned a mismatched prediction count.");
            var probabilities = new List<WinProbability>(samples.Count);
            foreach (var value in values.EnumerateArray())
            {
                var tWin = value.GetProperty("tWin").GetDouble();
                var ctWin = value.GetProperty("ctWin").GetDouble();
                if (!double.IsFinite(tWin) || !double.IsFinite(ctWin) ||
                    tWin is < 0 or > 1 || ctWin is < 0 or > 1 ||
                    Math.Abs(tWin + ctWin - 1) > 1e-9)
                    throw new WinInferenceException("Python returned invalid win probabilities.");
                probabilities.Add(new(tWin, ctWin));
            }
            return probabilities;
        }
        catch (Exception exception) when (
            exception is WinInferenceException or JsonException or KeyNotFoundException or
                InvalidOperationException or FormatException or OverflowException)
        {
            var wrapped = exception as WinInferenceException ??
                new WinInferenceException("Python returned a malformed prediction result.", exception);
            await MarkUnavailableAsync(wrapped);
            throw wrapped;
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        stopping = true;
        SetStatus(EmptyStatus("stopping", null));
        var activeProcess = process;
        if (activeProcess is not null && !HasExited(activeProcess))
        {
            try
            {
                await SendRequestAsync(
                    "shutdown",
                    new { },
                    TimeSpan.FromSeconds(options.ShutdownTimeoutSeconds),
                    cancellationToken);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(options.ShutdownTimeoutSeconds));
                await activeProcess.WaitForExitAsync(timeout.Token);
            }
            catch (Exception exception) when (
                exception is WinInferenceException or IOException or TimeoutException or OperationCanceledException)
            {
                logger.LogWarning(exception, "胜率推理服务未能正常退出，将终止子进程。");
            }
        }
        await TerminateProcessAsync();
        SetStatus(EmptyStatus("stopped", null));
    }

    public async ValueTask DisposeAsync()
    {
        if (process is not null)
            await StopAsync(CancellationToken.None);
        requestGate.Dispose();
        stderrCancellation?.Dispose();
    }

    private async Task<JsonElement> SendRequestAsync(
        string operation,
        object body,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var lockTaken = false;
        try
        {
            // Cancellation while queued is safe. Once a request has been written, consume
            // its response before observing caller cancellation so the JSONL stream stays aligned.
            await requestGate.WaitAsync(cancellationToken);
            lockTaken = true;
            using var timeoutSource = new CancellationTokenSource(timeout);
            try
            {
                var token = timeoutSource.Token;
                var activeProcess = process;
                if (activeProcess is null || HasExited(activeProcess))
                    throw new WinInferenceException("Python inference process is not running.");

                var id = Interlocked.Increment(ref nextRequestId).ToString();
                using var bodyDocument = JsonSerializer.SerializeToDocument(body, JsonOptions);
                var request = new Dictionary<string, object?>
                {
                    ["id"] = id,
                    ["op"] = operation
                };
                foreach (var property in bodyDocument.RootElement.EnumerateObject())
                    request[property.Name] = property.Value.Clone();
                var line = JsonSerializer.Serialize(request, JsonOptions);
                if (Encoding.UTF8.GetByteCount(line) > options.MaxRequestBytes)
                    throw new WinInferenceException("Prediction request exceeds the configured byte limit.");

                await activeProcess.StandardInput.WriteLineAsync(line.AsMemory(), token);
                await activeProcess.StandardInput.FlushAsync(token);
                var responseLine = await activeProcess.StandardOutput.ReadLineAsync(token);
                if (responseLine is null)
                    throw new WinInferenceException(
                        WithStderr("Python inference process ended before returning a response."));

                using var response = JsonDocument.Parse(responseLine);
                var root = response.RootElement;
                var responseId = root.GetProperty("id").ValueKind == JsonValueKind.String
                    ? root.GetProperty("id").GetString()
                    : root.GetProperty("id").GetRawText();
                if (responseId != id)
                    throw new WinInferenceException(
                        $"Python response id '{responseId}' did not match request '{id}'.");
                if (!root.GetProperty("ok").GetBoolean())
                {
                    var error = root.GetProperty("error");
                    var code = error.GetProperty("code").GetString() ?? "unknown";
                    var message = error.GetProperty("message").GetString() ??
                        "Unknown inference error.";
                    throw new WinInferenceException(
                        $"Python rejected the request ({code}): {message}");
                }

                var result = root.GetProperty("result").Clone();
                cancellationToken.ThrowIfCancellationRequested();
                return result;
            }
            catch (OperationCanceledException) when (timeoutSource.IsCancellationRequested)
            {
                throw new TimeoutException(
                    $"Python inference request timed out after {timeout.TotalSeconds:F0} seconds.");
            }
        }
        catch (JsonException exception)
        {
            throw new WinInferenceException("Python returned malformed JSON.", exception);
        }
        finally
        {
            if (lockTaken)
                requestGate.Release();
        }
    }
    private static WinModelStatus ParseHealth(JsonElement result)
    {
        var runtime = result.GetProperty("runtime")
            .EnumerateObject()
            .ToDictionary(
                property => property.Name,
                property => property.Value.GetString() ?? string.Empty,
                StringComparer.Ordinal);
        var status = new WinModelStatus(
            result.GetProperty("status").GetString() ?? "unavailable",
            Ready: result.GetProperty("status").GetString() == "ready",
            result.GetProperty("schemaVersion").GetInt32(),
            result.GetProperty("semanticVersion").GetString(),
            result.GetProperty("selectedModel").GetString(),
            result.GetProperty("calibration").GetString(),
            result.GetProperty("featureCount").GetInt32(),
            result.GetProperty("selfTestFixtures").GetInt32(),
            result.GetProperty("artifactSha256").GetString(),
            runtime,
            Error: null);
        if (status.SchemaVersion != SchemaVersion ||
            status.SemanticVersion != WinFeatureSampleBuilder.SemanticVersion)
            throw new WinInferenceException("Python health response reported an incompatible model contract.");
        return status;
    }

    private void HandleUnexpectedExit(Process exitedProcess)
    {
        if (stopping || !ReferenceEquals(process, exitedProcess))
            return;
        var exitCode = TryGetExitCode(exitedProcess);
        var detail = WithStderr($"Python inference process exited unexpectedly with code {exitCode}.");
        SetStatus(EmptyStatus("unavailable", detail));
        logger.LogError("胜率推理子进程意外退出：{Detail}", detail);
    }

    private async Task DrainStandardErrorAsync(Process activeProcess, CancellationToken cancellationToken)
    {
        try
        {
            while (await activeProcess.StandardError.ReadLineAsync(cancellationToken) is { } line)
            {
                lock (stateGate)
                {
                    stderrTail.Enqueue(line);
                    while (stderrTail.Count > 10)
                        stderrTail.Dequeue();
                }
                logger.LogWarning("胜率推理服务 stderr: {Message}", line);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private async Task MarkUnavailableAsync(Exception exception)
    {
        var detail = WithStderr(exception.Message);
        SetStatus(EmptyStatus("unavailable", detail));
        logger.LogError(exception, "胜率推理服务通信失败：{Detail}", detail);
        await TerminateProcessAsync();
    }

    private async Task TerminateProcessAsync()
    {
        var activeProcess = Interlocked.Exchange(ref process, null);
        if (activeProcess is not null)
        {
            try
            {
                if (!HasExited(activeProcess))
                    activeProcess.Kill(entireProcessTree: true);
                await activeProcess.WaitForExitAsync();
            }
            catch (InvalidOperationException)
            {
            }
            finally
            {
                activeProcess.Dispose();
            }
        }

        if (stderrCancellation is not null)
            await stderrCancellation.CancelAsync();
        if (stderrTask is not null)
        {
            try
            {
                await stderrTask;
            }
            catch (ObjectDisposedException)
            {
            }
        }
        stderrTask = null;
    }

    private void ValidateOptions()
    {
        if (string.IsNullOrWhiteSpace(options.PythonExecutable))
            throw new WinInferenceException("WinInference:PythonExecutable is required.");
        if (options.StartupTimeoutSeconds <= 0 || options.RequestTimeoutSeconds <= 0 ||
            options.ShutdownTimeoutSeconds <= 0)
            throw new WinInferenceException("WinInference timeouts must be positive.");
        if (options.MaxBatchSize <= 0)
            throw new WinInferenceException("WinInference:MaxBatchSize must be positive.");
        if (options.MaxRequestBytes < 1024)
            throw new WinInferenceException("WinInference:MaxRequestBytes must be at least 1024.");
    }

    private static string ResolveExistingPath(string configuredPath, bool directory)
    {
        if (string.IsNullOrWhiteSpace(configuredPath))
            throw new WinInferenceException("A configured inference path is empty.");

        IEnumerable<string> Candidates()
        {
            if (Path.IsPathRooted(configuredPath))
            {
                yield return Path.GetFullPath(configuredPath);
                yield break;
            }

            foreach (var root in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
            {
                for (var current = new DirectoryInfo(root); current is not null; current = current.Parent)
                    yield return Path.GetFullPath(Path.Combine(current.FullName, configuredPath));
            }
        }

        foreach (var candidate in Candidates().Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (directory ? Directory.Exists(candidate) : File.Exists(candidate))
                return candidate;
        }
        throw new FileNotFoundException(
            $"Configured inference {(directory ? "directory" : "file")} was not found: {configuredPath}");
    }

    private string WithStderr(string message)
    {
        lock (stateGate)
            return stderrTail.Count == 0 ? message : $"{message} stderr: {string.Join(" | ", stderrTail)}";
    }

    private void SetStatus(WinModelStatus value)
    {
        lock (stateGate)
            status = value;
    }

    private static WinModelStatus EmptyStatus(string value, string? error) =>
        new(value, Ready: false, null, null, null, null, null, null, null, null, error);

    private static bool HasExited(Process value)
    {
        try
        {
            return value.HasExited;
        }
        catch (InvalidOperationException)
        {
            return true;
        }
    }

    private static int TryGetExitCode(Process value)
    {
        try
        {
            return value.ExitCode;
        }
        catch (InvalidOperationException)
        {
            return -1;
        }
    }
}

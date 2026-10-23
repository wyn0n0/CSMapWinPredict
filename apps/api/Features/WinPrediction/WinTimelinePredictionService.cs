using System.Diagnostics;
using CsDemoMap.Api.Models;

namespace CsDemoMap.Api.Services;

public sealed class WinTimelinePredictionService
{
    public const double SampleIntervalSeconds = 1;

    private readonly WinInferenceClient inference;
    private readonly ILogger<WinTimelinePredictionService> logger;

    public WinTimelinePredictionService(
        WinInferenceClient inference,
        ILogger<WinTimelinePredictionService> logger)
    {
        this.inference = inference;
        this.logger = logger;
    }

    public async Task<WinTimelinePredictionResult> PredictAsync(
        DemoTimeline timeline,
        CancellationToken cancellationToken)
    {
        var model = inference.GetStatus();
        if (!model.Ready)
            return Unavailable(model.Error ?? $"Model status is {model.Status}.", model);

        try
        {
            var stopwatch = Stopwatch.StartNew();
            var sampling = WinEventSampleBuilder.Build(timeline, cancellationToken);
            var samples = sampling.Samples;
            var points = new List<WinPredictionPoint>(samples.Count);
            for (var offset = 0; offset < samples.Count; offset += inference.MaxBatchSize)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var batch = samples.Skip(offset).Take(inference.MaxBatchSize).ToArray();
                var probabilities = await inference.PredictAsync(batch, cancellationToken);
                for (var index = 0; index < batch.Length; index++)
                {
                    var sample = batch[index];
                    var probability = probabilities[index];
                    points.Add(new(
                        sample.Tick,
                        sample.DemoTimeSeconds,
                        sample.RoundId,
                        sample.SegmentId,
                        sample.RoundNumber,
                        sample.Phase,
                        probability.TWin,
                        probability.CTWin,
                        Triggers: sampling.Triggers[sample.Tick]));
                }
            }

            stopwatch.Stop();
            logger.LogInformation(
                "胜率推理完成：{Samples} 个采样点，{ElapsedSeconds:F2} 秒",
                points.Count,
                stopwatch.Elapsed.TotalSeconds);
            // Before-only queries are review evidence, never an earlier live update.
            var visible = points.Where(p => p.Triggers!.Any(t => t is "regular" or "event-after")).ToArray();
            return new(
                ReadyManifest(model, visible.Length) with
                {
                    SamplingMode = timeline.WinEventStates.Count > 0 ? "regular-plus-events-v1" : "regular",
                    InferenceSampleCount = points.Count
                },
                visible, points);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            var current = inference.GetStatus();
            var error = current.Error ?? exception.Message;
            logger.LogWarning(exception, "胜率推理失败，Demo 将不含预测点：{Error}", error);
            return Unavailable(error, model);
        }
    }

    private static WinTimelinePredictionResult Unavailable(
        string error,
        WinModelStatus model) =>
        new(
            new(
                Status: "unavailable",
                SchemaVersion: WinInferenceClient.SchemaVersion,
                SemanticVersion: WinFeatureSampleBuilder.SemanticVersion,
                model.SelectedModel,
                model.Calibration,
                model.ArtifactSha256,
                SampleCount: 0,
                SampleIntervalSeconds,
                Error: error),
            Array.Empty<WinPredictionPoint>());

    private static WinPredictionManifest ReadyManifest(WinModelStatus model, int sampleCount) =>
        new(
            Status: "ready",
            SchemaVersion: WinInferenceClient.SchemaVersion,
            SemanticVersion: WinFeatureSampleBuilder.SemanticVersion,
            model.SelectedModel,
            model.Calibration,
            model.ArtifactSha256,
            sampleCount,
            SampleIntervalSeconds,
            Error: null);
}

using System.Text.Json.Nodes;
using CsDemoMap.Api.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CsDemoMap.Api.Services;

internal static class WinInferenceVerifier
{
    public static async Task VerifyAsync(CancellationToken cancellationToken = default)
    {
        var client = new WinInferenceClient(
            Options.Create(new WinInferenceOptions()),
            NullLogger<WinInferenceClient>.Instance);
        await client.StartAsync(cancellationToken);
        try
        {
            var status = client.GetStatus();
            Check(status.Ready, $"model should be ready: {status.Error}");
            Check(status.SchemaVersion == WinInferenceClient.SchemaVersion, "schema version should match");
            Check(
                status.SemanticVersion == WinFeatureSampleBuilder.SemanticVersion,
                "semantic version should match");
            Check(status.FeatureCount == 98, "feature count should match the v4 manifest");
            Check(status.SelfTestFixtures == 3, "Python startup fixtures should pass");

            var fixturePath = ResolveFixturePath();
            var fixtures = JsonNode.Parse(await File.ReadAllTextAsync(fixturePath, cancellationToken))!
                .AsArray();
            var samples = fixtures.Select((fixture, index) => BuildSample(fixture!.AsObject(), index))
                .ToArray();
            var predictions = await client.PredictAsync(
                samples.Select(value => value.Sample).ToArray(),
                cancellationToken);
            Check(predictions.Count == samples.Length, "prediction count should match");
            for (var index = 0; index < predictions.Count; index++)
            {
                Check(
                    Math.Abs(predictions[index].TWin - samples[index].ExpectedTWin) < 1e-12,
                    $"fixture {index} should match across .NET and Python");
                Check(
                    Math.Abs(predictions[index].TWin + predictions[index].CTWin - 1) < 1e-12,
                    $"fixture {index} probabilities should sum to one");
            }

            var timelinePredictor = new WinTimelinePredictionService(
                client,
                NullLogger<WinTimelinePredictionService>.Instance);
            var timelineResult = await timelinePredictor.PredictAsync(
                V4DataVerifier.CreateTimeline(9000),
                cancellationToken);
            Check(timelineResult.Manifest.Status == "ready", "timeline prediction should be ready");
            Check(timelineResult.Manifest.SampleCount == 2, "timeline manifest sample count");
            Check(timelineResult.Points.Count == 2, "timeline prediction point count");
            Check(
                timelineResult.Points.All(point =>
                    point.RoundId == "s0-a2" && point.RoundNumber == 1 && point.SegmentId == 0),
                "timeline prediction identity");
            Check(
                timelineResult.Points.All(point =>
                    double.IsFinite(point.TWin) && double.IsFinite(point.CTWin) &&
                    Math.Abs(point.TWin + point.CTWin - 1) < 1e-12),
                "timeline prediction probabilities");
        }
        finally
        {
            await client.StopAsync(CancellationToken.None);
            await client.DisposeAsync();
        }

        Console.WriteLine("Win inference checks passed: 17");
    }

    private static (WinInferenceSample Sample, double ExpectedTWin) BuildSample(
        JsonObject fixture,
        int index)
    {
        var flattened = fixture["features"]?.AsObject() ??
            throw new InvalidOperationException($"Fixture {index} has no features.");
        var roundNumber = (int)(flattened["roundNumber"]?.GetValue<double>() ??
            throw new InvalidOperationException($"Fixture {index} has no roundNumber."));
        var mapName = flattened["mapName"]?.GetValue<string>() ??
            throw new InvalidOperationException($"Fixture {index} has no mapName.");
        var features = new JsonObject();
        foreach (var property in flattened)
        {
            if (!property.Key.StartsWith("features.", StringComparison.Ordinal))
                continue;
            SetPath(features, property.Key["features.".Length..], property.Value?.DeepClone());
        }
        var phase = features["phase"]?.GetValue<string>() ??
            throw new InvalidOperationException($"Fixture {index} has no phase.");
        var expected = fixture["expectedTWin"]?.GetValue<double>() ??
            throw new InvalidOperationException($"Fixture {index} has no expectedTWin.");
        return (
            new WinInferenceSample(
                Tick: index,
                DemoTimeSeconds: index,
                RoundId: $"fixture-{index}",
                SegmentId: 0,
                roundNumber,
                mapName,
                phase,
                features),
            expected);
    }

    private static void SetPath(JsonObject root, string path, JsonNode? value)
    {
        var parts = path.Split('.');
        var current = root;
        foreach (var part in parts[..^1])
        {
            if (current[part] is not JsonObject nested)
            {
                nested = new JsonObject();
                current[part] = nested;
            }
            current = nested;
        }
        current[parts[^1]] = value;
    }

    private static string ResolveFixturePath()
    {
        const string configured =
            "models/win-baseline-v4-holdout-68-5-20260906/inference-fixtures.json";
        foreach (var root in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            for (var current = new DirectoryInfo(root); current is not null; current = current.Parent)
            {
                var candidate = Path.Combine(current.FullName, configured);
                if (File.Exists(candidate))
                    return candidate;
            }
        }
        throw new FileNotFoundException("Cannot locate v4 inference fixtures.", configured);
    }

    private static void Check(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}

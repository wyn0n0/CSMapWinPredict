using System.Text.Json;

namespace CsDemoMap.Api.Tests;

internal static class SemanticPrefixVerifier
{
    public static async Task VerifyAsync(string path, int stopTick, CancellationToken token)
    {
        await using var fullSource = File.OpenRead(path);
        var full = await new DemoParserService().ParseAsync(fullSource, Path.GetFileName(path), token, true);
        await using var prefixSource = File.OpenRead(path);
        var prefix = await new DemoParserService().ParseAsync(prefixSource, Path.GetFileName(path), token, true, stopTick);
        var expected = full.Semantics!.Frames.Where(f => f.Tick <= stopTick).ToArray();
        if (expected.Length != prefix.Semantics!.Frames.Count || expected.Length == 0)
            throw new InvalidOperationException("Prefix semantic sample count mismatch.");
        for (var i = 0; i < expected.Length; i++)
            if (JsonSerializer.Serialize(expected[i]) != JsonSerializer.Serialize(prefix.Semantics.Frames[i]))
                throw new InvalidOperationException($"Prefix semantic mismatch at {expected[i].Tick}");
        var expectedFrames = full.Frames.Where(f => f.Tick <= stopTick).ToArray();
        if (expectedFrames.Length != prefix.Frames.Count)
            throw new InvalidOperationException("Prefix replay sample count mismatch.");
        for (var i = 0; i < expectedFrames.Length; i++)
            if (JsonSerializer.Serialize(expectedFrames[i]) != JsonSerializer.Serialize(prefix.Frames[i]))
                throw new InvalidOperationException($"Prefix replay mismatch at {expectedFrames[i].Tick}");
        var fullFeatures = new AsOfTickFeatureBuilder(full);
        var prefixFeatures = new AsOfTickFeatureBuilder(prefix);
        foreach (var frame in prefix.Frames.Where(f => f.Round.Phase is "live" or "post-plant"))
            if (JsonSerializer.Serialize(fullFeatures.Build(frame)) != JsonSerializer.Serialize(prefixFeatures.Build(frame)))
                throw new InvalidOperationException($"Prefix feature mismatch at {frame.Tick}");
        Console.WriteLine($"Real DEM prefix verification passed: {expected.Length} snapshots through tick {stopTick}.");
    }
}

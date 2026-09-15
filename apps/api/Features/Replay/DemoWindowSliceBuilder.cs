using CsDemoMap.Api.Models;

namespace CsDemoMap.Api.Services;

internal sealed record DemoWindowSlice(
    DemoWindow Window,
    int DataFromTick,
    int DataToTick);

internal static class DemoWindowSliceBuilder
{
    internal const int WindowOverlapSeconds = 2;

    public static int GetWindowCount(DemoMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        if (!double.IsFinite(metadata.DurationSeconds) || metadata.DurationSeconds < 0)
            throw new InvalidDataException("Demo duration is invalid.");
        return Math.Max(1, checked((int)Math.Ceiling(
            metadata.DurationSeconds / DemoImportService.WindowSeconds)));
    }

    public static DemoWindowSlice Build(
        DemoTimeline timeline,
        int windowIndex,
        IReadOnlyList<WinPredictionPoint> winPredictions)
    {
        ArgumentNullException.ThrowIfNull(timeline);
        ArgumentNullException.ThrowIfNull(winPredictions);
        var windowCount = GetWindowCount(timeline.Metadata);
        if (windowIndex < 0 || windowIndex >= windowCount)
            throw new ArgumentOutOfRangeException(nameof(windowIndex));
        if (timeline.Metadata.TickRate <= 0)
            throw new InvalidDataException("Demo tick rate must be positive.");

        var duration = Math.Max(0, timeline.Metadata.DurationSeconds);
        var coreFrom = windowIndex * (double)DemoImportService.WindowSeconds;
        var coreTo = Math.Min(duration, (windowIndex + 1) * (double)DemoImportService.WindowSeconds);
        var dataFrom = Math.Max(0d, coreFrom - WindowOverlapSeconds);
        var dataTo = Math.Min(duration, coreTo + WindowOverlapSeconds);
        var startTick = checked((int)Math.Floor(dataFrom * timeline.Metadata.TickRate));
        var endTick = checked((int)Math.Ceiling(dataTo * timeline.Metadata.TickRate));

        var indexedFrames = timeline.Frames
            .Select((frame, frameIndex) => (frame, frameIndex))
            .Where(item => item.frame.TimeSeconds >= dataFrom && item.frame.TimeSeconds <= dataTo)
            .ToArray();
        var frames = indexedFrames.Select(item => item.frame).ToArray();
        var firstFrameIndex = indexedFrames.Length == 0 ? 0 : indexedFrames[0].frameIndex;
        var utilityTracks = timeline.UtilityTracks
            .Where(track => track.EndTick >= startTick && track.StartTick <= endTick)
            .Select(track => track with
            {
                Trajectory = SliceSamples(track.Trajectory, startTick, endTick, point => point.Tick)
            })
            .Where(track => track.Trajectory.Count > 0)
            .ToArray();
        var utilityEffects = timeline.UtilityEffects
            .Where(effect => effect.EndTick >= startTick && effect.StartTick <= endTick)
            .Select(effect => effect with
            {
                Samples = SliceSamples(effect.Samples, startTick, endTick, sample => sample.Tick)
            })
            .Where(effect => effect.Samples.Count > 0)
            .ToArray();
        var utilityStates = timeline.PlayerUtilityStates
            .GroupBy(state => state.PlayerId)
            .SelectMany(group => SliceStateChanges(group, startTick, endTick))
            .OrderBy(state => state.Tick)
            .ToArray();
        var equipmentStates = timeline.PlayerEquipmentStates
            .GroupBy(state => state.PlayerId)
            .SelectMany(group => SliceEquipmentStateChanges(group, startTick, endTick))
            .OrderBy(state => state.Tick)
            .ToArray();
        var windowPredictionPoints = winPredictions
            .Where(point => point.TimeSeconds >= dataFrom && point.TimeSeconds <= dataTo)
            .ToArray();

        return new(
            new(
                windowIndex,
                coreFrom,
                coreTo,
                dataFrom,
                dataTo,
                firstFrameIndex,
                timeline.Frames.Count,
                frames,
                utilityTracks,
                utilityEffects,
                utilityStates,
                equipmentStates,
                windowPredictionPoints),
            startTick,
            endTick);
    }

    private static IReadOnlyList<T> SliceSamples<T>(
        IReadOnlyList<T> items,
        int startTick,
        int endTick,
        Func<T, int> getTick)
    {
        if (items.Count == 0)
            return [];

        var startIndex = FindLastAtOrBefore(items, startTick, getTick);
        if (startIndex < 0)
            startIndex = 0;

        var result = new List<T>();
        for (var index = startIndex; index < items.Count; index++)
        {
            var item = items[index];
            result.Add(item);
            if (getTick(item) > endTick)
                break;
        }
        return result;
    }

    private static IEnumerable<PlayerUtilityState> SliceStateChanges(
        IEnumerable<PlayerUtilityState> source,
        int startTick,
        int endTick)
    {
        var states = source.OrderBy(item => item.Tick).ToArray();
        var previous = states.LastOrDefault(item => item.Tick <= startTick);
        if (previous is not null)
            yield return previous;

        foreach (var state in states.Where(item => item.Tick > startTick && item.Tick <= endTick))
            yield return state;
    }

    private static IEnumerable<PlayerEquipmentState> SliceEquipmentStateChanges(
        IEnumerable<PlayerEquipmentState> source,
        int startTick,
        int endTick)
    {
        var states = source.OrderBy(item => item.Tick).ToArray();
        var previous = states.LastOrDefault(item => item.Tick <= startTick);
        if (previous is not null)
            yield return previous;

        foreach (var state in states.Where(item => item.Tick > startTick && item.Tick <= endTick))
            yield return state;
    }

    private static int FindLastAtOrBefore<T>(
        IReadOnlyList<T> items,
        int tick,
        Func<T, int> getTick)
    {
        var low = 0;
        var high = items.Count - 1;
        while (low <= high)
        {
            var middle = low + (high - low) / 2;
            if (getTick(items[middle]) <= tick)
                low = middle + 1;
            else
                high = middle - 1;
        }
        return high;
    }
}

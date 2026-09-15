using CsDemoMap.Api.Models;

namespace CsDemoMap.Api.Services;

internal sealed record SituationSceneServiceLimits(
    int MaxCacheEntries,
    long MaxCacheUtf8Bytes,
    int MaxConcurrentBuilds,
    int MaxQueuedBuilds)
{
    public static SituationSceneServiceLimits Default { get; } = new(
        256,
        64L * 1024 * 1024,
        2,
        32);

    public void Validate()
    {
        if (MaxCacheEntries <= 0)
            throw new ArgumentOutOfRangeException(nameof(MaxCacheEntries));
        if (MaxCacheUtf8Bytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(MaxCacheUtf8Bytes));
        if (MaxConcurrentBuilds <= 0)
            throw new ArgumentOutOfRangeException(nameof(MaxConcurrentBuilds));
        if (MaxQueuedBuilds < 0)
            throw new ArgumentOutOfRangeException(nameof(MaxQueuedBuilds));
    }
}

internal readonly record struct SituationSceneCacheKey(
    string SourceRevision,
    string DemoRef,
    int WindowIndex,
    int RequestedTick,
    string SceneVersion,
    string SceneBuilderVersion,
    string GeometryVersion);

internal sealed record SituationSceneCacheValue(
    string CanonicalJson,
    string Sha256,
    int Utf8Bytes)
{
    public SituationSceneBuildResult Materialize()
    {
        var scene = SituationCanonicalJson.Deserialize<MinimapSceneV1>(CanonicalJson);
        return new(scene, CanonicalJson, Sha256);
    }
}

internal sealed record SituationSceneExecutionSnapshot(
    int CacheEntries,
    long CacheUtf8Bytes,
    long CacheHits,
    long CacheMisses,
    long CacheEvictions,
    long BuildExecutions,
    int ActiveBuilds,
    int QueuedBuilds,
    int InflightKeys);

internal sealed class SituationSceneResultCache
{
    private readonly int maxEntries;
    private readonly long maxUtf8Bytes;
    private readonly object sync = new();
    private readonly Dictionary<SituationSceneCacheKey, LinkedListNode<CacheEntry>> entries = [];
    private readonly LinkedList<CacheEntry> lru = [];
    private long utf8Bytes;
    private long hits;
    private long misses;
    private long evictions;

    public SituationSceneResultCache(SituationSceneServiceLimits limits)
    {
        maxEntries = limits.MaxCacheEntries;
        maxUtf8Bytes = limits.MaxCacheUtf8Bytes;
    }

    public bool TryGet(SituationSceneCacheKey key, out SituationSceneCacheValue value)
    {
        lock (sync)
        {
            if (!entries.TryGetValue(key, out var node))
            {
                misses++;
                value = null!;
                return false;
            }

            lru.Remove(node);
            lru.AddFirst(node);
            hits++;
            value = node.Value.Value;
            return true;
        }
    }

    public void Set(SituationSceneCacheKey key, SituationSceneCacheValue value)
    {
        if (value.Utf8Bytes > maxUtf8Bytes)
            return;

        lock (sync)
        {
            if (entries.Remove(key, out var existing))
            {
                lru.Remove(existing);
                utf8Bytes -= existing.Value.Value.Utf8Bytes;
            }

            var node = lru.AddFirst(new CacheEntry(key, value));
            entries.Add(key, node);
            utf8Bytes += value.Utf8Bytes;
            while (entries.Count > maxEntries || utf8Bytes > maxUtf8Bytes)
            {
                var last = lru.Last
                    ?? throw new InvalidOperationException("Situation scene cache accounting is inconsistent.");
                lru.RemoveLast();
                entries.Remove(last.Value.Key);
                utf8Bytes -= last.Value.Value.Utf8Bytes;
                evictions++;
            }
        }
    }

    public (int Entries, long Utf8Bytes, long Hits, long Misses, long Evictions) Snapshot()
    {
        lock (sync)
            return (entries.Count, utf8Bytes, hits, misses, evictions);
    }

    private sealed record CacheEntry(SituationSceneCacheKey Key, SituationSceneCacheValue Value);
}

internal sealed class SituationServiceBusyException : InvalidOperationException
{
    public SituationServiceBusyException()
        : base("Situation scene service is busy.")
    {
    }
}

internal sealed class SituationSceneBuildCoordinator<TKey, TValue> : IDisposable
    where TKey : notnull
{
    private readonly object sync = new();
    private readonly Dictionary<TKey, InflightWork> inflight = [];
    private readonly SemaphoreSlim buildSlots;
    private readonly int maxQueuedBuilds;
    private int activeBuilds;
    private int queuedBuilds;
    private long buildExecutions;
    private bool disposed;

    public SituationSceneBuildCoordinator(int maxConcurrentBuilds, int maxQueuedBuilds)
    {
        if (maxConcurrentBuilds <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxConcurrentBuilds));
        if (maxQueuedBuilds < 0)
            throw new ArgumentOutOfRangeException(nameof(maxQueuedBuilds));
        buildSlots = new(maxConcurrentBuilds, maxConcurrentBuilds);
        this.maxQueuedBuilds = maxQueuedBuilds;
    }

    public Task<TValue> RunAsync(
        TKey key,
        Func<CancellationToken, Task<TValue>> factory,
        CancellationToken callerCancellation)
    {
        ArgumentNullException.ThrowIfNull(factory);
        callerCancellation.ThrowIfCancellationRequested();

        InflightWork work;
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (!inflight.TryGetValue(key, out work!))
            {
                work = new InflightWork(key);
                inflight.Add(key, work);
                work.WaiterCount = 1;
                work.Task = ExecuteAsync(key, work, factory);
            }
            else
            {
                work.WaiterCount++;
            }
        }

        return WaitForCallerAsync(work, callerCancellation);
    }

    public (long Executions, int Active, int Queued, int Inflight) Snapshot()
    {
        lock (sync)
            return (buildExecutions, activeBuilds, queuedBuilds, inflight.Count);
    }

    public void Dispose()
    {
        InflightWork[] running;
        lock (sync)
        {
            if (disposed)
                return;
            disposed = true;
            running = inflight.Values.ToArray();
        }
        foreach (var work in running)
            work.Cancellation.Cancel();
    }

    private async Task<TValue> WaitForCallerAsync(
        InflightWork work,
        CancellationToken callerCancellation)
    {
        try
        {
            return await work.Task.WaitAsync(callerCancellation);
        }
        finally
        {
            lock (sync)
            {
                work.WaiterCount--;
                if (work.WaiterCount == 0)
                {
                    if (!work.Task.IsCompleted)
                    {
                        if (inflight.TryGetValue(work.Key, out var current) && ReferenceEquals(current, work))
                            inflight.Remove(work.Key);
                        work.Cancellation.Cancel();
                    }
                    else
                        work.Cancellation.Dispose();
                }
            }
        }
    }

    private async Task<TValue> ExecuteAsync(
        TKey key,
        InflightWork work,
        Func<CancellationToken, Task<TValue>> factory)
    {
        var slotHeld = false;
        var queueReserved = false;
        var activeCounted = false;
        try
        {
            if (!buildSlots.Wait(0))
            {
                lock (sync)
                {
                    if (queuedBuilds >= maxQueuedBuilds)
                        throw new SituationServiceBusyException();
                    queuedBuilds++;
                    queueReserved = true;
                }

                await buildSlots.WaitAsync(work.Cancellation.Token);
                slotHeld = true;
                lock (sync)
                {
                    queuedBuilds--;
                    queueReserved = false;
                }
            }
            else
            {
                slotHeld = true;
            }

            lock (sync)
            {
                activeBuilds++;
                buildExecutions++;
                activeCounted = true;
            }
            return await factory(work.Cancellation.Token);
        }
        finally
        {
            if (queueReserved)
            {
                lock (sync)
                    queuedBuilds--;
            }
            if (activeCounted)
            {
                lock (sync)
                    activeBuilds--;
            }
            if (slotHeld)
                buildSlots.Release();

            lock (sync)
            {
                if (inflight.TryGetValue(key, out var current) && ReferenceEquals(current, work))
                    inflight.Remove(key);
                if (work.WaiterCount == 0)
                    work.Cancellation.Dispose();
            }
        }
    }

    private sealed class InflightWork(TKey key)
    {
        public TKey Key { get; } = key;
        public CancellationTokenSource Cancellation { get; } = new();
        public Task<TValue> Task { get; set; } = null!;
        public int WaiterCount { get; set; }
    }
}

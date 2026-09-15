using CsDemoMap.Api.Models;

namespace CsDemoMap.Api.Tests;

internal static class SituationSceneExecutionVerifier
{
    public static async Task VerifyAsync(CancellationToken cancellationToken)
    {
        var checks = 0;
        var root = Path.Combine(
            Path.GetTempPath(),
            $"situation-scene-execution-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);

        try
        {
            Check(SituationSceneServiceLimits.Default == new SituationSceneServiceLimits(
                    256, 64L * 1024 * 1024, 2, 32),
                "default cache and concurrency limits are frozen", ref checks);
            var timeline = SituationFlowVerifier.BuildBoundaryTimeline(
                reversePlayers: false,
                reverseRelations: false);
            var windowCount = DemoWindowSliceBuilder.GetWindowCount(timeline.Metadata);
            for (var index = 0; index < windowCount; index++)
            {
                var slice = DemoWindowSliceBuilder.Build(timeline, index, []);
                await SituationFlowVerifier.WriteWindowAsync(root, slice.Window);
                await SituationWindowSidecarStore.WriteAsync(
                    root,
                    SituationWindowSidecarStore.Build(
                        timeline, index, slice.DataFromTick, slice.DataToTick),
                    cancellationToken);
            }

            var limits = new SituationSceneServiceLimits(2, 64L * 1024 * 1024, 2, 32);
            using var cachedService = CreateService(root, timeline.Metadata, windowCount, limits);
            var first = await cachedService.BuildFromImportedDemoAsync(
                "cache-fixture", 1, 1940, cancellationToken);
            var firstSnapshot = cachedService.GetExecutionSnapshot();
            var second = await cachedService.BuildFromImportedDemoAsync(
                "cache-fixture", 1, 1940, cancellationToken);
            var secondSnapshot = cachedService.GetExecutionSnapshot();
            Check(first.CanonicalJson == second.CanonicalJson && first.Sha256 == second.Sha256 &&
                    !ReferenceEquals(first.Scene, second.Scene) &&
                    firstSnapshot.BuildExecutions == 1 &&
                    secondSnapshot.BuildExecutions == 1 &&
                    secondSnapshot.CacheHits == 1 &&
                    secondSnapshot.CacheEntries == 1,
                "successful file-backed results are cached and materialized independently", ref checks);

            if (second.Scene.Players is not IList<SituationPlayer> mutablePlayers)
                throw new InvalidOperationException("Cache verifier expected a mutable deserialized fixture list.");
            mutablePlayers[0] = mutablePlayers[0] with { Health = 1 };
            var afterCallerMutation = await cachedService.BuildFromImportedDemoAsync(
                "cache-fixture", 1, 1940, cancellationToken);
            Check(afterCallerMutation.CanonicalJson == first.CanonicalJson &&
                    afterCallerMutation.Scene.Players[0].Health == first.Scene.Players[0].Health,
                "caller mutation cannot contaminate a cached response", ref checks);

            using var lruService = CreateService(root, timeline.Metadata, windowCount, limits);
            _ = await lruService.BuildFromImportedDemoAsync("cache-fixture", 1, 1940, cancellationToken);
            _ = await lruService.BuildFromImportedDemoAsync("cache-fixture", 1, 1941, cancellationToken);
            _ = await lruService.BuildFromImportedDemoAsync("cache-fixture", 1, 1942, cancellationToken);
            var evicted = lruService.GetExecutionSnapshot();
            _ = await lruService.BuildFromImportedDemoAsync("cache-fixture", 1, 1940, cancellationToken);
            var rebuilt = lruService.GetExecutionSnapshot();
            Check(evicted.CacheEntries == 2 && evicted.CacheEvictions == 1 &&
                    evicted.BuildExecutions == 3 && rebuilt.BuildExecutions == 4,
                "LRU count eviction and requested-tick cache keys are enforced", ref checks);

            var tinyLimits = limits with { MaxCacheUtf8Bytes = 1 };
            using var tinyService = CreateService(root, timeline.Metadata, windowCount, tinyLimits);
            _ = await tinyService.BuildFromImportedDemoAsync("cache-fixture", 1, 1940, cancellationToken);
            _ = await tinyService.BuildFromImportedDemoAsync("cache-fixture", 1, 1940, cancellationToken);
            var tiny = tinyService.GetExecutionSnapshot();
            Check(tiny.CacheEntries == 0 && tiny.CacheUtf8Bytes == 0 && tiny.BuildExecutions == 2,
                "an oversized result is returned without being cached", ref checks);

            var resultBytes = System.Text.Encoding.UTF8.GetByteCount(first.CanonicalJson);
            var byteLimits = limits with
            {
                MaxCacheEntries = 10,
                MaxCacheUtf8Bytes = resultBytes * 2L - 1
            };
            using var byteBoundedService = CreateService(
                root, timeline.Metadata, windowCount, byteLimits);
            _ = await byteBoundedService.BuildFromImportedDemoAsync(
                "cache-fixture", 1, 1940, cancellationToken);
            _ = await byteBoundedService.BuildFromImportedDemoAsync(
                "cache-fixture", 1, 1941, cancellationToken);
            var byteBounded = byteBoundedService.GetExecutionSnapshot();
            Check(byteBounded.CacheEntries == 1 && byteBounded.CacheEvictions == 1 &&
                    byteBounded.CacheUtf8Bytes <= byteLimits.MaxCacheUtf8Bytes,
                "aggregate UTF-8 cache bytes trigger LRU eviction", ref checks);

            using var revisionService = CreateService(root, timeline.Metadata, windowCount, limits);
            var beforeTouch = await revisionService.BuildFromImportedDemoAsync(
                "cache-fixture", 1, 1940, cancellationToken);
            var sidecarPath = SituationWindowSidecarStore.GetPath(root, 1);
            File.SetLastWriteTimeUtc(sidecarPath, File.GetLastWriteTimeUtc(sidecarPath).AddSeconds(2));
            var afterTouch = await revisionService.BuildFromImportedDemoAsync(
                "cache-fixture", 1, 1940, cancellationToken);
            Check(beforeTouch.CanonicalJson == afterTouch.CanonicalJson &&
                    revisionService.GetExecutionSnapshot().BuildExecutions == 1,
                "metadata-only file changes preserve a content-addressed cache entry", ref checks);

            var originalSidecarBytes = await File.ReadAllBytesAsync(sidecarPath, cancellationToken);
            var originalSidecarTimestamp = File.GetLastWriteTimeUtc(sidecarPath);
            await File.WriteAllBytesAsync(
                sidecarPath, new byte[originalSidecarBytes.Length], cancellationToken);
            File.SetLastWriteTimeUtc(sidecarPath, originalSidecarTimestamp);
            await ExpectSituationErrorAsync(
                () => revisionService.BuildFromImportedDemoAsync(
                    "cache-fixture", 1, 1940, cancellationToken),
                SituationSceneErrorCode.CorruptData);
            checks++;
            await File.WriteAllBytesAsync(sidecarPath, originalSidecarBytes, cancellationToken);
            File.SetLastWriteTimeUtc(sidecarPath, originalSidecarTimestamp);

            using var serviceStopping = new CancellationTokenSource();
            using var stoppedService = new SituationSceneService(
                id => id == "cache-fixture"
                    ? new("cache-fixture", "completed", root, timeline.Metadata, windowCount)
                    : null,
                null,
                null,
                limits,
                serviceStopping.Token);
            serviceStopping.Cancel();
            await ExpectSituationErrorAsync(
                () => stoppedService.BuildFromImportedDemoAsync(
                    "cache-fixture", 1, 1940, cancellationToken),
                SituationSceneErrorCode.ServiceBusy);
            checks++;

            checks += await VerifySharedWorkAsync(cancellationToken);
            checks += await VerifyAllWaitersCancelAsync(cancellationToken);
            checks += await VerifyFailureRetryAsync(cancellationToken);
            checks += await VerifyQueueLimitAsync(cancellationToken);
            checks += await VerifyServiceShutdownAsync(cancellationToken);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }

        Console.WriteLine($"Situation scene cache/concurrency checks passed: {checks}");
    }

    private static SituationSceneService CreateService(
        string directory,
        DemoMetadata metadata,
        int windowCount,
        SituationSceneServiceLimits limits) => new(
            id => id == "cache-fixture"
                ? new("cache-fixture", "completed", directory, metadata, windowCount)
                : null,
            null,
            null,
            limits);

    private static async Task<int> VerifySharedWorkAsync(CancellationToken cancellationToken)
    {
        using var coordinator = new SituationSceneBuildCoordinator<string, int>(2, 32);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var executions = 0;
        var underlyingCanceled = false;
        async Task<int> Build(CancellationToken token)
        {
            Interlocked.Increment(ref executions);
            started.TrySetResult();
            try
            {
                await release.Task.WaitAsync(token);
                return 7;
            }
            catch (OperationCanceledException)
            {
                underlyingCanceled = true;
                throw;
            }
        }

        using var canceledCaller = new CancellationTokenSource();
        var canceledWait = coordinator.RunAsync("shared", Build, canceledCaller.Token);
        var remaining = Enumerable.Range(0, 7)
            .Select(_ => coordinator.RunAsync("shared", Build, cancellationToken))
            .ToArray();
        await started.Task.WaitAsync(cancellationToken);
        canceledCaller.Cancel();
        await ExpectThrowsAsync<OperationCanceledException>(() => canceledWait);
        if (underlyingCanceled)
            throw new InvalidOperationException("A single caller canceled shared work.");
        release.TrySetResult();
        var values = await Task.WhenAll(remaining);
        if (executions != 1 || values.Any(value => value != 7))
            throw new InvalidOperationException("Same-key requests did not share one build.");
        return 2;
    }

    private static async Task<int> VerifyAllWaitersCancelAsync(CancellationToken cancellationToken)
    {
        using var coordinator = new SituationSceneBuildCoordinator<string, int>(2, 32);
        using var firstCaller = new CancellationTokenSource();
        using var secondCaller = new CancellationTokenSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var underlyingCanceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<int> Build(CancellationToken token)
        {
            started.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return 0;
            }
            catch (OperationCanceledException)
            {
                underlyingCanceled.TrySetResult();
                throw;
            }
        }

        var first = coordinator.RunAsync("cancel-all", Build, firstCaller.Token);
        var second = coordinator.RunAsync("cancel-all", Build, secondCaller.Token);
        await started.Task.WaitAsync(cancellationToken);
        firstCaller.Cancel();
        secondCaller.Cancel();
        await ExpectThrowsAsync<OperationCanceledException>(() => first);
        await ExpectThrowsAsync<OperationCanceledException>(() => second);
        var retry = await coordinator.RunAsync("cancel-all", _ => Task.FromResult(9), cancellationToken);
        await underlyingCanceled.Task.WaitAsync(cancellationToken);
        if (retry != 9)
            throw new InvalidOperationException("Canceled shared work could not be retried immediately.");
        return 2;
    }

    private static async Task<int> VerifyFailureRetryAsync(CancellationToken cancellationToken)
    {
        using var coordinator = new SituationSceneBuildCoordinator<string, int>(2, 32);
        await ExpectThrowsAsync<InvalidDataException>(() => coordinator.RunAsync(
            "failure", _ => Task.FromException<int>(new InvalidDataException("fixture")), cancellationToken));
        var retried = await coordinator.RunAsync("failure", _ => Task.FromResult(11), cancellationToken);
        if (retried != 11 || coordinator.Snapshot().Executions != 2)
            throw new InvalidOperationException("Failed work remained in the in-flight registry.");
        return 1;
    }

    private static async Task<int> VerifyQueueLimitAsync(CancellationToken cancellationToken)
    {
        using var coordinator = new SituationSceneBuildCoordinator<int, int>(2, 32);
        using var callers = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<int> Build(int value, CancellationToken token)
        {
            await release.Task.WaitAsync(token);
            return value;
        }

        var pending = Enumerable.Range(0, 34)
            .Select(value => coordinator.RunAsync(
                value, token => Build(value, token), callers.Token))
            .ToArray();
        await WaitUntilAsync(() =>
        {
            var snapshot = coordinator.Snapshot();
            return snapshot.Active == 2 && snapshot.Queued == 32;
        }, cancellationToken);
        await ExpectThrowsAsync<SituationServiceBusyException>(() => coordinator.RunAsync(
            34, token => Build(34, token), callers.Token));
        var saturated = coordinator.Snapshot();
        if (saturated.Active != 2 || saturated.Queued != 32)
            throw new InvalidOperationException("Build concurrency or queue limits changed under saturation.");

        callers.Cancel();
        foreach (var task in pending)
            await IgnoreCancellationAsync(task);
        await WaitUntilAsync(() => coordinator.Snapshot().Inflight == 0, cancellationToken);
        return 2;
    }

    private static async Task<int> VerifyServiceShutdownAsync(CancellationToken cancellationToken)
    {
        var coordinator = new SituationSceneBuildCoordinator<string, int>(2, 32);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var task = coordinator.RunAsync("shutdown", async token =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return 0;
        }, cancellationToken);
        await started.Task.WaitAsync(cancellationToken);
        coordinator.Dispose();
        await ExpectThrowsAsync<OperationCanceledException>(() => task);
        return 1;
    }

    private static async Task WaitUntilAsync(Func<bool> predicate, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        while (!predicate())
            await Task.Delay(5, timeout.Token);
    }

    private static async Task IgnoreCancellationAsync(Task task)
    {
        try
        {
            await task;
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static async Task ExpectThrowsAsync<T>(Func<Task> action) where T : Exception
    {
        try
        {
            await action();
        }
        catch (T)
        {
            return;
        }
        throw new InvalidOperationException($"Expected {typeof(T).Name} was not thrown.");
    }

    private static async Task ExpectSituationErrorAsync(
        Func<Task> action,
        SituationSceneErrorCode expectedCode)
    {
        try
        {
            await action();
        }
        catch (SituationSceneException exception) when (exception.Code == expectedCode)
        {
            return;
        }
        throw new InvalidOperationException($"Expected situation error {expectedCode} was not thrown.");
    }

    private static void Check(bool condition, string label, ref int checks)
    {
        if (!condition)
            throw new InvalidOperationException($"Situation scene execution check failed: {label}.");
        checks++;
    }
}

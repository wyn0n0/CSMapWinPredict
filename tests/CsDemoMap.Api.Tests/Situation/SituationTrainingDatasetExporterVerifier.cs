using CsDemoMap.Api.Models;
using CsDemoMap.Api.Services;

namespace CsDemoMap.Api.Tests;

internal static class SituationTrainingDatasetExporterVerifier
{
    internal static async Task VerifyIndexBenchmarkAsync(string demoRoot, string splitPath, string newRoot, CancellationToken token)
    {
        SituationArtifactIO.EnsureNewOutput(newRoot);
        Directory.CreateDirectory(newRoot);
        await SituationArtifactIO.WriteJsonAsync(Path.Combine(newRoot, "manifest.json"),
            new { status = "incomplete", purpose = "train-index-benchmark", trainable = false }, token);
        var split = await SituationDatasetSplit.LoadApprovedAsync(splitPath, token);
        var versions = SituationTrainingRecordBuilder.CreateIdentityVersions(split.SplitSha256);
        var members = split.Split.Train.OrderBy(member => SituationTrainingContractJson.CreateIdentity(
            member.MatchId, "s0-a1", 0, versions).MatchRef, StringComparer.Ordinal).Take(5).ToArray();
        var metrics = new List<object>();
        foreach (var member in members)
        {
            var path = Path.Combine(demoRoot, member.FileName);
            if (await SituationArtifactIO.FileSha256Async(path, token) != member.MatchId)
                throw new InvalidDataException("Benchmark source hash differs.");
            await using var stream = File.OpenRead(path);
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var timeline = await new DemoParserService().ParseAsync(stream, member.FileName, token, collectSemantics: true);
            var parse = watch.Elapsed.TotalSeconds;
            watch.Restart();
            var index = SituationTrainingTimelineIndex.Create(timeline, token);
            var indexing = watch.Elapsed.TotalSeconds;
            using var service = new SituationSceneService();
            var selector = new SituationTrainingCandidateSelector(service);
            var attempts = index.CompletedAttempts;
            var old = new SituationTrainingRoundSelectionResult[attempts.Count];
            watch.Restart();
            await Parallel.ForEachAsync(Enumerable.Range(0, attempts.Count), new ParallelOptions
            { MaxDegreeOfParallelism = 4, CancellationToken = token }, (ordinal, cancellation) =>
            {
                old[ordinal] = selector.SelectRound(timeline, member.MatchId, attempts[ordinal], cancellation);
                return ValueTask.CompletedTask;
            });
            var legacy = watch.Elapsed.TotalSeconds;
            watch.Restart();
            var indexed = new SituationTrainingRoundSelectionWithPayloads[attempts.Count];
            await Parallel.ForEachAsync(Enumerable.Range(0, attempts.Count), new ParallelOptions
            { MaxDegreeOfParallelism = 4, CancellationToken = token }, (ordinal, cancellation) =>
            {
                indexed[ordinal] = selector.SelectRound(index, member.MatchId, attempts[ordinal].RoundId, cancellation);
                return ValueTask.CompletedTask;
            });
            var reused = watch.Elapsed.TotalSeconds;
            if (!old.Select(item => item.Sha256).SequenceEqual(indexed.Select(item => item.SelectionResult.Sha256)))
                throw new InvalidDataException("Real legacy/indexed selector hashes differ.");
            var metric = new { matchRef = SituationTrainingContractJson.CreateIdentity(member.MatchId, "s0-a1", 0, versions).MatchRef,
                parseSeconds = parse, indexSeconds = indexing, legacySelectionSeconds = legacy,
                indexedPayloadSeconds = reused, rounds = attempts.Count,
                samples = old.Sum(item => item.Selection.Samples.Count) };
            metrics.Add(metric);
            Console.WriteLine($"Real index benchmark {metrics.Count}/5: legacy={legacy:F3}s indexedPayload={reused:F3}s; all selection SHA identical.");
            await SituationArtifactIO.WriteJsonAsync(Path.Combine(newRoot, "metrics.json"), metrics, token);
        }
    }

    internal static async Task VerifyRealRecoveryAsync(
        string demoRoot, string splitPath, string baseline, string newRoot, CancellationToken token)
    {
        SituationArtifactIO.EnsureNewOutput(newRoot);
        Directory.CreateDirectory(newRoot);
        var baselineCheckpoint = SituationTrainingResumeValidator.Deserialize<SituationTrainingCheckpointV1>(
            await File.ReadAllTextAsync(Path.Combine(baseline, ".partial", "checkpoint.json"), token), "baseline checkpoint");
        if (baselineCheckpoint.NextOrdinal != 5 || baselineCheckpoint.Binding.Matches.Any(item => item.Split != SituationTrainingSplit.Train))
            throw new InvalidDataException("Real recovery requires a committed five-train-match benchmark.");
        foreach (var boundary in new[] { "record", "after-commit" })
        {
            var output = Path.Combine(newRoot, boundary);
            using var interrupted = CancellationTokenSource.CreateLinkedTokenSource(token);
            try
            {
                await SituationTrainingDatasetExporter.ExportAsync(demoRoot, splitPath, output, interrupted.Token,
                    new(4, false, 5, (name, ordinal) =>
                    {
                        if (name == boundary && ordinal == 1) interrupted.Cancel();
                    }));
                throw new InvalidOperationException("Real recovery interruption was not exercised.");
            }
            catch (OperationCanceledException) when (interrupted.IsCancellationRequested) { }
            var before = SituationTrainingResumeValidator.Deserialize<SituationTrainingCheckpointV1>(
                await File.ReadAllTextAsync(Path.Combine(output, ".partial", "checkpoint.json"), token), "interrupted checkpoint");
            if (before.NextOrdinal != (boundary == "record" ? 1 : 2))
                throw new InvalidOperationException("Real interruption checkpoint has wrong commit boundary.");
            await SituationTrainingDatasetExporter.ExportAsync(demoRoot, splitPath, output, token, new(4, true, 5));
            var recovered = SituationTrainingResumeValidator.Deserialize<SituationTrainingCheckpointV1>(
                await File.ReadAllTextAsync(Path.Combine(output, ".partial", "checkpoint.json"), token), "recovered checkpoint");
            if (recovered.NextOrdinal != 5) throw new InvalidDataException("Real recovery is incomplete.");
            foreach (var match in baselineCheckpoint.CompletedMatches)
            {
                var actual = recovered.CompletedMatches[match.Ordinal];
                if (actual.Spool != match.Spool || actual.Statistics != match.Statistics)
                    throw new InvalidDataException("Real recovery differs from uninterrupted data or statistics.");
            }
            Console.WriteLine($"Real five-train-match {boundary} recovery: all spool bytes/rows/SHA and statistics identical.");
        }
        // Existing pilot stays read-only; compare shared selected samples directly.
        var repository = SituationArtifactIO.FindRepositoryRoot(splitPath);
        var pilotPath = Path.Combine(repository, "datasets", "situation-stage4-pilot-20260919-r10", "train.jsonl");
        var pilot = File.ReadLines(pilotPath).ToDictionary(line =>
            SituationTrainingContractJson.DeserializeRecord(line).SampleId, StringComparer.Ordinal);
        var overlap = 0;
        foreach (var match in baselineCheckpoint.CompletedMatches)
        foreach (var line in File.ReadLines(Path.Combine(baseline, match.Spool.Path)))
        {
            var record = SituationTrainingContractJson.DeserializeRecord(line);
            if (pilot.TryGetValue(record.SampleId, out var old))
            {
                if (old != line) throw new InvalidDataException("Real pilot record bytes changed after payload reuse.");
                overlap++;
            }
        }
        if (overlap == 0) throw new InvalidOperationException("No real pilot overlap was checked.");
        Console.WriteLine($"Real pilot equivalence: {overlap} shared records byte-identical.");
        foreach (var fault in new[] { "checkpoint", "spool", "source", "config", "split", "complete" })
        {
            var original = Path.Combine(newRoot, "after-commit");
            var output = Path.Combine(newRoot, "reject-" + fault);
            Directory.CreateDirectory(output);
            foreach (var path in Directory.EnumerateFiles(original, "*", SearchOption.AllDirectories))
            {
                var target = Path.Combine(output, Path.GetRelativePath(original, path));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(path, target, overwrite: false);
            }
            var checkpointPath = Path.Combine(output, ".partial", "checkpoint.json");
            var checkpoint = SituationTrainingResumeValidator.Deserialize<SituationTrainingCheckpointV1>(
                await File.ReadAllTextAsync(checkpointPath, token), "real fault checkpoint");
            if (fault == "checkpoint") await File.WriteAllTextAsync(checkpointPath, "{truncated", token);
            else if (fault == "spool")
            {
                await using var stream = new FileStream(Path.Combine(output, checkpoint.CompletedMatches[0].Spool.Path),
                    FileMode.Open, FileAccess.Write);
                stream.SetLength(stream.Length - 1);
            }
            else if (fault == "complete")
            {
                var manifest = SituationTrainingContractJson.DeserializeManifest(
                    await File.ReadAllTextAsync(Path.Combine(output, "manifest.json"), token));
                await SituationArtifactIO.WriteJsonAsync(Path.Combine(output, "manifest.json"),
                    manifest with { Status = SituationArtifactStatus.Complete, Trainable = true }, token);
            }
            else
            {
                var binding = checkpoint.Binding;
                binding = fault switch
                {
                    "source" => binding with { SourceFiles = binding.SourceFiles.Select((item, ordinal) =>
                        ordinal == 0 ? item with { Sha256 = new string('a', 64) } : item).ToArray() },
                    "config" => binding with { PromptConfigSha256 = new string('a', 64) },
                    _ => binding with { SplitSha256 = new string('a', 64) }
                };
                await File.WriteAllTextAsync(checkpointPath, SituationTrainingResumeValidator.Serialize(
                    checkpoint with { Binding = binding, BindingSha256 = SituationTrainingResumeValidator.ComputeBindingSha256(binding) }), token);
            }
            var refused = false;
            try { await SituationTrainingDatasetExporter.ExportAsync(demoRoot, splitPath, output, token, new(4, true, 5)); }
            catch (Exception error) when (error is IOException or InvalidDataException) { refused = true; }
            if (!refused) throw new InvalidOperationException("Real recovery did not reject " + fault);
            if (fault == "complete")
            {
                // This is an intentionally forged marker, never a validated
                // complete dataset. Leave the generated diagnostic incomplete.
                File.Copy(Path.Combine(original, "manifest.json"), Path.Combine(output, "manifest.json"), overwrite: true);
            }
            Console.WriteLine($"Real five-train-match recovery rejected {fault}; diagnostic copy retained.");
        }
    }

    internal static async Task VerifyAsync(CancellationToken token)
    {
        var repository = SituationArtifactIO.FindRepositoryRoot(Environment.CurrentDirectory);
        var splitPath = Path.Combine(repository, "situation-implementation", "situation-stage4-split-v1.json");
        var root = Path.Combine(Path.GetTempPath(), "situation-full-integration-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var parsed = new Dictionary<string, int>(StringComparer.Ordinal);
        var checks = 0;
        var originalOutput = Console.Out;
        Console.SetOut(TextWriter.Null);
        try
        {
            var baseline = Path.Combine(root, "baseline");
            var complete = await Run(baseline, new());
            Check(complete.Status == SituationArtifactStatus.Complete &&
                complete.Counts["train"].Matches == 71 && complete.Counts["dev"].Matches == 8 &&
                complete.Counts["test"].Matches == 8, "complete frozen split");
            Check(parsed.Count == 87 && parsed.Values.All(value => value == 1), "one parse per match");
            await SituationTrainingDatasetValidator.VerifyAsync(repository, baseline, token);
            await Throws(() => Run(baseline, new()), "existing output rejected");
            await Throws(() => Run(baseline, new(Resume: true)), "complete resume rejected");

            foreach (var boundary in new[] { "record", "after-commit", "merged", "published", "cleaned", "validated" })
            {
                var output = Path.Combine(root, boundary);
                parsed.Clear();
                var interrupted = false;
                await Throws(() => Run(output, new(Boundary: (name, ordinal) =>
                {
                    if (!interrupted && name == boundary && (ordinal == 1 || ordinal == 87))
                    {
                        interrupted = true;
                        throw new OperationCanceledException("Injected integration interruption.");
                    }
                })), boundary + " cancellation");
                var incomplete = SituationTrainingContractJson.DeserializeManifest(
                    await File.ReadAllTextAsync(Path.Combine(output, "manifest.json"), token));
                Check(incomplete.Status == SituationArtifactStatus.Incomplete, boundary + " stays incomplete");
                await Run(output, new(Resume: true));
                Check(parsed.Values.All(value => value <= (boundary == "record" ? 2 : 1)) &&
                      parsed.Values.Count(value => value == 2) == (boundary == "record" ? 1 : 0),
                    boundary + " replay only uncommitted match");
                foreach (var file in Directory.GetFiles(baseline))
                    Check(await SituationArtifactIO.FileSha256Async(file, token) ==
                          await SituationArtifactIO.FileSha256Async(Path.Combine(output, Path.GetFileName(file)), token),
                        boundary + " deterministic " + Path.GetFileName(file));
            }
            foreach (var corruption in new[] { "checkpoint", "spool", "split-binding", "config-binding", "source-binding", "extra" })
            {
                var output = Path.Combine(root, corruption);
                await Throws(() => Run(output, new(Boundary: (name, ordinal) =>
                {
                    if (name == "after-commit" && ordinal == 0) throw new OperationCanceledException();
                })), "prepare " + corruption);
                var checkpointPath = Path.Combine(output, ".partial", "checkpoint.json");
                if (corruption == "checkpoint") await File.WriteAllTextAsync(checkpointPath, "{broken", token);
                else if (corruption == "spool")
                {
                    var spool = Directory.GetFiles(Path.Combine(output, ".partial", "spool"), "records.jsonl", SearchOption.AllDirectories).Single();
                    await using var stream = new FileStream(spool, FileMode.Open, FileAccess.Write);
                    stream.SetLength(stream.Length - 1);
                }
                else if (corruption == "extra") await File.WriteAllTextAsync(Path.Combine(output, "unexpected"), "x", token);
                else
                {
                    var checkpoint = SituationTrainingResumeValidator.Deserialize<SituationTrainingCheckpointV1>(
                        await File.ReadAllTextAsync(checkpointPath, token), "test checkpoint");
                    var binding = checkpoint.Binding;
                    binding = corruption switch
                    {
                        "split-binding" => binding with { SplitSha256 = new string('a', 64) },
                        "config-binding" => binding with { PromptConfigSha256 = new string('a', 64) },
                        _ => binding with { SourceFiles = binding.SourceFiles.Select((item, index) =>
                            index == 0 ? item with { Sha256 = new string('a', 64) } : item).ToArray() }
                    };
                    await File.WriteAllTextAsync(checkpointPath, SituationTrainingResumeValidator.Serialize(
                        checkpoint with { Binding = binding, BindingSha256 = SituationTrainingResumeValidator.ComputeBindingSha256(binding) }), token);
                }
                await Throws(() => Run(output, new(Resume: true)), "reject " + corruption);
            }
            var atomicFailure = Path.Combine(root, "manifest-replacement-failure");
            FileStream? publicationLock = null;
            try
            {
                await Throws(() => Run(atomicFailure, new(Boundary: (name, _) =>
                {
                    if (name == "cleaned")
                    {
                        Throws(() => Run(atomicFailure, new(Resume: true)),
                            "concurrent resume during publication rejected").GetAwaiter().GetResult();
                        Check(!Directory.Exists(Path.Combine(atomicFailure, ".partial")),
                            "concurrent resume cannot move publishing workspace");
                    }
                    if (name == "validated")
                    {
                        if (!OperatingSystem.IsWindows()) throw new IOException("Injected atomic replacement failure.");
                        publicationLock = new FileStream(Path.Combine(atomicFailure, "manifest.json"),
                            FileMode.Open, FileAccess.Read, FileShare.Read);
                    }
                })), "atomic manifest replacement failure");
            }
            finally { publicationLock?.Dispose(); }
            Check(SituationTrainingContractJson.DeserializeManifest(await File.ReadAllTextAsync(
                Path.Combine(atomicFailure, "manifest.json"), token)).Status == SituationArtifactStatus.Incomplete,
                "atomic replacement failure never publishes complete");
            await Run(atomicFailure, new(Resume: true));
            var blockedParent = Path.Combine(root, "non-writable-parent");
            await File.WriteAllTextAsync(blockedParent, "parent is a regular file", token);
            await Throws(() => Run(Path.Combine(blockedParent, "child"), new()), "non-writable output rejected");
            var writeFailure = Path.Combine(root, "write-failure");
            await Throws(() => Run(writeFailure, new(Boundary: (name, ordinal) =>
            {
                if (name == "record" && ordinal == 1) throw new IOException("Injected disk write failure.");
            })), "write failure");
            Check(SituationTrainingContractJson.DeserializeManifest(await File.ReadAllTextAsync(
                Path.Combine(writeFailure, "manifest.json"), token)).Status == SituationArtifactStatus.Incomplete,
                "write failure never publishes complete");
            // Payload building must remain byte-identical to the former pilot path.
            var timeline = V4DataVerifier.CreateTimeline(9000);
            var member = (await SituationDatasetSplit.LoadApprovedAsync(splitPath, token)).Split.Train[0];
            using var service = new SituationSceneService();
            var selector = new SituationTrainingCandidateSelector(service);
            var attempt = timeline.Semantics!.Attempts.Single();
            var indexed = selector.SelectRound(SituationTrainingTimelineIndex.Create(timeline), member.MatchId, attempt.RoundId);
            foreach (var payload in indexed.Payloads)
            {
                var oldRecord = SituationTrainingRecordBuilder.Build(timeline, member, attempt,
                    indexed.SelectionResult.Selection.Samples.Single(item => item.Tick == payload.Tick),
                    SituationTrainingSplit.Train, SituationDatasetSplit.FrozenStageFourSplitSha256,
                    new SituationEligibleSceneBuilder(service), SituationDeterministicAnalyzer.CreateFrozen(), token);
                var reused = SituationTrainingRecordBuilder.Build(member, payload, SituationTrainingSplit.Train,
                    SituationDatasetSplit.FrozenStageFourSplitSha256);
                Check(SituationTrainingContractJson.SerializeLine(oldRecord) == SituationTrainingContractJson.SerializeLine(reused),
                    "legacy and reused record bytes");
            }
        }
        finally
        {
            Console.SetOut(originalOutput);
            // This unique, test-created temporary root never contains historical artifacts.
            Directory.Delete(root, recursive: true);
        }
        Console.WriteLine($"Stage-six exporter integration/recovery checks passed: {checks}");

        Task<SituationTrainingDatasetManifestV1> Run(string output, SituationTrainingExportOptions options) =>
            SituationTrainingDatasetExporter.ExportAsync("unused-synthetic-demo", splitPath, output, token, options,
                async (member, split, sha, cancellation) =>
                {
                    parsed[member.MatchId] = parsed.GetValueOrDefault(member.MatchId) + 1;
                    return await SituationTrainingDatasetExporter.BuildMatchAsync(
                        SituationTrainingTimelineIndex.Create(V4DataVerifier.CreateTimeline(9000)), member, split, sha,
                        1, 0, 0, cancellation);
                });
        void Check(bool value, string description)
        {
            if (!value) throw new InvalidOperationException("Integration check failed: " + description);
            checks++;
        }
        async Task Throws(Func<Task> action, string description)
        {
            try { await action(); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or OperationCanceledException or InvalidOperationException)
            { checks++; return; }
            throw new InvalidOperationException("Expected integration failure: " + description);
        }
    }
}

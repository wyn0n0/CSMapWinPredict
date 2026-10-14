using System.Text;
using System.Text.Json;
using CsDemoMap.Api.Models;

namespace CsDemoMap.Api.Services;

/// <summary>Purpose-scoped v8 source generation. Historical sample IDs are lineage keys only.</summary>
internal static class SituationStage6CorpusSource
{
    internal static Dictionary<string, int> AssignShards(IEnumerable<(string MatchId, long Bytes)> inputs, int count)
    {
        if (count is < 1 or > 3) throw new InvalidDataException("stage6-shard-count");
        var totals = new long[count];
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (id, bytes) in inputs.OrderByDescending(x => x.Bytes).ThenBy(x => x.MatchId, StringComparer.Ordinal))
        {
            if (bytes < 0) throw new InvalidDataException("stage6-shard-size");
            var shard = Enumerable.Range(0, count).OrderBy(i => totals[i]).ThenBy(i => i).First();
            if (!result.TryAdd(id, shard)) throw new InvalidDataException("stage6-shard-duplicate");
            totals[shard] = checked(totals[shard] + bytes);
        }
        return result;
    }

    internal static void ValidatePurpose(string split, string purpose)
    {
        var allowed = split switch
        {
            "train" => new[] { "sft" }, "dev" => ["checkpoint-selection", "prompt-selection", "evaluation"],
            "test" => ["final-evaluation"], _ => []
        };
        if (!allowed.Contains(purpose, StringComparer.Ordinal)) throw new InvalidDataException("stage6-purpose-forbidden");
    }

    internal static SituationTrainingRecordV1 BuildRecord(SituationStageFourSplitMember member,
        SituationTrainingSelectionPayload payload, SituationTrainingSplit split, string splitHash)
    {
        if (payload.Facts.AnalysisRuleVersion != SituationAnalysisRuleLoader.CloseExposureVersion ||
            payload.SceneSha256 != SituationCanonicalJson.Sha256(payload.Scene) ||
            payload.FactsSha256 != SituationCanonicalJson.Sha256(payload.Facts) ||
            payload.NarrativeSha256 != SituationCanonicalJson.Sha256(payload.Narrative) ||
            payload.Tick != payload.Scene.Tick || payload.RoundId != payload.Scene.Round.Ref ||
            payload.SceneCanonicalJson != SituationCanonicalJson.Serialize(payload.Scene) ||
            payload.FactsCanonicalJson != SituationCanonicalJson.Serialize(payload.Facts) ||
            payload.NarrativeCanonicalJson != SituationCanonicalJson.Serialize(payload.Narrative))
            throw new InvalidDataException("stage6-source-payload-mismatch");
        SituationContractValidator.Validate(payload.Facts, payload.Scene, SituationAnalysisRuleLoader.CloseExposureVersion);
        SituationContractValidator.ValidateTemplate(payload.Narrative, payload.Facts);
        var scene = SituationModelInputProjector.Project(payload.Scene);
        var identity = SituationTrainingContractJson.CreateIdentity(member.MatchId, payload.RoundId,
            payload.Tick, SituationTrainingRecordBuilder.CreateIdentityVersions(splitHash));
        return new(SituationTrainingContractVersions.TrainingRecord, identity.SampleId,
            new(split, identity.MatchRef, identity.RoundRef, payload.Tick, payload.Phase,
                payload.SelectionTags, payload.SampleWeight, payload.WeightNumerator, payload.WeightDenominator,
                payload.SceneSha256, SituationCanonicalJson.Sha256(scene), payload.FactsSha256, payload.NarrativeSha256),
            new(scene, payload.Facts, payload.Facts.Evidence.Select(e => e.Id).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray()),
            payload.Narrative, SituationTrainingLabelSource.TemplatePrelabel, SituationTrainingReviewStatus.Unreviewed);
    }

    internal static async Task<object> ExportAsync(string demoDirectory, string splitPath, string baseDataset,
        string outputDirectory, string splitName, string purpose, CancellationToken ct, int shardIndex = 0, int shardCount = 1)
    {
        ValidatePurpose(splitName, purpose);
        if (shardCount is < 1 or > 3 || shardIndex < 0 || shardIndex >= shardCount)
            throw new InvalidDataException("stage6-shard-index");
        SituationArtifactIO.EnsureNewOutput(outputDirectory);
        var approved = await SituationDatasetSplit.LoadApprovedAsync(splitPath, ct);
        var repository = SituationArtifactIO.FindRepositoryRoot(splitPath);
        var split = Enum.Parse<SituationTrainingSplit>(splitName, true);
        var members = splitName switch { "train" => approved.Split.Train, "dev" => approved.Split.Dev, _ => approved.Split.Test };
        SituationTrainingPilotExporter.ValidateDemoMembership(demoDirectory, approved.Split);
        var assignments = AssignShards(members.Select(m => (m.MatchId, new FileInfo(Path.Combine(demoDirectory, m.FileName)).Length)), shardCount);
        members = members.Where(m => assignments[m.MatchId] == shardIndex).ToArray();
        if (members.Count == 0) throw new InvalidDataException("stage6-shard-empty");
        var baseManifestPath = Path.Combine(baseDataset, "manifest.json");
        using var baseManifest = JsonDocument.Parse(await File.ReadAllTextAsync(baseManifestPath, ct));
        if (baseManifest.RootElement.GetProperty("status").GetString() != "complete") throw new InvalidDataException("stage6-base-incomplete");
        var baseHash = await SituationArtifactIO.FileSha256Async(baseManifestPath, ct);
        var provenancePath = Path.Combine(baseDataset, "provenance.json");
        var provenanceHash = await SituationArtifactIO.FileSha256Async(provenancePath, ct);
        using var provenance = JsonDocument.Parse(await File.ReadAllTextAsync(provenancePath, ct));
        var baseRounds = provenance.RootElement.GetProperty("roundMappings").EnumerateArray()
            .Select(r => (r.GetProperty("matchRef").GetString(), r.GetProperty("roundRef").GetString())).ToHashSet();
        var mesh = Path.Combine(AppContext.BaseDirectory, "Geometry", "de_mirage.mesh");
        if (!File.Exists(mesh)) throw new InvalidDataException("stage6-pinned-mesh-required");
        var meshHash = await SituationArtifactIO.FileSha256Async(mesh, ct);
        var analyzer = SituationDeterministicAnalyzer.CreateCloseExposure(mesh);
        var selection = SituationTrainingSelectionLoader.LoadFrozen();
        var sourceFiles = new List<SourceBinding>();
        foreach (var relative in SituationTrainingDatasetExporter.SourcePaths(repository).Concat(
            Directory.EnumerateFiles(Path.Combine(repository, "schemas", "situation"), "*.json")
                .Select(p => Path.GetRelativePath(repository, p).Replace('\\', '/'))).Order(StringComparer.Ordinal))
            sourceFiles.Add(new("repo:" + relative, await SituationArtifactIO.FileSha256Async(Path.Combine(repository, relative), ct)));
        Directory.CreateDirectory(outputDirectory);
        var manifestPath = Path.Combine(outputDirectory, "manifest.json");
        await SituationArtifactIO.WriteJsonAsync(manifestPath, new { status = "incomplete", complete = false, frozen = false }, ct);
        var recordPath = Path.Combine(outputDirectory, splitName + ".jsonl");
        var count = 0;
        var rounds = new HashSet<string>(StringComparer.Ordinal);
        var matches = new List<string>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var started = System.Diagnostics.Stopwatch.StartNew();
        await using (var writer = new StreamWriter(new FileStream(recordPath, FileMode.CreateNew, FileAccess.Write, FileShare.None), new UTF8Encoding(false)))
        {
            foreach (var member in members.OrderBy(m => m.MatchId, StringComparer.Ordinal))
            {
                ct.ThrowIfCancellationRequested();
                var path = Path.Combine(demoDirectory, member.FileName);
                SituationTrainingPilotExporter.ValidateDirectFile(demoDirectory, path, member.FileName);
                await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (Convert.ToHexStringLower(await System.Security.Cryptography.SHA256.HashDataAsync(stream, ct)) != member.MatchId)
                    throw new InvalidDataException("stage6-demo-hash-mismatch");
                stream.Position = 0;
                var timeline = await new DemoParserService().ParseAsync(stream, member.FileName, ct, collectSemantics: true);
                var index = SituationTrainingTimelineIndex.Create(timeline, ct);
                using var scenes = new SituationSceneService();
                var selector = new SituationTrainingCandidateSelector(selection, new SituationEligibleSceneBuilder(scenes), analyzer);
                foreach (var attempt in index.CompletedAttempts)
                    foreach (var payload in selector.SelectRound(index, member.MatchId, attempt.RoundId, ct).Payloads)
                    {
                        var record = BuildRecord(member, payload, split, approved.SplitSha256);
                        if (!baseRounds.Contains((record.Metadata.MatchRef, record.Metadata.RoundRef)))
                            throw new InvalidDataException("stage6-base-round-lineage");
                        if (!ids.Add(record.SampleId)) throw new InvalidDataException("stage6-duplicate-source");
                        await writer.WriteLineAsync(SituationCanonicalJson.Serialize(record).AsMemory(), ct);
                        rounds.Add(record.Metadata.RoundRef);
                        count++;
                    }
                matches.Add(SituationTrainingContractJson.CreateIdentity(member.MatchId, "s0-a1", 0,
                    SituationTrainingRecordBuilder.CreateIdentityVersions(approved.SplitSha256)).MatchRef);
                Console.WriteLine($"Stage6 source {splitName} shard {shardIndex + 1}/{shardCount}: match {matches.Count}/{members.Count} complete; elapsed={started.Elapsed.TotalSeconds:F1}s.");
            }
        }
        var readCount = 0;
        var readIds = new HashSet<string>(StringComparer.Ordinal);
        var readMatches = matches.ToHashSet(StringComparer.Ordinal);
        using (var reader = new StreamReader(recordPath, new UTF8Encoding(false, true)))
            while (await reader.ReadLineAsync(ct) is { } line)
            {
                var record = SituationCanonicalJson.Deserialize<SituationTrainingRecordV1>(line);
                var model = record.Input.Scene;
                var scene = new MinimapSceneV1(model.SchemaVersion, model.SceneBuilderVersion, model.GeometryVersion,
                    "structured", model.Map, "stage6-readback", 0, model.Tick, model.Tick, model.TickRate,
                    model.Round, model.Players, model.Teams, model.Bomb, model.Utilities, model.Effects, model.Geometry, model.DataQuality);
                SituationContractValidator.Validate(record.Input.Facts, scene, SituationAnalysisRuleLoader.CloseExposureVersion);
                SituationContractValidator.ValidateTemplate(record.Output, record.Input.Facts);
                if (!readIds.Add(record.SampleId) || !readMatches.Contains(record.Metadata.MatchRef) ||
                    !record.Input.AllowedEvidenceIds.SequenceEqual(record.Input.Facts.Evidence.Select(e => e.Id).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)) ||
                    line != SituationCanonicalJson.Serialize(record) ||
                    record.Metadata.Split != split || record.Metadata.ModelInputSha256 != SituationCanonicalJson.Sha256(model) ||
                    record.Metadata.FactsSha256 != SituationCanonicalJson.Sha256(record.Input.Facts) ||
                    record.Metadata.PrelabelSha256 != SituationCanonicalJson.Sha256(record.Output)) throw new InvalidDataException("stage6-readback-mismatch");
                readCount++;
            }
        if (count != readCount || count == 0) throw new InvalidDataException("stage6-source-count-mismatch");
        foreach (var binding in sourceFiles)
            if (await SituationArtifactIO.FileSha256Async(Path.Combine(repository, binding.LogicalPath[5..]), ct) != binding.Sha256)
                throw new InvalidDataException("stage6-source-drift");
        if (await SituationArtifactIO.FileSha256Async(mesh, ct) != meshHash ||
            await SituationArtifactIO.FileSha256Async(baseManifestPath, ct) != baseHash ||
            await SituationArtifactIO.FileSha256Async(provenancePath, ct) != provenanceHash ||
            await SituationArtifactIO.FileSha256Async(splitPath, ct) != approved.SplitSha256)
            throw new InvalidDataException("stage6-input-drift");
        var manifest = new { schemaVersion = "situation-stage6-source-v1", status = "complete", complete = true, frozen = true,
            sharding = new { index = shardIndex, count = shardCount, algorithm = "demo-bytes-desc-greedy-v1", matchIds = members.Select(m => m.MatchId).Order(StringComparer.Ordinal).ToArray() },
            split = splitName, purpose, analysisRuleVersion = SituationAnalysisRuleLoader.CloseExposureVersion,
            splitSha256 = approved.SplitSha256, analysisRulesSha256 = analyzer.RuleLoad.Sha256,
            selectionSha256 = selection.Sha256, baseManifestSha256 = baseHash, baseProvenanceSha256 = provenanceHash, meshSha256 = meshHash,
            identityPolicy = "historical-v1-sample-id-lineage; v8 Facts/Narrative; complete v8 selector rerun",
            selectionPolicy = "all eligible Demo scenes; existing frozen selector with v8 analyzer; reviewed-only missing ticks appended by paired preparation",
            matches, counts = new { samples = count, rounds = rounds.Count, matches = matches.Count }, sourceFiles,
            files = new[] { new { path = splitName + ".jsonl", sha256 = await SituationArtifactIO.FileSha256Async(recordPath, ct), rows = count } } };
        await SituationArtifactIO.WriteJsonAsync(manifestPath, manifest, ct);
        return manifest;
    }

    private sealed record SourceBinding(string LogicalPath, string Sha256);
}

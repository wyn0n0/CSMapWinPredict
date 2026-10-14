using CsDemoMap.Api.Services;
using CsDemoMap.Api.Models;
namespace CsDemoMap.Api.Tests;
internal static class SituationStage6CorpusSourceTests
{
    internal static async Task VerifyAsync(CancellationToken ct)
    {
        var count = 0;
        foreach (var p in new[] { ("train", "sft"), ("dev", "evaluation"), ("dev", "prompt-selection"), ("dev", "checkpoint-selection"), ("test", "final-evaluation") })
        { SituationStage6CorpusSource.ValidatePurpose(p.Item1, p.Item2); count++; }
        foreach (var p in new[] { ("test", "sft"), ("train", "final-evaluation"), ("dev", ""), ("unknown", "sft") })
        {
            try { SituationStage6CorpusSource.ValidatePurpose(p.Item1, p.Item2); }
            catch (InvalidDataException) { count++; continue; }
            throw new InvalidOperationException("Stage6 purpose gate allowed forbidden use.");
        }
        try { await SituationStage6CorpusSource.ExportAsync("missing", "missing", "missing", "missing", "test", "sft", ct); }
        catch (InvalidDataException e) when (e.Message == "stage6-purpose-forbidden") { count++; }
        if (count != 10) throw new InvalidOperationException("Stage6 source gate check failed.");
        var sizes = new[] { ("a", 8L), ("b", 7L), ("c", 6L), ("d", 5L), ("e", 4L), ("f", 3L) };
        var shards = SituationStage6CorpusSource.AssignShards(sizes, 3);
        if (!new[] { 0, 1, 2, 2, 1, 0 }.SequenceEqual(sizes.Select(x => shards[x.Item1])) ||
            !shards.OrderBy(x => x.Key).SequenceEqual(SituationStage6CorpusSource.AssignShards(sizes.Reverse(), 3).OrderBy(x => x.Key)))
            throw new InvalidOperationException("Stage6 shards must be balanced, exhaustive and deterministic.");
        count++;
        foreach (var invalid in new[] { (0, 0), (-1, 3), (3, 3), (0, 4) })
        {
            try { await SituationStage6CorpusSource.ExportAsync("missing", "missing", "missing", "missing", "train", "sft", ct, invalid.Item1, invalid.Item2); }
            catch (InvalidDataException e) when (e.Message == "stage6-shard-index") { count++; continue; }
            throw new InvalidOperationException("Stage6 invalid shard must fail before IO.");
        }
        var scene = SituationRuleVerifier.BuildScene([
            new("T1", SituationSide.T, .4, .4, "same", null, null, null, null, 100),
            new("CT1", SituationSide.CT, .6, .6, "same", null, null, null, null, 100)]);
        var analysis = new SituationDeterministicAnalyzer(SituationAnalysisRuleLoader.LoadCloseExposure()).Analyze(scene);
        var roundId = scene.Round.Ref ?? throw new InvalidOperationException("Synthetic scene requires a round ID.");
        var payload = new SituationTrainingSelectionPayload(roundId, scene.Tick, "live", SituationTrainingPhase.Live,
            ["synthetic"], 1, 1, 1, scene, SituationCanonicalJson.Serialize(scene), SituationCanonicalJson.Sha256(scene),
            analysis.Facts.Facts, analysis.Facts.CanonicalJson, analysis.Facts.Sha256,
            analysis.Narrative.Narrative, analysis.Narrative.CanonicalJson, analysis.Narrative.Sha256);
        var member = new SituationStageFourSplitMember("fixture.dem", new string('a', 64), "train");
        var splitHash = new string('b', 64);
        var result = SituationStage6CorpusSource.BuildRecord(member, payload, SituationTrainingSplit.Train, splitHash);
        var identity = SituationTrainingContractJson.CreateIdentity(member.MatchId, roundId, scene.Tick,
            SituationTrainingRecordBuilder.CreateIdentityVersions(splitHash));
        if (result.SampleId != identity.SampleId || result.Input.Facts.AnalysisRuleVersion != SituationAnalysisRuleLoader.CloseExposureVersion ||
            SituationCanonicalJson.Sha256(result.Input.Scene) != SituationCanonicalJson.Sha256(SituationModelInputProjector.Project(scene)))
            throw new InvalidOperationException("Stage6 v8 payload/lineage/full scene mismatch.");
        count++;
        foreach (var bad in new[] { payload with { SceneSha256 = new string('0',64) },
            payload with { FactsSha256 = new string('0',64) }, payload with { NarrativeSha256 = new string('0',64) },
            payload with { Facts = payload.Facts with { AnalysisRuleVersion = SituationAnalysisRuleLoader.FrozenAnalysisRuleVersion } },
            payload with { Tick = payload.Tick + 1 }, payload with { SceneCanonicalJson = "{}" } })
        {
            try { SituationStage6CorpusSource.BuildRecord(member, bad, SituationTrainingSplit.Train, splitHash); }
            catch (InvalidDataException) { count++; continue; }
            throw new InvalidOperationException("Stage6 source accepted mutated payload.");
        }
        Console.WriteLine($"Stage6 source checks passed: {count}");
    }
}

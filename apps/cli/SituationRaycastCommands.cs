using System.Diagnostics;
using System.Text.Json;
using CsDemoMap.Api.Models;
using CsDemoMap.Api.Services;

namespace CsDemoMap.Cli;

internal static class SituationRaycastCommands
{
    internal static async Task AnalyzeAsync(string scenePath, string output, CancellationToken token)
    {
        SituationArtifactIO.EnsureNewOutput(output);
        var scene = SituationCanonicalJson.Deserialize<MinimapSceneV1>(await File.ReadAllTextAsync(scenePath,token));
        var result = SituationDeterministicAnalyzer.CreateRaycast().Analyze(scene);
        await using var stream = new FileStream(output,FileMode.CreateNew,FileAccess.Write,FileShare.None);
        await JsonSerializer.SerializeAsync(stream,result,new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented=true },token);
        Console.WriteLine($"Raycast contact risk: {result.Facts.Facts.ContactRisk}; rules: {result.Facts.Facts.AnalysisRuleVersion}");
    }

    // Replays existing scene inputs from exactly 20 train matches, never dev/test or a full export.
    internal static async Task SampleAsync(string dataset, string output, CancellationToken token, bool localPeek = false, bool positionPrediction = false, bool continuousSlope = false, bool extendedLocalPeek = false, bool verifiedExposure = false, bool closeExposure = false)
    {
        SituationArtifactIO.EnsureNewOutput(output);
        var meshPath = Path.Combine(AppContext.BaseDirectory,"Geometry","de_mirage.mesh");
        if (!File.Exists(meshPath)) throw new InvalidDataException("Prepare the pinned Mirage mesh first.");
        using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(dataset,"manifest.json"),token));
        if (manifest.RootElement.GetProperty("status").GetString() != "complete")
            throw new InvalidDataException("Sample source is incomplete.");
        foreach (var name in new[] { "train.jsonl", "split.json", "provenance.json" })
        {
            var file = manifest.RootElement.GetProperty("files").EnumerateArray().Single(f=>f.GetProperty("path").GetString()==name);
            if (await HashFile(Path.Combine(dataset,name),token) != file.GetProperty("sha256").GetString())
                throw new InvalidDataException("Sample source hash mismatch: " + name);
        }
        using var split = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(dataset,"split.json"),token));
        using var provenance = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(dataset,"provenance.json"),token));
        var maps = provenance.RootElement.GetProperty("demoMappings").EnumerateArray()
            .ToDictionary(m=>m.GetProperty("matchId").GetString()!,m=>m.GetProperty("matchRef").GetString()!);
        var selected = split.RootElement.GetProperty("train").EnumerateArray()
            .OrderBy(m=>SituationArtifactIO.Sha256("contact-raycast-sample-v1\n"+m.GetProperty("matchId").GetString()),StringComparer.Ordinal)
            .Take(20).Select(m=>new SampleMatch(m.GetProperty("fileName").GetString()!,m.GetProperty("matchId").GetString()!,
                maps[m.GetProperty("matchId").GetString()!])).ToArray();
        if (selected.Length != 20) throw new InvalidDataException("20 training matches are required.");
        var byRef = selected.ToDictionary(m=>m.MatchRef,StringComparer.Ordinal);
        var rounds = provenance.RootElement.GetProperty("roundMappings").EnumerateArray()
            .ToDictionary(r=>(r.GetProperty("matchRef").GetString()!,r.GetProperty("roundRef").GetString()!),
                r=>r.GetProperty("semanticRoundId").GetString()!);
        var frozen = SituationDeterministicAnalyzer.CreateFrozen();
        var baseline = closeExposure ? SituationDeterministicAnalyzer.CreateVerifiedExposure(meshPath) : verifiedExposure ? SituationDeterministicAnalyzer.CreateExtendedLocalPeek(meshPath) : extendedLocalPeek ? SituationDeterministicAnalyzer.CreateContinuousSlope(meshPath) : continuousSlope ? SituationDeterministicAnalyzer.CreatePositionPrediction(meshPath) : positionPrediction ? SituationDeterministicAnalyzer.CreateLocalPeek(meshPath) : localPeek ? SituationDeterministicAnalyzer.CreateRaycast(meshPath) : frozen;
        var current = closeExposure ? SituationDeterministicAnalyzer.CreateCloseExposure(meshPath) : verifiedExposure ? SituationDeterministicAnalyzer.CreateVerifiedExposure(meshPath) : extendedLocalPeek ? SituationDeterministicAnalyzer.CreateExtendedLocalPeek(meshPath) : continuousSlope ? SituationDeterministicAnalyzer.CreateContinuousSlope(meshPath) : positionPrediction ? SituationDeterministicAnalyzer.CreatePositionPrediction(meshPath) : localPeek ? SituationDeterministicAnalyzer.CreateLocalPeek(meshPath) : SituationDeterministicAnalyzer.CreateRaycast(meshPath);
        Directory.CreateDirectory(output);
        await File.WriteAllTextAsync(Path.Combine(output,"status.json"),"{\"status\":\"incomplete\"}",token);
        var transitions = new SortedDictionary<string,int>(StringComparer.Ordinal);
        var durations = new List<double>();
        var baselineDurations = new List<double>();
        var peekFound = 0; var peekUnknown = 0; var probeCount = 0;
        var predictionFound = 0; var predictionProbeCount = 0;
        var verifiedExposureCount = 0;
        var count = 0; var changed = 0; var removedPairs = 0; var repeated = 0;
        await using (var writer = new StreamWriter(new FileStream(Path.Combine(output,"comparison.jsonl"),FileMode.CreateNew)))
        {
            foreach (var line in File.ReadLines(Path.Combine(dataset,"train.jsonl")))
            {
                token.ThrowIfCancellationRequested();
                using var header = JsonDocument.Parse(line);
                if (!byRef.TryGetValue(header.RootElement.GetProperty("metadata").GetProperty("matchRef").GetString()!,out var match)) continue;
                var record = SituationTrainingContractJson.DeserializeRecord(line);
                if (record.Metadata.Split != SituationTrainingSplit.Train) throw new InvalidDataException("Non-training input.");
                var scene = SituationTrainingRecordBuilder.RestoreSourceScene(record,match.MatchId,
                    rounds[(record.Metadata.MatchRef,record.Metadata.RoundRef)]);
                if (SituationCanonicalJson.Sha256(scene) != record.Metadata.SourceSceneSha256)
                    throw new InvalidDataException("Restored scene hash mismatch.");
                var before = frozen.Analyze(scene);
                if (before.Facts.Sha256 != record.Metadata.FactsSha256 || before.Narrative.Sha256 != record.Metadata.PrelabelSha256)
                    throw new InvalidDataException("Frozen baseline replay changed.");
                if (localPeek || positionPrediction || continuousSlope || extendedLocalPeek || verifiedExposure || closeExposure)
                {
                    var baselineClock = Stopwatch.StartNew();
                    before = baseline.Analyze(scene);
                    baselineDurations.Add(baselineClock.Elapsed.TotalMilliseconds);
                }
                var clock = Stopwatch.StartNew();
                var after = current.Analyze(scene);
                durations.Add(clock.Elapsed.TotalMilliseconds);
                if (verifiedExposure || closeExposure)
                {
                    var expectedRisk = before.Facts.Facts.ContactRisk;
                    if (closeExposure && before.Facts.Diagnostics.Decisions.TryGetValue("contact-local-peek-distance-units", out var exposureUnits) &&
                        double.Parse(exposureUnits, System.Globalization.CultureInfo.InvariantCulture) <= 150)
                        expectedRisk = SituationContactRisk.High;
                    if (after.Facts.Facts.ContactRisk != expectedRisk)
                        throw new InvalidDataException("Unexpected exposure risk transition.");
                    var restored = after.Facts.Facts with { AnalysisRuleVersion = before.Facts.Facts.AnalysisRuleVersion, Evidence = before.Facts.Facts.Evidence, ContactRisk = before.Facts.Facts.ContactRisk };
                    if (SituationCanonicalJson.Sha256(restored) != before.Facts.Sha256)
                        throw new InvalidDataException("Exposure annotation changed factual labels.");
                    foreach (var key in new[] { "contact-local-peek-probes", "contact-position-prediction-probes", "contact-occluded-pairs" })
                        if (before.Facts.Diagnostics.Decisions.GetValueOrDefault(key) != after.Facts.Diagnostics.Decisions.GetValueOrDefault(key))
                            throw new InvalidDataException("Exposure annotation changed probe counts.");
                    var hasDistance = after.Facts.Diagnostics.Decisions.ContainsKey("contact-local-peek-distance-units");
                    if (hasDistance != after.Facts.Diagnostics.Decisions.ContainsKey("contact-local-peek"))
                        throw new InvalidDataException("Exposure distance is missing or attached to a non-local result.");
                    if (hasDistance) verifiedExposureCount++;
                }
                if (match.Scenes == 0)
                {
                    var again = current.Analyze(scene);
                    if (again.Facts.Sha256 != after.Facts.Sha256 || again.Narrative.Sha256 != after.Narrative.Sha256)
                        throw new InvalidDataException("Raycast repeat is not deterministic.");
                    repeated++;
                }
                var transition = $"{before.Facts.Facts.ContactRisk}->{after.Facts.Facts.ContactRisk}";
                transitions[transition] = transitions.GetValueOrDefault(transition)+1;
                if (before.Facts.Facts.ContactRisk != after.Facts.Facts.ContactRisk) changed++;
                if (after.Facts.Diagnostics.Decisions.TryGetValue("contact-occluded-pairs",out var value))
                    removedPairs += int.Parse(value,System.Globalization.CultureInfo.InvariantCulture);
                if (after.Facts.Diagnostics.Decisions.ContainsKey("contact-local-peek")) peekFound++;
                if ((localPeek || positionPrediction || continuousSlope || extendedLocalPeek || verifiedExposure || closeExposure) && after.Facts.Facts.ContactRisk == SituationContactRisk.Unknown) peekUnknown++;
                if (after.Facts.Diagnostics.Decisions.ContainsKey("contact-position-prediction")) predictionFound++;
                if (after.Facts.Diagnostics.Decisions.TryGetValue("contact-position-prediction-probes",out var predictionProbes))
                    predictionProbeCount += int.Parse(predictionProbes,System.Globalization.CultureInfo.InvariantCulture);
                if (after.Facts.Diagnostics.Decisions.TryGetValue("contact-local-peek-probes",out var probes))
                    probeCount += int.Parse(probes,System.Globalization.CultureInfo.InvariantCulture);
                match.Scenes++; count++;
                await writer.WriteLineAsync(SituationCanonicalJson.Serialize(new {
                    record.SampleId,record.Metadata.MatchRef,record.Metadata.RoundRef,record.Metadata.Tick,
                    sceneSha256=record.Metadata.SourceSceneSha256,before=before.Facts.Facts.ContactRisk,
                    after=after.Facts.Facts.ContactRisk,after.Facts.Facts.AnalysisRuleVersion,
                    factsSha256=after.Facts.Sha256,narrativeSha256=after.Narrative.Sha256,
                    evidence=after.Facts.Facts.Evidence.Single(e=>e.Id=="contact-risk"),
                    after.Facts.Diagnostics.Decisions }).AsMemory(),token);
                if (count%500==0) Console.WriteLine($"Raycast sample: {count} scenes; {changed} contact labels changed.");
            }
        }
        if (selected.Any(m=>m.Scenes==0)) throw new InvalidDataException("A selected match had no samples.");
        durations.Sort();
        baselineDurations.Sort();
        var report = new {
            status="complete",purpose="20-train-match scene replay; not human accuracy or historical-map compatibility acceptance",
            sourceManifestSha256=await HashFile(Path.Combine(dataset,"manifest.json"),token),
            baselineRulesSha256=baseline.RuleLoad.Sha256,rulesSha256=current.RuleLoad.Sha256,
            asset=current.RuleLoad.Rules.Visibility,selectedMatches=selected,
            scenes=count,changedContactLabels=changed,occludedPairObservations=removedPairs,
            deterministicRepeats=repeated,transitions,
            p50Milliseconds=durations[(int)Math.Ceiling(durations.Count*0.50)-1],
            p95Milliseconds=durations[(int)Math.Ceiling(durations.Count*0.95)-1],
            baselineP95Milliseconds=baselineDurations.Count == 0 ? (double?)null : baselineDurations[(int)Math.Ceiling(baselineDurations.Count*0.95)-1],
            peekFound,peekUnknown,probeCount,predictionFound,predictionProbeCount,
            verifiedExposureCount,
            comparisonSha256=await HashFile(Path.Combine(output,"comparison.jsonl"),token),
            limitations=new[] { "Static world only; ignores navigation, smoke and penetration. Prediction assumes constant horizontal velocity up to one second; optional continuous slope support follows sampled ground, not stairs or jumps. No future observations are read.",
                "Fixed body-height samples approximate pose; historical demo map version is not certified.",
                "Inputs were selected by frozen v1; this is a regression sample, not an unbiased accuracy estimate." }
        };
        await File.WriteAllTextAsync(Path.Combine(output,"report.json"),JsonSerializer.Serialize(report,
            new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented=true }),token);
        await File.WriteAllTextAsync(Path.Combine(output,"status.json"),"{\"status\":\"complete\"}",token);
        Console.WriteLine($"Raycast sample complete: {selected.Length} matches, {count} scenes, {changed} changed labels; {output}");
    }

    private static async Task<string> HashFile(string path,CancellationToken token)
    {
        await using var stream=File.OpenRead(path);
        return Convert.ToHexStringLower(await System.Security.Cryptography.SHA256.HashDataAsync(stream,token));
    }

    private sealed record SampleMatch(string FileName,string MatchId,string MatchRef)
    {
        public int Scenes { get; set; }
    }
}

using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using CsDemoMap.Api.Models;

namespace CsDemoMap.Api.Services;

public static class WinDatasetV4Exporter
{
    public const string SemanticVersion = "mirage-semantics-v4.2";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static async Task ExportAsync(string input, string output, CancellationToken token)
    {
        var directory = Path.GetFullPath(output);
        if (Directory.Exists(directory) || File.Exists(directory))
            throw new IOException("v4 output must be a new directory: " + directory);
        var paths = WinDatasetExporter.ResolveDemoPaths(input);
        Directory.CreateDirectory(directory);
        var reports = new List<object>();
        // CreateNew and a completion manifest make interrupted exports identifiable.
        await using var stream = new FileStream(Path.Combine(directory, "samples.jsonl"), FileMode.CreateNew);
        await using var writer = new StreamWriter(stream);
        foreach (var path in paths)
        {
            token.ThrowIfCancellationRequested();
            await using var source = File.OpenRead(path);
            var hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(source, token));
            source.Position = 0;
            var timeline = await new DemoParserService().ParseAsync(source, Path.GetFileName(path), token, true);
            if (timeline.Metadata.MapName != "de_mirage")
            {
                reports.Add(new { file = Path.GetFileName(path), matchId = hash, skipped = "unsupported-map", timeline.Metadata.MapName });
                Console.WriteLine($"Skipped unsupported map: {Path.GetFileName(path)}");
                continue;
            }
            var report = await WriteTimelineAsync(timeline, hash, writer, token);
            var semantic = timeline.Semantics!;
            await File.WriteAllTextAsync(Path.Combine(directory, hash + ".audit.json"),
                JsonSerializer.Serialize(new { timeline.Metadata, semantic.Attempts, semantic.Audit, report }, JsonOptions), token);
            reports.Add(new { file = Path.GetFileName(path), matchId = hash, report });
            await writer.FlushAsync(token);
            Console.WriteLine(JsonSerializer.Serialize(new { file = Path.GetFileName(path), report }, JsonOptions));
        }
        await writer.FlushAsync(token);
        await File.WriteAllTextAsync(Path.Combine(directory, "manifest.json"),
            JsonSerializer.Serialize(new
            {
                schemaVersion = 4, semanticVersion = SemanticVersion,
                parserVersion = "DemoFile.Game.Cs/0.44.1", sampleStrideTicks = 64,
                status = "complete", createdAtUtc = DateTimeOffset.UtcNow, matches = reports
            }, JsonOptions), token);
    }

    public static async Task<object> WriteTimelineAsync(DemoTimeline timeline, string matchId,
        TextWriter writer, CancellationToken token)
    {
        var semantic = timeline.Semantics ?? throw new InvalidOperationException("Semantic collection is required.");
        var frames = timeline.Frames.ToDictionary(f => f.Tick);
        var rejected = new Dictionary<string, int>();
        var rejectionDetails = new List<object>();
        var byPhaseOutcome = new Dictionary<string, int>();
        var rowCount = 0;
        var exportedRounds = 0;
        var numberChanges = new Dictionary<string, int>();
        foreach (var attempt in semantic.Attempts.Where(a => a.Disposition == "completed"))
        {
            var candidates = semantic.Frames.Where(f => f.RoundId == attempt.RoundId &&
                f.Tick >= attempt.LiveTick && f.Tick < attempt.EndTick &&
                f.Phase is "live" or "post-plant").OrderBy(f => f.Tick).ToArray();
            // Build history within this attempt only: no bomb/equipment carry from a restart.
            var correctedFrames = candidates.Select(s =>
            {
                var old = frames[s.Tick];
                return old with
                {
                    Players = old.Players.Where(p => p.Alive && p.Team is "T" or "CT")
                        .Select(p => p with
                        {
                            // Legacy bridge requires finite coordinates. Spatial outputs
                            // are nulled below whenever alive positions are incomplete.
                            X = float.IsFinite(p.X) ? p.X : 0,
                            Y = float.IsFinite(p.Y) ? p.Y : 0,
                            Z = float.IsFinite(p.Z) ? p.Z : 0
                        }).ToArray(),
                    Round = old.Round with
                    {
                        Number = s.RoundNumber ?? 0, Phase = s.Phase,
                        ElapsedSeconds = s.Clock.LiveElapsedSeconds ?? 0,
                        RemainingSeconds = s.Clock.RoundRemainingSeconds ?? 0
                    }
                };
            }).ToArray();
            var corrected = timeline with
            {
                Frames = correctedFrames,
                PlayerEquipmentStates = timeline.PlayerEquipmentStates
                    .Where(e => e.Tick >= attempt.StartTick && e.Tick < attempt.EndTick).ToArray()
            };
            var builder = new AsOfTickFeatureBuilder(corrected);
            var correctedByTick = correctedFrames.ToDictionary(f => f.Tick);
            var selected = new List<SemanticFrame>();
            var previous = int.MinValue;
            foreach (var sample in candidates)
            {
                if (previous != int.MinValue && sample.Tick - previous < timeline.Metadata.TickRate) continue;
                previous = sample.Tick;
                var reason = sample.RoundNumber is null ? "round-unconfirmed"
                    : sample.Roster.Quality == "unusable" ? string.Join('+', sample.Roster.Reasons)
                    : !sample.Clock.ClockKnown ? sample.Clock.ClockSource
                    : correctedByTick[sample.Tick].Players.Count != sample.Roster.AliveKnown ? "alive-snapshot-mismatch"
                    : null;
                var key = $"{sample.Phase}/{attempt.Winner}/{reason ?? "retained"}";
                byPhaseOutcome[key] = byPhaseOutcome.GetValueOrDefault(key) + 1;
                if (reason is not null)
                {
                    rejected[reason] = rejected.GetValueOrDefault(reason) + 1;
                    rejectionDetails.Add(new { sample.Tick, sample.RoundId, sample.Phase, reason });
                    continue;
                }
                selected.Add(sample);
            }
            if (selected.Count == 0) continue;
            exportedRounds++;
            foreach (var sample in selected)
            {
                token.ThrowIfCancellationRequested();
                var features = JsonSerializer.SerializeToNode(builder.Build(correctedByTick[sample.Tick]), JsonOptions)!.AsObject();
                // The nullable v4 clock replaces the non-nullable legacy bridge values.
                features.Remove("elapsedSeconds");
                features.Remove("remainingSeconds");
                features["clock"] = JsonSerializer.SerializeToNode(sample.Clock, JsonOptions);
                features["quality"] = JsonSerializer.SerializeToNode(sample.Roster, JsonOptions);
                features.Remove("players");
                features.Remove("zones");
                MaskMissing(features, sample.Roster);
                var oldNumber = frames[sample.Tick].Round.Number;
                if (oldNumber != sample.RoundNumber)
                {
                    var key = $"{oldNumber}->{sample.RoundNumber}";
                    numberChanges[key] = numberChanges.GetValueOrDefault(key) + 1;
                }
                var row = new
                {
                    schemaVersion = 4, semanticVersion = SemanticVersion,
                    matchId, roundId = sample.RoundId, segmentId = sample.SegmentId,
                    roundNumber = sample.RoundNumber, mapName = timeline.Metadata.MapName,
                    tick = sample.Tick, demoTimeSeconds = sample.Tick / (double)timeline.Metadata.TickRate,
                    features, labelTWin = attempt.Winner == "T" ? 1 : 0,
                    sampleWeight = 1d / selected.Count
                };
                await writer.WriteLineAsync(JsonSerializer.Serialize(row, JsonOptions).AsMemory(), token);
                rowCount++;
            }
        }
        return new
        {
            rowCount, exportedRounds,
            dispositions = semantic.Attempts.GroupBy(a => a.Disposition).ToDictionary(g => g.Key, g => g.Count()),
            rejected, rejectionDetails, byPhaseOutcome, numberChanges,
            pauseFlagObservations = semantic.Audit.Count(a => a.Rules.GamePaused || a.Rules.TechnicalTimeout || a.Rules.WaitingForResume),
            hardPauseObservations = semantic.Audit.Count(a => a.Rules.GamePaused || a.Rules.TechnicalTimeout),
            firstLiveElapsed = semantic.Frames.Where(f => f.Phase is "live" or "post-plant")
                .GroupBy(f => f.RoundId ?? "unknown").ToDictionary(g => g.Key, g => g.First().Clock.LiveElapsedSeconds)
        };
    }

    private static void MaskMissing(JsonObject features, RosterQuality q)
    {
        var baseline = features["baseline"]!.AsObject();
        if (q.AliveEquipmentKnown < q.AliveKnown)
        {
            // Conservative team-wide nulls: partial observed sums are not true totals.
            foreach (var team in new[] { "t", "ct" })
                foreach (var field in new[] { "totalMoney", "totalArmor", "helmetCount", "defuserCount",
                    "equipmentValue", "grenadeCount", "rifleCount", "sniperCount", "equipmentKnownPlayers" })
                    features[team]![field] = null;
            foreach (var field in new[] { "equipmentValueDifference", "moneyDifference", "armorDifference",
                "helmetCountDifference", "defuserCountDifference", "grenadeCountDifference", "rifleCountDifference",
                "sniperCountDifference", "tEquipmentCoverage", "ctEquipmentCoverage", "equipmentCoverageDifference" })
                baseline[field] = null;
        }
        if (q.AlivePositionKnown < q.AliveKnown)
        {
            foreach (var field in baseline.Select(p => p.Key).Where(k =>
                k.Contains("Distance", StringComparison.Ordinal) || k.Contains("Dispersion", StringComparison.Ordinal) ||
                k.Contains("Proximity", StringComparison.Ordinal) || k.EndsWith("ClosestSiteDistance", StringComparison.Ordinal)).ToArray())
                baseline[field] = field.EndsWith("Missing", StringComparison.Ordinal) ? JsonValue.Create(true) : null;
            baseline["tPositionDataMissing"] = true;
            baseline["ctPositionDataMissing"] = true;
        }
    }
}

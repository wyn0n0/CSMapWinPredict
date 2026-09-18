using System.Text.Json;
using System.Text.Json.Nodes;
using CsDemoMap.Api.Models;

namespace CsDemoMap.Api.Services;

public sealed class WinFeatureSampleBuilder
{
    public const string SemanticVersion = "mirage-semantics-v4.2";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public WinFeatureBuildResult Build(DemoTimeline timeline, CancellationToken token = default)
    {
        var semantic = timeline.Semantics ??
            throw new InvalidOperationException("Semantic collection is required.");
        var frames = timeline.Frames.ToDictionary(frame => frame.Tick);
        var samples = new List<WinInferenceSample>();
        var targets = new Dictionary<string, WinTrainingTarget>(StringComparer.Ordinal);
        var rejected = new Dictionary<string, int>(StringComparer.Ordinal);
        var rejectionDetails = new List<WinFeatureRejection>();
        var byPhaseOutcome = new Dictionary<string, int>(StringComparer.Ordinal);
        var numberChanges = new Dictionary<string, int>(StringComparer.Ordinal);
        var exportedRounds = 0;

        foreach (var attempt in semantic.Attempts.Where(item => item.Disposition == "completed"))
        {
            token.ThrowIfCancellationRequested();
            var candidates = semantic.Frames.Where(frame =>
                    RoundSampleEligibility.EvaluateRoundTick(
                        timeline.Metadata.MapName, attempt, frame).Eligible)
                .OrderBy(frame => frame.Tick)
                .ToArray();
            // Build history within this attempt only: no bomb/equipment carry from a restart.
            var correctedFrames = candidates.Select(sample =>
            {
                var old = frames[sample.Tick];
                return old with
                {
                    Players = old.Players.Where(player => player.Alive && player.Team is "T" or "CT")
                        .Select(player => player with
                        {
                            // The legacy feature bridge requires finite coordinates. Spatial
                            // outputs are nulled whenever alive positions are incomplete.
                            X = float.IsFinite(player.X) ? player.X : 0,
                            Y = float.IsFinite(player.Y) ? player.Y : 0,
                            Z = float.IsFinite(player.Z) ? player.Z : 0
                        }).ToArray(),
                    Round = old.Round with
                    {
                        Number = sample.RoundNumber ?? 0,
                        Phase = sample.Phase,
                        ElapsedSeconds = sample.Clock.LiveElapsedSeconds ?? 0,
                        RemainingSeconds = sample.Clock.RoundRemainingSeconds ?? 0
                    }
                };
            }).ToArray();
            var corrected = timeline with
            {
                Frames = correctedFrames,
                PlayerEquipmentStates = timeline.PlayerEquipmentStates
                    .Where(state => state.Tick >= attempt.StartTick && state.Tick < attempt.EndTick)
                    .ToArray()
            };
            var featureBuilder = new AsOfTickFeatureBuilder(corrected);
            var correctedByTick = correctedFrames.ToDictionary(frame => frame.Tick);
            var selected = new List<SemanticFrame>();
            var previous = int.MinValue;

            foreach (var sample in candidates)
            {
                if (previous != int.MinValue && sample.Tick - previous < timeline.Metadata.TickRate)
                    continue;
                previous = sample.Tick;
                var eligibility = RoundSampleEligibility.Evaluate(
                    timeline.Metadata.MapName, attempt, sample, correctedByTick[sample.Tick]);
                var reason = eligibility.ReasonCode;
                var key = $"{sample.Phase}/{attempt.Winner}/{reason ?? "retained"}";
                byPhaseOutcome[key] = byPhaseOutcome.GetValueOrDefault(key) + 1;
                if (reason is not null)
                {
                    rejected[reason] = rejected.GetValueOrDefault(reason) + 1;
                    rejectionDetails.Add(new(sample.Tick, sample.RoundId, sample.Phase, reason));
                    continue;
                }
                selected.Add(sample);
            }

            if (selected.Count == 0)
                continue;

            exportedRounds++;
            targets.Add(attempt.RoundId, new(attempt.Winner == "T" ? 1 : 0, selected.Count));
            foreach (var sample in selected)
            {
                token.ThrowIfCancellationRequested();
                var features = BuildFeatureNode(featureBuilder, correctedByTick[sample.Tick], sample);
                var oldNumber = frames[sample.Tick].Round.Number;
                if (oldNumber != sample.RoundNumber)
                {
                    var key = $"{oldNumber}->{sample.RoundNumber}";
                    numberChanges[key] = numberChanges.GetValueOrDefault(key) + 1;
                }
                samples.Add(new(
                    sample.Tick,
                    sample.Tick / (double)timeline.Metadata.TickRate,
                    attempt.RoundId,
                    sample.SegmentId,
                    sample.RoundNumber!.Value,
                    timeline.Metadata.MapName,
                    sample.Phase,
                    features));
            }
        }

        var report = new WinFeatureBuildReport(
            samples.Count,
            exportedRounds,
            semantic.Attempts.GroupBy(attempt => attempt.Disposition)
                .ToDictionary(group => group.Key, group => group.Count()),
            rejected,
            rejectionDetails,
            byPhaseOutcome,
            numberChanges,
            semantic.Audit.Count(item => item.Rules.GamePaused || item.Rules.TechnicalTimeout ||
                item.Rules.WaitingForResume),
            semantic.Audit.Count(item => item.Rules.GamePaused || item.Rules.TechnicalTimeout),
            semantic.Frames.Where(frame => frame.Phase is "live" or "post-plant")
                .GroupBy(frame => frame.RoundId ?? "unknown")
                .ToDictionary(group => group.Key, group => group.First().Clock.LiveElapsedSeconds));
        return new(samples, targets, report);
    }

    private static JsonObject BuildFeatureNode(
        AsOfTickFeatureBuilder builder,
        DemoFrame frame,
        SemanticFrame sample)
    {
        var features = JsonSerializer.SerializeToNode(builder.Build(frame), JsonOptions)!.AsObject();
        // Nullable v4 clock fields replace the non-nullable legacy bridge values.
        features.Remove("elapsedSeconds");
        features.Remove("remainingSeconds");
        features["clock"] = JsonSerializer.SerializeToNode(sample.Clock, JsonOptions);
        features["quality"] = JsonSerializer.SerializeToNode(sample.Roster, JsonOptions);
        features.Remove("players");
        features.Remove("zones");
        MaskMissing(features, sample.Roster);
        return features;
    }

    private static void MaskMissing(JsonObject features, RosterQuality quality)
    {
        var baseline = features["baseline"]!.AsObject();
        if (quality.AliveEquipmentKnown < quality.AliveKnown)
        {
            foreach (var team in new[] { "t", "ct" })
                foreach (var field in new[] { "totalMoney", "totalArmor", "helmetCount", "defuserCount",
                    "equipmentValue", "grenadeCount", "rifleCount", "sniperCount", "equipmentKnownPlayers" })
                    features[team]![field] = null;
            foreach (var field in new[] { "equipmentValueDifference", "moneyDifference", "armorDifference",
                "helmetCountDifference", "defuserCountDifference", "grenadeCountDifference", "rifleCountDifference",
                "sniperCountDifference", "tEquipmentCoverage", "ctEquipmentCoverage", "equipmentCoverageDifference" })
                baseline[field] = null;
        }
        if (quality.AlivePositionKnown < quality.AliveKnown)
        {
            foreach (var field in baseline.Select(property => property.Key).Where(key =>
                key.Contains("Distance", StringComparison.Ordinal) ||
                key.Contains("Dispersion", StringComparison.Ordinal) ||
                key.Contains("Proximity", StringComparison.Ordinal) ||
                key.EndsWith("ClosestSiteDistance", StringComparison.Ordinal)).ToArray())
                baseline[field] = field.EndsWith("Missing", StringComparison.Ordinal)
                    ? JsonValue.Create(true)
                    : null;
            baseline["tPositionDataMissing"] = true;
            baseline["ctPositionDataMissing"] = true;
        }
    }
}

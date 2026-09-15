using CsDemoMap.Api.Models;

namespace CsDemoMap.Api.Tests;

internal static class SituationContractVerifier
{
    public static void Verify(string? sampleDirectory = null)
    {
        var checks = 0;
        var adapter = new SituationInputAdapter();
        var builder = new SituationSceneBuilder();
        var scene = builder.Build(adapter.BuildFromTimeline(BuildTimeline(false), "fixture-demo", 0, 650));
        SituationContractValidator.Validate(scene);
        Check(scene.Tick == 648, "as-of target frame", ref checks);
        Check(scene.Players.Count == 2, "alive player projection", ref checks);
        Check(scene.Players.Select(player => player.Slot).Order().SequenceEqual(["CT1", "T1"]),
            "anonymous slots", ref checks);
        Check(scene.Players.All(player => player.Floor == SituationFloor.Unknown),
            "unknown floor is explicit", ref checks);
        Check(scene.Utilities.Count == 1 && scene.Utilities[0].Trajectory.Points.All(point => point.Tick <= scene.Tick),
            "future utility points removed", ref checks);
        Check(scene.Effects.Count == 1 && scene.Effects[0].SampleTick == 648,
            "future effect samples removed", ref checks);
        Check(scene.Bomb.CarrierSlot == "T1", "bomb identity anonymized", ref checks);

        var canonical = SituationCanonicalJson.Serialize(scene);
        Check(!canonical.Contains("Secret", StringComparison.Ordinal), "player names excluded", ref checks);
        Check(!canonical.Contains("steam-t", StringComparison.Ordinal), "source IDs excluded", ref checks);
        Check(!canonical.Contains("Winner", StringComparison.OrdinalIgnoreCase), "winner excluded", ref checks);
        Check(!canonical.Contains("EndTick", StringComparison.OrdinalIgnoreCase), "future end tick excluded", ref checks);
        Check(SituationCanonicalJson.Sha256(scene) == SituationCanonicalJson.Sha256(scene),
            "stable scene hash", ref checks);

        var reordered = builder.Build(adapter.BuildFromTimeline(BuildTimeline(true), "fixture-demo", 0, 650));
        Check(canonical == SituationCanonicalJson.Serialize(reordered),
            "input player order does not change canonical scene", ref checks);

        var facts = new SituationFactsV1(
            SituationContractVersions.Facts,
            "fixture-no-analysis-rules",
            new(1, 1),
            new(100, 100),
            new(SituationBombState.Carried, null),
            new(SituationFormation.Unknown, SituationFormation.Unknown),
            new(null, null),
            null,
            SituationContactRisk.Unknown,
            SituationIsolatedSide.Unknown,
            SituationSpatialAdvantage.Uncertain,
            SituationConfidence.Low,
            [
                new("alive.t", ["/teams/t/alive"], "fixture-copy", "T 方存活 1 人。"),
                new("alive.ct", ["/teams/ct/alive"], "fixture-copy", "CT 方存活 1 人。"),
                new("total-health.t", ["/teams/t/totalHealth"], "fixture-copy", "T 方总生命值为 100。"),
                new("total-health.ct", ["/teams/ct/totalHealth"], "fixture-copy", "CT 方总生命值为 100。"),
                new("bomb.state", ["/bomb/state"], "fixture-copy", "C4 当前由 T 方携带。"),
                new("confidence", ["/dataQuality"], "fixture-confidence", "结构夹具置信度标记为低。")
            ],
            []);
        var narrative = new SituationNarrativeV1(
            SituationContractVersions.Narrative,
            [
                new("T 方存活 1 人。", ["alive.t"]),
                new("C4 当前由 T 方携带。", ["bomb.state"])
            ],
            "当前仅有低置信度的结构化事实。",
            []);
        var analysis = new SituationAnalysisV1(
            SituationContractVersions.Analysis,
            scene.RequestedTick,
            scene.Tick,
            DateTimeOffset.UnixEpoch,
            new(
                scene.SchemaVersion,
                scene.SceneBuilderVersion,
                scene.GeometryVersion,
                facts.SchemaVersion,
                facts.AnalysisRuleVersion,
                narrative.SchemaVersion),
            null,
            SituationNarrativeSource.Template,
            facts,
            narrative);
        SituationContractValidator.Validate(analysis, scene);
        checks++;

        var missingSourceFacts = facts with
        {
            Evidence = facts.Evidence.Select(item => item.Id == "alive.t"
                ? item with { SourcePaths = ["/future/winner"] }
                : item).ToArray()
        };
        CheckThrows(
            () => SituationContractValidator.Validate(missingSourceFacts, scene),
            "missing evidence source path rejected",
            ref checks);
        var missingBindingFacts = facts with
        {
            Evidence = facts.Evidence.Where(item => item.Id != "confidence").ToArray()
        };
        CheckThrows(
            () => SituationContractValidator.Validate(missingBindingFacts),
            "missing facts evidence binding rejected",
            ref checks);
        var unstableEvidenceFacts = facts with
        {
            Evidence = facts.Evidence.Select(item => item.Id == "alive.t"
                ? item with { Id = "550e8400-e29b-41d4-a716-446655440000" }
                : item).ToArray()
        };
        CheckThrows(
            () => SituationContractValidator.Validate(unstableEvidenceFacts),
            "unstable evidence ID rejected",
            ref checks);

        var invalidNarrative = narrative with
        {
            Highlights = [
                new("未知证据。", ["missing"]),
                new("仍为未知证据。", ["missing"])
            ]
        };
        CheckThrows(
            () => SituationContractValidator.Validate(invalidNarrative, new HashSet<string>(["alive.t"])),
            "unknown narrative evidence rejected",
            ref checks);
        var predictiveNarrative = narrative with { SummaryZh = "T 方将会获胜，胜率为 80%。" };
        CheckThrows(
            () => SituationContractValidator.Validate(
                predictiveNarrative, facts.Evidence.Select(item => item.Id).ToHashSet(StringComparer.Ordinal)),
            "predictive narrative rejected",
            ref checks);

        var invalidAnalysisVersions = analysis with
        {
            Versions = analysis.Versions with { Rules = "unrelated-rules" }
        };
        CheckThrows(
            () => SituationContractValidator.Validate(invalidAnalysisVersions, scene),
            "analysis rules version mismatch rejected",
            ref checks);

        var invalidScene = scene with { Tick = scene.RequestedTick + 1 };
        CheckThrows(
            () => SituationContractValidator.Validate(invalidScene),
            "future scene tick rejected",
            ref checks);
        var firstPlayer = scene.Players[0];
        var invalidPositionScene = scene with
        {
            Players = [firstPlayer with { Position = new(double.NaN, 0, 0) }, .. scene.Players.Skip(1)]
        };
        CheckThrows(
            () => SituationContractValidator.Validate(invalidPositionScene),
            "non-finite position rejected",
            ref checks);
        var invalidRoundScene = scene with { Round = scene.Round with { Ref = "winner-T" } };
        CheckThrows(
            () => SituationContractValidator.Validate(invalidRoundScene),
            "winner-bearing round reference rejected",
            ref checks);
        var zeroHealthScene = scene with
        {
            Players = [firstPlayer with { Health = 0 }, .. scene.Players.Skip(1)]
        };
        CheckThrows(
            () => SituationContractValidator.Validate(zeroHealthScene),
            "alive zero-health conflict without quality rejected",
            ref checks);
        var invalidTeamScene = scene with
        {
            Teams = scene.Teams with { T = scene.Teams.T with { Alive = null } }
        };
        CheckThrows(
            () => SituationContractValidator.Validate(invalidTeamScene),
            "partial totals with incomplete roster rejected",
            ref checks);
        var invalidBombSiteScene = scene with { Bomb = scene.Bomb with { Site = SituationSite.A } };
        CheckThrows(
            () => SituationContractValidator.Validate(invalidBombSiteScene),
            "future pre-plant bomb site rejected",
            ref checks);
        var invalidQualityScene = scene with
        {
            DataQuality = [.. scene.DataQuality, new("uncontrolled", ["/players"], SituationQualitySeverity.Warning)]
        };
        CheckThrows(
            () => SituationContractValidator.Validate(invalidQualityScene),
            "uncontrolled quality code rejected",
            ref checks);
        var firstEffect = scene.Effects[0];
        var invalidAreaScene = scene with
        {
            Effects = [firstEffect with { Area = [] }, .. scene.Effects.Skip(1)]
        };
        CheckThrows(
            () => SituationContractValidator.Validate(invalidAreaScene),
            "empty effect area rejected",
            ref checks);

        if (sampleDirectory is not null)
            checks += SituationSampleDirectoryValidator.Validate(sampleDirectory);

        checks += SituationFlowVerifier.Verify();

        Console.WriteLine($"Situation contract checks passed: {checks}");
    }

    private static DemoTimeline BuildTimeline(bool reversePlayers)
    {
        var t = new PlayerSnapshot(
            "steam-t", "Secret T", "T", true, 100,
            100, 200, 0, 90, 10, 20, 0, "A Site", "weapon_ak47", 0, 0);
        var ct = new PlayerSnapshot(
            "steam-ct", "Secret CT", "CT", true, 100,
            -100, -200, 0, -90, -10, -20, 0, "CT Spawn", "weapon_m4a1", 0, 0);
        IReadOnlyList<PlayerSnapshot> players = reversePlayers ? [ct, t] : [t, ct];
        var round = new RoundSnapshot(1, "live", 0, 0, 10, 105, 0, 0);
        var bomb = new BombSnapshot(
            "carried", "steam-t", null, null, "A Site", 100, 200, 0, null, null);
        var frames = new[]
        {
            new DemoFrame(640, 10, players, round, bomb, []),
            new DemoFrame(648, 10.125, players, round, bomb, [])
        };
        var utilityTracks = new[]
        {
            new UtilityTrack(
                "private-projectile", "smoke", "steam-t", "Secret T", "T", 640, 700, null,
                [new(640, 10, 1, 2, 3), new(656, 10.25, 4, 5, 6)])
        };
        var effects = new[]
        {
            new UtilityEffectTrack(
                "private-effect", "smoke", "steam-t", "Secret T", "T", 640, 700,
                [
                    new(648, 10.125, 1, 2, 3, 144, []),
                    new(656, 10.25, 4, 5, 6, 144, [])
                ])
        };
        var carriedUtility = new[]
        {
            new PlayerUtilityState(640, 10, "steam-t", [new("flash", 1)]),
            new PlayerUtilityState(640, 10, "steam-ct", [])
        };
        var equipment = new[]
        {
            new PlayerEquipmentState(640, 10, "steam-t", 800, 100, true, false, 2700, 2700, 0,
                [new("weapon_ak47", "rifle", 1, 30, 90)]),
            new PlayerEquipmentState(640, 10, "steam-ct", 1000, 100, true, true, 3100, 3100, 0,
                [new("weapon_m4a1", "rifle", 1, 30, 90)])
        };
        var attempt = new RoundAttempt
        {
            RoundId = "s0-a1",
            SegmentId = 0,
            RoundNumber = 1,
            StartTick = 640,
            LiveTick = 640
        };
        var clock = new SemanticClock(10, 105, null, null, true, "fixture", "none");
        var roster = new RosterQuality(5, 5, 10, 2, 2, 2, true, "usable", []);
        var semantics = new SemanticTimeline(
            [attempt],
            [
                new(640, "s0-a1", 1, 0, "live", clock, roster),
                new(648, "s0-a1", 1, 0, "live", clock, roster)
            ],
            []);
        return new DemoTimeline(
            new("fixture.dem", "de_mirage", 64, 8, 700, 11),
            frames,
            utilityTracks,
            effects,
            carriedUtility,
            equipment,
            [],
            [])
        {
            Semantics = semantics
        };
    }

    private static void Check(bool condition, string label, ref int checks)
    {
        if (!condition)
            throw new InvalidOperationException($"Situation contract check failed: {label}.");
        checks++;
    }

    private static void CheckThrows(Action action, string label, ref int checks)
    {
        try
        {
            action();
        }
        catch (InvalidDataException)
        {
            checks++;
            return;
        }
        throw new InvalidOperationException($"Situation contract check failed: {label}.");
    }
}

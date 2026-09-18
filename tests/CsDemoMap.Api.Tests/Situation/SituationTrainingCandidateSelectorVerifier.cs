using System.Globalization;
using CsDemoMap.Api.Models;

namespace CsDemoMap.Api.Tests;

internal static class SituationTrainingCandidateSelectorVerifier
{
    internal static void Verify()
    {
        var checks = 0;
        using var sceneService = new SituationSceneService();
        var selector = new SituationTrainingCandidateSelector(sceneService);

        VerifyResultBoundary(ref checks);
        VerifyAllCategories(selector, ref checks);
        VerifyLimitAndFill(selector, ref checks);
        VerifyMerge(selector, ref checks);
        VerifyAnchorTolerance(selector, ref checks);
        VerifyRoundIsolation(selector, ref checks);
        VerifyDeterminism(selector, ref checks);
        VerifyTimelineIntegration(selector, sceneService, ref checks);

        Console.WriteLine($"Stage-four candidate selection checks passed: {checks}");
    }

    private static void VerifyResultBoundary(ref int checks)
    {
        var forbidden = new[] { "winner", "endReason", "labelTWin", "title", "detail" };
        foreach (var type in new[]
                 {
                     typeof(SituationTrainingRoundSelectionV1),
                     typeof(SituationTrainingSelectedTick),
                     typeof(SituationTrainingSelectionCategoryStats)
                 })
            Check(type.GetProperties().All(property => forbidden.All(term =>
                    !property.Name.Contains(term, StringComparison.OrdinalIgnoreCase))),
                $"{type.Name} excludes outcome, event text and labels", ref checks);
    }

    private static void VerifyAllCategories(
        SituationTrainingCandidateSelector selector,
        ref int checks)
    {
        var fixture = CreateRichFixture();
        var result = Select(selector, fixture);
        var config = SituationTrainingSelectionLoader.LoadFrozen().Config;
        var expectedCategories = config.CorePriority
            .Concat(config.EventPriority)
            .Concat(config.RarePriority)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var selectedTags = result.Selection.Samples
            .SelectMany(sample => sample.SelectionTags)
            .Where(tag => tag != config.FillStrategy)
            .Order(StringComparer.Ordinal)
            .ToArray();
        Check(result.Selection.Samples.Count == 16 &&
              selectedTags.SequenceEqual(expectedCategories, StringComparer.Ordinal),
            "all configured categories are selected once", ref checks);
        Check(result.Selection.Samples.Select(item => item.Tick).SequenceEqual(
                [0, 20, 30, 40, 50, 60, 90, 100, 110, 120, 130, 140, 150, 160, 170, 180]),
            "category priority and one-second event mapping", ref checks);
        Check(result.Selection.Samples.All(sample => sample.SelectionTags.SequenceEqual(
                sample.SelectionTags.Order(StringComparer.Ordinal), StringComparer.Ordinal)),
            "selection tags are ordinal sorted", ref checks);
        Check(result.Selection.Samples.All(sample =>
                  sample.WeightNumerator == 1 && sample.WeightDenominator == 16 &&
                  Math.Abs(sample.SampleWeight - 1d / 16) < 1e-12) &&
              Math.Abs(result.Selection.Samples.Sum(sample => sample.SampleWeight) - 1) < 1e-12,
            "selected round has exact 1/n metadata and unit total weight", ref checks);
        Check(result.Selection.Categories["deployment-complete"].CandidateCount > 0 &&
              result.Selection.Categories["deployment-complete"].SelectedCount == 1 &&
              !result.Selection.Shortages.ContainsKey("deployment-missing"),
            "deployment requires movement coverage and one-second stability", ref checks);
        Check(result.Selection.Categories["bomb-dropped"].SelectedCount == 1 &&
              result.Selection.Samples.Single(item => item.Tick == 50)
                  .SelectionTags.Contains("bomb-dropped", StringComparer.Ordinal),
            "bomb drop maps to first eligible frame after event", ref checks);
        Check(result.Selection.Categories[config.FillStrategy] ==
              new SituationTrainingSelectionCategoryStats(4, 0, 0, 4, 0),
            "cap-removal stats retain unselected fill candidates", ref checks);
        Check(result.Selection.SelectionConfigSha256 == SituationTrainingSelectionLoader.LoadFrozen().Sha256 &&
              result.Selection.SchemaVersion == SituationTrainingContractVersions.Selection &&
              result.Sha256 == SituationArtifactIO.Sha256(result.CanonicalJson),
            "selection config and canonical result hashes are bound", ref checks);
    }

    private static void VerifyLimitAndFill(
        SituationTrainingCandidateSelector selector,
        ref int checks)
    {
        foreach (var count in new[] { 0, 1, 15, 16, 20 })
        {
            var fixture = CreateNeutralFixture(count);
            var result = Select(selector, fixture);
            Check(result.Selection.Samples.Count == Math.Min(16, count),
                $"sample cap for {count} eligible ticks", ref checks);
            Check(result.Selection.Samples.Select(item => item.Tick).SequenceEqual(
                    result.Selection.Samples.Select(item => item.Tick).Order()),
                $"tick output order for {count} eligible ticks", ref checks);
            Check(count == 0 || result.Selection.Samples.All(sample =>
                      sample.WeightNumerator == 1 &&
                      sample.WeightDenominator == result.Selection.Samples.Count) &&
                  Math.Abs(result.Selection.Samples.Sum(sample => sample.SampleWeight) - 1) < 1e-12,
                $"round weight for {count} eligible ticks", ref checks);
            if (count == 0)
            {
                var config = SituationTrainingSelectionLoader.LoadFrozen().Config;
                var configuredCategories = config.CorePriority
                    .Concat(config.EventPriority)
                    .Concat(config.RarePriority);
                Check(configuredCategories.All(category =>
                        result.Selection.Categories[category].MissingAnchorCount > 0),
                    "every configured category reports a missing fixture", ref checks);
            }
        }

        var twenty = Select(selector, CreateNeutralFixture(20));
        var fill = SituationTrainingSelectionLoader.LoadFrozen().Config.FillStrategy;
        Check(twenty.Selection.Categories[fill].RemovedByLimitCount == 4 &&
              twenty.Selection.Samples.Any(item => item.Tick == 90),
            "farthest-point fill uses cap stats and earlier midpoint tie", ref checks);
        Check(twenty.Selection.Shortages["deployment-missing"] == 1,
            "missing deployment is reported without a fabricated anchor", ref checks);
        Check(twenty.Selection.Samples.Select(item => item.Tick).Distinct().Count() == 16,
            "fill never duplicates a physical tick", ref checks);
    }

    private static void VerifyMerge(
        SituationTrainingCandidateSelector selector,
        ref int checks)
    {
        var fixture = CreateMergeFixture();
        var result = Select(selector, fixture);
        var merged = result.Selection.Samples.Single(item => item.Tick == 20);
        var expected = new[]
        {
            "bomb-dropped", "first-casualty", "first-contact", "first-damage", "round-tail"
        };
        Check(expected.All(tag => merged.SelectionTags.Contains(tag, StringComparer.Ordinal)),
            "same-tick categories merge into one sample", ref checks);
        Check(result.Selection.Samples.Count == 3 &&
              result.Selection.Samples.Select(item => item.Tick).Distinct().Count() == 3,
            "merged tags occupy one sample slot", ref checks);
        Check(new[] { "bomb-dropped", "first-casualty", "first-contact", "first-damage" }
                  .All(tag => result.Selection.Categories[tag].MergedCount == 1) &&
              result.Selection.Categories["round-tail"].MergedCount == 0,
            "per-category merge stats are recorded", ref checks);
    }

    private static void VerifyAnchorTolerance(
        SituationTrainingCandidateSelector selector,
        ref int checks)
    {
        var fixture = CreateNeutralFixture(2, tickStep: 20);
        var dropFrame = CreateRoundFrame(
            fixture.Attempt,
            tick: 5,
            phase: "live",
            players: fixture.Observations[0].Snapshot.Players,
            bombState: "dropped",
            tickRate: fixture.TickRate);
        var later = fixture.Observations[1];
        var laterDropped = later with
        {
            Snapshot = later.Snapshot with { Bomb = Bomb("dropped") }
        };
        fixture = fixture with
        {
            Observations = [fixture.Observations[0], laterDropped],
            RoundFrames = [fixture.RoundFrames[0], dropFrame,
                new(laterDropped.Semantic, laterDropped.Snapshot)]
        };
        var result = Select(selector, fixture);
        Check(result.Selection.Categories["bomb-dropped"].CandidateCount == 0 &&
              result.Selection.Categories["bomb-dropped"].MissingAnchorCount == 1 &&
              result.Selection.Shortages["bomb-dropped-anchor-missing"] == 1,
            "event anchor beyond one second is dropped and counted", ref checks);
        Check(result.Selection.Samples.All(sample =>
                !sample.SelectionTags.Contains("bomb-dropped", StringComparer.Ordinal)),
            "late frame never impersonates a dropped-bomb event", ref checks);
    }

    private static void VerifyRoundIsolation(
        SituationTrainingCandidateSelector selector,
        ref int checks)
    {
        var fixture = CreateNeutralFixture(5);
        var otherAttempt = CloneAttempt(fixture.Attempt, roundId: "s0-a2");
        var other = CreateRoundFrame(
            otherAttempt,
            15,
            "live",
            fixture.Observations[0].Snapshot.Players,
            "dropped",
            fixture.TickRate);
        var baseline = Select(selector, fixture);
        var withOtherRoundFrame = Select(selector, fixture with
        {
            RoundFrames = [.. fixture.RoundFrames, other]
        });
        Check(baseline.Sha256 == withOtherRoundFrame.Sha256,
            "other-round C4 state is ignored", ref checks);

        var invalidObservation = fixture.Observations[0] with
        {
            Semantic = fixture.Observations[0].Semantic with { RoundId = "s0-a2" }
        };
        CheckThrows(() => Select(selector, fixture with
            {
                Observations = [invalidObservation, .. fixture.Observations.Skip(1)]
            }),
            "cross-round eligible observation fails", ref checks);
    }

    private static void VerifyDeterminism(
        SituationTrainingCandidateSelector selector,
        ref int checks)
    {
        var fixture = CreateRichFixture();
        var expected = Select(selector, fixture);
        var reversed = Select(selector, fixture with
        {
            Observations = fixture.Observations.Reverse().ToArray(),
            RoundFrames = fixture.RoundFrames.Reverse().ToArray(),
            KillEventTicks = fixture.KillEventTicks.Reverse().ToArray()
        });
        Check(expected.Sha256 == reversed.Sha256,
            "input enumeration order does not affect selection", ref checks);

        var originalCulture = CultureInfo.CurrentCulture;
        var originalUiCulture = CultureInfo.CurrentUICulture;
        try
        {
            foreach (var cultureName in new[] { "en-US", "fr-FR", "tr-TR", "zh-CN" })
            {
                var culture = CultureInfo.GetCultureInfo(cultureName);
                CultureInfo.CurrentCulture = culture;
                CultureInfo.CurrentUICulture = culture;
                Check(Select(selector, fixture).Sha256 == expected.Sha256,
                    $"culture-invariant selection for {cultureName}", ref checks);
            }
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
            CultureInfo.CurrentUICulture = originalUiCulture;
        }

        var repeatedTimeline = V4DataVerifier.CreateTimeline(9000);
        var repeatedAttempt = repeatedTimeline.Semantics!.Attempts.Single();
        var repeatedExpected = selector.SelectRound(
            repeatedTimeline, "repeat-fixture", repeatedAttempt);
        for (var index = 0; index < 100; index++)
            Check(selector.SelectRound(repeatedTimeline, "repeat-fixture", repeatedAttempt).Sha256 ==
                  repeatedExpected.Sha256,
                $"same-Timeline deterministic repeat {index + 1}", ref checks);

        var changedOutcome = CloneAttempt(fixture.Attempt);
        changedOutcome.Winner = "CT";
        changedOutcome.Reason = "time-expired";
        var changed = Select(selector, fixture with { Attempt = changedOutcome });
        Check(changed.Sha256 == expected.Sha256,
            "winner and end reason do not affect selection", ref checks);
    }

    private static void VerifyTimelineIntegration(
        SituationTrainingCandidateSelector selector,
        SituationSceneService sceneService,
        ref int checks)
    {
        var source = SituationFlowVerifier.BuildBoundaryTimeline(false, false);
        var baselineTimeline = CompleteTimeline(
            source,
            "T",
            "target-bombed",
            [new TimelineEvent(1800, 28.125, "kill", "private winner text", "private detail")]);
        var baselineAttempt = baselineTimeline.Semantics!.Attempts.Single();
        var baseline = selector.SelectRound(baselineTimeline, "selection-fixture", baselineAttempt);
        Check(baseline.Selection.Samples.Count == 16 &&
              baseline.Selection.Samples.Select(item => item.Tick).SequenceEqual(
                  baseline.Selection.Samples.Select(item => item.Tick).Order()),
            "full Timeline integration selects sorted capped candidates", ref checks);

        var changedTimeline = CompleteTimeline(
            SituationFlowVerifier.BuildBoundaryTimeline(false, false),
            "CT",
            "time-expired",
            [
                new TimelineEvent(1800, 28.125, "kill", "changed title", "changed detail"),
                new TimelineEvent(2300, 35.9375, "future", "future title", "future detail")
            ]) with
        {
            RoundResults = [new RoundResult(8, 1500, 1500, 2200, "CT", "time-expired")]
        };
        var changed = selector.SelectRound(
            changedTimeline, "selection-fixture", changedTimeline.Semantics!.Attempts.Single());
        Check(changed.Sha256 == baseline.Sha256,
            "winner, end reason, event text and future events do not affect selection", ref checks);
        Check(baseline.Selection.Shortages["first-casualty-state-missing"] == 1,
            "kill event without an authoritative alive decrease records a shortage", ref checks);
        Check(!baseline.CanonicalJson.Contains("private", StringComparison.OrdinalIgnoreCase) &&
              !baseline.CanonicalJson.Contains("winner", StringComparison.OrdinalIgnoreCase),
            "selection result excludes event text and outcomes", ref checks);

        var first = baseline.Selection.Samples[0];
        var semantic = baselineTimeline.Semantics.Frames.Single(item => item.Tick == first.Tick);
        using var oneSceneService = new SituationSceneService();
        var scene = new SituationEligibleSceneBuilder(oneSceneService).Build(
            baselineTimeline,
            "selection-fixture",
            first.Tick / (DemoImportService.WindowSeconds * baselineTimeline.Metadata.TickRate),
            baselineAttempt,
            semantic);
        var analysis = SituationDeterministicAnalyzer.CreateFrozen().Analyze(scene.Scene!.Scene);
        var modelInput = SituationModelInputProjector.Serialize(scene.Scene.Scene);
        var narrative = analysis.Narrative.CanonicalJson;
        Check(!modelInput.Contains("\"selectionTags\"", StringComparison.Ordinal) &&
              !narrative.Contains("\"selectionTags\"", StringComparison.Ordinal) &&
              typeof(SituationModelInputV1).GetProperty("SelectionTags") is null &&
              typeof(SituationNarrativeV1).GetProperty("SelectionTags") is null,
            "selection tags remain metadata-only", ref checks);
    }

    private static SituationTrainingRoundSelectionResult Select(
        SituationTrainingCandidateSelector selector,
        PreparedFixture fixture) => selector.SelectPrepared(
        fixture.MapName,
        fixture.TickRate,
        fixture.Attempt,
        fixture.Observations,
        fixture.RoundFrames,
        fixture.KillEventTicks);

    private static PreparedFixture CreateRichFixture()
    {
        const int tickRate = 10;
        var attempt = NewAttempt("s0-a1", 0, 200);
        var observations = Enumerable.Range(0, 20)
            .Select(index => index * 10)
            .Select(tick => CreateObservation(attempt, tick, tickRate, rich: true))
            .ToArray();
        var extras = new[]
        {
            CreateRoundFrame(attempt, 42, "live", Players(42, true), "dropped", tickRate),
            CreateRoundFrame(attempt, 52, "live", Players(52, true), "carried", tickRate),
            CreateRoundFrame(attempt, 122, "live", Players(122, true), "planting", tickRate),
            CreateRoundFrame(attempt, 142, "post-plant", Players(142, true), "defusing", tickRate)
        };
        var roundFrames = observations
            .Select(item => new SituationTrainingRoundFrame(item.Semantic, item.Snapshot))
            .Concat(extras)
            .OrderBy(item => item.Semantic.Tick)
            .ToArray();
        return new("de_mirage", tickRate, attempt, observations, roundFrames, [41]);
    }

    private static PreparedFixture CreateNeutralFixture(int count, int tickStep = 10)
    {
        const int tickRate = 10;
        var endTick = Math.Max(1, count * tickStep);
        var attempt = NewAttempt("s0-a1", 0, endTick);
        var observations = Enumerable.Range(0, count)
            .Select(index => index * tickStep)
            .Select(tick => CreateObservation(attempt, tick, tickRate, rich: false))
            .ToArray();
        return new(
            "de_mirage",
            tickRate,
            attempt,
            observations,
            observations.Select(item => new SituationTrainingRoundFrame(
                item.Semantic, item.Snapshot)).ToArray(),
            []);
    }

    private static PreparedFixture CreateMergeFixture()
    {
        var fixture = CreateNeutralFixture(3);
        var last = fixture.Observations[2];
        var players = new[]
        {
            last.Snapshot.Players.Single(item => item.Team == "T") with { Health = 90 },
            last.Snapshot.Players.Single(item => item.Team == "CT") with { Alive = false, Health = 0 }
        };
        var merged = last with
        {
            Snapshot = last.Snapshot with { Players = players, Bomb = Bomb("dropped") },
            Semantic = last.Semantic with
            {
                Roster = Quality(players),
                Clock = last.Semantic.Clock with { RoundRemainingSeconds = 5 }
            },
            Facts = last.Facts with
            {
                ContactRisk = SituationContactRisk.Medium,
                Alive = new(1, 0),
                TotalHealth = new(90, 0),
                Bomb = new(SituationBombState.Dropped, null)
            }
        };
        var drop = CreateRoundFrame(
            fixture.Attempt, 15, "live", fixture.Observations[1].Snapshot.Players,
            "dropped", fixture.TickRate);
        return fixture with
        {
            Observations = [fixture.Observations[0], fixture.Observations[1], merged],
            RoundFrames =
            [
                new(fixture.Observations[0].Semantic, fixture.Observations[0].Snapshot),
                new(fixture.Observations[1].Semantic, fixture.Observations[1].Snapshot),
                drop,
                new(merged.Semantic, merged.Snapshot)
            ],
            KillEventTicks = [15]
        };
    }

    private static SituationTrainingCandidateObservation CreateObservation(
        RoundAttempt attempt,
        int tick,
        int tickRate,
        bool rich)
    {
        var phase = rich && tick >= 130 ? "post-plant" : "live";
        var players = Players(tick, rich);
        var bombState = !rich ? "carried"
            : tick == 50 ? "dropped"
            : tick < 130 ? "carried"
            : tick < 150 ? "planted"
            : "defusing";
        var snapshot = new DemoFrame(
            tick,
            tick / (double)tickRate,
            players,
            new RoundSnapshot(1, phase, 0, 0, tick / (double)tickRate,
                rich && tick >= 120 ? Math.Max(0, 10 - (tick - 120) / 10d) : 100, 0, 0),
            Bomb(bombState),
            []);
        double? bombRemaining = rich && tick >= 130
            ? Math.Max(0, 40 - (tick - 130) / 2d)
            : null;
        var semantic = new SemanticFrame(
            tick,
            attempt.RoundId,
            1,
            0,
            phase,
            new(tick / (double)tickRate,
                rich && tick >= 120 ? Math.Max(0, 10 - (tick - 120) / 10d) : 100,
                bombRemaining,
                bombState == "defusing" ? 5 : null,
                true,
                "fixture",
                "none"),
            Quality(players));
        var counts = Counts(players);
        var facts = new SituationFactsV1(
            SituationContractVersions.Facts,
            SituationAnalysisRuleLoader.FrozenAnalysisRuleVersion,
            new(counts.T, counts.CT),
            new(players.Where(item => item.Alive && item.Team == "T").Sum(item => item.Health),
                players.Where(item => item.Alive && item.Team == "CT").Sum(item => item.Health)),
            new(ParseBomb(bombState), null),
            new(
                rich && tick >= 140 ? SituationFormation.Split : SituationFormation.Grouped,
                SituationFormation.Grouped),
            new(null, null),
            [],
            !rich || tick < 20 ? SituationContactRisk.Low
                : tick >= 170 ? SituationContactRisk.High
                : SituationContactRisk.Medium,
            rich && tick >= 160 ? SituationIsolatedSide.T : SituationIsolatedSide.None,
            SituationSpatialAdvantage.Even,
            SituationConfidence.High,
            [],
            []);
        return new(semantic, snapshot, facts);
    }

    private static SituationTrainingRoundFrame CreateRoundFrame(
        RoundAttempt attempt,
        int tick,
        string phase,
        IReadOnlyList<PlayerSnapshot> players,
        string bombState,
        int tickRate)
    {
        var snapshot = new DemoFrame(
            tick,
            tick / (double)tickRate,
            players,
            new RoundSnapshot(1, phase, 0, 0, tick / (double)tickRate, 100, 0, 0),
            Bomb(bombState),
            []);
        var semantic = new SemanticFrame(
            tick,
            attempt.RoundId,
            1,
            0,
            phase,
            new(tick / (double)tickRate, 100, null, null, true, "fixture", "none"),
            Quality(players));
        return new(semantic, snapshot);
    }

    private static IReadOnlyList<PlayerSnapshot> Players(int tick, bool rich)
    {
        if (!rich)
        {
            return
            [
                Player("t1", "T", true, 100, 0, 0),
                Player("ct1", "CT", true, 100, 1000, 1000)
            ];
        }
        var moved = tick >= 100 ? 600 : 0;
        var result = new List<PlayerSnapshot>();
        for (var index = 1; index <= 5; index++)
        {
            var alive = tick < 90 || index <= 2;
            if (tick >= 100)
                alive = index == 1;
            result.Add(Player(
                $"t{index}", "T", alive,
                alive ? (index == 1 && tick >= 30 ? 80 : 100) : 0,
                index * 20 + moved,
                index * 10 + moved));
        }
        for (var index = 1; index <= 5; index++)
        {
            var alive = tick < 40 || index < 5;
            result.Add(Player(
                $"ct{index}", "CT", alive, alive ? 100 : 0,
                1000 + index * 20 + moved,
                1000 + index * 10 + moved));
        }
        return result;
    }

    private static PlayerSnapshot Player(
        string id,
        string team,
        bool alive,
        int health,
        float x,
        float y) => new(
        id,
        "private-name",
        team,
        alive,
        health,
        x,
        y,
        0,
        0,
        0,
        0,
        0,
        team == "T" ? "T Spawn" : "CT Spawn",
        team == "T" ? "weapon_ak47" : "weapon_m4a1",
        0,
        0);

    private static BombSnapshot Bomb(string state) => new(
        state,
        state is "carried" or "planting" ? "t1" : null,
        state == "defusing" ? "ct1" : null,
        state is "planted" or "defusing" ? "A" : null,
        state is "planted" or "defusing" ? "A Site" : null,
        0,
        0,
        0,
        state is "planted" or "defusing" ? 30 : null,
        state == "defusing" ? 5 : null);

    private static RosterQuality Quality(IReadOnlyList<PlayerSnapshot> players)
    {
        var alive = players.Count(item => item.Alive && item.Team is "T" or "CT");
        return new(
            players.Count(item => item.Team == "T"),
            players.Count(item => item.Team == "CT"),
            players.Count(item => item.Team is "T" or "CT"),
            alive,
            alive,
            alive,
            true,
            "usable",
            []);
    }

    private static (int T, int CT) Counts(IReadOnlyList<PlayerSnapshot> players) => (
        players.Count(item => item.Alive && item.Team == "T"),
        players.Count(item => item.Alive && item.Team == "CT"));

    private static SituationBombState ParseBomb(string state) => state switch
    {
        "carried" => SituationBombState.Carried,
        "dropped" => SituationBombState.Dropped,
        "planting" => SituationBombState.Planting,
        "planted" => SituationBombState.Planted,
        "defusing" => SituationBombState.Defusing,
        _ => SituationBombState.Unknown
    };

    private static RoundAttempt NewAttempt(string roundId, int liveTick, int endTick) => new()
    {
        RoundId = roundId,
        SegmentId = 0,
        RoundNumber = 1,
        StartTick = liveTick,
        LiveTick = liveTick,
        EndTick = endTick,
        Disposition = "completed",
        Winner = "T",
        Reason = "target-bombed"
    };

    private static RoundAttempt CloneAttempt(RoundAttempt source, string? roundId = null) => new()
    {
        RoundId = roundId ?? source.RoundId,
        SegmentId = source.SegmentId,
        RoundNumber = source.RoundNumber,
        StartTick = source.StartTick,
        LiveTick = source.LiveTick,
        EndTick = source.EndTick,
        ScoreTAtLive = source.ScoreTAtLive,
        ScoreCTAtLive = source.ScoreCTAtLive,
        Disposition = source.Disposition,
        Winner = source.Winner,
        Reason = source.Reason
    };

    private static DemoTimeline CompleteTimeline(
        DemoTimeline source,
        string winner,
        string reason,
        IReadOnlyList<TimelineEvent> events)
    {
        var attempt = CloneAttempt(source.Semantics!.Attempts.Single());
        attempt.Disposition = "completed";
        attempt.Winner = winner;
        attempt.Reason = reason;
        return source with
        {
            Events = events,
            Semantics = source.Semantics with { Attempts = [attempt] }
        };
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
        throw new InvalidOperationException($"Stage-four selection check failed: {label}.");
    }

    private static void Check(bool condition, string label, ref int checks)
    {
        if (!condition)
            throw new InvalidOperationException($"Stage-four selection check failed: {label}.");
        checks++;
    }

    private sealed record PreparedFixture(
        string MapName,
        int TickRate,
        RoundAttempt Attempt,
        IReadOnlyList<SituationTrainingCandidateObservation> Observations,
        IReadOnlyList<SituationTrainingRoundFrame> RoundFrames,
        IReadOnlyList<int> KillEventTicks);
}

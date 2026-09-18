using System.Text.Json;
using System.Text.Json.Nodes;
using CsDemoMap.Api.Models;

namespace CsDemoMap.Api.Tests;

internal static class RoundSampleEligibilityVerifier
{
    internal static async Task VerifyAsync(CancellationToken cancellationToken)
    {
        var checks = 0;
        var repositoryRoot = SituationArtifactIO.FindRepositoryRoot(Environment.CurrentDirectory);
        var baselinePath = Path.Combine(
            repositoryRoot,
            "situation-implementation",
            "situation-stage4-eligibility-baseline-v1.json");
        using var baseline = JsonDocument.Parse(await File.ReadAllTextAsync(
            baselinePath, cancellationToken));

        VerifyResultBoundary(ref checks);
        VerifyEligibilityMatrix(ref checks);
        checks += await VerifySyntheticV4BaselineAsync(
            baseline.RootElement.GetProperty("syntheticFixture"), cancellationToken);
        VerifySyntheticRejections(
            baseline.RootElement.GetProperty("syntheticRejectionFixture"), ref checks);
        VerifyStageFourAsOfBoundary(ref checks);
        await VerifyRealArtifactBaselineAsync(
            repositoryRoot,
            baseline.RootElement,
            cancellationToken,
            count => checks += count);

        Console.WriteLine($"Stage-four eligibility checks passed: {checks}");
    }

    private static void VerifyResultBoundary(ref int checks)
    {
        var properties = typeof(RoundSampleEligibilityResult).GetProperties()
            .Select(property => property.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();
        Check(properties.SequenceEqual(["Eligible", "ReasonCode"], StringComparer.Ordinal),
            "helper result contains only eligibility and reason code", ref checks);
        Check(properties.All(property =>
                !property.Contains("winner", StringComparison.OrdinalIgnoreCase) &&
                !property.Contains("end", StringComparison.OrdinalIgnoreCase) &&
                !property.Contains("label", StringComparison.OrdinalIgnoreCase)),
            "helper result excludes outcome and training labels", ref checks);
    }

    private static void VerifyEligibilityMatrix(ref int checks)
    {
        var timeline = V4DataVerifier.CreateTimeline(9000);
        var attempt = timeline.Semantics!.Attempts.Single();
        var frame = timeline.Semantics.Frames[0];
        var snapshot = timeline.Frames.Single(item => item.Tick == frame.Tick);

        CheckDecision(RoundSampleEligibility.Evaluate(
            timeline.Metadata.MapName, attempt, frame, snapshot), true, null,
            "valid Mirage completed live tick", ref checks);
        CheckDecision(RoundSampleEligibility.EvaluateRoundTick(
            "de_inferno", attempt, frame), false, RoundSampleEligibility.UnsupportedMap,
            "Mirage map gate", ref checks);

        foreach (var disposition in new[] { "pending", "restarted", "abandoned" })
        {
            var incomplete = CloneAttempt(attempt);
            incomplete.Disposition = disposition;
            CheckDecision(RoundSampleEligibility.EvaluateRoundTick(
                timeline.Metadata.MapName, incomplete, frame),
                false, RoundSampleEligibility.RoundNotCompleted,
                $"completed attempt gate rejects {disposition}", ref checks);
        }

        CheckDecision(RoundSampleEligibility.EvaluateRoundTick(
            timeline.Metadata.MapName, attempt, frame with { RoundId = "s0-a99" }),
            false, RoundSampleEligibility.RoundAttemptMismatch,
            "round identity gate", ref checks);

        var unknownBoundary = CloneAttempt(attempt);
        unknownBoundary.LiveTick = null;
        CheckDecision(RoundSampleEligibility.EvaluateRoundTick(
            timeline.Metadata.MapName, unknownBoundary, frame),
            false, RoundSampleEligibility.RoundBoundaryUnknown,
            "known live/end boundary gate", ref checks);
        CheckDecision(RoundSampleEligibility.EvaluateRoundTick(
            timeline.Metadata.MapName, attempt, frame with { Tick = attempt.EndTick!.Value }),
            false, RoundSampleEligibility.TickOutsideLiveRange,
            "half-open live range gate", ref checks);
        CheckDecision(RoundSampleEligibility.EvaluateRoundTick(
            timeline.Metadata.MapName, attempt, frame with { Phase = "freeze" }),
            false, RoundSampleEligibility.PhaseNotEligible,
            "live or post-plant phase gate", ref checks);
        CheckDecision(RoundSampleEligibility.EvaluateRoundTick(
            timeline.Metadata.MapName, attempt, frame with { Phase = "ended" }),
            false, RoundSampleEligibility.PhaseNotEligible,
            "ended phase gate", ref checks);
        CheckDecision(RoundSampleEligibility.Evaluate(
            timeline.Metadata.MapName, attempt, frame with { Phase = "post-plant" }, snapshot),
            true, null, "post-plant phase accepted", ref checks);

        var unusable = frame with
        {
            RoundNumber = null,
            Roster = frame.Roster with
            {
                Quality = "unusable",
                Reasons = ["roster-unconfirmed", "life-unknown"]
            }
        };
        CheckDecision(RoundSampleEligibility.Evaluate(
            timeline.Metadata.MapName, attempt, unusable, snapshot),
            false, "round-unconfirmed", "v4.2 rejection precedence", ref checks);
        CheckDecision(RoundSampleEligibility.Evaluate(
            timeline.Metadata.MapName, attempt, unusable with { RoundNumber = 1 }, snapshot),
            false, "roster-unconfirmed+life-unknown", "v4.2 roster reasons", ref checks);
        CheckDecision(RoundSampleEligibility.Evaluate(
            timeline.Metadata.MapName,
            attempt,
            frame with { Clock = frame.Clock with { ClockKnown = false, ClockSource = "fixture-clock-unknown" } },
            snapshot),
            false, "fixture-clock-unknown", "v4.2 unknown clock source", ref checks);
        CheckDecision(RoundSampleEligibility.Evaluate(
            timeline.Metadata.MapName,
            attempt,
            frame with { Roster = frame.Roster with { AliveKnown = 3 } },
            snapshot),
            false, "alive-snapshot-mismatch", "v4.2 corrected alive count", ref checks);
        CheckDecision(RoundSampleEligibility.Evaluate(
            timeline.Metadata.MapName, attempt, frame, snapshot with { Tick = snapshot.Tick + 1 }),
            false, RoundSampleEligibility.SnapshotTickMismatch,
            "semantic/snapshot tick identity", ref checks);
    }

    private static async Task<int> VerifySyntheticV4BaselineAsync(
        JsonElement baseline,
        CancellationToken cancellationToken)
    {
        var checks = 0;
        var timeline = V4DataVerifier.CreateTimeline(9000);
        var built = new WinFeatureSampleBuilder().Build(timeline, cancellationToken);
        var expectedTicks = baseline.GetProperty("retainedTicks")
            .EnumerateArray().Select(item => item.GetInt32()).ToArray();
        Check(built.Samples.Select(sample => sample.Tick).SequenceEqual(expectedTicks),
            "v4.2 retained ticks unchanged", ref checks);
        Check(built.Report.RowCount == baseline.GetProperty("rowCount").GetInt32() &&
              built.Report.ExportedRounds == baseline.GetProperty("completedRounds").GetInt32(),
            "v4.2 row and completed-round counts unchanged", ref checks);
        Check(built.Report.Rejected.Count == 0 && built.Report.RejectionDetails.Count == 0,
            "v4.2 synthetic rejection baseline unchanged", ref checks);

        using var writer = new StringWriter();
        await WinDatasetV4Exporter.WriteTimelineAsync(
            timeline, baseline.GetProperty("matchId").GetString()!, writer, cancellationToken);
        var normalized = writer.ToString().Replace("\r\n", "\n", StringComparison.Ordinal);
        Check(SituationArtifactIO.Sha256(normalized) ==
              baseline.GetProperty("normalizedJsonlSha256").GetString(),
            "v4.2 normalized JSONL hash unchanged", ref checks);

        var rows = normalized.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => JsonNode.Parse(line)!.AsObject()).ToArray();
        var expectedFields = new[]
        {
            "schemaVersion", "semanticVersion", "matchId", "roundId", "segmentId",
            "roundNumber", "mapName", "tick", "demoTimeSeconds", "features",
            "labelTWin", "sampleWeight"
        };
        Check(rows.All(row => row.Select(property => property.Key).SequenceEqual(expectedFields)),
            "v4.2 row fields and order unchanged", ref checks);
        var weights = rows.Select(row => (double)row["sampleWeight"]!).ToArray();
        Check(Math.Abs(weights.Sum() - baseline.GetProperty("sampleWeightSum").GetDouble()) < 1e-12 &&
              weights.All(weight => Math.Abs(weight - 0.5) < 1e-12),
            "v4.2 per-round weights unchanged", ref checks);
        return checks;
    }

    private static void VerifySyntheticRejections(JsonElement baseline, ref int checks)
    {
        var timeline = CreateRejectionTimeline();
        var built = new WinFeatureSampleBuilder().Build(timeline);
        var expectedTicks = baseline.GetProperty("rejectedTicks")
            .EnumerateArray().Select(item => item.GetInt32()).ToArray();
        var expectedReasons = baseline.GetProperty("rejectionReasons")
            .EnumerateObject().ToDictionary(
                property => property.Name,
                property => property.Value.GetInt32(),
                StringComparer.Ordinal);
        Check(built.Report.Dispositions["completed"] ==
              baseline.GetProperty("completedRounds").GetInt32(),
            "rejection fixture completed-round count unchanged", ref checks);
        Check(built.Samples.Count == 0 && built.Report.ExportedRounds == 0,
            "rejection fixture retains no rows", ref checks);
        Check(built.Report.RejectionDetails.Select(item => item.Tick).SequenceEqual(expectedTicks),
            "rejected tick order unchanged", ref checks);
        Check(built.Report.Rejected.OrderBy(item => item.Key, StringComparer.Ordinal).SequenceEqual(
                expectedReasons.OrderBy(item => item.Key, StringComparer.Ordinal)),
            "v4.2 rejection reason counts unchanged", ref checks);
        Check(built.Report.RejectionDetails.Select(item => item.Reason).SequenceEqual(
                ["round-unconfirmed", "roster-unconfirmed+life-unknown",
                    "fixture-clock-unknown", "alive-snapshot-mismatch"]),
            "v4.2 rejection reason semantics unchanged", ref checks);
    }

    private static void VerifyStageFourAsOfBoundary(ref int checks)
    {
        var timeline = CompleteBoundaryTimeline("T", "target-bombed");
        var attempt = timeline.Semantics!.Attempts.Single();
        var frame = timeline.Semantics.Frames.Single(item => item.Tick == 1936);
        using var sceneService = new SituationSceneService();
        var builder = new SituationEligibleSceneBuilder(sceneService);
        var baseline = builder.Build(timeline, "eligibility-fixture", 1, attempt, frame);
        Check(baseline.Eligibility.Eligible && baseline.Scene is not null,
            "stage four builds an eligible scene", ref checks);

        var changedSource = CompleteBoundaryTimeline("CT", "time-expired");
        var changed = changedSource with
        {
            Events = [new TimelineEvent(2000, 31.25, "future-fixture", "must stay after target")],
            RoundResults = [new RoundResult(8, 1500, 1500, 2200, "CT", "time-expired")],
            PlayerEquipmentStates =
            [
                .. changedSource.PlayerEquipmentStates,
                new PlayerEquipmentState(
                    2000, 31.25, "t-id", 16000, 0, false, false, 0, 0, 0, [])
            ]
        };
        var changedAttempt = changed.Semantics!.Attempts.Single();
        var changedFrame = changed.Semantics.Frames.Single(item => item.Tick == 1936);
        var modified = builder.Build(
            changed, "eligibility-fixture", 1, changedAttempt, changedFrame);
        Check(modified.Eligibility.Eligible && modified.Scene is not null,
            "outcome/future mutation preserves eligibility", ref checks);
        Check(SelectionHash(attempt, frame, baseline.Eligibility) ==
              SelectionHash(changedAttempt, changedFrame, modified.Eligibility),
            "outcome/future mutation preserves selection hash", ref checks);
        Check(ModelInputHash(baseline.Scene!) == ModelInputHash(modified.Scene!),
            "outcome/future mutation preserves model input hash", ref checks);
        Check(PrelabelHash(baseline.Scene!) == PrelabelHash(modified.Scene!),
            "outcome/future mutation preserves prelabel hash", ref checks);

        var rejectedFrame = frame with { RoundNumber = null };
        var rejected = builder.Build(timeline, "not-built", -1, attempt, rejectedFrame);
        Check(!rejected.Eligibility.Eligible &&
              rejected.Eligibility.ReasonCode == RoundSampleEligibility.RoundUnconfirmed &&
              rejected.Scene is null,
            "stage four does not invoke scene construction for rejected ticks", ref checks);
    }

    private static async Task VerifyRealArtifactBaselineAsync(
        string repositoryRoot,
        JsonElement baselineRoot,
        CancellationToken cancellationToken,
        Action<int> addChecks)
    {
        var checks = 0;
        Check(baselineRoot.GetProperty("schemaVersion").GetString() ==
              "situation-stage4-eligibility-baseline-v1" &&
              baselineRoot.GetProperty("checkpointCommit").GetString() ==
              "34fa03da3b429312d421f2745d29085ceb895dfc",
            "baseline is tied to the approved checkpoint", ref checks);
        var baseline = baselineRoot.GetProperty("realArtifact");
        var relativeDirectory = baseline.GetProperty("sourceDirectory").GetString()!;
        Check(!Path.IsPathRooted(relativeDirectory) &&
              !relativeDirectory.Split('/', '\\').Contains("..", StringComparer.Ordinal),
            "real artifact baseline uses a repository-relative path", ref checks);
        var directory = Path.GetFullPath(Path.Combine(repositoryRoot, relativeDirectory));
        if (!Directory.Exists(directory))
        {
            Console.WriteLine("Real v4.2 eligibility artifact is absent; immutable baseline metadata remains verified.");
            addChecks(checks);
            return;
        }

        foreach (var property in baseline.GetProperty("files").EnumerateObject())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = Path.Combine(directory, property.Name);
            var info = new FileInfo(path);
            Check(info.Exists && info.Length == property.Value.GetProperty("bytes").GetInt64(),
                $"real artifact {property.Name} length", ref checks);
            Check(await SituationArtifactIO.FileSha256Async(path, cancellationToken) ==
                  property.Value.GetProperty("sha256").GetString(),
                $"real artifact {property.Name} hash", ref checks);
        }

        using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(
            Path.Combine(directory, "manifest.json"), cancellationToken));
        var manifestRoot = manifest.RootElement;
        var reports = manifestRoot.GetProperty("matches").EnumerateArray()
            .Select(item => item.GetProperty("report").Clone()).ToArray();
        var completedRounds = reports.Sum(report =>
            report.GetProperty("dispositions").TryGetProperty("completed", out var completed)
                ? completed.GetInt32()
                : 0);
        var retainedTicks = reports.Sum(report => report.GetProperty("rowCount").GetInt32());
        var rejectedTicks = reports.Sum(report => report.GetProperty("rejected")
            .EnumerateObject().Sum(reason => reason.Value.GetInt32()));
        Check(manifestRoot.GetProperty("semanticVersion").GetString() ==
              baseline.GetProperty("semanticVersion").GetString() &&
              reports.Length == baseline.GetProperty("matches").GetInt32(),
            "real artifact semantic version and match count", ref checks);
        Check(completedRounds == baseline.GetProperty("completedRounds").GetInt32() &&
              retainedTicks == baseline.GetProperty("retainedTicks").GetInt32() &&
              rejectedTicks == baseline.GetProperty("rejectedTicks").GetInt32(),
            "real artifact completed/retained/rejected counts", ref checks);

        using var comparison = JsonDocument.Parse(await File.ReadAllTextAsync(
            Path.Combine(directory, "comparison.json"), cancellationToken));
        Check(comparison.RootElement.GetProperty("valid").GetBoolean() &&
              comparison.RootElement.GetProperty("rows").GetInt32() == retainedTicks &&
              comparison.RootElement.GetProperty("rounds").GetInt32() == completedRounds &&
              comparison.RootElement.GetProperty("matches").GetInt32() == reports.Length,
            "real artifact comparison summary", ref checks);
        addChecks(checks);
    }

    private static DemoTimeline CreateRejectionTimeline()
    {
        var source = V4DataVerifier.CreateTimeline(9000);
        var templateFrame = source.Frames[0];
        var ticks = new[] { 64, 128, 192, 256 };
        var frames = ticks.Select(tick => templateFrame with
        {
            Tick = tick,
            TimeSeconds = tick / 64d
        }).ToArray();
        var quality = source.Semantics!.Frames[0].Roster;
        var clock = source.Semantics.Frames[0].Clock;
        var semanticFrames = new[]
        {
            new SemanticFrame(64, "s0-a2", null, 0, "live", clock, quality),
            new SemanticFrame(128, "s0-a2", 1, 0, "live", clock, quality with
            {
                Quality = "unusable",
                Reasons = ["roster-unconfirmed", "life-unknown"]
            }),
            new SemanticFrame(192, "s0-a2", 1, 0, "live", clock with
            {
                ClockKnown = false,
                ClockSource = "fixture-clock-unknown"
            }, quality),
            new SemanticFrame(256, "s0-a2", 1, 0, "live", clock, quality with { AliveKnown = 3 })
        };
        var attempt = CloneAttempt(source.Semantics.Attempts.Single());
        attempt.EndTick = 320;
        return source with
        {
            Metadata = source.Metadata with { TotalTicks = 320, DurationSeconds = 5 },
            Frames = frames,
            Semantics = new([attempt], semanticFrames, [])
        };
    }

    private static DemoTimeline CompleteBoundaryTimeline(string winner, string reason)
    {
        var source = SituationFlowVerifier.BuildBoundaryTimeline(false, false);
        var attempt = CloneAttempt(source.Semantics!.Attempts.Single());
        attempt.Disposition = "completed";
        attempt.Winner = winner;
        attempt.Reason = reason;
        return source with
        {
            Semantics = source.Semantics with { Attempts = [attempt] }
        };
    }

    private static RoundAttempt CloneAttempt(RoundAttempt source) => new()
    {
        RoundId = source.RoundId,
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

    private static string SelectionHash(
        RoundAttempt attempt,
        SemanticFrame frame,
        RoundSampleEligibilityResult eligibility) => SituationArtifactIO.Sha256(string.Join('\n',
            WinFeatureSampleBuilder.SemanticVersion,
            attempt.RoundId,
            frame.Tick,
            eligibility.Eligible,
            eligibility.ReasonCode ?? "retained"));

    private static string ModelInputHash(SituationSceneBuildResult result) =>
        SituationArtifactIO.Sha256(SituationModelInputProjector.Serialize(result.Scene));

    private static string PrelabelHash(SituationSceneBuildResult result)
    {
        var analysis = SituationDeterministicAnalyzer.CreateFrozen().Analyze(result.Scene);
        return SituationArtifactIO.Sha256(string.Join('\n',
            analysis.Facts.Sha256,
            analysis.Narrative.Sha256));
    }

    private static void CheckDecision(
        RoundSampleEligibilityResult actual,
        bool expectedEligible,
        string? expectedReason,
        string label,
        ref int checks) => Check(
            actual.Eligible == expectedEligible && actual.ReasonCode == expectedReason,
            label,
            ref checks);

    private static void Check(bool condition, string label, ref int checks)
    {
        if (!condition)
            throw new InvalidOperationException($"Stage-four eligibility check failed: {label}.");
        checks++;
    }
}

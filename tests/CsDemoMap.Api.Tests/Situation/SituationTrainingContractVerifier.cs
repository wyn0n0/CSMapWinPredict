using System.Text;
using System.Text.Json;
using CsDemoMap.Api.Models;
using CsDemoMap.Api.Services;

namespace CsDemoMap.Api.Tests;

internal static class SituationTrainingContractVerifier
{
    private static readonly string SplitSha256 = SituationArtifactIO.Sha256("stage-four-split-fixture");
    private static readonly string MatchId = SituationArtifactIO.Sha256("match-fixture");

    public static async Task VerifyAsync(CancellationToken cancellationToken)
    {
        var checks = 0;
        var repositoryRoot = SituationArtifactIO.FindRepositoryRoot(Environment.CurrentDirectory);
        var record = BuildRecord();

        VerifyRecordRoundTrip(record, ref checks);
        VerifyStrictJson(record, ref checks);
        VerifyIdentity(record, ref checks);
        VerifyModelBoundary(record, ref checks);
        VerifyRoundWeights(record, ref checks);
        VerifySelectionContract(ref checks);
        checks += await VerifySchemasAndManifestAsync(repositoryRoot, cancellationToken);
        VerifyReviewContracts(record, ref checks);

        Console.WriteLine($"Situation stage-four contract checks passed: {checks}");
    }

    private static void VerifyRecordRoundTrip(SituationTrainingRecordV1 record, ref int checks)
    {
        var json = SituationTrainingContractJson.SerializeLine(record);
        var bytes = SituationTrainingContractJson.SerializeLineUtf8(record);
        Check(!json.Contains('\r') && !json.Contains('\n'), "record is one line", ref checks);
        Check(bytes.AsSpan().SequenceEqual(new UTF8Encoding(false).GetBytes(json)),
            "record is UTF-8 without BOM", ref checks);
        Check(bytes.Length < 3 || !(bytes[0] == 0xef && bytes[1] == 0xbb && bytes[2] == 0xbf),
            "record has no BOM", ref checks);

        var orderedNames = new[]
        {
            "\"schemaVersion\"", "\"sampleId\"", "\"metadata\"", "\"input\"",
            "\"output\"", "\"labelSource\"", "\"reviewStatus\""
        };
        var positions = orderedNames.Select(name => json.IndexOf(name, StringComparison.Ordinal)).ToArray();
        Check(positions.All(position => position >= 0) &&
              positions.SequenceEqual(positions.Order()), "stable top-level field order", ref checks);
        Check(SituationTrainingContractJson.SerializeLine(record) == json,
            "record serialization is deterministic", ref checks);

        var parsed = SituationTrainingContractJson.DeserializeRecord(json);
        Check(SituationTrainingContractJson.SerializeLine(parsed) == json,
            "record strict round trip", ref checks);
        Check(parsed.Metadata.SourceSceneSha256 == SituationCanonicalJson.Sha256(BuildScene()),
            "full source scene is represented only by hash", ref checks);
        Check(parsed.LabelSource == SituationTrainingLabelSource.TemplatePrelabel &&
              parsed.ReviewStatus == SituationTrainingReviewStatus.Unreviewed,
            "prelabel state is explicit", ref checks);
    }

    private static void VerifyStrictJson(SituationTrainingRecordV1 record, ref int checks)
    {
        var json = SituationTrainingContractJson.SerializeLine(record);
        CheckThrows(() => SituationTrainingContractJson.DeserializeRecord(
            json[..^1] + ",\"winner\":\"T\"}"), "unknown field rejected", ref checks);
        CheckThrows(() => SituationTrainingContractJson.DeserializeRecord(
            json[..^1] + $",\"sampleId\":\"{record.SampleId}\"}}"),
            "duplicate field rejected", ref checks);
        CheckThrows(() => SituationTrainingContractJson.DeserializeRecord(
            json.Replace(",\"reviewStatus\":\"unreviewed\"", string.Empty, StringComparison.Ordinal)),
            "missing top-level field rejected", ref checks);
        CheckThrows(() => SituationTrainingContractJson.DeserializeRecord(
            json.Replace("\"tick\":640,", string.Empty, StringComparison.Ordinal)),
            "missing nested scalar field rejected", ref checks);
        CheckThrows(() => SituationTrainingContractJson.DeserializeRecord(
            json.Replace("\"ref\":null,", string.Empty, StringComparison.Ordinal)),
            "missing nullable nested field rejected", ref checks);
        CheckThrows(() => SituationTrainingContractJson.DeserializeRecord(
            json.Replace("\"sampleWeight\":1", "\"sampleWeight\":NaN", StringComparison.Ordinal)),
            "NaN rejected", ref checks);
        CheckThrows(() => SituationTrainingContractJson.DeserializeRecord(
            json.Replace("\"sampleWeight\":1", "\"sampleWeight\":Infinity", StringComparison.Ordinal)),
            "Infinity rejected", ref checks);
        CheckThrows(() => SituationTrainingContractJson.DeserializeRecord(
            json.Replace("\"split\":\"train\"", "\"split\":\"training\"", StringComparison.Ordinal)),
            "illegal enum rejected", ref checks);

        var unsortedTags = record with
        {
            Metadata = record.Metadata with { SelectionTags = ["round-tail", "live-start"] }
        };
        CheckThrows(() => SituationTrainingContractJson.Validate(unsortedTags),
            "noncanonical selection tags rejected", ref checks);
        var duplicateEvidence = record with
        {
            Input = record.Input with
            {
                AllowedEvidenceIds = record.Input.AllowedEvidenceIds
                    .Concat([record.Input.AllowedEvidenceIds[0]])
                    .ToArray()
            }
        };
        CheckThrows(() => SituationTrainingContractJson.Validate(duplicateEvidence),
            "noncanonical evidence IDs rejected", ref checks);
    }

    private static void VerifyIdentity(SituationTrainingRecordV1 record, ref int checks)
    {
        var versions = IdentityVersions();
        var first = SituationTrainingContractJson.CreateIdentity(MatchId, "s0-a1", 640, versions);
        var repeat = SituationTrainingContractJson.CreateIdentity(MatchId, "s0-a1", 640, versions);
        var otherTick = SituationTrainingContractJson.CreateIdentity(MatchId, "s0-a1", 641, versions);
        var otherRound = SituationTrainingContractJson.CreateIdentity(MatchId, "s0-a2", 640, versions);
        var otherMatch = SituationTrainingContractJson.CreateIdentity(
            SituationArtifactIO.Sha256("other-match"), "s0-a1", 640, versions);
        Check(first == repeat, "identity is deterministic", ref checks);
        Check(first.SampleId == record.SampleId && first.MatchRef == record.Metadata.MatchRef &&
              first.RoundRef == record.Metadata.RoundRef, "record uses derived identity", ref checks);
        Check(first.SampleId != otherTick.SampleId && first.SampleId != otherRound.SampleId &&
              first.SampleId != otherMatch.SampleId, "identity binds match, round, and tick", ref checks);
        Check(first.MatchRef == otherTick.MatchRef && first.RoundRef == otherTick.RoundRef,
            "tick does not alter grouping references", ref checks);
        Check(first.MatchRef != otherMatch.MatchRef && first.RoundRef != otherRound.RoundRef,
            "domain-separated references are scoped correctly", ref checks);

        var changedVersion = versions with { InputRepresentationVersion = "situation-model-input-v2" };
        CheckThrows(() => SituationTrainingContractJson.CreateIdentity(MatchId, "s0-a1", 640, changedVersion),
            "unfrozen upstream version rejected", ref checks);
    }

    private static void VerifyModelBoundary(SituationTrainingRecordV1 record, ref int checks)
    {
        var sceneJson = SituationCanonicalJson.Serialize(record.Input.Scene);
        Check(!sceneJson.Contains("\"source\"", StringComparison.Ordinal) &&
              !sceneJson.Contains("\"demoRef\"", StringComparison.Ordinal) &&
              !sceneJson.Contains("\"windowIndex\"", StringComparison.Ordinal) &&
              !sceneJson.Contains("\"requestedTick\"", StringComparison.Ordinal) &&
              !sceneJson.Contains("s0-a1", StringComparison.Ordinal),
            "scene projector removes source identity fields", ref checks);
        Check(record.Input.Scene.Round.Ref is null, "semantic round ID is removed", ref checks);
        Check(record.Input.AllowedEvidenceIds.SequenceEqual(
                record.Input.Facts.Evidence.Select(item => item.Id).Distinct(StringComparer.Ordinal)
                    .Order(StringComparer.Ordinal), StringComparer.Ordinal),
            "allowed evidence exactly mirrors Facts", ref checks);

        var prompt = SituationCanonicalJson.Serialize(
            SituationTrainingContractJson.ProjectPromptPair(record));
        Check(prompt.Contains("\"input\"", StringComparison.Ordinal) &&
              prompt.Contains("\"output\"", StringComparison.Ordinal) &&
              !prompt.Contains("\"metadata\"", StringComparison.Ordinal) &&
              !prompt.Contains("\"matchRef\"", StringComparison.Ordinal) &&
              !prompt.Contains("\"selectionTags\"", StringComparison.Ordinal) &&
              !prompt.Contains("Sha256", StringComparison.OrdinalIgnoreCase),
            "prompt projection contains only input and output", ref checks);

        var pathNarrative = record.Output with { SummaryZh = record.Output.SummaryZh + " demo.dem" };
        CheckThrows(() => SituationTrainingContractJson.ValidateModelBoundary(record.Input, pathNarrative),
            "sensitive string value rejected", ref checks);
        var futureNarrative = record.Output with { SummaryZh = record.Output.SummaryZh + " 将会获胜" };
        CheckThrows(() => SituationTrainingContractJson.ValidateModelBoundary(record.Input, futureNarrative),
            "future/result language rejected", ref checks);
        var steamNarrative = record.Output with { SummaryZh = record.Output.SummaryZh + " 76561198000000000" };
        CheckThrows(() => SituationTrainingContractJson.ValidateModelBoundary(record.Input, steamNarrative),
            "Steam-like identifier rejected", ref checks);
    }

    private static void VerifyRoundWeights(SituationTrainingRecordV1 record, ref int checks)
    {
        var records = new[] { 640, 704, 768 }
            .Select(tick => WithTickAndWeight(record, tick, 3))
            .ToArray();
        SituationTrainingContractJson.ValidateRoundWeights(records);
        Check(records.All(item => item.Metadata.WeightNumerator == 1 &&
                                  item.Metadata.WeightDenominator == 3 &&
                                  Math.Abs(item.Metadata.SampleWeight - 1d / 3) <= 1e-12),
            "round weights use exact 1/n rational metadata", ref checks);
        Check(Math.Abs(records.Sum(item => item.Metadata.SampleWeight) - 1d) <= 1e-12,
            "numeric round weights sum to one", ref checks);
        CheckThrows(() => SituationTrainingContractJson.ValidateRoundWeights(
                [records[0], records[0], records[2]]),
            "duplicate tick rejected", ref checks);
        var wrongDenominator = records[1] with
        {
            Metadata = records[1].Metadata with { WeightDenominator = 2, SampleWeight = 0.5 }
        };
        CheckThrows(() => SituationTrainingContractJson.ValidateRoundWeights(
                [records[0], wrongDenominator, records[2]]),
            "round amplification rejected", ref checks);
    }

    private static void VerifySelectionContract(ref int checks)
    {
        var frozen = SituationTrainingSelectionLoader.LoadFrozen();
        var repeat = SituationTrainingSelectionLoader.LoadFrozen();
        Check(frozen.Config.SchemaVersion == SituationTrainingContractVersions.Selection &&
              frozen.Config.MaxSamplesPerRound == 16 &&
              frozen.Config.RoundTail.MaximumRoundRemainingSeconds == 10 &&
              frozen.Config.RoundTail.MaximumBombRemainingSeconds == 10 &&
              frozen.Config.Clutch2vN == new SituationClutchSelectionRuleV1(2, 3) &&
              frozen.Config.Clutch1vN == new SituationClutchSelectionRuleV1(1, 2) &&
              frozen.Config.PostPlantPreference == "closest-known-bomb-countdown-midpoint",
            "frozen selection config loaded", ref checks);
        Check(frozen.Sha256 == repeat.Sha256 && frozen.CanonicalJson == repeat.CanonicalJson,
            "selection config is deterministic", ref checks);
        CheckThrows(() => SituationTrainingSelectionLoader.ParseForVerification(
            frozen.CanonicalJson[..^1] + ",\"unexpected\":1}", "unknown.json"),
            "selection unknown field rejected", ref checks);
        CheckThrows(() => SituationTrainingSelectionLoader.ParseForVerification(
            frozen.CanonicalJson[..^1] +
            $",\"schemaVersion\":\"{SituationTrainingContractVersions.Selection}\"}}", "duplicate.json"),
            "selection duplicate field rejected", ref checks);
        CheckThrows(() => SituationTrainingSelectionLoader.ParseForVerification(
            frozen.CanonicalJson.Replace("\"maxSamplesPerRound\":16,", string.Empty,
                StringComparison.Ordinal), "missing.json"),
            "selection missing field rejected", ref checks);
        CheckThrows(() => SituationTrainingSelectionLoader.ParseForVerification(
            frozen.CanonicalJson.Replace("\"allowPhysicalSampleDuplication\":false,", string.Empty,
                StringComparison.Ordinal), "missing-false.json"),
            "selection missing false-valued field rejected", ref checks);
        CheckThrows(() => SituationTrainingSelectionLoader.ParseForVerification(
            frozen.CanonicalJson.Replace("\"maximumRoundRemainingSeconds\":10",
                "\"maximumRoundRemainingSeconds\":11", StringComparison.Ordinal),
            "bad-tail.json"), "selection round-tail threshold drift rejected", ref checks);
        CheckThrows(() => SituationTrainingSelectionLoader.ParseForVerification(
            frozen.CanonicalJson.Replace("\"minimumOpponentAlive\":3",
                "\"minimumOpponentAlive\":4", StringComparison.Ordinal),
            "bad-clutch.json"), "selection clutch threshold drift rejected", ref checks);
    }

    private static async Task<int> VerifySchemasAndManifestAsync(
        string repositoryRoot,
        CancellationToken cancellationToken)
    {
        var checks = 0;
        var schemas = await SituationTrainingSchemaRegistry.LoadAsync(repositoryRoot, cancellationToken);
        Check(schemas.Count == 14 && schemas.All(item => item.Sha256.Length == 64),
            "all versioned schemas have SHA-256 references", ref checks);
        Check(schemas.Select(item => item.SchemaVersion).Distinct(StringComparer.Ordinal).Count() == schemas.Count,
            "schema versions are unique", ref checks);
        await SituationTrainingSchemaRegistry.VerifyAsync(repositoryRoot, schemas, cancellationToken);
        Check(true, "schema version and hash verification", ref checks);
        var badSchemas = schemas.ToArray();
        badSchemas[0] = badSchemas[0] with { Sha256 = new string('0', 64) };
        Check(await ThrowsAsync(
                () => SituationTrainingSchemaRegistry.VerifyAsync(repositoryRoot, badSchemas, cancellationToken)),
            "schema hash mismatch rejected", ref checks);

        var artifactRoot = Path.Combine(Path.GetTempPath(), $"situation-contract-{Guid.NewGuid():N}");
        Directory.CreateDirectory(artifactRoot);
        try
        {
            var filePath = Path.Combine(artifactRoot, "records.jsonl");
            var fileText = "{\"row\":1}\n{\"row\":2}\n";
            await File.WriteAllTextAsync(filePath, fileText, new UTF8Encoding(false), cancellationToken);
            var fileBytes = await File.ReadAllBytesAsync(filePath, cancellationToken);
            var manifest = new SituationTrainingDatasetManifestV1(
                SituationTrainingContractVersions.DatasetManifest,
                SituationArtifactStatus.Complete,
                SituationTrainingExportMode.Full,
                "stage-four-situation-training-dataset",
                true,
                null,
                SplitSha256,
                SituationArtifactIO.Sha256("parent-manifest"),
                SituationArtifactIO.Sha256("selection-config"),
                SituationArtifactIO.Sha256("input-representation-config"),
                new(
                    SituationTrainingContractVersions.Data,
                    SituationTrainingContractVersions.Split,
                    SituationTrainingContractVersions.TrainingRecord,
                    SituationContractVersions.Scene,
                    SituationSceneBuilder.BuilderVersion,
                    SituationSceneBuilder.GeometryVersion,
                    SituationContractVersions.Facts,
                    SituationAnalysisRuleLoader.FrozenAnalysisRuleVersion,
                    SituationContractVersions.Narrative,
                    WinFeatureSampleBuilder.SemanticVersion,
                    SituationTrainingContractVersions.Selection,
                    SituationTrainingContractVersions.InputRepresentation,
                    SituationTrainingContractVersions.PromptRepresentationConfig,
                    SituationTrainingContractVersions.RepresentationMeasurement,
                    SituationTrainingContractVersions.ReviewCandidate,
                    SituationTrainingContractVersions.ReviewDecision,
                    SituationTrainingContractVersions.LabelStats,
                    SituationTrainingContractVersions.FrozenReviewLabel,
                    SituationTrainingContractVersions.FrozenReviewManifest,
                    null),
                schemas,
                new Dictionary<string, SituationDatasetCountsV1>(StringComparer.Ordinal)
                {
                    ["train"] = new(1, 1, 2),
                    ["dev"] = new(0, 0, 0),
                    ["test"] = new(0, 0, 0)
                },
                [new("records.jsonl", fileBytes.Length, 2, SituationArtifactIO.Sha256(fileBytes))]);
            SituationTrainingManifestValidator.Validate(manifest);
            await SituationTrainingManifestValidator.VerifyAsync(
                repositoryRoot, artifactRoot, manifest, cancellationToken);
            Check(true, "manifest verifies versions, schemas, files, rows, and hashes", ref checks);

            var manifestJson = SituationCanonicalJson.Serialize(manifest);
            Check(SituationCanonicalJson.Serialize(
                      SituationTrainingContractJson.DeserializeManifest(manifestJson)) == manifestJson,
                "manifest strict round trip", ref checks);
            CheckThrows(() => SituationTrainingContractJson.DeserializeManifest(
                manifestJson[..^1] + ",\"unexpected\":1}"),
                "manifest unknown field rejected", ref checks);
            CheckThrows(() => SituationTrainingManifestValidator.Validate(manifest with
            {
                Versions = manifest.Versions with { Selection = "v1" }
            }), "generic manifest version rejected", ref checks);

            await File.AppendAllTextAsync(filePath, "tamper", new UTF8Encoding(false), cancellationToken);
            Check(await ThrowsAsync(() => SituationTrainingManifestValidator.VerifyAsync(
                    repositoryRoot, artifactRoot, manifest, cancellationToken)),
                "artifact hash or length mismatch rejected", ref checks);
        }
        finally
        {
            Directory.Delete(artifactRoot, recursive: true);
        }
        return checks;
    }

    private static void VerifyReviewContracts(SituationTrainingRecordV1 record, ref int checks)
    {
        var candidateSha256 = SituationCanonicalJson.Sha256(record.Output);
        var candidate = new SituationReviewCandidateV1(
            SituationTrainingContractVersions.ReviewCandidate,
            1,
            record.SampleId,
            record.Metadata.Split,
            record.Metadata.MatchRef,
            record.Metadata.RoundRef,
            record.Metadata.SelectionTags,
            record.Input,
            record.Output,
            SituationArtifactIO.Sha256(SituationTrainingContractJson.SerializeLine(record)),
            candidateSha256);
        SituationTrainingContractJson.Validate(candidate);
        var candidateJson = SituationCanonicalJson.Serialize(candidate);
        Check(SituationCanonicalJson.Serialize(
                  SituationTrainingContractJson.DeserializeReviewCandidate(candidateJson)) == candidateJson,
            "review candidate is strict and hash-bound", ref checks);
        var approved = SituationTrainingContractJson.WithDecisionHash(new(
            SituationTrainingContractVersions.ReviewDecision,
            SituationArtifactIO.Sha256("dataset"),
            candidateSha256,
            record.SampleId,
            0,
            SituationReviewDecisionKind.Approved,
            new(true, true, true, false),
            [],
            [],
            null,
            null,
            candidateSha256,
            null,
            false,
            new DateTimeOffset(2026, 9, 18, 0, 0, 0, TimeSpan.Zero),
            new string('0', 64)));
        SituationTrainingContractJson.Validate(approved, candidate);
        var approvedJson = SituationCanonicalJson.Serialize(approved);
        Check(SituationCanonicalJson.Serialize(
                  SituationTrainingContractJson.DeserializeReviewDecision(approvedJson, candidate)) == approvedJson,
            "approved review decision is strict and hash-bound", ref checks);

        var modifiedNarrative = record.Output with { SummaryZh = record.Output.SummaryZh + "。" };
        var modified = SituationTrainingContractJson.WithDecisionHash(approved with
        {
            Decision = SituationReviewDecisionKind.Modified,
            FinalNarrative = modifiedNarrative,
            FinalNarrativeSha256 = SituationCanonicalJson.Sha256(modifiedNarrative)
        });
        SituationTrainingContractJson.Validate(modified, candidate);
        Check(modified.FinalNarrativeSha256 != candidateSha256,
            "modified review is stored separately", ref checks);

        var rejected = SituationTrainingContractJson.WithDecisionHash(approved with
        {
            Decision = SituationReviewDecisionKind.Rejected,
            Evaluations = new(false, true, false, false),
            IssueFields = ["/facts/alive"],
            IssueCodes = ["facts-incorrect"],
            FinalNarrativeSha256 = null,
            RejectReasonCode = "facts-incorrect",
            BlockingIssue = true
        });
        SituationTrainingContractJson.Validate(rejected, candidate);
        CheckThrows(() => SituationTrainingContractJson.Validate(
                SituationTrainingContractJson.WithDecisionHash(rejected with { BlockingIssue = false })),
            "incorrect facts require a blocking review issue", ref checks);

        SituationTrainingContractJson.ValidateFrozenReviewLabel(new(
            SituationTrainingContractVersions.FrozenReviewLabel,
            record.SampleId,
            record.Output,
            SituationTrainingLabelSource.HumanApproved,
            "situation-review-v1",
            ["sft"],
            5), SituationTrainingSplit.Train, candidate);
        SituationTrainingContractJson.ValidateFrozenReviewLabel(new(
            SituationTrainingContractVersions.FrozenReviewLabel,
            record.SampleId,
            record.Output,
            SituationTrainingLabelSource.HumanModified,
            "situation-review-v1",
            ["checkpoint-selection", "evaluation", "prompt-selection"],
            null), SituationTrainingSplit.Dev, candidate);
        SituationTrainingContractJson.ValidateFrozenReviewLabel(new(
            SituationTrainingContractVersions.FrozenReviewLabel,
            record.SampleId,
            record.Output,
            SituationTrainingLabelSource.HumanApproved,
            "situation-review-v1",
            ["final-evaluation"],
            null), SituationTrainingSplit.Test, candidate);
        Check(true, "split-specific frozen label use accepted", ref checks);
        CheckThrows(() => SituationTrainingContractJson.ValidateFrozenReviewLabel(new(
                SituationTrainingContractVersions.FrozenReviewLabel,
                record.SampleId,
                record.Output,
                SituationTrainingLabelSource.HumanApproved,
                "situation-review-v1",
                ["sft"],
                5), SituationTrainingSplit.Dev),
            "dev/test SFT repetition rejected", ref checks);
    }

    internal static SituationTrainingRecordV1 BuildRecord()
    {
        var sourceScene = BuildScene();
        var analysis = SituationDeterministicAnalyzer.CreateFrozen().Analyze(sourceScene);
        var scene = SituationModelInputProjector.Project(sourceScene);
        var evidenceIds = analysis.Facts.Facts.Evidence.Select(item => item.Id)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (evidenceIds.Length == 0)
            throw new InvalidOperationException("Training contract fixture produced no evidence.");
        var identity = SituationTrainingContractJson.CreateIdentity(MatchId, "s0-a1", scene.Tick,
            IdentityVersions());
        var input = new SituationTrainingInputV1(scene, analysis.Facts.Facts, evidenceIds);
        var metadata = new SituationTrainingMetadataV1(
            SituationTrainingSplit.Train,
            identity.MatchRef,
            identity.RoundRef,
            scene.Tick,
            SituationTrainingPhase.Live,
            ["live-start"],
            1,
            1,
            1,
            SituationCanonicalJson.Sha256(sourceScene),
            SituationCanonicalJson.Sha256(scene),
            SituationCanonicalJson.Sha256(analysis.Facts.Facts),
            SituationCanonicalJson.Sha256(analysis.Narrative.Narrative));
        var record = new SituationTrainingRecordV1(
            SituationTrainingContractVersions.TrainingRecord,
            identity.SampleId,
            metadata,
            input,
            analysis.Narrative.Narrative,
            SituationTrainingLabelSource.TemplatePrelabel,
            SituationTrainingReviewStatus.Unreviewed);
        SituationTrainingContractJson.Validate(record);
        return record;
    }

    private static MinimapSceneV1 BuildScene()
    {
        var unknownUtility = new SituationUtilityCounts(null, null, null, null, null, null);
        var unknownWeapons = new SituationWeaponCounts(
            null, null, null, null, null, null, null, null, null, null, null);
        var unknownTeam = new SituationTeamSummary(
            null, null, null, null, null, null, null, unknownUtility, unknownWeapons);
        var scene = new MinimapSceneV1(
            SituationContractVersions.Scene,
            SituationSceneBuilder.BuilderVersion,
            SituationSceneBuilder.GeometryVersion,
            "structured",
            "de_mirage",
            "fixture-stage-four.dem",
            4,
            640,
            640,
            64,
            new("s0-a1", 0, 1, SituationRoundPhase.Live, 10, 105, new(0, 0), "fixture"),
            [],
            new(unknownTeam, unknownTeam),
            new(SituationBombState.Unknown, null, null, null, null, null,
                SituationRegionSource.Unknown, null, null),
            [],
            [],
            new([], []),
            []);
        SituationContractValidator.Validate(scene);
        return scene;
    }

    private static SituationTrainingIdentityVersions IdentityVersions() => new(
        SituationTrainingContractVersions.Data,
        SplitSha256,
        SituationContractVersions.Scene,
        SituationSceneBuilder.BuilderVersion,
        SituationSceneBuilder.GeometryVersion,
        SituationContractVersions.Facts,
        SituationAnalysisRuleLoader.FrozenAnalysisRuleVersion,
        SituationContractVersions.Narrative,
        WinFeatureSampleBuilder.SemanticVersion,
        SituationTrainingContractVersions.Selection,
        SituationTrainingContractVersions.InputRepresentation);

    private static SituationTrainingRecordV1 WithTickAndWeight(
        SituationTrainingRecordV1 record,
        int tick,
        int denominator)
    {
        var scene = record.Input.Scene with { Tick = tick };
        var identity = SituationTrainingContractJson.CreateIdentity(MatchId, "s0-a1", tick, IdentityVersions());
        return record with
        {
            SampleId = identity.SampleId,
            Metadata = record.Metadata with
            {
                Tick = tick,
                SampleWeight = 1d / denominator,
                WeightDenominator = denominator,
                SourceSceneSha256 = SituationArtifactIO.Sha256($"source-scene-{tick}"),
                ModelInputSha256 = SituationCanonicalJson.Sha256(scene)
            },
            Input = record.Input with { Scene = scene }
        };
    }

    private static void Check(bool condition, string label, ref int checks)
    {
        if (!condition)
            throw new InvalidOperationException($"Situation training contract check failed: {label}.");
        checks++;
    }

    private static void CheckThrows(Action action, string label, ref int checks)
    {
        try
        {
            action();
        }
        catch (Exception exception) when (exception is InvalidDataException or JsonException or NotSupportedException)
        {
            checks++;
            return;
        }
        throw new InvalidOperationException($"Situation training contract check failed: {label}.");
    }

    private static async Task<bool> ThrowsAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception exception) when (exception is InvalidDataException or FileNotFoundException)
        {
            return true;
        }
        return false;
    }
}

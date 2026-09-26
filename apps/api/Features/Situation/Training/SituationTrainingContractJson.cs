using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Reflection;
using CsDemoMap.Api.Models;

namespace CsDemoMap.Api.Services;

internal static class SituationTrainingContractJson
{
    internal const double WeightTolerance = 1e-12;

    private static readonly UTF8Encoding Utf8NoBom = new(false);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = false
    };

    private static readonly IReadOnlySet<string> ForbiddenPropertyNames = new HashSet<string>(
        [
            "winner", "roundWinner", "winningSide", "labelTWin", "roundResults",
            "steamId", "steamIds", "playerName", "playerNames", "teamName", "teamNames",
            "demoPath", "demoFile", "sourceDirectory", "futureEvent", "futureEvents",
            "winProbability", "tacticalInstruction", "tacticalInstructions"
        ],
        StringComparer.OrdinalIgnoreCase);

    private static readonly string[] ForbiddenStringTerms =
    [
        ".dem", "steam", "winner", "labelTWin", "roundResults", "future event",
        "win probability", "win_probability", "tactical instruction", "demo path",
        "player name", "team name", "选手名", "战队名",
        "胜率", "赢家", "获胜", "将会", "会赢", "建议", "应该", "应当", "战术指令"
    ];

    private static readonly IReadOnlySet<string> BlockingIssueCodes = new HashSet<string>(
        ["facts-incorrect", "identity-leak", "future-information", "unknown-evidence"],
        StringComparer.Ordinal);

    internal static SituationTrainingIdentityV1 CreateIdentity(
        string matchId,
        string semanticRoundId,
        int tick,
        SituationTrainingIdentityVersions versions)
    {
        if (!IsSha256(matchId) ||
            !Regex.IsMatch(semanticRoundId ?? string.Empty, "^s[0-9]+-a[1-9][0-9]*$", RegexOptions.CultureInvariant) ||
            tick < 0)
            throw new InvalidDataException("Training identity source values are invalid.");
        ValidateIdentityVersions(versions);

        var matchRef = "match-" + SituationArtifactIO.Sha256(string.Join('\n',
            "situation-training-match-ref-v1",
            $"data={versions.DataVersion}",
            $"match={matchId}"));
        var roundRef = "round-" + SituationArtifactIO.Sha256(string.Join('\n',
            "situation-training-round-ref-v1",
            $"data={versions.DataVersion}",
            $"match={matchId}",
            $"round={semanticRoundId}"));
        var sampleId = "sample-" + SituationArtifactIO.Sha256(string.Join('\n',
            "situation-training-sample-v1",
            $"data={versions.DataVersion}",
            $"split={versions.SplitSha256}",
            $"match={matchId}",
            $"round={semanticRoundId}",
            $"tick={tick}",
            $"scene={versions.SceneSchemaVersion}",
            $"builder={versions.SceneBuilderVersion}",
            $"geometry={versions.GeometryVersion}",
            $"facts={versions.FactsSchemaVersion}",
            $"rules={versions.AnalysisRuleVersion}",
            $"narrative={versions.NarrativeSchemaVersion}",
            $"eligibility={versions.SemanticEligibilityVersion}",
            $"selection={versions.SelectionVersion}",
            $"representation={versions.InputRepresentationVersion}"));
        return new(sampleId, matchRef, roundRef);
    }

    internal static string SerializeLine(SituationTrainingRecordV1 record)
    {
        Validate(record);
        var json = JsonSerializer.Serialize(record, JsonOptions);
        if (json.Contains('\r') || json.Contains('\n') || json.StartsWith('\uFEFF'))
            throw new InvalidOperationException("Training record serialization is not a single JSON line.");
        return json;
    }

    internal static byte[] SerializeLineUtf8(SituationTrainingRecordV1 record) =>
        Utf8NoBom.GetBytes(SerializeLine(record));

    internal static SituationTrainingRecordV1 DeserializeRecord(string json)
    {
        try
        {
            RejectDuplicateProperties(json, "Training record");
            RequireCompleteShape<SituationTrainingRecordV1>(json, "Training record");
            var record = JsonSerializer.Deserialize<SituationTrainingRecordV1>(json, JsonOptions)
                ?? throw new InvalidDataException("Training record is empty.");
            Validate(record);
            return record;
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Training record JSON is invalid or contains unknown fields.", exception);
        }
    }

    internal static SituationReviewDecisionV1 DeserializeReviewDecision(string json)
    {
        try
        {
            RejectDuplicateProperties(json, "Review decision");
            RequireCompleteShape<SituationReviewDecisionV1>(json, "Review decision");
            var decision = JsonSerializer.Deserialize<SituationReviewDecisionV1>(json, JsonOptions)
                ?? throw new InvalidDataException("Review decision is empty.");
            Validate(decision);
            return decision;
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Review decision JSON is invalid or contains unknown or missing fields.", exception);
        }
    }

    internal static SituationReviewDecisionV1 DeserializeReviewDecision(
        string json,
        SituationReviewCandidateV1 candidate)
    {
        var decision = DeserializeReviewDecision(json);
        Validate(decision, candidate);
        return decision;
    }

    internal static SituationReviewCandidateV1 DeserializeReviewCandidate(string json)
    {
        try
        {
            RejectDuplicateProperties(json, "Review candidate");
            RequireCompleteShape<SituationReviewCandidateV1>(json, "Review candidate");
            var candidate = JsonSerializer.Deserialize<SituationReviewCandidateV1>(json, JsonOptions)
                ?? throw new InvalidDataException("Review candidate is empty.");
            Validate(candidate);
            return candidate;
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Review candidate JSON is invalid or contains unknown or missing fields.", exception);
        }
    }

    internal static SituationTrainingDatasetManifestV1 DeserializeManifest(string json)
    {
        try
        {
            RejectDuplicateProperties(json, "Training manifest");
            RequireCompleteShape<SituationTrainingDatasetManifestV1>(json, "Training manifest");
            var manifest = JsonSerializer.Deserialize<SituationTrainingDatasetManifestV1>(json, JsonOptions)
                ?? throw new InvalidDataException("Training manifest is empty.");
            SituationTrainingManifestValidator.Validate(manifest);
            return manifest;
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Training manifest JSON is invalid or contains unknown or missing fields.", exception);
        }
    }

    internal static void Validate(SituationTrainingRecordV1 record)
    {
        var errors = new List<string>();
        Require(record.SchemaVersion == SituationTrainingContractVersions.TrainingRecord,
            "training schemaVersion mismatch", errors);
        Require(IsPrefixedSha256(record.SampleId, "sample-"), "sampleId is invalid", errors);
        if (record.Metadata is null) errors.Add("metadata is missing");
        if (record.Input is null) errors.Add("input is missing");
        if (record.Output is null) errors.Add("output is missing");
        if (errors.Count > 0)
            Throw(errors);

        var metadata = record.Metadata ?? throw new InvalidDataException("metadata is missing");
        Require(Enum.IsDefined(metadata.Split), "split is invalid", errors);
        Require(IsPrefixedSha256(metadata.MatchRef, "match-"), "matchRef is invalid", errors);
        Require(IsPrefixedSha256(metadata.RoundRef, "round-"), "roundRef is invalid", errors);
        Require(metadata.Tick >= 0, "tick is negative", errors);
        Require(Enum.IsDefined(metadata.Phase), "phase is invalid", errors);
        ValidateSortedSemanticSet(metadata.SelectionTags, "selectionTags", requireNonEmpty: true, errors);
        Require(metadata.WeightNumerator == 1, "weightNumerator must be one", errors);
        Require(metadata.WeightDenominator is >= 1 and <= 16, "weightDenominator is invalid", errors);
        Require(double.IsFinite(metadata.SampleWeight) &&
                Math.Abs(metadata.SampleWeight - 1d / metadata.WeightDenominator) <= WeightTolerance,
            "sampleWeight differs from its rational weight", errors);
        Require(IsSha256(metadata.SourceSceneSha256), "sourceSceneSha256 is invalid", errors);
        Require(IsSha256(metadata.ModelInputSha256), "modelInputSha256 is invalid", errors);
        Require(IsSha256(metadata.FactsSha256), "factsSha256 is invalid", errors);
        Require(IsSha256(metadata.PrelabelSha256), "prelabelSha256 is invalid", errors);

        var input = record.Input ?? throw new InvalidDataException("input is missing");
        if (input.Scene is null) errors.Add("input.scene is missing");
        if (input.Facts is null) errors.Add("input.facts is missing");
        if (input.AllowedEvidenceIds is null) errors.Add("input.allowedEvidenceIds is missing");
        if (errors.Count > 0)
            Throw(errors);

        var scene = input.Scene ?? throw new InvalidDataException("input.scene is missing");
        var facts = input.Facts ?? throw new InvalidDataException("input.facts is missing");
        var allowedEvidenceIds = input.AllowedEvidenceIds ?? throw new InvalidDataException("input.allowedEvidenceIds is missing");
        var output = record.Output ?? throw new InvalidDataException("output is missing");
        ValidateProjectedScene(scene, errors);
        var sourceScene = RestoreValidationScene(scene);
        try
        {
            SituationContractValidator.Validate(facts, sourceScene,
                SituationAnalysisRuleLoader.FrozenAnalysisRuleVersion);
        }
        catch (InvalidDataException exception)
        {
            errors.Add($"facts are invalid: {exception.Message}");
        }
        var expectedEvidenceIds = facts.Evidence.Select(item => item.Id)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        Require(allowedEvidenceIds.SequenceEqual(expectedEvidenceIds, StringComparer.Ordinal),
            "allowedEvidenceIds must exactly equal the sorted Facts evidence IDs", errors);
        try
        {
            SituationContractValidator.ValidateTemplate(output, facts);
        }
        catch (InvalidDataException exception)
        {
            errors.Add($"output is not a valid template prelabel: {exception.Message}");
        }
        Require(metadata.Tick == scene.Tick, "metadata tick differs from model scene", errors);
        Require((metadata.Phase == SituationTrainingPhase.Live &&
                 scene.Round.Phase == SituationRoundPhase.Live) ||
                (metadata.Phase == SituationTrainingPhase.PostPlant &&
                 scene.Round.Phase == SituationRoundPhase.PostPlant),
            "metadata phase differs from model scene", errors);
        Require(metadata.ModelInputSha256 == SituationCanonicalJson.Sha256(scene),
            "modelInputSha256 mismatch", errors);
        Require(metadata.FactsSha256 == SituationCanonicalJson.Sha256(facts),
            "factsSha256 mismatch", errors);
        Require(metadata.PrelabelSha256 == SituationCanonicalJson.Sha256(output),
            "prelabelSha256 mismatch", errors);
        Require(record.LabelSource == SituationTrainingLabelSource.TemplatePrelabel,
            "base record labelSource must be template-prelabel", errors);
        Require(record.ReviewStatus == SituationTrainingReviewStatus.Unreviewed,
            "base record reviewStatus must be unreviewed", errors);

        try
        {
            ValidateModelBoundary(input, output);
        }
        catch (InvalidDataException exception)
        {
            errors.Add(exception.Message);
        }
        Throw(errors);
    }

    internal static SituationPromptPairV1 ProjectPromptPair(SituationTrainingRecordV1 record)
    {
        Validate(record);
        return new(record.Input, record.Output);
    }

    internal static void ValidateRoundWeights(IEnumerable<SituationTrainingRecordV1> records)
    {
        var materialized = records.ToArray();
        foreach (var record in materialized)
            Validate(record);
        foreach (var group in materialized.GroupBy(
                     record => (record.Metadata.Split, record.Metadata.MatchRef, record.Metadata.RoundRef)))
        {
            var items = group.ToArray();
            if (items.Length is < 1 or > 16 ||
                items.Select(item => item.Metadata.Tick).Distinct().Count() != items.Length)
                throw new InvalidDataException("A training round contains duplicate ticks or more than 16 samples.");
            if (items.Any(item => item.Metadata.WeightNumerator != 1 ||
                                  item.Metadata.WeightDenominator != items.Length ||
                                  !double.IsFinite(item.Metadata.SampleWeight) ||
                                  Math.Abs(item.Metadata.SampleWeight - 1d / items.Length) > WeightTolerance))
                throw new InvalidDataException("A training round does not use exact 1/n sample weights.");
            if (items.Sum(item => item.Metadata.WeightNumerator) != items[0].Metadata.WeightDenominator ||
                Math.Abs(items.Sum(item => item.Metadata.SampleWeight) - 1d) > WeightTolerance)
                throw new InvalidDataException("A training round weight sum is not exactly one.");
        }
    }

    internal static string ComputeDecisionSha256(SituationReviewDecisionV1 decision) =>
        SituationCanonicalJson.Sha256(new
        {
            decision.SchemaVersion,
            decision.DatasetSha256,
            decision.CandidateSha256,
            decision.SampleId,
            decision.SampleRevision,
            decision.Decision,
            decision.Evaluations,
            decision.IssueFields,
            decision.IssueCodes,
            decision.Note,
            decision.FinalNarrative,
            decision.FinalNarrativeSha256,
            decision.RejectReasonCode,
            decision.BlockingIssue,
            decision.ReviewedAtUtc
        });

    internal static SituationReviewDecisionV1 WithDecisionHash(SituationReviewDecisionV1 decision) =>
        decision with { DecisionSha256 = ComputeDecisionSha256(decision) };

    internal static void Validate(SituationReviewCandidateV1 candidate)
    {
        var errors = new List<string>();
        Require(candidate.SchemaVersion is SituationTrainingContractVersions.ReviewCandidate or SituationRaycastReviewCatalog.CandidateSchema,
            "review candidate schemaVersion mismatch", errors);
        Require(candidate.ReviewOrdinal >= 1, "review candidate ordinal is invalid", errors);
        Require(IsPrefixedSha256(candidate.SampleId, "sample-"), "review candidate sampleId is invalid", errors);
        Require(Enum.IsDefined(candidate.Split), "review candidate split is invalid", errors);
        Require(IsPrefixedSha256(candidate.MatchRef, "match-"), "review candidate matchRef is invalid", errors);
        Require(IsPrefixedSha256(candidate.RoundRef, "round-"), "review candidate roundRef is invalid", errors);
        ValidateSortedSemanticSet(candidate.SelectionTags, "selectionTags", requireNonEmpty: true, errors);
        Require(IsSha256(candidate.RecordSha256), "review candidate recordSha256 is invalid", errors);
        Require(candidate.Input is not null, "review candidate input is missing", errors);
        Require(candidate.Candidate is not null, "review candidate narrative is missing", errors);
        if (candidate.Input is { } input && candidate.Candidate is { } narrative)
        {
            var evidenceIds = input.Facts?.Evidence?.Select(item => item.Id)
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToArray() ?? [];
            Require(input.AllowedEvidenceIds is not null &&
                    input.AllowedEvidenceIds.SequenceEqual(evidenceIds, StringComparer.Ordinal),
                "review candidate allowed evidence IDs mismatch", errors);
            if (input.Scene is not null && input.Facts is not null)
            {
                ValidateProjectedScene(input.Scene, errors);
                try
                {
                    SituationContractValidator.Validate(input.Facts, RestoreValidationScene(input.Scene),
                        candidate.SchemaVersion == SituationRaycastReviewCatalog.CandidateSchema
                            ? "situation-analysis-rules-v2-raycast-1" : SituationAnalysisRuleLoader.FrozenAnalysisRuleVersion);
                    SituationContractValidator.ValidateTemplate(narrative, input.Facts);
                    ValidateModelBoundary(input, narrative);
                }
                catch (InvalidDataException exception)
                {
                    errors.Add($"review candidate payload is invalid: {exception.Message}");
                }
            }
            Require(candidate.CandidateSha256 == SituationCanonicalJson.Sha256(narrative),
                "review candidate narrative hash mismatch", errors);
        }
        Throw(errors);
    }

    internal static void Validate(SituationReviewDecisionV1 decision)
    {
        var errors = new List<string>();
        Require(decision.SchemaVersion == SituationTrainingContractVersions.ReviewDecision,
            "review decision schemaVersion mismatch", errors);
        Require(IsSha256(decision.DatasetSha256), "review datasetSha256 is invalid", errors);
        Require(IsSha256(decision.CandidateSha256), "review candidateSha256 is invalid", errors);
        Require(IsPrefixedSha256(decision.SampleId, "sample-"), "review sampleId is invalid", errors);
        Require(decision.SampleRevision >= 0, "review sampleRevision is negative", errors);
        Require(Enum.IsDefined(decision.Decision), "review decision enum is invalid", errors);
        Require(decision.Evaluations is not null, "review evaluations are missing", errors);
        ValidateSortedJsonPointers(decision.IssueFields, "issueFields", errors);
        ValidateSortedSemanticSet(decision.IssueCodes, "issueCodes", requireNonEmpty: false, errors);
        Require(decision.Note is null || decision.Note.Length <= 1000, "review note is too long", errors);
        Require(decision.ReviewedAtUtc.Offset == TimeSpan.Zero, "reviewedAtUtc must be UTC", errors);
        Require(IsSha256(decision.DecisionSha256) &&
                decision.DecisionSha256 == ComputeDecisionSha256(decision),
            "decisionSha256 mismatch", errors);
        if (decision.Evaluations is not null)
        {
            var requiresBlock = !decision.Evaluations.FactsCorrect ||
                                decision.IssueCodes?.Any(BlockingIssueCodes.Contains) == true;
            Require(!requiresBlock || decision.BlockingIssue,
                "blocking review issue was not escalated", errors);
        }

        switch (decision.Decision)
        {
            case SituationReviewDecisionKind.Approved:
                Require(decision.FinalNarrative is null, "approved decision must not duplicate the candidate", errors);
                Require(decision.FinalNarrativeSha256 == decision.CandidateSha256,
                    "approved final hash must equal candidate hash", errors);
                Require(decision.RejectReasonCode is null, "approved decision has a reject reason", errors);
                break;
            case SituationReviewDecisionKind.Modified:
                Require(decision.FinalNarrative is not null, "modified decision has no final narrative", errors);
                Require(decision.RejectReasonCode is null, "modified decision has a reject reason", errors);
                if (decision.FinalNarrative is { } narrative)
                {
                    try
                    {
                        var ids = narrative.Highlights.SelectMany(item => item.EvidenceIds)
                            .ToHashSet(StringComparer.Ordinal);
                        SituationContractValidator.Validate(narrative, ids);
                    }
                    catch (InvalidDataException exception)
                    {
                        errors.Add($"modified narrative is invalid: {exception.Message}");
                    }
                    Require(decision.FinalNarrativeSha256 == SituationCanonicalJson.Sha256(narrative),
                        "modified final narrative hash mismatch", errors);
                    Require(decision.FinalNarrativeSha256 != decision.CandidateSha256,
                        "modified narrative is unchanged", errors);
                }
                break;
            case SituationReviewDecisionKind.Rejected:
                Require(decision.FinalNarrative is null && decision.FinalNarrativeSha256 is null,
                    "rejected decision contains a final narrative", errors);
                Require(IsSemanticSlug(decision.RejectReasonCode), "rejected decision reason is invalid", errors);
                break;
        }
        Throw(errors);
    }

    internal static void Validate(
        SituationReviewDecisionV1 decision,
        SituationReviewCandidateV1 candidate)
    {
        Validate(candidate);
        Validate(decision);
        var errors = new List<string>();
        Require(decision.SampleId == candidate.SampleId, "review decision sampleId mismatch", errors);
        Require(decision.CandidateSha256 == candidate.CandidateSha256,
            "review decision candidate hash mismatch", errors);
        if (decision.FinalNarrative is { } narrative)
        {
            try
            {
                SituationContractValidator.Validate(
                    narrative,
                    candidate.Input.AllowedEvidenceIds.ToHashSet(StringComparer.Ordinal));
                ValidateModelBoundary(candidate.Input, narrative);
            }
            catch (InvalidDataException exception)
            {
                errors.Add($"review final narrative is invalid: {exception.Message}");
            }
        }
        Throw(errors);
    }

    internal static void ValidateFrozenReviewLabel(
        SituationFrozenReviewLabelV1 label,
        SituationTrainingSplit split)
    {
        var errors = new List<string>();
        Require(label.SchemaVersion == SituationTrainingContractVersions.FrozenReviewLabel,
            "frozen review label schemaVersion mismatch", errors);
        Require(IsPrefixedSha256(label.SampleId, "sample-"), "frozen label sampleId is invalid", errors);
        Require(label.LabelSource is SituationTrainingLabelSource.HumanApproved or
                SituationTrainingLabelSource.HumanModified,
            "frozen label source is not human-reviewed", errors);
        Require(!string.IsNullOrWhiteSpace(label.ReviewVersion), "reviewVersion is empty", errors);
        Require(label.AllowedUse is not null &&
                label.AllowedUse.SequenceEqual(label.AllowedUse.Order(StringComparer.Ordinal), StringComparer.Ordinal) &&
                label.AllowedUse.Distinct(StringComparer.Ordinal).Count() == label.AllowedUse.Count,
            "allowedUse must be unique and ordinal-sorted", errors);
        if (label.AllowedUse is null)
            Throw(errors);
        var allowedUse = label.AllowedUse ?? throw new InvalidDataException("allowedUse is missing");
        switch (split)
        {
            case SituationTrainingSplit.Train:
                Require(allowedUse.SequenceEqual(["sft"], StringComparer.Ordinal) &&
                        label.RecommendedSftRepeat == 5,
                    "train label use or repeat count is invalid", errors);
                break;
            case SituationTrainingSplit.Dev:
                Require(allowedUse.SequenceEqual(
                            ["checkpoint-selection", "evaluation", "prompt-selection"], StringComparer.Ordinal) &&
                        label.RecommendedSftRepeat is null,
                    "dev label use must exclude SFT", errors);
                break;
            case SituationTrainingSplit.Test:
                Require(allowedUse.SequenceEqual(["final-evaluation"], StringComparer.Ordinal) &&
                        label.RecommendedSftRepeat is null,
                    "test label use must be final-evaluation only", errors);
                break;
        }
        Throw(errors);
    }

    internal static void ValidateFrozenReviewLabel(
        SituationFrozenReviewLabelV1 label,
        SituationTrainingSplit split,
        SituationReviewCandidateV1 candidate)
    {
        Validate(candidate);
        ValidateFrozenReviewLabel(label, split);
        var errors = new List<string>();
        Require(label.SampleId == candidate.SampleId, "frozen label sampleId mismatch", errors);
        try
        {
            SituationContractValidator.Validate(
                label.Narrative,
                candidate.Input.AllowedEvidenceIds.ToHashSet(StringComparer.Ordinal));
            ValidateModelBoundary(candidate.Input, label.Narrative);
        }
        catch (InvalidDataException exception)
        {
            errors.Add($"frozen review narrative is invalid: {exception.Message}");
        }
        Throw(errors);
    }

    internal static void RejectDuplicateProperties(string json, string description)
    {
        using var document = JsonDocument.Parse(json);
        Visit(document.RootElement, "$" );

        void Visit(JsonElement element, string path)
        {
            if (element.ValueKind == JsonValueKind.Object)
            {
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var property in element.EnumerateObject())
                {
                    if (!names.Add(property.Name))
                        throw new InvalidDataException($"{description} contains duplicate property {path}.{property.Name}.");
                    Visit(property.Value, $"{path}.{property.Name}");
                }
            }
            else if (element.ValueKind == JsonValueKind.Array)
            {
                var index = 0;
                foreach (var item in element.EnumerateArray())
                    Visit(item, $"{path}[{index++}]");
            }
        }
    }

    internal static void RequireCompleteShape<T>(string json, string description)
    {
        using var document = JsonDocument.Parse(json);
        var nullability = new NullabilityInfoContext();
        Visit(document.RootElement, typeof(T), "$", allowsNull: false);

        void Visit(JsonElement element, Type declaredType, string path, bool allowsNull)
        {
            var type = Nullable.GetUnderlyingType(declaredType) ?? declaredType;
            if (element.ValueKind == JsonValueKind.Null)
            {
                if (!allowsNull && Nullable.GetUnderlyingType(declaredType) is null)
                    throw new InvalidDataException($"{description} field {path} must not be null.");
                return;
            }
            if (IsJsonScalar(type))
                return;

            if (TryGetDictionaryValueType(type, out var valueType))
            {
                if (element.ValueKind != JsonValueKind.Object)
                    throw new InvalidDataException($"{description} field {path} must be an object.");
                foreach (var property in element.EnumerateObject())
                    Visit(property.Value, valueType, $"{path}.{property.Name}",
                        Nullable.GetUnderlyingType(valueType) is not null);
                return;
            }

            if (TryGetEnumerableElementType(type, out var elementType))
            {
                if (element.ValueKind != JsonValueKind.Array)
                    throw new InvalidDataException($"{description} field {path} must be an array.");
                var index = 0;
                foreach (var item in element.EnumerateArray())
                    Visit(item, elementType, $"{path}[{index++}]",
                        Nullable.GetUnderlyingType(elementType) is not null);
                return;
            }

            if (element.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException($"{description} field {path} must be an object.");
            foreach (var property in type.GetProperties(BindingFlags.Instance | BindingFlags.Public)
                         .Where(property => property.GetMethod is not null &&
                                            property.GetCustomAttribute<JsonIgnoreAttribute>() is null))
            {
                var name = property.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name ??
                           JsonNamingPolicy.CamelCase.ConvertName(property.Name);
                if (!element.TryGetProperty(name, out var child))
                    throw new InvalidDataException($"{description} is missing required field {path}.{name}.");
                var propertyNullability = nullability.Create(property);
                Visit(child, property.PropertyType, $"{path}.{name}",
                    propertyNullability.ReadState == NullabilityState.Nullable ||
                    Nullable.GetUnderlyingType(property.PropertyType) is not null);
            }
        }
    }

    private static bool IsJsonScalar(Type type) =>
        type.IsPrimitive || type.IsEnum || type == typeof(string) || type == typeof(decimal) ||
        type == typeof(DateTime) || type == typeof(DateTimeOffset) || type == typeof(Guid);

    private static bool TryGetDictionaryValueType(Type type, out Type valueType)
    {
        var dictionary = type.IsGenericType &&
                         type.GetGenericTypeDefinition() == typeof(IReadOnlyDictionary<,>)
            ? type
            : type.GetInterfaces().FirstOrDefault(candidate => candidate.IsGenericType &&
                candidate.GetGenericTypeDefinition() == typeof(IReadOnlyDictionary<,>));
        valueType = dictionary?.GetGenericArguments()[1] ?? typeof(object);
        return dictionary is not null;
    }

    private static bool TryGetEnumerableElementType(Type type, out Type elementType)
    {
        if (type.IsArray)
        {
            elementType = type.GetElementType()!;
            return true;
        }
        var enumerable = type.IsGenericType &&
                         type.GetGenericTypeDefinition() == typeof(IEnumerable<>)
            ? type
            : type.GetInterfaces().FirstOrDefault(candidate => candidate.IsGenericType &&
                candidate.GetGenericTypeDefinition() == typeof(IEnumerable<>));
        elementType = enumerable?.GetGenericArguments()[0] ?? typeof(object);
        return enumerable is not null;
    }

    private static void ValidateIdentityVersions(SituationTrainingIdentityVersions versions)
    {
        if (versions is null ||
            versions.DataVersion != SituationTrainingContractVersions.Data ||
            !IsSha256(versions.SplitSha256) ||
            versions.SceneSchemaVersion != SituationContractVersions.Scene ||
            versions.SceneBuilderVersion != SituationSceneBuilder.BuilderVersion ||
            versions.GeometryVersion != SituationSceneBuilder.GeometryVersion ||
            versions.FactsSchemaVersion != SituationContractVersions.Facts ||
            versions.AnalysisRuleVersion != SituationAnalysisRuleLoader.FrozenAnalysisRuleVersion ||
            versions.NarrativeSchemaVersion != SituationContractVersions.Narrative ||
            versions.SemanticEligibilityVersion != WinFeatureSampleBuilder.SemanticVersion ||
            versions.SelectionVersion != SituationTrainingContractVersions.Selection ||
            versions.InputRepresentationVersion != SituationTrainingContractVersions.InputRepresentation)
            throw new InvalidDataException("Training identity version set is invalid or incomplete.");
    }

    private static void ValidateProjectedScene(SituationModelInputV1 scene, ICollection<string> errors)
    {
        Require(scene.SchemaVersion == SituationContractVersions.Scene, "model scene schemaVersion mismatch", errors);
        Require(scene.SceneBuilderVersion == SituationSceneBuilder.BuilderVersion,
            "model scene builder version mismatch", errors);
        Require(scene.GeometryVersion == SituationSceneBuilder.GeometryVersion,
            "model scene geometry version mismatch", errors);
        Require(scene.Map == "de_mirage", "model scene map mismatch", errors);
        Require(scene.Round.Ref is null, "model scene exposes the semantic round ID", errors);
        try
        {
            SituationContractValidator.Validate(RestoreValidationScene(scene));
        }
        catch (InvalidDataException exception)
        {
            errors.Add($"model scene is invalid: {exception.Message}");
        }
    }

    private static MinimapSceneV1 RestoreValidationScene(SituationModelInputV1 scene) => new(
        scene.SchemaVersion,
        scene.SceneBuilderVersion,
        scene.GeometryVersion,
        "structured",
        scene.Map,
        "model-input-validation",
        0,
        scene.Tick,
        scene.Tick,
        scene.TickRate,
        scene.Round,
        scene.Players,
        scene.Teams,
        scene.Bomb,
        scene.Utilities,
        scene.Effects,
        scene.Geometry,
        scene.DataQuality);

    internal static void ValidateModelBoundary(SituationTrainingInputV1 input, SituationNarrativeV1 output)
    {
        using var document = JsonSerializer.SerializeToDocument(new { input, output }, JsonOptions);
        Visit(document.RootElement, "$" );

        static void Visit(JsonElement element, string path)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.Object:
                    foreach (var property in element.EnumerateObject())
                    {
                        if (ForbiddenPropertyNames.Contains(property.Name))
                            throw new InvalidDataException($"Model boundary contains forbidden property at {path}.");
                        Visit(property.Value, $"{path}.{property.Name}");
                    }
                    break;
                case JsonValueKind.Array:
                    var index = 0;
                    foreach (var item in element.EnumerateArray())
                        Visit(item, $"{path}[{index++}]");
                    break;
                case JsonValueKind.String:
                    var value = element.GetString() ?? string.Empty;
                    if (ForbiddenStringTerms.Any(term => value.Contains(term, StringComparison.OrdinalIgnoreCase)) ||
                        Regex.IsMatch(value, "(?<![0-9])[0-9]{17}(?![0-9])", RegexOptions.CultureInvariant) ||
                        Regex.IsMatch(value, "(?:^|[^A-Za-z])[A-Za-z]:[\\\\/]", RegexOptions.CultureInvariant) ||
                        value.StartsWith("/home/", StringComparison.OrdinalIgnoreCase) ||
                        value.StartsWith("/Users/", StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException($"Model boundary contains forbidden identity, path, future, result, probability, or instruction text at {path}.");
                    break;
            }
        }
    }

    private static void ValidateSortedSemanticSet(
        IReadOnlyList<string>? values,
        string label,
        bool requireNonEmpty,
        ICollection<string> errors)
    {
        if (values is null)
        {
            errors.Add($"{label} is missing");
            return;
        }
        Require(!requireNonEmpty || values.Count > 0, $"{label} is empty", errors);
        var allowsUppercase = label == "selectionTags";
        Require(values.All(value => allowsUppercase ? IsSelectionTag(value) : IsSemanticSlug(value)),
            $"{label} contains an invalid value", errors);
        Require(values.Distinct(StringComparer.Ordinal).Count() == values.Count,
            $"{label} contains duplicates", errors);
        Require(values.SequenceEqual(values.Order(StringComparer.Ordinal), StringComparer.Ordinal),
            $"{label} is not ordinal-sorted", errors);
    }

    private static void ValidateSortedJsonPointers(
        IReadOnlyList<string>? values,
        string label,
        ICollection<string> errors)
    {
        if (values is null)
        {
            errors.Add($"{label} is missing");
            return;
        }
        Require(values.All(value => value.StartsWith("/", StringComparison.Ordinal) &&
                                    !Regex.IsMatch(value, "~(?:[^01]|$)", RegexOptions.CultureInvariant)),
            $"{label} contains an invalid JSON pointer", errors);
        Require(values.Distinct(StringComparer.Ordinal).Count() == values.Count,
            $"{label} contains duplicates", errors);
        Require(values.SequenceEqual(values.Order(StringComparer.Ordinal), StringComparer.Ordinal),
            $"{label} is not ordinal-sorted", errors);
    }

    private static bool IsSha256(string? value) => SituationArtifactIO.IsSha256(value, lowercaseOnly: true);

    private static bool IsPrefixedSha256(string? value, string prefix) =>
        value is not null && value.StartsWith(prefix, StringComparison.Ordinal) && IsSha256(value[prefix.Length..]);

    private static bool IsSemanticSlug(string? value) => value is not null &&
        Regex.IsMatch(value, "^[a-z0-9]+(?:-[a-z0-9]+)*$", RegexOptions.CultureInvariant);

    private static bool IsSelectionTag(string? value) => value is not null &&
        Regex.IsMatch(value, "^[A-Za-z0-9]+(?:-[A-Za-z0-9]+)*$", RegexOptions.CultureInvariant);

    private static bool IsSemanticVersion(string? value) => value is not null &&
        Regex.IsMatch(value, "^[a-z][a-z0-9-]*(?:\\.[a-z0-9][a-z0-9-]*)*$", RegexOptions.CultureInvariant);

    private static void Require(bool condition, string error, ICollection<string> errors)
    {
        if (!condition)
            errors.Add(error);
    }

    private static void Throw(IReadOnlyCollection<string> errors)
    {
        if (errors.Count > 0)
            throw new InvalidDataException(string.Join(Environment.NewLine, errors));
    }
}

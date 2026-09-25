using CsDemoMap.Api.Models;

namespace CsDemoMap.Api.Services;

internal sealed record SituationTrainingDatasetStatisticsSnapshot(
    SituationLabelStatsV1 LabelStats,
    IReadOnlyDictionary<string, SituationDatasetCountsV1> Counts);

/// <summary>
/// Deterministic, mergeable statistics for stage-four training data. Instances are
/// intended to be populated per match and merged after that match is committed.
/// No wall-clock values are accepted by this type.
/// </summary>
internal sealed class SituationTrainingDatasetStatistics
{
    private static readonly SituationTrainingSplit[] SplitOrder =
        [SituationTrainingSplit.Train, SituationTrainingSplit.Dev, SituationTrainingSplit.Test];

    private readonly object gate = new();
    private readonly string splitSha256;
    private readonly string inputRepresentationVersion;
    private readonly string inputRepresentationConfigSha256;
    private readonly Dictionary<SituationTrainingSplit, SplitAccumulator> splits = [];
    private readonly HashSet<SelectionKey> selections = [];

    internal SituationTrainingDatasetStatistics(
        string splitSha256,
        string inputRepresentationVersion,
        string inputRepresentationConfigSha256)
    {
        if (!SituationArtifactIO.IsSha256(splitSha256, lowercaseOnly: true))
            throw new ArgumentException("Split SHA-256 is invalid.", nameof(splitSha256));
        if (string.IsNullOrWhiteSpace(inputRepresentationVersion))
            throw new ArgumentException("Input representation version is empty.", nameof(inputRepresentationVersion));
        if (!SituationArtifactIO.IsSha256(inputRepresentationConfigSha256, lowercaseOnly: true))
            throw new ArgumentException("Input representation config SHA-256 is invalid.",
                nameof(inputRepresentationConfigSha256));
        this.splitSha256 = splitSha256;
        this.inputRepresentationVersion = inputRepresentationVersion;
        this.inputRepresentationConfigSha256 = inputRepresentationConfigSha256;
        foreach (var split in SplitOrder)
            splits.Add(split, new());
    }

    internal void RegisterMatch(SituationTrainingSplit split, string matchRef)
    {
        ValidateMatchRef(matchRef);
        lock (gate)
            splits[split].Matches.Add(matchRef);
    }

    internal void AddRecord(SituationTrainingRecordV1 record)
    {
        ArgumentNullException.ThrowIfNull(record);
        SituationTrainingContractJson.Validate(record);
        lock (gate)
            splits[record.Metadata.Split].Add(record);
    }

    internal void AddSelection(
        SituationTrainingSplit split,
        string matchRef,
        SituationTrainingRoundSelectionV1 selection)
    {
        ValidateMatchRef(matchRef);
        ArgumentNullException.ThrowIfNull(selection);
        if (string.IsNullOrWhiteSpace(selection.RoundId))
            throw new InvalidDataException("Selection round ID is empty.");
        var key = new SelectionKey(split, matchRef, selection.RoundId);
        lock (gate)
        {
            if (!selections.Add(key))
                throw new InvalidDataException("Selection statistics were added more than once for a round.");
            var target = splits[split];
            target.Matches.Add(matchRef);
            target.Add(selection);
        }
    }

    internal void Merge(SituationTrainingDatasetStatistics other)
    {
        ArgumentNullException.ThrowIfNull(other);
        if (ReferenceEquals(this, other))
            throw new ArgumentException("Statistics cannot be merged with themselves.", nameof(other));
        var state = other.CopyState();
        if (state.SplitSha256 != splitSha256 ||
            state.InputRepresentationVersion != inputRepresentationVersion ||
            state.InputRepresentationConfigSha256 != inputRepresentationConfigSha256)
            throw new InvalidDataException("Statistics identity differs across merge inputs.");

        lock (gate)
        {
            foreach (var split in SplitOrder)
            {
                if (splits[split].Matches.Overlaps(state.Splits[split].Matches))
                    throw new InvalidDataException("Statistics merge contains the same match more than once.");
            }
            if (selections.Overlaps(state.Selections))
                throw new InvalidDataException("Statistics merge contains the same selection more than once.");
            foreach (var split in SplitOrder)
                splits[split].Merge(state.Splits[split]);
            selections.UnionWith(state.Selections);
        }
    }

    internal SituationTrainingDatasetStatisticsSnapshot CreateSnapshot()
    {
        var state = CopyState();
        var sections = new List<SituationLabelStatSectionV1>();
        var counts = new Dictionary<string, SituationDatasetCountsV1>(StringComparer.Ordinal);
        foreach (var split in SplitOrder)
        {
            var name = SplitName(split);
            var value = state.Splits[split];
            value.ValidateRounds();
            counts.Add(name, new(value.Matches.Count, value.Rounds.Count, value.SampleCount));
            AddSplitSections(sections, name, value);
        }
        AddCombinedHashSection(sections, "scene", state.Splits.Values.Select(item => item.SceneHashes));
        AddCombinedHashSection(sections, "model-input", state.Splits.Values.Select(item => item.ModelInputHashes));
        AddCombinedHashSection(sections, "facts", state.Splits.Values.Select(item => item.FactsHashes));
        AddCombinedHashSection(sections, "prelabel", state.Splits.Values.Select(item => item.PrelabelHashes));

        var labelStats = new SituationLabelStatsV1(
            SituationTrainingContractVersions.LabelStats,
            SituationTrainingContractVersions.DatasetManifest,
            splitSha256,
            inputRepresentationVersion,
            inputRepresentationConfigSha256,
            false,
            sections.OrderBy(section => section.Name, StringComparer.Ordinal).ToArray());
        return new(labelStats, counts);
    }

    internal static bool IsRecordDerivedSection(string name) =>
        name.StartsWith("dataset.split.", StringComparison.Ordinal) ||
        name.StartsWith("facts.", StringComparison.Ordinal) ||
        name.StartsWith("hashes.", StringComparison.Ordinal) ||
        name.StartsWith("label.", StringComparison.Ordinal) ||
        name.StartsWith("label-source.", StringComparison.Ordinal) ||
        name.StartsWith("phase.", StringComparison.Ordinal) ||
        name.StartsWith("review-status.", StringComparison.Ordinal) ||
        name.StartsWith("samples-per-round.", StringComparison.Ordinal) ||
        name.StartsWith("selection-tags.", StringComparison.Ordinal) ||
        name.StartsWith("weight-error.", StringComparison.Ordinal);

    private State CopyState()
    {
        lock (gate)
        {
            return new(
                splitSha256,
                inputRepresentationVersion,
                inputRepresentationConfigSha256,
                splits.ToDictionary(item => item.Key, item => item.Value.Copy()),
                new HashSet<SelectionKey>(selections));
        }
    }

    private static void AddSplitSections(
        ICollection<SituationLabelStatSectionV1> sections,
        string split,
        SplitAccumulator value)
    {
        sections.Add(Section($"dataset.split.{split}", new Dictionary<string, double>
        {
            ["matches"] = value.Matches.Count,
            ["rounds"] = value.Rounds.Count,
            ["samples"] = value.SampleCount
        }));
        sections.Add(Section($"samples-per-round.{split}", value.RoundSizeHistogram()));
        sections.Add(Section($"weight-error.{split}", new Dictionary<string, double>
        {
            ["max-record-rational-absolute"] = value.MaxRecordWeightError,
            ["max-round-sum-absolute"] = value.MaxRoundWeightError(),
            ["invalid-rounds"] = 0
        }));
        sections.Add(Section($"phase.{split}", value.Phases));
        sections.Add(Section($"selection-tags.{split}", value.SelectionTags));
        sections.Add(Section($"facts.bomb-state.{split}", value.BombStates));
        sections.Add(Section($"facts.formation.t.{split}", value.FormationT));
        sections.Add(Section($"facts.formation.ct.{split}", value.FormationCt));
        sections.Add(Section($"facts.contact-risk.{split}", value.ContactRisk));
        sections.Add(Section($"facts.isolated-side.{split}", value.IsolatedSide));
        sections.Add(Section($"facts.spatial-advantage.{split}", value.SpatialAdvantage));
        sections.Add(Section($"facts.confidence.{split}", value.Confidence));
        sections.Add(Section($"facts.quality-codes.{split}", value.QualityCodes));
        sections.Add(Section($"label.highlight-count.{split}", value.HighlightCounts));
        sections.Add(Section($"label.uncertainty-count.{split}", value.UncertaintyCounts));
        sections.Add(Section($"label-source.{split}", value.LabelSources));
        sections.Add(Section($"review-status.{split}", value.ReviewStatuses));
        sections.Add(Section($"selector.candidates.{split}", value.SelectorCandidates));
        sections.Add(Section($"selector.selected.{split}", value.SelectorSelected));
        sections.Add(Section($"selector.merged.{split}", value.SelectorMerged));
        sections.Add(Section($"selector.removed-by-limit.{split}", value.SelectorRemovedByLimit));
        sections.Add(Section($"selector.missing-anchors.{split}", value.SelectorMissingAnchors));
        sections.Add(Section($"selector.shortages.{split}", value.SelectorShortages));
        sections.Add(Section($"eligibility-rejected.{split}", value.EligibilityRejected));
        sections.Add(Section($"hashes.scene.{split}", value.SceneHashes.Metrics()));
        sections.Add(Section($"hashes.model-input.{split}", value.ModelInputHashes.Metrics()));
        sections.Add(Section($"hashes.facts.{split}", value.FactsHashes.Metrics()));
        sections.Add(Section($"hashes.prelabel.{split}", value.PrelabelHashes.Metrics()));
    }

    private static SituationLabelStatSectionV1 Section(
        string name,
        IReadOnlyDictionary<string, long> values) => Section(
            name,
            values.ToDictionary(item => item.Key, item => (double)item.Value, StringComparer.Ordinal));

    private static SituationLabelStatSectionV1 Section(
        string name,
        IReadOnlyDictionary<string, double> values) => new(
            name,
            values.OrderBy(item => item.Key, StringComparer.Ordinal)
                .Select(item => new SituationLabelStatMetricV1(item.Key, item.Value))
                .ToArray());

    private static void AddCombinedHashSection(
        ICollection<SituationLabelStatSectionV1> sections,
        string name,
        IEnumerable<HashAccumulator> sources)
    {
        var combined = new HashAccumulator();
        foreach (var source in sources)
            combined.Merge(source);
        sections.Add(Section($"hashes.{name}.all", combined.Metrics()));
    }

    private static string SplitName(SituationTrainingSplit split) => split switch
    {
        SituationTrainingSplit.Train => "train",
        SituationTrainingSplit.Dev => "dev",
        SituationTrainingSplit.Test => "test",
        _ => throw new InvalidDataException("Training split is invalid.")
    };

    private static string EnumName<T>(T value) where T : struct, Enum => (object)value switch
    {
        SituationTrainingPhase.Live => "live",
        SituationTrainingPhase.PostPlant => "post-plant",
        SituationIsolatedSide.T => "T",
        SituationIsolatedSide.CT => "CT",
        SituationSpatialAdvantage.T => "T",
        SituationSpatialAdvantage.CT => "CT",
        SituationTrainingLabelSource.TemplatePrelabel => "template-prelabel",
        SituationTrainingLabelSource.HumanApproved => "human-approved",
        SituationTrainingLabelSource.HumanModified => "human-modified",
        _ => value.ToString().ToLowerInvariant()
    };

    private static void ValidateMatchRef(string matchRef)
    {
        if (matchRef is not { Length: 70 } || !matchRef.StartsWith("match-", StringComparison.Ordinal) ||
            !SituationArtifactIO.IsSha256(matchRef[6..], lowercaseOnly: true))
            throw new InvalidDataException("Match reference is invalid.");
    }

    private sealed record State(
        string SplitSha256,
        string InputRepresentationVersion,
        string InputRepresentationConfigSha256,
        Dictionary<SituationTrainingSplit, SplitAccumulator> Splits,
        HashSet<SelectionKey> Selections);

    private readonly record struct SelectionKey(
        SituationTrainingSplit Split,
        string MatchRef,
        string RoundId);

    private readonly record struct RoundKey(string MatchRef, string RoundRef);

    private sealed class SplitAccumulator
    {
        internal HashSet<string> Matches { get; } = new(StringComparer.Ordinal);
        internal Dictionary<RoundKey, RoundAccumulator> Rounds { get; } = [];
        internal int SampleCount { get; private set; }
        internal double MaxRecordWeightError { get; private set; }
        internal Dictionary<string, long> Phases { get; } = new(StringComparer.Ordinal);
        internal Dictionary<string, long> SelectionTags { get; } = new(StringComparer.Ordinal);
        internal Dictionary<string, long> BombStates { get; } = new(StringComparer.Ordinal);
        internal Dictionary<string, long> FormationT { get; } = new(StringComparer.Ordinal);
        internal Dictionary<string, long> FormationCt { get; } = new(StringComparer.Ordinal);
        internal Dictionary<string, long> ContactRisk { get; } = new(StringComparer.Ordinal);
        internal Dictionary<string, long> IsolatedSide { get; } = new(StringComparer.Ordinal);
        internal Dictionary<string, long> SpatialAdvantage { get; } = new(StringComparer.Ordinal);
        internal Dictionary<string, long> Confidence { get; } = new(StringComparer.Ordinal);
        internal Dictionary<string, long> QualityCodes { get; } = new(StringComparer.Ordinal);
        internal Dictionary<string, long> HighlightCounts { get; } = new(StringComparer.Ordinal);
        internal Dictionary<string, long> UncertaintyCounts { get; } = new(StringComparer.Ordinal);
        internal Dictionary<string, long> LabelSources { get; } = new(StringComparer.Ordinal);
        internal Dictionary<string, long> ReviewStatuses { get; } = new(StringComparer.Ordinal);
        internal Dictionary<string, long> SelectorCandidates { get; } = new(StringComparer.Ordinal);
        internal Dictionary<string, long> SelectorSelected { get; } = new(StringComparer.Ordinal);
        internal Dictionary<string, long> SelectorMerged { get; } = new(StringComparer.Ordinal);
        internal Dictionary<string, long> SelectorRemovedByLimit { get; } = new(StringComparer.Ordinal);
        internal Dictionary<string, long> SelectorMissingAnchors { get; } = new(StringComparer.Ordinal);
        internal Dictionary<string, long> SelectorShortages { get; } = new(StringComparer.Ordinal);
        internal Dictionary<string, long> EligibilityRejected { get; } = new(StringComparer.Ordinal);
        internal HashAccumulator SceneHashes { get; } = new();
        internal HashAccumulator ModelInputHashes { get; } = new();
        internal HashAccumulator FactsHashes { get; } = new();
        internal HashAccumulator PrelabelHashes { get; } = new();

        internal void Add(SituationTrainingRecordV1 record)
        {
            Matches.Add(record.Metadata.MatchRef);
            var key = new RoundKey(record.Metadata.MatchRef, record.Metadata.RoundRef);
            if (!Rounds.TryGetValue(key, out var round))
            {
                round = new();
                Rounds.Add(key, round);
            }
            round.Add(record);
            SampleCount = checked(SampleCount + 1);
            MaxRecordWeightError = Math.Max(MaxRecordWeightError,
                Math.Abs(record.Metadata.SampleWeight - 1d / record.Metadata.WeightDenominator));
            Increment(Phases, EnumName(record.Metadata.Phase));
            AddAll(SelectionTags, record.Metadata.SelectionTags);
            Increment(BombStates, EnumName(record.Input.Facts.Bomb.State));
            Increment(FormationT, EnumName(record.Input.Facts.Formation.T));
            Increment(FormationCt, EnumName(record.Input.Facts.Formation.CT));
            Increment(ContactRisk, EnumName(record.Input.Facts.ContactRisk));
            Increment(IsolatedSide, EnumName(record.Input.Facts.IsolatedSide));
            Increment(SpatialAdvantage, EnumName(record.Input.Facts.SpatialAdvantage));
            Increment(Confidence, EnumName(record.Input.Facts.Confidence));
            AddAll(QualityCodes, record.Input.Facts.DataQuality.Select(item => item.Code));
            Increment(HighlightCounts, record.Output.Highlights.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Increment(UncertaintyCounts,
                record.Output.Uncertainties.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Increment(LabelSources, EnumName(record.LabelSource));
            Increment(ReviewStatuses, EnumName(record.ReviewStatus));
            SceneHashes.Add(record.Metadata.SourceSceneSha256);
            ModelInputHashes.Add(record.Metadata.ModelInputSha256);
            FactsHashes.Add(record.Metadata.FactsSha256);
            PrelabelHashes.Add(record.Metadata.PrelabelSha256);
        }

        internal void Add(SituationTrainingRoundSelectionV1 selection)
        {
            foreach (var item in selection.Categories)
            {
                if (item.Value.CandidateCount < 0 || item.Value.SelectedCount < 0 ||
                    item.Value.MergedCount < 0 || item.Value.RemovedByLimitCount < 0 ||
                    item.Value.MissingAnchorCount < 0)
                    throw new InvalidDataException("Selection statistics contain a negative count.");
                Increment(SelectorCandidates, item.Key, item.Value.CandidateCount);
                Increment(SelectorSelected, item.Key, item.Value.SelectedCount);
                Increment(SelectorMerged, item.Key, item.Value.MergedCount);
                Increment(SelectorRemovedByLimit, item.Key, item.Value.RemovedByLimitCount);
                Increment(SelectorMissingAnchors, item.Key, item.Value.MissingAnchorCount);
            }
            AddCounts(EligibilityRejected, selection.EligibilityRejected);
            AddCounts(SelectorShortages, selection.Shortages);
        }

        internal SplitAccumulator Copy()
        {
            var copy = new SplitAccumulator
            {
                SampleCount = SampleCount,
                MaxRecordWeightError = MaxRecordWeightError
            };
            copy.Matches.UnionWith(Matches);
            foreach (var item in Rounds)
                copy.Rounds.Add(item.Key, item.Value.Copy());
            CopyCounts(Phases, copy.Phases);
            CopyCounts(SelectionTags, copy.SelectionTags);
            CopyCounts(BombStates, copy.BombStates);
            CopyCounts(FormationT, copy.FormationT);
            CopyCounts(FormationCt, copy.FormationCt);
            CopyCounts(ContactRisk, copy.ContactRisk);
            CopyCounts(IsolatedSide, copy.IsolatedSide);
            CopyCounts(SpatialAdvantage, copy.SpatialAdvantage);
            CopyCounts(Confidence, copy.Confidence);
            CopyCounts(QualityCodes, copy.QualityCodes);
            CopyCounts(HighlightCounts, copy.HighlightCounts);
            CopyCounts(UncertaintyCounts, copy.UncertaintyCounts);
            CopyCounts(LabelSources, copy.LabelSources);
            CopyCounts(ReviewStatuses, copy.ReviewStatuses);
            CopyCounts(SelectorCandidates, copy.SelectorCandidates);
            CopyCounts(SelectorSelected, copy.SelectorSelected);
            CopyCounts(SelectorMerged, copy.SelectorMerged);
            CopyCounts(SelectorRemovedByLimit, copy.SelectorRemovedByLimit);
            CopyCounts(SelectorMissingAnchors, copy.SelectorMissingAnchors);
            CopyCounts(SelectorShortages, copy.SelectorShortages);
            CopyCounts(EligibilityRejected, copy.EligibilityRejected);
            copy.SceneHashes.Merge(SceneHashes);
            copy.ModelInputHashes.Merge(ModelInputHashes);
            copy.FactsHashes.Merge(FactsHashes);
            copy.PrelabelHashes.Merge(PrelabelHashes);
            return copy;
        }

        internal void Merge(SplitAccumulator other)
        {
            Matches.UnionWith(other.Matches);
            foreach (var item in other.Rounds)
            {
                if (!Rounds.TryAdd(item.Key, item.Value.Copy()))
                    throw new InvalidDataException("Statistics merge contains the same round more than once.");
            }
            SampleCount = checked(SampleCount + other.SampleCount);
            MaxRecordWeightError = Math.Max(MaxRecordWeightError, other.MaxRecordWeightError);
            MergeCounts(Phases, other.Phases);
            MergeCounts(SelectionTags, other.SelectionTags);
            MergeCounts(BombStates, other.BombStates);
            MergeCounts(FormationT, other.FormationT);
            MergeCounts(FormationCt, other.FormationCt);
            MergeCounts(ContactRisk, other.ContactRisk);
            MergeCounts(IsolatedSide, other.IsolatedSide);
            MergeCounts(SpatialAdvantage, other.SpatialAdvantage);
            MergeCounts(Confidence, other.Confidence);
            MergeCounts(QualityCodes, other.QualityCodes);
            MergeCounts(HighlightCounts, other.HighlightCounts);
            MergeCounts(UncertaintyCounts, other.UncertaintyCounts);
            MergeCounts(LabelSources, other.LabelSources);
            MergeCounts(ReviewStatuses, other.ReviewStatuses);
            MergeCounts(SelectorCandidates, other.SelectorCandidates);
            MergeCounts(SelectorSelected, other.SelectorSelected);
            MergeCounts(SelectorMerged, other.SelectorMerged);
            MergeCounts(SelectorRemovedByLimit, other.SelectorRemovedByLimit);
            MergeCounts(SelectorMissingAnchors, other.SelectorMissingAnchors);
            MergeCounts(SelectorShortages, other.SelectorShortages);
            MergeCounts(EligibilityRejected, other.EligibilityRejected);
            SceneHashes.Merge(other.SceneHashes);
            ModelInputHashes.Merge(other.ModelInputHashes);
            FactsHashes.Merge(other.FactsHashes);
            PrelabelHashes.Merge(other.PrelabelHashes);
        }

        internal void ValidateRounds()
        {
            foreach (var round in Rounds.Values)
                round.Validate();
        }

        internal Dictionary<string, long> RoundSizeHistogram()
        {
            var result = new Dictionary<string, long>(StringComparer.Ordinal);
            foreach (var round in Rounds.Values)
                Increment(result, round.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));
            return result;
        }

        internal double MaxRoundWeightError() => Rounds.Count == 0
            ? 0
            : Rounds.Values.Max(round => Math.Abs((double)(round.WeightSum - 1m)));
    }

    private sealed class RoundAccumulator
    {
        private readonly HashSet<int> ticks = [];
        internal int Count => ticks.Count;
        internal decimal WeightSum { get; private set; }
        internal int? Denominator { get; private set; }

        internal void Add(SituationTrainingRecordV1 record)
        {
            if (!ticks.Add(record.Metadata.Tick))
                throw new InvalidDataException("A training round contains a duplicate tick.");
            if (ticks.Count > 16)
                throw new InvalidDataException("A training round contains more than 16 samples.");
            if (Denominator is { } denominator && denominator != record.Metadata.WeightDenominator)
                throw new InvalidDataException("A training round contains inconsistent weight denominators.");
            Denominator = record.Metadata.WeightDenominator;
            WeightSum += (decimal)record.Metadata.SampleWeight;
        }

        internal void Validate()
        {
            if (Count is < 1 or > 16 || Denominator != Count ||
                Math.Abs((double)(WeightSum - 1m)) > SituationTrainingContractJson.WeightTolerance)
                throw new InvalidDataException("A training round does not use exact 1/n sample weights.");
        }

        internal RoundAccumulator Copy()
        {
            var copy = new RoundAccumulator { WeightSum = WeightSum, Denominator = Denominator };
            copy.ticks.UnionWith(ticks);
            return copy;
        }
    }

    private sealed class HashAccumulator
    {
        private readonly HashSet<string> unique = new(StringComparer.Ordinal);
        private long total;

        internal void Add(string value)
        {
            total = checked(total + 1);
            unique.Add(value);
        }

        internal void Merge(HashAccumulator other)
        {
            total = checked(total + other.total);
            unique.UnionWith(other.unique);
        }

        internal IReadOnlyDictionary<string, double> Metrics() => new Dictionary<string, double>
        {
            ["duplicates"] = total - unique.Count,
            ["total"] = total,
            ["unique"] = unique.Count
        };
    }

    private static void AddAll(IDictionary<string, long> target, IEnumerable<string> values)
    {
        foreach (var value in values)
            Increment(target, value);
    }

    private static void AddCounts(IDictionary<string, long> target, IReadOnlyDictionary<string, int> source)
    {
        foreach (var item in source)
        {
            if (item.Value < 0)
                throw new InvalidDataException("Selection statistics contain a negative count.");
            Increment(target, item.Key, item.Value);
        }
    }

    private static void Increment(IDictionary<string, long> target, string key, long value = 1)
    {
        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidDataException("Statistics category is empty.");
        target[key] = checked((target.TryGetValue(key, out var current) ? current : 0) + value);
    }

    private static void CopyCounts(
        IReadOnlyDictionary<string, long> source,
        IDictionary<string, long> target)
    {
        foreach (var item in source)
            target.Add(item.Key, item.Value);
    }

    private static void MergeCounts(
        IDictionary<string, long> target,
        IReadOnlyDictionary<string, long> source)
    {
        foreach (var item in source)
            Increment(target, item.Key, item.Value);
    }
}

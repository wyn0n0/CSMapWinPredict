using System.Globalization;
using CsDemoMap.Api.Models;

namespace CsDemoMap.Api.Services;

internal sealed record SituationTrainingRoundFrame(
    SemanticFrame Semantic,
    DemoFrame Snapshot);

internal sealed record SituationTrainingCandidateObservation(
    SemanticFrame Semantic,
    DemoFrame Snapshot,
    SituationFactsV1 Facts);

internal sealed record SituationTrainingSelectedTick(
    int Tick,
    SituationTrainingPhase Phase,
    IReadOnlyList<string> SelectionTags,
    double SampleWeight,
    int WeightNumerator,
    int WeightDenominator);

internal sealed record SituationTrainingSelectionCategoryStats(
    int CandidateCount,
    int SelectedCount,
    int MergedCount,
    int RemovedByLimitCount,
    int MissingAnchorCount);

internal sealed record SituationTrainingRoundSelectionV1(
    string SchemaVersion,
    string SelectionConfigSha256,
    string RoundId,
    IReadOnlyList<SituationTrainingSelectedTick> Samples,
    IReadOnlyDictionary<string, SituationTrainingSelectionCategoryStats> Categories,
    IReadOnlyDictionary<string, int> EligibilityRejected,
    IReadOnlyDictionary<string, int> Shortages);

internal sealed record SituationTrainingRoundSelectionResult(
    SituationTrainingRoundSelectionV1 Selection,
    string CanonicalJson,
    string Sha256);

/// <summary>
/// Deterministic stage-four candidate detection and per-round selection. The selector
/// consumes only structured snapshots, semantic state, frozen Facts and event types.
/// </summary>
internal sealed class SituationTrainingCandidateSelector
{
    private readonly SituationTrainingSelectionLoadResult selectionLoad;
    private readonly SituationEligibleSceneBuilder eligibleSceneBuilder;
    private readonly SituationDeterministicAnalyzer analyzer;

    internal SituationTrainingCandidateSelector(SituationSceneService sceneService)
        : this(
            SituationTrainingSelectionLoader.LoadFrozen(),
            new SituationEligibleSceneBuilder(sceneService),
            SituationDeterministicAnalyzer.CreateFrozen())
    {
    }

    internal SituationTrainingCandidateSelector(
        SituationTrainingSelectionLoadResult selectionLoad,
        SituationEligibleSceneBuilder eligibleSceneBuilder,
        SituationDeterministicAnalyzer analyzer)
    {
        ArgumentNullException.ThrowIfNull(selectionLoad);
        ArgumentNullException.ThrowIfNull(eligibleSceneBuilder);
        ArgumentNullException.ThrowIfNull(analyzer);
        SituationTrainingSelectionLoader.Validate(selectionLoad.Config);
        this.selectionLoad = selectionLoad;
        this.eligibleSceneBuilder = eligibleSceneBuilder;
        this.analyzer = analyzer;
    }

    internal SituationTrainingRoundSelectionResult SelectRound(
        DemoTimeline timeline,
        string demoRef,
        RoundAttempt attempt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(timeline);
        ArgumentNullException.ThrowIfNull(attempt);
        if (timeline.Semantics is null)
            throw new InvalidOperationException("Semantic collection is required.");
        if (timeline.Metadata.TickRate <= 0)
            throw new InvalidDataException("Timeline tick rate must be positive.");

        var sourceByTick = timeline.Frames
            .GroupBy(frame => frame.Tick)
            .ToDictionary(
                group => group.Key,
                group => group.Count() == 1
                    ? group.Single()
                    : throw new InvalidDataException($"Timeline contains duplicate frame tick {group.Key}."));
        var roundSemanticFrames = timeline.Semantics.Frames
            .Where(frame => string.Equals(frame.RoundId, attempt.RoundId, StringComparison.Ordinal))
            .OrderBy(frame => frame.Tick)
            .ToArray();
        var liveTick = attempt.LiveTick ?? int.MaxValue;
        var endTick = attempt.EndTick ?? int.MinValue;
        var killEventTicks = timeline.Events
            .Where(item => item.Tick >= liveTick && item.Tick < endTick)
            .Where(item => string.Equals(item.Type, "kill", StringComparison.Ordinal))
            .Select(item => item.Tick)
            .Order()
            .ToArray();
        return SelectRoundCore(
            timeline,
            demoRef,
            attempt,
            roundSemanticFrames,
            tick => sourceByTick.TryGetValue(tick, out var snapshot) ? snapshot : null,
            killEventTicks,
            includePayloads: false,
            cancellationToken).SelectionResult;
    }

    /// <summary>
    /// Selects one completed round from a match-scoped index and returns reusable,
    /// already validated build products only for ticks that survived selection.
    /// </summary>
    internal SituationTrainingRoundSelectionWithPayloads SelectRound(
        SituationTrainingTimelineIndex index,
        string demoRef,
        string roundId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(index);
        ArgumentException.ThrowIfNullOrWhiteSpace(demoRef);
        ArgumentException.ThrowIfNullOrWhiteSpace(roundId);
        cancellationToken.ThrowIfCancellationRequested();
        var attempt = index.GetCompletedAttempt(roundId);
        return SelectRoundCore(
            index.Timeline,
            demoRef,
            attempt,
            index.GetSemanticFrames(roundId),
            tick => index.TryGetFrame(tick, out var snapshot) ? snapshot : null,
            index.GetEventTicks(roundId, "kill"),
            includePayloads: true,
            cancellationToken);
    }

    private SituationTrainingRoundSelectionWithPayloads SelectRoundCore(
        DemoTimeline timeline,
        string demoRef,
        RoundAttempt attempt,
        IReadOnlyList<SemanticFrame> roundSemanticFrames,
        Func<int, DemoFrame?> resolveSnapshot,
        IReadOnlyList<int> killEventTicks,
        bool includePayloads,
        CancellationToken cancellationToken)
    {
        var observations = new List<SituationTrainingCandidateObservation>();
        var roundFrames = new List<SituationTrainingRoundFrame>();
        var rejected = new Dictionary<string, int>(StringComparer.Ordinal);
        var buildProducts = includePayloads
            ? new Dictionary<int, CandidateBuildProduct>()
            : null;

        foreach (var semantic in roundSemanticFrames)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var snapshot = resolveSnapshot(semantic.Tick)
                ?? throw new InvalidDataException($"Timeline frame {semantic.Tick} is missing.");
            roundFrames.Add(new(semantic, snapshot));
            var eligibility = RoundSampleEligibility.Evaluate(
                timeline.Metadata.MapName, attempt, semantic, snapshot);
            if (!eligibility.Eligible)
            {
                var reason = eligibility.ReasonCode ?? "eligibility-rejected";
                rejected[reason] = rejected.GetValueOrDefault(reason) + 1;
                continue;
            }

            var windowIndex = semantic.Tick /
                checked(DemoImportService.WindowSeconds * timeline.Metadata.TickRate);
            SituationEligibleSceneBuildResult built;
            try
            {
                built = eligibleSceneBuilder.Build(
                    timeline,
                    demoRef,
                    windowIndex,
                    attempt,
                    semantic,
                    snapshot,
                    cancellationToken);
            }
            catch (InvalidDataException exception)
            {
                throw new InvalidDataException(
                    $"Training candidate scene failed for round {attempt.RoundId} at tick {semantic.Tick}.",
                    exception);
            }
            if (!built.Eligibility.Eligible || built.Scene is null)
                throw new InvalidOperationException("Eligible scene construction did not return a scene.");
            var analysis = analyzer.Analyze(built.Scene.Scene);
            observations.Add(new(semantic, snapshot, analysis.Facts.Facts));
            if (buildProducts is not null && !buildProducts.TryAdd(
                    semantic.Tick,
                    new CandidateBuildProduct(semantic, built.Scene, analysis)))
                throw new InvalidDataException(
                    $"Training candidate round {attempt.RoundId} contains duplicate tick {semantic.Tick}.");
        }

        var selection = SelectPrepared(
            timeline.Metadata.MapName,
            timeline.Metadata.TickRate,
            attempt,
            observations,
            roundFrames,
            killEventTicks,
            rejected);
        cancellationToken.ThrowIfCancellationRequested();
        if (buildProducts is null)
            return new(selection, Array.AsReadOnly(Array.Empty<SituationTrainingSelectionPayload>()));
        var payloads = selection.Selection.Samples
            .Select(sample => CreatePayload(attempt.RoundId, sample, buildProducts[sample.Tick]))
            .ToArray();
        return new(selection, Array.AsReadOnly(payloads));
    }

    private static SituationTrainingSelectionPayload CreatePayload(
        string roundId,
        SituationTrainingSelectedTick selected,
        CandidateBuildProduct product)
    {
        if (!string.Equals(product.Semantic.RoundId, roundId, StringComparison.Ordinal) ||
            product.Semantic.Tick != selected.Tick ||
            product.Scene.Scene.Tick != selected.Tick ||
            product.Scene.Sha256 != SituationCanonicalJson.Sha256(product.Scene.Scene) ||
            product.Analysis.Facts.Sha256 != SituationCanonicalJson.Sha256(product.Analysis.Facts.Facts) ||
            product.Analysis.Narrative.Sha256 !=
                SituationCanonicalJson.Sha256(product.Analysis.Narrative.Narrative))
            throw new InvalidDataException("Selected training payload failed its build-product hash boundary.");
        SituationContractValidator.Validate(
            product.Analysis.Facts.Facts,
            product.Scene.Scene,
            product.Analysis.Facts.Facts.AnalysisRuleVersion);
        SituationContractValidator.ValidateTemplate(
            product.Analysis.Narrative.Narrative,
            product.Analysis.Facts.Facts);
        return new(
            roundId,
            selected.Tick,
            product.Semantic.Phase,
            selected.Phase,
            Array.AsReadOnly(selected.SelectionTags.ToArray()),
            selected.SampleWeight,
            selected.WeightNumerator,
            selected.WeightDenominator,
            product.Scene.Scene,
            product.Scene.CanonicalJson,
            product.Scene.Sha256,
            product.Analysis.Facts.Facts,
            product.Analysis.Facts.CanonicalJson,
            product.Analysis.Facts.Sha256,
            product.Analysis.Narrative.Narrative,
            product.Analysis.Narrative.CanonicalJson,
            product.Analysis.Narrative.Sha256);
    }

    internal SituationTrainingRoundSelectionResult SelectPrepared(
        string mapName,
        int tickRate,
        RoundAttempt attempt,
        IReadOnlyList<SituationTrainingCandidateObservation> observations,
        IReadOnlyList<SituationTrainingRoundFrame> roundFrames,
        IReadOnlyList<int> killEventTicks,
        IReadOnlyDictionary<string, int>? eligibilityRejected = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mapName);
        ArgumentNullException.ThrowIfNull(attempt);
        ArgumentNullException.ThrowIfNull(observations);
        ArgumentNullException.ThrowIfNull(roundFrames);
        ArgumentNullException.ThrowIfNull(killEventTicks);
        if (tickRate <= 0)
            throw new ArgumentOutOfRangeException(nameof(tickRate));

        var eligible = NormalizeObservations(attempt, observations);
        var fullRound = NormalizeRoundFrames(attempt, roundFrames);
        var stats = AllCategoryNames().ToDictionary(
            category => category,
            _ => new MutableCategoryStats(),
            StringComparer.Ordinal);
        var shortages = new Dictionary<string, int>(StringComparer.Ordinal);
        var selected = new SortedDictionary<int, HashSet<string>>();
        var config = selectionLoad.Config;

        var liveCandidates = eligible
            .Where(item => item.Semantic.Phase == "live")
            .Select(item => item.Semantic.Tick)
            .ToArray();
        Register(stats, "live-start", liveCandidates.Take(1));

        var deploymentCandidates = DeploymentCandidates(
            mapName, tickRate, eligible, config.DeploymentComplete);
        Register(stats, "deployment-complete", deploymentCandidates);

        var tailThresholdCandidates = eligible
            .Where(item => item.Semantic.Clock.RoundRemainingSeconds is { } roundRemaining &&
                               roundRemaining <= config.RoundTail.MaximumRoundRemainingSeconds ||
                           item.Semantic.Clock.BombRemainingSeconds is { } bombRemaining &&
                               bombRemaining <= config.RoundTail.MaximumBombRemainingSeconds)
            .Select(item => item.Semantic.Tick)
            .ToArray();
        var tailCandidates = tailThresholdCandidates.Length > 0
            ? tailThresholdCandidates
            : eligible.TakeLast(1).Select(item => item.Semantic.Tick).ToArray();
        Register(stats, "round-tail", tailCandidates);

        var firstContact = FirstContactTransition(eligible);
        Register(stats, "first-contact", Optional(firstContact));
        var firstDamage = FirstDamage(eligible);
        Register(stats, "first-damage", Optional(firstDamage));
        var firstCasualty = FirstCasualty(eligible);
        Register(stats, "first-casualty", Optional(firstCasualty));

        var bombDropped = FirstBombTransition(fullRound,
            (previous, current) => current == "dropped" && previous != "dropped");
        var bombPickedUp = FirstBombTransition(fullRound,
            (previous, current) => previous == "dropped" && current == "carried");
        var bombPlanting = FirstBombTransition(fullRound,
            (previous, current) => current == "planting" && previous != "planting");
        var bombPlanted = fullRound.FirstOrDefault(item =>
            item.Snapshot.Bomb.State is "planted" or "defusing" ||
            item.Semantic.Phase == "post-plant")?.Semantic.Tick;
        var bombDefusing = FirstBombTransition(fullRound,
            (previous, current) => current == "defusing" && previous != "defusing");

        var mappedDropped = MapAnchor("bomb-dropped", bombDropped, eligible, tickRate, stats, shortages);
        var mappedPickedUp = MapAnchor("bomb-picked-up", bombPickedUp, eligible, tickRate, stats, shortages);
        var mappedPlanting = MapAnchor(
            "bomb-planting-or-planted", bombPlanting, eligible, tickRate, stats, shortages,
            recordAbsent: false);
        if (mappedPlanting is null)
            mappedPlanting = MapAnchor(
                "bomb-planting-or-planted", bombPlanted, eligible, tickRate, stats, shortages);
        var mappedDefusing = MapAnchor(
            "bomb-defusing", bombDefusing, eligible, tickRate, stats, shortages);

        var clutch2 = eligible.Where(item => IsClutch(item, config.Clutch2vN))
            .Select(item => item.Semantic.Tick).ToArray();
        var clutch1 = eligible.Where(item => IsClutch(item, config.Clutch1vN))
            .Select(item => item.Semantic.Tick).ToArray();
        Register(stats, "clutch-2vN", clutch2);
        Register(stats, "clutch-1vN", clutch1);

        var detected = new Dictionary<string, int?>(StringComparer.Ordinal)
        {
            ["live-start"] = liveCandidates.FirstOrDefaultValue(),
            ["deployment-complete"] = deploymentCandidates.FirstOrDefaultValue(),
            ["round-tail"] = tailCandidates.FirstOrDefaultValue(),
            ["first-contact"] = firstContact,
            ["first-damage"] = firstDamage,
            ["first-casualty"] = firstCasualty,
            ["bomb-dropped"] = mappedDropped,
            ["bomb-picked-up"] = mappedPickedUp,
            ["bomb-planting-or-planted"] = mappedPlanting,
            ["bomb-defusing"] = mappedDefusing,
            ["clutch-2vN"] = clutch2.FirstOrDefaultValue(),
            ["clutch-1vN"] = clutch1.FirstOrDefaultValue()
        };

        foreach (var category in config.CorePriority.Concat(config.EventPriority))
        {
            if (detected[category] is { } tick)
                AddTag(selected, stats, category, tick, config.MaxSamplesPerRound);
            else if (stats[category].MissingAnchorCount == 0)
                RecordMissing(stats, shortages, category,
                    category == "deployment-complete" ? "deployment-missing" : category + "-missing");
        }
        if (firstCasualty is null && killEventTicks.Any(tick =>
                tick >= (attempt.LiveTick ?? int.MaxValue) && tick < (attempt.EndTick ?? int.MinValue)))
            AddShortage(shortages, "first-casualty-state-missing");

        var c4Ticks = selected
            .Where(item => item.Value.Any(tag => tag.StartsWith("bomb-", StringComparison.Ordinal)))
            .Select(item => item.Key)
            .ToHashSet();
        var postPlantCandidates = eligible
            .Where(item => item.Semantic.Phase == "post-plant" && !c4Ticks.Contains(item.Semantic.Tick))
            .ToArray();
        Register(stats, "post-plant", postPlantCandidates.Select(item => item.Semantic.Tick));
        var formationCandidates = eligible
            .Where(item => item.Facts.Formation.T == SituationFormation.Split ||
                           item.Facts.Formation.CT == SituationFormation.Split)
            .Select(item => item.Semantic.Tick)
            .ToArray();
        Register(stats, "formation-split", formationCandidates);
        var isolatedCandidates = eligible
            .Where(item => item.Facts.IsolatedSide is SituationIsolatedSide.T or
                SituationIsolatedSide.CT or SituationIsolatedSide.Both)
            .Select(item => item.Semantic.Tick)
            .ToArray();
        Register(stats, "isolated", isolatedCandidates);
        var highContactCandidates = eligible
            .Where(item => item.Facts.ContactRisk == SituationContactRisk.High)
            .Select(item => item.Semantic.Tick)
            .ToArray();
        Register(stats, "high-contact-risk", highContactCandidates);

        foreach (var category in config.RarePriority)
        {
            int? tick = category switch
            {
                "post-plant" => PreferredPostPlant(
                    postPlantCandidates, config.PostPlantPreference),
                "formation-split" => PreferUnselected(formationCandidates, selected),
                "isolated" => PreferUnselected(isolatedCandidates, selected),
                "high-contact-risk" => PreferUnselected(highContactCandidates, selected),
                _ => throw new InvalidDataException($"Unknown rare selection category {category}.")
            };
            if (tick is { } value)
                AddTag(selected, stats, category, value, config.MaxSamplesPerRound);
            else
                RecordMissing(stats, shortages, category, category + "-missing");
        }

        var fillCategory = config.FillStrategy;
        var fillPool = eligible
            .Where(item => !selected.ContainsKey(item.Semantic.Tick))
            .Select(item => item.Semantic.Tick)
            .Distinct()
            .ToArray();
        Register(stats, fillCategory, fillPool);
        while (selected.Count < Math.Min(config.MaxSamplesPerRound, eligible.Count) &&
               fillPool.Any(tick => !selected.ContainsKey(tick)))
        {
            var next = fillPool
                .Where(tick => !selected.ContainsKey(tick))
                .Select(tick => new
                {
                    Tick = tick,
                    Distance = selected.Count == 0
                        ? long.MaxValue
                        : selected.Keys.Min(value => Math.Abs((long)tick - value)),
                    StableId = StableCandidateId(attempt.RoundId, tick)
                })
                .OrderByDescending(item => item.Distance)
                .ThenBy(item => item.Tick)
                .ThenBy(item => item.StableId, StringComparer.Ordinal)
                .First();
            AddTag(selected, stats, fillCategory, next.Tick, config.MaxSamplesPerRound);
        }
        stats[fillCategory].RemovedByLimitCount = fillPool.Count(tick => !selected.ContainsKey(tick));

        var byTick = eligible.ToDictionary(item => item.Semantic.Tick);
        var selectedCount = selected.Count;
        var samples = selected.Select(item => new SituationTrainingSelectedTick(
                item.Key,
                byTick[item.Key].Semantic.Phase == "post-plant"
                    ? SituationTrainingPhase.PostPlant
                    : SituationTrainingPhase.Live,
                item.Value.Order(StringComparer.Ordinal).ToArray(),
                1d / selectedCount,
                1,
                selectedCount))
            .ToArray();
        foreach (var category in stats.Keys)
            stats[category].SelectedCount = samples.Count(sample =>
                sample.SelectionTags.Contains(category, StringComparer.Ordinal));

        var categoryStats = stats
            .OrderBy(item => item.Key, StringComparer.Ordinal)
            .ToDictionary(
                item => item.Key,
                item => item.Value.ToImmutable(),
                StringComparer.Ordinal);
        var document = new SituationTrainingRoundSelectionV1(
            config.AlgorithmVersion,
            selectionLoad.Sha256,
            attempt.RoundId,
            samples,
            categoryStats,
            NormalizeCounts(eligibilityRejected),
            NormalizeCounts(shortages));
        var canonical = SituationCanonicalJson.Serialize(document);
        return new(document, canonical, SituationArtifactIO.Sha256(canonical));
    }

    private IEnumerable<string> AllCategoryNames() => selectionLoad.Config.CorePriority
        .Concat(selectionLoad.Config.EventPriority)
        .Concat(selectionLoad.Config.RarePriority)
        .Append(selectionLoad.Config.FillStrategy)
        .Distinct(StringComparer.Ordinal);

    private static IReadOnlyList<SituationTrainingCandidateObservation> NormalizeObservations(
        RoundAttempt attempt,
        IReadOnlyList<SituationTrainingCandidateObservation> values)
    {
        var normalized = values.OrderBy(item => item.Semantic.Tick).ToArray();
        if (normalized.Select(item => item.Semantic.Tick).Distinct().Count() != normalized.Length ||
            normalized.Any(item => item.Semantic.Tick != item.Snapshot.Tick ||
                !string.Equals(item.Semantic.RoundId, attempt.RoundId, StringComparison.Ordinal) ||
                item.Semantic.Phase is not ("live" or "post-plant")))
            throw new InvalidDataException("Prepared eligible observations are invalid or cross round boundaries.");
        return normalized;
    }

    private static IReadOnlyList<SituationTrainingRoundFrame> NormalizeRoundFrames(
        RoundAttempt attempt,
        IReadOnlyList<SituationTrainingRoundFrame> values)
    {
        var normalized = values
            .Where(item => string.Equals(item.Semantic.RoundId, attempt.RoundId, StringComparison.Ordinal))
            .Where(item => item.Semantic.Tick >= attempt.LiveTick && item.Semantic.Tick < attempt.EndTick)
            .OrderBy(item => item.Semantic.Tick)
            .ToArray();
        if (normalized.Select(item => item.Semantic.Tick).Distinct().Count() != normalized.Length ||
            normalized.Any(item => item.Semantic.Tick != item.Snapshot.Tick))
            throw new InvalidDataException("Prepared round frames contain duplicate or mismatched ticks.");
        return normalized;
    }

    private static int[] DeploymentCandidates(
        string mapName,
        int tickRate,
        IReadOnlyList<SituationTrainingCandidateObservation> eligible,
        SituationDeploymentSelectionRuleV1 rule)
    {
        var first = eligible.FirstOrDefault(item => item.Semantic.Phase == "live");
        var geometry = MapFeatureGeometries.Find(mapName);
        if (first is null || geometry is null)
            return [];
        var origins = first.Snapshot.Players
            .Where(IsAliveCompetitivePlayerWithPosition)
            .GroupBy(player => player.Id, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => geometry.Normalize(group.First().X, group.First().Y),
                StringComparer.Ordinal);
        int? stableSince = null;
        var result = new List<int>();
        var stableTicks = rule.StableSeconds * tickRate;
        foreach (var item in eligible.Where(item => item.Semantic.Phase == "live"))
        {
            var deployed = item.Semantic.Clock.LiveElapsedSeconds is { } elapsed &&
                           elapsed >= rule.MinimumLiveElapsedSeconds &&
                           item.Semantic.Roster.AlivePositionKnown == item.Semantic.Roster.AliveKnown &&
                           SideMoved("T") && SideMoved("CT");
            if (!deployed)
            {
                stableSince = null;
                continue;
            }
            stableSince ??= item.Semantic.Tick;
            if (item.Semantic.Tick - stableSince.Value >= stableTicks)
                result.Add(item.Semantic.Tick);

            bool SideMoved(string side)
            {
                var players = item.Snapshot.Players
                    .Where(player => player.Team == side && IsAliveCompetitivePlayerWithPosition(player))
                    .ToArray();
                if (players.Length == 0)
                    return false;
                var moved = players.Count(player =>
                {
                    if (!origins.TryGetValue(player.Id, out var origin))
                        return false;
                    var current = geometry.Normalize(player.X, player.Y);
                    var dx = current.X - origin.X;
                    var dy = current.Y - origin.Y;
                    return Math.Sqrt(dx * dx + dy * dy) >= rule.MinimumNormalizedMovement;
                });
                return moved / (double)players.Length >= rule.MinimumMovedPlayerRatio;
            }
        }
        return result.ToArray();
    }

    private static int? FirstContactTransition(
        IReadOnlyList<SituationTrainingCandidateObservation> eligible)
    {
        for (var index = 1; index < eligible.Count; index++)
        {
            if (eligible[index - 1].Facts.ContactRisk is SituationContactRisk.Low or SituationContactRisk.Unknown &&
                eligible[index].Facts.ContactRisk is SituationContactRisk.Medium or SituationContactRisk.High)
                return eligible[index].Semantic.Tick;
        }
        return null;
    }

    private static int? FirstDamage(IReadOnlyList<SituationTrainingCandidateObservation> eligible)
    {
        for (var index = 1; index < eligible.Count; index++)
        {
            var previous = AliveById(eligible[index - 1].Snapshot);
            var current = AliveById(eligible[index].Snapshot);
            if (current.Any(item => previous.TryGetValue(item.Key, out var health) && item.Value < health))
                return eligible[index].Semantic.Tick;
        }
        return null;
    }

    private static int? FirstCasualty(IReadOnlyList<SituationTrainingCandidateObservation> eligible)
    {
        for (var index = 1; index < eligible.Count; index++)
        {
            var previous = AliveCounts(eligible[index - 1].Snapshot);
            var current = AliveCounts(eligible[index].Snapshot);
            if (current.T < previous.T || current.CT < previous.CT)
                return eligible[index].Semantic.Tick;
        }
        return null;
    }

    private static int? FirstBombTransition(
        IReadOnlyList<SituationTrainingRoundFrame> frames,
        Func<string, string, bool> predicate)
    {
        for (var index = 0; index < frames.Count; index++)
        {
            var previous = index == 0 ? "unknown" : frames[index - 1].Snapshot.Bomb.State;
            var current = frames[index].Snapshot.Bomb.State;
            if (predicate(previous, current))
                return frames[index].Semantic.Tick;
        }
        return null;
    }

    private int? MapAnchor(
        string category,
        int? eventTick,
        IReadOnlyList<SituationTrainingCandidateObservation> eligible,
        int tickRate,
        IReadOnlyDictionary<string, MutableCategoryStats> stats,
        IDictionary<string, int> shortages,
        bool recordAbsent = true)
    {
        if (eventTick is null)
        {
            if (recordAbsent)
                RecordMissing(stats, shortages, category, category + "-missing");
            return null;
        }
        var mapped = eligible.FirstOrDefault(item =>
            item.Semantic.Tick >= eventTick.Value &&
            item.Semantic.Tick - eventTick.Value <=
                selectionLoad.Config.EventMappingToleranceSeconds * tickRate);
        if (mapped is null)
        {
            RecordMissing(stats, shortages, category, category + "-anchor-missing");
            return null;
        }
        Register(stats, category, [mapped.Semantic.Tick]);
        return mapped.Semantic.Tick;
    }

    private static int? PreferredPostPlant(
        IReadOnlyList<SituationTrainingCandidateObservation> candidates,
        string preference)
    {
        if (preference != "closest-known-bomb-countdown-midpoint")
            throw new InvalidDataException($"Unknown post-plant preference {preference}.");
        if (candidates.Count == 0)
            return null;
        var known = candidates
            .Where(item => item.Semantic.Clock.BombRemainingSeconds is not null)
            .ToArray();
        if (known.Length == 0)
            return candidates[0].Semantic.Tick;
        var midpoint = known.Max(item => item.Semantic.Clock.BombRemainingSeconds!.Value) / 2d;
        return known
            .OrderBy(item => Math.Abs(item.Semantic.Clock.BombRemainingSeconds!.Value - midpoint))
            .ThenBy(item => item.Semantic.Tick)
            .First().Semantic.Tick;
    }

    private static int? PreferUnselected(
        IReadOnlyList<int> candidates,
        IReadOnlyDictionary<int, HashSet<string>> selected) =>
        candidates.Cast<int?>().FirstOrDefault(tick => !selected.ContainsKey(tick!.Value)) ??
        candidates.FirstOrDefaultValue();

    private static bool IsClutch(
        SituationTrainingCandidateObservation item,
        SituationClutchSelectionRuleV1 rule)
    {
        var counts = AliveCounts(item.Snapshot);
        return counts.T == rule.SideAlive && counts.CT >= rule.MinimumOpponentAlive ||
               counts.CT == rule.SideAlive && counts.T >= rule.MinimumOpponentAlive;
    }

    private static IReadOnlyDictionary<string, int> AliveById(DemoFrame frame) => frame.Players
        .Where(player => player.Alive && player.Team is "T" or "CT")
        .GroupBy(player => player.Id, StringComparer.Ordinal)
        .ToDictionary(group => group.Key, group => group.Last().Health, StringComparer.Ordinal);

    private static (int T, int CT) AliveCounts(DemoFrame frame) => (
        frame.Players.Count(player => player.Alive && player.Team == "T"),
        frame.Players.Count(player => player.Alive && player.Team == "CT"));

    private static bool IsAliveCompetitivePlayerWithPosition(PlayerSnapshot player) =>
        player.Alive && player.Team is "T" or "CT" &&
        float.IsFinite(player.X) && float.IsFinite(player.Y);

    private static void Register(
        IReadOnlyDictionary<string, MutableCategoryStats> stats,
        string category,
        IEnumerable<int> ticks)
    {
        foreach (var tick in ticks)
            stats[category].CandidateTicks.Add(tick);
    }

    private static void AddTag(
        IDictionary<int, HashSet<string>> selected,
        IReadOnlyDictionary<string, MutableCategoryStats> stats,
        string category,
        int tick,
        int limit)
    {
        if (selected.TryGetValue(tick, out var tags))
        {
            if (tags.Add(category))
                stats[category].MergedCount++;
            return;
        }
        if (selected.Count >= limit)
        {
            stats[category].RemovedByLimitCount++;
            return;
        }
        selected[tick] = new HashSet<string>([category], StringComparer.Ordinal);
    }

    private static void RecordMissing(
        IReadOnlyDictionary<string, MutableCategoryStats> stats,
        IDictionary<string, int> shortages,
        string category,
        string code)
    {
        stats[category].MissingAnchorCount++;
        AddShortage(shortages, code);
    }

    private static void AddShortage(IDictionary<string, int> shortages, string code) =>
        shortages[code] = shortages.TryGetValue(code, out var count) ? count + 1 : 1;

    private static IEnumerable<int> Optional(int? value) => value is { } tick ? [tick] : [];

    private string StableCandidateId(string roundId, int tick) => SituationArtifactIO.Sha256(
        string.Join('\n',
            selectionLoad.Config.AlgorithmVersion,
            selectionLoad.Sha256,
            roundId,
            tick.ToString(CultureInfo.InvariantCulture)));

    private static IReadOnlyDictionary<string, int> NormalizeCounts(
        IReadOnlyDictionary<string, int>? values) => (values ?? new Dictionary<string, int>())
        .Where(item => item.Value > 0)
        .OrderBy(item => item.Key, StringComparer.Ordinal)
        .ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal);

    private sealed class MutableCategoryStats
    {
        internal HashSet<int> CandidateTicks { get; } = [];
        internal int SelectedCount { get; set; }
        internal int MergedCount { get; set; }
        internal int RemovedByLimitCount { get; set; }
        internal int MissingAnchorCount { get; set; }

        internal SituationTrainingSelectionCategoryStats ToImmutable() => new(
            CandidateTicks.Count,
            SelectedCount,
            MergedCount,
            RemovedByLimitCount,
            MissingAnchorCount);
    }

    private sealed record CandidateBuildProduct(
        SemanticFrame Semantic,
        SituationSceneBuildResult Scene,
        SituationDeterministicAnalysisResult Analysis);
}

internal static class SituationTrainingCandidateSelectionExtensions
{
    internal static int? FirstOrDefaultValue(this IReadOnlyList<int> values) =>
        values.Count == 0 ? null : values[0];
}

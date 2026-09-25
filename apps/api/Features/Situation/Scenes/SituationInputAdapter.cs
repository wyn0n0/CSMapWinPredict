using CsDemoMap.Api.Models;

namespace CsDemoMap.Api.Services;

internal sealed class SituationInputAdapter
{
    internal const double HistorySeconds = 4.5;

    public SceneInputPrefix BuildFromTimeline(
        DemoTimeline timeline,
        string demoRef,
        int windowIndex,
        int requestedTick,
        SceneObservationBoundary observationBoundary = SceneObservationBoundary.CompleteTimeline,
        CancellationToken cancellationToken = default,
        SemanticFrame? semanticOverride = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(timeline);
        if (string.IsNullOrWhiteSpace(demoRef))
            throw new ArgumentException("Demo reference is required.", nameof(demoRef));
        if (windowIndex < 0)
            throw new ArgumentOutOfRangeException(nameof(windowIndex));
        if (requestedTick < 0)
            throw new ArgumentOutOfRangeException(nameof(requestedTick));
        if (timeline.Metadata.TickRate <= 0)
            throw new InvalidDataException("Timeline tick rate must be positive.");
        if (!string.Equals(timeline.Metadata.MapName, "de_mirage", StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException("Situation scene draft v1 only supports de_mirage.");

        var tickRate = timeline.Metadata.TickRate;
        var coreStartTick = checked(windowIndex * DemoImportService.WindowSeconds * tickRate);
        var coreEndTick = checked((windowIndex + 1) * DemoImportService.WindowSeconds * tickRate);
        if (requestedTick < coreStartTick || requestedTick >= coreEndTick)
            throw new ArgumentOutOfRangeException(
                nameof(requestedTick),
                "Requested tick must belong to the selected core window.");

        var frames = timeline.Frames.OrderBy(frame => frame.Tick).ToArray();
        cancellationToken.ThrowIfCancellationRequested();
        EnsureUniqueTicks(frames, cancellationToken);
        var target = frames.LastOrDefault(frame =>
            frame.Tick >= coreStartTick && frame.Tick < coreEndTick && frame.Tick <= requestedTick)
            ?? throw new InvalidDataException("No frame exists at or before the requested tick in the core window.");

        if (semanticOverride is not null && semanticOverride.Tick != target.Tick)
            throw new InvalidDataException("Semantic override must describe the exact target frame.");
        var semantic = semanticOverride ?? FindSemanticAtOrBefore(timeline.Semantics, target);
        var roundStartTick = FindRoundStartTick(timeline, target, semantic, cancellationToken);
        var desiredHistoryStart = Math.Max(
            roundStartTick,
            target.Tick - (int)Math.Floor(HistorySeconds * tickRate));
        var history = frames
            .Where(frame => frame.Tick >= desiredHistoryStart && frame.Tick <= target.Tick)
            .Where(frame => IsSameRound(timeline.Semantics, frame, target, semantic, roundStartTick))
            .Select(frame =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return ProjectFrame(frame);
            })
            .ToArray();

        if (history.Length == 0 || history[^1].Tick != target.Tick)
            throw new InvalidDataException("Target frame was lost while restricting history to the current round.");

        var currentPlayerIds = target.Players
            .Where(player => player.Team is "T" or "CT")
            .Select(player => player.Id)
            .ToHashSet(StringComparer.Ordinal);
        cancellationToken.ThrowIfCancellationRequested();
        var equipment = LatestEquipment(
            timeline.PlayerEquipmentStates,
            currentPlayerIds,
            roundStartTick,
            target.Tick);
        var carriedUtility = LatestCarriedUtility(
            timeline.PlayerUtilityStates,
            currentPlayerIds,
            roundStartTick,
            target.Tick);
        var activeUtilities = ActiveUtilities(timeline.UtilityTracks, target.Tick, observationBoundary);
        var activeEffects = ActiveEffects(timeline.UtilityEffects, target.Tick, observationBoundary);
        cancellationToken.ThrowIfCancellationRequested();
        var quality = BuildQuality(
            target,
            requestedTick,
            semantic,
            desiredHistoryStart,
            history,
            equipment,
            carriedUtility,
            tickRate,
            roundStartTick,
            frames[0].Tick);
        cancellationToken.ThrowIfCancellationRequested();

        return new SceneInputPrefix(
            demoRef.Trim(),
            windowIndex,
            timeline.Metadata.MapName.ToLowerInvariant(),
            requestedTick,
            target.Tick,
            tickRate,
            roundStartTick,
            semantic is null ? null : ProjectSemantic(semantic),
            ProjectFrame(target),
            history,
            equipment,
            carriedUtility,
            activeUtilities,
            activeEffects,
            quality);
    }

    private static void EnsureUniqueTicks(
        IReadOnlyList<DemoFrame> frames,
        CancellationToken cancellationToken)
    {
        for (var index = 1; index < frames.Count; index++)
        {
            if ((index & 127) == 0)
                cancellationToken.ThrowIfCancellationRequested();
            if (frames[index - 1].Tick == frames[index].Tick)
                throw new InvalidDataException($"Timeline contains duplicate frame tick {frames[index].Tick}.");
        }
    }

    private static SemanticFrame? FindSemanticAtOrBefore(
        SemanticTimeline? semantics,
        DemoFrame target)
    {
        var semantic = semantics?.Frames
            .Where(frame => frame.Tick <= target.Tick)
            .OrderBy(frame => frame.Tick)
            .LastOrDefault();
        return semantic?.RoundNumber is null || semantic.RoundNumber == target.Round.Number
            ? semantic
            : null;
    }

    private static int FindRoundStartTick(
        DemoTimeline timeline,
        DemoFrame target,
        SemanticFrame? semantic,
        CancellationToken cancellationToken)
    {
        if (semantic?.RoundId is { } roundId)
        {
            var attempt = timeline.Semantics?.Attempts
                .Where(item => item.StartTick <= target.Tick)
                .Where(item => item.SegmentId == semantic.SegmentId)
                .Where(item => string.Equals(item.RoundId, roundId, StringComparison.Ordinal))
                .OrderBy(item => item.StartTick)
                .LastOrDefault();
            if (attempt is not null)
                return attempt.StartTick;
        }

        var startTick = target.Tick;
        foreach (var frame in timeline.Frames
            .Where(frame => frame.Tick <= target.Tick)
            .OrderByDescending(frame => frame.Tick))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (frame.Round.Number != target.Round.Number)
                break;
            startTick = frame.Tick;
        }
        return startTick;
    }

    private static bool IsSameRound(
        SemanticTimeline? semantics,
        DemoFrame frame,
        DemoFrame target,
        SemanticFrame? targetSemantic,
        int roundStartTick)
    {
        if (frame.Tick < roundStartTick || frame.Round.Number != target.Round.Number)
            return false;
        if (targetSemantic?.RoundId is not { } roundId)
            return true;

        var frameSemantic = semantics?.Frames.FirstOrDefault(item => item.Tick == frame.Tick);
        return frameSemantic is not null &&
            frameSemantic.SegmentId == targetSemantic.SegmentId &&
            string.Equals(frameSemantic.RoundId, roundId, StringComparison.Ordinal);
    }

    private static SceneInputFrame ProjectFrame(DemoFrame frame) => new(
        frame.Tick,
        frame.TimeSeconds,
        frame.Players
            .Where(player => player.Team is "T" or "CT")
            .Select(player => new SceneInputPlayer(
                player.Id,
                player.Team,
                player.Alive,
                player.Health,
                player.X,
                player.Y,
                player.Z,
                player.Yaw,
                player.VelocityX,
                player.VelocityY,
                player.VelocityZ,
                player.Region,
                player.Weapon))
            .ToArray(),
        new SceneInputRound(
            frame.Round.Number,
            frame.Round.Phase,
            frame.Round.ScoreT,
            frame.Round.ScoreCT,
            frame.Round.ElapsedSeconds,
            frame.Round.RemainingSeconds),
        new SceneInputBomb(
            frame.Bomb.State,
            frame.Bomb.CarrierId,
            frame.Bomb.DefuserId,
            frame.Bomb.Site,
            frame.Bomb.Region,
            frame.Bomb.X,
            frame.Bomb.Y,
            frame.Bomb.Z,
            frame.Bomb.SecondsToExplosion,
            frame.Bomb.SecondsToDefuse));

    private static SceneInputSemantic ProjectSemantic(SemanticFrame semantic) => new(
        semantic.Tick,
        semantic.RoundId,
        semantic.RoundNumber,
        semantic.SegmentId,
        semantic.Phase,
        new SceneInputClock(
            semantic.Clock.LiveElapsedSeconds,
            semantic.Clock.RoundRemainingSeconds,
            semantic.Clock.BombRemainingSeconds,
            semantic.Clock.DefuseRemainingSeconds,
            semantic.Clock.ClockKnown,
            semantic.Clock.ClockSource,
            semantic.Clock.PauseContext),
        new SceneInputRosterQuality(
            semantic.Roster.TMembers,
            semantic.Roster.CTMembers,
            semantic.Roster.LifeKnown,
            semantic.Roster.AliveKnown,
            semantic.Roster.AlivePositionKnown,
            semantic.Roster.AliveEquipmentKnown,
            semantic.Roster.RosterKnown,
            semantic.Roster.Quality,
            semantic.Roster.Reasons.ToArray()));

    private static IReadOnlyList<SceneInputEquipment> LatestEquipment(
        IReadOnlyList<PlayerEquipmentState> states,
        IReadOnlySet<string> playerIds,
        int roundStartTick,
        int targetTick) => states
        .Where(state => playerIds.Contains(state.PlayerId))
        .Where(state => state.Tick >= roundStartTick && state.Tick <= targetTick)
        .GroupBy(state => state.PlayerId, StringComparer.Ordinal)
        .Select(group => group.OrderBy(state => state.Tick).Last())
        .OrderBy(state => state.PlayerId, StringComparer.Ordinal)
        .Select(state => new SceneInputEquipment(
            state.PlayerId,
            state.Tick,
            state.Money,
            state.Armor,
            state.HasHelmet,
            state.HasDefuser,
            state.CurrentEquipmentValue,
            state.RoundStartEquipmentValue,
            state.CashSpentThisRound,
            state.Items
                .Select(item => new SceneInputEquipmentItem(
                    item.Name,
                    item.Category,
                    item.Count,
                    item.ClipAmmo,
                    item.ReserveAmmo))
                .OrderBy(item => item.Category, StringComparer.Ordinal)
                .ThenBy(item => item.Name, StringComparer.Ordinal)
                .ToArray()))
        .ToArray();

    private static IReadOnlyList<SceneInputCarriedUtility> LatestCarriedUtility(
        IReadOnlyList<PlayerUtilityState> states,
        IReadOnlySet<string> playerIds,
        int roundStartTick,
        int targetTick) => states
        .Where(state => playerIds.Contains(state.PlayerId))
        .Where(state => state.Tick >= roundStartTick && state.Tick <= targetTick)
        .GroupBy(state => state.PlayerId, StringComparer.Ordinal)
        .Select(group => group.OrderBy(state => state.Tick).Last())
        .OrderBy(state => state.PlayerId, StringComparer.Ordinal)
        .Select(state => new SceneInputCarriedUtility(
            state.PlayerId,
            state.Tick,
            state.Items
                .Select(item => new SceneInputUtilityCount(item.Type, item.Count))
                .OrderBy(item => item.Type, StringComparer.Ordinal)
                .ToArray()))
        .ToArray();

    private static IReadOnlyList<SceneInputUtilityTrack> ActiveUtilities(
        IReadOnlyList<UtilityTrack> tracks,
        int targetTick,
        SceneObservationBoundary observationBoundary) => tracks
        .Where(track => track.StartTick <= targetTick && track.EndTick > targetTick)
        .Select(track => new SceneInputUtilityTrack(
            track.Id,
            track.Type,
            track.ThrowerId,
            track.Team,
            track.StartTick,
            observationBoundary,
            track.Trajectory
                .Where(point => point.Tick <= targetTick)
                .OrderBy(point => point.Tick)
                .Select(ProjectPoint)
                .ToArray()))
        .Where(track => track.Points.Count > 0)
        .OrderBy(track => track.SourceId, StringComparer.Ordinal)
        .ToArray();

    private static IReadOnlyList<SceneInputEffect> ActiveEffects(
        IReadOnlyList<UtilityEffectTrack> effects,
        int targetTick,
        SceneObservationBoundary observationBoundary) => effects
        .Where(effect => effect.StartTick <= targetTick && effect.EndTick > targetTick)
        .Select(effect => new
        {
            Effect = effect,
            Sample = effect.Samples
                .Where(sample => sample.Tick <= targetTick)
                .OrderBy(sample => sample.Tick)
                .LastOrDefault()
        })
        .Where(item => item.Sample is not null)
        .Select(item => new SceneInputEffect(
            item.Effect.Id,
            item.Effect.Type,
            item.Effect.ThrowerId,
            item.Effect.Team,
            item.Effect.StartTick,
            observationBoundary,
            item.Sample!.Tick,
            item.Sample.X,
            item.Sample.Y,
            item.Sample.Z,
            item.Sample.Radius,
            item.Sample.Area
                .Select(point => new SceneInputPoint(point.X, point.Y, point.Z))
                .ToArray()))
        .OrderBy(effect => effect.SourceId, StringComparer.Ordinal)
        .ToArray();

    private static SceneInputPointAtTick ProjectPoint(UtilityPoint point) =>
        new(point.Tick, point.X, point.Y, point.Z);

    private static IReadOnlyList<SituationDataQuality> BuildQuality(
        DemoFrame target,
        int requestedTick,
        SemanticFrame? semantic,
        int desiredHistoryStart,
        IReadOnlyList<SceneInputFrame> history,
        IReadOnlyList<SceneInputEquipment> equipment,
        IReadOnlyList<SceneInputCarriedUtility> utility,
        int tickRate,
        int roundStartTick,
        int earliestTimelineTick)
    {
        var quality = new List<SituationDataQuality>();
        if (target.Tick != requestedTick)
            quality.Add(Quality(SituationDataQualityCodes.StaleFrame, "/tick", SituationQualitySeverity.Info));

        if (semantic is null || !semantic.Clock.ClockKnown)
            quality.Add(Quality(
                SituationDataQualityCodes.ClockUnknown,
                ["/round/elapsedSeconds", "/round/remainingSeconds"],
                SituationQualitySeverity.Warning));
        if (semantic is null || !semantic.Roster.RosterKnown || semantic.Roster.Quality != "usable")
            quality.Add(Quality(
                SituationDataQualityCodes.RosterIncomplete,
                ["/players", "/teams"],
                SituationQualitySeverity.Warning));

        var aliveIds = target.Players
            .Where(player => player.Team is "T" or "CT" && player.Alive)
            .Select(player => player.Id)
            .ToHashSet(StringComparer.Ordinal);
        var equipmentIds = equipment.Select(item => item.SourcePlayerId).ToHashSet(StringComparer.Ordinal);
        var utilityIds = utility.Select(item => item.SourcePlayerId).ToHashSet(StringComparer.Ordinal);
        if (!aliveIds.IsSubsetOf(equipmentIds))
            quality.Add(Quality(SituationDataQualityCodes.EquipmentUnknown, "/players", SituationQualitySeverity.Warning));
        if (!aliveIds.IsSubsetOf(utilityIds))
            quality.Add(Quality(SituationDataQualityCodes.UtilityUnknown, "/players", SituationQualitySeverity.Warning));
        if (semantic is null || semantic.Roster.AliveEquipmentKnown < semantic.Roster.AliveKnown)
            quality.Add(Quality(
                SituationDataQualityCodes.LegacyDefaultAmbiguous,
                ["/players", "/teams"],
                SituationQualitySeverity.Warning));

        var expectedStride = Math.Max(1, tickRate / DemoParserService.SampleRate);
        var theoreticalHistoryStart = target.Tick - (int)Math.Floor(HistorySeconds * tickRate);
        if (roundStartTick > theoreticalHistoryStart)
            quality.Add(Quality(
                SituationDataQualityCodes.HistoryRoundBoundary,
                "/players",
                SituationQualitySeverity.Info));
        if (history[0].Tick > desiredHistoryStart + expectedStride)
        {
            quality.Add(Quality(SituationDataQualityCodes.HistoryTruncated, "/players", SituationQualitySeverity.Warning));
            if (earliestTimelineTick > desiredHistoryStart + expectedStride)
                quality.Add(Quality(
                    SituationDataQualityCodes.HistoryFileBoundary,
                    "/players",
                    SituationQualitySeverity.Warning));
        }
        if (history.Zip(history.Skip(1), (left, right) => right.Tick - left.Tick)
            .Any(gap => gap > expectedStride))
            quality.Add(Quality(SituationDataQualityCodes.HistoryGap, "/players", SituationQualitySeverity.Warning));

        return quality
            .OrderBy(item => item.Code, StringComparer.Ordinal)
            .ThenBy(item => item.FieldPaths[0], StringComparer.Ordinal)
            .ToArray();
    }

    private static SituationDataQuality Quality(
        string code,
        string fieldPath,
        SituationQualitySeverity severity) => Quality(code, [fieldPath], severity);

    private static SituationDataQuality Quality(
        string code,
        IReadOnlyList<string> fieldPaths,
        SituationQualitySeverity severity) => new(code, fieldPaths, severity);
}

internal sealed record SceneInputPrefix(
    string DemoRef,
    int WindowIndex,
    string Map,
    int RequestedTick,
    int Tick,
    int TickRate,
    int RoundStartTick,
    SceneInputSemantic? Semantic,
    SceneInputFrame CurrentFrame,
    IReadOnlyList<SceneInputFrame> HistoryFrames,
    IReadOnlyList<SceneInputEquipment> Equipment,
    IReadOnlyList<SceneInputCarriedUtility> CarriedUtility,
    IReadOnlyList<SceneInputUtilityTrack> ActiveUtilities,
    IReadOnlyList<SceneInputEffect> ActiveEffects,
    IReadOnlyList<SituationDataQuality> DataQuality);

internal sealed record SceneInputFrame(
    int Tick,
    double TimeSeconds,
    IReadOnlyList<SceneInputPlayer> Players,
    SceneInputRound Round,
    SceneInputBomb Bomb);

internal sealed record SceneInputPlayer(
    string SourcePlayerId,
    string Team,
    bool Alive,
    int Health,
    float X,
    float Y,
    float Z,
    float Yaw,
    float VelocityX,
    float VelocityY,
    float VelocityZ,
    string Region,
    string? Weapon);

internal sealed record SceneInputRound(
    int Number,
    string Phase,
    int ScoreT,
    int ScoreCT,
    double ElapsedSeconds,
    double RemainingSeconds);

internal sealed record SceneInputBomb(
    string State,
    string? CarrierSourcePlayerId,
    string? DefuserSourcePlayerId,
    string? Site,
    string? Region,
    float? X,
    float? Y,
    float? Z,
    double? SecondsToExplosion,
    double? SecondsToDefuse);

internal sealed record SceneInputSemantic(
    int Tick,
    string? RoundRef,
    int? RoundNumber,
    int SegmentId,
    string Phase,
    SceneInputClock Clock,
    SceneInputRosterQuality Roster);

internal sealed record SceneInputClock(
    double? LiveElapsedSeconds,
    double? RoundRemainingSeconds,
    double? BombRemainingSeconds,
    double? DefuseRemainingSeconds,
    bool ClockKnown,
    string ClockSource,
    string PauseContext);

internal sealed record SceneInputRosterQuality(
    int TMembers,
    int CTMembers,
    int LifeKnown,
    int AliveKnown,
    int AlivePositionKnown,
    int AliveEquipmentKnown,
    bool RosterKnown,
    string Quality,
    IReadOnlyList<string> Reasons);

internal sealed record SceneInputEquipment(
    string SourcePlayerId,
    int Tick,
    int Money,
    int Armor,
    bool HasHelmet,
    bool HasDefuser,
    int CurrentEquipmentValue,
    int RoundStartEquipmentValue,
    int CashSpentThisRound,
    IReadOnlyList<SceneInputEquipmentItem> Items);

internal sealed record SceneInputEquipmentItem(
    string Name,
    string Category,
    int Count,
    int ClipAmmo,
    int ReserveAmmo);

internal sealed record SceneInputCarriedUtility(
    string SourcePlayerId,
    int Tick,
    IReadOnlyList<SceneInputUtilityCount> Items);

internal sealed record SceneInputUtilityCount(string Type, int Count);

internal sealed record SceneInputUtilityTrack(
    string SourceId,
    string Type,
    string? ThrowerSourcePlayerId,
    string Team,
    int ObservedStartTick,
    SceneObservationBoundary ObservationBoundary,
    IReadOnlyList<SceneInputPointAtTick> Points);

internal sealed record SceneInputEffect(
    string SourceId,
    string Type,
    string? ThrowerSourcePlayerId,
    string Team,
    int ObservedStartTick,
    SceneObservationBoundary ObservationBoundary,
    int SampleTick,
    float X,
    float Y,
    float Z,
    float Radius,
    IReadOnlyList<SceneInputPoint> Area);

internal sealed record SceneInputPointAtTick(int Tick, float X, float Y, float Z);

internal sealed record SceneInputPoint(float X, float Y, float Z);

internal enum SceneObservationBoundary
{
    CompleteTimeline,
    ArtificialPrefixEnd
}

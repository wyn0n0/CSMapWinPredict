using System.Globalization;
using CsDemoMap.Api.Models;

namespace CsDemoMap.Api.Services;

internal sealed class SituationSceneBuilder
{
    internal const string BuilderVersion = "situation-scene-builder-v1.3";
    internal const string GeometryVersion = "mirage-geometry-v1";
    private const double MapDimension = 1024d;

    public MinimapSceneV1 Build(SceneInputPrefix input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var geometry = MapFeatureGeometries.Find(input.Map)
            ?? throw new NotSupportedException($"No geometry is available for map '{input.Map}'.");

        var currentPlayers = input.CurrentFrame.Players
            .Where(player => player.Team is "T" or "CT" && player.Alive)
            .Select(player => new PlayerSource(
                player,
                FindEquipment(input, player.SourcePlayerId),
                FindCarriedUtility(input, player.SourcePlayerId),
                BuildHistory(input, player.SourcePlayerId),
                BuildPlayerSortKey(input, player)))
            .OrderBy(source => source.Player.Team == "T" ? 0 : 1)
            .ThenBy(source => source.SortKey, StringComparer.Ordinal)
            .ToArray();

        var slotsBySourceId = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var side in new[] { "T", "CT" })
        {
            var sidePlayers = currentPlayers.Where(source => source.Player.Team == side).ToArray();
            for (var index = 0; index < sidePlayers.Length; index++)
                slotsBySourceId.Add(sidePlayers[index].Player.SourcePlayerId, $"{side}{index + 1}");
        }

        var quality = input.DataQuality.ToList();
        var players = currentPlayers
            .Select((source, index) => BuildPlayer(
                source,
                slotsBySourceId[source.Player.SourcePlayerId],
                index,
                input,
                geometry,
                quality))
            .OrderBy(player => player.Side)
            .ThenBy(player => player.Slot, StringComparer.Ordinal)
            .ToArray();
        AddFloorQuality(players, quality);

        var teams = new SituationTeams(
            BuildTeamSummary(players, currentPlayers, SituationSide.T, input),
            BuildTeamSummary(players, currentPlayers, SituationSide.CT, input));
        var bomb = BuildBomb(input.CurrentFrame.Bomb, slotsBySourceId, geometry, quality);
        var utilities = BuildUtilities(input, slotsBySourceId, geometry, quality);
        var effects = BuildEffects(input, slotsBySourceId, geometry, quality);
        var sceneGeometry = BuildGeometry(players, geometry);

        return new MinimapSceneV1(
            SituationContractVersions.Scene,
            BuilderVersion,
            GeometryVersion,
            "structured",
            input.Map,
            input.DemoRef,
            input.WindowIndex,
            input.RequestedTick,
            input.Tick,
            input.TickRate,
            BuildRound(input),
            players,
            teams,
            bomb,
            utilities,
            effects,
            sceneGeometry,
            NormalizeQuality(quality));
    }

    private static SceneInputEquipment? FindEquipment(SceneInputPrefix input, string sourcePlayerId) =>
        input.Equipment.FirstOrDefault(item =>
            string.Equals(item.SourcePlayerId, sourcePlayerId, StringComparison.Ordinal));

    private static SceneInputCarriedUtility? FindCarriedUtility(
        SceneInputPrefix input,
        string sourcePlayerId) => input.CarriedUtility.FirstOrDefault(item =>
            string.Equals(item.SourcePlayerId, sourcePlayerId, StringComparison.Ordinal));

    private static IReadOnlyList<SceneInputPointAtTick> BuildHistory(
        SceneInputPrefix input,
        string sourcePlayerId) => input.HistoryFrames
        .SelectMany(frame => frame.Players
            .Where(player => player.Alive && player.Team is "T" or "CT")
            .Where(player => string.Equals(player.SourcePlayerId, sourcePlayerId, StringComparison.Ordinal))
            .Select(player => new SceneInputPointAtTick(frame.Tick, player.X, player.Y, player.Z)))
        .OrderBy(point => point.Tick)
        .ToArray();

    private static string BuildPlayerSortKey(SceneInputPrefix input, SceneInputPlayer player)
    {
        var equipment = FindEquipment(input, player.SourcePlayerId);
        var carriedUtility = FindCarriedUtility(input, player.SourcePlayerId);
        var history = BuildHistory(input, player.SourcePlayerId);
        var bombRole = string.Equals(
            input.CurrentFrame.Bomb.CarrierSourcePlayerId,
            player.SourcePlayerId,
            StringComparison.Ordinal) ? "carrier" : string.Equals(
                input.CurrentFrame.Bomb.DefuserSourcePlayerId,
                player.SourcePlayerId,
                StringComparison.Ordinal) ? "defuser" : "none";
        var thrown = input.ActiveUtilities
            .Where(item => string.Equals(item.ThrowerSourcePlayerId, player.SourcePlayerId, StringComparison.Ordinal))
            .Select(item => string.Join(':',
                item.Type,
                item.Team,
                item.ObservedStartTick,
                string.Join(',', item.Points.Select(point =>
                    $"{point.Tick}:{Number(point.X)}:{Number(point.Y)}:{Number(point.Z)}"))))
            .OrderBy(value => value, StringComparer.Ordinal);
        var effects = input.ActiveEffects
            .Where(item => string.Equals(item.ThrowerSourcePlayerId, player.SourcePlayerId, StringComparison.Ordinal))
            .Select(item => string.Join(':',
                item.Type,
                item.Team,
                item.ObservedStartTick,
                item.SampleTick,
                Number(item.X), Number(item.Y), Number(item.Z), Number(item.Radius),
                string.Join(',', item.Area.Select(point =>
                    $"{Number(point.X)}:{Number(point.Y)}:{Number(point.Z)}"))))
            .OrderBy(value => value, StringComparer.Ordinal);
        return string.Join('|',
            player.Team,
            Number(player.X), Number(player.Y), Number(player.Z), Number(player.Yaw),
            Number(player.VelocityX), Number(player.VelocityY), Number(player.VelocityZ),
            player.Health.ToString(CultureInfo.InvariantCulture), player.Region, player.Weapon ?? "",
            bombRole,
            equipment is null ? "equipment:null" : string.Join(':',
                equipment.Money, equipment.Armor, equipment.HasHelmet, equipment.HasDefuser,
                equipment.CurrentEquipmentValue,
                string.Join(',', equipment.Items.Select(item => $"{item.Category}:{item.Name}:{item.Count}"))),
            carriedUtility is null ? "utility:null" : string.Join(',',
                carriedUtility.Items.Select(item => $"{item.Type}:{item.Count}")),
            string.Join(',', history.Select(point =>
                $"{point.Tick}:{Number(point.X)}:{Number(point.Y)}:{Number(point.Z)}")),
            string.Join(',', thrown),
            string.Join(',', effects));
    }

    private static SituationPlayer BuildPlayer(
        PlayerSource source,
        string slot,
        int playerIndex,
        SceneInputPrefix input,
        MapFeatureGeometry geometry,
        ICollection<SituationDataQuality> quality)
    {
        var playerPath = $"/players/{playerIndex}";
        var position = NormalizePosition(
            source.Player.X, source.Player.Y, source.Player.Z, geometry,
            quality, $"{playerPath}/position");
        var velocity = NormalizeVelocity(
            source.Player.VelocityX, source.Player.VelocityY, source.Player.VelocityZ,
            geometry, quality, $"{playerPath}/velocity");
        var heading = NormalizeHeading(source.Player.Yaw, quality, $"{playerPath}/heading");
        var region = NormalizeRegion(source.Player.Region);
        if (region is null)
            quality.Add(Quality(SituationDataQualityCodes.RegionUnknown, $"{playerPath}/region"));
        if (source.Player.Health is <= 0 or > 100)
            quality.Add(Quality(
                SituationDataQualityCodes.HealthStateConflict,
                $"{playerPath}/health",
                SituationQualitySeverity.Error));
        var teamPath = source.Player.Team == "T" ? "/teams/t" : "/teams/ct";
        if (source.Equipment is null)
            quality.Add(new(
                SituationDataQualityCodes.EquipmentUnknown,
                [
                    $"{playerPath}/armor",
                    $"{teamPath}/totalArmor",
                    $"{teamPath}/totalMoney",
                    $"{teamPath}/equipmentValue",
                    $"{teamPath}/helmetCount",
                    $"{teamPath}/defuserCount"
                ],
                SituationQualitySeverity.Warning));
        else
        {
            var ambiguousEquipmentPaths = new List<string>();
            if (source.Equipment.Armor == 0)
                ambiguousEquipmentPaths.Add($"{playerPath}/armor");
            if (source.Equipment.Money == 0)
                ambiguousEquipmentPaths.Add($"{teamPath}/totalMoney");
            if (source.Equipment.CurrentEquipmentValue == 0)
                ambiguousEquipmentPaths.Add($"{teamPath}/equipmentValue");
            if (ambiguousEquipmentPaths.Count > 0)
                quality.Add(new(
                    SituationDataQualityCodes.LegacyDefaultAmbiguous,
                    ambiguousEquipmentPaths,
                    SituationQualitySeverity.Warning));
        }
        if (source.CarriedUtility is null)
            quality.Add(new(
                SituationDataQualityCodes.UtilityUnknown,
                [$"{playerPath}/utilityCounts", $"{teamPath}/utilityCounts"],
                SituationQualitySeverity.Warning));
        if (source.Player.VelocityX == 0 && source.Player.VelocityY == 0 && source.Player.VelocityZ == 0)
            quality.Add(Quality(
                SituationDataQualityCodes.LegacyDefaultAmbiguous,
                $"{playerPath}/velocity",
                SituationQualitySeverity.Warning));
        var trajectoryPoints = source.History
            .Select(point => new
            {
                point.Tick,
                Position = NormalizePosition(
                    point.X, point.Y, point.Z, geometry,
                    quality, $"{playerPath}/trajectory")
            })
            .Where(point => point.Position is not null)
            .Select(point => new SituationTrajectoryPoint(point.Tick, point.Position!))
            .ToArray();
        var fromTick = trajectoryPoints.Length == 0 ? input.Tick : trajectoryPoints[0].Tick;
        var toTick = trajectoryPoints.Length == 0 ? input.Tick : trajectoryPoints[^1].Tick;

        return new SituationPlayer(
            slot,
            ParseSide(source.Player.Team),
            position,
            SituationFloor.Unknown,
            region,
            region is null ? SituationRegionSource.Unknown : SituationRegionSource.DemoPlaceName,
            heading,
            velocity,
            Math.Clamp(source.Player.Health, 0, 100),
            source.Equipment is null ? null : Math.Clamp(source.Equipment.Armor, 0, 100),
            ParseWeaponCategory(source.Player.Weapon),
            BuildUtilityCounts(source.CarriedUtility),
            new SituationTrajectory(
                fromTick,
                toTick,
                Round((toTick - fromTick) / (double)input.TickRate),
                trajectoryPoints));
    }

    private static SituationRound BuildRound(SceneInputPrefix input)
    {
        var semantic = input.Semantic;
        var clockKnown = semantic?.Clock.ClockKnown == true;
        return new SituationRound(
            semantic?.RoundRef,
            semantic?.SegmentId,
            semantic?.RoundNumber ?? input.CurrentFrame.Round.Number,
            ParseRoundPhase(semantic?.Phase ?? input.CurrentFrame.Round.Phase),
            clockKnown ? semantic!.Clock.LiveElapsedSeconds : null,
            clockKnown ? semantic!.Clock.RoundRemainingSeconds : null,
            new SituationScore(input.CurrentFrame.Round.ScoreT, input.CurrentFrame.Round.ScoreCT),
            semantic?.Clock.ClockSource);
    }

    private static SituationTeamSummary BuildTeamSummary(
        IReadOnlyList<SituationPlayer> players,
        IReadOnlyList<PlayerSource> sources,
        SituationSide side,
        SceneInputPrefix input)
    {
        var sidePlayers = players.Where(player => player.Side == side).ToArray();
        var sideSources = sources.Where(source => ParseSide(source.Player.Team) == side).ToArray();
        var rosterUsable = input.Semantic?.Roster is { RosterKnown: true } roster &&
            roster.Quality is "usable" or "partial";
        var equipmentComplete = rosterUsable && sideSources.All(source => source.Equipment is not null);
        var utilityComplete = rosterUsable && sideSources.All(source => source.CarriedUtility is not null);
        return new SituationTeamSummary(
            rosterUsable ? sidePlayers.Length : null,
            rosterUsable ? sidePlayers.Sum(player => player.Health ?? 0) : null,
            equipmentComplete ? sidePlayers.Sum(player => player.Armor ?? 0) : null,
            equipmentComplete ? sideSources.Sum(source => source.Equipment!.Money) : null,
            equipmentComplete ? sideSources.Sum(source => source.Equipment!.CurrentEquipmentValue) : null,
            equipmentComplete ? sideSources.Count(source => source.Equipment!.HasHelmet) : null,
            equipmentComplete ? sideSources.Count(source => source.Equipment!.HasDefuser) : null,
            utilityComplete ? SumUtilityCounts(sideSources.Select(source => source.CarriedUtility!)) : UnknownUtilityCounts(),
            rosterUsable ? CountWeapons(sidePlayers) : UnknownWeaponCounts());
    }

    private static SituationUtilityCounts BuildUtilityCounts(SceneInputCarriedUtility? utility)
    {
        if (utility is null)
            return UnknownUtilityCounts();
        int Count(string type) => utility.Items
            .Where(item => string.Equals(item.Type, type, StringComparison.Ordinal))
            .Sum(item => Math.Max(0, item.Count));
        return new(Count("flash"), Count("smoke"), Count("he"), Count("molotov"),
            Count("incendiary"), Count("decoy"));
    }

    private static SituationUtilityCounts SumUtilityCounts(IEnumerable<SceneInputCarriedUtility> states)
    {
        var items = states.SelectMany(state => state.Items).ToArray();
        int Count(string type) => items.Where(item => item.Type == type).Sum(item => Math.Max(0, item.Count));
        return new(Count("flash"), Count("smoke"), Count("he"), Count("molotov"),
            Count("incendiary"), Count("decoy"));
    }

    private static SituationUtilityCounts UnknownUtilityCounts() =>
        new(null, null, null, null, null, null);

    private static SituationWeaponCounts CountWeapons(IEnumerable<SituationPlayer> players)
    {
        var categories = players.GroupBy(player => player.WeaponCategory)
            .ToDictionary(group => group.Key, group => group.Count());
        int Count(SituationWeaponCategory category) => categories.GetValueOrDefault(category);
        return new(
            Count(SituationWeaponCategory.Pistol), Count(SituationWeaponCategory.Smg),
            Count(SituationWeaponCategory.Rifle), Count(SituationWeaponCategory.Sniper),
            Count(SituationWeaponCategory.Shotgun), Count(SituationWeaponCategory.Machinegun),
            Count(SituationWeaponCategory.Grenade), Count(SituationWeaponCategory.Knife),
            Count(SituationWeaponCategory.Bomb), Count(SituationWeaponCategory.Other),
            Count(SituationWeaponCategory.Unknown));
    }

    private static SituationWeaponCounts UnknownWeaponCounts() =>
        new(null, null, null, null, null, null, null, null, null, null, null);

    private static SituationBomb BuildBomb(
        SceneInputBomb source,
        IReadOnlyDictionary<string, string> slotsBySourceId,
        MapFeatureGeometry geometry,
        ICollection<SituationDataQuality> quality)
    {
        var state = ParseBombState(source.State);
        var carrier = ResolveSlot(source.CarrierSourcePlayerId, slotsBySourceId);
        var defuser = ResolveSlot(source.DefuserSourcePlayerId, slotsBySourceId);
        if (source.CarrierSourcePlayerId is not null && carrier is null)
            quality.Add(Quality(SituationDataQualityCodes.RosterIncomplete, "/bomb/carrierSlot"));
        if (source.DefuserSourcePlayerId is not null && defuser is null)
            quality.Add(Quality(SituationDataQualityCodes.RosterIncomplete, "/bomb/defuserSlot"));
        var position = source.X is { } x && source.Y is { } y && source.Z is { } z
            ? NormalizePosition(x, y, z, geometry, quality, "/bomb/position")
            : null;
        var region = NormalizeRegion(source.Region);
        var regionSource = state switch
        {
            SituationBombState.Carried or SituationBombState.Planting when region is not null =>
                SituationRegionSource.DemoPlaceName,
            SituationBombState.Planted or SituationBombState.Defusing or
                SituationBombState.Defused or SituationBombState.Exploded when region is not null =>
                SituationRegionSource.Geometry,
            _ => SituationRegionSource.Unknown
        };
        return new(
            state, carrier, defuser, ParseSite(source.Site), position, region, regionSource,
            NonNegativeOrNull(source.SecondsToExplosion), NonNegativeOrNull(source.SecondsToDefuse));
    }

    private static IReadOnlyList<SituationUtility> BuildUtilities(
        SceneInputPrefix input,
        IReadOnlyDictionary<string, string> slotsBySourceId,
        MapFeatureGeometry geometry,
        ICollection<SituationDataQuality> quality)
    {
        var historyStartTick = input.Tick - (int)Math.Floor(
            SituationInputAdapter.HistorySeconds * input.TickRate);
        var ordered = input.ActiveUtilities
            .Select(source =>
            {
                var relevantPoints = source.Points
                    .Where(point => point.Tick >= historyStartTick)
                    .OrderBy(point => point.Tick)
                    .ToArray();
                var lastPoint = relevantPoints.LastOrDefault(point =>
                    float.IsFinite(point.X) && float.IsFinite(point.Y) && float.IsFinite(point.Z));
                return new
                {
                    Source = source,
                    Type = ParseUtilityType(source.Type),
                    SortPosition = lastPoint is null
                        ? null
                        : NormalizePositionForSort(lastPoint.X, lastPoint.Y, lastPoint.Z, geometry),
                    SortKey = string.Join('|',
                        source.Type,
                        source.Team,
                        ResolveSlot(source.ThrowerSourcePlayerId, slotsBySourceId) ?? "",
                        source.ObservedStartTick.ToString(CultureInfo.InvariantCulture),
                        string.Join(',', relevantPoints.Select(point =>
                            $"{point.Tick}:{PositionSortKey(point.X, point.Y, point.Z, geometry)}")))
                };
            })
            .OrderBy(item => item.Type)
            .ThenBy(item => item.Source.ObservedStartTick)
            .ThenBy(item => item.SortPosition?.X)
            .ThenBy(item => item.SortPosition?.Y)
            .ThenBy(item => item.SortKey, StringComparer.Ordinal)
            .ToArray();
        return ordered.Select((candidate, index) =>
        {
            var source = candidate.Source;
            var points = source.Points
                .Where(point => point.Tick >= historyStartTick)
                .Select(point => new
                {
                    point.Tick,
                    Position = NormalizePosition(point.X, point.Y, point.Z, geometry, quality,
                        $"/utilities/{index}/trajectory")
                })
                .Where(point => point.Position is not null)
                .Select(point => new SituationTrajectoryPoint(point.Tick, point.Position!))
                .ToArray();
            var position = points.LastOrDefault()?.Position;
            var fromTick = points.Length == 0 ? input.Tick : points[0].Tick;
            var toTick = points.Length == 0 ? input.Tick : points[^1].Tick;
            return new SituationUtility(
                $"utility-{index + 1}",
                candidate.Type,
                TryParseSide(source.Team),
                ResolveSlot(source.ThrowerSourcePlayerId, slotsBySourceId),
                source.ObservedStartTick,
                position,
                new SituationTrajectory(
                    fromTick, toTick,
                    Round((toTick - fromTick) / (double)input.TickRate), points));
        })
        .ToArray();
    }

    private static IReadOnlyList<SituationEffect> BuildEffects(
        SceneInputPrefix input,
        IReadOnlyDictionary<string, string> slotsBySourceId,
        MapFeatureGeometry geometry,
        ICollection<SituationDataQuality> quality)
    {
        var ordered = input.ActiveEffects
            .Select(source => new
            {
                Source = source,
                Type = source.Type == "smoke" ? SituationEffectType.Smoke : SituationEffectType.Fire,
                SortPosition = NormalizePositionForSort(source.X, source.Y, source.Z, geometry),
                SortKey = string.Join('|',
                    source.Type,
                    source.Team,
                    source.ObservedStartTick.ToString(CultureInfo.InvariantCulture),
                    source.SampleTick.ToString(CultureInfo.InvariantCulture),
                    PositionSortKey(source.X, source.Y, source.Z, geometry),
                    float.IsFinite(source.Radius)
                        ? Round(Math.Max(0, source.Radius) / (geometry.Scale * MapDimension))
                            .ToString("R", CultureInfo.InvariantCulture)
                        : "null",
                    string.Join(',', source.Area.Select(point =>
                        PositionSortKey(point.X, point.Y, point.Z, geometry))))
            })
            .OrderBy(item => item.Type)
            .ThenBy(item => item.Source.ObservedStartTick)
            .ThenBy(item => item.SortPosition?.X)
            .ThenBy(item => item.SortPosition?.Y)
            .ThenBy(item => item.SortKey, StringComparer.Ordinal)
            .ToArray();
        return ordered.Select((candidate, index) =>
        {
            var source = candidate.Source;
            var type = candidate.Type;
            if (type == SituationEffectType.Fire && source.Area.Count == 0)
                quality.Add(Quality(
                    SituationDataQualityCodes.EffectStateIncomplete,
                    "/effects",
                    SituationQualitySeverity.Warning));
            return new SituationEffect(
                $"effect-{index + 1}",
                type,
                TryParseSide(source.Team),
                source.ObservedStartTick,
                source.SampleTick,
                NormalizePosition(source.X, source.Y, source.Z, geometry, quality,
                    $"/effects/{index}/position"),
                float.IsFinite(source.Radius)
                    ? Round(Math.Max(0, source.Radius) / (geometry.Scale * MapDimension))
                    : null,
                source.Area.Count == 0 ? null : source.Area
                    .Select(point => NormalizePosition(point.X, point.Y, point.Z, geometry, quality,
                        $"/effects/{index}/area"))
                    .Where(point => point is not null)
                    .Select(point => point!)
                    .ToArray());
        })
        .ToArray();
    }

    private static SituationGeometry BuildGeometry(
        IReadOnlyList<SituationPlayer> players,
        MapFeatureGeometry geometry)
    {
        var regionOccupancy = players
            .Where(player => player.Region is not null)
            .GroupBy(player => player.Region!, StringComparer.Ordinal)
            .Select(group => new SituationRegionOccupancy(
                group.Key,
                group.Count(player => player.Side == SituationSide.T),
                group.Count(player => player.Side == SituationSide.CT)))
            .OrderBy(item => item.Region, StringComparer.Ordinal)
            .ToArray();
        var siteA = geometry.Normalize(geometry.SiteA.X, geometry.SiteA.Y);
        var siteB = geometry.Normalize(geometry.SiteB.X, geometry.SiteB.Y);
        var distances = players.Select(player => new SituationPlayerDistances(
            player.Slot,
            Distance(player.Position, siteA),
            Distance(player.Position, siteB),
            NearestDistance(player, players.Where(other => other.Side == player.Side && other.Slot != player.Slot)),
            NearestDistance(player, players.Where(other => other.Side != player.Side))))
            .OrderBy(item => item.Slot, StringComparer.Ordinal)
            .ToArray();
        return new(regionOccupancy, distances);
    }

    private static double? NearestDistance(
        SituationPlayer player,
        IEnumerable<SituationPlayer> candidates)
    {
        if (player.Position is null)
            return null;
        var distances = candidates
            .Select(candidate => Distance(player.Position, candidate.Position))
            .Where(distance => distance is not null)
            .Select(distance => distance!.Value)
            .ToArray();
        return distances.Length == 0 ? null : distances.Min();
    }

    private static double? Distance(SituationVec3? position, MapPoint target)
    {
        if (position is null)
            return null;
        var dx = position.X - target.X;
        var dy = position.Y - target.Y;
        return Round(Math.Sqrt(dx * dx + dy * dy));
    }

    private static double? Distance(SituationVec3? left, SituationVec3? right)
    {
        if (left is null || right is null)
            return null;
        var dx = left.X - right.X;
        var dy = left.Y - right.Y;
        return Round(Math.Sqrt(dx * dx + dy * dy));
    }

    private static SituationVec3? NormalizePosition(
        float worldX,
        float worldY,
        float worldZ,
        MapFeatureGeometry geometry,
        ICollection<SituationDataQuality> quality,
        string fieldPath)
    {
        if (!float.IsFinite(worldX) || !float.IsFinite(worldY) || !float.IsFinite(worldZ))
        {
            quality.Add(Quality(SituationDataQualityCodes.PositionInvalid, fieldPath, SituationQualitySeverity.Error));
            return null;
        }
        var point = geometry.Normalize(worldX, worldY);
        if (point.X is < 0 or > 1 || point.Y is < 0 or > 1)
            quality.Add(Quality(
                SituationDataQualityCodes.PositionOutsideRadar,
                fieldPath,
                SituationQualitySeverity.Warning));
        return new(Round(point.X), Round(point.Y), Round(worldZ / (geometry.Scale * MapDimension)));
    }

    private static SituationVec3? NormalizePositionForSort(
        float worldX,
        float worldY,
        float worldZ,
        MapFeatureGeometry geometry)
    {
        if (!float.IsFinite(worldX) || !float.IsFinite(worldY) || !float.IsFinite(worldZ))
            return null;
        var point = geometry.Normalize(worldX, worldY);
        return new(Round(point.X), Round(point.Y), Round(worldZ / (geometry.Scale * MapDimension)));
    }

    private static string PositionSortKey(
        float worldX,
        float worldY,
        float worldZ,
        MapFeatureGeometry geometry)
    {
        var position = NormalizePositionForSort(worldX, worldY, worldZ, geometry);
        return position is null
            ? $"invalid:{Number(worldX)}:{Number(worldY)}:{Number(worldZ)}"
            : string.Join(':',
                position.X.ToString("R", CultureInfo.InvariantCulture),
                position.Y.ToString("R", CultureInfo.InvariantCulture),
                position.Z.ToString("R", CultureInfo.InvariantCulture));
    }

    private static SituationVec3? NormalizeVelocity(
        float worldX,
        float worldY,
        float worldZ,
        MapFeatureGeometry geometry,
        ICollection<SituationDataQuality> quality,
        string fieldPath)
    {
        if (!float.IsFinite(worldX) || !float.IsFinite(worldY) || !float.IsFinite(worldZ))
        {
            quality.Add(Quality(SituationDataQualityCodes.PositionInvalid, fieldPath, SituationQualitySeverity.Error));
            return null;
        }
        var denominator = geometry.Scale * MapDimension;
        return new(Round(worldX / denominator), Round(-worldY / denominator), Round(worldZ / denominator));
    }

    private static SituationVec2? NormalizeHeading(
        float yaw,
        ICollection<SituationDataQuality> quality,
        string fieldPath)
    {
        if (!float.IsFinite(yaw))
        {
            quality.Add(Quality(SituationDataQualityCodes.PositionInvalid, fieldPath, SituationQualitySeverity.Error));
            return null;
        }
        var radians = yaw * Math.PI / 180d;
        return new(Round(Math.Cos(radians)), Round(-Math.Sin(radians)));
    }

    private static void AddFloorQuality(
        IReadOnlyList<SituationPlayer> players,
        ICollection<SituationDataQuality> quality)
    {
        if (players.Count == 0)
            return;
        quality.Add(new(
            SituationDataQualityCodes.FloorUnknown,
            players.Select((_, index) => $"/players/{index}/floor").ToArray(),
            SituationQualitySeverity.Info));
    }

    private static IReadOnlyList<SituationDataQuality> NormalizeQuality(
        IEnumerable<SituationDataQuality> quality) => quality
        .GroupBy(item => new
        {
            item.Code,
            item.Severity,
            Paths = string.Join('\n', item.FieldPaths.OrderBy(path => path, StringComparer.Ordinal))
        })
        .Select(group => group.First() with
        {
            FieldPaths = group.First().FieldPaths.OrderBy(path => path, StringComparer.Ordinal).ToArray()
        })
        .OrderBy(item => item.Code, StringComparer.Ordinal)
        .ThenBy(item => item.FieldPaths.FirstOrDefault(), StringComparer.Ordinal)
        .ToArray();

    private static string? ResolveSlot(
        string? sourcePlayerId,
        IReadOnlyDictionary<string, string> slotsBySourceId) =>
        sourcePlayerId is not null && slotsBySourceId.TryGetValue(sourcePlayerId, out var slot)
            ? slot
            : null;

    private static SituationSide ParseSide(string side) => side == "T"
        ? SituationSide.T
        : side == "CT"
            ? SituationSide.CT
            : throw new InvalidDataException($"Unsupported player side '{side}'.");

    private static SituationSide? TryParseSide(string side) => side switch
    {
        "T" => SituationSide.T,
        "CT" => SituationSide.CT,
        _ => null
    };

    private static SituationSite? ParseSite(string? site) => site switch
    {
        "A" => SituationSite.A,
        "B" => SituationSite.B,
        _ => null
    };

    private static SituationRoundPhase ParseRoundPhase(string? phase) => phase switch
    {
        "warmup" => SituationRoundPhase.Warmup,
        "team-intro" => SituationRoundPhase.TeamIntro,
        "freeze" => SituationRoundPhase.Freeze,
        "live" => SituationRoundPhase.Live,
        "post-plant" => SituationRoundPhase.PostPlant,
        "ended" => SituationRoundPhase.Ended,
        _ => SituationRoundPhase.Unknown
    };

    private static SituationBombState ParseBombState(string state) => state switch
    {
        "carried" => SituationBombState.Carried,
        "dropped" => SituationBombState.Dropped,
        "planting" => SituationBombState.Planting,
        "planted" => SituationBombState.Planted,
        "defusing" => SituationBombState.Defusing,
        "defused" => SituationBombState.Defused,
        "exploded" => SituationBombState.Exploded,
        _ => SituationBombState.Unknown
    };

    private static SituationUtilityType ParseUtilityType(string type) => type switch
    {
        "flash" => SituationUtilityType.Flash,
        "smoke" => SituationUtilityType.Smoke,
        "he" => SituationUtilityType.He,
        "molotov" => SituationUtilityType.Molotov,
        "incendiary" => SituationUtilityType.Incendiary,
        "decoy" => SituationUtilityType.Decoy,
        _ => SituationUtilityType.Unknown
    };

    private static SituationWeaponCategory ParseWeaponCategory(string? weapon)
    {
        if (string.IsNullOrWhiteSpace(weapon))
            return SituationWeaponCategory.Unknown;
        if (weapon == "weapon_c4")
            return SituationWeaponCategory.Bomb;
        if (weapon.Contains("knife", StringComparison.Ordinal) || weapon == "weapon_bayonet")
            return SituationWeaponCategory.Knife;
        if (weapon is "weapon_smokegrenade" or "weapon_flashbang" or "weapon_hegrenade" or
            "weapon_molotov" or "weapon_incgrenade" or "weapon_decoy")
            return SituationWeaponCategory.Grenade;
        if (weapon is "weapon_glock" or "weapon_hkp2000" or "weapon_usp_silencer" or
            "weapon_p250" or "weapon_deagle" or "weapon_elite" or "weapon_fiveseven" or
            "weapon_tec9" or "weapon_cz75a" or "weapon_revolver")
            return SituationWeaponCategory.Pistol;
        if (weapon is "weapon_mac10" or "weapon_mp9" or "weapon_mp7" or "weapon_mp5sd" or
            "weapon_ump45" or "weapon_p90" or "weapon_bizon")
            return SituationWeaponCategory.Smg;
        if (weapon is "weapon_nova" or "weapon_xm1014" or "weapon_mag7" or "weapon_sawedoff")
            return SituationWeaponCategory.Shotgun;
        if (weapon is "weapon_awp" or "weapon_ssg08" or "weapon_scar20" or "weapon_g3sg1")
            return SituationWeaponCategory.Sniper;
        if (weapon is "weapon_m249" or "weapon_negev")
            return SituationWeaponCategory.Machinegun;
        if (weapon is "weapon_ak47" or "weapon_galilar" or "weapon_famas" or "weapon_m4a1" or
            "weapon_m4a1_silencer" or "weapon_aug" or "weapon_sg556")
            return SituationWeaponCategory.Rifle;
        return SituationWeaponCategory.Other;
    }

    private static string? NormalizeRegion(string? region) =>
        string.IsNullOrWhiteSpace(region) || region == "unknown" ? null : region.Trim();

    private static double? NonNegativeOrNull(double? value) =>
        value is { } number && double.IsFinite(number) ? Round(Math.Max(0, number)) : null;

    private static double Round(double value)
    {
        var rounded = Math.Round(value, 6, MidpointRounding.AwayFromZero);
        return rounded == 0 ? 0 : rounded;
    }

    private static string Number(float value) =>
        value.ToString("R", CultureInfo.InvariantCulture);

    private static SituationDataQuality Quality(
        string code,
        string path,
        SituationQualitySeverity severity = SituationQualitySeverity.Warning) =>
        new(code, [path], severity);

    private sealed record PlayerSource(
        SceneInputPlayer Player,
        SceneInputEquipment? Equipment,
        SceneInputCarriedUtility? CarriedUtility,
        IReadOnlyList<SceneInputPointAtTick> History,
        string SortKey);
}

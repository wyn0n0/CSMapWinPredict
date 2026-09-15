using CsDemoMap.Api.Models;

namespace CsDemoMap.Api.Services;

public sealed class AsOfTickFeatureBuilder
{
    private readonly IReadOnlyDictionary<string, PlayerEquipmentState[]> equipmentByPlayer;
    private readonly IReadOnlyDictionary<int, BombTransition[]> bombTransitionsByRound;
    private readonly int tickRate;
    private readonly MapFeatureGeometry? mapGeometry;

    public AsOfTickFeatureBuilder(DemoTimeline timeline)
    {
        equipmentByPlayer = timeline.PlayerEquipmentStates
            .GroupBy(state => state.PlayerId, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.OrderBy(state => state.Tick).ToArray(),
                StringComparer.Ordinal);
        tickRate = Math.Max(1, timeline.Metadata.TickRate);
        mapGeometry = MapFeatureGeometries.Find(timeline.Metadata.MapName);
        bombTransitionsByRound = timeline.Frames
            .Where(frame => frame.Round.Phase is "live" or "post-plant")
            .OrderBy(frame => frame.Tick)
            .GroupBy(frame => frame.Round.Number)
            .ToDictionary(group => group.Key, BuildBombTransitions);

    }

    public WinFeatureSnapshot Build(DemoFrame frame)
    {
        var currentPlayers = frame.Players
            .Where(player => player.Team is "T" or "CT")
            .Select(player => new CurrentPlayer(
                player,
                FindEquipmentAtOrBefore(player.Id, frame.Tick)))
            .ToArray();

        var players = currentPlayers
            .OrderBy(player => player.Snapshot.Team, StringComparer.Ordinal)
            .ThenByDescending(player => player.Snapshot.Alive)
            .ThenBy(player => player.Snapshot.Region, StringComparer.Ordinal)
            .ThenBy(player => player.Snapshot.X)
            .ThenBy(player => player.Snapshot.Y)
            .Select(ToPlayerFeature)
            .ToArray();

        var bomb = frame.Bomb;
        var tSummary = SummarizeTeam(currentPlayers, "T");
        var ctSummary = SummarizeTeam(currentPlayers, "CT");
        return new WinFeatureSnapshot(
            frame.Round.Phase,
            frame.Round.ElapsedSeconds,
            frame.Round.RemainingSeconds,
            frame.Round.ScoreT,
            frame.Round.ScoreCT,
            frame.Round.ConsecutiveLossesT,
            frame.Round.ConsecutiveLossesCT,
            new BombFeatureSnapshot(
                bomb.State,
                bomb.CarrierId is not null,
                bomb.DefuserId is not null,
                bomb.Site,
                bomb.Region,
                bomb.X,
                bomb.Y,
                bomb.Z,
                bomb.SecondsToExplosion,
                bomb.SecondsToDefuse),
            tSummary,
            ctSummary,
            players,
            frame.Zones
                .OrderBy(zone => zone.Region, StringComparer.Ordinal)
                .Select(zone => new ZoneFeatureSnapshot(
                    zone.Region,
                    zone.TAlive,
                    zone.CTAlive,
                    zone.TTotal,
                    zone.CTTotal))
                .ToArray(),
            BuildBaseline(frame, currentPlayers, tSummary, ctSummary));
    }

    private PlayerEquipmentState? FindEquipmentAtOrBefore(string playerId, int tick)
    {
        if (!equipmentByPlayer.TryGetValue(playerId, out var states))
            return null;

        var low = 0;
        var high = states.Length - 1;
        while (low <= high)
        {
            var middle = low + (high - low) / 2;
            if (states[middle].Tick <= tick)
                low = middle + 1;
            else
                high = middle - 1;
        }

        return high >= 0 ? states[high] : null;
    }

    private BaselineFeatureSnapshot BuildBaseline(
        DemoFrame frame,
        IReadOnlyList<CurrentPlayer> players,
        TeamFeatureSnapshot tSummary,
        TeamFeatureSnapshot ctSummary)
    {
        var transition = FindBombTransition(frame);
        var tPositions = NormalizedAlivePositions(players, "T");
        var ctPositions = NormalizedAlivePositions(players, "CT");
        var siteA = mapGeometry is null
            ? null
            : mapGeometry.Normalize(mapGeometry.SiteA.X, mapGeometry.SiteA.Y);
        var siteB = mapGeometry is null
            ? null
            : mapGeometry.Normalize(mapGeometry.SiteB.X, mapGeometry.SiteB.Y);
        var tPositionDispersion = PositionDispersion(tPositions);
        var ctPositionDispersion = PositionDispersion(ctPositions);
        var nearestOpponentDistance = NearestDistance(tPositions, ctPositions);
        var tMeanDistanceToSiteA = MeanDistance(tPositions, siteA);
        var tMeanDistanceToSiteB = MeanDistance(tPositions, siteB);
        var ctMeanDistanceToSiteA = MeanDistance(ctPositions, siteA);
        var ctMeanDistanceToSiteB = MeanDistance(ctPositions, siteB);
        var tMinDistanceToSiteA = MinDistance(tPositions, siteA);
        var tMinDistanceToSiteB = MinDistance(tPositions, siteB);
        var ctMinDistanceToSiteA = MinDistance(ctPositions, siteA);
        var ctMinDistanceToSiteB = MinDistance(ctPositions, siteB);
        var tEquipmentCoverage = EquipmentCoverage(players, "T");
        var ctEquipmentCoverage = EquipmentCoverage(players, "CT");
        return new BaselineFeatureSnapshot(
            ScoreDifference: frame.Round.ScoreT - frame.Round.ScoreCT,
            LossStreakDifference: frame.Round.ConsecutiveLossesT - frame.Round.ConsecutiveLossesCT,
            BombState: frame.Bomb.State,
            PreviousBombState: transition.PreviousState,
            BombStateChangeCount: transition.ChangeCount,
            SecondsSinceBombStateChange: Math.Max(0, (frame.Tick - transition.Tick) / (double)tickRate),
            HasExplosionTimer: frame.Bomb.SecondsToExplosion is not null,
            HasDefuseTimer: frame.Bomb.SecondsToDefuse is not null,
            BombWasDropped: transition.WasDropped,
            BombWasPlanting: transition.WasPlanting,
            BombWasPlanted: transition.WasPlanted,
            BombWasDefusing: transition.WasDefusing,
            TPositionDispersion: tPositionDispersion,
            CTPositionDispersion: ctPositionDispersion,
            PositionDispersionDifference: Difference(tPositionDispersion, ctPositionDispersion),
            TPositionDataMissing: tPositions is null || tPositions.Length == 0,
            CTPositionDataMissing: ctPositions is null || ctPositions.Length == 0,
            NearestOpponentDistance: nearestOpponentDistance,
            NearestOpponentDistanceMissing: nearestOpponentDistance is null,
            TMeanDistanceToSiteA: tMeanDistanceToSiteA,
            TMeanDistanceToSiteB: tMeanDistanceToSiteB,
            CTMeanDistanceToSiteA: ctMeanDistanceToSiteA,
            CTMeanDistanceToSiteB: ctMeanDistanceToSiteB,
            TMinDistanceToSiteA: tMinDistanceToSiteA,
            TMinDistanceToSiteB: tMinDistanceToSiteB,
            CTMinDistanceToSiteA: ctMinDistanceToSiteA,
            CTMinDistanceToSiteB: ctMinDistanceToSiteB,
            TClosestSiteDistance: SmallestDistance(tMinDistanceToSiteA, tMinDistanceToSiteB),
            CTClosestSiteDistance: SmallestDistance(ctMinDistanceToSiteA, ctMinDistanceToSiteB),
            SiteAProximityDifference: Difference(ctMeanDistanceToSiteA, tMeanDistanceToSiteA),
            SiteBProximityDifference: Difference(ctMeanDistanceToSiteB, tMeanDistanceToSiteB),
            EquipmentValueDifference: tSummary.EquipmentValue - ctSummary.EquipmentValue,
            MoneyDifference: tSummary.TotalMoney - ctSummary.TotalMoney,
            ArmorDifference: tSummary.TotalArmor - ctSummary.TotalArmor,
            HelmetCountDifference: tSummary.HelmetCount - ctSummary.HelmetCount,
            DefuserCountDifference: tSummary.DefuserCount - ctSummary.DefuserCount,
            GrenadeCountDifference: tSummary.GrenadeCount - ctSummary.GrenadeCount,
            RifleCountDifference: tSummary.RifleCount - ctSummary.RifleCount,
            SniperCountDifference: tSummary.SniperCount - ctSummary.SniperCount,
            HealthDifference: tSummary.TotalHealth - ctSummary.TotalHealth,
            AliveDifference: tSummary.Alive - ctSummary.Alive,
            TotalAlive: tSummary.Alive + ctSummary.Alive,
            TMeanHealth: MeanHealth(tSummary),
            CTMeanHealth: MeanHealth(ctSummary),
            IsClutch: tSummary.Alive == 1 || ctSummary.Alive == 1,
            TEquipmentCoverage: tEquipmentCoverage,
            CTEquipmentCoverage: ctEquipmentCoverage,
            EquipmentCoverageDifference: tEquipmentCoverage - ctEquipmentCoverage);
    }
    private static double? Difference(double? minuend, double? subtrahend) =>
        minuend is null || subtrahend is null ? null : minuend.Value - subtrahend.Value;

    private static double? SmallestDistance(double? first, double? second) =>
        first is null || second is null ? null : Math.Min(first.Value, second.Value);

    private static double MeanHealth(TeamFeatureSnapshot team) =>
        team.Alive == 0 ? 0 : team.TotalHealth / (double)team.Alive;

    private static double EquipmentCoverage(IReadOnlyList<CurrentPlayer> players, string team)
    {
        var members = players.Where(player => player.Snapshot.Team == team).ToArray();
        return members.Length == 0
            ? 0
            : members.Count(player => player.Equipment is not null) / (double)members.Length;
    }

    private BombTransition FindBombTransition(DemoFrame frame)
    {
        if (!bombTransitionsByRound.TryGetValue(frame.Round.Number, out var transitions))
            return InitialTransition(frame.Tick, frame.Bomb.State);

        var low = 0;
        var high = transitions.Length - 1;
        while (low <= high)
        {
            var middle = low + (high - low) / 2;
            if (transitions[middle].Tick <= frame.Tick)
                low = middle + 1;
            else
                high = middle - 1;
        }
        return high >= 0 ? transitions[high] : InitialTransition(frame.Tick, frame.Bomb.State);
    }

    private MapPoint[]? NormalizedAlivePositions(
        IReadOnlyList<CurrentPlayer> players,
        string team)
    {
        if (mapGeometry is null)
            return null;
        return players
            .Where(player => player.Snapshot.Team == team && player.Snapshot.Alive)
            .Select(player => mapGeometry.Normalize(player.Snapshot.X, player.Snapshot.Y))
            .ToArray();
    }

    private static double? PositionDispersion(IReadOnlyList<MapPoint>? positions)
    {
        if (positions is null)
            return null;
        if (positions.Count <= 1)
            return 0;
        var centerX = positions.Average(point => point.X);
        var centerY = positions.Average(point => point.Y);
        var center = new MapPoint(centerX, centerY);
        return Math.Sqrt(positions.Average(point => SquaredDistance(point, center)));
    }

    private static double? NearestDistance(
        IReadOnlyList<MapPoint>? tPositions,
        IReadOnlyList<MapPoint>? ctPositions)
    {
        if (tPositions is null || ctPositions is null ||
            tPositions.Count == 0 || ctPositions.Count == 0)
            return null;
        return Math.Sqrt(tPositions
            .SelectMany(t => ctPositions.Select(ct => SquaredDistance(t, ct)))
            .Min());
    }

    private static double? MeanDistance(
        IReadOnlyList<MapPoint>? positions,
        MapPoint? target)
    {
        if (positions is null || positions.Count == 0 || target is null)
            return null;
        return positions.Average(position => Math.Sqrt(SquaredDistance(position, target)));
    }

    private static double? MinDistance(
        IReadOnlyList<MapPoint>? positions,
        MapPoint? target)
    {
        if (positions is null || positions.Count == 0 || target is null)
            return null;
        return positions.Min(position => Math.Sqrt(SquaredDistance(position, target)));
    }

    private static double SquaredDistance(MapPoint from, MapPoint to)
    {
        var x = from.X - to.X;
        var y = from.Y - to.Y;
        return x * x + y * y;
    }

    private static BombTransition[] BuildBombTransitions(IEnumerable<DemoFrame> frames)
    {
        var result = new List<BombTransition>();
        string? previousState = null;
        var changeCount = 0;
        var wasDropped = false;
        var wasPlanting = false;
        var wasPlanted = false;
        var wasDefusing = false;
        foreach (var frame in frames.OrderBy(item => item.Tick))
        {
            var state = frame.Bomb.State;
            if (state == previousState)
                continue;
            if (previousState is not null)
                changeCount++;
            wasDropped |= state == "dropped";
            wasPlanting |= state == "planting";
            wasPlanted |= state is "planted" or "defusing";
            wasDefusing |= state == "defusing";
            result.Add(new BombTransition(
                frame.Tick,
                state,
                previousState,
                changeCount,
                wasDropped,
                wasPlanting,
                wasPlanted,
                wasDefusing));
            previousState = state;
        }
        return result.ToArray();
    }

    private static BombTransition InitialTransition(int tick, string state) => new(
        tick,
        state,
        null,
        0,
        state == "dropped",
        state == "planting",
        state is "planted" or "defusing",
        state == "defusing");

    private static PlayerFeatureSnapshot ToPlayerFeature(CurrentPlayer player)
    {
        var snapshot = player.Snapshot;
        var equipment = player.Equipment;
        return new PlayerFeatureSnapshot(
            snapshot.Team,
            snapshot.Alive,
            snapshot.Health,
            snapshot.Region,
            snapshot.Weapon,
            snapshot.Kills,
            snapshot.Deaths,
            snapshot.X,
            snapshot.Y,
            snapshot.Z,
            snapshot.Yaw,
            snapshot.VelocityX,
            snapshot.VelocityY,
            snapshot.VelocityZ,
            equipment?.Money,
            equipment?.Armor,
            equipment?.HasHelmet,
            equipment?.HasDefuser,
            equipment?.CurrentEquipmentValue,
            equipment is null ? null : CountItems(equipment, "grenade"));
    }

    private static TeamFeatureSnapshot SummarizeTeam(
        IReadOnlyList<CurrentPlayer> players,
        string team)
    {
        var members = players.Where(player => player.Snapshot.Team == team).ToArray();
        var knownEquipment = members
            .Select(player => player.Equipment)
            .OfType<PlayerEquipmentState>()
            .ToArray();
        return new TeamFeatureSnapshot(
            members.Count(player => player.Snapshot.Alive),
            members.Sum(player => Math.Max(0, player.Snapshot.Health)),
            members.Sum(player => player.Snapshot.Kills),
            members.Sum(player => player.Snapshot.Deaths),
            knownEquipment.Length,
            knownEquipment.Sum(state => state.Money),
            knownEquipment.Sum(state => state.Armor),
            knownEquipment.Count(state => state.HasHelmet),
            knownEquipment.Count(state => state.HasDefuser),
            knownEquipment.Sum(state => state.CurrentEquipmentValue),
            knownEquipment.Sum(state => CountItems(state, "grenade")),
            knownEquipment.Sum(state => CountItems(state, "rifle")),
            knownEquipment.Sum(state => CountItems(state, "sniper")));
    }

    private static int CountItems(PlayerEquipmentState state, string category) => state.Items
        .Where(item => string.Equals(item.Category, category, StringComparison.Ordinal))
        .Sum(item => item.Count);

    private sealed record BombTransition(
        int Tick,
        string State,
        string? PreviousState,
        int ChangeCount,
        bool WasDropped,
        bool WasPlanting,
        bool WasPlanted,
        bool WasDefusing);

    private sealed record CurrentPlayer(
        PlayerSnapshot Snapshot,
        PlayerEquipmentState? Equipment);
}

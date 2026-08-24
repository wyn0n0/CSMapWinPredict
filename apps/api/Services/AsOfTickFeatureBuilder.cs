using CsDemoMap.Api.Models;

namespace CsDemoMap.Api.Services;

public sealed class AsOfTickFeatureBuilder
{
    private readonly IReadOnlyDictionary<string, PlayerEquipmentState[]> equipmentByPlayer;

    public AsOfTickFeatureBuilder(DemoTimeline timeline)
    {
        equipmentByPlayer = timeline.PlayerEquipmentStates
            .GroupBy(state => state.PlayerId, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.OrderBy(state => state.Tick).ToArray(),
                StringComparer.Ordinal);
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
            SummarizeTeam(currentPlayers, "T"),
            SummarizeTeam(currentPlayers, "CT"),
            players,
            frame.Zones
                .OrderBy(zone => zone.Region, StringComparer.Ordinal)
                .Select(zone => new ZoneFeatureSnapshot(
                    zone.Region,
                    zone.TAlive,
                    zone.CTAlive,
                    zone.TTotal,
                    zone.CTTotal))
                .ToArray());
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

    private sealed record CurrentPlayer(
        PlayerSnapshot Snapshot,
        PlayerEquipmentState? Equipment);
}

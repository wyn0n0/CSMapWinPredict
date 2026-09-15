using System.Text.Json;
using CsDemoMap.Api.Models;

namespace CsDemoMap.Api.Tests;

internal static class SituationSourceVerifier
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public static async Task VerifyAsync(
        string demoPath,
        IReadOnlyList<int> requestedTicks,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(demoPath);
        if (requestedTicks.Count == 0 || requestedTicks.Any(tick => tick < 0))
            throw new ArgumentOutOfRangeException(nameof(requestedTicks));

        var fullPath = Path.GetFullPath(demoPath);
        var fileName = Path.GetFileName(fullPath);
        var parser = new DemoParserService();
        await using var fullStream = File.OpenRead(fullPath);
        var full = await parser.ParseAsync(
            fullStream, fileName, cancellationToken, collectSemantics: true);
        if (!string.Equals(full.Metadata.MapName, "de_mirage", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Source verification only supports de_mirage.");

        var adapter = new SituationInputAdapter();
        var builder = new SituationSceneBuilder();
        var tickResults = new List<PrefixComparison>();
        foreach (var requestedTick in requestedTicks.Distinct().Order())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var windowTicks = DemoImportService.WindowSeconds * full.Metadata.TickRate;
            var windowIndex = requestedTick / windowTicks;
            var fullInput = adapter.BuildFromTimeline(
                full, "source-audit", windowIndex, requestedTick);
            var fullScene = builder.Build(fullInput);
            SituationContractValidator.Validate(fullScene);

            await using var prefixStream = File.OpenRead(fullPath);
            var prefix = await new DemoParserService().ParseAsync(
                prefixStream,
                fileName,
                cancellationToken,
                collectSemantics: true,
                stopAfterTick: requestedTick);
            var prefixInput = adapter.BuildFromTimeline(
                prefix, "source-audit", windowIndex, requestedTick,
                SceneObservationBoundary.ArtificialPrefixEnd);
            var prefixScene = builder.Build(prefixInput);
            SituationContractValidator.Validate(prefixScene);

            var fullCanonical = SituationCanonicalJson.Serialize(fullScene);
            var prefixCanonical = SituationCanonicalJson.Serialize(prefixScene);
            if (!string.Equals(fullCanonical, prefixCanonical, StringComparison.Ordinal))
                throw new InvalidDataException(
                    $"Full and prefix scene differ at requested tick {requestedTick}.");
            if (prefix.UtilityTracks.SelectMany(track => track.Trajectory)
                .Any(point => point.Tick > prefixInput.Tick))
                throw new InvalidDataException("Prefix projectile trajectory contains a future point.");
            if (prefix.UtilityEffects.SelectMany(effect => effect.Samples)
                .Any(sample => sample.Tick > prefixInput.Tick))
                throw new InvalidDataException("Prefix effect history contains a future sample.");
            if (prefixInput.ActiveUtilities.Any(item =>
                    item.ObservationBoundary != SceneObservationBoundary.ArtificialPrefixEnd) ||
                prefixInput.ActiveEffects.Any(item =>
                    item.ObservationBoundary != SceneObservationBoundary.ArtificialPrefixEnd))
                throw new InvalidDataException("Prefix lifecycle observations lost their artificial-boundary provenance.");

            var lastParsedTick = prefix.Frames.Count == 0 ? 0 : prefix.Frames.Max(frame => frame.Tick);
            tickResults.Add(new PrefixComparison(
                requestedTick,
                fullInput.Tick,
                fullInput.ActiveUtilities.Count,
                fullInput.ActiveUtilities.Select(item => item.Type).Order(StringComparer.Ordinal).ToArray(),
                fullInput.ActiveEffects.Count,
                fullInput.ActiveEffects.Select(item => item.Type).Order(StringComparer.Ordinal).ToArray(),
                full.UtilityTracks
                    .Where(track => track.StartTick <= fullInput.Tick && track.EndTick > fullInput.Tick)
                    .SelectMany(track => track.Trajectory)
                    .Count(point => point.Tick > fullInput.Tick),
                full.UtilityEffects
                    .Where(effect => effect.StartTick <= fullInput.Tick && effect.EndTick > fullInput.Tick)
                    .SelectMany(effect => effect.Samples)
                    .Count(sample => sample.Tick > fullInput.Tick),
                prefix.UtilityTracks.Count(track =>
                    track.StartTick <= prefixInput.Tick &&
                    track.EndTick > lastParsedTick),
                prefix.UtilityEffects.Count(effect =>
                    effect.StartTick <= prefixInput.Tick &&
                    effect.EndTick > lastParsedTick),
                SituationCanonicalJson.Sha256(fullScene),
                true));
        }

        var players = full.Frames
            .SelectMany(frame => frame.Players.Select(player => new { Frame = frame, Player = player }))
            .Where(item => item.Player.Team is "T" or "CT")
            .ToArray();
        var firstRoundPlayerSnapshots = players
            .GroupBy(item => new { item.Frame.Round.Number, item.Player.Id })
            .Select(group => group.OrderBy(item => item.Frame.Tick).First().Player)
            .ToArray();
        var equipment = full.PlayerEquipmentStates;
        var bombFrames = full.Frames.Select(frame => frame.Bomb).ToArray();
        var result = new SourceAudit(
            fileName,
            full.Metadata.TotalTicks,
            full.Frames.Count,
            full.Semantics?.Frames.Count ?? 0,
            new DefaultValueAudit(
                equipment.Count,
                equipment.Count(item => item.Money == 0),
                equipment.Count(item => item.Armor == 0),
                equipment.Count(item => item.CurrentEquipmentValue == 0),
                equipment.Count(item => item.CashSpentThisRound == 0),
                players.Length,
                players.Count(item => IsZeroVelocity(item.Player)),
                firstRoundPlayerSnapshots.Length,
                firstRoundPlayerSnapshots.Count(IsZeroVelocity),
                false,
                false),
            new RegionAndBombAudit(
                players.Count(item => IsUnknownRegion(item.Player.Region)),
                bombFrames.Count(item => item.State == "unavailable"),
                bombFrames.Count(item => IsUnknownRegion(item.Region)),
                bombFrames.Count(item => item.State == "dropped" && !IsUnknownRegion(item.Region)),
                false,
                bombFrames.Count(item => item.SecondsToExplosion is not null),
                bombFrames.Count(item => item.SecondsToDefuse is not null)),
            new UtilitySourceAudit(
                full.PlayerUtilityStates.Count,
                full.PlayerUtilityStates.Count(item => item.Items.Count == 0),
                CountTypes(full.UtilityTracks.Select(item => item.Type)),
                CountTypes(full.UtilityEffects.Select(item => item.Type))),
            tickResults);
        Console.WriteLine(JsonSerializer.Serialize(result, JsonOptions));
    }

    private static bool IsZeroVelocity(PlayerSnapshot player) =>
        player.VelocityX == 0 && player.VelocityY == 0 && player.VelocityZ == 0;

    private static bool IsUnknownRegion(string? region) =>
        string.IsNullOrWhiteSpace(region) || string.Equals(region, "unknown", StringComparison.Ordinal);

    private static IReadOnlyDictionary<string, int> CountTypes(IEnumerable<string> values) => values
        .GroupBy(value => value, StringComparer.Ordinal)
        .OrderBy(group => group.Key, StringComparer.Ordinal)
        .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);

    private sealed record SourceAudit(
        string Demo,
        int TotalTicks,
        int FrameCount,
        int SemanticFrameCount,
        DefaultValueAudit DefaultValues,
        RegionAndBombAudit RegionsAndBomb,
        UtilitySourceAudit Utilities,
        IReadOnlyList<PrefixComparison> PrefixComparisons);

    private sealed record DefaultValueAudit(
        int EquipmentObservationCount,
        int MoneyZeroCount,
        int ArmorZeroCount,
        int CurrentEquipmentValueZeroCount,
        int CashSpentThisRoundZeroCount,
        int PlayerSnapshotCount,
        int ZeroVelocitySnapshotCount,
        int FirstRoundPlayerSnapshotCount,
        int FirstRoundPlayerZeroVelocityCount,
        bool EquipmentZerosHaveKnownMarker,
        bool VelocityZerosHaveKnownMarker);

    private sealed record RegionAndBombAudit(
        int UnknownPlayerRegionCount,
        int BombUnavailableFrameCount,
        int UnknownBombRegionCount,
        int DroppedBombRegionWithoutSourceMarkerCount,
        bool SourceBombRegionHasProvenanceMarker,
        int ExplosionCountdownKnownCount,
        int DefuseCountdownKnownCount);

    private sealed record UtilitySourceAudit(
        int InventoryObservationCount,
        int EmptyInventoryObservationCount,
        IReadOnlyDictionary<string, int> ProjectileTrackCountByType,
        IReadOnlyDictionary<string, int> EffectTrackCountByType);

    private sealed record PrefixComparison(
        int RequestedTick,
        int ActualTick,
        int ActiveProjectileCount,
        IReadOnlyList<string> ActiveProjectileTypes,
        int ActiveEffectCount,
        IReadOnlyList<string> ActiveEffectTypes,
        int FullParseFutureProjectilePointCount,
        int FullParseFutureEffectSampleCount,
        int PrefixProjectileArtificiallyOpenCount,
        int PrefixEffectArtificiallyOpenCount,
        string SceneSha256,
        bool CanonicalSceneMatches);
}

using System.Text.Json;
using System.Text.RegularExpressions;
using CsDemoMap.Api.Models;

namespace CsDemoMap.Api.Services;

internal static class SituationContractValidator
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly IReadOnlySet<string> KnownQualityCodes = new HashSet<string>(StringComparer.Ordinal)
    {
        SituationDataQualityCodes.MissingFrame,
        SituationDataQualityCodes.StaleFrame,
        SituationDataQualityCodes.HistoryTruncated,
        SituationDataQualityCodes.HistoryFileBoundary,
        SituationDataQualityCodes.HistoryRoundBoundary,
        SituationDataQualityCodes.HistoryGap,
        SituationDataQualityCodes.ClockUnknown,
        SituationDataQualityCodes.RosterIncomplete,
        SituationDataQualityCodes.EquipmentUnknown,
        SituationDataQualityCodes.UtilityUnknown,
        SituationDataQualityCodes.PositionInvalid,
        SituationDataQualityCodes.PositionOutsideRadar,
        SituationDataQualityCodes.RegionUnknown,
        SituationDataQualityCodes.FloorUnknown,
        SituationDataQualityCodes.EffectStateIncomplete,
        SituationDataQualityCodes.LegacyDefaultAmbiguous,
        SituationDataQualityCodes.HealthStateConflict
    };
    private static readonly string[] ForbiddenNarrativeTerms =
    [
        "steam", ".dem", "胜率", "将会", "会赢", "建议", "应该", "应当", "%"
    ];

    public static void Validate(MinimapSceneV1 scene)
    {
        var errors = new List<string>();
        Require(scene.SchemaVersion == SituationContractVersions.Scene, "scene schemaVersion mismatch", errors);
        Require(scene.SceneBuilderVersion == SituationSceneBuilder.BuilderVersion,
            "sceneBuilderVersion mismatch", errors);
        Require(scene.GeometryVersion == SituationSceneBuilder.GeometryVersion,
            "geometryVersion mismatch", errors);
        Require(scene.Source == "structured", "scene source must be structured", errors);
        Require(scene.Map == "de_mirage", "scene map must be de_mirage", errors);
        Require(!string.IsNullOrWhiteSpace(scene.DemoRef), "scene demoRef is empty", errors);
        Require(scene.Tick <= scene.RequestedTick, "scene tick exceeds requestedTick", errors);
        Require(scene.TickRate > 0, "scene tickRate must be positive", errors);
        Require(scene.WindowIndex >= 0, "scene windowIndex is negative", errors);
        ValidateRound(scene.Round, errors);

        var slots = scene.Players.Select(player => player.Slot).ToArray();
        Require(scene.Players.Count <= 10, "scene has more than ten players", errors);
        Require(scene.Players.Count(player => player.Side == SituationSide.T) <= 5,
            "scene has more than five T players", errors);
        Require(scene.Players.Count(player => player.Side == SituationSide.CT) <= 5,
            "scene has more than five CT players", errors);
        Require(slots.Distinct(StringComparer.Ordinal).Count() == slots.Length,
            "player slots are not unique", errors);
        for (var index = 0; index < scene.Players.Count; index++)
            ValidatePlayer(scene, scene.Players[index], index, errors);

        ValidateTeam(scene, SituationSide.T, scene.Teams.T, errors);
        ValidateTeam(scene, SituationSide.CT, scene.Teams.CT, errors);
        ValidateBomb(scene, slots, errors);
        ValidateUtilities(scene, slots, errors);
        ValidateEffects(scene, errors);
        ValidateGeometry(scene, slots, errors);
        ValidateQuality(scene.DataQuality, errors);
        ThrowIfErrors(errors);
    }

    public static void Validate(SituationFactsV1 facts)
    {
        var errors = new List<string>();
        ValidateFacts(facts, errors);
        ThrowIfErrors(errors);
    }

    public static void Validate(SituationFactsV1 facts, MinimapSceneV1 scene)
        => Validate(facts, scene, null);

    public static void Validate(
        SituationFactsV1 facts,
        MinimapSceneV1 scene,
        string? expectedAnalysisRuleVersion)
    {
        var errors = new List<string>();
        ValidateFacts(facts, errors);
        if (expectedAnalysisRuleVersion is not null)
            Require(facts.AnalysisRuleVersion == expectedAnalysisRuleVersion,
                "analysisRuleVersion differs from loaded rules", errors);
        Require(facts.Alive.T == scene.Teams.T.Alive && facts.Alive.CT == scene.Teams.CT.Alive,
            "facts alive values differ from scene", errors);
        Require(facts.TotalHealth.T == scene.Teams.T.TotalHealth &&
                facts.TotalHealth.CT == scene.Teams.CT.TotalHealth,
            "facts totalHealth values differ from scene", errors);
        Require(facts.Bomb.State == scene.Bomb.State && facts.Bomb.Site == scene.Bomb.Site,
            "facts bomb values differ from scene", errors);
        using var source = JsonSerializer.SerializeToDocument(scene, JsonOptions);
        foreach (var evidence in facts.Evidence)
            foreach (var path in evidence.SourcePaths)
                Require(JsonPointerExists(source.RootElement, path),
                    $"evidence {evidence.Id} references missing source path {path}", errors);
        ThrowIfErrors(errors);
    }

    public static void Validate(SituationNarrativeV1 narrative, IReadOnlySet<string> evidenceIds) =>
        ValidateNarrative(narrative, evidenceIds, null);

    public static void ValidateTemplate(SituationNarrativeV1 narrative, SituationFactsV1 facts)
    {
        ValidateNarrative(
            narrative,
            facts.Evidence.Select(item => item.Id).ToHashSet(StringComparer.Ordinal),
            facts);
        var evidence = facts.Evidence.ToDictionary(item => item.Id, StringComparer.Ordinal);
        var errors = new List<string>();
        foreach (var highlight in narrative.Highlights)
            Require(highlight.EvidenceIds.Any(id =>
                    evidence.TryGetValue(id, out var item) && item.TextZh == highlight.TextZh),
                "template highlight is not verbatim supported by its evidence", errors);
        ThrowIfErrors(errors);
    }

    public static void Validate(SituationAnalysisV1 analysis) => ValidateAnalysis(analysis, null);

    public static void Validate(SituationAnalysisV1 analysis, MinimapSceneV1 scene) =>
        ValidateAnalysis(analysis, scene);

    private static void ValidateRound(SituationRound round, ICollection<string> errors)
    {
        Require(Enum.IsDefined(round.Phase), "round phase is invalid", errors);
        Require(round.Number is null or >= 1, "round number is invalid", errors);
        Require(round.SegmentId is null or >= 0, "round segmentId is invalid", errors);
        Require(IsFiniteNonNegative(round.ElapsedSeconds), "round elapsedSeconds is invalid", errors);
        Require(IsFiniteNonNegative(round.RemainingSeconds), "round remainingSeconds is invalid", errors);
        Require(round.Score.T is null or >= 0, "round T score is invalid", errors);
        Require(round.Score.CT is null or >= 0, "round CT score is invalid", errors);
        if (round.Ref is { } roundRef)
        {
            Require(Regex.IsMatch(roundRef, "^s[0-9]+-a[1-9][0-9]*$", RegexOptions.CultureInvariant),
                "round ref is not an anonymous attempt reference", errors);
            if (round.SegmentId is { } segmentId)
                Require(roundRef.StartsWith($"s{segmentId}-", StringComparison.Ordinal),
                    "round ref does not match segmentId", errors);
        }
    }

    private static void ValidatePlayer(
        MinimapSceneV1 scene,
        SituationPlayer player,
        int index,
        ICollection<string> errors)
    {
        Require(IsSlotForSide(player.Slot, player.Side), $"slot {player.Slot} does not match side", errors);
        Require(player.Health is >= 0 and <= 100, $"player {player.Slot} health is invalid", errors);
        if (player.Health == 0)
            Require(HasQuality(scene.DataQuality, SituationDataQualityCodes.HealthStateConflict,
                    $"/players/{index}/health", SituationQualitySeverity.Error),
                $"player {player.Slot} is listed alive with zero health and no conflict quality", errors);
        Require(player.Armor is null or >= 0 and <= 100, $"player {player.Slot} armor is invalid", errors);
        Require(player.Region is not null || player.RegionSource == SituationRegionSource.Unknown,
            $"player {player.Slot} has a region source without a region", errors);
        Require(player.Region is null || player.RegionSource != SituationRegionSource.Unknown,
            $"player {player.Slot} region has unknown provenance", errors);
        ValidateFinite(player.Position, $"player {player.Slot} position", errors);
        ValidateFinite(player.Velocity, $"player {player.Slot} velocity", errors);
        if (player.Heading is { } heading)
        {
            Require(double.IsFinite(heading.X) && double.IsFinite(heading.Y),
                $"player {player.Slot} heading is non-finite", errors);
            var magnitude = Math.Sqrt(heading.X * heading.X + heading.Y * heading.Y);
            Require(Math.Abs(magnitude - 1) <= 0.00001,
                $"player {player.Slot} heading is not a unit vector", errors);
        }
        ValidateUtilityCounts(player.UtilityCounts, $"player {player.Slot}", errors);
        ValidateTrajectory(player.Trajectory, scene.Tick, scene.TickRate, $"player {player.Slot}", errors);
    }

    private static void ValidateBomb(
        MinimapSceneV1 scene,
        IReadOnlyCollection<string> slots,
        ICollection<string> errors)
    {
        var bomb = scene.Bomb;
        ValidateSlotReference(bomb.CarrierSlot, SituationSide.T, slots, "bomb carrier", errors);
        ValidateSlotReference(bomb.DefuserSlot, SituationSide.CT, slots, "bomb defuser", errors);
        Require(bomb.CarrierSlot is null || bomb.State is SituationBombState.Carried or SituationBombState.Planting,
            "bomb carrier is incompatible with bomb state", errors);
        Require(bomb.DefuserSlot is null || bomb.State == SituationBombState.Defusing,
            "bomb defuser is incompatible with bomb state", errors);
        Require(bomb.Site is null || bomb.State is SituationBombState.Planted or SituationBombState.Defusing or
                SituationBombState.Defused or SituationBombState.Exploded,
            "bomb site is present before a site was observed", errors);
        Require(bomb.SecondsToExplosion is null || bomb.State is
                SituationBombState.Planted or SituationBombState.Defusing,
            "explosion timer is incompatible with bomb state", errors);
        Require(bomb.SecondsToDefuse is null || bomb.State == SituationBombState.Defusing,
            "defuse timer is incompatible with bomb state", errors);
        Require(IsFiniteNonNegative(bomb.SecondsToExplosion), "bomb explosion timer is invalid", errors);
        Require(IsFiniteNonNegative(bomb.SecondsToDefuse), "bomb defuse timer is invalid", errors);
        Require(bomb.Region is not null || bomb.RegionSource == SituationRegionSource.Unknown,
            "bomb has a region source without a region", errors);
        ValidateFinite(bomb.Position, "bomb position", errors);
        if (bomb.State == SituationBombState.Unknown)
            Require(bomb.CarrierSlot is null && bomb.DefuserSlot is null && bomb.Site is null &&
                    bomb.Position is null && bomb.Region is null && bomb.SecondsToExplosion is null &&
                    bomb.SecondsToDefuse is null,
                "unknown bomb state contains asserted details", errors);
    }

    private static void ValidateUtilities(
        MinimapSceneV1 scene,
        IReadOnlyCollection<string> slots,
        ICollection<string> errors)
    {
        Require(scene.Utilities.Select(item => item.Id).Distinct(StringComparer.Ordinal).Count() == scene.Utilities.Count,
            "utility IDs are not unique", errors);
        foreach (var utility in scene.Utilities)
        {
            Require(Regex.IsMatch(utility.Id, "^utility-[1-9][0-9]*$", RegexOptions.CultureInvariant),
                $"utility ID {utility.Id} is not a local anonymous reference", errors);
            Require(utility.ObservedStartTick <= scene.Tick, $"utility {utility.Id} starts in the future", errors);
            if (utility.ThrowerSlot is { } thrower)
            {
                Require(slots.Contains(thrower, StringComparer.Ordinal),
                    $"utility {utility.Id} has unknown thrower", errors);
                Require(utility.Side is not null && IsSlotForSide(thrower, utility.Side.Value),
                    $"utility {utility.Id} thrower and side disagree", errors);
            }
            ValidateFinite(utility.Position, $"utility {utility.Id} position", errors);
            ValidateTrajectory(utility.Trajectory, scene.Tick, scene.TickRate, $"utility {utility.Id}", errors);
            if (utility.Trajectory.Points.Count > 0)
                Require(Equal(utility.Position, utility.Trajectory.Points[^1].Position),
                    $"utility {utility.Id} position is not its latest trajectory point", errors);
        }
    }

    private static void ValidateEffects(MinimapSceneV1 scene, ICollection<string> errors)
    {
        Require(scene.Effects.Select(item => item.Id).Distinct(StringComparer.Ordinal).Count() == scene.Effects.Count,
            "effect IDs are not unique", errors);
        for (var index = 0; index < scene.Effects.Count; index++)
        {
            var effect = scene.Effects[index];
            Require(Regex.IsMatch(effect.Id, "^effect-[1-9][0-9]*$", RegexOptions.CultureInvariant),
                $"effect ID {effect.Id} is not a local anonymous reference", errors);
            Require(effect.ObservedStartTick <= effect.SampleTick && effect.SampleTick <= scene.Tick,
                $"effect {effect.Id} has invalid temporal bounds", errors);
            Require(IsFiniteNonNegative(effect.Radius), $"effect {effect.Id} radius is invalid", errors);
            ValidateFinite(effect.Position, $"effect {effect.Id} position", errors);
            if (effect.Area is { } area)
            {
                Require(area.Count > 0, $"effect {effect.Id} uses an empty area for unknown shape", errors);
                foreach (var point in area)
                    ValidateFinite(point, $"effect {effect.Id} area", errors);
            }
            if (effect.Type == SituationEffectType.Fire && effect.Area is null)
                Require(HasQuality(scene.DataQuality, SituationDataQualityCodes.EffectStateIncomplete,
                        "/effects"),
                    $"fire effect {effect.Id} has unknown area without quality marker", errors);
        }
    }

    private static void ValidateGeometry(
        MinimapSceneV1 scene,
        IReadOnlyList<string> slots,
        ICollection<string> errors)
    {
        var expectedOccupancy = scene.Players
            .Where(player => player.Region is not null)
            .GroupBy(player => player.Region!, StringComparer.Ordinal)
            .Select(group => new SituationRegionOccupancy(
                group.Key,
                group.Count(player => player.Side == SituationSide.T),
                group.Count(player => player.Side == SituationSide.CT)))
            .OrderBy(item => item.Region, StringComparer.Ordinal)
            .ToArray();
        Require(scene.Geometry.RegionOccupancy.SequenceEqual(expectedOccupancy),
            "region occupancy is not reproducible from current players", errors);
        Require(scene.Geometry.PlayerDistances.Select(item => item.Slot)
                .SequenceEqual(slots.OrderBy(slot => slot, StringComparer.Ordinal), StringComparer.Ordinal),
            "geometry player distance slots do not match players", errors);
        foreach (var item in scene.Geometry.PlayerDistances)
        {
            Require(IsFiniteNonNegative(item.SiteA) && IsFiniteNonNegative(item.SiteB) &&
                    IsFiniteNonNegative(item.NearestAlly) && IsFiniteNonNegative(item.NearestEnemy),
                $"geometry distance is invalid for {item.Slot}", errors);
            var player = scene.Players.Single(player => player.Slot == item.Slot);
            var mapGeometry = MapFeatureGeometries.Find(scene.Map)!;
            var allies = scene.Players.Where(other => other.Side == player.Side && other.Slot != player.Slot);
            var enemies = scene.Players.Where(other => other.Side != player.Side);
            Require(Equal(item.SiteA, Distance(player.Position, mapGeometry.Normalize(
                    mapGeometry.SiteA.X, mapGeometry.SiteA.Y))),
                $"siteA distance is not reproducible for {item.Slot}", errors);
            Require(Equal(item.SiteB, Distance(player.Position, mapGeometry.Normalize(
                    mapGeometry.SiteB.X, mapGeometry.SiteB.Y))),
                $"siteB distance is not reproducible for {item.Slot}", errors);
            Require(Equal(item.NearestAlly, NearestDistance(player, allies)),
                $"nearestAlly is not reproducible for {item.Slot}", errors);
            Require(Equal(item.NearestEnemy, NearestDistance(player, enemies)),
                $"nearestEnemy is not reproducible for {item.Slot}", errors);
        }
    }

    private static void ValidateFacts(SituationFactsV1 facts, ICollection<string> errors)
    {
        Require(facts.SchemaVersion == SituationContractVersions.Facts, "facts schemaVersion mismatch", errors);
        Require(!string.IsNullOrWhiteSpace(facts.AnalysisRuleVersion), "analysisRuleVersion is empty", errors);
        ValidateSideRange(facts.Alive, 5, "alive", errors);
        ValidateSideRange(facts.TotalHealth, 500, "totalHealth", errors);
        Require((facts.Pressure.Side is null) == (facts.Pressure.Site is null),
            "pressure side and site must both be known or both be null", errors);
        if (facts.ContestedRegions is { } contested)
        {
            Require(contested.All(value => !string.IsNullOrWhiteSpace(value)),
                "contestedRegions contains an empty region", errors);
            Require(contested.Distinct(StringComparer.Ordinal).Count() == contested.Count,
                "contestedRegions contains duplicates", errors);
        }
        Require(facts.Evidence.Select(item => item.Id).Distinct(StringComparer.Ordinal).Count() == facts.Evidence.Count,
            "evidence IDs are not unique", errors);
        foreach (var evidence in facts.Evidence)
        {
            Require(IsSemanticId(evidence.Id), $"evidence ID {evidence.Id} is not stable semantic form", errors);
            Require(!ContainsSensitiveIdentity(evidence.Id), $"evidence ID {evidence.Id} contains identity data", errors);
            Require(evidence.SourcePaths.Count > 0 && evidence.SourcePaths.All(IsJsonPointer),
                $"evidence {evidence.Id} has invalid source paths", errors);
            Require(evidence.SourcePaths.Distinct(StringComparer.Ordinal).Count() == evidence.SourcePaths.Count,
                $"evidence {evidence.Id} has duplicate source paths", errors);
            Require(IsRuleId(evidence.RuleId), $"evidence {evidence.Id} ruleId is invalid", errors);
            Require(!string.IsNullOrWhiteSpace(evidence.TextZh) && ContainsCjk(evidence.TextZh),
                $"evidence {evidence.Id} text is not non-empty Chinese text", errors);
        }
        ValidateEvidenceBindings(facts, errors);
        ValidateQuality(facts.DataQuality, errors);
    }

    private static void ValidateEvidenceBindings(SituationFactsV1 facts, ICollection<string> errors)
    {
        var ids = facts.Evidence.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        RequireEvidence(facts.Alive.T is not null, "alive.t", ids, errors);
        RequireEvidence(facts.Alive.CT is not null, "alive.ct", ids, errors);
        RequireEvidence(facts.TotalHealth.T is not null, "total-health.t", ids, errors);
        RequireEvidence(facts.TotalHealth.CT is not null, "total-health.ct", ids, errors);
        RequireEvidence(facts.Bomb.State != SituationBombState.Unknown, "bomb.state", ids, errors);
        RequireEvidence(facts.Bomb.Site is not null, "bomb.site", ids, errors);
        RequireEvidence(facts.Formation.T != SituationFormation.Unknown, "formation.t", ids, errors);
        RequireEvidence(facts.Formation.CT != SituationFormation.Unknown, "formation.ct", ids, errors);
        RequireEvidence(facts.Pressure.Side is not null, "pressure.side", ids, errors);
        RequireEvidence(facts.Pressure.Site is not null, "pressure.site", ids, errors);
        RequireEvidence(facts.ContestedRegions is not null, "contested-regions", ids, errors);
        RequireEvidence(facts.ContactRisk != SituationContactRisk.Unknown, "contact-risk", ids, errors);
        RequireEvidence(facts.IsolatedSide != SituationIsolatedSide.Unknown, "isolated-side", ids, errors);
        RequireEvidence(facts.SpatialAdvantage != SituationSpatialAdvantage.Uncertain,
            "spatial-advantage", ids, errors);
        RequireEvidence(true, "confidence", ids, errors);
    }

    private static void ValidateNarrative(
        SituationNarrativeV1 narrative,
        IReadOnlySet<string> evidenceIds,
        SituationFactsV1? facts)
    {
        var errors = new List<string>();
        Require(narrative.SchemaVersion == SituationContractVersions.Narrative,
            "narrative schemaVersion mismatch", errors);
        Require(narrative.Highlights.Count is >= 2 and <= 4, "narrative must have 2-4 highlights", errors);
        Require(!string.IsNullOrWhiteSpace(narrative.SummaryZh) && narrative.SummaryZh.Length <= 240 &&
                ContainsCjk(narrative.SummaryZh),
            "summaryZh is empty, non-Chinese, or exceeds 240 characters", errors);
        ValidateNarrativeText(narrative.SummaryZh, "summaryZh", errors);
        foreach (var highlight in narrative.Highlights)
        {
            Require(!string.IsNullOrWhiteSpace(highlight.TextZh) && ContainsCjk(highlight.TextZh),
                "highlight text is not non-empty Chinese text", errors);
            Require(highlight.EvidenceIds.Count > 0, "highlight has no evidence IDs", errors);
            Require(highlight.EvidenceIds.Distinct(StringComparer.Ordinal).Count() == highlight.EvidenceIds.Count,
                "highlight has duplicate evidence IDs", errors);
            Require(highlight.EvidenceIds.All(evidenceIds.Contains), "highlight references unknown evidence", errors);
            ValidateNarrativeText(highlight.TextZh, "highlight", errors);
        }
        Require(narrative.Uncertainties.All(value => !string.IsNullOrWhiteSpace(value) && ContainsCjk(value)),
            "uncertainties contains empty or non-Chinese text", errors);
        Require(narrative.Uncertainties.Distinct(StringComparer.Ordinal).Count() == narrative.Uncertainties.Count,
            "uncertainties contains duplicates", errors);
        foreach (var uncertainty in narrative.Uncertainties)
            ValidateNarrativeText(uncertainty, "uncertainty", errors);
        if (facts is not null && narrative.Uncertainties.Count > 0)
            Require(HasUncertaintySupport(facts),
                "narrative uncertainties have no facts quality or unknown-field support", errors);
        ThrowIfErrors(errors);
    }

    private static void ValidateAnalysis(SituationAnalysisV1 analysis, MinimapSceneV1? scene)
    {
        var errors = new List<string>();
        Require(analysis.SchemaVersion == SituationContractVersions.Analysis,
            "analysis schemaVersion mismatch", errors);
        Require(analysis.Tick <= analysis.RequestedTick, "analysis tick exceeds requestedTick", errors);
        Require(analysis.Versions.Scene == SituationContractVersions.Scene, "scene version metadata mismatch", errors);
        Require(analysis.Versions.Builder == SituationSceneBuilder.BuilderVersion,
            "builder version metadata mismatch", errors);
        Require(analysis.Versions.Geometry == SituationSceneBuilder.GeometryVersion,
            "geometry version metadata mismatch", errors);
        Require(analysis.Versions.Facts == analysis.Facts.SchemaVersion, "facts version metadata mismatch", errors);
        Require(analysis.Versions.Rules == analysis.Facts.AnalysisRuleVersion,
            "rules version metadata mismatch", errors);
        Require(analysis.Versions.Narrative == analysis.Narrative.SchemaVersion,
            "narrative version metadata mismatch", errors);
        Require(analysis.NarrativeSource == SituationNarrativeSource.Lora || analysis.Model is null,
            "template narrative must not include model metadata", errors);
        Require(analysis.NarrativeSource != SituationNarrativeSource.Lora || analysis.Model is not null,
            "LoRA narrative has no model metadata", errors);
        if (analysis.Model is { } model)
        {
            Require(!string.IsNullOrWhiteSpace(model.BaseRevision), "model baseRevision is empty", errors);
            Require(model.AdapterSha256.Length == 64 && model.AdapterSha256.All(IsLowerHex),
                "adapter SHA-256 is invalid", errors);
        }
        if (scene is not null)
        {
            Require(analysis.RequestedTick == scene.RequestedTick, "analysis requestedTick differs from scene", errors);
            Require(analysis.Tick == scene.Tick, "analysis tick differs from scene", errors);
            Require(analysis.Versions.Scene == scene.SchemaVersion, "analysis scene version differs from scene", errors);
            Require(analysis.Versions.Builder == scene.SceneBuilderVersion,
                "analysis builder version differs from scene", errors);
            Require(analysis.Versions.Geometry == scene.GeometryVersion,
                "analysis geometry version differs from scene", errors);
        }
        ThrowIfErrors(errors);
        if (scene is null)
            Validate(analysis.Facts);
        else
            Validate(analysis.Facts, scene);
        ValidateNarrative(
            analysis.Narrative,
            analysis.Facts.Evidence.Select(item => item.Id).ToHashSet(StringComparer.Ordinal),
            analysis.Facts);
    }

    private static void ValidateTeam(
        MinimapSceneV1 scene,
        SituationSide side,
        SituationTeamSummary team,
        ICollection<string> errors)
    {
        var label = side == SituationSide.T ? "T" : "CT";
        var players = scene.Players.Where(player => player.Side == side).ToArray();
        ValidateUtilityCounts(team.UtilityCounts, $"{label} team", errors);
        var weaponValues = WeaponCountValues(team.WeaponCounts);
        Require(AllNullOrAllKnown(weaponValues), $"{label} weapon summary is partially known", errors);
        if (team.Alive is null)
        {
            Require(team.TotalHealth is null && team.TotalArmor is null && team.TotalMoney is null &&
                    team.EquipmentValue is null && team.HelmetCount is null && team.DefuserCount is null &&
                    UtilityCountValues(team.UtilityCounts).All(value => value is null) &&
                    weaponValues.All(value => value is null),
                $"{label} incomplete roster contains partial team totals", errors);
            return;
        }

        Require(team.Alive is >= 0 and <= 5 && team.Alive == players.Length,
            $"{label} alive summary does not match players", errors);
        if (team.TotalHealth is { } health)
            Require(players.All(player => player.Health is not null) && health == players.Sum(player => player.Health),
                $"{label} health summary does not match players", errors);
        if (team.TotalArmor is { } armor)
            Require(players.All(player => player.Armor is not null) && armor == players.Sum(player => player.Armor),
                $"{label} armor summary does not match players", errors);
        Require(team.TotalMoney is null or >= 0, $"{label} totalMoney is invalid", errors);
        Require(team.EquipmentValue is null or >= 0, $"{label} equipmentValue is invalid", errors);
        Require(team.HelmetCount is null || team.HelmetCount is >= 0 && team.HelmetCount <= team.Alive,
            $"{label} helmetCount is invalid", errors);
        Require(team.DefuserCount is null || team.DefuserCount is >= 0 && team.DefuserCount <= team.Alive,
            $"{label} defuserCount is invalid", errors);
        var teamUtility = UtilityCountValues(team.UtilityCounts);
        if (teamUtility.All(value => value is not null))
        {
            Require(players.All(player => UtilityCountValues(player.UtilityCounts).All(value => value is not null)),
                $"{label} utility total is known while a player inventory is unknown", errors);
            for (var index = 0; index < teamUtility.Count; index++)
                Require(teamUtility[index] == players.Sum(player => UtilityCountValues(player.UtilityCounts)[index]),
                    $"{label} utility summary does not match players", errors);
        }
        if (weaponValues.All(value => value is not null))
            Require(WeaponCountTotal(team.WeaponCounts) == players.Length,
                $"{label} weapon summary does not match players", errors);
    }

    private static void ValidateTrajectory(
        SituationTrajectory trajectory,
        int sceneTick,
        int tickRate,
        string label,
        ICollection<string> errors)
    {
        Require(trajectory.FromTick <= trajectory.ToTick, $"{label} trajectory range is reversed", errors);
        Require(trajectory.ToTick <= sceneTick, $"{label} trajectory reads beyond scene tick", errors);
        Require(double.IsFinite(trajectory.CoverageSeconds) &&
                trajectory.CoverageSeconds is >= 0 and <= SituationInputAdapter.HistorySeconds,
            $"{label} trajectory coverage is invalid", errors);
        Require(Equal(trajectory.CoverageSeconds,
                Math.Round((trajectory.ToTick - trajectory.FromTick) / (double)tickRate,
                    6, MidpointRounding.AwayFromZero)),
            $"{label} trajectory coverage does not match ticks", errors);
        Require(trajectory.Points.All(point =>
                point.Tick >= trajectory.FromTick && point.Tick <= trajectory.ToTick && point.Tick <= sceneTick),
            $"{label} has an out-of-range trajectory point", errors);
        Require(IsAscending(trajectory.Points.Select(point => point.Tick)),
            $"{label} trajectory is not ordered", errors);
        foreach (var point in trajectory.Points)
            ValidateFinite(point.Position, $"{label} trajectory position", errors);
        if (trajectory.Points.Count > 0)
        {
            Require(trajectory.Points[0].Tick == trajectory.FromTick,
                $"{label} trajectory fromTick differs from first point", errors);
            Require(trajectory.Points[^1].Tick == trajectory.ToTick,
                $"{label} trajectory toTick differs from last point", errors);
        }
    }

    private static void ValidateUtilityCounts(
        SituationUtilityCounts counts,
        string label,
        ICollection<string> errors)
    {
        var values = UtilityCountValues(counts);
        Require(values.All(value => value is null or >= 0), $"{label} utility count is invalid", errors);
        Require(AllNullOrAllKnown(values), $"{label} utility counts are partially known", errors);
    }

    private static void ValidateSideRange(
        SituationSideValues<int?> values,
        int maximum,
        string label,
        ICollection<string> errors)
    {
        Require(values.T is null || values.T is >= 0 && values.T <= maximum,
            $"{label}.t is invalid", errors);
        Require(values.CT is null || values.CT is >= 0 && values.CT <= maximum,
            $"{label}.ct is invalid", errors);
    }

    private static void ValidateQuality(
        IReadOnlyList<SituationDataQuality> quality,
        ICollection<string> errors)
    {
        foreach (var item in quality)
        {
            Require(KnownQualityCodes.Contains(item.Code), $"quality code {item.Code} is not controlled", errors);
            Require(item.FieldPaths.Count > 0 && item.FieldPaths.All(IsJsonPointer),
                $"quality {item.Code} has invalid field paths", errors);
            Require(item.FieldPaths.Distinct(StringComparer.Ordinal).Count() == item.FieldPaths.Count,
                $"quality {item.Code} has duplicate field paths", errors);
        }
    }

    private static bool HasQuality(
        IEnumerable<SituationDataQuality> quality,
        string code,
        string path,
        SituationQualitySeverity? severity = null) => quality.Any(item =>
        item.Code == code && item.FieldPaths.Contains(path, StringComparer.Ordinal) &&
        (severity is null || item.Severity == severity));

    private static void RequireEvidence(
        bool required,
        string id,
        IReadOnlySet<string> evidenceIds,
        ICollection<string> errors)
    {
        if (required)
            Require(evidenceIds.Contains(id) || evidenceIds.Any(value => value.StartsWith(id + ".", StringComparison.Ordinal)),
                $"facts field has no evidence binding {id}", errors);
    }

    private static bool HasUncertaintySupport(SituationFactsV1 facts) =>
        facts.DataQuality.Count > 0 ||
        facts.Alive.T is null || facts.Alive.CT is null ||
        facts.TotalHealth.T is null || facts.TotalHealth.CT is null ||
        facts.Formation.T == SituationFormation.Unknown || facts.Formation.CT == SituationFormation.Unknown ||
        facts.Pressure.Side is null || facts.ContestedRegions is null ||
        facts.ContactRisk == SituationContactRisk.Unknown ||
        facts.IsolatedSide == SituationIsolatedSide.Unknown ||
        facts.SpatialAdvantage == SituationSpatialAdvantage.Uncertain;

    private static void ValidateNarrativeText(string text, string label, ICollection<string> errors)
    {
        foreach (var term in ForbiddenNarrativeTerms)
            Require(!text.Contains(term, StringComparison.OrdinalIgnoreCase),
                $"{label} contains forbidden identity, future, prediction, or instruction term '{term}'", errors);
    }

    private static bool IsSemanticId(string value) => !string.IsNullOrWhiteSpace(value) &&
        Regex.IsMatch(value, "^[a-z][a-z0-9-]*(?:\\.(?:[a-z][a-z0-9-]*|(?:T|CT)[1-5]))*$",
            RegexOptions.CultureInvariant);

    private static bool IsRuleId(string value) => !string.IsNullOrWhiteSpace(value) &&
        Regex.IsMatch(value, "^[a-z][a-z0-9-]*(?:\\.[a-z][a-z0-9-]*)*$", RegexOptions.CultureInvariant);

    private static bool ContainsSensitiveIdentity(string value) =>
        value.Contains("steam", StringComparison.OrdinalIgnoreCase) ||
        Regex.IsMatch(value, "[0-9]{17}", RegexOptions.CultureInvariant);

    private static bool ContainsCjk(string value) => value.Any(character => character is >= '\u3400' and <= '\u9fff');

    private static bool IsJsonPointer(string path)
    {
        if (!path.StartsWith("/", StringComparison.Ordinal))
            return false;
        return !Regex.IsMatch(path, "~(?:[^01]|$)", RegexOptions.CultureInvariant);
    }

    private static bool JsonPointerExists(JsonElement root, string path)
    {
        if (!IsJsonPointer(path))
            return false;
        var current = root;
        foreach (var rawToken in path.Split('/').Skip(1))
        {
            var token = rawToken.Replace("~1", "/", StringComparison.Ordinal)
                .Replace("~0", "~", StringComparison.Ordinal);
            if (current.ValueKind == JsonValueKind.Object && current.TryGetProperty(token, out var property))
                current = property;
            else if (current.ValueKind == JsonValueKind.Array && int.TryParse(token, out var index) &&
                     index >= 0 && index < current.GetArrayLength())
                current = current[index];
            else
                return false;
        }
        return true;
    }

    private static IReadOnlyList<int?> UtilityCountValues(SituationUtilityCounts counts) =>
        [counts.Flash, counts.Smoke, counts.He, counts.Molotov, counts.Incendiary, counts.Decoy];

    private static IReadOnlyList<int?> WeaponCountValues(SituationWeaponCounts counts) =>
    [
        counts.Pistol, counts.Smg, counts.Rifle, counts.Sniper, counts.Shotgun,
        counts.Machinegun, counts.Grenade, counts.Knife, counts.Bomb, counts.Other, counts.Unknown
    ];

    private static bool AllNullOrAllKnown(IEnumerable<int?> values)
    {
        var items = values.ToArray();
        return items.All(value => value is null) || items.All(value => value is not null);
    }

    private static int WeaponCountTotal(SituationWeaponCounts counts) =>
        WeaponCountValues(counts).Sum(value => value ?? 0);

    private static bool IsFiniteNonNegative(double? value) =>
        value is null || double.IsFinite(value.Value) && value.Value >= 0;

    private static bool IsSlotForSide(string slot, SituationSide side) =>
        slot.StartsWith(side == SituationSide.T ? "T" : "CT", StringComparison.Ordinal) &&
        int.TryParse(slot[(side == SituationSide.T ? 1 : 2)..], out var index) &&
        index is >= 1 and <= 5;

    private static void ValidateSlotReference(
        string? slot,
        SituationSide expectedSide,
        IReadOnlyCollection<string> slots,
        string label,
        ICollection<string> errors)
    {
        if (slot is null)
            return;
        Require(slots.Contains(slot, StringComparer.Ordinal), $"{label} references an unknown slot", errors);
        Require(IsSlotForSide(slot, expectedSide), $"{label} references the wrong side", errors);
    }

    private static bool IsAscending(IEnumerable<int> values)
    {
        var previous = -1;
        foreach (var value in values)
        {
            if (value <= previous)
                return false;
            previous = value;
        }
        return true;
    }

    private static void ValidateFinite(SituationVec3? value, string label, ICollection<string> errors)
    {
        if (value is null)
            return;
        Require(double.IsFinite(value.X) && double.IsFinite(value.Y) && double.IsFinite(value.Z),
            $"{label} is non-finite", errors);
    }

    private static double? NearestDistance(SituationPlayer player, IEnumerable<SituationPlayer> candidates)
    {
        if (player.Position is null)
            return null;
        var values = candidates.Where(candidate => candidate.Position is not null)
            .Select(candidate => Distance(player.Position, candidate.Position!))
            .Where(value => value is not null)
            .Select(value => value!.Value)
            .ToArray();
        return values.Length == 0 ? null : values.Min();
    }

    private static double? Distance(SituationVec3? position, MapPoint target)
    {
        if (position is null)
            return null;
        var dx = position.X - target.X;
        var dy = position.Y - target.Y;
        return Math.Round(Math.Sqrt(dx * dx + dy * dy), 6, MidpointRounding.AwayFromZero);
    }

    private static double? Distance(SituationVec3? left, SituationVec3? right)
    {
        if (left is null || right is null)
            return null;
        var dx = left.X - right.X;
        var dy = left.Y - right.Y;
        return Math.Round(Math.Sqrt(dx * dx + dy * dy), 6, MidpointRounding.AwayFromZero);
    }

    private static bool Equal(double? left, double? right) =>
        left is null && right is null || left is not null && right is not null &&
        Math.Abs(left.Value - right.Value) <= 0.000001;

    private static bool Equal(SituationVec3? left, SituationVec3? right) =>
        left is null && right is null || left is not null && right is not null &&
        Math.Abs(left.X - right.X) <= 0.000001 &&
        Math.Abs(left.Y - right.Y) <= 0.000001 &&
        Math.Abs(left.Z - right.Z) <= 0.000001;

    private static bool IsLowerHex(char value) => value is >= '0' and <= '9' or >= 'a' and <= 'f';

    private static void Require(bool condition, string error, ICollection<string> errors)
    {
        if (!condition)
            errors.Add(error);
    }

    private static void ThrowIfErrors(IReadOnlyCollection<string> errors)
    {
        if (errors.Count > 0)
            throw new InvalidDataException(string.Join(Environment.NewLine, errors));
    }
}

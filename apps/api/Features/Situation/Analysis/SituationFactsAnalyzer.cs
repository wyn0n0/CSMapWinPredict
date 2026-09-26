using System.Globalization;
using CsDemoMap.Api.Models;

namespace CsDemoMap.Api.Services;

internal sealed class SituationFactsAnalyzer
{
    private readonly ISituationVisibilityQuery? visibility;

    internal SituationFactsAnalyzer(ISituationVisibilityQuery? visibility = null) => this.visibility = visibility;

    public SituationFactsAnalysisResult Analyze(
        MinimapSceneV1 scene,
        SituationAnalysisRuleSet rules)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(rules);
        SituationContractValidator.Validate(scene);
        SituationAnalysisRuleLoader.Validate(rules);
        ValidateCompatibility(scene, rules);

        var context = AnalysisContext.Create(scene, rules);
        var evidence = new SituationEvidenceBuilder();
        var margins = new List<SituationRuleMargin>();
        var decisions = new SortedDictionary<string, string>(StringComparer.Ordinal);

        AddDirectEvidence(scene, evidence);
        var formationT = AnalyzeFormation(context, SituationSide.T, evidence, margins, decisions);
        var formationCt = AnalyzeFormation(context, SituationSide.CT, evidence, margins, decisions);
        var contested = AnalyzeContestedRegions(context, evidence, decisions);
        var pressure = AnalyzePressure(context, evidence, margins, decisions);
        var contact = AnalyzeContact(context, evidence, margins, decisions);
        var isolation = AnalyzeIsolation(context, evidence, margins, decisions);
        var spatial = AnalyzeSpatial(
            context, pressure, contact, isolation, evidence, margins, decisions);

        var quality = NormalizeQuality(scene.DataQuality);
        evidence.Add(
            "input-quality",
            ["/dataQuality"],
            "quality.scene-input",
            quality.Count == 0
                ? "当前结构化输入未报告质量异常。"
                : $"当前结构化输入包含 {quality.Count.ToString(CultureInfo.InvariantCulture)} 项质量标记。");

        var confidence = AnalyzeConfidence(
            context,
            formationT,
            formationCt,
            contested,
            pressure,
            contact,
            isolation,
            spatial,
            quality,
            margins,
            evidence,
            decisions,
            out var unknownCount,
            out var hasRelevantError,
            out var hasRelevantWarning);

        var facts = new SituationFactsV1(
            SituationContractVersions.Facts,
            rules.AnalysisRuleVersion,
            new(scene.Teams.T.Alive, scene.Teams.CT.Alive),
            new(scene.Teams.T.TotalHealth, scene.Teams.CT.TotalHealth),
            new(scene.Bomb.State, scene.Bomb.Site),
            new(formationT.Value, formationCt.Value),
            new(pressure.Side, pressure.Site),
            contested.Regions,
            contact.Risk,
            isolation.Side,
            spatial.Advantage,
            confidence,
            evidence.Build(),
            quality);

        SituationContractValidator.Validate(facts, scene, rules.AnalysisRuleVersion);
        var canonical = SituationCanonicalJson.Serialize(facts);
        var diagnostics = new SituationRuleDiagnostics(
            margins
                .OrderBy(item => item.Rule, StringComparer.Ordinal)
                .ThenBy(item => item.Metric, StringComparer.Ordinal)
                .ToArray(),
            decisions,
            spatial.Components,
            spatial.Score,
            unknownCount,
            hasRelevantError,
            hasRelevantWarning);
        return new(facts, canonical, SituationCanonicalJson.Sha256(facts), diagnostics);
    }

    private static void ValidateCompatibility(MinimapSceneV1 scene, SituationAnalysisRuleSet rules)
    {
        if (scene.Source != "structured" ||
            scene.Map != rules.Map ||
            scene.SchemaVersion != rules.SceneSchemaVersion ||
            scene.SceneBuilderVersion != rules.SceneBuilderVersion ||
            scene.GeometryVersion != rules.GeometryVersion ||
            rules.FactsSchemaVersion != SituationContractVersions.Facts)
            throw new InvalidDataException("Situation scene and analysis rule versions are incompatible.");
    }

    private static void AddDirectEvidence(MinimapSceneV1 scene, SituationEvidenceBuilder evidence)
    {
        if (scene.Teams.T.Alive is { } tAlive)
            evidence.Add("alive.t", ["/teams/t/alive"], "direct.alive",
                $"T 方当前存活 {Number(tAlive)} 人。");
        if (scene.Teams.CT.Alive is { } ctAlive)
            evidence.Add("alive.ct", ["/teams/ct/alive"], "direct.alive",
                $"CT 方当前存活 {Number(ctAlive)} 人。");
        if (scene.Teams.T.TotalHealth is { } tHealth)
            evidence.Add("total-health.t", ["/teams/t/totalHealth"], "direct.total-health",
                $"T 方当前总生命值为 {Number(tHealth)}。");
        if (scene.Teams.CT.TotalHealth is { } ctHealth)
            evidence.Add("total-health.ct", ["/teams/ct/totalHealth"], "direct.total-health",
                $"CT 方当前总生命值为 {Number(ctHealth)}。");
        if (scene.Bomb.State != SituationBombState.Unknown)
            evidence.Add("bomb.state", ["/bomb/state"], "direct.bomb",
                $"C4 当前状态为{BombStateZh(scene.Bomb.State)}。");
        if (scene.Bomb.Site is { } site)
            evidence.Add("bomb.site", ["/bomb/site"], "direct.bomb",
                $"C4 当前包点为 {site} 点。");
    }

    private static FormationOutcome AnalyzeFormation(
        AnalysisContext context,
        SituationSide side,
        SituationEvidenceBuilder evidence,
        ICollection<SituationRuleMargin> margins,
        IDictionary<string, string> decisions)
    {
        var key = side == SituationSide.T ? "t" : "ct";
        var id = $"formation.{key}";
        var entries = context.ForSide(side);
        var alive = context.Alive(side);
        var sources = entries.Count == 0
            ? new[] { $"/teams/{key}/alive" }
            : entries.Select(entry => $"/players/{entry.PlayerIndex}/position").ToArray();
        if (alive is null || alive < 2 || entries.Count != alive || entries.Any(entry => entry.Player.Position is null))
        {
            evidence.Add(id, sources, "formation.cluster",
                $"{SideZh(side)}当前缺少足够的完整位置，阵型暂无法确认。");
            decisions[id] = "unknown";
            return new(SituationFormation.Unknown, false);
        }

        var components = ConnectedComponents(entries, context.Rules.Formation.LinkDistance,
            context.Rules.ComparisonEpsilon);
        var largest = components[0];
        var coverage = largest.Count / (double)entries.Count;
        var diameter = Diameter(largest);
        AddMargin(margins, id, "grouped-coverage", coverage,
            context.Rules.Formation.GroupedCoverageRatio, coverage - context.Rules.Formation.GroupedCoverageRatio,
            "greater-or-equal");
        AddMargin(margins, id, "grouped-diameter", diameter,
            context.Rules.Formation.GroupedMaxDiameter,
            diameter - context.Rules.Formation.GroupedMaxDiameter, "less-or-equal");
        if (GreaterOrEqual(coverage, context.Rules.Formation.GroupedCoverageRatio, context.Rules) &&
            LessOrEqual(diameter, context.Rules.Formation.GroupedMaxDiameter, context.Rules))
        {
            evidence.Add(id, sources, "formation.cluster",
                $"{SideZh(side)}主要形成一个 {Number(largest.Count)} 人簇，覆盖率 {Percent(coverage)}，簇直径 {Number(diameter)}。");
            decisions[id] = "grouped";
            return new(SituationFormation.Grouped, true);
        }

        var meaningful = components
            .Where(component => component.Count >= context.Rules.Formation.SplitMinClusterSize)
            .Take(2)
            .ToArray();
        if (meaningful.Length == 2)
        {
            var splitCoverage = (meaningful[0].Count + meaningful[1].Count) / (double)entries.Count;
            var centroidDistance = Distance(Centroid(meaningful[0]), Centroid(meaningful[1]));
            AddMargin(margins, id, "split-coverage", splitCoverage,
                context.Rules.Formation.SplitCoverageRatio,
                splitCoverage - context.Rules.Formation.SplitCoverageRatio, "greater-or-equal");
            AddMargin(margins, id, "split-centroid-distance", centroidDistance,
                context.Rules.Formation.SplitMinCentroidDistance,
                centroidDistance - context.Rules.Formation.SplitMinCentroidDistance, "greater-or-equal");
            if (GreaterOrEqual(splitCoverage, context.Rules.Formation.SplitCoverageRatio, context.Rules) &&
                GreaterOrEqual(centroidDistance, context.Rules.Formation.SplitMinCentroidDistance, context.Rules))
            {
                evidence.Add(id, sources, "formation.cluster",
                    $"{SideZh(side)}形成 {Number(meaningful[0].Count)} 人与 {Number(meaningful[1].Count)} 人两簇，质心距离 {Number(centroidDistance)}。");
                decisions[id] = "split";
                return new(SituationFormation.Split, true);
            }
        }

        evidence.Add(id, sources, "formation.cluster",
            $"{SideZh(side)}位置完整，但不满足集中或双路分簇条件，当前为分散阵型。");
        decisions[id] = "spread";
        return new(SituationFormation.Spread, true);
    }

    private static ContestedOutcome AnalyzeContestedRegions(
        AnalysisContext context,
        SituationEvidenceBuilder evidence,
        IDictionary<string, string> decisions)
    {
        var complete = context.BothRostersMatch() && context.Entries.All(entry => entry.Player.Region is not null);
        if (!complete)
        {
            evidence.Add("contested-regions", ["/players", "/geometry/regionOccupancy"],
                "region.same-occupancy", "部分存活玩家区域未知，争夺区域暂无法完整确认。");
            decisions["contested-regions"] = "unknown";
            return new(null, false);
        }
        var regions = context.Scene.Geometry.RegionOccupancy
            .Where(item => item.TAlive > 0 && item.CTAlive > 0)
            .Select(item => item.Region)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var text = regions.Length == 0
            ? "双方当前没有位于同一已知区域的存活玩家。"
            : $"双方当前共同占用的争夺区域为：{string.Join("、", regions)}。";
        evidence.Add("contested-regions", ["/geometry/regionOccupancy"],
            "region.same-occupancy", text);
        decisions["contested-regions"] = regions.Length == 0
            ? "none"
            : string.Join(",", regions);
        return new(regions, true);
    }

    private static PressureOutcome AnalyzePressure(
        AnalysisContext context,
        SituationEvidenceBuilder evidence,
        ICollection<SituationRuleMargin> margins,
        IDictionary<string, string> decisions)
    {
        if (context.Scene.Round.Phase == SituationRoundPhase.Ended ||
            context.Scene.Bomb.State is SituationBombState.Defused or SituationBombState.Exploded)
            return AddNoPressure(context, evidence, decisions, true, "回合已结束，当前没有活动包点压力。");

        SituationSide? activeSide;
        SituationSite[] sites;
        switch (context.Scene.Bomb.State)
        {
            case SituationBombState.Carried:
            case SituationBombState.Dropped:
            case SituationBombState.Planting:
                activeSide = SituationSide.T;
                sites = [SituationSite.A, SituationSite.B];
                break;
            case SituationBombState.Planted:
            case SituationBombState.Defusing:
                activeSide = SituationSide.CT;
                if (context.Scene.Bomb.Site is not { } plantedSite)
                    return AddNoPressure(context, evidence, decisions, false,
                        "C4 包点未知，当前包点压力暂无法确认。");
                sites = [plantedSite];
                break;
            default:
                return AddNoPressure(context, evidence, decisions, false,
                    "C4 状态未知，当前包点压力暂无法确认。");
        }

        var side = activeSide.Value;
        var entries = context.ForSide(side);
        if (context.Alive(side) is not { } alive || entries.Count != alive ||
            entries.Any(entry => entry.Player.Position is null))
            return AddNoPressure(context, evidence, decisions, false,
                $"{SideZh(side)}位置覆盖不完整，当前包点压力暂无法确认。");

        var metrics = sites.Select(site => EvaluatePressureSite(context, side, site, entries, margins)).ToArray();
        var qualified = metrics.Where(item => item.Qualified).ToArray();
        if (qualified.Length == 0)
        {
            if (metrics.Any(item => !item.TrendComplete))
                return AddNoPressure(context, evidence, decisions, false,
                    "接近包点所需的轨迹历史不足，当前压力暂无法确认。");
            return AddNoPressure(context, evidence, decisions, true,
                $"{SideZh(side)}当前没有至少 {Number(context.Rules.Pressure.MinPlayers)} 人形成同一包点压力。");
        }

        var best = qualified
            .OrderByDescending(item => item.NearCount)
            .ThenByDescending(item => item.ApproachingCount)
            .ThenByDescending(item => item.TotalApproachDelta)
            .ThenBy(item => item.AverageDistance)
            .ThenBy(item => item.Site)
            .First();
        var tied = qualified.Count(item => SamePressureRank(item, best, context.Rules.ComparisonEpsilon)) > 1;
        if (tied || metrics.Any(item => !item.Qualified && !item.TrendComplete))
            return AddNoPressure(context, evidence, decisions, false,
                "两个包点的压力依据并列或轨迹覆盖不足，当前无法确认唯一压力点。");

        var sources = new List<string> { "/bomb/state" };
        if (context.Scene.Bomb.Site is not null)
            sources.Add("/bomb/site");
        foreach (var entry in entries)
        {
            sources.Add($"/geometry/playerDistances/{entry.DistanceIndex}/site{best.Site}");
            sources.Add($"/players/{entry.PlayerIndex}/trajectory");
        }
        var text = $"{SideZh(side)}对 {best.Site} 点形成当前压力：近点 {Number(best.NearCount)} 人，持续接近 {Number(best.ApproachingCount)} 人。";
        evidence.Add("pressure.side", sources, "pressure.site-approach", text);
        evidence.Add("pressure.site", sources, "pressure.site-approach", text);
        decisions["pressure"] = $"{side}-{best.Site}";
        return new(side, best.Site, true);
    }

    private static PressureOutcome AddNoPressure(
        AnalysisContext context,
        SituationEvidenceBuilder evidence,
        IDictionary<string, string> decisions,
        bool complete,
        string text)
    {
        var sources = context.Scene.Bomb.Site is null
            ? new[] { "/bomb/state", "/geometry/playerDistances" }
            : new[] { "/bomb/state", "/bomb/site", "/geometry/playerDistances" };
        evidence.Add("pressure.side", sources, "pressure.site-approach", text);
        evidence.Add("pressure.site", sources, "pressure.site-approach", text);
        decisions["pressure"] = complete ? "none" : "unknown";
        return new(null, null, complete);
    }

    private static SitePressureMetric EvaluatePressureSite(
        AnalysisContext context,
        SituationSide side,
        SituationSite site,
        IReadOnlyList<PlayerEntry> entries,
        ICollection<SituationRuleMargin> margins)
    {
        var distances = entries.Select(entry => context.SiteDistance(entry, site)).ToArray();
        if (distances.Any(value => value is null))
            return new(site, 0, 0, 0, double.PositiveInfinity, false, false);
        var known = distances.Select(value => value!.Value).ToArray();
        var nearCount = known.Count(value => LessOrEqual(value, context.Rules.Pressure.SiteRadius, context.Rules));
        var approachingCount = 0;
        var totalApproach = 0d;
        var trendComplete = true;
        for (var index = 0; index < entries.Count; index++)
        {
            var approach = ApproachToSite(context, entries[index], site);
            if (approach is null)
            {
                trendComplete = false;
                continue;
            }
            AddMargin(margins, $"pressure.{side.ToString().ToLowerInvariant()}.{site.ToString().ToLowerInvariant()}",
                $"approach-{entries[index].Player.Slot}", approach.Value,
                context.Rules.Pressure.MinApproachDelta,
                approach.Value - context.Rules.Pressure.MinApproachDelta, "greater-or-equal");
            if (GreaterOrEqual(approach.Value, context.Rules.Pressure.MinApproachDelta, context.Rules))
            {
                approachingCount++;
                totalApproach += approach.Value;
            }
        }
        AddMargin(margins, $"pressure.{side.ToString().ToLowerInvariant()}.{site.ToString().ToLowerInvariant()}",
            "near-count", nearCount, context.Rules.Pressure.MinPlayers,
            nearCount - context.Rules.Pressure.MinPlayers, "greater-or-equal");
        AddMargin(margins, $"pressure.{side.ToString().ToLowerInvariant()}.{site.ToString().ToLowerInvariant()}",
            "approaching-count", approachingCount, context.Rules.Pressure.MinPlayers,
            approachingCount - context.Rules.Pressure.MinPlayers, "greater-or-equal");
        var qualified = nearCount >= context.Rules.Pressure.MinPlayers ||
                        approachingCount >= context.Rules.Pressure.MinPlayers;
        return new(site, nearCount, approachingCount, Round(totalApproach), Round(known.Average()),
            trendComplete, qualified);
    }

    private static double? ApproachToSite(AnalysisContext context, PlayerEntry entry, SituationSite site)
    {
        var trajectory = entry.Player.Trajectory;
        if (trajectory.Points.Count == 0 || entry.Player.Position is null)
            return null;
        var earliest = trajectory.Points
            .Where(point => context.Scene.Tick - point.Tick >=
                context.Rules.Pressure.MinHistorySeconds * context.Scene.TickRate - context.Rules.ComparisonEpsilon)
            .OrderBy(point => point.Tick)
            .FirstOrDefault();
        if (earliest is null)
            return null;
        var sitePoint = context.SitePoint(site);
        return Round(Distance(earliest.Position, sitePoint) - Distance(entry.Player.Position, sitePoint));
    }

    private ContactOutcome AnalyzeContact(
        AnalysisContext context,
        SituationEvidenceBuilder evidence,
        ICollection<SituationRuleMargin> margins,
        IDictionary<string, string> decisions)
    {
        var tAlive = context.Alive(SituationSide.T);
        var ctAlive = context.Alive(SituationSide.CT);
        if (tAlive == 0 || ctAlive == 0)
        {
            evidence.Add("contact-risk", ["/teams/t/alive", "/teams/ct/alive"],
                "contact.proximity-cues", "一方当前没有存活玩家，敌我即时接触风险为低。" );
            decisions["contact-risk"] = "low";
            return new(SituationContactRisk.Low, true, null);
        }
        if (!context.BothRostersMatch() || context.Entries.Any(entry => entry.Player.Position is null))
        {
            evidence.Add("contact-risk", ["/players", "/teams/t/alive", "/teams/ct/alive"],
                "contact.proximity-cues", "敌我位置覆盖不完整，即时接触风险暂无法确认。" );
            decisions["contact-risk"] = "unknown";
            return new(SituationContactRisk.Unknown, false, null);
        }

        var pairs = context.AllPairs()
            .OrderBy(item => item.Distance)
            .ThenBy(item => item.T.Player.Slot, StringComparer.Ordinal)
            .ThenBy(item => item.CT.Player.Slot, StringComparer.Ordinal)
            .ToArray();
        var pair = pairs[0];
        if (context.Rules.Visibility is not null)
        {
            var visible = new List<(PlayerPair Pair, SituationContactRisk Risk)>();
            var unknownMax = 0;
            var blocked = 0;
            foreach (var candidate in pairs)
            {
                var cues = GetContactCues(context, candidate);
                var possible = ContactRisk(candidate.Distance, cues.Count, context.Rules);
                if (possible == SituationContactRisk.Low) continue;
                var status = visibility?.Query(candidate.T.Player.Position!, candidate.CT.Player.Position!)
                    ?? SituationVisibility.Unknown;
                if (status == SituationVisibility.Clear) visible.Add((candidate, possible));
                else if (status == SituationVisibility.Blocked) blocked++;
                else unknownMax = Math.Max(unknownMax, RiskRank(possible));
            }
            decisions["contact-occluded-pairs"] = blocked.ToString(CultureInfo.InvariantCulture);
            var best = visible.OrderByDescending(item => RiskRank(item.Risk))
                .ThenBy(item => item.Pair.Distance)
                .ThenBy(item => item.Pair.T.Player.Slot, StringComparer.Ordinal)
                .ThenBy(item => item.Pair.CT.Player.Slot, StringComparer.Ordinal).FirstOrDefault();
            if (unknownMax > (best.Pair is null ? 0 : RiskRank(best.Risk)))
            {
                evidence.Add("contact-risk", ["/players"], "contact.raycast-proximity",
                    "部分近距离敌我组合的地图遮挡信息无法确认，当前直接接触风险未知。");
                decisions["contact-risk"] = "unknown";
                return new(SituationContactRisk.Unknown, false, null);
            }
            if (best.Pair is null)
            {
                evidence.Add("contact-risk", ["/players"], "contact.raycast-proximity",
                    $"近距离候选中 {Number(blocked)} 对被静态地图遮挡，未发现满足中高风险条件的无遮挡组合，当前直接接触风险为低；未评估绕角接触与穿透射击。");
                decisions["contact-risk"] = "low";
                return new(SituationContactRisk.Low, true, null);
            }
            pair = best.Pair;
        }
        var contactCues = GetContactCues(context, pair);
        var sameRegion = contactCues.SameRegion;
        var facing = contactCues.Facing;
        var closingDelta = contactCues.ClosingDelta;
        var closing = contactCues.Closing;
        var cueCount = contactCues.Count;
        AddMargin(margins, "contact-risk", "high-distance", pair.Distance,
            context.Rules.Contact.HighDistance, pair.Distance - context.Rules.Contact.HighDistance,
            "less-or-equal");
        AddMargin(margins, "contact-risk", "medium-distance", pair.Distance,
            context.Rules.Contact.MediumDistance, pair.Distance - context.Rules.Contact.MediumDistance,
            "less-or-equal");
        AddMargin(margins, "contact-risk", "cue-distance", pair.Distance,
            context.Rules.Contact.CueDistance, pair.Distance - context.Rules.Contact.CueDistance,
            "less-or-equal");
        if (closingDelta is { } closingValue)
            AddMargin(margins, "contact-risk", "closing-delta", closingValue,
                context.Rules.Contact.MinClosingDelta,
                closingValue - context.Rules.Contact.MinClosingDelta, "greater-or-equal");

        var risk = ContactRisk(pair.Distance, cueCount, context.Rules);
        var cueText = new List<string>();
        if (sameRegion) cueText.Add("同区");
        if (facing) cueText.Add("朝向接近");
        if (closing) cueText.Add("距离缩短");
        var sources = new[]
        {
            $"/players/{pair.T.PlayerIndex}/position",
            $"/players/{pair.T.PlayerIndex}/region",
            $"/players/{pair.T.PlayerIndex}/heading",
            $"/players/{pair.T.PlayerIndex}/trajectory",
            $"/players/{pair.CT.PlayerIndex}/position",
            $"/players/{pair.CT.PlayerIndex}/region",
            $"/players/{pair.CT.PlayerIndex}/heading",
            $"/players/{pair.CT.PlayerIndex}/trajectory"
        };
        var text = cueText.Count == 0
            ? $"最近敌我距离为 {Number(pair.Distance)}，即时接触风险为{RiskZh(risk)}。"
            : $"最近敌我距离为 {Number(pair.Distance)}，并出现{string.Join("、", cueText)}线索，即时接触风险为{RiskZh(risk)}。";
        if (context.Rules.Visibility is not null)
            text = text.Replace("最近敌我距离", "主导无遮挡敌我组合的平面距离", StringComparison.Ordinal) +
                " 静态地图射线存在通路；未评估烟雾、视野方向与穿透射击。";
        evidence.Add("contact-risk", sources,
            context.Rules.Visibility is null ? "contact.proximity-cues" : "contact.raycast-proximity", text);
        decisions["contact-risk"] = risk.ToString().ToLowerInvariant();
        return new(risk, true, pair);
    }

    private static int RiskRank(SituationContactRisk risk) => risk switch
    {
        SituationContactRisk.High => 2,
        SituationContactRisk.Medium => 1,
        _ => 0
    };

    private static (bool SameRegion, bool Facing, double? ClosingDelta, bool Closing, int Count)
        GetContactCues(AnalysisContext context, PlayerPair pair)
    {
        var same = pair.T.Player.Region is not null && pair.T.Player.Region == pair.CT.Player.Region;
        var facing = Faces(pair.T.Player, pair.CT.Player, context.Rules.Contact.FacingDotMin, context.Rules.ComparisonEpsilon) ||
            Faces(pair.CT.Player, pair.T.Player, context.Rules.Contact.FacingDotMin, context.Rules.ComparisonEpsilon);
        var delta = ClosingDelta(context, pair);
        var closing = delta is { } value && GreaterOrEqual(value, context.Rules.Contact.MinClosingDelta, context.Rules);
        return (same, facing, delta, closing, (same ? 1 : 0)+(facing ? 1 : 0)+(closing ? 1 : 0));
    }

    private static IsolationOutcome AnalyzeIsolation(
        AnalysisContext context,
        SituationEvidenceBuilder evidence,
        ICollection<SituationRuleMargin> margins,
        IDictionary<string, string> decisions)
    {
        var t = AnalyzeIsolationSide(context, SituationSide.T, margins);
        var ct = AnalyzeIsolationSide(context, SituationSide.CT, margins);
        SituationIsolatedSide value;
        if (!t.Complete || !ct.Complete)
            value = SituationIsolatedSide.Unknown;
        else if (t.IsolatedSlots.Count > 0 && ct.IsolatedSlots.Count > 0)
            value = SituationIsolatedSide.Both;
        else if (t.IsolatedSlots.Count > 0)
            value = SituationIsolatedSide.T;
        else if (ct.IsolatedSlots.Count > 0)
            value = SituationIsolatedSide.CT;
        else
            value = SituationIsolatedSide.None;

        var paths = t.SourcePaths.Concat(ct.SourcePaths).DefaultIfEmpty("/geometry/playerDistances");
        var text = value switch
        {
            SituationIsolatedSide.T => "T 方当前存在孤立玩家。",
            SituationIsolatedSide.CT => "CT 方当前存在孤立玩家。",
            SituationIsolatedSide.Both => "双方当前均存在孤立玩家。",
            SituationIsolatedSide.None => "双方当前均未满足孤立判定条件。",
            _ => "队伍或位置覆盖不完整，孤立情况暂无法确认。"
        };
        evidence.Add("isolated-side", paths, "isolation.support-distance", text);
        decisions["isolated-side"] = value switch
        {
            SituationIsolatedSide.T => "T",
            SituationIsolatedSide.CT => "CT",
            _ => value.ToString().ToLowerInvariant()
        };
        return new(value, value != SituationIsolatedSide.Unknown);
    }

    private static SideIsolationOutcome AnalyzeIsolationSide(
        AnalysisContext context,
        SituationSide side,
        ICollection<SituationRuleMargin> margins)
    {
        var key = side == SituationSide.T ? "t" : "ct";
        var alive = context.Alive(side);
        var enemiesAlive = context.Alive(side == SituationSide.T ? SituationSide.CT : SituationSide.T);
        var entries = context.ForSide(side);
        var sources = new List<string> { $"/teams/{key}/alive" };
        if (alive is null || enemiesAlive is null || entries.Count != alive || !context.BothRostersMatch() ||
            context.Entries.Any(entry => entry.Player.Position is null))
            return new(false, [], sources);
        if (alive == 0 || enemiesAlive == 0)
            return new(true, [], sources);
        if (alive == 1)
        {
            sources.Add($"/geometry/playerDistances/{entries[0].DistanceIndex}/nearestEnemy");
            return new(true, [entries[0].Player.Slot], sources);
        }

        var isolated = new List<string>();
        foreach (var entry in entries)
        {
            var ally = entry.Distances.NearestAlly;
            var enemy = entry.Distances.NearestEnemy;
            sources.Add($"/geometry/playerDistances/{entry.DistanceIndex}/nearestAlly");
            sources.Add($"/geometry/playerDistances/{entry.DistanceIndex}/nearestEnemy");
            if (ally is null || enemy is null)
                return new(false, [], sources);
            AddMargin(margins, $"isolation.{key}.{entry.Player.Slot}", "ally-distance", ally.Value,
                context.Rules.Isolation.AllyDistanceMin,
                ally.Value - context.Rules.Isolation.AllyDistanceMin, "greater-or-equal");
            var enemyMargin = ally.Value - enemy.Value;
            AddMargin(margins, $"isolation.{key}.{entry.Player.Slot}", "enemy-closer-margin", enemyMargin,
                context.Rules.Isolation.EnemyCloserMargin,
                enemyMargin - context.Rules.Isolation.EnemyCloserMargin, "greater-or-equal");
            if (GreaterOrEqual(ally.Value, context.Rules.Isolation.AllyDistanceMin, context.Rules) &&
                GreaterOrEqual(enemyMargin, context.Rules.Isolation.EnemyCloserMargin, context.Rules))
                isolated.Add(entry.Player.Slot);
        }
        return new(true, isolated.Order(StringComparer.Ordinal).ToArray(), sources);
    }

    private static SpatialOutcome AnalyzeSpatial(
        AnalysisContext context,
        PressureOutcome pressure,
        ContactOutcome contact,
        IsolationOutcome isolation,
        SituationEvidenceBuilder evidence,
        ICollection<SituationRuleMargin> margins,
        IDictionary<string, string> decisions)
    {
        var components = new SortedDictionary<string, double>(StringComparer.Ordinal);
        if (context.Scene.Teams.T.Alive is { } tAlive && context.Scene.Teams.CT.Alive is { } ctAlive)
            components["alive"] = Clamp(
                (tAlive - ctAlive) * context.Rules.Spatial.AlivePerPlayer,
                context.Rules.Spatial.AliveMaxContribution);
        if (context.Scene.Teams.T.TotalHealth is { } tHealth &&
            context.Scene.Teams.CT.TotalHealth is { } ctHealth)
            components["health"] = Clamp(
                (tHealth - ctHealth) / 100d * context.Rules.Spatial.HealthPerHundred,
                context.Rules.Spatial.HealthMaxContribution);
        if (contact.Pair is { } pair && context.Entries.All(entry => entry.Player.Position is not null))
        {
            var midpoint = new SituationVec3(
                (pair.T.Player.Position!.X + pair.CT.Player.Position!.X) / 2,
                (pair.T.Player.Position.Y + pair.CT.Player.Position.Y) / 2,
                0);
            var localT = context.ForSide(SituationSide.T).Count(entry =>
                Distance(entry.Player.Position!, midpoint) <= context.Rules.Spatial.LocalRadius + context.Rules.ComparisonEpsilon);
            var localCt = context.ForSide(SituationSide.CT).Count(entry =>
                Distance(entry.Player.Position!, midpoint) <= context.Rules.Spatial.LocalRadius + context.Rules.ComparisonEpsilon);
            components["local"] = Clamp(
                (localT - localCt) * context.Rules.Spatial.LocalPerPlayer,
                context.Rules.Spatial.LocalMaxContribution);
            AddMargin(margins, "spatial-advantage", "local-radius", context.Rules.Spatial.LocalRadius,
                context.Rules.Spatial.LocalRadius, 0, "configured-radius");
        }
        if (pressure.Complete)
            components["pressure"] = pressure.Side switch
            {
                SituationSide.T => context.Rules.Spatial.PressureContribution,
                SituationSide.CT => -context.Rules.Spatial.PressureContribution,
                _ => 0
            };
        if (context.Scene.Bomb.State != SituationBombState.Unknown)
            components["bomb"] = context.Scene.Bomb.State switch
            {
                SituationBombState.Planted => context.Rules.Spatial.PlantedBombContribution,
                SituationBombState.Defusing => -context.Rules.Spatial.DefusingBombContribution,
                _ => 0
            };
        if (isolation.Side != SituationIsolatedSide.Unknown)
            components["isolation"] = isolation.Side switch
            {
                SituationIsolatedSide.T => -context.Rules.Spatial.IsolationContribution,
                SituationIsolatedSide.CT => context.Rules.Spatial.IsolationContribution,
                _ => 0
            };

        var score = components.Count == 0 ? (double?)null : Round(components.Values.Sum());
        var relevantError = HasRelevantQuality(
            context.Scene.DataQuality, context.Rules, SituationQualitySeverity.Error, "spatial");
        SituationSpatialAdvantage advantage;
        if (score is null || components.Count < context.Rules.Spatial.RequiredComponents || relevantError)
            advantage = SituationSpatialAdvantage.Uncertain;
        else if (GreaterOrEqual(score.Value, context.Rules.Spatial.DecisiveScore, context.Rules))
            advantage = SituationSpatialAdvantage.T;
        else if (LessOrEqual(score.Value, -context.Rules.Spatial.DecisiveScore, context.Rules))
            advantage = SituationSpatialAdvantage.CT;
        else if (LessOrEqual(Math.Abs(score.Value), context.Rules.Spatial.EvenScore, context.Rules))
            advantage = SituationSpatialAdvantage.Even;
        else
            advantage = SituationSpatialAdvantage.Uncertain;

        if (score is { } knownScore)
        {
            AddMargin(margins, "spatial-advantage", "decisive-score", Math.Abs(knownScore),
                context.Rules.Spatial.DecisiveScore,
                Math.Abs(knownScore) - context.Rules.Spatial.DecisiveScore, "greater-or-equal");
            AddMargin(margins, "spatial-advantage", "even-score", Math.Abs(knownScore),
                context.Rules.Spatial.EvenScore,
                Math.Abs(knownScore) - context.Rules.Spatial.EvenScore, "less-or-equal");
        }
        var text = advantage switch
        {
            SituationSpatialAdvantage.T => $"当前空间与即时兵力条件偏向 T 方，规则分为 {Number(score!.Value)}。",
            SituationSpatialAdvantage.CT => $"当前空间与即时兵力条件偏向 CT 方，规则分为 {Number(score!.Value)}。",
            SituationSpatialAdvantage.Even => $"当前空间与即时兵力条件大致均衡，规则分为 {Number(score!.Value)}。",
            _ => score is null
                ? "当前有效空间证据不足，空间优势暂无法确认。"
                : $"当前规则分为 {Number(score.Value)}，但位于非决定区间，空间优势暂无法确认。"
        };
        evidence.Add("spatial-advantage",
            ["/teams/t/alive", "/teams/ct/alive", "/teams/t/totalHealth", "/teams/ct/totalHealth",
             "/players", "/geometry/playerDistances", "/bomb/state", "/bomb/site", "/dataQuality"],
            "spatial.weighted-balance", text);
        decisions["spatial-advantage"] = advantage switch
        {
            SituationSpatialAdvantage.T => "T",
            SituationSpatialAdvantage.CT => "CT",
            _ => advantage.ToString().ToLowerInvariant()
        };
        return new(advantage, score, components, advantage != SituationSpatialAdvantage.Uncertain);
    }

    private static SituationConfidence AnalyzeConfidence(
        AnalysisContext context,
        FormationOutcome formationT,
        FormationOutcome formationCt,
        ContestedOutcome contested,
        PressureOutcome pressure,
        ContactOutcome contact,
        IsolationOutcome isolation,
        SpatialOutcome spatial,
        IReadOnlyList<SituationDataQuality> quality,
        IReadOnlyList<SituationRuleMargin> margins,
        SituationEvidenceBuilder evidence,
        IDictionary<string, string> decisions,
        out int unknownCount,
        out bool hasRelevantError,
        out bool hasRelevantWarning)
    {
        unknownCount = 0;
        if (!formationT.Complete) unknownCount++;
        if (!formationCt.Complete) unknownCount++;
        if (!contested.Complete) unknownCount++;
        if (!pressure.Complete) unknownCount++;
        if (!contact.Complete) unknownCount++;
        if (!isolation.Complete) unknownCount++;
        if (!spatial.Complete) unknownCount++;
        hasRelevantError = quality.Any(item => item.Severity == SituationQualitySeverity.Error &&
            context.Rules.QualityImpacts.TryGetValue(item.Code, out var scopes) && scopes.Count > 0);
        hasRelevantWarning = quality.Any(item => item.Severity == SituationQualitySeverity.Warning &&
            context.Rules.QualityImpacts.TryGetValue(item.Code, out var scopes) && scopes.Count > 0);
        var nearBoundary = margins.Any(item =>
            item.Decision != "configured-radius" &&
            Math.Abs(item.Margin) <= context.Rules.Confidence.BoundaryBand + context.Rules.ComparisonEpsilon);

        SituationConfidence confidence;
        if (hasRelevantError || unknownCount >= context.Rules.Confidence.LowUnknownCount)
            confidence = SituationConfidence.Low;
        else if (hasRelevantWarning || unknownCount == 1 || nearBoundary)
            confidence = SituationConfidence.Medium;
        else
            confidence = SituationConfidence.High;
        var text = confidence switch
        {
            SituationConfidence.High => "输入完整且关键判定远离阈值边界，规则置信度为高。",
            SituationConfidence.Medium => "输入存在局部质量或阈值边界影响，规则置信度为中。",
            _ => "输入缺失、质量错误或多个规则无法确认，规则置信度为低。"
        };
        evidence.Add("confidence", ["/dataQuality", "/players", "/teams"],
            "confidence.input-margin", text);
        decisions["confidence"] = confidence.ToString().ToLowerInvariant();
        return confidence;
    }

    private static IReadOnlyList<IReadOnlyList<PlayerEntry>> ConnectedComponents(
        IReadOnlyList<PlayerEntry> entries,
        double linkDistance,
        double epsilon)
    {
        var remaining = new HashSet<string>(entries.Select(entry => entry.Player.Slot), StringComparer.Ordinal);
        var bySlot = entries.ToDictionary(entry => entry.Player.Slot, StringComparer.Ordinal);
        var result = new List<IReadOnlyList<PlayerEntry>>();
        while (remaining.Count > 0)
        {
            var start = remaining.Order(StringComparer.Ordinal).First();
            var queue = new Queue<string>();
            var component = new List<PlayerEntry>();
            remaining.Remove(start);
            queue.Enqueue(start);
            while (queue.Count > 0)
            {
                var slot = queue.Dequeue();
                var current = bySlot[slot];
                component.Add(current);
                var neighbours = remaining
                    .Where(candidate => Distance(current.Player.Position!, bySlot[candidate].Player.Position!) <=
                                        linkDistance + epsilon)
                    .Order(StringComparer.Ordinal)
                    .ToArray();
                foreach (var neighbour in neighbours)
                {
                    remaining.Remove(neighbour);
                    queue.Enqueue(neighbour);
                }
            }
            result.Add(component.OrderBy(item => item.Player.Slot, StringComparer.Ordinal).ToArray());
        }
        return result
            .OrderByDescending(component => component.Count)
            .ThenBy(component => string.Join(",", component.Select(item => item.Player.Slot)), StringComparer.Ordinal)
            .ToArray();
    }

    private static double Diameter(IReadOnlyList<PlayerEntry> entries) => entries
        .SelectMany((left, index) => entries.Skip(index + 1)
            .Select(right => Distance(left.Player.Position!, right.Player.Position!)))
        .DefaultIfEmpty(0)
        .Max();

    private static SituationVec3 Centroid(IReadOnlyList<PlayerEntry> entries) => new(
        entries.Average(entry => entry.Player.Position!.X),
        entries.Average(entry => entry.Player.Position!.Y),
        0);

    private static bool SamePressureRank(SitePressureMetric left, SitePressureMetric right, double epsilon) =>
        left.NearCount == right.NearCount &&
        left.ApproachingCount == right.ApproachingCount &&
        Math.Abs(left.TotalApproachDelta - right.TotalApproachDelta) <= epsilon &&
        Math.Abs(left.AverageDistance - right.AverageDistance) <= epsilon;

    private static bool Faces(
        SituationPlayer source,
        SituationPlayer target,
        double minimumDot,
        double epsilon)
    {
        if (source.Position is null || target.Position is null || source.Heading is null)
            return false;
        var dx = target.Position.X - source.Position.X;
        var dy = target.Position.Y - source.Position.Y;
        var length = Math.Sqrt(dx * dx + dy * dy);
        if (length <= epsilon)
            return false;
        var dot = source.Heading.X * dx / length + source.Heading.Y * dy / length;
        return dot + epsilon >= minimumDot;
    }

    private static double? ClosingDelta(AnalysisContext context, PlayerPair pair)
    {
        var rightPoints = pair.CT.Player.Trajectory.Points
            .GroupBy(point => point.Tick)
            .ToDictionary(group => group.Key, group => group.First());
        var minimumTicks = context.Rules.Contact.MinHistorySeconds * context.Scene.TickRate;
        foreach (var left in pair.T.Player.Trajectory.Points.OrderBy(point => point.Tick))
        {
            if (context.Scene.Tick - left.Tick < minimumTicks - context.Rules.ComparisonEpsilon ||
                !rightPoints.TryGetValue(left.Tick, out var right))
                continue;
            return Round(Distance(left.Position, right.Position) - pair.Distance);
        }
        return null;
    }

    private static IReadOnlyList<SituationDataQuality> NormalizeQuality(
        IReadOnlyList<SituationDataQuality> values) => values
        .GroupBy(item => (item.Code, item.Severity))
        .Select(group => new SituationDataQuality(
            group.Key.Code,
            group.SelectMany(item => item.FieldPaths)
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToArray(),
            group.Key.Severity))
        .OrderBy(item => item.Code, StringComparer.Ordinal)
        .ThenBy(item => item.Severity)
        .ToArray();

    private static bool HasRelevantQuality(
        IReadOnlyList<SituationDataQuality> quality,
        SituationAnalysisRuleSet rules,
        SituationQualitySeverity severity,
        string scope) => quality.Any(item => item.Severity == severity &&
            rules.QualityImpacts.TryGetValue(item.Code, out var scopes) &&
            scopes.Contains(scope, StringComparer.Ordinal));

    private static void AddMargin(
        ICollection<SituationRuleMargin> margins,
        string rule,
        string metric,
        double value,
        double threshold,
        double margin,
        string decision) => margins.Add(new(
            rule, metric, Round(value), Round(threshold), Round(margin), decision));

    private static bool LessOrEqual(double value, double threshold, SituationAnalysisRuleSet rules) =>
        value <= threshold + rules.ComparisonEpsilon;

    private static bool GreaterOrEqual(double value, double threshold, SituationAnalysisRuleSet rules) =>
        value + rules.ComparisonEpsilon >= threshold;

    private static SituationContactRisk ContactRisk(
        double distance,
        int cueCount,
        SituationAnalysisRuleSet rules)
    {
        if (LessOrEqual(distance, rules.Contact.HighDistance, rules) ||
            LessOrEqual(distance, rules.Contact.MediumDistance, rules) && cueCount >= 2)
            return SituationContactRisk.High;
        if (LessOrEqual(distance, rules.Contact.MediumDistance, rules) ||
            LessOrEqual(distance, rules.Contact.CueDistance, rules) && cueCount >= 1)
            return SituationContactRisk.Medium;
        return SituationContactRisk.Low;
    }

    private static double Clamp(double value, double maximum) => Round(Math.Clamp(value, -maximum, maximum));

    private static double Distance(SituationVec3 left, SituationVec3 right)
    {
        var dx = left.X - right.X;
        var dy = left.Y - right.Y;
        return Round(Math.Sqrt(dx * dx + dy * dy));
    }

    private static double Round(double value) => Math.Round(value, 6, MidpointRounding.AwayFromZero);

    private static string Number(double value) => value.ToString("0.######", CultureInfo.InvariantCulture);

    private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static string Percent(double value) =>
        (value * 100).ToString("0.#", CultureInfo.InvariantCulture) + "％";

    private static string SideZh(SituationSide side) => side == SituationSide.T ? "T 方" : "CT 方";

    private static string RiskZh(SituationContactRisk risk) => risk switch
    {
        SituationContactRisk.High => "高",
        SituationContactRisk.Medium => "中",
        SituationContactRisk.Low => "低",
        _ => "未知"
    };

    private static string BombStateZh(SituationBombState state) => state switch
    {
        SituationBombState.Carried => "携带中",
        SituationBombState.Dropped => "已掉落",
        SituationBombState.Planting => "下包中",
        SituationBombState.Planted => "已下包",
        SituationBombState.Defusing => "拆除中",
        SituationBombState.Defused => "已拆除",
        SituationBombState.Exploded => "已爆炸",
        _ => "未知"
    };

    private sealed record FormationOutcome(SituationFormation Value, bool Complete);

    private sealed record ContestedOutcome(IReadOnlyList<string>? Regions, bool Complete);

    private sealed record PressureOutcome(SituationSide? Side, SituationSite? Site, bool Complete);

    private sealed record ContactOutcome(SituationContactRisk Risk, bool Complete, PlayerPair? Pair);

    private sealed record IsolationOutcome(SituationIsolatedSide Side, bool Complete);

    private sealed record SideIsolationOutcome(
        bool Complete,
        IReadOnlyList<string> IsolatedSlots,
        IReadOnlyList<string> SourcePaths);

    private sealed record SpatialOutcome(
        SituationSpatialAdvantage Advantage,
        double? Score,
        IReadOnlyDictionary<string, double> Components,
        bool Complete);

    private sealed record SitePressureMetric(
        SituationSite Site,
        int NearCount,
        int ApproachingCount,
        double TotalApproachDelta,
        double AverageDistance,
        bool TrendComplete,
        bool Qualified);

    private sealed record PlayerEntry(
        SituationPlayer Player,
        int PlayerIndex,
        SituationPlayerDistances Distances,
        int DistanceIndex);

    private sealed record PlayerPair(PlayerEntry T, PlayerEntry CT, double Distance);

    private sealed class AnalysisContext
    {
        private AnalysisContext(
            MinimapSceneV1 scene,
            SituationAnalysisRuleSet rules,
            IReadOnlyList<PlayerEntry> entries,
            SituationVec3 siteA,
            SituationVec3 siteB)
        {
            Scene = scene;
            Rules = rules;
            Entries = entries;
            SiteA = siteA;
            SiteB = siteB;
        }

        public MinimapSceneV1 Scene { get; }
        public SituationAnalysisRuleSet Rules { get; }
        public IReadOnlyList<PlayerEntry> Entries { get; }
        private SituationVec3 SiteA { get; }
        private SituationVec3 SiteB { get; }

        public static AnalysisContext Create(
            MinimapSceneV1 scene,
            SituationAnalysisRuleSet rules)
        {
            var distances = scene.Geometry.PlayerDistances
                .Select((value, index) => (value, index))
                .ToDictionary(item => item.value.Slot, item => item, StringComparer.Ordinal);
            var entries = scene.Players.Select((player, index) =>
            {
                var distance = distances[player.Slot];
                return new PlayerEntry(player, index, distance.value, distance.index);
            }).ToArray();
            var geometry = MapFeatureGeometries.Find(scene.Map)
                ?? throw new InvalidDataException("Situation map geometry is unavailable.");
            var a = geometry.Normalize(geometry.SiteA.X, geometry.SiteA.Y);
            var b = geometry.Normalize(geometry.SiteB.X, geometry.SiteB.Y);
            return new(scene, rules, entries,
                new SituationVec3(Round(a.X), Round(a.Y), 0),
                new SituationVec3(Round(b.X), Round(b.Y), 0));
        }

        public IReadOnlyList<PlayerEntry> ForSide(SituationSide side) => Entries
            .Where(entry => entry.Player.Side == side)
            .OrderBy(entry => entry.Player.Slot, StringComparer.Ordinal)
            .ToArray();

        public int? Alive(SituationSide side) => side == SituationSide.T
            ? Scene.Teams.T.Alive
            : Scene.Teams.CT.Alive;

        public bool BothRostersMatch() =>
            Alive(SituationSide.T) == ForSide(SituationSide.T).Count &&
            Alive(SituationSide.CT) == ForSide(SituationSide.CT).Count;

        public SituationVec3 SitePoint(SituationSite site) => site == SituationSite.A ? SiteA : SiteB;

        public double? SiteDistance(PlayerEntry entry, SituationSite site) => site == SituationSite.A
            ? entry.Distances.SiteA
            : entry.Distances.SiteB;

        public IReadOnlyList<PlayerPair> AllPairs() => ForSide(SituationSide.T)
            .SelectMany(t => ForSide(SituationSide.CT)
                .Select(ct => new PlayerPair(t, ct, Distance(t.Player.Position!, ct.Player.Position!))))
            .ToArray();
    }
}

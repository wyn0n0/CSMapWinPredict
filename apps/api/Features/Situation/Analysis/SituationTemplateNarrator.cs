using CsDemoMap.Api.Models;

namespace CsDemoMap.Api.Services;

internal sealed class SituationTemplateNarrator
{
    private readonly SituationAnalysisRuleSet rules;

    public SituationTemplateNarrator(SituationAnalysisRuleSet rules)
    {
        ArgumentNullException.ThrowIfNull(rules);
        SituationAnalysisRuleLoader.Validate(rules);
        this.rules = rules;
    }

    public SituationNarrativeAnalysisResult Create(SituationFactsV1 facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        SituationContractValidator.Validate(facts);
        if (facts.AnalysisRuleVersion != rules.AnalysisRuleVersion)
            throw new InvalidDataException("Situation facts and template rule versions differ.");

        var evidence = facts.Evidence.ToDictionary(item => item.Id, StringComparer.Ordinal);
        var selected = new List<SituationHighlight>();
        var selectedIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in rules.TemplatePriority)
        {
            if (selected.Count >= 4 || !IsPrimaryCandidate(id, facts) ||
                !TryAdd(id, evidence, selected, selectedIds))
                continue;
        }
        foreach (var id in rules.TemplatePriority)
        {
            if (selected.Count >= 2)
                break;
            TryAdd(id, evidence, selected, selectedIds);
        }
        if (selected.Count < 2)
            throw new InvalidDataException("Template narrative cannot produce two supported highlights.");

        while (selected.Count > 2 && SummaryLength(selected) > 240)
        {
            selectedIds.Remove(selected[^1].EvidenceIds[0]);
            selected.RemoveAt(selected.Count - 1);
        }
        if (SummaryLength(selected) > 240)
            throw new InvalidDataException("Template narrative summary exceeds the contract limit.");

        var narrative = new SituationNarrativeV1(
            SituationContractVersions.Narrative,
            selected,
            string.Join("；", selected.Select(item => item.TextZh.TrimEnd('。'))) + "。",
            BuildUncertainties(facts));
        SituationContractValidator.ValidateTemplate(narrative, facts);
        var canonical = SituationCanonicalJson.Serialize(narrative);
        return new(narrative, canonical, SituationCanonicalJson.Sha256(narrative));
    }

    private static bool IsPrimaryCandidate(string id, SituationFactsV1 facts) => id switch
    {
        "bomb.state" => facts.Bomb.State != SituationBombState.Unknown,
        "spatial-advantage" => facts.SpatialAdvantage != SituationSpatialAdvantage.Uncertain,
        "contact-risk" => facts.ContactRisk == SituationContactRisk.High,
        "pressure.side" => facts.Pressure.Side is not null,
        "contested-regions" => facts.ContestedRegions is { Count: > 0 },
        "isolated-side" => facts.IsolatedSide is not SituationIsolatedSide.None and not SituationIsolatedSide.Unknown,
        "alive.t" => facts.Alive.T is not null,
        "alive.ct" => facts.Alive.CT is not null,
        "total-health.t" => facts.TotalHealth.T is not null,
        "total-health.ct" => facts.TotalHealth.CT is not null,
        "formation.t" => facts.Formation.T != SituationFormation.Unknown,
        "formation.ct" => facts.Formation.CT != SituationFormation.Unknown,
        "input-quality" => facts.DataQuality.Count > 0,
        "confidence" => facts.Confidence != SituationConfidence.High,
        _ => false
    };

    private static bool TryAdd(
        string id,
        IReadOnlyDictionary<string, SituationEvidence> evidence,
        ICollection<SituationHighlight> selected,
        ISet<string> selectedIds)
    {
        if (!selectedIds.Add(id) || !evidence.TryGetValue(id, out var item) ||
            string.IsNullOrWhiteSpace(item.TextZh) || item.TextZh.Length > 240)
        {
            selectedIds.Remove(id);
            return false;
        }
        selected.Add(new(item.TextZh, [id]));
        return true;
    }

    private static int SummaryLength(IReadOnlyList<SituationHighlight> selected) =>
        string.Join("；", selected.Select(item => item.TextZh.TrimEnd('。'))).Length + 1;

    private static IReadOnlyList<string> BuildUncertainties(SituationFactsV1 facts)
    {
        var values = new List<string>();
        if (facts.Alive.T is null || facts.Alive.CT is null)
            values.Add("存活人数信息不完整。");
        if (facts.TotalHealth.T is null || facts.TotalHealth.CT is null)
            values.Add("队伍生命信息不完整。");
        if (facts.Bomb.State == SituationBombState.Unknown)
            values.Add("C4 当前状态无法确认。");
        if (facts.Formation.T == SituationFormation.Unknown || facts.Formation.CT == SituationFormation.Unknown)
            values.Add("至少一方阵型因人数或位置不足无法确认。");
        if (facts.ContestedRegions is null)
            values.Add("区域信息不完整，争夺区域无法确认。");
        if (facts.ContactRisk == SituationContactRisk.Unknown)
            values.Add("敌我位置不足，即时接触风险无法确认。");
        if (facts.IsolatedSide == SituationIsolatedSide.Unknown)
            values.Add("队伍支援距离不足以完整判断孤立情况。");
        if (facts.SpatialAdvantage == SituationSpatialAdvantage.Uncertain)
            values.Add("当前空间证据不足或处于非决定区间。");

        var codes = facts.DataQuality.Select(item => item.Code).ToHashSet(StringComparer.Ordinal);
        if (codes.Contains(SituationDataQualityCodes.MissingFrame) ||
            codes.Contains(SituationDataQualityCodes.StaleFrame))
            values.Add("目标帧存在缺失或滞后。");
        if (codes.Contains(SituationDataQualityCodes.RosterIncomplete) ||
            codes.Contains(SituationDataQualityCodes.PositionInvalid))
            values.Add("阵容或位置覆盖不完整。");
        if (codes.Contains(SituationDataQualityCodes.HistoryGap) ||
            codes.Contains(SituationDataQualityCodes.HistoryTruncated))
            values.Add("历史轨迹不完整，趋势线索受到限制。");
        if (codes.Contains(SituationDataQualityCodes.HealthStateConflict))
            values.Add("生命与存活状态存在冲突标记。");
        return values.Distinct(StringComparer.Ordinal).ToArray();
    }
}

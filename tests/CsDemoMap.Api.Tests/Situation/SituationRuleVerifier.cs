using System.Globalization;
using CsDemoMap.Api.Models;

namespace CsDemoMap.Api.Tests;

internal static class SituationRuleVerifier
{
    public static void Verify()
    {
        var checks = 0;
        var candidate = SituationAnalysisRuleLoader.LoadCandidate();
        Check(candidate.Rules.AnalysisRuleVersion == "situation-analysis-rules-v1-candidate-2",
            "candidate version", ref checks);
        Check(candidate.Sha256.Length == 64 && candidate.Sha256.All(IsLowerHex),
            "candidate hash", ref checks);
        Check(candidate.CanonicalJson == SituationAnalysisRuleLoader.LoadCandidate().CanonicalJson,
            "candidate repeat load", ref checks);
        CheckThrows(
            () => SituationAnalysisRuleLoader.ParseForVerification(
                candidate.CanonicalJson[..^1] + ",\"unexpected\":1}", "invalid-extra.json"),
            "unknown config field rejected", ref checks);
        CheckThrows(
            () => SituationAnalysisRuleLoader.ParseForVerification(
                candidate.CanonicalJson[..^1] + ",\"map\":\"de_mirage\"}", "invalid-duplicate.json"),
            "duplicate config field rejected", ref checks);
        CheckThrows(
            () => SituationAnalysisRuleLoader.ParseForVerification(
                candidate.CanonicalJson.Replace("\"formation\":{", "\"formationMissing\":{", StringComparison.Ordinal),
                "invalid-missing.json"),
            "missing config field rejected", ref checks);
        CheckThrows(
            () => SituationAnalysisRuleLoader.Validate(candidate.Rules with
            {
                Contact = candidate.Rules.Contact with { MediumDistance = candidate.Rules.Contact.HighDistance }
            }),
            "conflicting contact thresholds rejected", ref checks);
        CheckThrows(
            () => SituationAnalysisRuleLoader.Validate(candidate.Rules with
            {
                AnalysisRuleVersion = "INVALID"
            }),
            "invalid rule version rejected", ref checks);
        var previous = SituationAnalysisRuleLoader.LoadEmbeddedForVerification(
            "situation-analysis-rules-v1-candidate-1.json");
        var frozen = SituationAnalysisRuleLoader.LoadFrozen();
        Check(frozen.Rules.AnalysisRuleVersion == "situation-analysis-rules-v1",
            "approved frozen version", ref checks);
        Check(frozen.Sha256.Length == 64 && frozen.Sha256.All(IsLowerHex),
            "frozen hash", ref checks);
        Check(SituationCanonicalJson.Serialize(frozen.Rules with
              { AnalysisRuleVersion = candidate.Rules.AnalysisRuleVersion }) == candidate.CanonicalJson,
            "frozen rules equal approved candidate except version", ref checks);
        Check(frozen.Sha256 == "afa19d686b4c5ade2a53b8bdfd0655965d5230d032d39ea0bb8685e7929daa35",
            "frozen rules hash unchanged", ref checks);
        var analyzer = new SituationDeterministicAnalyzer(candidate);
        var grouped = analyzer.Analyze(BuildScene(
            [
                P("T1", SituationSide.T, 0.20, 0.20, "T Spawn", 0.19, 0.20),
                P("T2", SituationSide.T, 0.24, 0.20, "T Spawn", 0.23, 0.20),
                P("CT1", SituationSide.CT, 0.70, 0.70, "CT Spawn", 0.69, 0.70),
                P("CT2", SituationSide.CT, 0.74, 0.70, "CT Spawn", 0.73, 0.70)
            ], SituationBombState.Defused));
        Check(grouped.Facts.Facts.Formation.T == SituationFormation.Grouped &&
              grouped.Facts.Facts.Formation.CT == SituationFormation.Grouped,
            "grouped formation", ref checks);
        Check(grouped.Facts.Facts.ContestedRegions is { Count: 0 },
            "known empty contested regions", ref checks);
        Check(grouped.Facts.Facts.ContactRisk == SituationContactRisk.Low,
            "low contact risk", ref checks);
        Check(grouped.Facts.Facts.IsolatedSide == SituationIsolatedSide.None,
            "no isolated side", ref checks);
        Check(grouped.Facts.Facts.SpatialAdvantage == SituationSpatialAdvantage.Even,
            $"even spatial conditions ({grouped.Facts.Facts.SpatialAdvantage}, score {grouped.Facts.Diagnostics.SpatialScore}, components {grouped.Facts.Diagnostics.SpatialComponents.Count})", ref checks);
        Check(grouped.Facts.Facts.Confidence == SituationConfidence.High,
            "high confidence with complete inputs", ref checks);

        var split = analyzer.Analyze(BuildScene(
            [
                P("T1", SituationSide.T, 0.10, 0.20, "A"),
                P("T2", SituationSide.T, 0.12, 0.20, "A"),
                P("T3", SituationSide.T, 0.55, 0.65, "B"),
                P("T4", SituationSide.T, 0.57, 0.65, "B"),
                P("CT1", SituationSide.CT, 0.80, 0.20, "C"),
                P("CT2", SituationSide.CT, 0.82, 0.20, "C")
            ]));
        Check(split.Facts.Facts.Formation.T == SituationFormation.Split,
            "split formation", ref checks);

        var spread = analyzer.Analyze(BuildScene(
            [
                P("T1", SituationSide.T, 0.10, 0.10, "A"),
                P("T2", SituationSide.T, 0.45, 0.45, "B"),
                P("T3", SituationSide.T, 0.80, 0.80, "C"),
                P("CT1", SituationSide.CT, 0.15, 0.80, "D"),
                P("CT2", SituationSide.CT, 0.18, 0.80, "D")
            ]));
        Check(spread.Facts.Facts.Formation.T == SituationFormation.Spread,
            "spread formation", ref checks);

        var single = analyzer.Analyze(BuildScene(
            [P("T1", SituationSide.T, 0.20, 0.20, "A"), P("CT1", SituationSide.CT, 0.70, 0.70, "B")]));
        Check(single.Facts.Facts.Formation.T == SituationFormation.Unknown &&
              single.Facts.Facts.Formation.CT == SituationFormation.Unknown,
            "single player formation unknown", ref checks);
        Check(single.Facts.Facts.IsolatedSide == SituationIsolatedSide.Both,
            "single survivor isolation", ref checks);

        var contested = analyzer.Analyze(BuildScene(
            [
                P("T1", SituationSide.T, 0.30, 0.30, "Mid"),
                P("T2", SituationSide.T, 0.32, 0.30, "Mid"),
                P("CT1", SituationSide.CT, 0.35, 0.30, "Mid"),
                P("CT2", SituationSide.CT, 0.37, 0.30, "Mid")
            ]));
        Check(contested.Facts.Facts.ContestedRegions?.SequenceEqual(["Mid"]) == true,
            "same-region contest", ref checks);
        Check(contested.Facts.Facts.ContactRisk == SituationContactRisk.High,
            "high contact risk", ref checks);

        var medium = analyzer.Analyze(BuildScene(
            [
                P("T1", SituationSide.T, 0.30, 0.30, "Left", headingX: 1, headingY: 0),
                P("T2", SituationSide.T, 0.28, 0.30, "Left"),
                P("CT1", SituationSide.CT, 0.40, 0.30, "Right"),
                P("CT2", SituationSide.CT, 0.42, 0.30, "Right")
            ]));
        Check(medium.Facts.Facts.ContactRisk == SituationContactRisk.Medium,
            "medium contact risk", ref checks);

        var siteA = Site(SituationSite.A);
        var tPressure = analyzer.Analyze(BuildScene(
            [
                P("T1", SituationSide.T, siteA.X + 0.02, siteA.Y, "A Site"),
                P("T2", SituationSide.T, siteA.X - 0.02, siteA.Y, "A Site"),
                P("CT1", SituationSide.CT, 0.80, 0.80, "CT Spawn"),
                P("CT2", SituationSide.CT, 0.82, 0.80, "CT Spawn")
            ], SituationBombState.Carried));
        Check(tPressure.Facts.Facts.Pressure == new SituationPressure(SituationSide.T, SituationSite.A),
            "pre-plant T pressure", ref checks);

        var ctPressure = analyzer.Analyze(BuildScene(
            [
                P("T1", SituationSide.T, 0.80, 0.80, "T Hold"),
                P("T2", SituationSide.T, 0.82, 0.80, "T Hold"),
                P("CT1", SituationSide.CT, siteA.X + 0.02, siteA.Y, "A Site"),
                P("CT2", SituationSide.CT, siteA.X - 0.02, siteA.Y, "A Site")
            ], SituationBombState.Planted, SituationSite.A));
        Check(ctPressure.Facts.Facts.Pressure == new SituationPressure(SituationSide.CT, SituationSite.A),
            "post-plant CT pressure", ref checks);

        var isolatedT = analyzer.Analyze(BuildScene(
            [
                P("T1", SituationSide.T, 0.20, 0.20, "A"),
                P("T2", SituationSide.T, 0.80, 0.20, "B"),
                P("CT1", SituationSide.CT, 0.25, 0.20, "C"),
                P("CT2", SituationSide.CT, 0.26, 0.20, "C")
            ]));
        Check(isolatedT.Facts.Facts.IsolatedSide == SituationIsolatedSide.T,
            "T isolated", ref checks);

        var strongT = analyzer.Analyze(BuildScene(
            [
                P("T1", SituationSide.T, 0.20, 0.20, "A"),
                P("T2", SituationSide.T, 0.22, 0.20, "A"),
                P("T3", SituationSide.T, 0.24, 0.20, "A"),
                P("CT1", SituationSide.CT, 0.70, 0.70, "B")
            ]));
        Check(strongT.Facts.Facts.SpatialAdvantage == SituationSpatialAdvantage.T,
            "T spatial advantage", ref checks);

        var unknownRegionScene = BuildScene(
            [
                P("T1", SituationSide.T, 0.20, 0.20, null),
                P("T2", SituationSide.T, 0.22, 0.20, "A"),
                P("CT1", SituationSide.CT, 0.70, 0.70, "B"),
                P("CT2", SituationSide.CT, 0.72, 0.70, "B")
            ], SituationBombState.Defused,
            quality: [new(SituationDataQualityCodes.RegionUnknown, ["/players/0/region"], SituationQualitySeverity.Warning)]);
        var unknownRegion = analyzer.Analyze(unknownRegionScene);
        Check(unknownRegion.Facts.Facts.ContestedRegions is null,
            "unknown region contest is null", ref checks);
        Check(unknownRegion.Facts.Facts.Confidence == SituationConfidence.Medium,
            "relevant warning lowers confidence", ref checks);

        var floorInfo = analyzer.Analyze(BuildScene(
            [
                P("T1", SituationSide.T, 0.20, 0.20, "A"),
                P("T2", SituationSide.T, 0.22, 0.20, "A"),
                P("CT1", SituationSide.CT, 0.70, 0.70, "B"),
                P("CT2", SituationSide.CT, 0.72, 0.70, "B")
            ], SituationBombState.Defused,
            quality: [new(SituationDataQualityCodes.FloorUnknown, ["/players"], SituationQualitySeverity.Info)]));
        Check(floorInfo.Facts.Facts.Confidence == SituationConfidence.High,
            "irrelevant floor info preserves confidence", ref checks);
        var legacyEquipment = analyzer.Analyze(BuildScene(
            [
                P("T1", SituationSide.T, 0.20, 0.20, "A"),
                P("T2", SituationSide.T, 0.24, 0.20, "A"),
                P("CT1", SituationSide.CT, 0.70, 0.70, "B"),
                P("CT2", SituationSide.CT, 0.74, 0.70, "B")
            ], SituationBombState.Defused,
            quality: [new(SituationDataQualityCodes.LegacyDefaultAmbiguous,
                ["/players/0/armor", "/players/1/velocity", "/teams/ct/totalMoney"],
                SituationQualitySeverity.Warning)]));
        Check(legacyEquipment.Facts.Facts.Confidence == SituationConfidence.High,
            "irrelevant legacy equipment quality preserves confidence", ref checks);

        var oneIncompleteRule = analyzer.Analyze(BuildScene(
            [
                P("T1", SituationSide.T, 0.20, 0.20, null),
                P("T2", SituationSide.T, 0.24, 0.20, "A"),
                P("CT1", SituationSide.CT, 0.70, 0.70, "B"),
                P("CT2", SituationSide.CT, 0.74, 0.70, "B")
            ], SituationBombState.Defused));
        Check(oneIncompleteRule.Facts.Diagnostics.UnknownCount == 1 &&
              !oneIncompleteRule.Facts.Diagnostics.HasRelevantError &&
              !oneIncompleteRule.Facts.Diagnostics.HasRelevantWarning &&
              oneIncompleteRule.Facts.Facts.Confidence == SituationConfidence.Medium,
            "one incomplete rule gives medium confidence", ref checks);

        var twoIncompleteRules = analyzer.Analyze(BuildScene(
            [
                P("T1", SituationSide.T, 0.20, 0.20, null),
                P("CT1", SituationSide.CT, 0.70, 0.70, "B"),
                P("CT2", SituationSide.CT, 0.74, 0.70, "B")
            ], SituationBombState.Defused));
        Check(twoIncompleteRules.Facts.Diagnostics.UnknownCount == 2 &&
              twoIncompleteRules.Facts.Facts.Confidence == SituationConfidence.Low,
            "two incomplete rules give low confidence", ref checks);

        var relevantError = analyzer.Analyze(BuildScene(
            [
                P("T1", SituationSide.T, 0.20, 0.20, null),
                P("T2", SituationSide.T, 0.24, 0.20, "A"),
                P("CT1", SituationSide.CT, 0.70, 0.70, "B"),
                P("CT2", SituationSide.CT, 0.74, 0.70, "B")
            ], SituationBombState.Defused,
            quality: [new(SituationDataQualityCodes.RegionUnknown,
                ["/players/0/region"], SituationQualitySeverity.Error)]));
        Check(relevantError.Facts.Diagnostics.UnknownCount == 1 &&
              relevantError.Facts.Diagnostics.HasRelevantError &&
              relevantError.Facts.Facts.Confidence == SituationConfidence.Low,
            "relevant error takes priority over medium confidence", ref checks);

        const double precisionUnit = 0.000001;
        var formationAtBoundary = BuildScene(
            [
                P("T1", SituationSide.T, 0.20, 0.20, "A"),
                P("T2", SituationSide.T, 0.30, 0.20, "A"),
                P("CT1", SituationSide.CT, 0.70, 0.70, "B"),
                P("CT2", SituationSide.CT, 0.74, 0.70, "B")
            ], SituationBombState.Defused);
        Check(analyzer.Analyze(formationAtBoundary).Facts.Facts.Formation.T == SituationFormation.Grouped,
            "formation link exact boundary is included", ref checks);
        var formationOutside = BuildScene(
            [
                P("T1", SituationSide.T, 0.20, 0.20, "A"),
                P("T2", SituationSide.T, 0.30 + precisionUnit, 0.20, "A"),
                P("CT1", SituationSide.CT, 0.70, 0.70, "B"),
                P("CT2", SituationSide.CT, 0.74, 0.70, "B")
            ], SituationBombState.Defused);
        Check(analyzer.Analyze(formationOutside).Facts.Facts.Formation.T == SituationFormation.Spread,
            "formation link one precision unit outside", ref checks);

        var contactBoundary = analyzer.Analyze(BuildScene(
            [
                P("T1", SituationSide.T, 0.20, 0.20, "A"),
                P("T2", SituationSide.T, 0.05, 0.80, "A2"),
                P("CT1", SituationSide.CT, 0.26, 0.20, "B"),
                P("CT2", SituationSide.CT, 0.95, 0.80, "B2")
            ], SituationBombState.Defused));
        Check(contactBoundary.Facts.Facts.ContactRisk == SituationContactRisk.High &&
              contactBoundary.Facts.Facts.Confidence == SituationConfidence.Medium,
            "contact high exact boundary and confidence band", ref checks);
        var contactOutside = analyzer.Analyze(BuildScene(
            [
                P("T1", SituationSide.T, 0.20, 0.20, "A"),
                P("T2", SituationSide.T, 0.05, 0.80, "A2"),
                P("CT1", SituationSide.CT, 0.26 + precisionUnit, 0.20, "B"),
                P("CT2", SituationSide.CT, 0.95, 0.80, "B2")
            ], SituationBombState.Defused));
        Check(contactOutside.Facts.Facts.ContactRisk == SituationContactRisk.Medium,
            "contact high one precision unit outside", ref checks);

        var confidenceBandX = 0.20 + candidate.Rules.Contact.HighDistance +
            candidate.Rules.Confidence.BoundaryBand;
        var confidenceBandInside = analyzer.Analyze(BuildScene(
            [
                P("T1", SituationSide.T, 0.20, 0.20, "A"),
                P("T2", SituationSide.T, 0.05, 0.80, "A2"),
                P("CT1", SituationSide.CT, confidenceBandX - precisionUnit, 0.20, "B"),
                P("CT2", SituationSide.CT, 0.95, 0.80, "B2")
            ], SituationBombState.Defused));
        Check(confidenceBandInside.Facts.Facts.ContactRisk == SituationContactRisk.Medium &&
              confidenceBandInside.Facts.Facts.Confidence == SituationConfidence.Medium,
            "confidence boundary band inside", ref checks);
        var confidenceBandExact = analyzer.Analyze(BuildScene(
            [
                P("T1", SituationSide.T, 0.20, 0.20, "A"),
                P("T2", SituationSide.T, 0.05, 0.80, "A2"),
                P("CT1", SituationSide.CT, confidenceBandX, 0.20, "B"),
                P("CT2", SituationSide.CT, 0.95, 0.80, "B2")
            ], SituationBombState.Defused));
        Check(confidenceBandExact.Facts.Facts.ContactRisk == SituationContactRisk.Medium &&
              confidenceBandExact.Facts.Facts.Confidence == SituationConfidence.Medium,
            "confidence boundary band exact configured edge", ref checks);
        var confidenceBandOutside = analyzer.Analyze(BuildScene(
            [
                P("T1", SituationSide.T, 0.20, 0.20, "A"),
                P("T2", SituationSide.T, 0.05, 0.80, "A2"),
                P("CT1", SituationSide.CT, confidenceBandX + 2 * precisionUnit, 0.20, "B"),
                P("CT2", SituationSide.CT, 0.95, 0.80, "B2")
            ], SituationBombState.Defused));
        Check(confidenceBandOutside.Facts.Facts.ContactRisk == SituationContactRisk.Medium &&
              confidenceBandOutside.Facts.Facts.Confidence == SituationConfidence.High,
            "confidence boundary band outside", ref checks);

        var siteBoundary = Site(SituationSite.A);
        var pressureBoundary = analyzer.Analyze(BuildScene(
            [
                P("T1", SituationSide.T, siteBoundary.X + candidate.Rules.Pressure.SiteRadius, siteBoundary.Y, "A"),
                P("T2", SituationSide.T, siteBoundary.X - candidate.Rules.Pressure.SiteRadius, siteBoundary.Y, "A"),
                P("CT1", SituationSide.CT, 0.05, 0.95, "B"),
                P("CT2", SituationSide.CT, 0.08, 0.95, "B")
            ]));
        Check(pressureBoundary.Facts.Facts.Pressure.Site == SituationSite.A,
            "pressure radius exact boundary is included", ref checks);
        var pressureOutside = analyzer.Analyze(BuildScene(
            [
                P("T1", SituationSide.T, siteBoundary.X + candidate.Rules.Pressure.SiteRadius + precisionUnit, siteBoundary.Y, "A"),
                P("T2", SituationSide.T, siteBoundary.X - candidate.Rules.Pressure.SiteRadius - precisionUnit, siteBoundary.Y, "A"),
                P("CT1", SituationSide.CT, 0.05, 0.95, "B"),
                P("CT2", SituationSide.CT, 0.08, 0.95, "B")
            ]));
        Check(pressureOutside.Facts.Facts.Pressure.Site is null,
            "pressure radius one precision unit outside", ref checks);

        var isolationBoundary = analyzer.Analyze(BuildScene(
            [
                P("T1", SituationSide.T, 0.20, 0.20, "A"),
                P("T2", SituationSide.T, 0.38, 0.20, "A"),
                P("CT1", SituationSide.CT, 0.20, 0.34, "B"),
                P("CT2", SituationSide.CT, 0.22, 0.34, "B")
            ], SituationBombState.Defused));
        Check(isolationBoundary.Facts.Facts.IsolatedSide == SituationIsolatedSide.T,
            "isolation exact ally and enemy margin boundaries", ref checks);
        var isolationInside = analyzer.Analyze(BuildScene(
            [
                P("T1", SituationSide.T, 0.20, 0.20, "A"),
                P("T2", SituationSide.T, 0.38 - precisionUnit, 0.20, "A"),
                P("CT1", SituationSide.CT, 0.20, 0.34, "B"),
                P("CT2", SituationSide.CT, 0.22, 0.34, "B")
            ], SituationBombState.Defused));
        Check(isolationInside.Facts.Facts.IsolatedSide == SituationIsolatedSide.None,
            "isolation ally distance one precision unit below", ref checks);

        var strongScore = Math.Abs(strongT.Facts.Diagnostics.SpatialScore!.Value);
        var exactSpatialRules = candidate.Rules with
        {
            Spatial = candidate.Rules.Spatial with { DecisiveScore = strongScore }
        };
        Check(AnalyzeWithRules(BuildScene(
            [
                P("T1", SituationSide.T, 0.20, 0.20, "A"),
                P("T2", SituationSide.T, 0.22, 0.20, "A"),
                P("T3", SituationSide.T, 0.24, 0.20, "A"),
                P("CT1", SituationSide.CT, 0.70, 0.70, "B")
            ]), exactSpatialRules).Facts.Facts.SpatialAdvantage == SituationSpatialAdvantage.T,
            "spatial decisive exact boundary is included", ref checks);
        var outsideSpatialRules = candidate.Rules with
        {
            Spatial = candidate.Rules.Spatial with { DecisiveScore = strongScore + precisionUnit }
        };
        Check(AnalyzeWithRules(BuildScene(
            [
                P("T1", SituationSide.T, 0.20, 0.20, "A"),
                P("T2", SituationSide.T, 0.22, 0.20, "A"),
                P("T3", SituationSide.T, 0.24, 0.20, "A"),
                P("CT1", SituationSide.CT, 0.70, 0.70, "B")
            ]), outsideSpatialRules).Facts.Facts.SpatialAdvantage == SituationSpatialAdvantage.Uncertain,
            "spatial decisive one precision unit outside", ref checks);

        Check(grouped.Narrative.Narrative.Highlights.Count is >= 2 and <= 4,
            "template highlight count", ref checks);
        Check(grouped.Narrative.Narrative.Highlights.All(highlight =>
                highlight.EvidenceIds.All(id => grouped.Facts.Facts.Evidence.Any(item => item.Id == id))),
            "template evidence references", ref checks);
        Check(!ContainsForbidden(grouped.Narrative.CanonicalJson),
            "template forbidden terms", ref checks);
        SituationContractValidator.ValidateTemplate(grouped.Narrative.Narrative, grouped.Facts.Facts);
        checks++;

        var changedFacts = grouped.Facts.Facts with { Alive = new(5, grouped.Facts.Facts.Alive.CT) };
        CheckThrows(
            () => SituationContractValidator.Validate(
                changedFacts, BuildScene(
                [
                    P("T1", SituationSide.T, 0.20, 0.20, "T Spawn", 0.19, 0.20),
                    P("T2", SituationSide.T, 0.24, 0.20, "T Spawn", 0.23, 0.20),
                    P("CT1", SituationSide.CT, 0.70, 0.70, "CT Spawn", 0.69, 0.70),
                    P("CT2", SituationSide.CT, 0.74, 0.70, "CT Spawn", 0.73, 0.70)
                ], SituationBombState.Defused), candidate.Rules.AnalysisRuleVersion),
            "direct fact mismatch rejected", ref checks);

        for (var iteration = 0; iteration < 100; iteration++)
        {
            var repeat = analyzer.Analyze(BuildScene(
                [
                    P("T1", SituationSide.T, 0.20, 0.20, "T Spawn", 0.19, 0.20),
                    P("T2", SituationSide.T, 0.24, 0.20, "T Spawn", 0.23, 0.20),
                    P("CT1", SituationSide.CT, 0.70, 0.70, "CT Spawn", 0.69, 0.70),
                    P("CT2", SituationSide.CT, 0.74, 0.70, "CT Spawn", 0.73, 0.70)
                ], SituationBombState.Defused));
            Check(repeat.Facts.Sha256 == grouped.Facts.Sha256 &&
                  repeat.Narrative.Sha256 == grouped.Narrative.Sha256,
                $"repeat determinism {iteration + 1}", ref checks);
        }

        var originalCulture = CultureInfo.CurrentCulture;
        var originalUiCulture = CultureInfo.CurrentUICulture;
        try
        {
            foreach (var cultureName in new[] { "zh-CN", "en-US", "fr-FR", "tr-TR" })
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(cultureName);
                CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(cultureName);
                var repeat = analyzer.Analyze(BuildScene(
                    [
                        P("T1", SituationSide.T, 0.20, 0.20, "T Spawn", 0.19, 0.20),
                        P("T2", SituationSide.T, 0.24, 0.20, "T Spawn", 0.23, 0.20),
                        P("CT1", SituationSide.CT, 0.70, 0.70, "CT Spawn", 0.69, 0.70),
                        P("CT2", SituationSide.CT, 0.74, 0.70, "CT Spawn", 0.73, 0.70)
                    ], SituationBombState.Defused));
                Check(repeat.Facts.Sha256 == grouped.Facts.Sha256 &&
                      repeat.Narrative.Sha256 == grouped.Narrative.Sha256,
                    $"culture determinism {cultureName}", ref checks);
            }
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
            CultureInfo.CurrentUICulture = originalUiCulture;
        }

        var reorderedQualityScene = unknownRegionScene with
        {
            DataQuality = unknownRegionScene.DataQuality.Reverse().ToArray()
        };
        Check(analyzer.Analyze(reorderedQualityScene).Facts.Sha256 == unknownRegion.Facts.Sha256,
            "quality order determinism", ref checks);

        Console.WriteLine($"Situation rule checks passed: {checks}; candidate SHA-256: {candidate.Sha256}; previous candidate SHA-256: {previous.Sha256}; frozen SHA-256: {frozen.Sha256}");
    }

    private static MinimapSceneV1 BuildScene(
        IReadOnlyList<PlayerSpec> specs,
        SituationBombState bombState = SituationBombState.Carried,
        SituationSite? bombSite = null,
        IReadOnlyList<SituationDataQuality>? quality = null)
    {
        const int tick = 640;
        const int tickRate = 64;
        var players = specs
            .OrderBy(spec => spec.Side)
            .ThenBy(spec => spec.Slot, StringComparer.Ordinal)
            .Select(spec => BuildPlayer(spec, tick, tickRate))
            .ToArray();
        var geometry = BuildGeometry(players);
        var t = Team(players, SituationSide.T);
        var ct = Team(players, SituationSide.CT);
        var carrier = bombState is SituationBombState.Carried or SituationBombState.Planting
            ? players.FirstOrDefault(player => player.Side == SituationSide.T)?.Slot
            : null;
        var defuser = bombState == SituationBombState.Defusing
            ? players.FirstOrDefault(player => player.Side == SituationSide.CT)?.Slot
            : null;
        var phase = bombState is SituationBombState.Planted or SituationBombState.Defusing
            ? SituationRoundPhase.PostPlant
            : SituationRoundPhase.Live;
        var scene = new MinimapSceneV1(
            SituationContractVersions.Scene,
            SituationSceneBuilder.BuilderVersion,
            SituationSceneBuilder.GeometryVersion,
            "structured",
            "de_mirage",
            "fixture-stage3",
            0,
            tick,
            tick,
            tickRate,
            new("s0-a1", 0, 1, phase, 10, 105, new(0, 0), "fixture"),
            players,
            new(t, ct),
            new(
                bombState,
                carrier,
                defuser,
                bombSite,
                null,
                null,
                SituationRegionSource.Unknown,
                bombState is SituationBombState.Planted or SituationBombState.Defusing ? 30 : null,
                bombState == SituationBombState.Defusing ? 5 : null),
            [],
            [],
            geometry,
            quality ?? []);
        SituationContractValidator.Validate(scene);
        return scene;
    }

    private static SituationPlayer BuildPlayer(PlayerSpec spec, int tick, int tickRate)
    {
        var current = spec.X is null || spec.Y is null ? null : new SituationVec3(spec.X.Value, spec.Y.Value, 0);
        IReadOnlyList<SituationTrajectoryPoint> points;
        int fromTick;
        if (current is null)
        {
            fromTick = tick;
            points = [];
        }
        else if (spec.PastX is { } pastX && spec.PastY is { } pastY)
        {
            fromTick = tick - 128;
            points = [new(fromTick, new(pastX, pastY, 0)), new(tick, current)];
        }
        else
        {
            fromTick = tick - 128;
            points = [new(fromTick, current), new(tick, current)];
        }
        return new(
            spec.Slot,
            spec.Side,
            current,
            SituationFloor.Unknown,
            spec.Region,
            spec.Region is null ? SituationRegionSource.Unknown : SituationRegionSource.DemoPlaceName,
            spec.HeadingX is { } hx && spec.HeadingY is { } hy ? new(hx, hy) : null,
            current is null ? null : new SituationVec3(0, 0, 0),
            spec.Health,
            100,
            SituationWeaponCategory.Rifle,
            ZerosUtility(),
            new(fromTick, tick, Math.Round((tick - fromTick) / (double)tickRate, 6), points));
    }

    private static SituationTeamSummary Team(IReadOnlyList<SituationPlayer> players, SituationSide side)
    {
        var team = players.Where(player => player.Side == side).ToArray();
        return new(
            team.Length,
            team.Sum(player => player.Health),
            team.Sum(player => player.Armor),
            0,
            0,
            0,
            0,
            ZerosUtility(),
            new(0, 0, team.Length, 0, 0, 0, 0, 0, 0, 0, 0));
    }

    private static SituationGeometry BuildGeometry(IReadOnlyList<SituationPlayer> players)
    {
        var occupancy = players
            .Where(player => player.Region is not null)
            .GroupBy(player => player.Region!, StringComparer.Ordinal)
            .Select(group => new SituationRegionOccupancy(
                group.Key,
                group.Count(player => player.Side == SituationSide.T),
                group.Count(player => player.Side == SituationSide.CT)))
            .OrderBy(item => item.Region, StringComparer.Ordinal)
            .ToArray();
        var a = Site(SituationSite.A);
        var b = Site(SituationSite.B);
        var distances = players
            .Select(player => new SituationPlayerDistances(
                player.Slot,
                Distance(player.Position, a),
                Distance(player.Position, b),
                Nearest(player, players.Where(other => other.Side == player.Side && other.Slot != player.Slot)),
                Nearest(player, players.Where(other => other.Side != player.Side))))
            .OrderBy(item => item.Slot, StringComparer.Ordinal)
            .ToArray();
        return new(occupancy, distances);
    }

    private static SituationUtilityCounts ZerosUtility() => new(0, 0, 0, 0, 0, 0);

    private static SituationVec3 Site(SituationSite site)
    {
        var geometry = MapFeatureGeometries.Find("de_mirage")!;
        var world = site == SituationSite.A ? geometry.SiteA : geometry.SiteB;
        var point = geometry.Normalize(world.X, world.Y);
        return new(point.X, point.Y, 0);
    }

    private static SituationDeterministicAnalysisResult AnalyzeWithRules(
        MinimapSceneV1 scene,
        SituationAnalysisRuleSet rules)
    {
        SituationAnalysisRuleLoader.Validate(rules);
        var canonical = SituationCanonicalJson.Serialize(rules);
        var load = new SituationAnalysisRuleLoadResult(
            rules, canonical, SituationCanonicalJson.Sha256(rules), "synthetic-rule-boundary");
        return new SituationDeterministicAnalyzer(load).Analyze(scene);
    }

    private static double? Nearest(SituationPlayer player, IEnumerable<SituationPlayer> candidates)
    {
        if (player.Position is null)
            return null;
        var values = candidates.Select(candidate => Distance(player.Position, candidate.Position))
            .Where(value => value is not null).Select(value => value!.Value).ToArray();
        return values.Length == 0 ? null : values.Min();
    }

    private static double? Distance(SituationVec3? left, SituationVec3? right)
    {
        if (left is null || right is null)
            return null;
        var dx = left.X - right.X;
        var dy = left.Y - right.Y;
        return Math.Round(Math.Sqrt(dx * dx + dy * dy), 6, MidpointRounding.AwayFromZero);
    }

    private static PlayerSpec P(
        string slot,
        SituationSide side,
        double? x,
        double? y,
        string? region,
        double? pastX = null,
        double? pastY = null,
        double? headingX = null,
        double? headingY = null,
        int health = 100) => new(slot, side, x, y, region, pastX, pastY, headingX, headingY, health);

    private static bool ContainsForbidden(string text) => new[]
        { "steam", ".dem", "胜率", "将会", "会赢", "建议", "应该", "应当", "%" }
        .Any(term => text.Contains(term, StringComparison.OrdinalIgnoreCase));

    private static bool IsLowerHex(char value) => value is >= '0' and <= '9' or >= 'a' and <= 'f';

    private static void Check(bool condition, string label, ref int checks)
    {
        if (!condition)
            throw new InvalidOperationException($"Situation rule check failed: {label}.");
        checks++;
    }

    private static void CheckThrows(Action action, string label, ref int checks)
    {
        try
        {
            action();
        }
        catch (InvalidDataException)
        {
            checks++;
            return;
        }
        throw new InvalidOperationException($"Situation rule check failed: {label}.");
    }

    private sealed record PlayerSpec(
        string Slot,
        SituationSide Side,
        double? X,
        double? Y,
        string? Region,
        double? PastX,
        double? PastY,
        double? HeadingX,
        double? HeadingY,
        int Health);
}

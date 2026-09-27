using System.Globalization;
using CsDemoMap.Api.Models;
using CsDemoMap.Api.Services;

namespace CsDemoMap.Api.Tests;

internal static class SituationVerifiedExposureVerifier
{
    internal static void Verify()
    {
        var count = 0;
        void Check(bool ok, string name) { if (!ok) throw new InvalidOperationException("Verified exposure: " + name); count++; }
        var oldLoad = SituationAnalysisRuleLoader.LoadExtendedLocalPeek();
        var load = SituationAnalysisRuleLoader.LoadVerifiedExposure();
        Check(SituationCanonicalJson.Sha256(oldLoad.Rules.Visibility) == SituationCanonicalJson.Sha256(load.Rules.Visibility), "geometry and search parameters unchanged");
        var scene = Scene();
        foreach (var mode in new[] { "first", "unreachable-first", "reverse", "blocked", "unknown", "direct", "no-ground" })
        {
            var beforeQuery = new Probe(scene, mode);
            var afterQuery = new Probe(scene, mode);
            var before = new SituationDeterministicAnalyzer(oldLoad, beforeQuery).Analyze(scene);
            var after = new SituationDeterministicAnalyzer(load, afterQuery).Analyze(scene);
            Check(beforeQuery.Calls.SequenceEqual(afterQuery.Calls), mode + " identical query order and count");
            var restored = after.Facts.Facts with { AnalysisRuleVersion = before.Facts.Facts.AnalysisRuleVersion, Evidence = before.Facts.Facts.Evidence };
            Check(SituationCanonicalJson.Sha256(restored) == before.Facts.Sha256, mode + " factual labels unchanged");
            var decisions = after.Facts.Diagnostics.Decisions;
            var found = mode is "first" or "unreachable-first" or "reverse";
            Check(decisions.ContainsKey("contact-local-peek-distance-units") == found, mode + " distance only for reachable local exposure");
            if (!found) continue;
            var expected = mode == "unreachable-first" ? 64 : 256;
            Check(double.Parse(decisions["contact-local-peek-distance-units"], CultureInfo.InvariantCulture) == expected, mode + " horizontal world units (ignores slope Z)");
            Check(double.Parse(decisions["contact-local-peek-estimated-seconds"], CultureInfo.InvariantCulture) == expected / 200.0, mode + " time at 200 horizontal units per second");
            Check(decisions["contact-local-peek-moving-slot"] == (mode == "reverse" ? "CT1" : "T1"), mode + " moving player recorded");
            Check(decisions["contact-local-peek-distance-metric"] == "horizontal", mode + " explicit metric");
            var text = after.Facts.Facts.Evidence.Single(e => e.Id == "contact-risk").TextZh;
            Check(text.Contains("可能存在更短路径") && text.Contains("水平") && text.Length <= 240, mode + " honest, bounded narrative");
            Check(after.Narrative.Narrative.Highlights.Any(h => h.EvidenceIds.Contains("contact-risk")), mode + " visible in review summary");
            if (mode == "first") Check(afterQuery.Calls.Count(c => c.StartsWith("ray", StringComparison.Ordinal)) == 2, "stop at first valid target despite a later shorter target");
        }
        // Real mesh: annotation must not add ground, movement or visibility queries.
        var triangles = new CollisionTriangle[] {
            new(new(-1500,-1500,0),new(1500,-1500,0),new(1500,1500,0)),
            new(new(-1500,-1500,0),new(1500,1500,0),new(-1500,1500,0)),
            new(new(0,-1500,-100),new(0,0,-100),new(0,0,300)),
            new(new(0,-1500,-100),new(0,0,300),new(0,-1500,300)) };
        var mesh = new SituationCollisionMesh(triangles);
        var actual = new SituationDeterministicAnalyzer(load, new SituationVisibilityQuery(mesh, load.Rules.Visibility!)).Analyze(scene);
        Check(actual.Facts.Diagnostics.Decisions.ContainsKey("contact-local-peek-distance-units"), "real corner exposure recorded");
        Check(actual.Facts.Facts.ContactRisk == SituationContactRisk.Medium, "potential exposure remains medium");
        Check(actual.Facts.Sha256 == new SituationDeterministicAnalyzer(load, new SituationVisibilityQuery(mesh, load.Rules.Visibility!)).Analyze(scene).Facts.Sha256, "deterministic output");
        Console.WriteLine($"Verified exposure checks passed: {count}; first success, horizontal distance, estimated time, early exit and unchanged query sequence.");
    }

    internal static void VerifyCloseExposure()
    {
        var count = 0;
        void Check(bool ok, string name) { if (!ok) throw new InvalidOperationException("Close exposure: " + name); count++; }
        var prior = SituationAnalysisRuleLoader.LoadVerifiedExposure();
        var load = SituationAnalysisRuleLoader.LoadCloseExposure();
        var scene = Scene();
        foreach (var distance in new[] { 64.0, 128, 149.999999, 150, 150.000001, 192, 256, 600 })
        {
            var oldQuery = new Probe(scene, "first", distance);
            var query = new Probe(scene, "first", distance);
            var before = new SituationDeterministicAnalyzer(prior, oldQuery).Analyze(scene);
            var after = new SituationDeterministicAnalyzer(load, query).Analyze(scene);
            var high = distance <= 150;
            Check(before.Facts.Facts.ContactRisk == SituationContactRisk.Medium, "v7 unchanged");
            Check(after.Facts.Facts.ContactRisk == (high ? SituationContactRisk.High : SituationContactRisk.Medium), $"threshold {distance}");
            Check(oldQuery.Calls.SequenceEqual(query.Calls), "query order unchanged, no search for later 64-unit target");
            Check(after.Facts.Diagnostics.Decisions["contact-risk"] == (high ? "high" : "medium"), "diagnostic agrees");
            Check(after.Facts.Facts.Evidence.Single(e => e.Id == "contact-risk").TextZh.Contains(high ? "风险为高" : "风险为中"), "evidence agrees");
            Check(after.Facts.Facts.Confidence != SituationConfidence.High && !after.Facts.Diagnostics.SpatialComponents.ContainsKey("local"), "hypothesis confidence and current battle center unchanged");
        }
        foreach (var mode in new[] { "blocked", "unknown", "no-ground", "direct", "unreachable-first", "reverse" })
        {
            var oldQuery = new Probe(scene, mode, 150);
            var query = new Probe(scene, mode, 150);
            var before = new SituationDeterministicAnalyzer(prior, oldQuery).Analyze(scene);
            var after = new SituationDeterministicAnalyzer(load, query).Analyze(scene);
            Check(oldQuery.Calls.SequenceEqual(query.Calls), mode + " unchanged queries");
            Check(after.Facts.Facts.ContactRisk == (mode is "unreachable-first" or "reverse" ? SituationContactRisk.High : before.Facts.Facts.ContactRisk), mode + " only verified exposure upgraded");
        }
        foreach (var invalid in new[] {
            load.Rules with { AnalysisRuleVersion = SituationAnalysisRuleLoader.VerifiedExposureVersion },
            load.Rules with { Visibility = load.Rules.Visibility! with { LocalPeek = load.Rules.Visibility.LocalPeek! with { HighExposureDistance = null } } },
            load.Rules with { Visibility = load.Rules.Visibility! with { LocalPeek = load.Rules.Visibility.LocalPeek! with { HighExposureDistance = 151 } } }
        })
        {
            var rejected = false;
            try { SituationAnalysisRuleLoader.Validate(invalid); } catch (InvalidDataException) { rejected = true; }
            Check(rejected, "threshold version isolation");
        }
        Console.WriteLine($"Close exposure checks passed: {count}; inclusive 150-unit boundary, verified paths only, unchanged early exit and version isolation.");
    }

    private static MinimapSceneV1 Scene() => SituationRuleVerifier.BuildScene(new[] { -32.0, 32.0 }.Select((x, i) =>
        new SituationRuleVerifier.PlayerSpec(i == 0 ? "T1" : "CT1", i == 0 ? SituationSide.T : SituationSide.CT,
            (x + 3230) / 5120, (1713 + 200) / 5120.0, "same", null, null, null, null, 100)).ToArray());

    private sealed class Probe(MinimapSceneV1 scene, string mode, double distance = 256) : ISituationVisibilityQuery, ISituationLocalMoveQuery
    {
        internal List<string> Calls { get; } = [];
        private SituationVec3 T => scene.Players.Single(p => p.Slot == "T1").Position!;
        private SituationVec3 Ct => scene.Players.Single(p => p.Slot == "CT1").Position!;
        public SituationVisibility Query(SituationVec3 a, SituationVec3 b)
        {
            Calls.Add("ray:" + SituationCanonicalJson.Serialize(new[] { a, b }));
            if (mode == "unknown") return SituationVisibility.Unknown;
            if (mode == "direct") return SituationVisibility.Clear;
            return a == T || a == Ct || mode == "blocked" ? SituationVisibility.Blocked : SituationVisibility.Clear;
        }
        public IReadOnlyList<SituationVec3>? LocalTargets(SituationVec3 origin)
        {
            Calls.Add("targets:" + SituationCanonicalJson.Serialize(origin));
            if (mode == "no-ground") return null;
            return [origin with { Y = origin.Y - distance / 5120, Z = 192.0 / 5120 }, origin with { Y = origin.Y - 64.0 / 5120 }];
        }
        public bool CanMoveLocal(SituationVec3 origin, SituationVec3 target, double? maxDistance = null)
        {
            Calls.Add("move:" + SituationCanonicalJson.Serialize(new[] { origin, target }));
            if (mode == "reverse" && origin == T) return false;
            return mode != "unreachable-first" || target.Z == 0;
        }
    }
}

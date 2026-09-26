using CsDemoMap.Api.Models;

namespace CsDemoMap.Api.Tests;

internal static class SituationPositionPredictionVerifier
{
    internal static void Verify()
    {
        var checks = 0;
        void Check(bool ok, string name) { if (!ok) throw new InvalidOperationException("Prediction: " + name); checks++; }
        var load = SituationAnalysisRuleLoader.LoadPositionPrediction();
        var config = load.Rules.Visibility!.PositionPrediction!;
        var ground = Ground(-600,600);
        var corner = new SituationCollisionMesh(Wall(0).Concat(ground));
        var analyzer = AnalyzeWith(corner);
        var scene = Scene(-200, V(0,250), V(0,250));
        var previous = new SituationDeterministicAnalyzer(SituationAnalysisRuleLoader.LoadLocalPeek(),
            new SituationVisibilityQuery(corner,SituationAnalysisRuleLoader.LoadLocalPeek().Rules.Visibility!)).Analyze(scene);
        Check(previous.Facts.Facts.ContactRisk == SituationContactRisk.Low, "v3 cannot reach distant corner");
        var result = analyzer.Analyze(scene);
        Check(result.Facts.Facts.ContactRisk == SituationContactRisk.Medium, "both players emerge within one second");
        Check(result.Facts.Diagnostics.Decisions["contact-position-prediction"] == "1", "one-second endpoint included");
        Check(result.Facts.Facts.Confidence != SituationConfidence.High, "hypothesis confidence capped");
        Check(!result.Facts.Diagnostics.SpatialComponents.ContainsKey("local"), "no hypothetical battle center");
        Check(result.Facts.Facts.Evidence.Single(e=>e.Id=="contact-risk").SourcePaths.Count == 4, "both velocities and positions attributed");
        Check(result.Narrative.Narrative.Highlights.Any(h=>h.EvidenceIds.Contains("contact-risk")), "prediction in summary");
        Check(!analyzer.Analyze(Scene(-200,V(0,250),V(0,-250))).Facts.Diagnostics.Decisions.ContainsKey("contact-position-prediction"),
            "synchronous samples do not combine different times");
        Check(analyzer.Analyze(Scene(-200,V(0,-250),V(0,-250))).Facts.Facts.ContactRisk == SituationContactRisk.Low, "moving away stays low");
        Check(analyzer.Analyze(Scene(-300,V(0,250),V(0,250))).Facts.Facts.ContactRisk == SituationContactRisk.Low, "beyond horizon not invented");
        Check(analyzer.Analyze(Scene(-200,V(80,250),V(0,250))).Facts.Facts.ContactRisk == SituationContactRisk.Low, "prediction cannot move through wall");
        Check(AnalyzeWith(new SituationCollisionMesh(Wall(500).Concat(ground))).Analyze(scene).Facts.Facts.ContactRisk == SituationContactRisk.Low,
            "solid wall stays low");
        Check(!AnalyzeWith(new SituationCollisionMesh(Wall(0).Concat(Ground(-600,0)))).Analyze(scene)
            .Facts.Diagnostics.Decisions.ContainsKey("contact-position-prediction"), "unsupported destination rejected");
        Check(!AnalyzeWith(new SituationCollisionMesh(Wall(0).Concat(Ground(-600,-100)).Concat(Ground(-50,600)))).Analyze(scene)
            .Facts.Diagnostics.Decisions.ContainsKey("contact-position-prediction"), "intermediate floor gap rejected");
        Check(!AnalyzeWith(new SituationCollisionMesh(Wall(0))).Analyze(scene).Facts.Diagnostics.Decisions.ContainsKey("contact-position-prediction"),
            "unsupported origin rejected");
        Check(analyzer.Analyze(Scene(-200,null,V(0,250))).Facts.Facts.ContactRisk == SituationContactRisk.Low, "missing velocity keeps local fallback");
        Check(analyzer.Analyze(Scene(-16,null,null)).Facts.Facts.ContactRisk == SituationContactRisk.Medium, "local peek still works without velocity");
        Check(analyzer.Analyze(Scene(-16,V(0,250),V(0,0))).Facts.Diagnostics.Decisions.ContainsKey("contact-position-prediction"), "known stationary peer supported");
        var ambiguous = Scene(-16,V(0,250),V(0,0)) with { DataQuality = [new(SituationDataQualityCodes.LegacyDefaultAmbiguous,["/players/1/velocity"],SituationQualitySeverity.Warning)] };
        Check(SituationPositionPrediction.Project(ambiguous,1,1,config,320) is null, "ambiguous zero not assumed stationary");
        foreach (var velocity in new SituationVec3?[] { null, V(0,350), V(0,250,10), new(double.NaN,0,0) })
            Check(SituationPositionPrediction.Project(Scene(-200,velocity,V(0,250)),0,1,config,320) is null, "unusable velocity rejected");
        var stale = scene with { DataQuality = [new(SituationDataQualityCodes.StaleFrame,["/tick"],SituationQualitySeverity.Warning)] };
        Check(SituationPositionPrediction.Project(stale,0,1,config,320) is null, "stale frame rejected");
        var p = SituationPositionPrediction.Project(scene,0,1,config,320)!;
        Check(Math.Abs(SituationVisibilityQuery.World(p).Y-50)<1e-6 && p.Z==scene.Players[0].Position!.Z, "normalized velocity Y inversion and no vertical extrapolation");
        Check(SituationPositionPrediction.Project(scene,0,1,config,64) is null, "distance limit enforced without clamping");
        var edge = scene with { Players = [scene.Players[0] with { Position = new(0.999,0.5,0), Velocity=V(250,0) }, scene.Players[1]] };
        Check(SituationPositionPrediction.Project(edge,0,1,config,320) is null, "radar boundary rejected");
        var direct = SituationRuleVerifier.BuildScene(new[] { N(-32,-200),N(-100,-200) }.Select((p,i)=>
            new SituationRuleVerifier.PlayerSpec(i==0?"T1":"CT1",i==0?SituationSide.T:SituationSide.CT,p.X,p.Y,"same",null,null,null,null,100)).ToArray());
        Check(!analyzer.Analyze(direct).Facts.Diagnostics.Decisions.ContainsKey("contact-position-prediction-probes"), "direct high contact skips prediction");
        for (var i=0;i<5;i++) Check(analyzer.Analyze(scene).Facts.Sha256==result.Facts.Sha256,"deterministic repeat");
        var reverse = analyzer.Analyze(scene with { Players=scene.Players.Reverse().ToArray() });
        Check(reverse.Facts.Facts.ContactRisk==result.Facts.Facts.ContactRisk,"input order preserves outcome");
        foreach (var times in new[] { new[] {1.01}, new[] {0.5,0.25}, new[] {double.NaN}, Array.Empty<double>() })
        {
            try { SituationAnalysisRuleLoader.Validate(load.Rules with { Visibility=load.Rules.Visibility with {
                PositionPrediction=config with { SampleSeconds=times } } }); throw new Exception("Invalid time accepted"); }
            catch (InvalidDataException) { checks++; }
        }
        try { SituationAnalysisRuleLoader.Validate(load.Rules with { AnalysisRuleVersion=SituationAnalysisRuleLoader.LocalPeekVersion }); throw new Exception("Version mixed"); }
        catch (InvalidDataException) { checks++; }
        Console.WriteLine($"Position prediction checks passed: {checks}; one-second horizon, synchronous motion, path safety, quality and fallback.");

        SituationDeterministicAnalyzer AnalyzeWith(SituationCollisionMesh mesh) => new(load,new SituationVisibilityQuery(mesh,load.Rules.Visibility!));
    }

    private static SituationVec3 V(double x,double y,double z=0) => new(x/5120,-y/5120,z/5120);
    private static SituationVec3 N(double x,double y) => new((x+3230)/5120,(1713-y)/5120,0);
    private static MinimapSceneV1 Scene(double y, SituationVec3? tVelocity, SituationVec3? ctVelocity)
    {
        var positions = new[] { N(-32,y), N(32,y) };
        var scene = SituationRuleVerifier.BuildScene(positions.Select((p,i)=>new SituationRuleVerifier.PlayerSpec(
            i==0?"T1":"CT1",i==0?SituationSide.T:SituationSide.CT,p.X,p.Y,"same",null,null,null,null,100)).ToArray());
        return scene with { Players = scene.Players.Select((p,i)=>p with { Velocity=i==0?tVelocity:ctVelocity }).ToArray() };
    }
    private static IEnumerable<CollisionTriangle> Wall(double top) => [
        new(new(0,-600,-10),new(0,top,-10),new(0,top,200)),new(new(0,-600,-10),new(0,top,200),new(0,-600,200))];
    private static IEnumerable<CollisionTriangle> Ground(double low,double high) => [
        new(new(-600,low,0),new(600,low,0),new(600,high,0)),new(new(-600,low,0),new(600,high,0),new(-600,high,0))];
}

using CsDemoMap.Api.Models;

namespace CsDemoMap.Api.Tests;

internal static class SituationExtendedLocalPeekVerifier
{
    internal static void Verify()
    {
        var count=0;
        void Check(bool ok,string name) { if (!ok) throw new InvalidOperationException("Extended peek: "+name); count++; }
        var load=SituationAnalysisRuleLoader.LoadExtendedLocalPeek();
        var prior=SituationAnalysisRuleLoader.LoadContinuousSlope();
        Check(load.Rules.Visibility!.LocalPeek!.Distance==600,"200 units per second times 3");
        Check(load.Rules.Visibility.PositionPrediction==prior.Rules.Visibility!.PositionPrediction ||
            SituationCanonicalJson.Sha256(load.Rules.Visibility.PositionPrediction)==SituationCanonicalJson.Sha256(prior.Rules.Visibility.PositionPrediction),"one-second prediction unchanged");
        var mesh=new SituationCollisionMesh(Ground(-1500,1500).Concat(Wall(0)));
        var query=new SituationVisibilityQuery(mesh,load.Rules.Visibility);
        var origin=N(-32,-200);
        var targets=query.LocalTargets(origin)!;
        var distances=targets.Select(p=> { var d=SituationVisibilityQuery.World(p)-SituationVisibilityQuery.World(origin); return Math.Sqrt(d.X*d.X+d.Y*d.Y); }).ToArray();
        Check(distances.All(d=>d<=600+1e-6),"upper bound");
        Check(distances.Any(d=>Math.Abs(d-64)<1e-6) && distances.Any(d=>Math.Abs(d-600)<1e-6),"old near ring and exact 600 endpoint");
        Check(targets.Count<=80,"bounded 8 directions times 10 radii");
        var analyzer=new SituationDeterministicAnalyzer(load,query);
        var oldAnalyzer=new SituationDeterministicAnalyzer(prior,new SituationVisibilityQuery(mesh,prior.Rules.Visibility!));
        var scene=Scene(-200);
        Check(oldAnalyzer.Analyze(scene).Facts.Facts.ContactRisk==SituationContactRisk.Low,"64-unit baseline cannot reach corner");
        var result=analyzer.Analyze(scene);
        Check(result.Facts.Facts.ContactRisk==SituationContactRisk.Medium,"extended radius reaches corner");
        Check(result.Narrative.Narrative.Highlights.Any(h=>h.EvidenceIds.Contains("contact-risk")),"range hypothesis included in summary");
        Check(result.Facts.Facts.Confidence!=SituationConfidence.High && !result.Facts.Diagnostics.SpatialComponents.ContainsKey("local"),"hypothesis capped and not current battle center");
        Check(analyzer.Analyze(Scene(-400)).Facts.Facts.ContactRisk==SituationContactRisk.Low,"beyond reachable single-person exposure remains low");
        Check(analyzer.Analyze(Scene(-16)).Facts.Facts.ContactRisk==SituationContactRisk.Medium,"near exposure retained");
        var closed=new SituationVisibilityQuery(new SituationCollisionMesh(Ground(-1500,1500).Concat(Wall(1500))),load.Rules.Visibility);
        Check(new SituationDeterministicAnalyzer(load,closed).Analyze(scene).Facts.Facts.ContactRisk==SituationContactRisk.Low,"solid partition cannot be crossed");
        var shortGround=new SituationVisibilityQuery(new SituationCollisionMesh(Ground(-1500,300)),load.Rules.Visibility);
        Check(shortGround.LocalTargets(N(0,0))!.Any(p=>SituationVisibilityQuery.World(p).Y>250),"valid prefix retained before distant gap");
        Check(!shortGround.CanMoveLocal(N(0,0),N(0,600),600),"unsupported distant target rejected");
        var edge=shortGround.LocalTargets(N(-3100,0));
        Check(edge is null,"out-of-mesh origin unavailable");
        for (var i=0;i<3;i++) Check(analyzer.Analyze(scene).Facts.Sha256==result.Facts.Sha256,"deterministic repeat");
        try { SituationAnalysisRuleLoader.Validate(load.Rules with { AnalysisRuleVersion=SituationAnalysisRuleLoader.ContinuousSlopeVersion }); throw new Exception("Old version accepted expanded radius"); }
        catch (InvalidDataException) { count++; }
        try { SituationAnalysisRuleLoader.Validate(load.Rules with { Visibility=load.Rules.Visibility with { LocalPeek=load.Rules.Visibility.LocalPeek with { Distance=960 } } }); throw new Exception("Wrong speed accepted"); }
        catch (InvalidDataException) { count++; }
        Console.WriteLine($"Extended local peek checks passed: {count}; 600 units, intermediate probes, walls, prefix, preserved prediction and version isolation.");
    }
    private static SituationVec3 N(double x,double y)=>new((x+3230)/5120,(1713-y)/5120,0);
    private static IEnumerable<CollisionTriangle> Ground(double lo,double hi)=>[
        new(new(-1500,lo,0),new(1500,lo,0),new(1500,hi,0)),new(new(-1500,lo,0),new(1500,hi,0),new(-1500,hi,0))];
    private static IEnumerable<CollisionTriangle> Wall(double top)=>[
        new(new(0,-1500,-100),new(0,top,-100),new(0,top,300)),new(new(0,-1500,-100),new(0,top,300),new(0,-1500,300))];
    private static MinimapSceneV1 Scene(double y)=>SituationRuleVerifier.BuildScene(new[] { N(-32,y),N(32,y) }.Select((p,i)=>
        new SituationRuleVerifier.PlayerSpec(i==0?"T1":"CT1",i==0?SituationSide.T:SituationSide.CT,p.X,p.Y,"same",null,null,null,null,100)).ToArray());
}

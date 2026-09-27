using CsDemoMap.Api.Models;

namespace CsDemoMap.Api.Tests;

internal static class SituationContinuousSlopeVerifier
{
    internal static void Verify()
    {
        var checks=0;
        void Check(bool ok,string name) { if (!ok) throw new InvalidOperationException("Slope check: "+name); checks++; }
        var load=SituationAnalysisRuleLoader.LoadContinuousSlope();
        var flat=new SituationCollisionMesh(Plane(0,0));
        Check(flat.GroundAt(0,0,4,-4) is { Point.Z: 0, Normal.Z: 1 },"flat hit and normal");
        Check(flat.GroundAt(0,0,20,10) is null,"bounded ray misses lower floor");
        var floors=new SituationCollisionMesh(Plane(0,0).Concat(Plane(0,0,100)));
        Check(floors.GroundAt(0,0,150,-10)?.Point.Z==100,"nearest floor hit");
        Check(floors.GroundAt(0,0,4,-4)?.Point.Z==0,"local interval preserves floor");
        Check(flat.GroundAt(1000,1000,4,-4) is null,"outside mesh");
        Check(flat.GroundAt(0,0,double.NaN,-4) is null,"invalid ray");
        var layered=new SituationCollisionMesh(Enumerable.Range(0,20).SelectMany(i=>Plane(0.2,0.1,i*20)));
        var random=new Random(260926);
        for (var i=0;i<100;i++)
        {
            var x=random.NextDouble()*800-400; var y=random.NextDouble()*800-400; var layer=random.Next(20);
            var expected=0.2*x+0.1*y+layer*20;
            var hit=layered.GroundAt(x,y,expected+5,expected-50);
            Check(hit is not null && Math.Abs(hit.Value.Point.Z-expected)<1e-7,"BVH nearest sloped-floor analytic oracle");
        }
        var slopeMesh=new SituationCollisionMesh(Plane(0.5,0));
        var slope=Query(slopeMesh);
        var end=slope.ProjectSurface(N(0,0,0),N(64,0,0),64);
        Check(end is not null && Math.Abs(W(end).Z-32)<1e-6,"uphill endpoint follows ground");
        Check(slope.CanMoveLocal(N(0,0,0),end!),"uphill body follows path");
        var down=slope.ProjectSurface(N(64,0,32),N(0,0,32),64);
        Check(down is not null && Math.Abs(W(down).Z)<1e-6 && slope.CanMoveLocal(N(64,0,32),down),"downhill follows ground");
        Check(!slope.CanMoveLocal(N(0,0,0),N(64,0,0)),"unadjusted endpoint refused");
        Check(slope.LocalTargets(N(0,0,0))!.Any(p=>Math.Abs(W(p).Z)>20),"local targets include slope heights");
        var reversed=Query(new SituationCollisionMesh(Plane(0.5,0).Select(t=>new CollisionTriangle(t.A,t.C,t.B))));
        Check(reversed.ProjectSurface(N(0,0,0),N(64,0,0),64)==end,"double-sided winding consistent");
        Check(Query(new SituationCollisionMesh(Plane(1,0))).ProjectSurface(N(0,0,0),N(64,0,0),64) is not null,"45 degree boundary");
        Check(Query(new SituationCollisionMesh(Plane(1.05,0))).ProjectSurface(N(0,0,0),N(64,0,0),64) is null,"steep slope rejected");
        var transition=Query(new SituationCollisionMesh(Plane(0,0,0,-512,0).Concat(Plane(0.5,0,0,0,512))));
        var transitionEnd=transition.ProjectSurface(N(-64,0,0),N(64,0,0),128);
        Check(transitionEnd is not null && Math.Abs(W(transitionEnd).Z-32)<1e-6 && transition.CanMoveLocal(N(-64,0,0),transitionEnd,128),"flat to continuous ramp");
        var step=Query(new SituationCollisionMesh(Plane(0,0,0,-512,0).Concat(Plane(0,0,8,0,512))));
        Check(step.ProjectSurface(N(-32,0,0),N(32,0,0),64) is null,"step without riser geometry rejected");
        Check(step.ProjectSurface(N(32,0,8),N(-32,0,8),64) is null,"down step rejected");
        var gap=Query(new SituationCollisionMesh(Plane(0,0,0,-512,-16).Concat(Plane(0,0,0,16,512))));
        Check(gap.ProjectSurface(N(-32,0,0),N(32,0,0),64) is null,"gap rejected");
        Check(slope.ProjectSurface(N(0,0,20),N(64,0,20),64) is null,"airborne origin not snapped to floor");
        var ceiling=Query(new SituationCollisionMesh(Plane(0.5,0).Concat(Plane(0.5,0,40))));
        Check(ceiling.LocalTargets(N(0,0,0)) is null,"standing clearance required");
        var wall=Query(new SituationCollisionMesh(Plane(0.5,0).Concat(Wall(32,512))));
        var wallEnd=wall.ProjectSurface(N(0,0,0),N(64,0,0),64);
        Check(wallEnd is not null && !wall.CanMoveLocal(N(0,0,0),wallEnd),"wall blocks slope movement");
        var narrow=Query(new SituationCollisionMesh(Plane(0.5,0,0,-512,512,-8,8)));
        var narrowEnd=narrow.ProjectSurface(N(0,0,0),N(64,0,0),64);
        Check(narrowEnd is not null && !narrow.CanMoveLocal(N(0,0,0),narrowEnd),"body width support required");
        Check(slope.ProjectSurface(N(0,0,0),N(64,0,0),64,V(100,0,50)) is not null,"slope-consistent vertical velocity allowed");
        Check(slope.ProjectSurface(N(0,0,0),N(64,0,0),64,V(100,0,100)) is null,"jump velocity rejected");
        var mesh=new SituationCollisionMesh(Plane(0,0.25).Concat(Wall(0,0)));
        var scene=Scene(-200,62.5);
        var analyzer=new SituationDeterministicAnalyzer(load,Query(mesh));
        var result=analyzer.Analyze(scene);
        Check(result.Facts.Facts.ContactRisk==SituationContactRisk.Medium && result.Facts.Diagnostics.Decisions.ContainsKey("contact-position-prediction"),"one-second slope corner prediction");
        Check(result.Facts.Facts.Confidence!=SituationConfidence.High && !result.Facts.Diagnostics.SpatialComponents.ContainsKey("local"),"prediction confidence and present battle center");
        Check(result.Narrative.Narrative.Highlights.Any(h=>h.EvidenceIds.Contains("contact-risk")),"new rule evidence included");
        Check(!analyzer.Analyze(Scene(-200,125)).Facts.Diagnostics.Decisions.ContainsKey("contact-position-prediction"),"analyzer rejects jumping prediction");
        Check(analyzer.Analyze(Scene(-16,0)).Facts.Diagnostics.Decisions.ContainsKey("contact-local-peek"),"local peek follows slope without valid velocity");
        for (var i=0;i<5;i++) Check(analyzer.Analyze(scene).Facts.Sha256==result.Facts.Sha256,"repeat determinism");
        Parallel.For(0,10,_=> { if (analyzer.Analyze(scene).Facts.Sha256!=result.Facts.Sha256) throw new Exception("Slope concurrency"); });
        Check(true,"immutable concurrent geometry");
        try { SituationAnalysisRuleLoader.Validate(load.Rules with { Visibility=load.Rules.Visibility! with { ContinuousSlope=null } }); throw new Exception("Missing slope accepted"); }
        catch (InvalidDataException) { checks++; }
        try { SituationAnalysisRuleLoader.Validate(load.Rules with { AnalysisRuleVersion=SituationAnalysisRuleLoader.PositionPredictionVersion }); throw new Exception("Old version mixed"); }
        catch (InvalidDataException) { checks++; }
        foreach (var angle in new[] { double.NaN,0,46 })
        {
            try { SituationAnalysisRuleLoader.Validate(load.Rules with { Visibility=load.Rules.Visibility! with { ContinuousSlope=load.Rules.Visibility.ContinuousSlope! with { MaxSlopeDegrees=angle } } }); throw new Exception("Angle accepted"); }
            catch (InvalidDataException) { checks++; }
        }
        Console.WriteLine($"Continuous slope checks passed: {checks}; bounded ground rays, normals, ramps, stairs, gaps, body, prediction and version isolation.");
        SituationVisibilityQuery Query(SituationCollisionMesh mesh) => new(mesh,load.Rules.Visibility!);
    }
    private static CollisionPoint W(SituationVec3 p)=>SituationVisibilityQuery.World(p);
    private static SituationVec3 N(double x,double y,double z)=>new((x+3230)/5120,(1713-y)/5120,z/5120);
    private static SituationVec3 V(double x,double y,double z)=>new(x/5120,-y/5120,z/5120);
    private static IEnumerable<CollisionTriangle> Plane(double sx,double sy,double z=0,double x0=-512,double x1=512,double y0=-512,double y1=512)
    {
        CollisionPoint P(double x,double y)=>new(x,y,sx*x+sy*y+z);
        return [new(P(x0,y0),P(x1,y0),P(x1,y1)),new(P(x0,y0),P(x1,y1),P(x0,y1))];
    }
    private static IEnumerable<CollisionTriangle> Wall(double x,double yTop)=>[
        new(new(x,-512,-300),new(x,yTop,-300),new(x,yTop,400)),new(new(x,-512,-300),new(x,yTop,400),new(x,-512,400))];
    private static MinimapSceneV1 Scene(double y,double vz)
    {
        var points=new[] { N(-32,y,y*0.25),N(32,y,y*0.25) };
        var scene=SituationRuleVerifier.BuildScene(points.Select((p,i)=>new SituationRuleVerifier.PlayerSpec(
            i==0?"T1":"CT1",i==0?SituationSide.T:SituationSide.CT,p.X,p.Y,"same",null,null,null,null,100)).ToArray());
        return scene with { Players=scene.Players.Select((p,i)=>p with { Position=points[i],Velocity=V(0,250,vz) }).ToArray(),
            Bomb=scene.Bomb with { Position=points[0] } };
    }
}

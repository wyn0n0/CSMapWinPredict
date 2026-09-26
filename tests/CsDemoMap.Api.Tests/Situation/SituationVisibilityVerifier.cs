using CsDemoMap.Api.Models;
using System.Globalization;

namespace CsDemoMap.Api.Tests;

internal static class SituationVisibilityVerifier
{
    internal static void Verify()
    {
        var checks = 0;
        void Check(bool condition, string label)
        {
            if (!condition) throw new InvalidOperationException("Raycast check failed: " + label);
            checks++;
        }
        var wall = new SituationCollisionMesh(Wall(0, -100, 100, -100, 200));
        Check(wall.IsBlocked(new(-50,0,50),new(50,0,50)), "solid wall");
        Check(wall.IsBlocked(new(50,0,50),new(-50,0,50)), "double-sided wall");
        Check(!wall.IsBlocked(new(-100,0,50),new(-50,0,50)), "wall beyond finite segment");
        Check(!wall.IsBlocked(new(-50,120,50),new(50,120,50)), "outside wall");
        Check(!wall.IsBlocked(new(-50,0,50),new(-50,80,50)), "parallel ray");
        Check(!wall.IsBlocked(new(-50,0,50),new(-50,0,50)), "zero ray");
        Check(!wall.IsBlocked(new(0,0,50),new(50,0,50)), "endpoint tolerance");
        var floor = new SituationCollisionMesh([
            new(new(-100,-100,100),new(100,-100,100),new(100,100,100)),
            new(new(-100,-100,100),new(100,100,100),new(-100,100,100))]);
        Check(floor.IsBlocked(new(0,0,50),new(0,0,150)), "stacked floors");
        var load = SituationAnalysisRuleLoader.LoadRaycast();
        var query = new SituationVisibilityQuery(wall, load.Rules.Visibility!);
        Check(query.Query(N(-50,0,0),N(50,0,0)) == SituationVisibility.Blocked, "all body heights blocked");
        var cover = new SituationVisibilityQuery(new SituationCollisionMesh(Wall(0,-100,100,-100,45)),load.Rules.Visibility!);
        Check(cover.Query(N(-50,0,0),N(50,0,0)) == SituationVisibility.Clear, "low cover permits upper ray");
        var window = new SituationVisibilityQuery(new SituationCollisionMesh(
            Wall(0,-100,100,-100,45).Concat(Wall(0,-100,100,60,200))),load.Rules.Visibility!);
        Check(window.Query(N(-50,0,0),N(50,0,0)) == SituationVisibility.Clear, "window opening");
        Check(new SituationVisibilityQuery(null,load.Rules.Visibility!).Query(N(-50,0,0),N(50,0,0)) == SituationVisibility.Unknown,
            "missing mesh is unknown");
        Check(query.Query(new(-1,0,0),N(0,0,0)) == SituationVisibility.Unknown, "out-of-radar coordinate");
        var normalized = N(123,-456,78);
        var world = SituationVisibilityQuery.World(normalized);
        Check(Math.Abs(world.X-123)<1e-6 && Math.Abs(world.Y+456)<1e-6 && Math.Abs(world.Z-78)<1e-6,
            "radar Y inversion and height roundtrip");

        var scene = Scene(("T1",SituationSide.T,-50,0),("CT1",SituationSide.CT,50,0));
        var old = SituationDeterministicAnalyzer.CreateFrozen().Analyze(scene);
        Check(old.Facts.Facts.ContactRisk == SituationContactRisk.High, "v1 remains distance-only");
        var analyzer = new SituationDeterministicAnalyzer(load,query);
        var blocked = analyzer.Analyze(scene);
        Check(blocked.Facts.Facts.ContactRisk == SituationContactRisk.Low, "v2 removes wall false positive");
        var projected = SituationModelInputProjector.Project(scene);
        var candidate = new SituationReviewCandidateV1("situation-review-candidate-v1", 1,
            "sample-" + new string('a',64), SituationTrainingSplit.Train, "match-" + new string('b',64),
            "round-" + new string('c',64), ["ordinary-live"],
            new(projected, old.Facts.Facts, old.Facts.Facts.Evidence.Select(e=>e.Id).Order(StringComparer.Ordinal).ToArray()),
            old.Narrative.Narrative, new string('d',64), old.Narrative.Sha256);
        var reviewed = SituationRaycastReviewCatalog.Derive(candidate, analyzer);
        Check(reviewed.Input.Facts.ContactRisk == SituationContactRisk.Low, "review consumes wall filtering");
        Check(reviewed.Input.Scene == candidate.Input.Scene && reviewed.RecordSha256 == candidate.RecordSha256,
            "review preserves source scene and lineage");
        Check(reviewed.Input.Facts == blocked.Facts.Facts || SituationCanonicalJson.Sha256(reviewed.Input.Facts) == blocked.Facts.Sha256,
            "review matches full source analysis");
        Check(reviewed.CandidateSha256 == blocked.Narrative.Sha256, "review narrative regenerated");
        Check(!blocked.Facts.Diagnostics.SpatialComponents.ContainsKey("local"), "blocked pair not a local battle center");
        var unknown = new SituationDeterministicAnalyzer(load).Analyze(scene);
        Check(unknown.Facts.Facts.ContactRisk == SituationContactRisk.Unknown, "unknown does not become low");
        Check(unknown.Narrative.Narrative.Uncertainties.Any(s=>s.Contains("遮挡",StringComparison.Ordinal)), "unknown narrative explains geometry");
        var second = Scene(("T1",SituationSide.T,-50,0),("CT1",SituationSide.CT,50,0),("CT2",SituationSide.CT,-150,0));
        var other = analyzer.Analyze(second);
        Check(other.Facts.Facts.ContactRisk == SituationContactRisk.High, "another clear pair still high");
        Check(other.Facts.Facts.Evidence.Single(e=>e.Id=="contact-risk").SourcePaths.Contains("/players/2/position"),
            "evidence selects clear pair");
        var reverse = analyzer.Analyze(second with { Players = second.Players.Reverse().ToArray() });
        Check(reverse.Facts.Facts.ContactRisk == other.Facts.Facts.ContactRisk, "input order preserves risk");
        var clearQuery = new FixedQuery(SituationVisibility.Clear);
        var clear = new SituationDeterministicAnalyzer(load,clearQuery).Analyze(scene);
        Check(clear.Facts.Facts.ContactRisk == old.Facts.Facts.ContactRisk, "clear contact retains risk");
        var mixed = Scene(("T1",SituationSide.T,-50,0),("CT1",SituationSide.CT,50,0),("CT2",SituationSide.CT,-450,0));
        Check(new SituationDeterministicAnalyzer(load,new SplitQuery()).Analyze(mixed).Facts.Facts.ContactRisk == SituationContactRisk.Unknown,
            "unknown high candidate prevents clear medium being called final");
        Check(new SituationDeterministicAnalyzer(load,new SplitQuery()).Analyze(second).Facts.Facts.ContactRisk == SituationContactRisk.High,
            "known high dominates unknown candidate");
        var far = Scene(("T1",SituationSide.T,-1000,0),("CT1",SituationSide.CT,1000,0));
        Check(new SituationDeterministicAnalyzer(load).Analyze(far).Facts.Facts.ContactRisk == SituationContactRisk.Low,
            "far pairs do not require visibility");
        var floorQuery = new SituationVisibilityQuery(floor,load.Rules.Visibility!);
        Check(floorQuery.Query(N(0,0,0),N(0,0,150)) == SituationVisibility.Blocked,
            "same XY on different floors is blocked");
        // BVH must agree with an analytic infinite plane bounded to this wall rectangle.
        var random = new Random(260926);
        for (var i=0;i<300;i++)
        {
            var a=new CollisionPoint(-10-random.NextDouble()*90,random.NextDouble()*400-200,random.NextDouble()*500-200);
            var b=new CollisionPoint(10+random.NextDouble()*90,random.NextDouble()*400-200,random.NextDouble()*500-200);
            var t=-a.X/(b.X-a.X);
            var y=a.Y+t*(b.Y-a.Y); var z=a.Z+t*(b.Z-a.Z);
            Check(wall.IsBlocked(a,b)==(y>=-100 && y<=100 && z>=-100 && z<=200),"analytic rectangle oracle");
        }
        try
        {
            SituationAnalysisRuleLoader.Validate(load.Rules with { Visibility=load.Rules.Visibility! with { SampleHeights=[64,36] } });
            throw new InvalidOperationException("Unordered heights accepted.");
        }
        catch (InvalidDataException) { checks++; }
        try
        {
            SituationAnalysisRuleLoader.Validate(load.Rules with { Visibility=null });
            throw new InvalidOperationException("Missing raycast configuration accepted.");
        }
        catch (InvalidDataException) { checks++; }
        var culture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            Check(analyzer.Analyze(scene).Facts.Sha256 == blocked.Facts.Sha256, "deterministic culture");
        }
        finally { CultureInfo.CurrentCulture = culture; }
        Parallel.For(0,30,_ => {
            if (analyzer.Analyze(scene).Facts.Sha256 != blocked.Facts.Sha256)
                throw new InvalidOperationException("Concurrent raycast changed result.");
        });
        Check(true,"concurrent immutable mesh");
        var realPath = Path.Combine(AppContext.BaseDirectory,"Geometry","de_mirage.mesh");
        if (!File.Exists(realPath)) throw new InvalidOperationException("Prepare the pinned Mirage mesh before verification.");
        var real = SituationCollisionMesh.Load(realPath,load.Rules.Visibility!.AssetSha256);
        Check(real.TriangleCount > 1000,"pinned real asset loads");
        try { SituationCollisionMesh.Load(realPath,new string('0',64)); throw new InvalidOperationException("Invalid hash accepted"); }
        catch (InvalidDataException) { checks++; }
        var peekLoad = SituationAnalysisRuleLoader.LoadLocalPeek();
        var ground = Ground(-500,500,-500,500);
        var cornerMesh = new SituationCollisionMesh(Wall(0,-500,0,-10,200).Concat(ground));
        var cornerQuery = new SituationVisibilityQuery(cornerMesh, peekLoad.Rules.Visibility!);
        var cornerScene = Scene(("T1",SituationSide.T,-32,-16),("CT1",SituationSide.CT,32,-16));
        var cornerAnalyzer = new SituationDeterministicAnalyzer(peekLoad, cornerQuery);
        var peekResult = cornerAnalyzer.Analyze(cornerScene);
        Check(new SituationDeterministicAnalyzer(load,new SituationVisibilityQuery(cornerMesh,load.Rules.Visibility!))
            .Analyze(cornerScene).Facts.Facts.ContactRisk == SituationContactRisk.Low,"v2 remains occluded at corner");
        Check(peekResult.Facts.Facts.ContactRisk == SituationContactRisk.Medium,"short corner exposure is medium");
        Check(peekResult.Facts.Facts.Confidence != SituationConfidence.High,"hypothetical exposure not high confidence");
        Check(!peekResult.Facts.Diagnostics.SpatialComponents.ContainsKey("local"),"peek does not create current battle center");
        Check(peekResult.Narrative.Narrative.Highlights.Any(h=>h.EvidenceIds.Contains("contact-risk")),"peek included in narrative");
        Check(cornerQuery.LocalPositions(N(-32,-16,0))!.All(p=>SituationVisibilityQuery.World(p).X < 0),"cannot walk through wall");
        var longWall = new SituationDeterministicAnalyzer(peekLoad,
            new SituationVisibilityQuery(new SituationCollisionMesh(Wall(0,-500,500,-10,200).Concat(ground)),peekLoad.Rules.Visibility!));
        Check(longWall.Analyze(cornerScene).Facts.Facts.ContactRisk == SituationContactRisk.Low,"separate rooms remain low");
        Check(cornerAnalyzer.Analyze(Scene(("T1",SituationSide.T,-32,-200),("CT1",SituationSide.CT,32,-200))).Facts.Facts.ContactRisk == SituationContactRisk.Low,"far corner not invented");
        var unsupported = new SituationDeterministicAnalyzer(peekLoad,new SituationVisibilityQuery(wall,peekLoad.Rules.Visibility!));
        Check(unsupported.Analyze(scene).Facts.Facts.ContactRisk == SituationContactRisk.Unknown,"missing ground is unknown");
        var ledge = new SituationVisibilityQuery(new SituationCollisionMesh(Wall(0,-500,0,-10,200)
            .Concat(Ground(-500,500,-500,0))),peekLoad.Rules.Visibility!);
        Check(ledge.LocalPositions(N(-32,-16,0))!.All(p=>SituationVisibilityQuery.World(p).Y <= 0),"cannot peek from unsupported ledge");
        var windowMesh = new SituationCollisionMesh(Wall(0,-500,500,-10,28).Concat(Wall(0,-500,500,44,200)).Concat(ground));
        var windowQuery = new SituationVisibilityQuery(windowMesh,peekLoad.Rules.Visibility!);
        Check(windowQuery.LocalPositions(N(-32,0,0))!.All(p=>SituationVisibilityQuery.World(p).X < 0),"cannot walk through narrow window");
        var open = Scene(("T1",SituationSide.T,-80,-16),("CT1",SituationSide.CT,-32,-16));
        var openResult = cornerAnalyzer.Analyze(open);
        Check(openResult.Facts.Facts.ContactRisk == SituationContactRisk.High && !openResult.Facts.Diagnostics.Decisions.ContainsKey("contact-local-peek-probes"),"direct contact skips peek work");
        for (var i=0;i<10;i++) Check(cornerAnalyzer.Analyze(cornerScene).Facts.Sha256==peekResult.Facts.Sha256,"peek deterministic");
        try {
            SituationAnalysisRuleLoader.Validate(peekLoad.Rules with { Visibility = peekLoad.Rules.Visibility! with {
                LocalPeek = peekLoad.Rules.Visibility.LocalPeek! with { Distance = double.NaN } } });
            throw new InvalidOperationException("Invalid peek range accepted");
        } catch (InvalidDataException) { checks++; }
        Console.WriteLine($"Situation raycast checks passed: {checks}; triangles: {real.TriangleCount}; frozen rules unchanged.");
    }

    private static IEnumerable<CollisionTriangle> Wall(double x,double y0,double y1,double z0,double z1) => [
        new(new(x,y0,z0),new(x,y1,z0),new(x,y1,z1)), new(new(x,y0,z0),new(x,y1,z1),new(x,y0,z1))];

    private static IEnumerable<CollisionTriangle> Ground(double x0,double x1,double y0,double y1) => [
        new(new(x0,y0,0),new(x1,y0,0),new(x1,y1,0)),new(new(x0,y0,0),new(x1,y1,0),new(x0,y1,0))];

    private static SituationVec3 N(double x,double y,double z) => new((x+3230)/5120,(1713-y)/5120,z/5120);

    private static MinimapSceneV1 Scene(params (string Slot,SituationSide Side,double X,double Y)[] players) =>
        SituationRuleVerifier.BuildScene(players.Select(p=> {
            var n=N(p.X,p.Y,0);
            return new SituationRuleVerifier.PlayerSpec(p.Slot,p.Side,n.X,n.Y,"same",null,null,null,null,100);
        }).ToArray());

    private sealed class FixedQuery(SituationVisibility result) : ISituationVisibilityQuery
    {
        public SituationVisibility Query(SituationVec3 left,SituationVec3 right) => result;
    }

    private sealed class SplitQuery : ISituationVisibilityQuery
    {
        public SituationVisibility Query(SituationVec3 left,SituationVec3 right) =>
            SituationVisibilityQuery.World(right).X>0 ? SituationVisibility.Unknown : SituationVisibility.Clear;
    }
}

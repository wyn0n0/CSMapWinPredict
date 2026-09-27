using CsDemoMap.Api.Models;

namespace CsDemoMap.Api.Services;

internal readonly record struct SituationGroundHit(CollisionPoint Point, CollisionPoint Normal);

internal sealed partial class SituationCollisionMesh
{
    // Closest hit along a bounded vertical ray. The boolean visibility hot path is unchanged.
    internal SituationGroundHit? GroundAt(double x, double y, double top, double bottom)
    {
        if (!double.IsFinite(x) || !double.IsFinite(y) || !double.IsFinite(top) || !double.IsFinite(bottom) || top <= bottom)
            return null;
        SituationGroundHit? nearest = null;
        Visit(root);
        return nearest;

        void Visit(Node node)
        {
            if (x < node.Min.X || x > node.Max.X || y < node.Min.Y || y > node.Max.Y ||
                node.Min.Z > top || node.Max.Z < (nearest?.Point.Z ?? bottom)) return;
            if (node.Left is not null) { Visit(node.Left); Visit(node.Right!); return; }
            for (var i=node.Start; i<node.Start+node.Count; i++)
            {
                var tri = triangles[i];
                var e1=tri.B-tri.A; var e2=tri.C-tri.A;
                var n=e1.Cross(e2);
                if (Math.Abs(n.Z)<1e-9) continue;
                var dx=x-tri.A.X; var dy=y-tri.A.Y;
                var u=(dx*e2.Y-dy*e2.X)/n.Z;
                var v=(e1.X*dy-e1.Y*dx)/n.Z;
                if (u < -1e-9 || v < -1e-9 || u+v > 1+1e-9) continue;
                var z=tri.A.Z+u*e1.Z+v*e2.Z;
                if (z < bottom || z > top || z < (nearest?.Point.Z ?? bottom)-1e-9) continue;
                var length=Math.Sqrt(n.Dot(n)); var sign=n.Z<0 ? -1 : 1;
                var normal=new CollisionPoint(sign*n.X/length,sign*n.Y/length,sign*n.Z/length);
                if (nearest is { } old && Math.Abs(z-old.Point.Z)<1e-9 && normal.Z<=old.Normal.Z) continue;
                nearest=new(new(x,y,z),normal);
            }
        }
    }
}

internal sealed partial class SituationVisibilityQuery
{
    private double SlopeGradient => Math.Tan(rules.ContinuousSlope!.MaxSlopeDegrees*Math.PI/180);
    private bool Walkable(SituationGroundHit hit) => hit.Normal.Z+1e-9 >= Math.Cos(rules.ContinuousSlope!.MaxSlopeDegrees*Math.PI/180);
    private SituationGroundHit? OriginGround(SituationVec3 origin)
    {
        var p=World(origin); var tolerance=rules.LocalPeek!.GroundTolerance;
        var hit=mesh!.GroundAt(p.X,p.Y,p.Z+tolerance,p.Z-tolerance);
        return hit is { } ground && Walkable(ground) && ClearStanding(ground.Point) ? ground : null;
    }
    private bool ClearStanding(CollisionPoint p) => !mesh!.IsBlocked(p with { Z=p.Z+2 },p with { Z=p.Z+64 },rules.EndpointEpsilon);
    private static SituationVec3 Normalized(CollisionPoint p)
    {
        var map=MapFeatureGeometries.Find("de_mirage")!; var scale=map.Scale*1024;
        return new((p.X-map.PositionX)/scale,(map.PositionY-p.Y)/scale,p.Z/scale);
    }

    private IReadOnlyList<SituationVec3>? SlopeTargets(SituationVec3 origin)
    {
        if (OriginGround(origin) is null) return null;
        if (rules.LocalPeek!.ProbeSpacing is { } spacing) return ExtendedSlopeTargets(origin,spacing);
        var start=World(origin); var targets=new List<SituationVec3>(8);
        for (var i=0;i<8;i++)
        {
            var angle=i*Math.PI/4;
            var target=Normalized(new(start.X+rules.LocalPeek!.Distance*Math.Cos(angle),start.Y+rules.LocalPeek.Distance*Math.Sin(angle),start.Z));
            var projected=ProjectSurface(origin,target,rules.LocalPeek.Distance);
            if (projected is not null) targets.Add(projected);
        }
        return targets;
    }

    private IReadOnlyList<SituationVec3> ExtendedSlopeTargets(SituationVec3 origin, double spacing)
    {
        var start=World(origin);
        var targets=new List<(int Ring,int Direction,SituationVec3 Point)>();
        for (var direction=0;direction<8;direction++)
        {
            var angle=direction*Math.PI/4;
            // Stop at the radar edge instead of discarding all valid shorter probes.
            var dx=Math.Cos(angle); var dy=Math.Sin(angle);
            var distance=rules.LocalPeek!.Distance;
            while (distance>=spacing && !Valid(Normalized(new(start.X+distance*dx,start.Y+distance*dy,start.Z))))
                distance=Math.Floor((distance-1e-6)/spacing)*spacing;
            if (distance<spacing) continue;
            var path=SurfacePath(origin,Normalized(new(start.X+distance*dx,start.Y+distance*dy,start.Z)),distance,null,allowPartial:true);
            if (path is null) continue;
            var stride=(int)(spacing/rules.LocalPeek.SupportStep);
            for (var step=stride;step<path.Count;step+=stride)
                targets.Add((step/stride,direction,Normalized(path[step].Point)));
            var last=path[^1].Point-start;
            if (Math.Abs(Math.Sqrt(last.X*last.X+last.Y*last.Y)-rules.LocalPeek.Distance)<1e-6 &&
                rules.LocalPeek.Distance%spacing!=0)
                targets.Add(((int)Math.Ceiling(rules.LocalPeek.Distance/spacing),direction,Normalized(path[^1].Point)));
        }
        return targets.OrderBy(t=>t.Ring).ThenBy(t=>t.Direction).Select(t=>t.Point).ToArray();
    }

    public SituationVec3? ProjectSurface(SituationVec3 origin, SituationVec3 target, double maxDistance, SituationVec3? velocity = null)
    {
        var path=SurfacePath(origin,target,maxDistance,velocity);
        return path is null ? null : Normalized(path[^1].Point);
    }

    private List<SituationGroundHit>? SurfacePath(SituationVec3 origin, SituationVec3 target, double maxDistance, SituationVec3? velocity, bool allowPartial = false)
    {
        if (mesh is null || rules.ContinuousSlope is null || !Valid(origin) || !Valid(target) ||
            !double.IsFinite(maxDistance) || maxDistance<=0 || maxDistance>Math.Max(320,rules.LocalPeek!.Distance) || OriginGround(origin) is not { } first) return null;
        var end=World(target); var dx=end.X-first.Point.X; var dy=end.Y-first.Point.Y;
        var distance=Math.Sqrt(dx*dx+dy*dy);
        if (distance>maxDistance+1e-6) return null;
        if (velocity is { } v)
        {
            var scale=MapFeatureGeometries.Find("de_mirage")!.Scale*1024;
            var expected=-(first.Normal.X*v.X-first.Normal.Y*v.Y)/first.Normal.Z*scale;
            if (Math.Abs(v.Z*scale-expected)>rules.PositionPrediction!.MaxVerticalSpeed) return null;
        }
        var path=new List<SituationGroundHit> { first };
        var steps=Math.Max(1,(int)Math.Ceiling(distance/rules.LocalPeek!.SupportStep));
        for (var step=1;step<=steps;step++)
        {
            var previous=path[^1];
            var fraction=allowPartial ? Math.Min(step*rules.LocalPeek.SupportStep/distance,1) : (double)step/steps;
            var x=allowPartial ? first.Point.X+dx*fraction : first.Point.X+dx*step/steps;
            var y=allowPartial ? first.Point.Y+dy*fraction : first.Point.Y+dy*step/steps;
            var segmentLength=allowPartial ? Math.Min(rules.LocalPeek.SupportStep,distance-(step-1)*rules.LocalPeek.SupportStep) : distance/steps;
            var limit=SlopeGradient*segmentLength+rules.ContinuousSlope.SurfaceTolerance;
            var hit=mesh.GroundAt(x,y,previous.Point.Z+limit,previous.Point.Z-limit);
            if (hit is not { } current || !Walkable(current) || !Continuous(previous,current)) return allowPartial ? path : null;
            path.Add(current);
        }
        return path;
    }

    private bool Continuous(SituationGroundHit a, SituationGroundHit b)
    {
        var dx=b.Point.X-a.Point.X; var dy=b.Point.Y-a.Point.Y; var dz=b.Point.Z-a.Point.Z;
        var expectedA=-(a.Normal.X*dx+a.Normal.Y*dy)/a.Normal.Z;
        var expectedB=-(b.Normal.X*dx+b.Normal.Y*dy)/b.Normal.Z;
        var tolerance=rules.ContinuousSlope!.SurfaceTolerance;
        // Flat stair treads cannot explain a vertical jump. Slope transitions can explain a bounded change.
        return dz>=Math.Min(expectedA,expectedB)-tolerance && dz<=Math.Max(expectedA,expectedB)+tolerance;
    }

    private bool CanMoveSlope(SituationVec3 origin, SituationVec3 target, double maxDistance)
    {
        var path=SurfacePath(origin,target,maxDistance,null);
        if (path is null || Math.Abs(path[^1].Point.Z-World(target).Z)>rules.ContinuousSlope!.SurfaceTolerance) return false;
        var delta=path[^1].Point-path[0].Point; var length=Math.Sqrt(delta.X*delta.X+delta.Y*delta.Y);
        var lateral=length<1e-6 ? new CollisionPoint(rules.LocalPeek!.BodyRadius,0,0) :
            new CollisionPoint(-delta.Y/length*rules.LocalPeek!.BodyRadius,delta.X/length*rules.LocalPeek.BodyRadius,0);
        for (var i=0;i<path.Count;i++)
        {
            var p=path[i].Point;
            if (!ClearStanding(p)) return false;
            foreach (var side in new[] { -1,1 })
            {
                var x=p.X+side*lateral.X; var y=p.Y+side*lateral.Y;
                var z=p.Z-(path[i].Normal.X*side*lateral.X+path[i].Normal.Y*side*lateral.Y)/path[i].Normal.Z;
                var tolerance=rules.ContinuousSlope!.SurfaceTolerance;
                if (mesh!.GroundAt(x,y,z+tolerance,z-tolerance) is not { } support || !Walkable(support)) return false;
            }
            foreach (var height in BodyHeights)
            {
                var b=p with { Z=p.Z+height };
                if (mesh!.IsBlocked(b-lateral,b+lateral,rules.EndpointEpsilon)) return false;
                if (i==0) continue;
                var a=path[i-1].Point with { Z=path[i-1].Point.Z+height };
                for (var side=-1;side<=1;side++)
                {
                    var offset=new CollisionPoint(side*lateral.X,side*lateral.Y,0);
                    // Include shared segment endpoints; otherwise a wall exactly at a ground sample can be skipped twice.
                    if (mesh.IsBlocked(a+offset,b+offset,0)) return false;
                }
            }
            // Near-foot segment detects a riser or an intervening ridge between ground samples.
            if (i>0 && mesh!.IsBlocked(path[i-1].Point with { Z=path[i-1].Point.Z+2 },p with { Z=p.Z+2 },0)) return false;
        }
        return true;
    }
}

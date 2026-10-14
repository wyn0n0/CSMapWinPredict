using CsDemoMap.Api.Models;

namespace CsDemoMap.Api.Services;

public sealed record WinSpatialRadii(double SupportRadius, double SiteRadius)
{
    public void Validate()
    {
        if (!double.IsFinite(SupportRadius) || SupportRadius <= 0 ||
            !double.IsFinite(SiteRadius) || SiteRadius <= 0)
            throw new ArgumentException("Spatial radii must be finite positive world-unit distances.");
    }
}

public sealed record WinSpatialTeam(
    int ObservedAliveCount, int ValidPositionCount, string PositionStatus,
    int C4DistanceCount, double? NearestC4Distance, double? SecondC4Distance,
    int? SiteANearbyCount, int? SiteBNearbyCount,
    double? MeanNearestTeammateDistance, double? MaxNearestTeammateDistance);

public sealed record WinSpatialCarrier(
    string Status, int? TeammateCount, int? ValidTeammatePositionCount,
    double? NearestTeammateDistance, int? NearbyTeammateCount);

public sealed record WinSpatialFeatures(
    string BombState, string BombPositionStatus, string RosterStatus,
    WinSpatialTeam T, WinSpatialTeam CT, WinSpatialCarrier Carrier,
    double? NearestC4DistanceDifference, double? SecondC4DistanceDifference);

// This experiment consumes only one raw frame and its as-of roster observation.
// No timeline history, round outcome, player identity or map navigation is a feature.
public sealed class WinSpatialFeatureBuilder
{
    public const string ContractVersion = "win-spatial-v1";
    private readonly WinSpatialRadii radii;
    private readonly MapFeatureGeometry geometry;

    public WinSpatialFeatureBuilder(string mapName, WinSpatialRadii radii)
    {
        radii.Validate();
        if (mapName != "de_mirage")
            throw new ArgumentException("Spatial v1 supports de_mirage only.");
        this.radii = radii;
        geometry = MapFeatureGeometries.Find(mapName)!;
    }

    public WinSpatialFeatures Build(DemoFrame frame, SemanticFrame semantic)
    {
        if (frame.Tick != semantic.Tick)
            throw new ArgumentException("Spatial frame and semantic tick must match.");
        var alive = frame.Players.Where(p => p.Alive && p.Team is "T" or "CT").ToArray();
        var quality = semantic.Roster;
        var rosterComplete = quality.RosterKnown && quality.TMembers == 5 && quality.CTMembers == 5 &&
            quality.LifeKnown == 10 && quality.AliveKnown == alive.Length &&
            alive.Select(p => p.Id).Distinct(StringComparer.Ordinal).Count() == alive.Length &&
            alive.Count(p => p.Team == "T") <= 5 && alive.Count(p => p.Team == "CT") <= 5;
        var positionCountsAgree = quality.AlivePositionKnown == alive.Count(HasPosition);
        var carrier = frame.Bomb.CarrierId is null ? null :
            alive.FirstOrDefault(p => p.Id == frame.Bomb.CarrierId && p.Team == "T");
        var bomb = frame.Bomb;
        var carried = bomb.State is "carried" or "planting";
        var bombStatus = bomb.State is "defused" or "exploded" ? "not-applicable"
            : bomb.State is not ("carried" or "planting" or "dropped" or "planted" or "defusing") ? "unknown-state"
            : carried && carrier is null ? "missing-carrier"
            : Finite(bomb.X) && Finite(bomb.Y) && Finite(bomb.Z) ? "known" : "missing-position";
        MapPoint? bombPosition = bombStatus == "known" ? new(bomb.X!.Value, bomb.Y!.Value) : null;
        var t = Team("T");
        var ct = Team("CT");
        return new(bomb.State, bombStatus, rosterComplete ? "complete" : "incomplete", t, ct,
            Carrier(), Difference(t.NearestC4Distance, ct.NearestC4Distance),
            Difference(t.SecondC4Distance, ct.SecondC4Distance));

        WinSpatialTeam Team(string team)
        {
            var members = alive.Where(p => p.Team == team).ToArray();
            var positioned = members.Where(HasPosition).ToArray();
            var status = !rosterComplete ? "incomplete-roster" :
                !positionCountsAgree || positioned.Length != members.Length ? "missing-position" : "complete";
            var complete = status == "complete";
            var c4Distances = bombPosition is null ? [] : positioned.Select(p => Distance(p, bombPosition)).Order().ToArray();
            var support = complete && members.Length >= 2 ? members.Select(p =>
                members.Where(q => q.Id != p.Id).Min(q => Distance(p, new(q.X, q.Y)))).ToArray() : [];
            return new(members.Length, positioned.Length, status, c4Distances.Length,
                complete && c4Distances.Length >= 1 ? c4Distances[0] : null,
                complete && c4Distances.Length >= 2 ? c4Distances[1] : null,
                complete ? positioned.Count(p => Distance(p, geometry.SiteA) <= radii.SiteRadius) : null,
                complete ? positioned.Count(p => Distance(p, geometry.SiteB) <= radii.SiteRadius) : null,
                support.Length > 0 ? support.Average() : null,
                support.Length > 0 ? support.Max() : null);
        }

        WinSpatialCarrier Carrier()
        {
            if (!carried) return new("not-applicable", null, null, null, null);
            if (!rosterComplete) return new("incomplete-roster", null, null, null, null);
            if (carrier is null) return new("missing-carrier", null, null, null, null);
            var teammates = alive.Where(p => p.Team == "T" && p.Id != carrier.Id).ToArray();
            var positioned = teammates.Where(HasPosition).ToArray();
            if (!positionCountsAgree || !HasPosition(carrier) || positioned.Length != teammates.Length)
                return new("missing-position", teammates.Length, positioned.Length, null, null);
            var distances = teammates.Select(p => Distance(p, new(carrier.X, carrier.Y))).ToArray();
            return new(teammates.Length == 0 ? "no-teammates" : "available", teammates.Length,
                positioned.Length, distances.Length > 0 ? distances.Min() : null,
                distances.Count(d => d <= radii.SupportRadius));
        }
    }

    private static bool Finite(float? value) => value.HasValue && float.IsFinite(value.Value);
    private static bool HasPosition(PlayerSnapshot p) =>
        float.IsFinite(p.X) && float.IsFinite(p.Y) && float.IsFinite(p.Z);
    private static double Distance(PlayerSnapshot p, MapPoint point) =>
        Math.Sqrt(Math.Pow((double)p.X - point.X, 2) + Math.Pow((double)p.Y - point.Y, 2));
    private static double? Difference(double? a, double? b) => a.HasValue && b.HasValue ? a - b : null;
}

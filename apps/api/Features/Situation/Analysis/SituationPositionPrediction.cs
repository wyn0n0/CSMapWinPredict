using CsDemoMap.Api.Models;

namespace CsDemoMap.Api.Services;

// Constant-velocity extrapolation of the current structured snapshot only.
// A null result is unavailable, never an assumption that the player is stationary.
internal static class SituationPositionPrediction
{
    internal static SituationVec3? Project(MinimapSceneV1 scene, int index, double seconds,
        SituationPositionPredictionRuleSet rules, double maxDistance, bool continuousSlope = false)
    {
        var player = scene.Players[index];
        if (player.Position is not { } p || player.Velocity is not { } v ||
            !double.IsFinite(v.X) || !double.IsFinite(v.Y) || !double.IsFinite(v.Z)) return null;
        if (scene.DataQuality.Any(q => q.Code is SituationDataQualityCodes.MissingFrame or SituationDataQualityCodes.StaleFrame ||
            q.FieldPaths.Any(path => path == "/players" || path == $"/players/{index}" ||
                path.StartsWith($"/players/{index}/velocity", StringComparison.Ordinal) ||
                path.StartsWith($"/players/{index}/position", StringComparison.Ordinal)))) return null;
        var scale = MapFeatureGeometries.Find("de_mirage")!.Scale * 1024;
        var speed = Math.Sqrt(v.X*v.X + v.Y*v.Y) * scale;
        if (!double.IsFinite(speed) || speed > rules.MaxSpeed || Math.Abs(v.Z)*scale > (continuousSlope ? speed+rules.MaxVerticalSpeed : rules.MaxVerticalSpeed) ||
            speed*seconds > maxDistance) return null;
        // Scene velocity already uses radar coordinates, including the inverted Y axis.
        var target = new SituationVec3(p.X + v.X*seconds, p.Y + v.Y*seconds, p.Z);
        return double.IsFinite(target.Z) && target.X is >= 0 and <= 1 && target.Y is >= 0 and <= 1 ? target : null;
    }
}

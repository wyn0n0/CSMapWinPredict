using CsDemoMap.Api.Models;

namespace CsDemoMap.Api.Services;

// This is the only shape intended to cross the future model boundary. It has no
// demo reference, file mapping, window index, request metadata or raw events.
internal sealed record SituationModelInputV1(
    string SchemaVersion,
    string SceneBuilderVersion,
    string GeometryVersion,
    string Map,
    int Tick,
    int TickRate,
    SituationRound Round,
    IReadOnlyList<SituationPlayer> Players,
    SituationTeams Teams,
    SituationBomb Bomb,
    IReadOnlyList<SituationUtility> Utilities,
    IReadOnlyList<SituationEffect> Effects,
    SituationGeometry Geometry,
    IReadOnlyList<SituationDataQuality> DataQuality);

internal static class SituationModelInputProjector
{
    public static SituationModelInputV1 Project(MinimapSceneV1 scene)
    {
        SituationContractValidator.Validate(scene);
        return new(
            scene.SchemaVersion,
            scene.SceneBuilderVersion,
            scene.GeometryVersion,
            scene.Map,
            scene.Tick,
            scene.TickRate,
            scene.Round with { Ref = null },
            scene.Players,
            scene.Teams,
            scene.Bomb,
            scene.Utilities,
            scene.Effects,
            scene.Geometry,
            scene.DataQuality);
    }

    public static string Serialize(MinimapSceneV1 scene) =>
        SituationCanonicalJson.Serialize(Project(scene));
}

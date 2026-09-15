namespace CsDemoMap.Api.Models;

public static class SituationWindowSidecarVersions
{
    public const string V1 = "situation-window-sidecar-v1";
}

// Contains only observations needed to rebuild a situation scene. Outcome fields,
// raw events, player identities and win predictions deliberately have no place here.
public sealed record SituationRoundBoundaryV1(
    string RoundRef,
    int SegmentId,
    int? RoundNumber,
    int StartTick,
    int? LiveTick,
    int? ObservedEndTick);

public sealed record SituationWindowSidecarV1(
    string SchemaVersion,
    int WindowIndex,
    string Map,
    int TickRate,
    int DataFromTick,
    int DataToTick,
    IReadOnlyList<SituationRoundBoundaryV1> RoundBoundaries,
    IReadOnlyList<SemanticFrame> SemanticFrames);

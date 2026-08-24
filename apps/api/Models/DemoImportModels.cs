namespace CsDemoMap.Api.Models;

public sealed record DemoManifest(
    string Id,
    DemoMetadata Metadata,
    IReadOnlyList<TimelineEvent> Events,
    int FrameCount,
    int UtilityTrackCount,
    int UtilityEffectCount,
    int PlayerUtilityStateCount,
    int PlayerEquipmentStateCount,
    int WindowSeconds,
    int WindowCount,
    int SchemaVersion,
    IReadOnlyList<RoundResult> RoundResults);

public sealed record DemoWindow(
    int Index,
    double CoreFromSeconds,
    double CoreToSeconds,
    double DataFromSeconds,
    double DataToSeconds,
    int FirstFrameIndex,
    int TotalFrameCount,
    IReadOnlyList<DemoFrame> Frames,
    IReadOnlyList<UtilityTrack> UtilityTracks,
    IReadOnlyList<UtilityEffectTrack> UtilityEffects,
    IReadOnlyList<PlayerUtilityState> PlayerUtilityStates,
    IReadOnlyList<PlayerEquipmentState> PlayerEquipmentStates);

public sealed record DemoImportAccepted(string Id, string Status);

public sealed record DemoImportStatus(
    string Id,
    string Status,
    string FileName,
    long FileSizeBytes,
    string? Error,
    DemoManifest? Manifest);

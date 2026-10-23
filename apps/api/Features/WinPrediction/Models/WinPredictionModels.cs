namespace CsDemoMap.Api.Models;

public sealed record WinProbability(
    double TWin,
    double CTWin);

public sealed record WinPredictionPoint(
    int Tick,
    double TimeSeconds,
    string RoundId,
    int SegmentId,
    int RoundNumber,
    string Phase,
    double TWin,
    double CTWin,
    WinPredictionQuality? Quality = null,
    IReadOnlyList<string>? Triggers = null);

public sealed record WinPredictionQuality(bool EquipmentMissing, bool PositionMissing);

public sealed record WinPredictionManifest(
    string Status,
    int SchemaVersion,
    string SemanticVersion,
    string? SelectedModel,
    string? Calibration,
    string? ArtifactSha256,
    int SampleCount,
    double SampleIntervalSeconds,
    string? Error,
    string SamplingMode = "regular",
    int? InferenceSampleCount = null);

public sealed record WinTimelinePredictionResult(
    WinPredictionManifest Manifest,
    IReadOnlyList<WinPredictionPoint> Points,
    IReadOnlyList<WinPredictionPoint>? ReviewPoints = null);

public sealed record WinModelStatus(
    string Status,
    bool Ready,
    int? SchemaVersion,
    string? SemanticVersion,
    string? SelectedModel,
    string? Calibration,
    int? FeatureCount,
    int? SelfTestFixtures,
    string? ArtifactSha256,
    IReadOnlyDictionary<string, string>? Runtime,
    string? Error);

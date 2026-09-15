using System.Text.Json.Nodes;

namespace CsDemoMap.Api.Models;

public sealed record WinInferenceSample(
    int Tick,
    double DemoTimeSeconds,
    string RoundId,
    int SegmentId,
    int RoundNumber,
    string MapName,
    string Phase,
    JsonObject Features);

public sealed record WinTrainingTarget(int LabelTWin, int SampleCount);

public sealed record WinFeatureRejection(
    int Tick,
    string? RoundId,
    string Phase,
    string Reason);

public sealed record WinFeatureBuildReport(
    int RowCount,
    int ExportedRounds,
    IReadOnlyDictionary<string, int> Dispositions,
    IReadOnlyDictionary<string, int> Rejected,
    IReadOnlyList<WinFeatureRejection> RejectionDetails,
    IReadOnlyDictionary<string, int> ByPhaseOutcome,
    IReadOnlyDictionary<string, int> NumberChanges,
    int PauseFlagObservations,
    int HardPauseObservations,
    IReadOnlyDictionary<string, double?> FirstLiveElapsed);

public sealed record WinFeatureBuildResult(
    IReadOnlyList<WinInferenceSample> Samples,
    IReadOnlyDictionary<string, WinTrainingTarget> Targets,
    WinFeatureBuildReport Report);

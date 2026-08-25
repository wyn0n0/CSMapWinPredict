namespace CsDemoMap.Api.Models;

public sealed record WinTrainingRow(
    int SchemaVersion,
    string MatchId,
    string MapName,
    int RoundNumber,
    int Tick,
    double TimeSeconds,
    WinFeatureSnapshot Features,
    int LabelTWin,
    double SampleWeight);

public sealed record WinFeatureSnapshot(
    string Phase,
    double ElapsedSeconds,
    double RemainingSeconds,
    int ScoreT,
    int ScoreCT,
    int ConsecutiveLossesT,
    int ConsecutiveLossesCT,
    BombFeatureSnapshot Bomb,
    TeamFeatureSnapshot T,
    TeamFeatureSnapshot CT,
    IReadOnlyList<PlayerFeatureSnapshot> Players,
    IReadOnlyList<ZoneFeatureSnapshot> Zones,
    BaselineFeatureSnapshot Baseline);

public sealed record BaselineFeatureSnapshot(
    string BombState,
    string? PreviousBombState,
    int BombStateChangeCount,
    double SecondsSinceBombStateChange,
    bool BombWasDropped,
    bool BombWasPlanting,
    bool BombWasPlanted,
    bool BombWasDefusing,
    double? TPositionDispersion,
    double? CTPositionDispersion,
    double? NearestOpponentDistance,
    double? TMeanDistanceToSiteA,
    double? TMeanDistanceToSiteB,
    double? CTMeanDistanceToSiteA,
    double? CTMeanDistanceToSiteB,
    double? TMinDistanceToSiteA,
    double? TMinDistanceToSiteB,
    double? CTMinDistanceToSiteA,
    double? CTMinDistanceToSiteB,
    int EquipmentValueDifference,
    int HealthDifference,
    int AliveDifference);

public sealed record BombFeatureSnapshot(
    string State,
    bool HasCarrier,
    bool HasDefuser,
    string? Site,
    string? Region,
    float? WorldX,
    float? WorldY,
    float? WorldZ,
    double? SecondsToExplosion,
    double? SecondsToDefuse);

public sealed record TeamFeatureSnapshot(
    int Alive,
    int TotalHealth,
    int TotalKills,
    int TotalDeaths,
    int EquipmentKnownPlayers,
    int TotalMoney,
    int TotalArmor,
    int HelmetCount,
    int DefuserCount,
    int EquipmentValue,
    int GrenadeCount,
    int RifleCount,
    int SniperCount);

public sealed record PlayerFeatureSnapshot(
    string Team,
    bool Alive,
    int Health,
    string Region,
    string? Weapon,
    int Kills,
    int Deaths,
    float WorldX,
    float WorldY,
    float WorldZ,
    float Yaw,
    float VelocityX,
    float VelocityY,
    float VelocityZ,
    int? Money,
    int? Armor,
    bool? HasHelmet,
    bool? HasDefuser,
    int? EquipmentValue,
    int? GrenadeCount);

public sealed record ZoneFeatureSnapshot(
    string Region,
    int TAlive,
    int CTAlive,
    int TTotal,
    int CTTotal);

public sealed record WinDatasetExportSummary(
    string OutputPath,
    int DemoCount,
    int RoundCount,
    int RowCount);

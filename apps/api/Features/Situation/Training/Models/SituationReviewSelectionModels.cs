namespace CsDemoMap.Api.Models;

internal sealed record ReviewPoolEntry(
    string SampleId, SituationTrainingSplit Split, string MatchRef, string RoundRef, int Tick,
    string SourceSceneSha256, string ModelInputSha256, string FactsSha256,
    string PrelabelSha256, string RecordSha256, long LineNumber,
    IReadOnlyList<string> Categories, IReadOnlyList<string> EventCategories);

internal sealed record ReviewSplitPolicy(
    int Samples, int Matches, int MinPerMatch, int MaxPerMatch,
    IReadOnlyDictionary<string, int> Quotas);

internal sealed record ReviewSelectionPolicy(
    string SchemaVersion, string AlgorithmVersion, string StableHashDomain,
    int MaxPerRound, int ExceptionalMaxPerRound,
    IReadOnlyList<string> IsolationBlockingQualityCodes,
    IReadOnlyList<string> RelevantQualityCodes,
    IReadOnlyList<string> EventCategories,
    IReadOnlyList<string> CategoryPriority,
    IReadOnlyList<string> TieBreaker,
    IReadOnlyDictionary<string, ReviewSplitPolicy> Splits);

internal sealed record ReviewSelectedEntry(int ReviewOrdinal, ReviewPoolEntry Entry);
internal sealed record ReviewQuotaShortfall(
    string Split, string Category, int Target, int Available, int Selected,
    string ReasonCode, IReadOnlyList<string> AlternativeCategories);
internal sealed record ReviewRoundException(
    string Split, string MatchRef, string RoundRef,
    IReadOnlyList<string> SampleIds, IReadOnlyList<string> DistinctEventCategories);
internal sealed record ReviewSelectionPlan(
    IReadOnlyList<ReviewSelectedEntry> Selected,
    IReadOnlyDictionary<string, IReadOnlyList<string>> Backups,
    IReadOnlyList<ReviewQuotaShortfall> QuotaShortfalls,
    IReadOnlyList<ReviewRoundException> RoundExceptions);

using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CsDemoMap.Api.Models;

namespace CsDemoMap.Api.Services;

internal sealed record SituationTrainingDatasetValidationResult(
    SituationTrainingDatasetManifestV1 Manifest,
    SituationTrainingDatasetStatisticsSnapshot Statistics,
    long JsonlRows);

/// <summary>
/// Streaming readback validator for complete full stage-four datasets. JSONL bodies
/// are never materialized; memory is limited to per-round records and the identity/
/// hash sets required for uniqueness and cross-split isolation.
/// </summary>
internal static class SituationTrainingDatasetValidator
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly JsonSerializerOptions StrictJson = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };
    private static readonly string[] RequiredFiles =
    [
        "dev.jsonl", "label-stats.json", "provenance.json", "split.json", "test.jsonl", "train.jsonl"
    ];

    internal static async Task<SituationTrainingDatasetValidationResult> VerifyAsync(
        string repositoryRoot,
        string artifactRoot,
        CancellationToken cancellationToken,
        bool verifyCurrentSourceFiles = true,
        SituationTrainingDatasetManifestV1? preparedManifest = null)
    {
        repositoryRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(repositoryRoot));
        artifactRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(artifactRoot));
        if (!Directory.Exists(artifactRoot))
            throw new DirectoryNotFoundException("Training dataset directory does not exist.");

        var manifestPath = Path.Combine(artifactRoot, "manifest.json");
        var manifestJson = await ReadStrictJsonTextAsync(manifestPath, "Training manifest", cancellationToken);
        var diskManifest = SituationTrainingContractJson.DeserializeManifest(manifestJson);
        if (preparedManifest is not null && diskManifest.Status != SituationArtifactStatus.Incomplete)
            throw new InvalidDataException("Prepared publication requires an incomplete on-disk manifest.");
        var manifest = preparedManifest ?? diskManifest;
        if (manifest.Status != SituationArtifactStatus.Complete ||
            manifest.Mode != SituationTrainingExportMode.Full ||
            !manifest.Trainable || manifest.SampleLimit is not null)
            throw new InvalidDataException("Dataset validator requires a complete, trainable full manifest.");
        ValidateManifestFileSet(manifest, artifactRoot);
        await SituationTrainingManifestValidator.VerifyAsync(
            repositoryRoot, artifactRoot, manifest, cancellationToken);

        var split = await ReadStrictJsonAsync<SituationStageFourSplitDocument>(
            Path.Combine(artifactRoot, "split.json"), "Training split", cancellationToken);
        SituationDatasetSplit.ValidateDocument(split, split.ParentSplitSha256, split.ParentManifestSha256);
        if (manifest.SplitSha256 != await SituationArtifactIO.FileSha256Async(
                Path.Combine(artifactRoot, "split.json"), cancellationToken) ||
            manifest.ParentManifestSha256 != split.ParentManifestSha256)
            throw new InvalidDataException("Manifest and split identity differ.");

        var provenance = await ReadStrictJsonAsync<SituationTrainingProvenanceDocumentV1>(
            Path.Combine(artifactRoot, "provenance.json"), "Training provenance", cancellationToken);
        ValidateProvenanceBindings(manifest, split, provenance);
        if (verifyCurrentSourceFiles)
        {
            await SituationTrainingProvenanceBuilder.VerifyCurrentRepositoryStateAsync(
                repositoryRoot, provenance, cancellationToken);
        }

        var expectedMatches = BuildExpectedMatches(split, provenance, manifest.SplitSha256);
        var roundMappings = provenance.RoundMappings.ToDictionary(item => item.RoundRef, StringComparer.Ordinal);
        var demoMappings = provenance.DemoMappings.ToDictionary(item => item.MatchRef, StringComparer.Ordinal);
        var identityVersions = IdentityVersions(manifest.SplitSha256);
        var statistics = new SituationTrainingDatasetStatistics(
            manifest.SplitSha256,
            manifest.Versions.InputRepresentation,
            manifest.InputRepresentationConfigSha256);
        foreach (var splitItem in expectedMatches)
        foreach (var matchRef in splitItem.Value)
            statistics.RegisterMatch(splitItem.Key, matchRef);

        var matchOwners = new Dictionary<string, SituationTrainingSplit>(StringComparer.Ordinal);
        var roundOwners = new Dictionary<string, SituationTrainingSplit>(StringComparer.Ordinal);
        var sceneOwners = new Dictionary<string, SituationTrainingSplit>(StringComparer.Ordinal);
        var sampleIds = new HashSet<string>(StringComparer.Ordinal);
        long rows = 0;
        foreach (var splitName in new[] { "train", "dev", "test" })
        {
            var splitValue = ParseSplit(splitName);
            var path = Path.Combine(artifactRoot, splitName + ".jsonl");
            var count = await ValidateJsonlAsync(
                path,
                splitValue,
                expectedMatches[splitValue],
                roundMappings,
                demoMappings,
                identityVersions,
                statistics,
                matchOwners,
                roundOwners,
                sceneOwners,
                sampleIds,
                cancellationToken);
            if (count == 0)
                throw new InvalidDataException($"Training split {splitName} is empty.");
            rows = checked(rows + count);
            var entry = manifest.Files.Single(item => item.Path == splitName + ".jsonl");
            if (entry.Rows != count)
                throw new InvalidDataException($"Manifest row count differs for {splitName}.jsonl.");
        }

        var snapshot = statistics.CreateSnapshot();
        ValidateCounts(manifest.Counts, snapshot.Counts);
        var storedStats = await ReadStrictJsonAsync<SituationLabelStatsV1>(
            Path.Combine(artifactRoot, "label-stats.json"), "Label statistics", cancellationToken);
        ValidateLabelStats(storedStats, snapshot.LabelStats, manifest);
        return new(manifest, snapshot, rows);
    }

    private static async Task<long> ValidateJsonlAsync(
        string path,
        SituationTrainingSplit expectedSplit,
        IReadOnlySet<string> expectedMatches,
        IReadOnlyDictionary<string, SituationTrainingProvenanceRoundMappingV1> roundMappings,
        IReadOnlyDictionary<string, SituationProvenanceDemoMappingV1> demoMappings,
        SituationTrainingIdentityVersions identityVersions,
        SituationTrainingDatasetStatistics statistics,
        IDictionary<string, SituationTrainingSplit> matchOwners,
        IDictionary<string, SituationTrainingSplit> roundOwners,
        IDictionary<string, SituationTrainingSplit> sceneOwners,
        ISet<string> sampleIds,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length == 0)
            return 0;
        var prefix = new byte[Math.Min(3, checked((int)stream.Length))];
        _ = await stream.ReadAsync(prefix, cancellationToken);
        if (prefix.AsSpan().StartsWith(Encoding.UTF8.Preamble))
            throw new InvalidDataException($"Training JSONL uses a UTF-8 BOM: {Path.GetFileName(path)}");
        stream.Seek(-1, SeekOrigin.End);
        if (stream.ReadByte() != (byte)'\n')
            throw new InvalidDataException($"Training JSONL must end with LF: {Path.GetFileName(path)}");
        stream.Seek(0, SeekOrigin.Begin);

        using var checkedStream = new CarriageReturnRejectingStream(stream);
        using var reader = new StreamReader(
            checkedStream, StrictUtf8, detectEncodingFromByteOrderMarks: false, 1024 * 1024, leaveOpen: true);
        SortKey? previous = null;
        RoundKey? activeRound = null;
        var roundRecords = new List<SituationTrainingRecordV1>(16);
        var analyzer = SituationDeterministicAnalyzer.CreateFrozen();
        long rows = 0;
        try
        {
            while (await reader.ReadLineAsync(cancellationToken) is { } line)
            {
                rows = checked(rows + 1);
                if (line.Length == 0 || line.Contains('\r') || line.StartsWith('\uFEFF'))
                    throw new InvalidDataException($"Training JSONL row {rows} is empty, CRLF, or BOM-prefixed.");
                var record = SituationTrainingContractJson.DeserializeRecord(line);
                var canonical = SituationTrainingContractJson.SerializeLine(record);
                if (!string.Equals(line, canonical, StringComparison.Ordinal))
                    throw new InvalidDataException($"Training JSONL row {rows} is not canonical.");
                if (record.Metadata.Split != expectedSplit)
                    throw new InvalidDataException($"Training JSONL row {rows} is in the wrong split file.");
                if (!expectedMatches.Contains(record.Metadata.MatchRef))
                    throw new InvalidDataException($"Training JSONL row {rows} references a match outside its split.");
                ValidateIdentity(record, roundMappings, demoMappings, identityVersions);
                var sourceScene = SituationTrainingRecordBuilder.RestoreSourceScene(record,
                    demoMappings[record.Metadata.MatchRef].MatchId,
                    roundMappings[record.Metadata.RoundRef].SemanticRoundId);
                if (SituationCanonicalJson.Sha256(sourceScene) != record.Metadata.SourceSceneSha256)
                    throw new InvalidDataException("Training source scene hash differs from restored provenance.");
                var rebuilt = analyzer.Analyze(sourceScene);
                if (rebuilt.Facts.Sha256 != record.Metadata.FactsSha256 ||
                    rebuilt.Narrative.Sha256 != record.Metadata.PrelabelSha256)
                    throw new InvalidDataException("Training Facts or Narrative differ from frozen recomputation.");

                var key = new SortKey(
                    record.Metadata.MatchRef,
                    record.Metadata.RoundRef,
                    record.Metadata.Tick,
                    record.SampleId);
                if (previous is { } old && Compare(old, key) >= 0)
                    throw new InvalidDataException($"Training JSONL is not strictly sorted at row {rows}.");
                previous = key;

                AddOwner(matchOwners, record.Metadata.MatchRef, expectedSplit, "matchRef");
                AddOwner(roundOwners, record.Metadata.RoundRef, expectedSplit, "roundRef");
                AddOwner(sceneOwners, record.Metadata.SourceSceneSha256, expectedSplit, "sourceSceneSha256");
                if (!sampleIds.Add(record.SampleId))
                    throw new InvalidDataException("Training dataset contains a duplicate sampleId.");

                var round = new RoundKey(record.Metadata.MatchRef, record.Metadata.RoundRef);
                if (activeRound is { } current && current != round)
                {
                    SituationTrainingContractJson.ValidateRoundWeights(roundRecords);
                    roundRecords.Clear();
                }
                activeRound = round;
                roundRecords.Add(record);
                if (roundRecords.Count > 16)
                    throw new InvalidDataException("Training round contains more than 16 samples.");
                statistics.AddRecord(record);
            }
        }
        catch (DecoderFallbackException exception)
        {
            throw new InvalidDataException($"Training JSONL is not valid UTF-8: {Path.GetFileName(path)}", exception);
        }
        if (roundRecords.Count > 0)
            SituationTrainingContractJson.ValidateRoundWeights(roundRecords);
        return rows;
    }

    private static IReadOnlyDictionary<SituationTrainingSplit, IReadOnlySet<string>> BuildExpectedMatches(
        SituationStageFourSplitDocument split,
        SituationTrainingProvenanceDocumentV1 provenance,
        string splitSha256)
    {
        var mappings = provenance.DemoMappings.ToDictionary(item => item.FileName, StringComparer.Ordinal);
        var versions = IdentityVersions(splitSha256);
        var result = new Dictionary<SituationTrainingSplit, IReadOnlySet<string>>();
        Add(SituationTrainingSplit.Train, split.Train);
        Add(SituationTrainingSplit.Dev, split.Dev);
        Add(SituationTrainingSplit.Test, split.Test);
        if (mappings.Count != split.SourceDemoCount)
            throw new InvalidDataException("Provenance demo mappings do not cover the complete split.");
        return result;

        void Add(SituationTrainingSplit splitValue, IReadOnlyList<SituationStageFourSplitMember> members)
        {
            var matches = new HashSet<string>(StringComparer.Ordinal);
            foreach (var member in members)
            {
                if (!mappings.TryGetValue(member.FileName, out var mapping) || mapping.MatchId != member.MatchId)
                    throw new InvalidDataException("Provenance demo mapping differs from the split.");
                var expected = SituationTrainingContractJson.CreateIdentity(
                    member.MatchId, "s0-a1", 0, versions).MatchRef;
                if (mapping.MatchRef != expected || !matches.Add(mapping.MatchRef))
                    throw new InvalidDataException("Provenance matchRef is invalid or duplicated.");
            }
            result.Add(splitValue, matches);
        }
    }

    private static void ValidateProvenanceBindings(
        SituationTrainingDatasetManifestV1 manifest,
        SituationStageFourSplitDocument split,
        SituationTrainingProvenanceDocumentV1 provenance)
    {
        SituationTrainingProvenanceBuilder.Validate(provenance);
        if (provenance.SplitSha256 != manifest.SplitSha256 ||
            provenance.ParentSplitSha256 != split.ParentSplitSha256 ||
            provenance.ParentManifestSha256 != manifest.ParentManifestSha256 ||
            provenance.ParentManifestReference != split.ParentManifestReference ||
            provenance.AnalysisRulesSha256 != SituationAnalysisRuleLoader.LoadFrozen().Sha256 ||
            provenance.SelectionConfigSha256 != manifest.SelectionConfigSha256 ||
            provenance.InputRepresentationVersion != manifest.Versions.InputRepresentation ||
            provenance.InputRepresentationConfigSha256 != manifest.InputRepresentationConfigSha256 ||
            !provenance.SchemaFiles.SequenceEqual(manifest.SchemaFiles))
            throw new InvalidDataException("Provenance identity does not match the manifest and split.");
    }

    private static void ValidateManifestFileSet(
        SituationTrainingDatasetManifestV1 manifest,
        string artifactRoot)
    {
        if (!manifest.Files.Select(item => item.Path).SequenceEqual(RequiredFiles, StringComparer.Ordinal))
            throw new InvalidDataException("Complete full manifest file set is invalid.");
        foreach (var file in manifest.Files)
        {
            var jsonl = file.Path.EndsWith(".jsonl", StringComparison.Ordinal);
            if (jsonl != (file.Rows is not null))
                throw new InvalidDataException("Only JSONL manifest entries may declare row counts.");
        }
        var actual = Directory.EnumerateFiles(artifactRoot, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(artifactRoot, path).Replace('\\', '/'))
            .Where(path => path != "manifest.json")
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (!actual.SequenceEqual(RequiredFiles, StringComparer.Ordinal))
            throw new InvalidDataException("Dataset contains missing or extra non-manifest files.");
        if (Directory.EnumerateDirectories(artifactRoot).Any() ||
            (new DirectoryInfo(artifactRoot).Attributes & FileAttributes.ReparsePoint) != 0 ||
            Directory.EnumerateFiles(artifactRoot).Any(path =>
                (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0))
            throw new InvalidDataException("Complete dataset must contain only direct regular files.");
    }

    private static void ValidateCounts(
        IReadOnlyDictionary<string, SituationDatasetCountsV1> actual,
        IReadOnlyDictionary<string, SituationDatasetCountsV1> expected)
    {
        foreach (var name in new[] { "train", "dev", "test" })
        {
            if (!actual.TryGetValue(name, out var actualValue) ||
                !expected.TryGetValue(name, out var expectedValue) || actualValue != expectedValue)
                throw new InvalidDataException($"Manifest counts differ from JSONL records for {name}.");
        }
    }

    private static void ValidateLabelStats(
        SituationLabelStatsV1 actual,
        SituationLabelStatsV1 expected,
        SituationTrainingDatasetManifestV1 manifest)
    {
        if (actual.SchemaVersion != SituationTrainingContractVersions.LabelStats ||
            actual.DatasetSchemaVersion != SituationTrainingContractVersions.DatasetManifest ||
            actual.SplitSha256 != manifest.SplitSha256 ||
            actual.InputRepresentationVersion != manifest.Versions.InputRepresentation ||
            actual.InputRepresentationConfigSha256 != manifest.InputRepresentationConfigSha256 ||
            actual.ExactTokenizerMeasured)
            throw new InvalidDataException("Label statistics identity is invalid.");
        if (!actual.Sections.SequenceEqual(actual.Sections.OrderBy(item => item.Name, StringComparer.Ordinal)) ||
            actual.Sections.Select(item => item.Name).Distinct(StringComparer.Ordinal).Count() != actual.Sections.Count)
            throw new InvalidDataException("Label statistics sections are duplicated or unsorted.");
        foreach (var section in actual.Sections)
        {
            if (string.IsNullOrWhiteSpace(section.Name) ||
                !section.Metrics.SequenceEqual(section.Metrics.OrderBy(item => item.Name, StringComparer.Ordinal)) ||
                section.Metrics.Select(item => item.Name).Distinct(StringComparer.Ordinal).Count() != section.Metrics.Count ||
                section.Metrics.Any(item => string.IsNullOrWhiteSpace(item.Name) ||
                                            !double.IsFinite(item.Value) || item.Value < 0))
                throw new InvalidDataException("Label statistics metrics are invalid, duplicated, or unsorted.");
        }
        var expectedRecordSections = expected.Sections
            .Where(section => SituationTrainingDatasetStatistics.IsRecordDerivedSection(section.Name))
            .ToDictionary(section => section.Name, StringComparer.Ordinal);
        var actualRecordSections = actual.Sections
            .Where(section => SituationTrainingDatasetStatistics.IsRecordDerivedSection(section.Name))
            .ToDictionary(section => section.Name, StringComparer.Ordinal);
        if (!expectedRecordSections.Keys.Order(StringComparer.Ordinal)
                .SequenceEqual(actualRecordSections.Keys.Order(StringComparer.Ordinal), StringComparer.Ordinal))
            throw new InvalidDataException("Label statistics record-derived section set is incomplete.");
        foreach (var item in expectedRecordSections)
        {
            if (!MetricsEqual(item.Value.Metrics, actualRecordSections[item.Key].Metrics))
                throw new InvalidDataException($"Label statistics differ from JSONL records: {item.Key}");
        }
        foreach (var split in new[] { "train", "dev", "test" })
        foreach (var prefix in new[]
                 {
                     "selector.candidates.", "selector.selected.", "selector.merged.",
                     "selector.removed-by-limit.", "selector.missing-anchors.",
                     "selector.shortages.", "eligibility-rejected."
                 })
        {
            if (!actual.Sections.Any(section => section.Name == prefix + split))
                throw new InvalidDataException("Label statistics omit selector or eligibility coverage.");
        }
    }

    private static bool MetricsEqual(
        IReadOnlyList<SituationLabelStatMetricV1> left,
        IReadOnlyList<SituationLabelStatMetricV1> right) =>
        left.Count == right.Count && left.Zip(right).All(pair =>
            pair.First.Name == pair.Second.Name && pair.First.Value.Equals(pair.Second.Value));

    private static void AddOwner(
        IDictionary<string, SituationTrainingSplit> owners,
        string value,
        SituationTrainingSplit split,
        string description)
    {
        if (owners.TryGetValue(value, out var owner) && owner != split)
            throw new InvalidDataException($"Training dataset has a cross-split {description} conflict.");
        owners[value] = split;
    }

    private static void ValidateIdentity(
        SituationTrainingRecordV1 record,
        IReadOnlyDictionary<string, SituationTrainingProvenanceRoundMappingV1> roundMappings,
        IReadOnlyDictionary<string, SituationProvenanceDemoMappingV1> demoMappings,
        SituationTrainingIdentityVersions identityVersions)
    {
        if (!roundMappings.TryGetValue(record.Metadata.RoundRef, out var round) ||
            round.MatchRef != record.Metadata.MatchRef ||
            !demoMappings.TryGetValue(record.Metadata.MatchRef, out var demo))
            throw new InvalidDataException("Training record identity is absent from provenance mappings.");
        var expected = SituationTrainingContractJson.CreateIdentity(
            demo.MatchId, round.SemanticRoundId, record.Metadata.Tick, identityVersions);
        if (expected.MatchRef != record.Metadata.MatchRef ||
            expected.RoundRef != record.Metadata.RoundRef ||
            expected.SampleId != record.SampleId)
            throw new InvalidDataException("Training record matchRef, roundRef, or sampleId is invalid.");
    }

    private static async Task<T> ReadStrictJsonAsync<T>(
        string path,
        string description,
        CancellationToken cancellationToken)
    {
        var json = await ReadStrictJsonTextAsync(path, description, cancellationToken);
        SituationTrainingContractJson.RejectDuplicateProperties(json, description);
        SituationTrainingContractJson.RequireCompleteShape<T>(json, description);
        try
        {
            return JsonSerializer.Deserialize<T>(json, StrictJson)
                ?? throw new InvalidDataException($"{description} is empty.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException($"{description} is invalid or contains unknown fields.", exception);
        }
    }

    private static async Task<string> ReadStrictJsonTextAsync(
        string path,
        string description,
        CancellationToken cancellationToken)
    {
        var bytes = await File.ReadAllBytesAsync(path, cancellationToken);
        if (bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble))
            throw new InvalidDataException($"{description} uses a UTF-8 BOM.");
        try
        {
            return StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException exception)
        {
            throw new InvalidDataException($"{description} is not valid UTF-8.", exception);
        }
    }

    private static SituationTrainingSplit ParseSplit(string value) => value switch
    {
        "train" => SituationTrainingSplit.Train,
        "dev" => SituationTrainingSplit.Dev,
        "test" => SituationTrainingSplit.Test,
        _ => throw new InvalidDataException("Training split is invalid.")
    };

    private static int Compare(SortKey left, SortKey right)
    {
        var result = string.Compare(left.MatchRef, right.MatchRef, StringComparison.Ordinal);
        if (result != 0) return result;
        result = string.Compare(left.RoundRef, right.RoundRef, StringComparison.Ordinal);
        if (result != 0) return result;
        result = left.Tick.CompareTo(right.Tick);
        return result != 0 ? result : string.Compare(left.SampleId, right.SampleId, StringComparison.Ordinal);
    }

    private static SituationTrainingIdentityVersions IdentityVersions(string splitSha256) => new(
        SituationTrainingContractVersions.Data,
        splitSha256,
        SituationContractVersions.Scene,
        SituationSceneBuilder.BuilderVersion,
        SituationSceneBuilder.GeometryVersion,
        SituationContractVersions.Facts,
        SituationAnalysisRuleLoader.FrozenAnalysisRuleVersion,
        SituationContractVersions.Narrative,
        WinFeatureSampleBuilder.SemanticVersion,
        SituationTrainingContractVersions.Selection,
        SituationTrainingContractVersions.InputRepresentation);

    private readonly record struct SortKey(string MatchRef, string RoundRef, int Tick, string SampleId);
    private readonly record struct RoundKey(string MatchRef, string RoundRef);

    private sealed class CarriageReturnRejectingStream(Stream inner) : Stream
    {
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position
        {
            get => inner.Position;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = inner.Read(buffer, offset, count);
            Reject(buffer.AsSpan(offset, read));
            return read;
        }

        public override int Read(Span<byte> buffer)
        {
            var read = inner.Read(buffer);
            Reject(buffer[..read]);
            return read;
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            var read = await inner.ReadAsync(buffer, cancellationToken);
            Reject(buffer.Span[..read]);
            return read;
        }

        public override async Task<int> ReadAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken)
        {
            var read = await inner.ReadAsync(buffer.AsMemory(offset, count), cancellationToken);
            Reject(buffer.AsSpan(offset, read));
            return read;
        }

        public override int ReadByte()
        {
            var value = inner.ReadByte();
            if (value == '\r')
                throw new InvalidDataException("Training JSONL contains a carriage return.");
            return value;
        }

        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            // The owning validator disposes the underlying FileStream.
            base.Dispose(disposing);
        }

        private static void Reject(ReadOnlySpan<byte> value)
        {
            if (value.Contains((byte)'\r'))
                throw new InvalidDataException("Training JSONL contains a carriage return.");
        }
    }
}

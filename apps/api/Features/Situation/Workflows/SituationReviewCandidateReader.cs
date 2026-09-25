using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using CsDemoMap.Api.Models;

namespace CsDemoMap.Api.Services;

internal static class SituationReviewCandidateReader
{
    internal const string BaseProvenanceSha256 = "93fb7d2f0339f9a1c4b9ba73bfe2b9ecb4f9da655611181960f116ca62a76427";
    internal const string BaseSplitSha256 = "fddbf3f8feff81e8930bf309c68671ae2561a55e51985b0b5868a05773fbbd9f";
    internal static readonly UTF8Encoding StrictUtf8 = new(false, true);

    internal static async Task<SituationReviewCandidateBase> OpenApprovedBaseAsync(string datasetRoot, CancellationToken cancellationToken)
    {
        datasetRoot = Path.GetFullPath(datasetRoot);
        var repositoryRoot = SituationArtifactIO.FindRepositoryRoot(datasetRoot);
        await RequireHashAsync(Path.Combine(datasetRoot, "manifest.json"), SituationReviewSelectionPolicy.ApprovedDatasetSha256, cancellationToken);
        await RequireHashAsync(Path.Combine(datasetRoot, "provenance.json"), BaseProvenanceSha256, cancellationToken);
        await RequireHashAsync(Path.Combine(datasetRoot, "split.json"), BaseSplitSha256, cancellationToken);
        var manifest = SituationTrainingContractJson.DeserializeManifest(await ReadTextAsync(Path.Combine(datasetRoot, "manifest.json"), cancellationToken));
        if (manifest.Status != SituationArtifactStatus.Complete || manifest.Mode != SituationTrainingExportMode.Full || !manifest.Trainable || manifest.SplitSha256 != BaseSplitSha256)
            throw new InvalidDataException("Review requires the approved complete full dataset.");
        RequireFiles(datasetRoot, ["dev.jsonl", "label-stats.json", "manifest.json", "provenance.json", "split.json", "test.jsonl", "train.jsonl"]);
        foreach (var file in manifest.Files)
        {
            var path = SituationArtifactIO.ResolveRepositoryPath(datasetRoot, file.Path, "Base artifact");
            if (new FileInfo(path).Length != file.Bytes) throw new InvalidDataException("Base artifact size changed: " + file.Path);
            await RequireHashAsync(path, file.Sha256, cancellationToken);
        }
        await SituationTrainingSchemaRegistry.VerifyAsync(repositoryRoot, manifest.SchemaFiles, cancellationToken);
        var provenance = await ReadJsonAsync<SituationTrainingProvenanceDocumentV1>(Path.Combine(datasetRoot, "provenance.json"), cancellationToken);
        SituationTrainingProvenanceBuilder.Validate(provenance);
        if (provenance.AnalysisRulesSha256 != SituationAnalysisRuleLoader.LoadFrozen().Sha256 ||
            provenance.SelectionConfigSha256 != SituationTrainingSelectionLoader.LoadFrozen().Sha256 ||
            provenance.InputRepresentationConfigSha256 != SituationPromptRepresentationLoader.LoadFrozen().Sha256)
            throw new InvalidDataException("Historical producer configuration changed.");
        foreach (var source in provenance.SourceFiles)
        {
            // Only the approved step-seven integration points may differ from the pinned producer.
            if (source.Path is "apps/api/CsDemoMap.Api.csproj" or "apps/cli/DeveloperCommandDispatcher.cs") continue;
            await RequireHashAsync(SituationArtifactIO.ResolveRepositoryPath(repositoryRoot, source.Path, "Historical source"), source.Sha256, cancellationToken);
        }
        return new(datasetRoot, repositoryRoot, manifest, provenance);
    }

    internal static async Task<IReadOnlyList<ReviewPoolEntry>> ReadPoolAsync(SituationReviewCandidateBase source, ReviewSelectionPolicy policy, CancellationToken cancellationToken)
    {
        var result = new List<ReviewPoolEntry>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var split in SituationReviewSelectionPolicy.SplitOrder)
        {
            long lineNumber = 0;
            await foreach (var line in ReadLinesAsync(Path.Combine(source.Root, split + ".jsonl"), cancellationToken))
            {
                // The pinned file bytes have already passed the historical full semantic validation.
                // Do not rerun 30,384 analyses; only selected payloads are fully recomputed below.
                var record = JsonSerializer.Deserialize<SituationTrainingRecordV1>(line, SituationReviewSelectionPolicy.StrictJson)
                    ?? throw new InvalidDataException("Empty source record.");
                if (SituationReviewSelectionPolicy.SplitName(record.Metadata.Split) != split || !ids.Add(record.SampleId))
                    throw new InvalidDataException("Base sample identity or split mismatch.");
                result.Add(SituationReviewSelectionPolicy.Describe(record, ++lineNumber, policy));
            }
            if (lineNumber != source.Manifest.Files.Single(f => f.Path == split + ".jsonl").Rows)
                throw new InvalidDataException("Base row count mismatch.");
        }
        return result;
    }

    internal static async Task<IReadOnlyDictionary<string, SituationTrainingRecordV1>> MaterializeAsync(SituationReviewCandidateBase source,
        IReadOnlyList<ReviewSelectedEntry> selected, ReviewSelectionPolicy policy, CancellationToken cancellationToken)
    {
        var wanted = selected.ToDictionary(e => e.Entry.SampleId, e => e.Entry, StringComparer.Ordinal);
        var records = new Dictionary<string, SituationTrainingRecordV1>(StringComparer.Ordinal);
        var analyzer = SituationDeterministicAnalyzer.CreateFrozen();
        var demos = source.Provenance.DemoMappings.ToDictionary(d => d.MatchRef, StringComparer.Ordinal);
        var rounds = source.Provenance.RoundMappings.ToDictionary(r => r.RoundRef, StringComparer.Ordinal);
        foreach (var split in SituationReviewSelectionPolicy.SplitOrder)
        {
            long lineNumber = 0;
            var positions = wanted.Values.Where(e => SituationReviewSelectionPolicy.SplitName(e.Split) == split).ToDictionary(e => e.LineNumber);
            await foreach (var line in ReadLinesAsync(Path.Combine(source.Root, split + ".jsonl"), cancellationToken))
            {
                if (!positions.TryGetValue(++lineNumber, out var expected)) continue;
                var record = SituationTrainingContractJson.DeserializeRecord(line);
                if (!Equal(expected, SituationReviewSelectionPolicy.Describe(record, lineNumber, policy)))
                    throw new InvalidDataException("Selected source record/hash differs from source index.");
                var scene = SituationTrainingRecordBuilder.RestoreSourceScene(record, demos[record.Metadata.MatchRef].MatchId, rounds[record.Metadata.RoundRef].SemanticRoundId);
                var rebuilt = analyzer.Analyze(scene);
                if (SituationCanonicalJson.Sha256(scene) != expected.SourceSceneSha256 || rebuilt.Facts.Sha256 != expected.FactsSha256 || rebuilt.Narrative.Sha256 != expected.PrelabelSha256)
                    throw new InvalidDataException("Selected scene/Facts/Narrative differs from frozen recomputation.");
                records.Add(record.SampleId, record);
            }
        }
        if (records.Count != wanted.Count) throw new InvalidDataException("Selected source record is missing.");
        return records;
    }

    internal static SituationReviewCandidateV1 Candidate(ReviewSelectedEntry selected, SituationTrainingRecordV1 record) => new(
        SituationTrainingContractVersions.ReviewCandidate, selected.ReviewOrdinal, record.SampleId, record.Metadata.Split,
        record.Metadata.MatchRef, record.Metadata.RoundRef, record.Metadata.SelectionTags, record.Input, record.Output,
        SituationCanonicalJson.Sha256(record), record.Metadata.PrelabelSha256);

    internal static bool Equal<T>(T left, T right) => SituationCanonicalJson.Serialize(left) == SituationCanonicalJson.Serialize(right);
    internal static async Task RequireHashAsync(string path, string expected, CancellationToken cancellationToken)
    {
        if (await SituationArtifactIO.FileSha256Async(path, cancellationToken) != expected)
            throw new InvalidDataException("SHA-256 mismatch: " + Path.GetFileName(path));
    }
    internal static void RequireFiles(string root, IEnumerable<string> required)
    {
        if (Directory.GetDirectories(root).Length != 0 || !Directory.GetFiles(root).Select(Path.GetFileName).Order(StringComparer.Ordinal).SequenceEqual(required.Order(StringComparer.Ordinal)))
            throw new InvalidDataException("Artifact contains missing or extra files/directories.");
        if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0 || Directory.GetFiles(root).Any(p => (File.GetAttributes(p) & FileAttributes.ReparsePoint) != 0))
            throw new InvalidDataException("Artifact symbolic links are not permitted.");
    }
    internal static async Task<string> ReadTextAsync(string path, CancellationToken cancellationToken)
    {
        var bytes = await File.ReadAllBytesAsync(path, cancellationToken);
        if (bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble)) throw new InvalidDataException("UTF-8 BOM is not permitted.");
        return StrictUtf8.GetString(bytes);
    }
    internal static async Task<T> ReadJsonAsync<T>(string path, CancellationToken cancellationToken)
    {
        var json = await ReadTextAsync(path, cancellationToken);
        SituationTrainingContractJson.RejectDuplicateProperties(json, typeof(T).Name);
        SituationTrainingContractJson.RequireCompleteShape<T>(json, typeof(T).Name);
        return JsonSerializer.Deserialize<T>(json, SituationReviewSelectionPolicy.StrictJson) ?? throw new InvalidDataException("Empty JSON document.");
    }
    internal static async IAsyncEnumerable<string> ReadLinesAsync(string path, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length == 0) yield break;
        stream.Seek(-1, SeekOrigin.End);
        if (stream.ReadByte() != '\n') throw new InvalidDataException("JSONL requires trailing LF.");
        stream.Seek(0, SeekOrigin.Begin);
        using var checkedStream = new RejectCarriageReturnStream(stream);
        using var reader = new StreamReader(checkedStream, StrictUtf8, false, 1024 * 1024, true);
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (line.Length == 0 || line.StartsWith('\uFEFF') || line.Contains('\r')) throw new InvalidDataException("Invalid JSONL line.");
            yield return line;
        }
    }

    private sealed class RejectCarriageReturnStream(Stream inner) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = inner.Read(buffer, offset, count);
            Check(buffer.AsSpan(offset, read));
            return read;
        }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var read = await inner.ReadAsync(buffer, cancellationToken);
            Check(buffer.Span[..read]);
            return read;
        }
        private static void Check(ReadOnlySpan<byte> bytes)
        {
            if (bytes.Contains((byte)'\r')) throw new InvalidDataException("JSONL contains a carriage return.");
        }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

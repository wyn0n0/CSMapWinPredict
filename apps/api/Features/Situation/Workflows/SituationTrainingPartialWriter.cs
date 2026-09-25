using System.Text;
using CsDemoMap.Api.Models;

namespace CsDemoMap.Api.Services;

internal sealed class SituationTrainingPartialWriter : IAsyncDisposable
{
    private static readonly UTF8Encoding Utf8NoBom = new(false, true);
    private const string ManifestRelativePath = "manifest.json";
    private const string CheckpointRelativePath = ".partial/checkpoint.json";
    private const string LockRelativePath = ".partial/writer.lock";
    private const string CurrentRelativePath = ".partial/current";
    private const string MergeCurrentRelativePath = ".partial/merge/current";
    private const string MergePreparedRelativePath = ".partial/merge/prepared";

    private readonly string _root;
    private readonly string _repositoryRoot;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private FileStream? _lock;
    private StreamWriter? _recordWriter;
    private SituationTrainingMatchTransactionV1? _current;
    private SituationTrainingCheckpointV1 _checkpoint;
    private bool _disposed;
    private bool _workspaceCleaned;

    private SituationTrainingPartialWriter(
        string root,
        string repositoryRoot,
        FileStream writerLock,
        SituationTrainingCheckpointV1 checkpoint)
    {
        _root = root;
        _repositoryRoot = repositoryRoot;
        _lock = writerLock;
        _checkpoint = checkpoint;
    }

    internal SituationTrainingWriterPhase Phase => _checkpoint.Phase;
    internal int NextOrdinal => _checkpoint.NextOrdinal;
    internal IReadOnlyList<SituationTrainingCompletedMatchV1> CompletedMatches =>
        _checkpoint.CompletedMatches;
    internal IReadOnlyDictionary<string, long> SplitRows => _checkpoint.SplitRows;

    internal static string ComputeSamplePlanSha256(
        string samplePlanVersion,
        IReadOnlyList<SituationTrainingPlannedMatchV1> matches) =>
        SituationTrainingResumeValidator.ComputeSamplePlanSha256(samplePlanVersion, matches);

    internal static SituationTrainingStatisticsSnapshotV1 CreateStatisticsSnapshot<T>(T value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var canonical = SituationCanonicalJson.Serialize(value);
        var snapshot = new SituationTrainingStatisticsSnapshotV1(
            SituationTrainingCheckpointVersions.Statistics,
            canonical,
            SituationArtifactIO.Sha256(canonical));
        SituationTrainingResumeValidator.ValidateStatistics(snapshot);
        return snapshot;
    }

    internal static async Task<SituationTrainingPartialWriter> CreateNewAsync(
        string outputDirectory,
        string repositoryRoot,
        SituationTrainingWriterBindingV1 binding,
        SituationTrainingDatasetManifestV1 incompleteManifest,
        CancellationToken cancellationToken)
    {
        var root = NormalizeOutput(outputDirectory);
        var repository = NormalizeExistingDirectory(repositoryRoot, "Repository root");
        SituationArtifactIO.EnsureNewOutput(root);
        await SituationTrainingResumeValidator.ValidateRuntimeBindingAsync(
            repository, binding, cancellationToken);
        SituationTrainingResumeValidator.ValidateManifestBinding(
            incompleteManifest, binding, requireIncomplete: true);

        Directory.CreateDirectory(root);
        Directory.CreateDirectory(ToPath(root, ".partial/spool/train"));
        Directory.CreateDirectory(ToPath(root, ".partial/spool/dev"));
        Directory.CreateDirectory(ToPath(root, ".partial/spool/test"));
        Directory.CreateDirectory(ToPath(root, ".partial/diagnostics/matches"));
        Directory.CreateDirectory(ToPath(root, ".partial/diagnostics/merges"));
        Directory.CreateDirectory(ToPath(root, ".partial/merge"));

        var writerLock = OpenLock(root, createNew: true);
        try
        {
            var manifestPath = ToPath(root, ManifestRelativePath);
            await SituationArtifactIO.WriteJsonAsync(manifestPath, incompleteManifest, cancellationToken);
            var manifestSha256 = await SituationArtifactIO.FileSha256Async(manifestPath, cancellationToken);
            var checkpoint = new SituationTrainingCheckpointV1(
                SituationTrainingCheckpointVersions.Checkpoint,
                SituationArtifactStatus.Incomplete,
                SituationTrainingWriterPhase.Writing,
                binding,
                SituationTrainingResumeValidator.ComputeBindingSha256(binding),
                manifestSha256,
                0,
                1,
                1,
                EmptySplitRows(),
                [],
                []);
            await WriteCheckpointAsync(root, checkpoint, cancellationToken);
            await SituationTrainingResumeValidator.ValidateRuntimeBindingAsync(
                repository, binding, cancellationToken);
            await ValidateLayoutAsync(root, checkpoint, allowCurrent: false, cancellationToken);
            return new(root, repository, writerLock, checkpoint);
        }
        catch
        {
            await writerLock.DisposeAsync();
            throw;
        }
    }

    internal static async Task<SituationTrainingPartialWriter> ResumeAsync(
        string outputDirectory,
        string repositoryRoot,
        SituationTrainingWriterBindingV1 expectedBinding,
        CancellationToken cancellationToken)
    {
        var root = NormalizeExistingDirectory(outputDirectory, "Training output");
        var repository = NormalizeExistingDirectory(repositoryRoot, "Repository root");
        ValidateNoReparsePoint(new DirectoryInfo(root), "Training output");
        var initialManifest = SituationTrainingContractJson.DeserializeManifest(
            await ReadTextStrictAsync(ToPath(root, ManifestRelativePath), "Training manifest", cancellationToken));
        if (initialManifest.Status != SituationArtifactStatus.Incomplete)
            throw new InvalidDataException("Only an incomplete training output can be resumed.");
        await SituationTrainingResumeValidator.ValidateRuntimeBindingAsync(repository, expectedBinding, cancellationToken);
        var writerLock = OpenLock(root);
        try
        {
            RestoreInterruptedCleanup(root);
            var manifestPath = ToPath(root, ManifestRelativePath);
            var manifestJson = await ReadTextStrictAsync(manifestPath, "Incomplete manifest", cancellationToken);
            var manifest = SituationTrainingContractJson.DeserializeManifest(manifestJson);
            if (manifest.Status != SituationArtifactStatus.Incomplete)
                throw new InvalidDataException("Only an incomplete training output can be resumed.");
            SituationTrainingResumeValidator.ValidateManifestBinding(
                manifest, expectedBinding, requireIncomplete: true);
            await SituationTrainingResumeValidator.ValidateRuntimeBindingAsync(
                repository, expectedBinding, cancellationToken);

            var checkpointPath = ToPath(root, CheckpointRelativePath);
            var checkpoint = SituationTrainingResumeValidator.Deserialize<SituationTrainingCheckpointV1>(
                await ReadTextStrictAsync(checkpointPath, "Training checkpoint", cancellationToken),
                "Training checkpoint");
            var manifestSha256 = await SituationArtifactIO.FileSha256Async(manifestPath, cancellationToken);
            SituationTrainingResumeValidator.ValidateCheckpoint(
                checkpoint, expectedBinding, manifestSha256);
            await ValidateLayoutAsync(root, checkpoint, allowCurrent: true, cancellationToken);

            checkpoint = await RecoverInterruptedStateAsync(root, checkpoint, cancellationToken);
            SituationTrainingResumeValidator.ValidateCheckpoint(
                checkpoint, expectedBinding, manifestSha256);
            await ValidateLayoutAsync(root, checkpoint, allowCurrent: false, cancellationToken);
            return new(root, repository, writerLock, checkpoint);
        }
        catch
        {
            await writerLock.DisposeAsync();
            throw;
        }
    }

    internal async Task BeginMatchAsync(
        SituationTrainingPlannedMatchV1 match,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            ThrowIfUnavailable();
            if (_checkpoint.Phase != SituationTrainingWriterPhase.Writing || _current is not null ||
                _recordWriter is not null)
                throw new InvalidOperationException("Training writer cannot begin a match in its current state.");
            if (_checkpoint.NextOrdinal >= _checkpoint.Binding.Matches.Count ||
                match != _checkpoint.Binding.Matches[_checkpoint.NextOrdinal])
                throw new InvalidDataException("Training match does not equal the next stable sample-plan member.");

            var attempt = _checkpoint.NextAttempt;
            _checkpoint = _checkpoint with { NextAttempt = checked(attempt + 1) };
            await PersistCheckpointAsync(cancellationToken);

            var currentPath = ToPath(_root, CurrentRelativePath);
            if (Directory.Exists(currentPath) || File.Exists(currentPath))
                throw new InvalidDataException("An uncommitted training match already exists on disk.");
            Directory.CreateDirectory(currentPath);
            var transaction = new SituationTrainingMatchTransactionV1(
                SituationTrainingCheckpointVersions.Transaction,
                _checkpoint.BindingSha256,
                match.Ordinal,
                attempt,
                match.MatchRef,
                match.Split);
            await WriteJsonStrictAtomicAsync(
                Path.Combine(currentPath, "transaction.json"), transaction, cancellationToken);
            var stream = new FileStream(
                Path.Combine(currentPath, "records.jsonl.partial"),
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.Read,
                1024 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            _recordWriter = new StreamWriter(stream, Utf8NoBom) { NewLine = "\n" };
            _current = transaction;
        }
        finally
        {
            _gate.Release();
        }
    }

    internal async ValueTask WriteRecordAsync(
        SituationTrainingRecordV1 record,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            ThrowIfUnavailable();
            if (_current is null || _recordWriter is null)
                throw new InvalidOperationException("No training match transaction is active.");
            ValidateRecordIdentity(record, _current.MatchRef, _current.Split);
            var line = SituationTrainingContractJson.SerializeLine(record);
            await _recordWriter.WriteLineAsync(line.AsMemory(), cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    internal async Task CommitMatchAsync(
        SituationTrainingStatisticsSnapshotV1 statistics,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            ThrowIfUnavailable();
            if (_current is null || _recordWriter is null)
                throw new InvalidOperationException("No training match transaction is active.");
            SituationTrainingResumeValidator.ValidateStatistics(statistics);
            await CloseCurrentWriterAsync(cancellationToken);

            var transaction = _current;
            var partialPath = ToPath(_root, $"{CurrentRelativePath}/records.jsonl.partial");
            var validation = await ValidateRecordFileAsync(
                partialPath, transaction.Split, transaction.MatchRef, cancellationToken);
            var planned = _checkpoint.Binding.Matches[transaction.Ordinal];
            var spoolRelative = $"{SituationTrainingResumeValidator.SpoolDirectory(planned)}/records.jsonl";
            var spool = new SituationTrainingSpoolFileV1(
                spoolRelative,
                validation.Bytes,
                validation.Rows,
                validation.Sha256);
            var commit = new SituationTrainingMatchCommitV1(
                SituationTrainingCheckpointVersions.Commit,
                _checkpoint.BindingSha256,
                transaction.Ordinal,
                transaction.Attempt,
                transaction.MatchRef,
                transaction.Split,
                spool,
                statistics);
            await WriteJsonStrictAtomicAsync(
                ToPath(_root, $"{CurrentRelativePath}/commit.json"), commit, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            File.Move(
                partialPath,
                ToPath(_root, $"{CurrentRelativePath}/records.jsonl"),
                overwrite: false);
            var spoolDirectory = ToPath(_root, SituationTrainingResumeValidator.SpoolDirectory(planned));
            if (Directory.Exists(spoolDirectory) || File.Exists(spoolDirectory))
                throw new InvalidDataException("The immutable match spool already exists.");
            MoveDirectoryWithRetry(ToPath(_root, CurrentRelativePath), spoolDirectory);
            _current = null;
            _checkpoint = AddCommitted(_checkpoint, commit);
            await PersistCheckpointAsync(CancellationToken.None);
        }
        finally
        {
            _gate.Release();
        }
    }

    internal async Task AbandonCurrentMatchAsync(string reason, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            ThrowIfUnavailable();
            if (_current is null)
                throw new InvalidOperationException("No training match transaction is active.");
            await CloseCurrentWriterAsync(cancellationToken);
            await PreserveCurrentForDiagnosticsAsync(_root, _current, reason, cancellationToken);
            _current = null;
        }
        finally
        {
            _gate.Release();
        }
    }

    internal async Task<IReadOnlyList<SituationTrainingMergedFileV1>> MergeAsync(
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            ThrowIfUnavailable();
            if (_current is not null || _recordWriter is not null ||
                _checkpoint.Phase != SituationTrainingWriterPhase.Writing ||
                _checkpoint.NextOrdinal != _checkpoint.Binding.Matches.Count)
                throw new InvalidOperationException("All planned matches must be committed before stable merge.");
            await SituationTrainingResumeValidator.ValidateRuntimeBindingAsync(
                _repositoryRoot, _checkpoint.Binding, cancellationToken);

            var attempt = _checkpoint.NextMergeAttempt;
            _checkpoint = _checkpoint with { NextMergeAttempt = checked(attempt + 1) };
            await PersistCheckpointAsync(cancellationToken);
            var currentDirectory = ToPath(_root, MergeCurrentRelativePath);
            var preparedDirectory = ToPath(_root, MergePreparedRelativePath);
            if (Directory.Exists(currentDirectory) || Directory.Exists(preparedDirectory))
                throw new InvalidDataException("A merge workspace already exists.");
            Directory.CreateDirectory(currentDirectory);

            var merged = new List<SituationTrainingMergedFileV1>();
            foreach (var split in StableSplits())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var splitName = SituationTrainingResumeValidator.SplitName(split);
                var path = Path.Combine(currentDirectory, $"{splitName}.jsonl.partial");
                await using (var output = new FileStream(
                                 path, FileMode.CreateNew, FileAccess.Write, FileShare.Read,
                                 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
                {
                    foreach (var completed in _checkpoint.CompletedMatches.Where(item => item.Split == split))
                    {
                        await using var input = new FileStream(
                            ToPath(_root, completed.Spool.Path), FileMode.Open, FileAccess.Read, FileShare.Read,
                            1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                        await input.CopyToAsync(output, 1024 * 1024, cancellationToken);
                    }
                    await output.FlushAsync(cancellationToken);
                }
                var validation = await ValidateRecordFileAsync(path, split, null, cancellationToken);
                merged.Add(new(
                    split,
                    $"{MergePreparedRelativePath}/{splitName}.jsonl.partial",
                    $"{splitName}.jsonl",
                    validation.Bytes,
                    validation.Rows,
                    validation.Sha256));
            }
            var merge = new SituationTrainingMergeV1(
                SituationTrainingCheckpointVersions.Merge,
                _checkpoint.BindingSha256,
                attempt,
                merged);
            await WriteJsonStrictAtomicAsync(
                Path.Combine(currentDirectory, "merge.json"), merge, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            MoveDirectoryWithRetry(currentDirectory, preparedDirectory);
            _checkpoint = _checkpoint with
            {
                Phase = SituationTrainingWriterPhase.Merged,
                MergedFiles = merged
            };
            await PersistCheckpointAsync(CancellationToken.None);
            return merged;
        }
        finally
        {
            _gate.Release();
        }
    }

    internal async Task PublishAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            ThrowIfUnavailable();
            if (_current is not null || _recordWriter is not null ||
                _checkpoint.Phase is not (SituationTrainingWriterPhase.Merged or SituationTrainingWriterPhase.Published))
                throw new InvalidOperationException("Training JSONL files must be merged before publication.");
            await SituationTrainingResumeValidator.ValidateRuntimeBindingAsync(
                _repositoryRoot, _checkpoint.Binding, cancellationToken);

            foreach (var merged in _checkpoint.MergedFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var partial = ToPath(_root, merged.PartialPath);
                var final = ToPath(_root, merged.FinalPath);
                if (File.Exists(partial) == File.Exists(final))
                    throw new InvalidDataException(
                        $"Exactly one merged or published file must exist for {merged.FinalPath}.");
                var source = File.Exists(final) ? final : partial;
                await ValidateMergedMetadataAsync(source, merged, cancellationToken);
            }
            cancellationToken.ThrowIfCancellationRequested();

            foreach (var merged in _checkpoint.MergedFiles)
            {
                var partial = ToPath(_root, merged.PartialPath);
                var final = ToPath(_root, merged.FinalPath);
                if (File.Exists(partial))
                    File.Move(partial, final, overwrite: false);
                await ValidateMergedMetadataAsync(final, merged, CancellationToken.None);
            }
            _checkpoint = _checkpoint with { Phase = SituationTrainingWriterPhase.Published };
            await PersistCheckpointAsync(CancellationToken.None);
        }
        finally
        {
            _gate.Release();
        }
    }

    internal async Task CleanupPublishedWorkspaceAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            ThrowIfUnavailable();
            if (_checkpoint.Phase != SituationTrainingWriterPhase.Published || _current is not null ||
                _recordWriter is not null)
                throw new InvalidOperationException("Only a fully published writer workspace can be cleaned.");
            await ValidateLayoutAsync(_root, _checkpoint, allowCurrent: false, cancellationToken);
            foreach (var merged in _checkpoint.MergedFiles)
                await ValidateMergedMetadataAsync(
                    ToPath(_root, merged.FinalPath), merged, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            var partialRoot = ToPath(_root, ".partial");
            var cleanupRoot = _root + ".writer-recovery";
            EnsureOwnedPartialWorkspace(partialRoot);
            if (Directory.Exists(cleanupRoot) || File.Exists(cleanupRoot))
                throw new InvalidDataException("Writer cleanup staging path already exists.");
            MoveDirectoryWithRetry(partialRoot, cleanupRoot);
            // Keep the last checkpoint outside the complete artifact. A crash before
            // manifest publication can restore this exact workspace without reparsing.
            _workspaceCleaned = true;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (_disposed)
                return;
            if (_recordWriter is not null)
            {
                await _recordWriter.FlushAsync();
                await _recordWriter.DisposeAsync();
                _recordWriter = null;
            }
            if (_lock is not null)
            {
                await _lock.DisposeAsync();
                _lock = null;
            }
            _disposed = true;
        }
        finally
        {
            _gate.Release();
            _gate.Dispose();
        }
    }

    private async Task PersistCheckpointAsync(CancellationToken cancellationToken)
    {
        SituationTrainingResumeValidator.ValidateCheckpoint(
            _checkpoint,
            _checkpoint.Binding,
            _checkpoint.IncompleteManifestSha256);
        await WriteCheckpointAsync(_root, _checkpoint, cancellationToken);
    }

    private static async Task<SituationTrainingCheckpointV1> RecoverInterruptedStateAsync(
        string root,
        SituationTrainingCheckpointV1 checkpoint,
        CancellationToken cancellationToken)
    {
        var next = checkpoint.NextOrdinal < checkpoint.Binding.Matches.Count
            ? checkpoint.Binding.Matches[checkpoint.NextOrdinal]
            : null;
        if (next is not null)
        {
            var orphanDirectory = ToPath(root, SituationTrainingResumeValidator.SpoolDirectory(next));
            if (Directory.Exists(orphanDirectory))
            {
                var commit = await ReadCommitAsync(root, orphanDirectory, checkpoint, cancellationToken);
                checkpoint = AddCommitted(checkpoint, commit);
                await WriteCheckpointAsync(root, checkpoint, CancellationToken.None);
            }
        }

        var currentDirectory = ToPath(root, CurrentRelativePath);
        if (Directory.Exists(currentDirectory))
        {
            var transaction = await ReadTransactionAsync(currentDirectory, checkpoint, cancellationToken);
            var commitPath = Path.Combine(currentDirectory, "commit.json");
            if (File.Exists(commitPath))
            {
                var commit = SituationTrainingResumeValidator.Deserialize<SituationTrainingMatchCommitV1>(
                    await ReadTextStrictAsync(commitPath, "Training match commit", cancellationToken),
                    "Training match commit");
                ValidateCommitIdentity(commit, transaction, checkpoint);
                var preparedPlan = checkpoint.Binding.Matches[transaction.Ordinal];
                if (commit.Spool.Path !=
                    $"{SituationTrainingResumeValidator.SpoolDirectory(preparedPlan)}/records.jsonl")
                    throw new InvalidDataException("Prepared match spool path is invalid.");
                var partial = Path.Combine(currentDirectory, "records.jsonl.partial");
                var records = Path.Combine(currentDirectory, "records.jsonl");
                if (File.Exists(partial) == File.Exists(records))
                    throw new InvalidDataException("Prepared match transaction has an invalid record file set.");
                var source = File.Exists(records) ? records : partial;
                await ValidateSpoolMetadataAsync(source, commit.Spool, commit.Split, commit.MatchRef, cancellationToken);
                if (File.Exists(partial))
                    File.Move(partial, records, overwrite: false);
                var planned = checkpoint.Binding.Matches[checkpoint.NextOrdinal];
                var spoolDirectory = ToPath(root, SituationTrainingResumeValidator.SpoolDirectory(planned));
                if (Directory.Exists(spoolDirectory))
                    throw new InvalidDataException("Prepared match conflicts with an existing immutable spool.");
                MoveDirectoryWithRetry(currentDirectory, spoolDirectory);
                checkpoint = AddCommitted(checkpoint, commit);
                await WriteCheckpointAsync(root, checkpoint, CancellationToken.None);
            }
            else
            {
                await ValidateActiveFilesAsync(currentDirectory, transaction, cancellationToken);
                await PreserveCurrentForDiagnosticsAsync(
                    root, transaction, "resume-uncommitted", CancellationToken.None);
            }
        }

        var mergeCurrent = ToPath(root, MergeCurrentRelativePath);
        if (Directory.Exists(mergeCurrent))
            await PreserveMergeForDiagnosticsAsync(root, checkpoint, cancellationToken);
        var mergePrepared = ToPath(root, MergePreparedRelativePath);
        if (Directory.Exists(mergePrepared) && checkpoint.Phase == SituationTrainingWriterPhase.Writing)
        {
            if (checkpoint.NextOrdinal != checkpoint.Binding.Matches.Count)
                throw new InvalidDataException("Prepared merge exists before all matches were committed.");
            var merge = await ReadMergeAsync(mergePrepared, checkpoint, cancellationToken);
            checkpoint = checkpoint with
            {
                Phase = SituationTrainingWriterPhase.Merged,
                MergedFiles = merge.Files
            };
            await WriteCheckpointAsync(root, checkpoint, CancellationToken.None);
        }
        return checkpoint;
    }

    private static SituationTrainingCheckpointV1 AddCommitted(
        SituationTrainingCheckpointV1 checkpoint,
        SituationTrainingMatchCommitV1 commit)
    {
        if (checkpoint.Phase != SituationTrainingWriterPhase.Writing ||
            commit.Ordinal != checkpoint.NextOrdinal ||
            commit.Ordinal >= checkpoint.Binding.Matches.Count)
            throw new InvalidDataException("Training match commit skips or repeats stable plan order.");
        var planned = checkpoint.Binding.Matches[commit.Ordinal];
        if (commit.MatchRef != planned.MatchRef || commit.Split != planned.Split ||
            commit.BindingSha256 != checkpoint.BindingSha256 ||
            commit.SchemaVersion != SituationTrainingCheckpointVersions.Commit)
            throw new InvalidDataException("Training match commit does not match its planned transaction.");
        SituationTrainingResumeValidator.ValidateStatistics(commit.Statistics);
        var rows = checkpoint.SplitRows.ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal);
        rows[SituationTrainingResumeValidator.SplitName(commit.Split)] = checked(
            rows[SituationTrainingResumeValidator.SplitName(commit.Split)] + commit.Spool.Rows);
        return checkpoint with
        {
            NextOrdinal = checked(checkpoint.NextOrdinal + 1),
            SplitRows = rows,
            CompletedMatches = checkpoint.CompletedMatches.Append(new(
                commit.Ordinal,
                commit.MatchRef,
                commit.Split,
                commit.Spool,
                commit.Statistics)).ToArray()
        };
    }

    private static async Task<SituationTrainingMatchCommitV1> ReadCommitAsync(
        string root,
        string spoolDirectory,
        SituationTrainingCheckpointV1 checkpoint,
        CancellationToken cancellationToken)
    {
        var commitPath = Path.Combine(spoolDirectory, "commit.json");
        var commit = SituationTrainingResumeValidator.Deserialize<SituationTrainingMatchCommitV1>(
            await ReadTextStrictAsync(commitPath, "Training match commit", cancellationToken),
            "Training match commit");
        var transaction = SituationTrainingResumeValidator.Deserialize<SituationTrainingMatchTransactionV1>(
            await ReadTextStrictAsync(Path.Combine(spoolDirectory, "transaction.json"),
                "Training match transaction", cancellationToken),
            "Training match transaction");
        if (commit.Ordinal != checkpoint.NextOrdinal ||
            commit.Ordinal >= checkpoint.Binding.Matches.Count)
            throw new InvalidDataException("Orphan match spool jumps or repeats stable plan order.");
        var planned = checkpoint.Binding.Matches[commit.Ordinal];
        if (commit.SchemaVersion != SituationTrainingCheckpointVersions.Commit ||
            commit.BindingSha256 != checkpoint.BindingSha256 || commit.MatchRef != planned.MatchRef ||
            commit.Split != planned.Split ||
            commit.Spool.Path != $"{SituationTrainingResumeValidator.SpoolDirectory(planned)}/records.jsonl")
            throw new InvalidDataException("Orphan match spool commit metadata is invalid.");
        if (transaction.SchemaVersion != SituationTrainingCheckpointVersions.Transaction ||
            transaction.BindingSha256 != checkpoint.BindingSha256 ||
            transaction.Ordinal != commit.Ordinal || transaction.Attempt != commit.Attempt ||
            transaction.MatchRef != commit.MatchRef || transaction.Split != commit.Split)
            throw new InvalidDataException("Orphan match transaction metadata is invalid.");
        SituationTrainingResumeValidator.ValidateStatistics(commit.Statistics);
        await ValidateSpoolMetadataAsync(
            ToPath(root, commit.Spool.Path), commit.Spool, commit.Split, commit.MatchRef, cancellationToken);
        return commit;
    }

    private static async Task<SituationTrainingMatchTransactionV1> ReadTransactionAsync(
        string currentDirectory,
        SituationTrainingCheckpointV1 checkpoint,
        CancellationToken cancellationToken)
    {
        var transactionPath = Path.Combine(currentDirectory, "transaction.json");
        if (!File.Exists(transactionPath))
        {
            if (checkpoint.NextOrdinal >= checkpoint.Binding.Matches.Count || checkpoint.NextAttempt < 2 ||
                Directory.EnumerateFiles(currentDirectory).Any(path => Path.GetFileName(path) != "transaction.json.tmp") ||
                Directory.EnumerateDirectories(currentDirectory).Any())
                throw new InvalidDataException("Interrupted transaction initialization has unknown files or progress.");
            var initialPlan = checkpoint.Binding.Matches[checkpoint.NextOrdinal];
            var initialized = new SituationTrainingMatchTransactionV1(
                SituationTrainingCheckpointVersions.Transaction, checkpoint.BindingSha256,
                initialPlan.Ordinal, checkpoint.NextAttempt - 1, initialPlan.MatchRef, initialPlan.Split);
            await WriteJsonStrictAtomicAsync(transactionPath, initialized, cancellationToken);
            await using var empty = new FileStream(Path.Combine(currentDirectory, "records.jsonl.partial"),
                FileMode.CreateNew, FileAccess.Write);
        }
        var transaction = SituationTrainingResumeValidator.Deserialize<SituationTrainingMatchTransactionV1>(
            await ReadTextStrictAsync(
                Path.Combine(currentDirectory, "transaction.json"),
                "Training match transaction",
                cancellationToken),
            "Training match transaction");
        if (transaction.SchemaVersion != SituationTrainingCheckpointVersions.Transaction ||
            transaction.BindingSha256 != checkpoint.BindingSha256 ||
            transaction.Ordinal != checkpoint.NextOrdinal || transaction.Attempt < 1 ||
            transaction.Attempt >= checkpoint.NextAttempt ||
            transaction.Ordinal >= checkpoint.Binding.Matches.Count)
            throw new InvalidDataException("Active training match transaction is invalid or out of order.");
        var planned = checkpoint.Binding.Matches[transaction.Ordinal];
        if (transaction.MatchRef != planned.MatchRef || transaction.Split != planned.Split)
            throw new InvalidDataException("Active training match differs from the stable sample plan.");
        var partialPath = Path.Combine(currentDirectory, "records.jsonl.partial");
        if (!File.Exists(partialPath) && Directory.EnumerateFiles(currentDirectory)
                .All(path => Path.GetFileName(path) == "transaction.json") &&
            !Directory.EnumerateDirectories(currentDirectory).Any())
        {
            // Crash between publishing the transaction and opening its empty
            // stream: preserve this uncommitted attempt, then replay the match.
            await using var empty = new FileStream(partialPath, FileMode.CreateNew, FileAccess.Write);
        }
        return transaction;
    }

    private static void ValidateCommitIdentity(
        SituationTrainingMatchCommitV1 commit,
        SituationTrainingMatchTransactionV1 transaction,
        SituationTrainingCheckpointV1 checkpoint)
    {
        if (commit.SchemaVersion != SituationTrainingCheckpointVersions.Commit ||
            commit.BindingSha256 != checkpoint.BindingSha256 ||
            commit.Ordinal != transaction.Ordinal || commit.Attempt != transaction.Attempt ||
            commit.MatchRef != transaction.MatchRef || commit.Split != transaction.Split)
            throw new InvalidDataException("Prepared match commit differs from its transaction.");
        SituationTrainingResumeValidator.ValidateStatistics(commit.Statistics);
    }

    private static async Task<SituationTrainingMergeV1> ReadMergeAsync(
        string preparedDirectory,
        SituationTrainingCheckpointV1 checkpoint,
        CancellationToken cancellationToken)
    {
        var merge = SituationTrainingResumeValidator.Deserialize<SituationTrainingMergeV1>(
            await ReadTextStrictAsync(Path.Combine(preparedDirectory, "merge.json"),
                "Training merge", cancellationToken),
            "Training merge");
        if (merge.SchemaVersion != SituationTrainingCheckpointVersions.Merge ||
            merge.BindingSha256 != checkpoint.BindingSha256 || merge.Attempt < 1 ||
            merge.Attempt >= checkpoint.NextMergeAttempt || merge.Files.Count != 3 ||
            !merge.Files.Select(item => item.Split).SequenceEqual(StableSplits()))
            throw new InvalidDataException("Prepared training merge metadata is invalid.");
        foreach (var file in merge.Files)
            await ValidateMergedMetadataAsync(ToPath(preparedDirectory,
                Path.GetFileName(file.PartialPath)), file, cancellationToken);
        return merge;
    }

    private static async Task PreserveCurrentForDiagnosticsAsync(
        string root,
        SituationTrainingMatchTransactionV1 transaction,
        string reason,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(reason) || reason.Contains('\r') || reason.Contains('\n'))
            throw new ArgumentException("Training abandonment reason must be a non-empty single line.", nameof(reason));
        var current = ToPath(root, CurrentRelativePath);
        var relative = $".partial/diagnostics/matches/{transaction.Ordinal:D4}-{transaction.MatchRef}-{transaction.Attempt:D6}";
        var destination = ToPath(root, relative);
        if (Directory.Exists(destination) || File.Exists(destination))
            throw new InvalidDataException("Training match diagnostic destination already exists.");
        MoveDirectoryWithRetry(current, destination);
        await SituationArtifactIO.WriteTextAtomicAsync(
            Path.Combine(destination, "abandon.txt"), reason + "\n", cancellationToken);
    }

    private static async Task PreserveMergeForDiagnosticsAsync(
        string root,
        SituationTrainingCheckpointV1 checkpoint,
        CancellationToken cancellationToken)
    {
        var current = ToPath(root, MergeCurrentRelativePath);
        var mergePath = Path.Combine(current, "merge.json");
        var attempt = checkpoint.NextMergeAttempt - 1;
        if (File.Exists(mergePath))
        {
            var merge = SituationTrainingResumeValidator.Deserialize<SituationTrainingMergeV1>(
                await ReadTextStrictAsync(mergePath, "Interrupted training merge", cancellationToken),
                "Interrupted training merge");
            if (merge.BindingSha256 != checkpoint.BindingSha256 || merge.Attempt != attempt)
                throw new InvalidDataException("Interrupted merge metadata differs from the checkpoint.");
        }
        var destination = ToPath(root, $".partial/diagnostics/merges/{attempt:D6}");
        if (Directory.Exists(destination) || File.Exists(destination))
            throw new InvalidDataException("Training merge diagnostic destination already exists.");
        MoveDirectoryWithRetry(current, destination);
        await SituationArtifactIO.WriteTextAtomicAsync(
            Path.Combine(destination, "abandon.txt"), "resume-uncommitted\n", CancellationToken.None);
    }

    private static async Task ValidateActiveFilesAsync(
        string directory,
        SituationTrainingMatchTransactionV1 transaction,
        CancellationToken cancellationToken)
    {
        var expected = new HashSet<string>(StringComparer.Ordinal)
        {
            "records.jsonl.partial", "transaction.json"
        };
        var actual = Directory.GetFiles(directory, "*", SearchOption.TopDirectoryOnly)
            .Select(Path.GetFileName)
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (!expected.IsSubsetOf(actual.OfType<string>()) ||
            actual.Any(name => name is not ("transaction.json" or "records.jsonl.partial" or "commit.json.tmp")) ||
            Directory.GetDirectories(directory, "*", SearchOption.TopDirectoryOnly).Length != 0)
            throw new InvalidDataException("Active training match contains unknown or missing files.");
        // Uncommitted bytes can end anywhere after an abrupt process exit. They are
        // archived and never promoted; only committed spools require full validation.
        cancellationToken.ThrowIfCancellationRequested();
        await Task.CompletedTask;
    }

    private static async Task ValidateLayoutAsync(
        string root,
        SituationTrainingCheckpointV1 checkpoint,
        bool allowCurrent,
        CancellationToken cancellationToken)
    {
        var directories = EnumerateDirectoriesWithoutReparse(root).ToArray();
        foreach (var completed in checkpoint.CompletedMatches)
        {
            await ValidateSpoolMetadataAsync(
                ToPath(root, completed.Spool.Path), completed.Spool,
                completed.Split, completed.MatchRef, cancellationToken);
            await ValidateCommittedMetadataAsync(root, checkpoint, completed, cancellationToken);
        }

        var knownFiles = new HashSet<string>(StringComparer.Ordinal)
        {
            ManifestRelativePath,
            ManifestRelativePath + ".tmp",
            CheckpointRelativePath,
            CheckpointRelativePath + ".tmp",
            LockRelativePath
        };
        foreach (var allowed in checkpoint.Binding.AllowedArtifactPaths)
        {
            knownFiles.Add(allowed);
            knownFiles.Add(allowed + ".tmp");
        }
        foreach (var completed in checkpoint.CompletedMatches)
        {
            knownFiles.Add(completed.Spool.Path);
            knownFiles.Add($"{SituationTrainingResumeValidator.SpoolDirectory(
                new(completed.Ordinal, completed.MatchRef, completed.Split))}/commit.json");
            knownFiles.Add($"{SituationTrainingResumeValidator.SpoolDirectory(
                new(completed.Ordinal, completed.MatchRef, completed.Split))}/transaction.json");
        }
        if (checkpoint.NextOrdinal < checkpoint.Binding.Matches.Count)
        {
            var next = checkpoint.Binding.Matches[checkpoint.NextOrdinal];
            var orphan = SituationTrainingResumeValidator.SpoolDirectory(next);
            if (Directory.Exists(ToPath(root, orphan)))
            {
                knownFiles.Add($"{orphan}/records.jsonl");
                knownFiles.Add($"{orphan}/commit.json");
                knownFiles.Add($"{orphan}/transaction.json");
            }
        }
        if (allowCurrent && Directory.Exists(ToPath(root, CurrentRelativePath)))
        {
            knownFiles.Add($"{CurrentRelativePath}/transaction.json");
            knownFiles.Add($"{CurrentRelativePath}/transaction.json.tmp");
            knownFiles.Add($"{CurrentRelativePath}/records.jsonl.partial");
            knownFiles.Add($"{CurrentRelativePath}/records.jsonl");
            knownFiles.Add($"{CurrentRelativePath}/commit.json");
            knownFiles.Add($"{CurrentRelativePath}/commit.json.tmp");
        }
        foreach (var merged in checkpoint.MergedFiles)
        {
            knownFiles.Add(merged.PartialPath);
            knownFiles.Add(merged.FinalPath);
        }
        if (Directory.Exists(ToPath(root, MergePreparedRelativePath)))
        {
            knownFiles.Add($"{MergePreparedRelativePath}/merge.json");
            foreach (var split in StableSplits())
                knownFiles.Add($"{MergePreparedRelativePath}/{SituationTrainingResumeValidator.SplitName(split)}.jsonl.partial");
        }
        if (allowCurrent && Directory.Exists(ToPath(root, MergeCurrentRelativePath)))
        {
            knownFiles.Add($"{MergeCurrentRelativePath}/merge.json");
            foreach (var split in StableSplits())
                knownFiles.Add($"{MergeCurrentRelativePath}/{SituationTrainingResumeValidator.SplitName(split)}.jsonl.partial");
        }

        foreach (var file in Directory.GetFiles(root, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            ValidateNoReparsePoint(new FileInfo(file), "Training output file");
            var relative = Relative(root, file);
            if (relative.StartsWith(".partial/diagnostics/", StringComparison.Ordinal))
            {
                ValidateDiagnosticRelativePath(relative);
                continue;
            }
            if (!knownFiles.Contains(relative))
                throw new InvalidDataException($"Training output contains an unknown or unexpected file: {relative}");
        }

        var knownDirectories = new HashSet<string>(StringComparer.Ordinal)
        {
            ".partial",
            ".partial/spool",
            ".partial/spool/train",
            ".partial/spool/dev",
            ".partial/spool/test",
            ".partial/diagnostics",
            ".partial/diagnostics/matches",
            ".partial/diagnostics/merges",
            ".partial/merge"
        };
        foreach (var file in knownFiles)
        {
            var parent = Path.GetDirectoryName(file.Replace('/', Path.DirectorySeparatorChar));
            while (!string.IsNullOrEmpty(parent))
            {
                knownDirectories.Add(parent.Replace('\\', '/'));
                parent = Path.GetDirectoryName(parent);
            }
        }
        foreach (var directory in directories.Order(StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = Relative(root, directory);
            if (relative.StartsWith(".partial/diagnostics/matches/", StringComparison.Ordinal) ||
                relative.StartsWith(".partial/diagnostics/merges/", StringComparison.Ordinal))
            {
                ValidateDiagnosticDirectory(relative);
                continue;
            }
            if (!knownDirectories.Contains(relative))
                throw new InvalidDataException($"Training output contains an unknown directory: {relative}");
        }
    }

    private static void ValidateDiagnosticRelativePath(string relative)
    {
        var parts = relative.Split('/');
        if (parts.Length != 5 || parts[0] != ".partial" || parts[1] != "diagnostics" ||
            parts[2] is not ("matches" or "merges") ||
            parts[4] is not ("transaction.json" or "records.jsonl.partial" or "records.jsonl" or
                "commit.json" or "commit.json.tmp" or "merge.json" or "train.jsonl.partial" or "dev.jsonl.partial" or
                "test.jsonl.partial" or "abandon.txt"))
            throw new InvalidDataException($"Training diagnostics contain an unknown file: {relative}");
        if (parts[2] == "matches" && !IsMatchDiagnosticDirectory(parts[3]) ||
            parts[2] == "merges" && (parts[3].Length != 6 || parts[3].Any(character => !char.IsAsciiDigit(character))))
            throw new InvalidDataException($"Training diagnostic directory name is invalid: {relative}");
    }

    private static void ValidateDiagnosticDirectory(string relative)
    {
        var parts = relative.Split('/');
        if (parts.Length != 4 || parts[0] != ".partial" || parts[1] != "diagnostics" ||
            parts[2] is not ("matches" or "merges") ||
            parts[2] == "matches" && !IsMatchDiagnosticDirectory(parts[3]) ||
            parts[2] == "merges" && (parts[3].Length != 6 || parts[3].Any(character => !char.IsAsciiDigit(character))))
            throw new InvalidDataException($"Training diagnostic directory is invalid: {relative}");
    }

    private static bool IsMatchDiagnosticDirectory(string name)
    {
        if (name.Length != 4 + 1 + 70 + 1 + 6 || name[4] != '-' || name[75] != '-' ||
            name[..4].Any(character => !char.IsAsciiDigit(character)) ||
            name[76..].Any(character => !char.IsAsciiDigit(character)))
            return false;
        return SituationTrainingResumeValidator.IsMatchRef(name.Substring(5, 70));
    }

    private static async Task ValidateSpoolMetadataAsync(
        string path,
        SituationTrainingSpoolFileV1 expected,
        SituationTrainingSplit split,
        string matchRef,
        CancellationToken cancellationToken)
    {
        var actual = await ValidateRecordFileAsync(path, split, matchRef, cancellationToken);
        if (actual.Bytes != expected.Bytes || actual.Rows != expected.Rows || actual.Sha256 != expected.Sha256)
            throw new InvalidDataException($"Training spool bytes, rows, or SHA-256 changed: {expected.Path}");
    }

    private static async Task ValidateCommittedMetadataAsync(
        string root,
        SituationTrainingCheckpointV1 checkpoint,
        SituationTrainingCompletedMatchV1 completed,
        CancellationToken cancellationToken)
    {
        var planned = new SituationTrainingPlannedMatchV1(
            completed.Ordinal, completed.MatchRef, completed.Split);
        var directory = ToPath(root, SituationTrainingResumeValidator.SpoolDirectory(planned));
        var transaction = SituationTrainingResumeValidator.Deserialize<SituationTrainingMatchTransactionV1>(
            await ReadTextStrictAsync(Path.Combine(directory, "transaction.json"),
                "Committed match transaction", cancellationToken),
            "Committed match transaction");
        var commit = SituationTrainingResumeValidator.Deserialize<SituationTrainingMatchCommitV1>(
            await ReadTextStrictAsync(Path.Combine(directory, "commit.json"),
                "Committed match commit", cancellationToken),
            "Committed match commit");
        if (transaction.SchemaVersion != SituationTrainingCheckpointVersions.Transaction ||
            transaction.BindingSha256 != checkpoint.BindingSha256 ||
            transaction.Ordinal != completed.Ordinal || transaction.MatchRef != completed.MatchRef ||
            transaction.Split != completed.Split || transaction.Attempt < 1 ||
            commit.SchemaVersion != SituationTrainingCheckpointVersions.Commit ||
            commit.BindingSha256 != checkpoint.BindingSha256 || commit.Ordinal != completed.Ordinal ||
            commit.Attempt != transaction.Attempt || commit.MatchRef != completed.MatchRef ||
            commit.Split != completed.Split || commit.Spool != completed.Spool ||
            commit.Statistics != completed.Statistics)
            throw new InvalidDataException("Committed match transaction metadata differs from its checkpoint.");
    }

    private static async Task ValidateMergedMetadataAsync(
        string path,
        SituationTrainingMergedFileV1 expected,
        CancellationToken cancellationToken)
    {
        var actual = await ValidateRecordFileAsync(path, expected.Split, null, cancellationToken);
        if (actual.Bytes != expected.Bytes || actual.Rows != expected.Rows || actual.Sha256 != expected.Sha256)
            throw new InvalidDataException($"Merged training JSONL bytes, rows, or SHA-256 changed: {expected.FinalPath}");
    }

    private static async Task<(long Bytes, long Rows, string Sha256)> ValidateRecordFileAsync(
        string path,
        SituationTrainingSplit split,
        string? matchRef,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException("Training JSONL file is missing.", path);
        var info = new FileInfo(path);
        if (info.Length > 0)
        {
            await using var tail = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            tail.Seek(-1, SeekOrigin.End);
            if (tail.ReadByte() != (byte)'\n')
                throw new InvalidDataException("Training JSONL has a truncated final line.");
        }
        long rows = 0;
        (string MatchRef, string RoundRef, int Tick, string SampleId)? previous = null;
        using var reader = new StreamReader(path, Utf8NoBom, detectEncodingFromByteOrderMarks: true);
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (line.Length == 0 || line.Contains('\r') || line.StartsWith('\uFEFF'))
                throw new InvalidDataException("Training JSONL contains an empty, CR, or BOM-prefixed line.");
            var record = SituationTrainingContractJson.DeserializeRecord(line);
            ValidateRecordIdentity(record, matchRef, split);
            var key = (record.Metadata.MatchRef, record.Metadata.RoundRef,
                record.Metadata.Tick, record.SampleId);
            if (previous is { } old && CompareKeys(old, key) >= 0)
                throw new InvalidDataException("Training JSONL is not strictly ordered or contains a duplicate sample.");
            previous = key;
            rows++;
        }
        return (info.Length, rows, await SituationArtifactIO.FileSha256Async(path, cancellationToken));
    }

    private static void ValidateRecordIdentity(
        SituationTrainingRecordV1 record,
        string? matchRef,
        SituationTrainingSplit split)
    {
        SituationTrainingContractJson.Validate(record);
        if (record.Metadata.Split != split ||
            matchRef is not null && record.Metadata.MatchRef != matchRef)
            throw new InvalidDataException("Training record does not belong to its match transaction and split.");
    }

    private static int CompareKeys(
        (string MatchRef, string RoundRef, int Tick, string SampleId) left,
        (string MatchRef, string RoundRef, int Tick, string SampleId) right)
    {
        var result = StringComparer.Ordinal.Compare(left.MatchRef, right.MatchRef);
        if (result != 0) return result;
        result = StringComparer.Ordinal.Compare(left.RoundRef, right.RoundRef);
        if (result != 0) return result;
        result = left.Tick.CompareTo(right.Tick);
        return result != 0 ? result : StringComparer.Ordinal.Compare(left.SampleId, right.SampleId);
    }

    private static void EnsureOwnedPartialWorkspace(string partialRoot)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(partialRoot));
        if (!Directory.Exists(root) || Path.GetFileName(root) != ".partial")
            throw new InvalidDataException("Writer cleanup target is not the owned .partial workspace.");
        foreach (var entry in Directory.EnumerateFileSystemEntries(root, "*", SearchOption.TopDirectoryOnly))
        {
            var name = Path.GetFileName(entry);
            if (name is not ("checkpoint.json" or "checkpoint.json.tmp" or "writer.lock" or "spool" or "diagnostics" or "merge"))
                throw new InvalidDataException($"Writer cleanup refused unknown .partial entry: {name}");
        }
    }

    private static IEnumerable<string> EnumerateDirectoriesWithoutReparse(string root)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var parent = pending.Pop();
            foreach (var directory in Directory.GetDirectories(parent, "*", SearchOption.TopDirectoryOnly)
                         .OrderDescending(StringComparer.Ordinal))
            {
                var info = new DirectoryInfo(directory);
                ValidateNoReparsePoint(info, "Training output directory");
                yield return directory;
                pending.Push(directory);
            }
        }
    }

    private static void RestoreInterruptedCleanup(string root)
    {
        var partial = ToPath(root, ".partial");
        var cleanup = root + ".writer-recovery";
        if (!Directory.Exists(cleanup))
            return;
        ValidateNoReparsePoint(new DirectoryInfo(cleanup), "Writer recovery directory");
        if (Directory.Exists(partial) || File.Exists(partial) || File.Exists(cleanup))
            throw new InvalidDataException("Training output contains conflicting cleanup workspaces.");
        MoveDirectoryWithRetry(cleanup, partial);
    }

    private static void MoveDirectoryWithRetry(string source, string destination)
    {
        // Windows indexers can briefly deny rename after the JSONL handle closes.
        // Retry only the same no-overwrite atomic operation; permanent errors fail closed.
        for (var attempt = 0; ; attempt++)
        {
            try { Directory.Move(source, destination); return; }
            catch (IOException error) when (attempt < 20 &&
                (error.HResult & 0xffff) is 5 or 32 or 33 &&
                Directory.Exists(source) && !Directory.Exists(destination) && !File.Exists(destination))
            {
                Thread.Sleep(100);
            }
        }
    }

    private async Task CloseCurrentWriterAsync(CancellationToken cancellationToken)
    {
        if (_recordWriter is null)
            return;
        await _recordWriter.FlushAsync(cancellationToken);
        await _recordWriter.DisposeAsync();
        _recordWriter = null;
    }

    private void ThrowIfUnavailable()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_workspaceCleaned)
            throw new InvalidOperationException("Training writer workspace was already cleaned.");
    }

    private static IReadOnlyDictionary<string, long> EmptySplitRows() =>
        new Dictionary<string, long>(StringComparer.Ordinal)
        {
            ["dev"] = 0,
            ["test"] = 0,
            ["train"] = 0
        };

    private static SituationTrainingSplit[] StableSplits() =>
        [SituationTrainingSplit.Train, SituationTrainingSplit.Dev, SituationTrainingSplit.Test];

    private static FileStream OpenLock(string root, bool createNew = false)
    {
        // Adjacent lock survives movement of the writer workspace and stays held
        // through final manifest publication. It is not a dataset artifact.
        var path = root + ".writer.lock";
        if (File.Exists(path)) ValidateNoReparsePoint(new FileInfo(path), "Writer lock");
        try
        {
            return new FileStream(path, createNew ? FileMode.CreateNew : FileMode.OpenOrCreate,
                FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException exception)
        {
            throw new InvalidOperationException("Training output is already open by another writer.", exception);
        }
    }

    private static async Task WriteCheckpointAsync(
        string root,
        SituationTrainingCheckpointV1 checkpoint,
        CancellationToken cancellationToken) =>
        await WriteJsonStrictAtomicAsync(ToPath(root, CheckpointRelativePath), checkpoint, cancellationToken);

    private static async Task WriteJsonStrictAtomicAsync<T>(
        string path,
        T value,
        CancellationToken cancellationToken)
    {
        var text = SituationTrainingResumeValidator.Serialize(value);
        await SituationArtifactIO.WriteTextAtomicAsync(path, text, cancellationToken);
    }

    private static async Task<string> ReadTextStrictAsync(
        string path,
        string description,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException($"{description} is missing.", path);
        var bytes = await File.ReadAllBytesAsync(path, cancellationToken);
        if (bytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }))
            throw new InvalidDataException($"{description} must not contain a UTF-8 BOM.");
        try
        {
            return Utf8NoBom.GetString(bytes);
        }
        catch (DecoderFallbackException exception)
        {
            throw new InvalidDataException($"{description} is not valid UTF-8.", exception);
        }
    }

    private static string NormalizeOutput(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("Training output is required.", nameof(path));
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        for (var parent = Directory.GetParent(full); parent is not null; parent = parent.Parent)
            if (parent.Exists) ValidateNoReparsePoint(parent, "Training output parent");
        return full;
    }

    private static string NormalizeExistingDirectory(string path, string description)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException($"{description} is required.");
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (!Directory.Exists(full))
            throw new DirectoryNotFoundException($"{description} does not exist.");
        return full;
    }

    private static string ToPath(string root, string relative)
    {
        if (!SituationTrainingResumeValidator.IsCanonicalRelativePath(relative))
            throw new InvalidDataException("Training writer path is not canonical and relative.");
        var path = Path.GetFullPath(Path.Combine(root,
            relative.Replace('/', Path.DirectorySeparatorChar)));
        SituationArtifactIO.EnsureDescendant(root, path, "Training writer path escapes the output root.");
        return path;
    }

    private static string Relative(string root, string path) =>
        Path.GetRelativePath(root, path).Replace('\\', '/');

    private static void ValidateNoReparsePoint(FileSystemInfo info, string description)
    {
        info.Refresh();
        if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException($"{description} cannot be a symbolic link or reparse point.");
    }
}

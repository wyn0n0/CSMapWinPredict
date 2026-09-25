using System.Globalization;
using System.Text;
using System.Text.Json;
using CsDemoMap.Api.Models;

namespace CsDemoMap.Api.Services;

internal sealed record SituationPilotCandidate(
    string MatchId,
    RoundAttempt Attempt,
    SituationTrainingSelectedTick Selected);

internal static class SituationTrainingPilotAllocator
{
    internal const string AlgorithmVersion = "situation-training-pilot-selection-v1";
    internal const int MaxSamplesPerMatch = 10;

    internal static IReadOnlyList<(SituationStageFourSplitMember Member, int Quota)> AllocateQuotas(
        IReadOnlyList<SituationStageFourSplitMember> train,
        string splitSha256,
        int sampleLimit)
    {
        if (train.Count == 0 || sampleLimit <= 0 || sampleLimit > train.Count * MaxSamplesPerMatch)
            throw new InvalidDataException("Pilot sample limit cannot be allocated across train matches.");
        var ordered = train
            .OrderBy(member => SituationArtifactIO.Sha256(string.Join('|',
                AlgorithmVersion, splitSha256, member.MatchId)), StringComparer.Ordinal)
            .ThenBy(member => member.FileName, StringComparer.Ordinal)
            .ToArray();
        var quotas = ordered.ToDictionary(member => member.MatchId, _ => 0, StringComparer.Ordinal);
        for (var index = 0; index < sampleLimit; index++)
        {
            var member = ordered[index % ordered.Length];
            if (quotas[member.MatchId] >= MaxSamplesPerMatch)
                throw new InvalidDataException("Pilot round-robin allocation exceeded the per-match limit.");
            quotas[member.MatchId]++;
        }
        return ordered.Select(member => (member, quotas[member.MatchId])).ToArray();
    }

    internal static IReadOnlyList<SituationPilotCandidate> SelectForMatch(
        IReadOnlyList<SituationPilotCandidate> candidates,
        int quota,
        IReadOnlyList<string> coveragePriority,
        IDictionary<string, int> globalTagCounts)
    {
        if (quota is < 1 or > MaxSamplesPerMatch || candidates.Count < quota)
            throw new InvalidDataException("Pilot match does not contain enough selected candidates for its quota.");
        var priority = coveragePriority
            .Select((tag, index) => (tag, index))
            .ToDictionary(item => item.tag, item => item.index, StringComparer.Ordinal);
        var remaining = candidates.ToList();
        var selected = new List<SituationPilotCandidate>();
        var selectedRounds = new HashSet<string>(StringComparer.Ordinal);
        while (selected.Count < quota)
        {
            var best = remaining
                .Select(candidate => new
                {
                    Candidate = candidate,
                    CoverageTags = candidate.Selected.SelectionTags
                        .Where(priority.ContainsKey)
                        .OrderBy(tag => priority[tag])
                        .ToArray()
                })
                .OrderByDescending(item => item.CoverageTags.Count(tag =>
                    Count(globalTagCounts, tag) == 0))
                .ThenByDescending(item => item.CoverageTags.Sum(tag =>
                    1_000_000 / (1 + Count(globalTagCounts, tag))))
                .ThenByDescending(item => item.CoverageTags.Length)
                .ThenBy(item => selectedRounds.Contains(item.Candidate.Attempt.RoundId))
                .ThenBy(item => StableCandidateKey(item.Candidate), StringComparer.Ordinal)
                .First().Candidate;
            selected.Add(best);
            selectedRounds.Add(best.Attempt.RoundId);
            foreach (var tag in best.Selected.SelectionTags)
                globalTagCounts[tag] = Count(globalTagCounts, tag) + 1;
            remaining.Remove(best);
        }
        return selected;
    }

    private static int Count(IDictionary<string, int> values, string key) =>
        values.TryGetValue(key, out var count) ? count : 0;

    private static string StableCandidateKey(SituationPilotCandidate candidate) =>
        SituationArtifactIO.Sha256(string.Join('|',
            AlgorithmVersion,
            candidate.MatchId,
            candidate.Attempt.RoundId,
            candidate.Selected.Tick.ToString(CultureInfo.InvariantCulture)));
}

internal static class SituationTrainingPilotExporter
{
    internal const int PilotSampleLimit = 500;
    private static readonly UTF8Encoding Utf8NoBom = new(false);
    private static readonly JsonSerializerOptions IndentedJson = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    internal static async Task<SituationTrainingDatasetManifestV1> ExportAsync(
        string demoDirectory,
        string splitPath,
        string outputDirectory,
        int pilotSampleLimit,
        CancellationToken cancellationToken)
    {
        if (pilotSampleLimit != PilotSampleLimit)
            throw new InvalidDataException($"The frozen pilot requires exactly {PilotSampleLimit} samples.");
        var demoRoot = NormalizeExistingDirectory(demoDirectory, "Demo directory");
        var splitFullPath = NormalizeExistingFile(splitPath, "Stage-four split");
        var outputRoot = Path.GetFullPath(outputDirectory);
        SituationArtifactIO.EnsureNewOutput(outputRoot);
        ValidateNoReparsePoint(new DirectoryInfo(demoRoot), "Demo directory");

        var split = await SituationDatasetSplit.LoadApprovedAsync(splitFullPath, cancellationToken);
        ValidateDemoMembership(demoRoot, split.Split);
        var repositoryRoot = SituationArtifactIO.FindRepositoryRoot(splitFullPath);
        var schemaFiles = await SituationTrainingSchemaRegistry.LoadAsync(repositoryRoot, cancellationToken);
        var selectionLoad = SituationTrainingSelectionLoader.LoadFrozen();
        var promptRenderer = new SituationPromptRenderer();
        var ruleLoad = SituationAnalysisRuleLoader.LoadFrozen();
        var versions = CreateArtifactVersions();
        var emptyCounts = new Dictionary<string, SituationDatasetCountsV1>(StringComparer.Ordinal)
        {
            ["train"] = new(0, 0, 0)
        };

        Directory.CreateDirectory(outputRoot);
        var manifestPath = Path.Combine(outputRoot, "manifest.json");
        var incomplete = CreateManifest(
            SituationArtifactStatus.Incomplete,
            split,
            selectionLoad.Sha256,
            promptRenderer.Load.Sha256,
            versions,
            schemaFiles,
            emptyCounts,
            []);
        await SituationArtifactIO.WriteJsonAsync(manifestPath, incomplete, cancellationToken);

        var quotas = SituationTrainingPilotAllocator.AllocateQuotas(
            split.Split.Train, split.SplitSha256, pilotSampleLimit);
        var records = new List<SituationTrainingRecordV1>(pilotSampleLimit);
        var globalTagCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        var aggregate = new SelectionAggregate();
        using var sceneService = new SituationSceneService();
        var selector = new SituationTrainingCandidateSelector(sceneService);
        var coveragePriority = selectionLoad.Config.EventPriority
            .Concat(selectionLoad.Config.RarePriority)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        for (var matchIndex = 0; matchIndex < quotas.Count; matchIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (member, quota) = quotas[matchIndex];
            var demoPath = Path.Combine(demoRoot, member.FileName);
            ValidateDirectFile(demoRoot, demoPath, member.FileName);
            var sourceSha256 = await SituationArtifactIO.FileSha256Async(demoPath, cancellationToken);
            if (sourceSha256 != member.MatchId)
                throw new InvalidDataException($"Train Demo SHA-256 mismatch at ordinal {matchIndex + 1}.");

            await using var stream = new FileStream(
                demoPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var timeline = await new DemoParserService().ParseAsync(
                stream, member.FileName, cancellationToken, collectSemantics: true);
            var semantic = timeline.Semantics
                ?? throw new InvalidDataException("Pilot Demo is missing semantic collection.");
            if (timeline.Metadata.MapName != "de_mirage")
                throw new InvalidDataException("Pilot input contains a non-Mirage Demo.");

            var candidates = new List<SituationPilotCandidate>();
            var attempts = semantic.Attempts
                .Where(attempt => attempt.Disposition == "completed")
                .OrderBy(attempt => attempt.StartTick)
                .ThenBy(attempt => attempt.RoundId, StringComparer.Ordinal)
                .ToArray();
            var timelineIndex = SituationTrainingTimelineIndex.Create(timeline, cancellationToken);
            var selections = new SituationTrainingRoundSelectionWithPayloads[attempts.Length];
            await Parallel.ForEachAsync(
                Enumerable.Range(0, attempts.Length),
                new ParallelOptions
                {
                    CancellationToken = cancellationToken,
                    MaxDegreeOfParallelism = Math.Min(4, Math.Max(1, Environment.ProcessorCount))
                },
                (attemptIndex, token) =>
                {
                    selections[attemptIndex] = selector.SelectRound(
                        timelineIndex, member.MatchId, attempts[attemptIndex].RoundId, token);
                    return ValueTask.CompletedTask;
                });
            for (var attemptIndex = 0; attemptIndex < attempts.Length; attemptIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var attempt = attempts[attemptIndex];
                var result = selections[attemptIndex].SelectionResult;
                aggregate.Add(result.Selection);
                candidates.AddRange(result.Selection.Samples.Select(sample =>
                    new SituationPilotCandidate(member.MatchId, attempt, sample)));
            }

            var chosen = SituationTrainingPilotAllocator.SelectForMatch(
                candidates, quota, coveragePriority, globalTagCounts);
            var payloads = selections.SelectMany(item => item.Payloads)
                .ToDictionary(item => (item.RoundId, item.Tick));
            foreach (var candidate in chosen)
                records.Add(SituationTrainingRecordBuilder.Build(
                    member,
                    payloads[(candidate.Attempt.RoundId, candidate.Selected.Tick)],
                    SituationTrainingSplit.Train,
                    split.SplitSha256));
            Console.WriteLine(
                $"Pilot train match {matchIndex + 1}/{quotas.Count}: selected {chosen.Count}, " +
                $"total {records.Count}/{pilotSampleLimit}.");
        }

        if (records.Count != pilotSampleLimit)
            throw new InvalidDataException("Pilot did not produce the requested number of samples.");
        var orderedRecords = records
            .OrderBy(record => record.Metadata.MatchRef, StringComparer.Ordinal)
            .ThenBy(record => record.Metadata.RoundRef, StringComparer.Ordinal)
            .ThenBy(record => record.Metadata.Tick)
            .ThenBy(record => record.SampleId, StringComparer.Ordinal)
            .ToArray();
        if (orderedRecords.Select(record => record.SampleId).Distinct(StringComparer.Ordinal).Count() !=
            orderedRecords.Length)
            throw new InvalidDataException("Pilot contains duplicate sample IDs.");

        var trainPath = Path.Combine(outputRoot, "train.jsonl");
        var recordLines = orderedRecords.Select(SituationTrainingContractJson.SerializeLine).ToArray();
        await WriteLinesAsync(trainPath, recordLines, cancellationToken);

        var measurements = new List<SituationRepresentationMeasurementV1>(orderedRecords.Length);
        var measurementLines = new List<string>(orderedRecords.Length);
        for (var index = 0; index < orderedRecords.Length; index++)
        {
            var measurement = Measure(orderedRecords[index], recordLines[index], promptRenderer);
            measurements.Add(measurement);
            measurementLines.Add(SituationCanonicalJson.Serialize(measurement));
        }
        ValidateRepresentationDecision(measurements, promptRenderer.Load.Config);
        var measurementsPath = Path.Combine(outputRoot, "representation-measurements.jsonl");
        await WriteLinesAsync(measurementsPath, measurementLines, cancellationToken);

        var splitOutputPath = Path.Combine(outputRoot, "split.json");
        var splitText = await File.ReadAllTextAsync(splitFullPath, cancellationToken);
        await SituationArtifactIO.WriteTextAtomicAsync(splitOutputPath, splitText, cancellationToken);
        if (await SituationArtifactIO.FileSha256Async(splitOutputPath, cancellationToken) != split.SplitSha256)
            throw new InvalidDataException("Pilot split copy SHA-256 differs from the approved split.");

        var provenance = await CreateProvenanceAsync(
            repositoryRoot,
            split,
            ruleLoad.Sha256,
            selectionLoad.Sha256,
            promptRenderer.Load.Sha256,
            cancellationToken);
        var provenancePath = Path.Combine(outputRoot, "provenance.json");
        await SituationArtifactIO.WriteJsonAsync(provenancePath, provenance, cancellationToken);

        var knownBytes = new[] { trainPath, measurementsPath, splitOutputPath, provenancePath }
            .Sum(path => new FileInfo(path).Length);
        var labelStatsPath = Path.Combine(outputRoot, "label-stats.json");
        long totalBytes = knownBytes;
        string labelStatsJson;
        while (true)
        {
            var labelStats = CreateLabelStats(
                split.SplitSha256,
                promptRenderer.Load,
                measurements,
                orderedRecords,
                aggregate,
                new FileInfo(trainPath).Length,
                new FileInfo(measurementsPath).Length,
                totalBytes);
            labelStatsJson = JsonSerializer.Serialize(labelStats, IndentedJson);
            var next = knownBytes + Utf8NoBom.GetByteCount(labelStatsJson);
            if (next == totalBytes)
                break;
            totalBytes = next;
        }
        await SituationArtifactIO.WriteTextAtomicAsync(labelStatsPath, labelStatsJson, cancellationToken);

        var inventory = await SituationArtifactIO.InventoryFilesAsync(
            outputRoot, cancellationToken, "manifest.json");
        var files = inventory.Select(file => new SituationArtifactFileV1(
                file.Path,
                file.Bytes,
                file.Path switch
                {
                    "train.jsonl" => orderedRecords.Length,
                    "representation-measurements.jsonl" => measurements.Count,
                    _ => null
                },
                file.Sha256))
            .OrderBy(file => file.Path, StringComparer.Ordinal)
            .ToArray();
        var counts = new Dictionary<string, SituationDatasetCountsV1>(StringComparer.Ordinal)
        {
            ["train"] = new(
                orderedRecords.Select(record => record.Metadata.MatchRef).Distinct(StringComparer.Ordinal).Count(),
                orderedRecords.Select(record => record.Metadata.RoundRef).Distinct(StringComparer.Ordinal).Count(),
                orderedRecords.Length)
        };
        var complete = CreateManifest(
            SituationArtifactStatus.Complete,
            split,
            selectionLoad.Sha256,
            promptRenderer.Load.Sha256,
            versions,
            schemaFiles,
            counts,
            files);
        await SituationArtifactIO.WriteJsonAsync(manifestPath, complete, cancellationToken);
        await SituationTrainingManifestValidator.VerifyAsync(
            repositoryRoot, outputRoot, complete, cancellationToken);
        return complete;
    }

    private static SituationRepresentationMeasurementV1 Measure(
        SituationTrainingRecordV1 record,
        string recordLine,
        SituationPromptRenderer renderer)
    {
        var inputJson = renderer.SerializeModelInput(record.Input);
        var expanded = renderer.Render(record.Input, "expanded-v1");
        var compact = renderer.Render(record.Input, "compact-v1");
        var compactJson = ExtractPromptJson(compact);
        if (renderer.RestoreCompactInputJson(compactJson) != inputJson)
            throw new InvalidDataException("compact-v1 is not a lossless reversible input representation.");
        var config = renderer.Load.Config;
        var scene = SituationCanonicalJson.Serialize(record.Input.Scene);
        var facts = SituationCanonicalJson.Serialize(record.Input.Facts);
        var evidence = SituationCanonicalJson.Serialize(record.Input.Facts.Evidence);
        var label = SituationCanonicalJson.Serialize(record.Output);
        return new(
            SituationTrainingContractVersions.RepresentationMeasurement,
            record.SampleId,
            Dimensions(recordLine, recordLine, config),
            Dimensions(inputJson, inputJson, config),
            Dimensions(expanded, ExtractPromptJson(expanded), config),
            Dimensions(compact, compactJson, config),
            Utf8NoBom.GetByteCount(scene),
            Utf8NoBom.GetByteCount(facts),
            Utf8NoBom.GetByteCount(evidence),
            Utf8NoBom.GetByteCount(label));
    }

    private static SituationRepresentationDimensionsV1 Dimensions(
        string text,
        string json,
        SituationPromptRepresentationConfigV1 config)
    {
        var bytes = Utf8NoBom.GetByteCount(text);
        var characters = text.EnumerateRunes().Count();
        var maxLine = text.Split('\n').Max(line => line.EnumerateRunes().Count());
        using var document = JsonDocument.Parse(json);
        var maxArray = MaxArrayLength(document.RootElement);
        return new(
            bytes,
            characters,
            maxLine,
            maxArray,
            bytes > config.CandidateMaxUtf8Bytes ||
            characters > config.CandidateMaxUnicodeCharacters);
    }

    private static int MaxArrayLength(JsonElement element)
    {
        var result = element.ValueKind == JsonValueKind.Array ? element.GetArrayLength() : 0;
        if (element.ValueKind == JsonValueKind.Array)
            foreach (var item in element.EnumerateArray())
                result = Math.Max(result, MaxArrayLength(item));
        else if (element.ValueKind == JsonValueKind.Object)
            foreach (var property in element.EnumerateObject())
                result = Math.Max(result, MaxArrayLength(property.Value));
        return result;
    }

    private static string ExtractPromptJson(string prompt)
    {
        var lines = prompt.Split('\n');
        if (lines.Length != 4)
            throw new InvalidDataException("Prompt wrapper must contain exactly four lines.");
        return lines[2];
    }

    private static void ValidateRepresentationDecision(
        IReadOnlyList<SituationRepresentationMeasurementV1> measurements,
        SituationPromptRepresentationConfigV1 config)
    {
        if (measurements.Count == 0)
            throw new InvalidDataException("Representation decision requires non-empty measurements.");
        if (config.SelectedVersion == "compact-v1" &&
            Percentile(measurements.Select(item => item.CompactPrompt.Utf8Bytes), 0.95) >=
            Percentile(measurements.Select(item => item.ExpandedPrompt.Utf8Bytes), 0.95))
            throw new InvalidDataException("compact-v1 was selected without reducing p95 UTF-8 prompt bytes.");
    }

    private static SituationLabelStatsV1 CreateLabelStats(
        string splitSha256,
        SituationPromptRepresentationLoadResult promptLoad,
        IReadOnlyList<SituationRepresentationMeasurementV1> measurements,
        IReadOnlyList<SituationTrainingRecordV1> records,
        SelectionAggregate aggregate,
        long trainBytes,
        long measurementBytes,
        long totalArtifactBytes)
    {
        var sections = new List<SituationLabelStatSectionV1>();
        AddDimensions("structured-record", measurements.Select(item => item.StructuredRecord));
        AddDimensions("model-input", measurements.Select(item => item.ModelInput));
        AddDimensions("expanded-v1", measurements.Select(item => item.ExpandedPrompt));
        AddDimensions("compact-v1", measurements.Select(item => item.CompactPrompt));
        AddSummary("segment.scene.utf8-bytes", measurements.Select(item => (double)item.SceneUtf8Bytes));
        AddSummary("segment.facts.utf8-bytes", measurements.Select(item => (double)item.FactsUtf8Bytes));
        AddSummary("segment.evidence.utf8-bytes", measurements.Select(item => (double)item.EvidenceUtf8Bytes));
        AddSummary("segment.label.utf8-bytes", measurements.Select(item => (double)item.LabelUtf8Bytes));
        sections.Add(Section("disk-volume", new Dictionary<string, double>(StringComparer.Ordinal)
        {
            ["representation-measurements-jsonl-bytes"] = measurementBytes,
            ["total-artifact-bytes-excluding-manifest"] = totalArtifactBytes,
            ["train-jsonl-bytes"] = trainBytes
        }));
        sections.Add(Section("pilot", new Dictionary<string, double>(StringComparer.Ordinal)
        {
            ["matches"] = records.Select(item => item.Metadata.MatchRef).Distinct(StringComparer.Ordinal).Count(),
            ["max-samples-per-match"] = records.GroupBy(item => item.Metadata.MatchRef).Max(group => group.Count()),
            ["rounds"] = records.Select(item => item.Metadata.RoundRef).Distinct(StringComparer.Ordinal).Count(),
            ["samples"] = records.Count
        }));
        sections.Add(Section("phase", records
            .GroupBy(item => item.Metadata.Phase.ToString().ToLowerInvariant())
            .ToDictionary(group => group.Key, group => (double)group.Count(), StringComparer.Ordinal)));
        sections.Add(Section("selection-tags", records
            .SelectMany(item => item.Metadata.SelectionTags)
            .GroupBy(tag => tag, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => (double)group.Count(), StringComparer.Ordinal)));
        sections.Add(Section("facts.contact-risk", records
            .GroupBy(item => item.Input.Facts.ContactRisk.ToString().ToLowerInvariant())
            .ToDictionary(group => group.Key, group => (double)group.Count(), StringComparer.Ordinal)));
        sections.Add(Section("facts.bomb-state", records
            .GroupBy(item => item.Input.Facts.Bomb.State.ToString().ToLowerInvariant())
            .ToDictionary(group => group.Key, group => (double)group.Count(), StringComparer.Ordinal)));
        sections.Add(Section("facts.quality-codes", records
            .SelectMany(item => item.Input.Facts.DataQuality)
            .GroupBy(item => item.Code, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => (double)group.Count(), StringComparer.Ordinal)));
        AddSummary("facts.evidence-count", records.Select(item => (double)item.Input.Facts.Evidence.Count));
        AddSummary("label.highlight-count", records.Select(item => (double)item.Output.Highlights.Count));
        AddSummary("label.uncertainty-count", records.Select(item => (double)item.Output.Uncertainties.Count));
        sections.Add(Section("selector.candidates", aggregate.Categories.ToDictionary(
            item => item.Key,
            item => (double)item.Value.CandidateCount,
            StringComparer.Ordinal)));
        sections.Add(Section("selector.selected", aggregate.Categories.ToDictionary(
            item => item.Key,
            item => (double)item.Value.SelectedCount,
            StringComparer.Ordinal)));
        sections.Add(Section("selector.merged", aggregate.Categories.ToDictionary(
            item => item.Key,
            item => (double)item.Value.MergedCount,
            StringComparer.Ordinal)));
        sections.Add(Section("selector.removed-by-limit", aggregate.Categories.ToDictionary(
            item => item.Key,
            item => (double)item.Value.RemovedByLimitCount,
            StringComparer.Ordinal)));
        sections.Add(Section("selector.missing-anchors", aggregate.Categories.ToDictionary(
            item => item.Key,
            item => (double)item.Value.MissingAnchorCount,
            StringComparer.Ordinal)));
        sections.Add(Section("selector.shortages", aggregate.Shortages.ToDictionary(
            item => item.Key, item => (double)item.Value, StringComparer.Ordinal)));
        sections.Add(Section("eligibility-rejected", aggregate.EligibilityRejected.ToDictionary(
            item => item.Key, item => (double)item.Value, StringComparer.Ordinal)));

        return new(
            SituationTrainingContractVersions.LabelStats,
            SituationTrainingContractVersions.DatasetManifest,
            splitSha256,
            promptLoad.Config.SelectedVersion,
            promptLoad.Sha256,
            false,
            sections.OrderBy(section => section.Name, StringComparer.Ordinal).ToArray());

        void AddDimensions(
            string prefix,
            IEnumerable<SituationRepresentationDimensionsV1> dimensions)
        {
            var values = dimensions.ToArray();
            AddSummary($"representation.{prefix}.utf8-bytes", values.Select(item => (double)item.Utf8Bytes));
            AddSummary($"representation.{prefix}.unicode-characters",
                values.Select(item => (double)item.UnicodeCharacters));
            AddSummary($"representation.{prefix}.max-line-length",
                values.Select(item => (double)item.MaxLineLength));
            AddSummary($"representation.{prefix}.max-nested-array-length",
                values.Select(item => (double)item.MaxNestedArrayLength));
            sections.Add(Section($"representation.{prefix}.candidate-limit", new Dictionary<string, double>
            {
                ["exceeded-count"] = values.Count(item => item.ExceedsCandidateLimit)
            }));
        }

        void AddSummary(string name, IEnumerable<double> source)
        {
            var values = source.Order().ToArray();
            if (values.Length == 0)
                return;
            sections.Add(Section(name, new Dictionary<string, double>(StringComparer.Ordinal)
            {
                ["max"] = values[^1],
                ["min"] = values[0],
                ["p50"] = Percentile(values, 0.50),
                ["p90"] = Percentile(values, 0.90),
                ["p95"] = Percentile(values, 0.95),
                ["p99"] = Percentile(values, 0.99),
                ["total"] = values.Sum()
            }));
        }
    }

    private static SituationLabelStatSectionV1 Section(
        string name,
        IReadOnlyDictionary<string, double> values) => new(
        name,
        values.OrderBy(item => item.Key, StringComparer.Ordinal)
            .Select(item => new SituationLabelStatMetricV1(item.Key, item.Value))
            .ToArray());

    private static double Percentile(IEnumerable<int> source, double percentile) =>
        Percentile(source.Select(value => (double)value), percentile);

    private static double Percentile(IEnumerable<double> source, double percentile)
    {
        var values = source.Order().ToArray();
        if (values.Length == 0 || percentile is <= 0 or > 1)
            throw new InvalidDataException("Nearest-rank percentile input is invalid.");
        return values[Math.Max(0, (int)Math.Ceiling(percentile * values.Length) - 1)];
    }

    private static async Task<SituationTrainingProvenanceV1> CreateProvenanceAsync(
        string repositoryRoot,
        SituationStageFourSplitResult split,
        string analysisRulesSha256,
        string selectionConfigSha256,
        string inputRepresentationConfigSha256,
        CancellationToken cancellationToken)
    {
        var sourcePaths = new[]
        {
            "apps/api/Features/Situation/Training/Models/SituationTrainingModels.cs",
            "apps/api/Features/Situation/Training/SituationPromptRepresentation.cs",
            "apps/api/Features/Situation/Training/SituationTrainingCandidateSelector.cs",
            "apps/api/Features/Situation/Training/SituationTrainingContractJson.cs",
            "apps/api/Features/Situation/Training/situation-prompt-representation-v1.json",
            "apps/api/Features/Situation/Training/situation-training-selection-v1.json",
            "apps/api/Features/Situation/Workflows/SituationTrainingPilotExporter.cs"
        };
        var sourceFiles = new List<SituationProvenanceSourceFileV1>();
        foreach (var relative in sourcePaths.Order(StringComparer.Ordinal))
        {
            var path = SituationArtifactIO.ResolveRepositoryPath(repositoryRoot, relative, "Pilot source file");
            sourceFiles.Add(new(relative,
                await SituationArtifactIO.FileSha256Async(path, cancellationToken)));
        }
        var identityVersions = CreateIdentityVersions(split.SplitSha256);
        var mappings = split.Split.Train
            .OrderBy(member => member.FileName, StringComparer.Ordinal)
            .Select(member => new SituationProvenanceDemoMappingV1(
                member.FileName,
                SituationTrainingContractJson.CreateIdentity(
                    member.MatchId, "s0-a1", 0, identityVersions).MatchRef,
                member.MatchId))
            .ToArray();
        return new(
            SituationTrainingContractVersions.Provenance,
            split.SplitSha256,
            analysisRulesSha256,
            selectionConfigSha256,
            inputRepresentationConfigSha256,
            SituationArtifactIO.TryReadGitHead(repositoryRoot),
            sourceFiles,
            mappings);
    }

    private static SituationTrainingIdentityVersions CreateIdentityVersions(string splitSha256) => new(
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

    internal static SituationTrainingArtifactVersionsV1 CreateArtifactVersions() => new(
        SituationTrainingContractVersions.Data,
        SituationTrainingContractVersions.Split,
        SituationTrainingContractVersions.TrainingRecord,
        SituationContractVersions.Scene,
        SituationSceneBuilder.BuilderVersion,
        SituationSceneBuilder.GeometryVersion,
        SituationContractVersions.Facts,
        SituationAnalysisRuleLoader.FrozenAnalysisRuleVersion,
        SituationContractVersions.Narrative,
        WinFeatureSampleBuilder.SemanticVersion,
        SituationTrainingContractVersions.Selection,
        SituationTrainingContractVersions.InputRepresentation,
        SituationTrainingContractVersions.PromptRepresentationConfig,
        SituationTrainingContractVersions.RepresentationMeasurement,
        SituationTrainingContractVersions.ReviewCandidate,
        SituationTrainingContractVersions.ReviewDecision,
        SituationTrainingContractVersions.LabelStats,
        SituationTrainingContractVersions.FrozenReviewLabel,
        SituationTrainingContractVersions.FrozenReviewManifest,
        null);

    private static SituationTrainingDatasetManifestV1 CreateManifest(
        SituationArtifactStatus status,
        SituationStageFourSplitResult split,
        string selectionConfigSha256,
        string inputRepresentationConfigSha256,
        SituationTrainingArtifactVersionsV1 versions,
        IReadOnlyList<SituationSchemaFileReferenceV1> schemaFiles,
        IReadOnlyDictionary<string, SituationDatasetCountsV1> counts,
        IReadOnlyList<SituationArtifactFileV1> files) => new(
        SituationTrainingContractVersions.DatasetManifest,
        status,
        SituationTrainingExportMode.Pilot,
        "representation-measurement",
        false,
        PilotSampleLimit,
        split.SplitSha256,
        split.Split.ParentManifestSha256,
        selectionConfigSha256,
        inputRepresentationConfigSha256,
        versions,
        schemaFiles,
        counts,
        files);

    private static async Task WriteLinesAsync(
        string path,
        IEnumerable<string> lines,
        CancellationToken cancellationToken)
    {
        var temporary = path + ".partial";
        await using (var stream = new FileStream(
                         temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                         1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
        await using (var writer = new StreamWriter(stream, Utf8NoBom) { NewLine = "\n" })
        {
            foreach (var line in lines)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (line.Contains('\r') || line.Contains('\n') || line.StartsWith('\uFEFF'))
                    throw new InvalidDataException("JSONL content must be a UTF-8 single line without BOM.");
                await writer.WriteLineAsync(line.AsMemory(), cancellationToken);
            }
        }
        File.Move(temporary, path, overwrite: false);
    }

    internal static void ValidateDemoMembership(string demoRoot, SituationStageFourSplitDocument split)
    {
        var files = Directory.EnumerateFiles(demoRoot, "*", SearchOption.TopDirectoryOnly)
            .Where(path => string.Equals(Path.GetExtension(path), ".dem", StringComparison.OrdinalIgnoreCase))
            .Select(path => new FileInfo(path))
            .ToArray();
        var members = split.Train.Concat(split.Dev).Concat(split.Test).ToArray();
        if (files.Length != split.SourceDemoCount ||
            files.Select(file => file.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != files.Length)
            throw new InvalidDataException("Pilot Demo directory must contain exactly the frozen 87 .dem files.");
        var actual = files.ToDictionary(file => file.Name, StringComparer.OrdinalIgnoreCase);
        if (members.Any(member => !actual.TryGetValue(member.FileName, out var file) ||
                                  !string.Equals(file.Name, member.FileName, StringComparison.Ordinal)) ||
            actual.Keys.Any(name => !members.Any(member =>
                string.Equals(member.FileName, name, StringComparison.OrdinalIgnoreCase))))
            throw new InvalidDataException("Pilot Demo directory membership differs from the frozen split.");
    }

    internal static void ValidateDirectFile(string root, string path, string expectedName)
    {
        var file = new FileInfo(path);
        if (!file.Exists || file.Name != expectedName ||
            (file.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Pilot Demo input is missing, renamed, or a reparse point.");
        SituationArtifactIO.EnsureDirectChild(root, file.FullName,
            "Pilot Demo must be a direct child of the explicit input directory.");
    }

    private static string NormalizeExistingDirectory(string path, string description)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException($"{description} is required.");
        var full = Path.GetFullPath(path);
        if (!Directory.Exists(full))
            throw new DirectoryNotFoundException($"{description} does not exist.");
        return Path.TrimEndingDirectorySeparator(full);
    }

    private static string NormalizeExistingFile(string path, string description)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException($"{description} is required.");
        var full = Path.GetFullPath(path);
        if (!File.Exists(full))
            throw new FileNotFoundException($"{description} does not exist.", full);
        return full;
    }

    private static void ValidateNoReparsePoint(FileSystemInfo info, string description)
    {
        info.Refresh();
        if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException($"{description} cannot be a symbolic link or reparse point.");
    }

    private sealed class SelectionAggregate
    {
        internal Dictionary<string, SituationTrainingSelectionCategoryStats> Categories { get; } =
            new(StringComparer.Ordinal);
        internal Dictionary<string, int> EligibilityRejected { get; } = new(StringComparer.Ordinal);
        internal Dictionary<string, int> Shortages { get; } = new(StringComparer.Ordinal);

        internal void Add(SituationTrainingRoundSelectionV1 selection)
        {
            foreach (var item in selection.Categories)
            {
                var old = Categories.GetValueOrDefault(item.Key) ?? new(0, 0, 0, 0, 0);
                Categories[item.Key] = new(
                    old.CandidateCount + item.Value.CandidateCount,
                    old.SelectedCount + item.Value.SelectedCount,
                    old.MergedCount + item.Value.MergedCount,
                    old.RemovedByLimitCount + item.Value.RemovedByLimitCount,
                    old.MissingAnchorCount + item.Value.MissingAnchorCount);
            }
            AddCounts(EligibilityRejected, selection.EligibilityRejected);
            AddCounts(Shortages, selection.Shortages);
        }

        private static void AddCounts(IDictionary<string, int> target, IReadOnlyDictionary<string, int> source)
        {
            foreach (var item in source)
                target[item.Key] = (target.TryGetValue(item.Key, out var count) ? count : 0) + item.Value;
        }
    }
}

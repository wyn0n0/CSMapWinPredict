using System.Text;
using System.Text.Json;
using CsDemoMap.Api.Models;
using CsDemoMap.Api.Services;

namespace CsDemoMap.Cli;

internal static class SituationTokenizerCommands
{
    internal static async Task ExportAsync(string source, string output, CancellationToken cancellationToken)
    {
        var root = Path.GetFullPath(source);
        var repository = SituationArtifactIO.FindRepositoryRoot(Environment.CurrentDirectory);
        var manifest = SituationTrainingContractJson.DeserializeManifest(
            await File.ReadAllTextAsync(Path.Combine(root, "manifest.json"), cancellationToken));
        if (manifest.Status != SituationArtifactStatus.Complete || manifest.Mode != SituationTrainingExportMode.Pilot ||
            manifest.Trainable || manifest.Purpose != "representation-measurement" || manifest.SampleLimit != 500)
            throw new InvalidDataException("This command only measures the frozen, non-trainable 500-row pilot.");
        await SituationTrainingManifestValidator.VerifyAsync(repository, root, manifest, cancellationToken);
        var renderer = new SituationPromptRenderer();
        if (renderer.Load.Sha256 != manifest.InputRepresentationConfigSha256)
            throw new InvalidDataException("Frozen representation configuration differs from pilot.");
        using var split = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(root, "split.json"), cancellationToken));
        var trainRefs = split.RootElement.GetProperty("train").EnumerateArray().Select(member =>
            "match-" + SituationArtifactIO.Sha256("situation-training-match-ref-v1\ndata=" + manifest.Versions.Data +
                                                "\nmatch=" + member.GetProperty("matchId").GetString())).ToHashSet(StringComparer.Ordinal);
        var records = (await File.ReadAllLinesAsync(Path.Combine(root, "train.jsonl"), cancellationToken))
            .Select(SituationTrainingContractJson.DeserializeRecord).ToArray();
        if (records.Length != 500 || records.Select(r => r.SampleId).Distinct().Count() != 500 ||
            records.Any(r => r.Metadata.Split != SituationTrainingSplit.Train || !trainRefs.Contains(r.Metadata.MatchRef)))
            throw new InvalidDataException("Pilot rows are not unique train members.");
        SituationArtifactIO.EnsureNewOutput(output);
        Directory.CreateDirectory(output);
        var path = Path.Combine(output, "rendered-pilot.jsonl");
        await using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write))
        await using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
        {
            foreach (var record in records)
            {
                var rendered = renderer.Render(record.Input, "compact-v1");
                var pieces = rendered.Split('\n');
                if (pieces.Length != 4 ||
                    renderer.RestoreCompactInputJson(pieces[2]) != renderer.SerializeModelInput(record.Input))
                    throw new InvalidDataException("Frozen compact input failed exact canonical round-trip.");
                var row = new
                {
                    record.SampleId, record.Metadata.MatchRef, record.Metadata.SelectionTags,
                    phase = record.Metadata.Phase.ToString(), system = pieces[0],
                    user = string.Join('\n', pieces.Skip(1)), compactJson = pieces[2],
                    target = SituationCanonicalJson.Serialize(record.Output),
                    frozenRenderedUtf8Bytes = Encoding.UTF8.GetByteCount(rendered),
                    frozenRenderedSha256 = SituationArtifactIO.Sha256(rendered)
                };
                await writer.WriteLineAsync(JsonSerializer.Serialize(row, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            }
        }
        await SituationArtifactIO.WriteJsonAsync(Path.Combine(output, "export-manifest.json"), new
        {
            status = "complete", purpose = "tokenizer-measurement-only", trainable = false,
            rows = records.Length, representation = "compact-v1", representationConfigSha256 = renderer.Load.Sha256,
            renderedSha256 = await SituationArtifactIO.FileSha256Async(path, cancellationToken),
            assemblySha256 = await SituationArtifactIO.FileSha256Async(typeof(SituationPromptRenderer).Assembly.Location, cancellationToken)
        }, cancellationToken);
    }
}

using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

// Use the already frozen producer assembly: do not reproduce its Unicode,
// ordinal property ordering or six-decimal numeric rules in another language.
if (args.Length != 4) throw new ArgumentException("api-dll base-dataset review-inputs new-output required");
var apiPath = Path.GetFullPath(args[0]);
var basePath = Path.GetFullPath(args[1]);
var reviewPath = Path.GetFullPath(args[2]);
var outputPath = Path.GetFullPath(args[3]);
if (File.Exists(outputPath)) throw new InvalidDataException("output-exists");
string HashFile(string p) { using var f = File.OpenRead(p); return Convert.ToHexStringLower(SHA256.HashData(f)); }
string HashText(string s) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(s)));
var apiHash = HashFile(apiPath);
var reviewHash = HashFile(reviewPath);
var baseManifestHash = HashFile(Path.Combine(basePath, "manifest.json"));
var partitions = new[] { "train", "dev", "test" }.ToDictionary(s => s, s => HashFile(Path.Combine(basePath, s + ".jsonl")));
AssemblyLoadContext.Default.Resolving += (_, name) =>
{
    var path = Path.Combine(Path.GetDirectoryName(apiPath)!, name.Name + ".dll");
    return File.Exists(path) ? AssemblyLoadContext.Default.LoadFromAssemblyPath(path) : null;
};
var api = AssemblyLoadContext.Default.LoadFromAssemblyPath(apiPath);
var contract = api.GetType("CsDemoMap.Api.Services.SituationTrainingContractJson", true)!;
var canonical = api.GetType("CsDemoMap.Api.Services.SituationCanonicalJson", true)!;
var deserialize = contract.GetMethod("DeserializeRecord", BindingFlags.NonPublic | BindingFlags.Static)!;
var hashMethod = canonical.GetMethod("Sha256", BindingFlags.Public | BindingFlags.Static)!;
string CanonicalHash(object value, Type type) => (string)hashMethod.MakeGenericMethod(type).Invoke(null, [value])!;
var wanted = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
foreach (var line in File.ReadLines(reviewPath, Encoding.UTF8))
{
    using var doc = JsonDocument.Parse(line);
    var row = doc.RootElement.Clone();
    if (!wanted.TryAdd(row.GetProperty("sampleId").GetString()!, row)) throw new InvalidDataException("review-id-duplicate");
}
if (wanted.Count != 50) throw new InvalidDataException("review-scope-mismatch");
var result = new Dictionary<string, object>(StringComparer.Ordinal);
foreach (var split in partitions.Keys)
{
    foreach (var line in File.ReadLines(Path.Combine(basePath, split + ".jsonl"), Encoding.UTF8))
    {
        using var doc = JsonDocument.Parse(line);
        var row = doc.RootElement;
        var id = row.GetProperty("sampleId").GetString()!;
        if (!wanted.TryGetValue(id, out var reviewed)) continue;
        var record = deserialize.Invoke(null, [line])!;
        var canonicalHash = CanonicalHash(record, record.GetType());
        var originalSceneHash = CanonicalHash(row.GetProperty("input").GetProperty("scene"), typeof(JsonElement));
        var reviewedSceneHash = CanonicalHash(reviewed.GetProperty("input").GetProperty("scene"), typeof(JsonElement));
        var metadata = row.GetProperty("metadata");
        if (canonicalHash != reviewed.GetProperty("recordSha256").GetString()) throw new InvalidDataException("canonical-record-hash-mismatch");
        if (originalSceneHash != reviewedSceneHash || originalSceneHash != metadata.GetProperty("modelInputSha256").GetString())
            throw new InvalidDataException("canonical-scene-lineage-mismatch");
        if (split != reviewed.GetProperty("split").GetString() || split != metadata.GetProperty("split").GetString() ||
            reviewed.GetProperty("matchRef").GetString() != metadata.GetProperty("matchRef").GetString() ||
            reviewed.GetProperty("roundRef").GetString() != metadata.GetProperty("roundRef").GetString())
            throw new InvalidDataException("review-lineage-mismatch");
        if (!result.TryAdd(id, new { split, canonicalRecordSha256 = canonicalHash, rawLineSha256 = HashText(line),
            sceneSha256 = originalSceneHash, reviewedSceneSha256 = reviewedSceneHash })) throw new InvalidDataException("source-id-duplicate");
    }
}
if (result.Count != wanted.Count) throw new InvalidDataException("review-source-coverage-mismatch");
if (HashFile(apiPath) != apiHash || HashFile(reviewPath) != reviewHash || HashFile(Path.Combine(basePath, "manifest.json")) != baseManifestHash ||
    partitions.Any(x => HashFile(Path.Combine(basePath, x.Key + ".jsonl")) != x.Value)) throw new InvalidDataException("bridge-input-drift");
var report = new { schemaVersion = "situation-stage6-review-source-hashes-v1", status = "passed", complete = true,
    canonicalizerAssemblySha256 = apiHash, baseManifestSha256 = baseManifestHash, reviewInputsSha256 = reviewHash,
    sourcePartitionSha256 = partitions, hashDomain = "existing SituationCanonicalJson.Sha256<T>; typed source record; canonical projected scene",
    records = result };
using (var target = new FileStream(outputPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
    JsonSerializer.Serialize(target, report, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true });
Console.WriteLine(JsonSerializer.Serialize(new { status = "passed", reviewedRecords = result.Count }));

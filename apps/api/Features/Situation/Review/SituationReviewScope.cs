using System.Text;

namespace CsDemoMap.Api.Services;

// A persisted review target over immutable candidates. The full journal and existing
// decisions keep their original identity; the scope follows ordinals through replacements.
internal sealed class SituationReviewScope : IReviewBackend
{
    internal sealed record Plan(string SchemaVersion, ReviewWorkspaceIdentity Identity,
        int Target, long StartingRevision, IReadOnlyList<int> Ordinals);
    private sealed record Document(Plan Plan, string Sha256);
    private readonly IReviewBackend inner;
    private readonly IReviewWorkStore store;
    private readonly Dictionary<int, int> displayOrdinals;

    private SituationReviewScope(IReviewBackend inner, IReviewWorkStore store, Plan plan)
    {
        this.inner = inner; this.store = store;
        displayOrdinals = plan.Ordinals.Select((ordinal, index) => (ordinal, display: index + 1))
            .ToDictionary(x => x.ordinal, x => x.display);
    }

    internal static async Task<IReviewBackend> OpenAsync(IReviewBackend inner, IReviewWorkStore store,
        string work, int? requestedTarget, CancellationToken ct)
    {
        var path = Path.Combine(work, "review-scope.json");
        SituationReviewArtifactLoader.EnsureNoLinks(path);
        if (!File.Exists(path) && requestedTarget is null) return inner;
        var samples = inner.GetSamples();
        Plan plan;
        if (File.Exists(path))
        {
            var document = SituationReviewJson.Deserialize<Document>(await File.ReadAllTextAsync(path, ct));
            plan = document.Plan;
            if (SituationCanonicalJson.Sha256(plan) != document.Sha256 ||
                !SituationReviewCandidateReader.Equal(plan.Identity, store.Identity) ||
                plan.SchemaVersion != "situation-review-scope-v1" || plan.Target != 50 ||
                requestedTarget is not null && requestedTarget != plan.Target ||
                plan.StartingRevision > store.Snapshot.WorkspaceRevision ||
                plan.Ordinals.Count != plan.Target ||
                !plan.Ordinals.SequenceEqual(plan.Ordinals.Distinct().Order()) ||
                plan.Ordinals.Any(o => !samples.Any(s => s.ReviewOrdinal == o)))
                throw new ReviewException("review-scope-mismatch", 409);
        }
        else
        {
            if (requestedTarget != 50) throw new ReviewException("invalid-review-target");
            var state = store.Snapshot;
            var retained = state.Replacements.Select(r => r.ReviewOrdinal).ToHashSet();
            var selected = Select(samples, retained);
            plan = new("situation-review-scope-v1", store.Identity, 50, state.WorkspaceRevision,
                selected.Select(s => s.ReviewOrdinal).Order().ToArray());
            var text = SituationCanonicalJson.Serialize(new Document(plan, SituationCanonicalJson.Sha256(plan)));
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await stream.WriteAsync(Encoding.UTF8.GetBytes(text), ct);
                stream.Flush(true);
            }
            File.Move(temporary, path);
            if (await File.ReadAllTextAsync(path, ct) != text) throw new ReviewException("storage-failed", 503);
        }
        return new SituationReviewScope(inner, store, plan);
    }

    internal static IReadOnlyList<ReviewSampleSummary> Select(IReadOnlyList<ReviewSampleSummary> samples,
        IReadOnlySet<int> retained)
    {
        var selected = samples.Where(s => s.Decision != "unreviewed" || retained.Contains(s.ReviewOrdinal)).ToList();
        var quotas = new Dictionary<string, int> { ["train"] = 36, ["dev"] = 7, ["test"] = 7 };
        if (selected.Count > 50 || quotas.Any(q => selected.Count(s => s.Split == q.Key) > q.Value ||
            samples.Count(s => s.Split == q.Key) < q.Value)) throw new ReviewException("review-scope-cannot-retain-progress", 409);
        foreach (var (split, target) in quotas)
        {
            while (selected.Count(s => s.Split == split) < target)
            {
                var covered = selected.Where(s => s.Split == split).SelectMany(s => s.Categories).ToHashSet(StringComparer.Ordinal);
                var candidates = samples.Where(s => s.Split == split && !selected.Any(x => x.SampleId == s.SampleId));
                // Stable category coverage first, then original distributed source ordering.
                var next = candidates.OrderByDescending(s => s.Categories.Count(c => !covered.Contains(c)))
                    .ThenBy(s => SituationArtifactIO.Sha256("review-scope50-v1\n" + s.SampleId), StringComparer.Ordinal).First();
                selected.Add(next);
            }
        }
        return selected.OrderBy(s => s.ReviewOrdinal).ToArray();
    }

    public IReadOnlyList<ReviewSampleSummary> GetSamples() => inner.GetSamples()
        .Where(s => displayOrdinals.ContainsKey(s.ReviewOrdinal))
        .Select(s => s with { ReviewOrdinal = displayOrdinals[s.ReviewOrdinal] }).OrderBy(s => s.ReviewOrdinal).ToArray();

    private ReviewProgress Progress()
    {
        var full = inner.GetSession().Progress;
        var rows = GetSamples();
        var splits = new[] { "train", "dev", "test" }.ToDictionary(split => split, split =>
        {
            var items = rows.Where(s => s.Split == split).ToArray();
            return new ReviewSplitProgress(items.Length, items.Count(s => s.Decision is "approved" or "modified"),
                items.Count(s => s.Decision == "rejected"), items.Count(s => s.Decision == "unreviewed"));
        });
        return full with { Total = rows.Count, Completed = splits.Values.Sum(s => s.Completed),
            Rejected = splits.Values.Sum(s => s.Rejected), Unreviewed = splits.Values.Sum(s => s.Unreviewed), Splits = splits };
    }

    public ReviewSession GetSession() => inner.GetSession() with { Progress = Progress(),
        ReviewDraft = inner.GetSession().ReviewDraft + "-scope50" };

    private void RequireScoped(string id)
    {
        var state = store.Snapshot;
        if (!state.Active.Any(s => s.SampleId == id && displayOrdinals.ContainsKey(s.ReviewOrdinal)) &&
            !state.Replacements.Any(r => r.OldSampleId == id && displayOrdinals.ContainsKey(r.ReviewOrdinal)))
            throw new ReviewException("outside-review-scope", 404);
    }

    public async Task<ReviewSampleDetail> GetSampleAsync(string id, CancellationToken ct)
    {
        RequireScoped(id);
        var detail = await inner.GetSampleAsync(id, ct);
        return detail with { Candidate = detail.Candidate with { ReviewOrdinal = displayOrdinals[detail.Candidate.ReviewOrdinal] } };
    }
    public Task<ReviewValidationResult> ValidateAsync(ReviewValidateRequest request, CancellationToken ct)
    { RequireScoped(request.SampleId); return inner.ValidateAsync(request, ct); }
    public async Task<ReviewSaveResponse> SaveAsync(ReviewSaveRequest request, CancellationToken ct)
    { RequireScoped(request.SampleId); return (await inner.SaveAsync(request, ct)) with { Progress = Progress() }; }
    public async Task<ReviewSaveResponse> ReplaceAsync(ReviewReplaceRequest request, CancellationToken ct)
    { RequireScoped(request.SampleId); return (await inner.ReplaceAsync(request, ct)) with { Progress = Progress() }; }
}

using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using CsDemoMap.Api.Services;
using CsDemoMap.Cli;

namespace CsDemoMap.Api.Tests;

internal static class SituationReviewServerVerifier
{
    internal static async Task VerifyAsync(CancellationToken cancellationToken)
    {
        var root = Path.Combine(Path.GetTempPath(), "review-server-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "index.html"), "<!doctype html><title>Review fixture</title>", cancellationToken);
            await File.WriteAllTextAsync(Path.Combine(root, "app.js"), "export const fixture = true;", cancellationToken);
            await File.WriteAllTextAsync(Path.Combine(root, "secret.txt"), "do-not-serve", cancellationToken);
            var backend = new FakeBackend();
            await using var server = await SituationReviewServer.StartAsync(backend, root, Path.Combine(root, "missing.webp"), cancellationToken: cancellationToken);
            Check(new Uri(server.Url).Host == "127.0.0.1" && new Uri(server.Url).Port > 0, "IPv4 dynamic binding");
            using var client = new HttpClient { BaseAddress = new Uri(server.Url) };
            var sessionResponse = await client.GetAsync("/review/session", cancellationToken);
            Check(sessionResponse.StatusCode == HttpStatusCode.OK, "session");
            Check(sessionResponse.Headers.CacheControl?.NoStore == true, "session no-store");
            Check(!sessionResponse.Headers.Contains("Access-Control-Allow-Origin"), "no CORS");
            Check(sessionResponse.Headers.GetValues("Content-Security-Policy").Single().Contains("frame-ancestors 'none'", StringComparison.Ordinal), "CSP");
            var session = await sessionResponse.Content.ReadFromJsonAsync<ReviewSession>(SituationReviewJson.Options, cancellationToken);
            var token = session!.Token!;
            Check(token.Length == 64 && !server.Url.Contains(token, StringComparison.Ordinal), "ephemeral token");
            Check((await client.GetAsync("/", cancellationToken)).StatusCode == HttpStatusCode.OK, "fixed index");
            Check((await client.GetAsync("/review-ui/app.js", cancellationToken)).StatusCode == HttpStatusCode.OK, "fixed script");
            foreach (var path in new[] { "/review-ui/secret.txt", "/workspace.json", "/review-assets/mirage.webp", "/review/samples/../secret.txt", "/review/samples/not-a-sample" })
                Check((await client.GetAsync(path, cancellationToken)).StatusCode == HttpStatusCode.NotFound, "no arbitrary resource " + path);
            Check((await client.GetAsync("/review/samples?path=secret", cancellationToken)).StatusCode == HttpStatusCode.BadRequest, "query forbidden");
            Check((await client.GetAsync("/review/samples/" + FakeBackend.Id, cancellationToken)).StatusCode == HttpStatusCode.NotFound, "unknown backend ID");
            using (var request = new HttpRequestMessage(HttpMethod.Get, "/review/session"))
            {
                request.Headers.Host = "evil.example";
                Check((await client.SendAsync(request, cancellationToken)).StatusCode == HttpStatusCode.Forbidden, "host enforced");
            }
            using (var request = new HttpRequestMessage(HttpMethod.Get, "/review/session"))
            {
                request.Headers.Add("Origin", "https://evil.example");
                Check((await client.SendAsync(request, cancellationToken)).StatusCode == HttpStatusCode.Forbidden, "cross-origin read denied");
            }
            var replace = JsonSerializer.Serialize(new ReviewReplaceRequest(FakeBackend.Id, 0, Guid.NewGuid().ToString()), SituationReviewJson.Options);
            await Post("/review/replacements", replace, HttpStatusCode.Forbidden, origin: null);
            await Post("/review/replacements", replace, HttpStatusCode.Forbidden, origin: "https://evil.example");
            await Post("/review/replacements", replace, HttpStatusCode.Forbidden, suppliedToken: "wrong");
            await Post("/review/replacements", replace, HttpStatusCode.Forbidden, suppliedToken: null);
            await Post("/review/replacements", replace, HttpStatusCode.BadRequest, contentType: "text/plain");
            await Post("/review/replacements", replace[..^1] + ",\"path\":\"secret\"}", HttpStatusCode.BadRequest);
            await Post("/review/replacements", replace[..^1] + ",\"sampleId\":\"duplicate\"}", HttpStatusCode.BadRequest);
            await Post("/review/replacements", "{}", HttpStatusCode.BadRequest);
            await Post("/review/replacements", "null", HttpStatusCode.BadRequest);
            var oversized = new string(' ', SituationReviewServer.MaximumRequestBytes + 1);
            Check(await Post("/review/replacements", oversized, HttpStatusCode.RequestEntityTooLarge) == "{\"code\":\"request-too-large\"}", "bounded Content-Length error");
            Check(await Post("/review/replacements", oversized, HttpStatusCode.RequestEntityTooLarge, chunked: true) == "{\"code\":\"request-too-large\"}", "bounded chunked error");
            Check((await client.GetAsync("/review/session", cancellationToken)).StatusCode == HttpStatusCode.OK, "host remains usable after oversized requests");
            await Post("/review/replacements", replace.Replace("\"expectedWorkspaceRevision\":0", "\"expectedWorkspaceRevision\":-1", StringComparison.Ordinal), HttpStatusCode.BadRequest);
            Check(backend.Mutations == 0, "bad requests never reach backend");
            var narrative = SituationTrainingContractVerifier.BuildRecord().Output;
            var validate = JsonSerializer.Serialize(new ReviewValidateRequest(FakeBackend.Id, narrative), SituationReviewJson.Options);
            await Post("/review/validate", validate, HttpStatusCode.OK);
            await Post("/review/validate", "{\"sampleId\":\"" + FakeBackend.Id + "\",\"narrative\":{}}", HttpStatusCode.BadRequest);
            var saveInput = new ReviewSaveRequest(FakeBackend.Id, 0, Guid.NewGuid().ToString(), "approved", new(true, true, true, false), [], [], null, null, null);
            await Post("/review/decisions", JsonSerializer.Serialize(saveInput with { Evaluations = new(null, true, true, false) }, SituationReviewJson.Options), HttpStatusCode.BadRequest);
            await Post("/review/decisions", JsonSerializer.Serialize(saveInput with { Decision = "unknown" }, SituationReviewJson.Options), HttpStatusCode.BadRequest);
            await Post("/review/decisions", JsonSerializer.Serialize(saveInput with { ExpectedRevision = -1 }, SituationReviewJson.Options), HttpStatusCode.BadRequest);
            await Post("/review/decisions", JsonSerializer.Serialize(saveInput with { RequestId = "../path" }, SituationReviewJson.Options), HttpStatusCode.BadRequest);
            await Post("/review/replacements", replace, HttpStatusCode.OK);
            Check(backend.Mutations == 1, "valid replacement reaches backend once");
            await Post("/review/decisions", JsonSerializer.Serialize(saveInput, SituationReviewJson.Options), HttpStatusCode.OK);
            Check(backend.Mutations == 2, "valid decision reaches backend once");
            backend.Failure = new ReviewException("revision-conflict", 409);
            await Post("/review/replacements", replace, HttpStatusCode.Conflict);
            backend.Failure = new IOException("PRIVATE disk path and note");
            var error = await Post("/review/replacements", replace, HttpStatusCode.ServiceUnavailable);
            Check(error == "{\"code\":\"storage-failed\"}", "exception redacted");
            backend.Failure = new ReviewException("PRIVATE/path", 503);
            Check(await Post("/review/replacements", replace, HttpStatusCode.ServiceUnavailable) == "{\"code\":\"review-error\"}", "unsafe error code redacted");
            var occupiedFailed = false;
            try { await using var occupied = await SituationReviewServer.StartAsync(backend, root, "missing", new Uri(server.Url).Port, cancellationToken); }
            catch (IOException) { occupiedFailed = true; }
            Check(occupiedFailed, "occupied explicit port fails");
            Console.WriteLine("Situation review HTTP server verification passed.");

            async Task<string> Post(string path, string body, HttpStatusCode expected, string? origin = "self", string? suppliedToken = "self", string contentType = "application/json", bool chunked = false)
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = new StringContent(body, Encoding.UTF8, contentType) };
                if (chunked) request.Headers.TransferEncodingChunked = true;
                if (origin is not null) request.Headers.Add("Origin", origin == "self" ? server.Url : origin);
                if (suppliedToken is not null) request.Headers.Add("X-Review-Token", suppliedToken == "self" ? token : suppliedToken);
                using var response = await client.SendAsync(request, cancellationToken);
                Check(response.StatusCode == expected, path + " expected " + expected + " received " + response.StatusCode);
                return await response.Content.ReadAsStringAsync(cancellationToken);
            }
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Review HTTP verification: " + message);
    }

    private sealed class FakeBackend : IReviewBackend
    {
        internal static readonly string Id = "sample-" + new string('a', 64);
        internal int Mutations { get; private set; }
        internal Exception? Failure { get; set; }
        private static readonly ReviewProgress Progress = new(1, 0, 0, 1, 0, 0, false, new Dictionary<string, ReviewSplitProgress>());
        public ReviewSession GetSession() => new(new string('a', 64), new string('b', 64), "synthetic", Progress, []);
        public IReadOnlyList<ReviewSampleSummary> GetSamples() => [new(Id, 1, "train", [], "unreviewed", 0, false)];
        public Task<ReviewSampleDetail> GetSampleAsync(string sampleId, CancellationToken cancellationToken) => throw new ReviewException("unknown-sample", 404);
        public Task<ReviewValidationResult> ValidateAsync(ReviewValidateRequest request, CancellationToken cancellationToken) => Task.FromResult(new ReviewValidationResult(true, []));
        public Task<ReviewSaveResponse> SaveAsync(ReviewSaveRequest request, CancellationToken cancellationToken) => Mutate();
        public Task<ReviewSaveResponse> ReplaceAsync(ReviewReplaceRequest request, CancellationToken cancellationToken) => Mutate();
        private Task<ReviewSaveResponse> Mutate()
        {
            if (Failure is not null) throw Failure;
            Mutations++;
            return Task.FromResult(new ReviewSaveResponse(Id, 1, null, 1, Progress));
        }
    }
}

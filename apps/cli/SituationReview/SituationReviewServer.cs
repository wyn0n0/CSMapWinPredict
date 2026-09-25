using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CsDemoMap.Api.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CsDemoMap.Cli;

/// <summary>A separate, ephemeral IPv4 loopback host. It never mounts a filesystem directory.</summary>
internal sealed class SituationReviewServer : IAsyncDisposable
{
    internal const int MaximumRequestBytes = 128 * 1024;
    private readonly WebApplication app;
    private readonly IReviewBackend backend;
    private readonly string token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    private readonly Dictionary<string, (byte[] Bytes, string Type)> assets = new(StringComparer.Ordinal);
    private static readonly Regex SamplePattern = new("^sample-[0-9a-f]{64}$", RegexOptions.CultureInvariant);
    internal string Url { get; private set; } = "";

    private SituationReviewServer(WebApplication app, IReviewBackend backend, string uiRoot, string radarPath)
    {
        this.app = app;
        this.backend = backend;
        // Read only fixed files at startup; subsequent requests cannot select local paths or follow new links.
        Add("/", Path.Combine(uiRoot, "index.html"), "text/html; charset=utf-8");
        foreach (var name in new[] { "app.js", "core.mjs", "radar.mjs", "styles.css" })
            Add("/review-ui/" + name, Path.Combine(uiRoot, name), name.EndsWith(".css", StringComparison.Ordinal) ? "text/css; charset=utf-8" : "text/javascript; charset=utf-8");
        Add("/review-assets/mirage.webp", radarPath, "image/webp");
    }

    private void Add(string route, string path, string type)
    {
        SituationReviewArtifactLoader.EnsureNoLinks(Path.GetFullPath(path));
        if (File.Exists(path)) assets.Add(route, (File.ReadAllBytes(path), type));
    }

    internal static async Task<SituationReviewServer> StartAsync(IReviewBackend backend, string uiRoot,
        string radarPath, int port = 0, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(backend);
        if (port is < 0 or > 65535) throw new ArgumentOutOfRangeException(nameof(port));
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [] });
        builder.Logging.ClearProviders(); // No default access/error logging of paths, bodies, or credentials.
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.AddServerHeader = false;
            // The bounded reader below owns the limit and its JSON error. A second Kestrel
            // limit can abort its unread-body drain after our response, truncating that response.
            options.Limits.MaxRequestBodySize = null;
            options.Listen(IPAddress.Loopback, port);
        });
        var app = builder.Build();
        try
        {
            var server = new SituationReviewServer(app, backend, uiRoot, radarPath);
            app.Run(server.HandleAsync);
            await app.StartAsync(cancellationToken);
            server.Url = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!
                .Addresses.Single().TrimEnd('/');
            return server;
        }
        catch { await app.DisposeAsync(); throw; }
    }

    internal Task WaitForShutdownAsync(CancellationToken cancellationToken = default) => app.WaitForShutdownAsync(cancellationToken);
    public async ValueTask DisposeAsync()
    {
        await app.StopAsync();
        await app.DisposeAsync();
    }

    private async Task HandleAsync(HttpContext context)
    {
        var response = context.Response;
        response.Headers.CacheControl = "no-store";
        response.Headers.ContentSecurityPolicy = "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self'; connect-src 'self'; object-src 'none'; base-uri 'none'; frame-ancestors 'none'; form-action 'self'";
        response.Headers.XContentTypeOptions = "nosniff";
        response.Headers["Referrer-Policy"] = "no-referrer";
        response.Headers["Cross-Origin-Resource-Policy"] = "same-origin";
        try
        {
            var request = context.Request;
            if (request.Host.Value != new Uri(Url).Authority) throw new ReviewException("invalid-host", 403);
            if (request.QueryString.HasValue) throw new ReviewException("invalid-request");
            var origin = request.Headers.Origin;
            if (origin.Count > 0 && (origin.Count != 1 || origin[0] != Url)) throw new ReviewException("origin-denied", 403);
            if (request.Headers["Sec-Fetch-Site"].Any(value => value is "cross-site" or "same-site"))
                throw new ReviewException("origin-denied", 403);
            var path = request.Path.Value ?? "";
            if (HttpMethods.IsGet(request.Method))
            {
                if (assets.TryGetValue(path, out var asset))
                {
                    response.ContentType = asset.Type;
                    await response.Body.WriteAsync(asset.Bytes, context.RequestAborted);
                    return;
                }
                object payload = path switch
                {
                    "/review/session" => backend.GetSession() with { Token = token },
                    "/review/samples" => backend.GetSamples(),
                    _ when path.StartsWith("/review/samples/", StringComparison.Ordinal) =>
                        await backend.GetSampleAsync(RequireSample(path["/review/samples/".Length..]), context.RequestAborted),
                    _ => throw new ReviewException("not-found", 404)
                };
                await JsonAsync(context, payload);
                return;
            }
            if (!HttpMethods.IsPost(request.Method)) throw new ReviewException("method-not-allowed", 405);
            if (origin.Count != 1 || origin[0] != Url) throw new ReviewException("origin-denied", 403);
            var suppliedToken = request.Headers["X-Review-Token"];
            if (suppliedToken.Count != 1 || !CryptographicOperations.FixedTimeEquals(
                    Encoding.UTF8.GetBytes(suppliedToken[0] ?? ""), Encoding.UTF8.GetBytes(token)))
                throw new ReviewException("token-invalid", 403);
            if (path is not ("/review/validate" or "/review/decisions" or "/review/replacements")) throw new ReviewException("not-found", 404);
            if (request.ContentType is null || !System.Net.Http.Headers.MediaTypeHeaderValue.TryParse(request.ContentType, out var contentType)
                || contentType.MediaType != "application/json" || contentType.Parameters.Any(p => p.Name != "charset" || !string.Equals(p.Value?.Trim('"'), "utf-8", StringComparison.OrdinalIgnoreCase))
                || request.Headers.ContentEncoding.Count != 0) throw new ReviewException("invalid-content-type");
            var json = await ReadBodyAsync(request);
            object result;
            if (path == "/review/validate")
            {
                var input = SituationReviewJson.Deserialize<ReviewValidateRequest>(json);
                RequireSample(input.SampleId);
                if (input.Narrative is null) throw new ReviewException("invalid-request");
                RequireNarrativeShape(json, "narrative");
                result = await backend.ValidateAsync(input, context.RequestAborted);
            }
            else if (path == "/review/decisions")
            {
                var input = SituationReviewJson.Deserialize<ReviewSaveRequest>(json);
                RequireSample(input.SampleId);
                if (input.ExpectedRevision < 0 || !Guid.TryParseExact(input.RequestId, "D", out _) || input.Decision is not ("approved" or "modified" or "rejected")
                    || input.Evaluations is null || input.Evaluations.FactsCorrect is null || input.Evaluations.FocusReasonable is null
                    || input.Evaluations.SummaryAccurateUseful is null || input.Evaluations.Hallucination is null
                    || input.IssueFields is null || input.IssueCodes is null || input.IssueFields.Any(x => x is null) || input.IssueCodes.Any(x => x is null))
                    throw new ReviewException("invalid-request");
                if (input.EditedNarrative is not null) RequireNarrativeShape(json, "editedNarrative");
                result = await backend.SaveAsync(input, context.RequestAborted);
            }
            else
            {
                var input = SituationReviewJson.Deserialize<ReviewReplaceRequest>(json);
                RequireSample(input.SampleId);
                if (input.ExpectedWorkspaceRevision < 0 || !Guid.TryParseExact(input.RequestId, "D", out _)) throw new ReviewException("invalid-request");
                result = await backend.ReplaceAsync(input, context.RequestAborted);
            }
            await JsonAsync(context, result);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { }
        catch (ReviewException error)
        {
            // Never relay exception messages or arbitrary codes from storage implementations.
            var code = Regex.IsMatch(error.Code, "^[a-z][a-z0-9-]{0,63}$", RegexOptions.CultureInvariant) ? error.Code : "review-error";
            response.StatusCode = error.StatusCode is 400 or 403 or 404 or 405 or 409 or 413 or 503 ? error.StatusCode : 400;
            await JsonAsync(context, new { code });
        }
        catch (Exception error) when (error is JsonException or InvalidDataException or DecoderFallbackException or BadHttpRequestException)
        {
            response.StatusCode = 400;
            await JsonAsync(context, new { code = "invalid-request" });
        }
        catch (Exception)
        {
            response.StatusCode = 503;
            await JsonAsync(context, new { code = "storage-failed" });
        }
    }

    private static string RequireSample(string? sampleId) => sampleId is not null && SamplePattern.IsMatch(sampleId)
        ? sampleId : throw new ReviewException("unknown-sample", 404);

    private static void RequireNarrativeShape(string json, string property)
    {
        using var document = JsonDocument.Parse(json);
        var value = document.RootElement.GetProperty(property);
        if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty("schemaVersion", out var schema) || schema.ValueKind != JsonValueKind.String
            || !value.TryGetProperty("summaryZh", out var summary) || summary.ValueKind != JsonValueKind.String
            || !value.TryGetProperty("uncertainties", out var uncertainties) || uncertainties.ValueKind != JsonValueKind.Array
            || uncertainties.EnumerateArray().Any(x => x.ValueKind != JsonValueKind.String)
            || !value.TryGetProperty("highlights", out var highlights) || highlights.ValueKind != JsonValueKind.Array)
            throw new ReviewException("invalid-request");
        foreach (var highlight in highlights.EnumerateArray())
            if (highlight.ValueKind != JsonValueKind.Object || !highlight.TryGetProperty("textZh", out var text) || text.ValueKind != JsonValueKind.String
                || !highlight.TryGetProperty("evidenceIds", out var evidence) || evidence.ValueKind != JsonValueKind.Array
                || evidence.EnumerateArray().Any(x => x.ValueKind != JsonValueKind.String)) throw new ReviewException("invalid-request");
    }

    private static async Task<string> ReadBodyAsync(HttpRequest request)
    {
        if (request.ContentLength > MaximumRequestBytes) throw new ReviewException("request-too-large", 413);
        using var body = new MemoryStream();
        var buffer = new byte[8192];
        int count;
        while ((count = await request.Body.ReadAsync(buffer, request.HttpContext.RequestAborted)) > 0)
        {
            if (body.Length + count > MaximumRequestBytes) throw new ReviewException("request-too-large", 413);
            body.Write(buffer, 0, count);
        }
        return new UTF8Encoding(false, true).GetString(body.ToArray());
    }

    private static Task JsonAsync(HttpContext context, object value) =>
        context.Response.WriteAsJsonAsync(value, SituationReviewJson.Options, context.RequestAborted);
}

using CsDemoMap.Api.Models;
using CsDemoMap.Api.Services;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;

const long MaxUploadBytes = 1024L * 1024 * 1024;

var builder = WebApplication.CreateBuilder(args);

builder.WebHost.UseUrls(builder.Configuration["urls"] ?? "http://localhost:5088");
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = MaxUploadBytes);
builder.Logging.ClearProviders();
builder.Logging.AddConsole();
builder.Services.Configure<FormOptions>(options =>
{
    options.MultipartBodyLengthLimit = MaxUploadBytes;
});
builder.Services.AddSingleton<DemoParserService>();
builder.Services.AddSingleton<DemoImportService>();
builder.Services.AddSingleton<SituationSidecarOverrideRegistry>();
builder.Services.AddSingleton(provider => new SituationSceneService(
    provider.GetRequiredService<DemoImportService>().GetSituationSource,
    provider.GetRequiredService<SituationSidecarOverrideRegistry>(),
    provider.GetRequiredService<ILogger<SituationSceneService>>(),
    SituationSceneServiceLimits.Default,
    provider.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping));
builder.Services.AddSingleton<OfflineDemoCatalog>();
builder.Services.Configure<WinInferenceOptions>(
    builder.Configuration.GetSection(WinInferenceOptions.SectionName));
builder.Services.AddSingleton<WinInferenceClient>();
builder.Services.AddSingleton<WinTimelinePredictionService>();
builder.Services.AddHostedService(provider => provider.GetRequiredService<WinInferenceClient>());
builder.Services.AddHostedService(provider => provider.GetRequiredService<DemoImportService>());
builder.Services.AddCors(options => options.AddDefaultPolicy(policy =>
    policy.WithOrigins("http://localhost:5173").AllowAnyHeader().AllowAnyMethod()));

var app = builder.Build();
app.UseCors();

app.MapGet("/api/health", (SituationSceneService _) => Results.Ok(new
{
    status = "ok",
    parser = "DemoFile.Game.Cs",
    sampleRate = DemoParserService.SampleRate,
    situationScene = "ready"
}));

app.MapGet("/api/win-model/status", (WinInferenceClient inference) =>
{
    var status = inference.GetStatus();
    return Results.Json(
        status,
        statusCode: status.Ready
            ? StatusCodes.Status200OK
            : StatusCodes.Status503ServiceUnavailable);
});

app.MapGet("/api/demos/offline", (OfflineDemoCatalog catalog) =>
{
    try
    {
        return Results.Ok(catalog.List());
    }
    catch (UnauthorizedAccessException)
    {
        return Results.Problem(
            statusCode: StatusCodes.Status403Forbidden,
            title: "无法访问离线 Demo 目录。");
    }
    catch (IOException exception)
    {
        return Results.Problem(
            statusCode: StatusCodes.Status500InternalServerError,
            title: "读取离线 Demo 目录失败。",
            detail: exception.Message);
    }
});

app.MapPost("/api/demos/offline/import", async (
    OfflineDemoImportRequest request,
    OfflineDemoCatalog catalog,
    DemoImportService imports,
    CancellationToken cancellationToken) =>
{
    try
    {
        var selection = catalog.Resolve(request.FileName);
        if (selection.File.FileSizeBytes == 0)
            return Results.BadRequest(new { error = "文件为空。" });
        if (selection.File.FileSizeBytes > MaxUploadBytes)
            return Results.BadRequest(new { error = "单文件上限为 1 GiB。" });

        var job = await imports.CreateFromFileAsync(
            selection.FullPath,
            selection.File.FileName,
            selection.File.FileSizeBytes,
            cancellationToken);
        return Results.Accepted($"/api/demos/{job.Id}/status", job);
    }
    catch (ArgumentException exception)
    {
        return Results.BadRequest(new { error = exception.Message });
    }
    catch (FileNotFoundException exception)
    {
        return Results.NotFound(new { error = exception.Message });
    }
    catch (UnauthorizedAccessException)
    {
        return Results.Problem(
            statusCode: StatusCodes.Status403Forbidden,
            title: "无法访问指定的离线 Demo。");
    }
    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
    {
        return Results.StatusCode(StatusCodes.Status499ClientClosedRequest);
    }
});

app.MapPost("/api/demos/import", async (
    IFormFile file,
    DemoImportService imports,
    CancellationToken cancellationToken) =>
{
    if (file.Length == 0)
        return Results.BadRequest(new { error = "文件为空。" });

    if (!string.Equals(Path.GetExtension(file.FileName), ".dem", StringComparison.OrdinalIgnoreCase))
        return Results.BadRequest(new { error = "只接受 .dem 文件。" });

    if (file.Length > MaxUploadBytes)
        return Results.BadRequest(new { error = "单文件上限为 1 GiB。" });

    try
    {
        await using var stream = file.OpenReadStream();
        var job = await imports.CreateAsync(stream, file.FileName, file.Length, cancellationToken);
        return Results.Accepted($"/api/demos/{job.Id}/status", job);
    }
    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
    {
        return Results.StatusCode(StatusCodes.Status499ClientClosedRequest);
    }
})
    .DisableAntiforgery()
    .WithMetadata(new RequestSizeLimitAttribute(MaxUploadBytes));

app.MapGet("/api/demos/{id}/status", (string id, DemoImportService imports) =>
{
    var status = imports.GetStatus(id);
    return status is null ? Results.NotFound(new { error = "找不到导入任务。" }) : Results.Ok(status);
});

app.MapGet("/api/demos/{id}/windows/{index:int}", (
    string id,
    int index,
    HttpContext context,
    DemoImportService imports) =>
{
    var path = imports.GetWindowPath(id, index);
    if (path is null)
        return Results.NotFound(new { error = "窗口尚未生成或不存在。" });

    context.Response.Headers.ContentEncoding = "br";
    context.Response.Headers.CacheControl = "public, max-age=31536000, immutable";
    return Results.File(path, "application/json", enableRangeProcessing: false);
});

app.Run();

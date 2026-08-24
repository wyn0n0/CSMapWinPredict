using CsDemoMap.Api.Services;
using DemoFile;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using System.Diagnostics;
using System.Text.Json;

const long MaxUploadBytes = 1024L * 1024 * 1024;

if (args is ["--inspect-demo", var demoPath])
{
    await InspectDemoAsync(demoPath);
    return;
}

var builder = WebApplication.CreateBuilder(args);

builder.WebHost.UseUrls("http://localhost:5088");
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = MaxUploadBytes);
builder.Logging.ClearProviders();
builder.Logging.AddConsole();
builder.Services.Configure<FormOptions>(options =>
{
    options.MultipartBodyLengthLimit = MaxUploadBytes;
});
builder.Services.AddSingleton<DemoParserService>();
builder.Services.AddSingleton<DemoImportService>();
builder.Services.AddHostedService(provider => provider.GetRequiredService<DemoImportService>());
builder.Services.AddCors(options => options.AddDefaultPolicy(policy =>
    policy.WithOrigins("http://localhost:5173").AllowAnyHeader().AllowAnyMethod()));

var app = builder.Build();
app.UseCors();

app.MapGet("/api/health", () => Results.Ok(new
{
    status = "ok",
    parser = "DemoFile.Game.Cs",
    sampleRate = DemoParserService.SampleRate
}));

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

static async Task InspectDemoAsync(string demoPath)
{
    if (!File.Exists(demoPath))
        throw new FileNotFoundException("找不到 demo 文件。", demoPath);

    var stopwatch = Stopwatch.StartNew();
    await using var stream = File.OpenRead(demoPath);
    var timeline = await new DemoParserService().ParseAsync(
        stream,
        Path.GetFileName(demoPath),
        CancellationToken.None);
    stopwatch.Stop();

    var snapshots = timeline.Frames.SelectMany(frame => frame.Players).ToArray();
    var positioned = snapshots.Where(player =>
        float.IsFinite(player.X) && float.IsFinite(player.Y) && float.IsFinite(player.Z)).ToArray();
    var trajectoryPoints = timeline.UtilityTracks.SelectMany(track => track.Trajectory).ToArray();
    var effectSamples = timeline.UtilityEffects.SelectMany(effect => effect.Samples).ToArray();
    var fireAreaPoints = timeline.UtilityEffects
        .Where(effect => effect.Type == "fire")
        .SelectMany(effect => effect.Samples)
        .SelectMany(sample => sample.Area)
        .ToArray();
    var equipmentStates = timeline.PlayerEquipmentStates.ToArray();
    var roundStates = timeline.Frames.Select(frame => frame.Round).ToArray();
    var bombStates = timeline.Frames.Select(frame => frame.Bomb).ToArray();

    var summary = new
    {
        timeline.Metadata,
        ParseElapsedSeconds = stopwatch.Elapsed.TotalSeconds,
        FrameCount = timeline.Frames.Count,
        SnapshotCount = snapshots.Length,
        PositionedSnapshotCount = positioned.Length,
        MovingSnapshotCount = snapshots.Count(player =>
            player.VelocityX != 0 || player.VelocityY != 0 || player.VelocityZ != 0),
        MaxPlayerSpeed = snapshots.Length == 0 ? 0 : snapshots.Max(player => Math.Sqrt(
            player.VelocityX * player.VelocityX +
            player.VelocityY * player.VelocityY +
            player.VelocityZ * player.VelocityZ)),
        UtilityTracks = timeline.UtilityTracks
            .GroupBy(track => track.Type)
            .ToDictionary(
                group => group.Key,
                group => new
                {
                    Tracks = group.Count(),
                    Points = group.Sum(track => track.Trajectory.Count),
                    LinkedThrowers = group.Count(track => track.ThrowerId is not null)
                }),
        UtilityEffects = timeline.UtilityEffects
            .GroupBy(effect => effect.Type)
            .ToDictionary(
                group => group.Key,
                group => new
                {
                    Effects = group.Count(),
                    Samples = group.Sum(effect => effect.Samples.Count),
                    AreaPoints = group.SelectMany(effect => effect.Samples).Sum(sample => sample.Area.Count)
                }),
        PlayerUtilityStateCount = timeline.PlayerUtilityStates.Count,
        PlayerEquipmentStateCount = equipmentStates.Length,
        EquipmentItems = equipmentStates.Sum(state => state.Items.Count),
        Economy = equipmentStates.Length == 0 ? null : new
        {
            MinMoney = equipmentStates.Min(state => state.Money),
            MaxMoney = equipmentStates.Max(state => state.Money),
            MaxEquipmentValue = equipmentStates.Max(state => state.CurrentEquipmentValue),
            Players = equipmentStates.Select(state => state.PlayerId).Distinct().Count()
        },
        Rounds = roundStates.Select(state => state.Number).Distinct().Order().ToArray(),
        RoundPhases = roundStates.GroupBy(state => state.Phase).ToDictionary(group => group.Key, group => group.Count()),
        BombStates = bombStates.GroupBy(state => state.State).ToDictionary(group => group.Key, group => group.Count()),
        BombSites = bombStates.Where(state => state.Site is not null).GroupBy(state => state.Site!).ToDictionary(group => group.Key, group => group.Count()),
        BombCarriers = bombStates.Where(state => state.CarrierId is not null).Select(state => state.CarrierId).Distinct().Count(),
        MapRegions = timeline.Frames.SelectMany(frame => frame.Zones).Select(zone => zone.Region).Distinct().Order().ToArray(),
        PlayersWithUtility = timeline.PlayerUtilityStates
            .Where(state => state.Items.Count > 0)
            .Select(state => state.PlayerId)
            .Distinct()
            .Count(),
        TrajectoryBounds = Bounds(trajectoryPoints.Select(point => (point.X, point.Y, point.Z))),
        EffectBounds = Bounds(effectSamples.Select(point => (point.X, point.Y, point.Z))),
        FireAreaBounds = Bounds(fireAreaPoints.Select(point => (point.X, point.Y, point.Z))),
        Players = snapshots
            .GroupBy(player => new { player.Id, player.Name, player.Team })
            .Select(group => new
            {
                group.Key.Id,
                group.Key.Name,
                group.Key.Team,
                Snapshots = group.Count()
            })
            .OrderBy(player => player.Team)
            .ThenBy(player => player.Name),
        CoordinateBounds = positioned.Length == 0 ? null : new
        {
            MinX = positioned.Min(player => player.X),
            MaxX = positioned.Max(player => player.X),
            MinY = positioned.Min(player => player.Y),
            MaxY = positioned.Max(player => player.Y),
            MinZ = positioned.Min(player => player.Z),
            MaxZ = positioned.Max(player => player.Z)
        },
        FirstFrame = timeline.Frames.FirstOrDefault(),
        LastFrame = timeline.Frames.LastOrDefault(),
        EventCounts = timeline.Events
            .GroupBy(item => item.Type)
            .ToDictionary(group => group.Key, group => group.Count())
    };

    Console.WriteLine(JsonSerializer.Serialize(summary, new JsonSerializerOptions
    {
        WriteIndented = true
    }));

    static object? Bounds(IEnumerable<(float X, float Y, float Z)> values)
    {
        var points = values.Where(point =>
            float.IsFinite(point.X) && float.IsFinite(point.Y) && float.IsFinite(point.Z)).ToArray();
        return points.Length == 0 ? null : new
        {
            MinX = points.Min(point => point.X),
            MaxX = points.Max(point => point.X),
            MinY = points.Min(point => point.Y),
            MaxY = points.Max(point => point.Y),
            MinZ = points.Min(point => point.Z),
            MaxZ = points.Max(point => point.Z)
        };
    }
}

using System.Text.Json.Serialization;
using Bast.Application;
using Bast.Application.Persistence;
using Bast.Application.Publishing;
using Bast.Connectors.Meta;
using Bast.Connectors.YouTube;
using Bast.Domain;
using Bast.Infrastructure;
using Bast.Infrastructure.Media;
using Bast.Notifications.Telegram;
using Bast.Service;
using Microsoft.EntityFrameworkCore;

try
{
    // Acentos legíveis ao rodar em console. Como serviço do Windows não há console, e a troca falha sem consequência.
    Console.OutputEncoding = System.Text.Encoding.UTF8;
}
catch (IOException)
{
}

var builder = WebApplication.CreateBuilder(args);

// Configuração específica desta máquina (pasta principal, caminhos do ffmpeg). Não vai para o repositório.
builder.Configuration.AddJsonFile(Path.Combine(builder.Environment.ContentRootPath, "appsettings.Local.json"), optional: true, reloadOnChange: false);
builder.Configuration.AddEnvironmentVariables();

builder.Host.UseWindowsService(o => o.ServiceName = "BastSocialPublisher");
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddBast(builder.Configuration);
builder.Services.AddYouTubeConnector(builder.Configuration);
builder.Services.AddMetaConnectors(builder.Configuration);
builder.Services.AddHostedService<StartupService>();
builder.Services.AddHostedService<ImportWorker>();
builder.Services.AddTelegramNotifications();
builder.Services.AddHostedService<PublishingWorker>();
builder.Services.AddHostedService<NotificationWorker>();

var app = builder.Build();

app.MapGet("/api/health", async (IDbContextFactory<BastDbContext> factory, Workspace workspace, MediaTools tools,
    IConnectorRegistry registry, SchedulingService scheduling, CancellationToken ct) =>
{
    await using var db = await factory.CreateDbContextAsync(ct);
    var databaseOk = await db.Database.CanConnectAsync(ct);
    var settings = await scheduling.GetSettingsAsync(ct);
    return Results.Ok(new
    {
        status = databaseOk && tools.Ffprobe is not null && tools.Ffmpeg is not null ? "ok" : "degraded",
        rootFolder = workspace.Root,
        database = databaseOk,
        ffprobe = tools.Ffprobe is not null,
        ffmpeg = tools.Ffmpeg is not null,
        simulationMode = registry.SimulationMode,
        connectedPlatforms = registry.Available.Order(),
        paused = settings.Paused,
    });
});

app.MapGet("/api/videos", async (IDbContextFactory<BastDbContext> factory, CancellationToken ct) =>
{
    await using var db = await factory.CreateDbContextAsync(ct);
    var videos = await db.Videos.AsNoTracking().ToListAsync(ct);
    return videos
        .OrderBy(v => v.FolderName, NaturalStringComparer.Instance)
        .ThenBy(v => v.Generation)
        .Select(v => new
        {
            v.Id,
            v.FolderName,
            v.Generation,
            v.State,
            folder = v.CurrentFolder,
            v.RelativePath,
            v.ContentHash,
            v.DurationSeconds,
            v.Width,
            v.Height,
            v.VideoCodec,
            v.AudioCodec,
            v.FileSizeBytes,
            v.ScheduledAtUtc,
            v.ImportedAtUtc,
            v.Note,
        });
});

app.MapGet("/api/import-issues", async (IDbContextFactory<BastDbContext> factory, bool? all, CancellationToken ct) =>
{
    await using var db = await factory.CreateDbContextAsync(ct);
    var query = db.ImportIssues.AsNoTracking();
    if (all != true) query = query.Where(i => i.ResolvedAtUtc == null);
    return await query.OrderByDescending(i => i.LastSeenAtUtc).ToListAsync(ct);
});

// Fila: vídeos com publicações, na ordem em que serão publicados, com o estado de cada plataforma.
app.MapGet("/api/queue", async (IDbContextFactory<BastDbContext> factory, CancellationToken ct) =>
{
    await using var db = await factory.CreateDbContextAsync(ct);
    var videos = await db.Videos.AsNoTracking().Include(v => v.Publications)
        .Where(v => v.State != VideoState.Importing)
        .ToListAsync(ct);
    return videos
        .OrderBy(v => v.ScheduledAtUtc ?? DateTime.MaxValue)
        .ThenBy(v => v.FolderName, NaturalStringComparer.Instance)
        .Select(v => new
        {
            v.Id,
            v.FolderName,
            v.State,
            folder = v.CurrentFolder,
            v.ScheduledAtUtc,
            youTubeTitle = CaptionRules.YouTubeTitle(v.CaptionText),
            publications = v.Publications.OrderBy(p => p.Platform).Select(p => new
            {
                p.Id,
                p.Platform,
                p.State,
                p.ScheduledAtUtc,
                p.StartedAtUtc,
                p.CompletedAtUtc,
                p.NextRetryAtUtc,
                p.AttemptCount,
                p.LastError,
                p.ExternalId,
                p.ExternalUrl,
                p.Simulated,
            }),
        });
});

app.MapGet("/api/settings/schedule", async (SchedulingService scheduling, CancellationToken ct) =>
    ToResponse(await scheduling.GetSettingsAsync(ct)));

app.MapPut("/api/settings/schedule", async (ScheduleSettingsInput input, QueueCommands commands, SchedulingService scheduling, CancellationToken ct) =>
{
    var problems = await commands.UpdateSettingsAsync(input, ct);
    return problems.Count > 0
        ? Results.BadRequest(new { problems })
        : Results.Ok(ToResponse(await scheduling.GetSettingsAsync(ct)));
});

app.MapPost("/api/queue/pause", async (QueueCommands commands, CancellationToken ct) =>
{
    await commands.SetPausedAsync(true, ct);
    return Results.Ok(new { paused = true });
});

app.MapPost("/api/queue/resume", async (QueueCommands commands, CancellationToken ct) =>
{
    await commands.SetPausedAsync(false, ct);
    return Results.Ok(new { paused = false });
});

app.MapPost("/api/videos/{id:guid}/cancel", async (Guid id, QueueCommands commands, CancellationToken ct) =>
{
    var result = await commands.CancelVideoAsync(id, ct);
    return result is null
        ? Results.NotFound()
        : Results.Ok(new { cancelled = result.Value.Cancelled, notCancellable = result.Value.NotCancellable });
});

// Contas: situação de cada rede. Tokens e segredos nunca são devolvidos pela API.
app.MapGet("/api/accounts", async (AccountService accounts, IConnectorRegistry registry, GoogleAuth google, MetaAuth meta, CancellationToken ct) =>
{
    var stored = (await accounts.GetAllAsync(ct)).ToDictionary(a => a.Platform);
    return Enum.GetValues<Platform>().Select(platform =>
    {
        stored.TryGetValue(platform, out var account);
        return new
        {
            platform,
            status = account?.Status ?? AccountStatus.Disconnected,
            account?.DisplayName,
            account?.ExternalAccountId,
            account?.ConnectedAtUtc,
            account?.LastError,
            available = registry.Available.Contains(platform),
            integrationImplemented = platform is Platform.YouTube or Platform.Facebook or Platform.Instagram,
            hasClientCredentials = platform switch
            {
                Platform.YouTube => google.HasClientCredentials,
                Platform.Facebook or Platform.Instagram => meta.HasAppCredentials,
                _ => (bool?)null,
            },
        };
    });
});

app.MapPut("/api/connect/youtube/credentials", (GoogleClientInput input, GoogleAuth google) =>
{
    if (string.IsNullOrWhiteSpace(input.ClientId) || string.IsNullOrWhiteSpace(input.ClientSecret))
    {
        return Results.BadRequest(new { problems = new[] { "Informe clientId e clientSecret." } });
    }

    google.SaveClientCredentials(input.ClientId, input.ClientSecret);
    return Results.Ok(new { saved = true });
});

app.MapPost("/api/connect/youtube/start", (GoogleAuth google) =>
{
    try
    {
        return Results.Ok(new { authorizationUrl = google.BeginAuthorization() });
    }
    catch (InvalidOperationException ex)
    {
        return Results.BadRequest(new { problems = new[] { ex.Message } });
    }
});

app.MapGet("/api/connect/youtube/status", (GoogleAuth google) =>
    Results.Ok(new { connected = google.IsConnected, hasClientCredentials = google.HasClientCredentials, flow = google.FlowStatus }));

app.MapDelete("/api/connect/youtube", async (GoogleAuth google, CancellationToken ct) =>
{
    await google.DisconnectAsync(ct);
    return Results.Ok(new { connected = false });
});

// Meta (Facebook e Instagram): o token é gerado pelo usuário no Explorador da Graph API e enviado por script.
app.MapPut("/api/connect/meta/credentials", (MetaAppInput input, MetaAuth meta) =>
{
    if (string.IsNullOrWhiteSpace(input.AppId) || string.IsNullOrWhiteSpace(input.AppSecret))
    {
        return Results.BadRequest(new { problems = new[] { "Informe appId e appSecret." } });
    }

    meta.SaveAppCredentials(input.AppId, input.AppSecret);
    return Results.Ok(new { saved = true });
});

app.MapPost("/api/connect/meta/token", async (MetaTokenInput input, MetaAuth meta, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(input.UserToken))
    {
        return Results.BadRequest(new { problems = new[] { "Informe userToken." } });
    }

    try
    {
        var result = await meta.ConnectAsync(input.UserToken, string.IsNullOrWhiteSpace(input.PageId) ? null : input.PageId, ct);
        return Results.Ok(new { result.Connected, result.Message, result.Pages });
    }
    catch (HttpRequestException ex)
    {
        return Results.Ok(new { connected = false, message = $"Sem conexão com a Meta: {ex.Message}", pages = Array.Empty<MetaPage>() });
    }
});

app.MapGet("/api/connect/meta/status", (MetaAuth meta) => Results.Ok(new
{
    hasAppCredentials = meta.HasAppCredentials,
    facebookConnected = meta.Connection is not null,
    instagramConnected = meta.Connection?.InstagramUserId is not null,
    page = meta.Connection?.PageName,
    instagram = meta.Connection?.InstagramUsername,
}));

app.MapDelete("/api/connect/meta", async (MetaAuth meta, CancellationToken ct) =>
{
    await meta.DisconnectAsync(ct);
    return Results.Ok(new { connected = false });
});

// Telegram: avisos por um bot do próprio usuário. O token nunca é devolvido pela API.
app.MapGet("/api/connect/telegram/status", (TelegramBot telegram) => Results.Ok(telegram.Status));

app.MapPut("/api/connect/telegram/token", (TelegramTokenInput input, TelegramBot telegram, CancellationToken ct) =>
    string.IsNullOrWhiteSpace(input.Token)
        ? Task.FromResult(Results.BadRequest(new { problems = new[] { "Informe token." } }))
        : TelegramCallAsync(() => telegram.SaveTokenAsync(input.Token, ct)));

app.MapPost("/api/connect/telegram/link", (TelegramLinkInput input, TelegramBot telegram, CancellationToken ct) =>
    string.IsNullOrWhiteSpace(input.Code)
        ? Task.FromResult(Results.BadRequest(new { problems = new[] { "Informe code." } }))
        : TelegramCallAsync(() => telegram.LinkAsync(input.Code, ct)));

app.MapPost("/api/connect/telegram/test", (TelegramBot telegram, CancellationToken ct) =>
    TelegramCallAsync(async () =>
    {
        var result = await telegram.SendAsync("🔔 Aviso de teste do BAST SOCIAL PUBLISHER.", ct);
        return new TelegramResult(result.Status == ChannelSendStatus.Sent, result.Error ?? "Aviso de teste enviado.");
    }));

app.MapDelete("/api/connect/telegram", (TelegramBot telegram) =>
{
    telegram.Disconnect();
    return Results.Ok(new { connected = false });
});

// Avisos mais recentes e a situação de cada um (enviado, aguardando, descartado).
app.MapGet("/api/notifications", async (IDbContextFactory<BastDbContext> factory, CancellationToken ct) =>
{
    await using var db = await factory.CreateDbContextAsync(ct);
    return await db.Notifications.AsNoTracking().OrderByDescending(n => n.CreatedAtUtc).Take(50)
        .Select(n => new { n.Id, n.Kind, n.Text, n.CreatedAtUtc, n.SentAtUtc, n.DiscardedAtUtc, n.Attempts, n.NextAttemptAtUtc, n.LastError })
        .ToListAsync(ct);
});

app.Run();

static async Task<IResult> TelegramCallAsync(Func<Task<TelegramResult>> call)
{
    try
    {
        var result = await call();
        return Results.Ok(new { ok = result.Ok, message = result.Message });
    }
    catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException)
    {
        // Sem a mensagem da exceção: ela pode citar o endereço do pedido, que contém o token do bot.
        return Results.Ok(new { ok = false, message = "Sem conexão com o Telegram. Tente de novo." });
    }
}

static object ToResponse(ScheduleSettings s) => new
{
    startTime = s.StartTime.ToString("HH:mm"),
    endTime = s.EndTime.ToString("HH:mm"),
    s.IntervalMinutes,
    s.MaxPerDay,
    days = s.Days,
    s.TimeZoneId,
    platforms = s.Platforms,
    s.MissedPolicy,
    s.MissedGraceMinutes,
    s.MaxAttempts,
    s.Paused,
    s.UpdatedAtUtc,
};

public sealed record GoogleClientInput(string ClientId, string ClientSecret);
public sealed record MetaAppInput(string AppId, string AppSecret);
public sealed record MetaTokenInput(string UserToken, string? PageId);
public sealed record TelegramTokenInput(string Token);
public sealed record TelegramLinkInput(string Code);

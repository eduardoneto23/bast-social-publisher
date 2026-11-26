using System.Text.Json.Serialization;
using Bast.Application;
using Bast.Application.Persistence;
using Bast.Application.Production;
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
builder.Services.AddHostedService<ThreadsTokenWorker>();
builder.Services.AddHostedService<TelegramButtonsWorker>();
builder.Services.AddHostedService<ProductionWorker>();

var app = builder.Build();

// Recusa pedidos que não partiram do próprio painel nem de um script local (ver LocalRequestGuard).
app.Use(async (context, next) =>
{
    var headers = context.Request.Headers;
    if (!LocalRequestGuard.IsAllowed(context.Request.Method, headers.Host.ToString(),
            headers.TryGetValue("Origin", out var origin) ? origin.ToString() : null,
            headers.TryGetValue("Sec-Fetch-Site", out var site) ? site.ToString() : null))
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        await context.Response.WriteAsJsonAsync(new { problems = new[] { "Pedido recusado: a API só aceita o painel local e scripts deste computador." } });
        return;
    }

    // O painel não carrega nada de fora e não pode ser embutido em outra página.
    context.Response.Headers["Content-Security-Policy"] = "default-src 'self'; frame-ancestors 'none'; base-uri 'none'; form-action 'self'";
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["Referrer-Policy"] = "no-referrer";
    await next();
});

// Painel: página estática em wwwroot, servida em http://127.0.0.1:5080/
app.UseDefaultFiles();
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = file => file.Context.Response.Headers.CacheControl = "no-cache",
});

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
            v.Lane,
            folder = v.CurrentFolder,
            v.ScheduledAtUtc,
            v.Width,
            v.Height,
            hasThumbnail = v.ThumbnailFileName != null,
            awaitingManual = v.AwaitsManual,
            v.ManualPendingSinceUtc,
            v.ManualDoneAtUtc,
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
                p.Warning,
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

// O usuário postou à mão nas outras redes: a pasta sai de PENDENTE MANUAL e vai para ENVIADOS.
app.MapPost("/api/videos/{id:guid}/manual-done", async (Guid id, QueueCommands commands, CancellationToken ct) =>
{
    var done = await commands.ConfirmManualAsync(id, ct);
    return done switch
    {
        null => Results.NotFound(),
        true => Results.Ok(new { message = "Envio manual confirmado. A pasta vai para ENVIADOS." }),
        false => Results.BadRequest(new { problems = new[] { "Este vídeo não está aguardando o envio manual." } }),
    };
});

// Vídeo em erro: refaz as redes que falharam no envio.
app.MapPost("/api/videos/{id:guid}/retry", async (Guid id, QueueCommands commands, CancellationToken ct) =>
{
    var retried = await commands.RetryFailedAsync(id, ct);
    return retried is null ? Results.NotFound() : Results.Ok(new { publications = retried.Value });
});

// Vídeo em erro que já saiu em alguma rede: vai para PENDENTE MANUAL para o usuário postar onde falhou.
app.MapPost("/api/videos/{id:guid}/to-manual", async (Guid id, QueueCommands commands, CancellationToken ct) =>
{
    var moved = await commands.SendToManualAsync(id, ct);
    return moved switch
    {
        null => Results.NotFound(),
        true => Results.Ok(new { message = "O vídeo foi para 07 - PENDENTE MANUAL." }),
        false => Results.BadRequest(new { problems = new[] { "Só vídeos em erro que já saíram em alguma rede vão para o envio manual." } }),
    };
});

app.MapPost("/api/videos/{id:guid}/publish-now", async (Guid id, QueueCommands commands, CancellationToken ct) =>
{
    var moved = await commands.PublishNowAsync(id, ct);
    return moved is null ? Results.NotFound() : Results.Ok(new { publications = moved.Value });
});

// Horário escolhido pelo usuário, no fuso da agenda ("2026-11-20T14:30"). Vale nos dois modos.
app.MapPost("/api/videos/{id:guid}/time", async (Guid id, VideoTimeInput input, SchedulingService scheduling, CancellationToken ct) =>
{
    if (!DateTime.TryParseExact(input.LocalTime, ["yyyy-MM-dd'T'HH:mm", "yyyy-MM-dd'T'HH:mm:ss", "yyyy-MM-dd HH:mm"],
            System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var local))
    {
        return Results.BadRequest(new { problems = new[] { "Informe data e hora no formato AAAA-MM-DDTHH:mm." } });
    }

    var result = await scheduling.SetTimeAsync(id, local, ct);
    if (result.NotFound) return Results.NotFound();
    return result.Ok
        ? Results.Ok(new { message = result.Message, result.ScheduledAtUtc })
        : Results.BadRequest(new { problems = new[] { result.Message } });
});

// Publicação assistida (TikTok): o usuário avisa pelo painel que publicou, ou que não vai publicar.
app.MapPost("/api/publications/{id:guid}/manual", async (Guid id, ManualAnswerInput input, ManualPublishing manual, CancellationToken ct) =>
{
    var (ok, message) = await manual.AnswerAsync(id, input.Published, ct);
    return Results.Ok(new { ok, message });
});

// Contas: situação de cada rede. Tokens e segredos nunca são devolvidos pela API.
app.MapGet("/api/accounts", async (AccountService accounts, IConnectorRegistry registry, GoogleAuth google, MetaAuth meta, ThreadsAuth threads,
    TelegramBot telegram, CancellationToken ct) =>
{
    var stored = (await accounts.GetAllAsync(ct)).ToDictionary(a => a.Platform);
    return Enum.GetValues<Platform>().Select(platform =>
    {
        stored.TryGetValue(platform, out var account);
        // TikTok é assistido: não há conta conectada, só o Telegram para entregar o vídeo.
        var tiktok = platform == Platform.TikTok;
        return new
        {
            platform,
            status = tiktok ? (telegram.IsConfigured ? AccountStatus.Connected : AccountStatus.Disconnected) : account?.Status ?? AccountStatus.Disconnected,
            DisplayName = tiktok ? (telegram.IsConfigured ? "Modo assistido pelo Telegram" : null) : account?.DisplayName,
            account?.ExternalAccountId,
            account?.ConnectedAtUtc,
            account?.LastError,
            available = registry.Available.Contains(platform),
            integrationImplemented = true,
            assisted = tiktok,
            hasClientCredentials = platform switch
            {
                Platform.YouTube => google.HasClientCredentials,
                Platform.Facebook or Platform.Instagram => meta.HasAppCredentials,
                Platform.Threads => threads.HasAppCredentials,
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

// Threads: login OAuth oficial. O script abre a autorização e devolve o endereço de retorno colado pelo usuário.
app.MapPut("/api/connect/threads/credentials", (ThreadsAppInput input, ThreadsAuth threads) =>
{
    if (string.IsNullOrWhiteSpace(input.AppId) || string.IsNullOrWhiteSpace(input.AppSecret) || string.IsNullOrWhiteSpace(input.RedirectUri))
    {
        return Results.BadRequest(new { problems = new[] { "Informe appId, appSecret e redirectUri." } });
    }

    var problem = threads.SaveAppCredentials(input.AppId, input.AppSecret, input.RedirectUri);
    return problem is null ? Results.Ok(new { saved = true }) : Results.BadRequest(new { problems = new[] { problem } });
});

app.MapPost("/api/connect/threads/start", (ThreadsAuth threads) =>
{
    var result = threads.Start();
    return Results.Ok(new { ok = result.Ok, message = result.Message, authorizeUrl = result.AuthorizeUrl });
});

app.MapPost("/api/connect/threads/code", async (ThreadsCodeInput input, ThreadsAuth threads, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(input.RedirectedUrl))
    {
        return Results.BadRequest(new { problems = new[] { "Informe redirectedUrl." } });
    }

    try
    {
        var result = await threads.ConnectAsync(input.RedirectedUrl, ct);
        return Results.Ok(new { ok = result.Ok, message = result.Message });
    }
    catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException)
    {
        // Sem a mensagem da exceção: os pedidos do Threads levam o token no endereço.
        return Results.Ok(new { ok = false, message = "Sem conexão com o Threads. Tente de novo." });
    }
});

app.MapGet("/api/connect/threads/status", (ThreadsAuth threads) => Results.Ok(new
{
    hasAppCredentials = threads.HasAppCredentials,
    redirectUri = threads.RedirectUri,
    connected = threads.Connection is not null,
    username = threads.Connection?.Username,
    tokenExpiresAtUtc = threads.Connection?.ExpiresAtUtc,
}));

app.MapDelete("/api/connect/threads", async (ThreadsAuth threads, CancellationToken ct) =>
{
    await threads.DisconnectAsync(ct);
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

// ---------- Produção: gravações do OBS viram jobs com etapas ----------

app.MapGet("/api/production", async (IDbContextFactory<BastDbContext> factory, Bast.Application.Production.RecordingScanner scanner,
    Bast.Application.Production.ProductionRunner runner, Bast.Application.Production.IDiskSpace disk, BastOptions options, CancellationToken ct) =>
{
    await using var db = await factory.CreateDbContextAsync(ct);
    var control = await db.ProductionControl.AsNoTracking().FirstOrDefaultAsync(c => c.Id == 1, ct);
    var jobs = await db.ProductionJobs.AsNoTracking().Include(j => j.Steps)
        .OrderByDescending(j => j.ReceivedAtUtc).Take(50).ToListAsync(ct);
    var free = string.IsNullOrWhiteSpace(options.ProductionWorkFolder) ? null : disk.FreeBytes(options.ProductionWorkFolder);
    const double gb = Bast.Application.Production.ProductionSpace.Gb;
    return Results.Ok(new
    {
        stopped = control?.Stopped ?? false,
        stopReason = control?.StopReason,
        stoppedAtUtc = control?.StoppedAtUtc,
        runningJobId = runner.RunningJobId,
        recordingsFolder = scanner.Folder,
        disk = new
        {
            workFolder = options.ProductionWorkFolder,
            configured = !string.IsNullOrWhiteSpace(options.ProductionWorkFolder),
            freeGb = free is null ? (double?)null : Math.Round(free.Value / gb, 1),
            reserveGb = options.ProductionReserveGb,
        },
        recordings = scanner.LastScan,
        jobs = jobs.Select(j => new
        {
            j.Id,
            j.SourceFileName,
            j.ReceivedAtUtc,
            j.DurationSeconds,
            j.Width,
            j.Height,
            j.Fps,
            sizeGb = Math.Round(j.SizeBytes / gb, 1),
            j.AudioTracks,
            j.Status,
            j.CurrentStep,
            j.Progress,
            j.LastError,
            j.Authorized,
            j.AuthorizedAtUtc,
            j.Paused,
            j.BlockedReason,
            j.WorkFolder,
            j.CompletedAtUtc,
            steps = j.Steps.OrderBy(s => s.Order).Select(s => new { s.Name, s.State, s.Attempts, s.StartedAtUtc, s.FinishedAtUtc, s.Output, s.Error }),
        }),
    });
});

app.MapGet("/api/production/jobs/{id:guid}/logs", async (Guid id, IDbContextFactory<BastDbContext> factory, CancellationToken ct) =>
{
    await using var db = await factory.CreateDbContextAsync(ct);
    return await db.ProductionLogs.AsNoTracking().Where(l => l.JobId == id).OrderByDescending(l => l.Id).Take(300)
        .Select(l => new { l.AtUtc, l.Level, l.Step, l.Message, l.Program, l.File })
        .ToListAsync(ct);
});

app.MapPost("/api/production/jobs/{id:guid}/{action}", async (Guid id, string action, Bast.Application.Production.ProductionCommands commands, CancellationToken ct) =>
{
    (Bast.Application.Production.CommandResult Result, string Message) outcome = action switch
    {
        "authorize" => await commands.AuthorizeAsync(id, ct),
        "pause" => await commands.PauseAsync(id, ct),
        "resume" => await commands.ResumeAsync(id, ct),
        "cancel" => await commands.CancelAsync(id, ct),
        _ => (Bast.Application.Production.CommandResult.NotFound, "Ação desconhecida."),
    };
    return outcome.Result switch
    {
        Bast.Application.Production.CommandResult.Done => Results.Ok(new { message = outcome.Message }),
        Bast.Application.Production.CommandResult.NotFound => Results.NotFound(new { problems = new[] { outcome.Message } }),
        _ => Results.BadRequest(new { problems = new[] { outcome.Message } }),
    };
});

// Entregas do job para revisar no painel: só arquivos de vídeo, imagem e relatório das pastas de entrega
// (horizontal, verticais, thumbnails, relatório), até dois níveis. Nada fora da pasta de trabalho do job.
app.MapGet("/api/production/jobs/{id:guid}/files", async (Guid id, IDbContextFactory<BastDbContext> factory, CancellationToken ct) =>
{
    await using var db = await factory.CreateDbContextAsync(ct);
    var work = await db.ProductionJobs.AsNoTracking().Where(j => j.Id == id).Select(j => j.WorkFolder).FirstOrDefaultAsync(ct);
    if (work is null || !Directory.Exists(work)) return Results.Ok(Array.Empty<object>());
    var files = ProductionFiles.Deliverables(work)
        .Select(f => new
        {
            path = Path.GetRelativePath(work, f.FullName).Replace('\\', '/'),
            sizeKb = f.Length / 1024,
            kind = ProductionFiles.KindOf(f.Extension),
            modifiedAtUtc = f.LastWriteTimeUtc,
        });
    return Results.Ok(files);
});

app.MapGet("/api/production/jobs/{id:guid}/file", async (Guid id, string path, IDbContextFactory<BastDbContext> factory, CancellationToken ct) =>
{
    await using var db = await factory.CreateDbContextAsync(ct);
    var work = await db.ProductionJobs.AsNoTracking().Where(j => j.Id == id).Select(j => j.WorkFolder).FirstOrDefaultAsync(ct);
    if (work is null) return Results.NotFound();
    var full = ProductionFiles.Resolve(work, path);
    if (full is null) return Results.NotFound();
    return Results.File(full, ProductionFiles.ContentType(Path.GetExtension(full)), enableRangeProcessing: true);
});

// ---------- Radar de Conteúdo e Estúdio de Ideias ----------

app.MapGet("/api/radar", async (IDbContextFactory<BastDbContext> factory, Bast.Application.Radar.RadarService radar,
    Bast.Application.Radar.StudioService studio, SchedulingService scheduling, Bast.Application.Radar.IYouTubeResearch youtube, CancellationToken ct) =>
{
    var tz = (await scheduling.GetSettingsAsync(ct)).TimeZoneId;
    var due = await radar.DueAsync(tz, ct);
    await using var db = await factory.CreateDbContextAsync(ct);
    var settings = await db.RadarSettings.AsNoTracking().FirstAsync(s => s.Id == 1, ct);
    var tags = await db.RadarTags.AsNoTracking().OrderBy(t => t.Name).ToListAsync(ct);
    var runs = await db.RadarRuns.AsNoTracking().OrderByDescending(r => r.StartedAtUtc).Take(30).ToListAsync(ct);
    var lastDone = runs.FirstOrDefault(r => r.Status is RadarRunStatus.Completed or RadarRunStatus.Partial);
    var suggestions = lastDone is null ? [] : await db.RadarSuggestions.AsNoTracking().Where(s => s.RunId == lastDone.Id)
        .OrderBy(s => s.Format).ThenBy(s => s.TagName).ThenBy(s => s.Rank).ToListAsync(ct);
    var saved = await db.RadarSuggestions.AsNoTracking().Where(s => s.Status == RadarSuggestionStatus.Saved)
        .OrderByDescending(s => s.SavedAtUtc).Take(50).ToListAsync(ct);
    var productions = await db.StudioProductions.AsNoTracking().OrderByDescending(p => p.CreatedAtUtc).Take(50).ToListAsync(ct);
    var counts = await db.RadarSuggestions.AsNoTracking().GroupBy(s => s.TagName).Select(g => new { tag = g.Key, total = g.Count() }).ToListAsync(ct);
    return Results.Ok(new
    {
        settings = new { settings.DailyTime, settings.YouTubeSearchBudget, settings.ManualRequestedAtUtc, timeZone = tz },
        due = new { due.Due, due.Reason, due.SearchesLeft },
        youtubeConnected = youtube.IsAvailable,
        studio = new { enabled = studio.Enabled, folder = studio.Enabled ? studio.Stage(0) : null },
        tags = tags.Select(t => new { t.Id, t.Name, t.Synonyms, t.IncludeTerms, t.ExcludeTerms, t.Active, names = t.AllNames,
            suggestions = counts.FirstOrDefault(c => c.tag == t.Name)?.total ?? 0 }),
        latest = lastDone is null ? null : new { lastDone.Id, lastDone.StartedAtUtc, lastDone.FinishedAtUtc, lastDone.Status, lastDone.Summary, lastDone.Shortfall },
        runs = runs.Select(r => new { r.Id, r.StartedAtUtc, r.FinishedAtUtc, r.Status, r.Trigger, r.Error, r.Shortfall }),
        suggestions = suggestions.Select(RadarView.Suggestion),
        saved = saved.Select(RadarView.Suggestion),
        productions = productions.Select(p => new { p.Id, p.Code, p.Title, p.TagName, p.Format, p.Status, p.StatusDetail, p.Folder,
            p.FolderName, p.MissingScenes, files = RadarView.Parse(p.FilesJson), p.RecordingFinishedAtUtc, p.ReleasedAtUtc, p.CreatedAtUtc, p.UpdatedAtUtc }),
    });
});

app.MapGet("/api/radar/runs/{id:guid}", async (Guid id, IDbContextFactory<BastDbContext> factory, CancellationToken ct) =>
{
    await using var db = await factory.CreateDbContextAsync(ct);
    var run = await db.RadarRuns.AsNoTracking().Include(r => r.Suggestions).FirstOrDefaultAsync(r => r.Id == id, ct);
    return run is null ? Results.NotFound() : Results.Ok(new
    {
        run.Id, run.StartedAtUtc, run.FinishedAtUtc, run.Status, run.Summary, run.Error, run.Shortfall,
        suggestions = run.Suggestions.OrderBy(s => s.Format).ThenBy(s => s.Rank).Select(RadarView.Suggestion),
    });
});

// Usado pela tarefa agendada do Claude: está na hora? quais tags? quantas buscas restam?
app.MapGet("/api/radar/due", async (Bast.Application.Radar.RadarService radar, SchedulingService scheduling, CancellationToken ct) =>
    Results.Ok(await radar.DueAsync((await scheduling.GetSettingsAsync(ct)).TimeZoneId, ct)));

app.MapPost("/api/radar/runs/begin", async (RadarBeginInput? input, Bast.Application.Radar.RadarService radar, CancellationToken ct) =>
{
    var run = await radar.BeginRunAsync(string.IsNullOrWhiteSpace(input?.Trigger) ? "agendada" : input!.Trigger!, ct);
    return Results.Ok(new { run.Id, run.StartedAtUtc });
});

app.MapPost("/api/radar/runs/{id:guid}/submit", async (Guid id, Bast.Application.Radar.RadarRunInput input, Bast.Application.Radar.RadarService radar, CancellationToken ct) =>
{
    var (run, rejected) = await radar.SubmitRunAsync(id, input, ct);
    return run is null
        ? Results.BadRequest(new { problems = rejected })
        : Results.Ok(new { run.Id, run.Status, run.Shortfall, run.Error, rejected });
});

app.MapPost("/api/radar/runs/{id:guid}/fail", async (Guid id, RadarFailInput input, Bast.Application.Radar.RadarService radar, CancellationToken ct) =>
    await radar.FailRunAsync(id, input.Error ?? "", ct) is null ? Results.NotFound() : Results.Ok(new { message = "Falha registrada e avisada." }));

app.MapPost("/api/radar/refresh", async (Bast.Application.Radar.RadarService radar, CancellationToken ct) =>
{
    await radar.RequestManualAsync(ct);
    return Results.Ok(new { message = "Pesquisa pedida. A tarefa do Claude atende no próximo ciclo (até 30 min, com o app aberto)." });
});

app.MapPut("/api/radar/settings", async (RadarSettingsInput input, Bast.Application.Radar.RadarService radar, CancellationToken ct) =>
{
    var problems = await radar.UpdateSettingsAsync(input.DailyTime, input.YouTubeSearchBudget, ct);
    return problems.Count == 0 ? Results.Ok(new { message = "Configuração do Radar salva." }) : Results.BadRequest(new { problems });
});

app.MapPost("/api/radar/tags", async (Bast.Application.Radar.RadarTagInput input, Bast.Application.Radar.RadarService radar, CancellationToken ct) =>
{
    var (tag, problems) = await radar.SaveTagAsync(null, input, ct);
    return tag is null ? Results.BadRequest(new { problems }) : Results.Ok(new { tag.Id, message = $"Tag {tag.Name} criada." });
});

app.MapPut("/api/radar/tags/{id:guid}", async (Guid id, Bast.Application.Radar.RadarTagInput input, Bast.Application.Radar.RadarService radar, CancellationToken ct) =>
{
    var (tag, problems) = await radar.SaveTagAsync(id, input, ct);
    return tag is null ? Results.BadRequest(new { problems }) : Results.Ok(new { tag.Id, message = $"Tag {tag.Name} salva." });
});

app.MapDelete("/api/radar/tags/{id:guid}", async (Guid id, Bast.Application.Radar.RadarService radar, CancellationToken ct) =>
    await radar.DeleteTagAsync(id, ct) ? Results.Ok(new { message = "Tag excluída. O histórico de sugestões continua." }) : Results.NotFound());

// Busca no YouTube com métricas oficiais; cada consulta é guardada para medir o crescimento na próxima.
app.MapGet("/api/radar/youtube", async (string q, string? lang, string? region, int? days, int? max,
    Bast.Application.Radar.IYouTubeResearch youtube, Bast.Application.Radar.RadarService radar, SchedulingService scheduling,
    IDbContextFactory<BastDbContext> factory, CancellationToken ct) =>
{
    if (!youtube.IsAvailable) return Results.BadRequest(new { problems = new[] { "O YouTube não está conectado." } });
    if (!await radar.TakeSearchAsync((await scheduling.GetSettingsAsync(ct)).TimeZoneId, ct))
    {
        return Results.BadRequest(new { problems = new[] { "Orçamento diário de buscas no YouTube esgotado (a cota é compartilhada com os envios)." } });
    }

    IReadOnlyList<Bast.Application.Radar.RadarVideo> videos;
    try
    {
        videos = await youtube.SearchAsync(q, lang ?? "en", region ?? "US", days ?? 7, max ?? 15, ct);
    }
    catch (Bast.Application.Radar.YouTubeResearchException ex)
    {
        return Results.BadRequest(new { problems = new[] { ex.Message } });
    }

    await using var db = await factory.CreateDbContextAsync(ct);
    var ids = videos.Select(v => v.VideoId).ToList();
    var previous = (await db.RadarVideoSnapshots.AsNoTracking().Where(s => ids.Contains(s.VideoId)).ToListAsync(ct))
        .GroupBy(s => s.VideoId).ToDictionary(g => g.Key, g => g.OrderByDescending(s => s.AtUtc).First());
    var enriched = videos.Select(v =>
    {
        if (previous.TryGetValue(v.VideoId, out var before) && before.Views is { } bv && v.Views is { } nv && v.ConsultedAtUtc > before.AtUtc)
        {
            var hours = Math.Max(0.25, (v.ConsultedAtUtc - before.AtUtc).TotalHours);
            return v with { RecentViewsPerHour = Math.Round((nv - bv) / hours, 1) };
        }

        return v;
    }).ToList();
    db.RadarVideoSnapshots.AddRange(videos.Select(v => new RadarVideoSnapshot { VideoId = v.VideoId, AtUtc = v.ConsultedAtUtc, Views = v.Views, Likes = v.Likes, Comments = v.Comments }));
    await db.SaveChangesAsync(ct);
    return Results.Ok(enriched);
});

app.MapPost("/api/radar/suggestions/{id:guid}/{action}", async (Guid id, string action, Bast.Application.Radar.RadarService radar,
    Bast.Application.Radar.StudioService studio, SchedulingService scheduling, CancellationToken ct) =>
{
    switch (action)
    {
        case "save":
        case "ignore":
            var (found, message) = await radar.SetSuggestionStatusAsync(id, action == "save" ? RadarSuggestionStatus.Saved : RadarSuggestionStatus.Ignored, ct);
            return found ? Results.Ok(new { message }) : Results.NotFound();
        case "start":
            var (production, text) = await studio.StartAsync(id, (await scheduling.GetSettingsAsync(ct)).TimeZoneId, ct);
            return production is null ? Results.BadRequest(new { problems = new[] { text } }) : Results.Ok(new { production.Id, production.Code, production.Folder, message = text });
        default:
            return Results.NotFound();
    }
});

app.MapPost("/api/studio/{id:guid}/{action}", async (Guid id, string action, Bast.Application.Radar.StudioService studio, CancellationToken ct) =>
{
    var (found, message) = action switch
    {
        "finished" => await studio.MarkRecordingFinishedAsync(id, ct),
        "release" => await studio.ReleaseAsync(id, ct),
        _ => (false, ""),
    };
    return found ? Results.Ok(new { message }) : Results.NotFound();
});

// Botão de emergência: nenhuma etapa nova começa e a atual é interrompida. Nada é apagado.
app.MapPost("/api/production/stop", async (StopInput? input, Bast.Application.Production.ProductionCommands commands, CancellationToken ct) =>
    Results.Ok(new { message = await commands.EmergencyStopAsync(input?.Reason, ct) }));

app.MapPost("/api/production/release", async (Bast.Application.Production.ProductionCommands commands, CancellationToken ct) =>
    Results.Ok(new { message = await commands.ReleaseStopAsync(ct) }));

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
    s.Mode,
    s.ManualMaxPerDay,
    verticalSlots = s.VerticalSlotTimes.Select(t => t.ToString("HH:mm")),
    verticalPerDay = s.VerticalSlotTimes.Count,
    s.ChosenTimeMaxPerDay,
    horizontalSlots = s.HorizontalSlotTimes.Select(t => t.ToString("HH:mm")),
    s.HorizontalPerDay,
    s.ManualPosting,
    s.AutoPublishApproved,
    s.Paused,
    s.UpdatedAtUtc,
};

public sealed record GoogleClientInput(string ClientId, string ClientSecret);
public sealed record MetaAppInput(string AppId, string AppSecret);
public sealed record MetaTokenInput(string UserToken, string? PageId);
public sealed record ManualAnswerInput(bool Published);
public sealed record VideoTimeInput(string LocalTime);
public sealed record ThreadsAppInput(string AppId, string AppSecret, string RedirectUri);
public sealed record ThreadsCodeInput(string RedirectedUrl);
public sealed record TelegramTokenInput(string Token);
public sealed record TelegramLinkInput(string Code);
public sealed record StopInput(string? Reason);
public sealed record RadarBeginInput(string? Trigger);
public sealed record RadarFailInput(string? Error);
public sealed record RadarSettingsInput(string DailyTime, int YouTubeSearchBudget);

static class RadarView
{
    public static System.Text.Json.Nodes.JsonNode? Parse(string json)
    {
        try { return System.Text.Json.Nodes.JsonNode.Parse(json); }
        catch (System.Text.Json.JsonException) { return null; }
    }

    public static object Suggestion(RadarSuggestion s) => new
    {
        s.Id, shortId = s.Id.ToString("N")[..8], s.RunId, s.TagName, s.Format, s.Rank, s.Title, s.Category, s.Market, s.Opportunity,
        s.DiscoveredAtUtc, s.Status, s.StudioProductionId,
        sources = Parse(s.SourcesJson), analysis = Parse(s.AnalysisJson), plan = Parse(s.PlanJson),
    };
}

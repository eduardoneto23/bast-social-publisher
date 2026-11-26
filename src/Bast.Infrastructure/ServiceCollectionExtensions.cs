using Bast.Application;
using Bast.Application.Import;
using Bast.Application.Persistence;
using Bast.Application.Publishing;
using Bast.Infrastructure.Media;
using Bast.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Bast.Infrastructure;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddBast(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<BastOptions>()
            .Bind(configuration.GetSection(BastOptions.SectionName))
            .Validate(o => !string.IsNullOrWhiteSpace(o.RootFolder),
                "Configure Bast:RootFolder (pasta principal do BAST SOCIAL PUBLISHER) em appsettings.Local.json.")
            .Validate(o => o.StabilitySeconds >= 1 && o.ScanIntervalSeconds >= 1 && o.EngineIntervalSeconds >= 1,
                "StabilitySeconds, ScanIntervalSeconds e EngineIntervalSeconds devem ser >= 1.")
            .ValidateOnStart();

        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(sp => sp.GetRequiredService<IOptions<BastOptions>>().Value);
        services.AddSingleton(sp => new Workspace(sp.GetRequiredService<BastOptions>().RootFolder));

        services.AddDbContextFactory<BastDbContext>((sp, db) =>
            db.UseBastSqlite(sp.GetRequiredService<Workspace>().DatabasePath));

        services.AddSingleton<DatabaseInitializer>();
        services.AddSingleton<MediaTools>();
        services.AddSingleton<IVideoAnalyzer, FfprobeVideoAnalyzer>();
        services.AddSingleton<IImportStore, EfImportStore>();
        services.AddSingleton<FolderInspector>();
        services.AddSingleton<ImportService>();

        services.AddSingleton<ISecretStore, DpapiSecretStore>();
        services.AddSingleton<IPublicFileShare, CloudflaredFileShare>();
        services.AddSingleton<IVideoCompactor, FfmpegVideoCompactor>();
        services.AddSingleton<AccountService>();
        services.AddSingleton<IConnectorRegistry, ConnectorRegistry>();
        services.AddSingleton<SchedulingService>();
        services.AddSingleton<FolderSync>();
        services.AddSingleton<PublishingEngine>();
        services.AddSingleton<QueueCommands>();

        // Produção: gravações do OBS em 00 - GRAVACOES viram jobs com etapas.
        services.AddSingleton<Bast.Application.Production.IDiskSpace, Bast.Application.Production.DriveDiskSpace>();
        services.AddSingleton<Bast.Application.Production.IProductionTools, ProductionToolset>();
        services.AddSingleton<Bast.Application.Production.IProductionStep, Bast.Application.Production.IntegrityStep>();
        services.AddSingleton<Bast.Application.Production.IProductionStep, Bast.Application.Production.MediaAnalysisStep>();
        services.AddSingleton<Bast.Application.Production.ProductionPipeline>();
        services.AddSingleton<Bast.Application.Production.RecordingScanner>();
        services.AddSingleton<Bast.Application.Production.ProductionRunner>();
        services.AddSingleton<Bast.Application.Production.ProductionCommands>();

        // Radar de Conteúdo e Estúdio de Ideias (fluxo separado das lives).
        services.AddSingleton<Bast.Application.Radar.RadarService>();
        services.AddSingleton<Bast.Application.Radar.StudioService>();
        return services;
    }
}

public sealed class DatabaseInitializer(IDbContextFactory<BastDbContext> factory, Workspace workspace)
{
    /// <summary>Cria a estrutura de pastas, aplica migrações pendentes e ativa o modo WAL (resistente a quedas).</summary>
    public async Task InitializeAsync(CancellationToken ct)
    {
        workspace.EnsureCreated();
        await using var db = await factory.CreateDbContextAsync(ct);
        await db.Database.MigrateAsync(ct);
        await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;", ct);
        await db.Database.ExecuteSqlRawAsync("PRAGMA synchronous=FULL;", ct);
    }
}

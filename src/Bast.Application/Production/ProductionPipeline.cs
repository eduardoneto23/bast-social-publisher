using Bast.Application.Persistence;
using Bast.Domain;

namespace Bast.Application.Production;

/// <summary>O que uma etapa recebe para trabalhar: o job, a pasta de trabalho dele e o registro.</summary>
public sealed class StepContext(ProductionJob job, string workFolder, Action<ProductionLogLevel, string, string?, string?> log)
{
    public ProductionJob Job { get; } = job;

    /// <summary>Pasta de trabalho do job no disco de produção (já criada).</summary>
    public string WorkFolder { get; } = workFolder;

    public int Progress { get; private set; }

    public void ReportProgress(int percent) => Progress = Math.Clamp(percent, 0, 100);

    public void Log(string message, ProductionLogLevel level = ProductionLogLevel.Info, string? program = null, string? file = null) =>
        log(level, message, program, file);
}

/// <summary>
/// Uma etapa do pipeline. Regras: escreve só dentro da pasta de trabalho do job; arquivos parciais com nome
/// temporário, renomeados no fim (uma etapa interrompida nunca deixa um entregável pela metade); nunca toca no
/// original. Devolve um resumo curto do resultado.
/// </summary>
public interface IProductionStep
{
    string Name { get; }
    string Title { get; }

    /// <summary>Status do job enquanto a etapa roda.</summary>
    ProductionStatus Status { get; }

    Task<string> RunAsync(StepContext context, CancellationToken ct);
}

/// <summary>
/// Etapa feita fora do serviço, pelo Claude (tarefa agendada de produção): o executor não a roda; o job espera
/// até o Claude entregar o resultado pela API, que é conferido aqui antes de a etapa contar como concluída.
/// </summary>
public interface IExternalStep : IProductionStep
{
    /// <summary>Texto do painel enquanto o job espera esta etapa.</summary>
    string WaitingText { get; }

    /// <summary>Confere o que foi entregue. Problemas = recusa (a etapa continua esperando, com o motivo).</summary>
    Task<ExternalStepCheck> CheckAsync(StepContext context, CancellationToken ct);
}

/// <summary>Resultado da conferência de uma etapa externa.</summary>
/// <param name="Review">Motivo para parar o job em revisão humana (cena sensível, dúvida de segurança...), ou nulo.</param>
public sealed record ExternalStepCheck(IReadOnlyList<string> Problems, string Summary, string? Review = null);

/// <summary>Etapa prevista que ainda não foi implementada: o job para nela, sem simular nada.</summary>
public sealed record PlannedStep(string Name, string Title, ProductionStatus Status, int Phase);

/// <summary>Ordem das etapas do pipeline de produção, implementadas e previstas.</summary>
public sealed class ProductionPipeline
{
    private readonly List<object> _steps;

    public ProductionPipeline(IEnumerable<IProductionStep> implemented)
    {
        var byName = implemented.ToDictionary(s => s.Name);
        object Pick(PlannedStep planned) => byName.TryGetValue(planned.Name, out var step) ? step : planned;

        _steps =
        [
            Pick(new PlannedStep(IntegrityStep.StepName, "Verificação de integridade", ProductionStatus.Analyzing, 2)),
            Pick(new PlannedStep("analise-midia", "Análise de mídia (áudio, câmera, cenas)", ProductionStatus.Analyzing, 3)),
            Pick(new PlannedStep("selecao", "Detecção e seleção dos melhores momentos", ProductionStatus.SelectingHighlights, 4)),
            Pick(new PlannedStep("edicao-horizontal", "Edição horizontal", ProductionStatus.EditingHorizontal, 4)),
            Pick(new PlannedStep("edicao-vertical", "Edição vertical", ProductionStatus.EditingVertical, 5)),
            Pick(new PlannedStep("thumbnails", "Thumbnails e metadados", ProductionStatus.GeneratingThumbnails, 6)),
            Pick(new PlannedStep("qa", "Revisão independente dos arquivos finais", ProductionStatus.QualityCheck, 7)),
            Pick(new PlannedStep("aprovacao", "Aprovação e entrega no painel", ProductionStatus.WaitingApproval, 8)),
        ];
    }

    public IReadOnlyList<(string Name, string Title, ProductionStatus Status, IProductionStep? Step, int Phase)> Steps =>
        _steps.Select(s => s switch
        {
            IProductionStep step => (step.Name, step.Title, step.Status, step, 0),
            PlannedStep planned => (planned.Name, planned.Title, planned.Status, (IProductionStep?)null, planned.Phase),
            _ => throw new InvalidOperationException(),
        }).ToList();

    /// <summary>Cria no job as etapas que ainda não existem (jobs antigos ganham as etapas novas).</summary>
    public bool EnsureSteps(BastDbContext db, ProductionJob job)
    {
        var changed = false;
        var order = 0;
        foreach (var (name, _, _, _, _) in Steps)
        {
            order++;
            var existing = job.Steps.FirstOrDefault(s => s.Name == name);
            if (existing is null)
            {
                var step = new ProductionStep { Id = Guid.NewGuid(), JobId = job.Id, Name = name, Order = order };
                job.Steps.Add(step);
                db.ProductionSteps.Add(step);
                changed = true;
            }
            else if (existing.Order != order)
            {
                existing.Order = order;
                changed = true;
            }
        }

        return changed;
    }
}

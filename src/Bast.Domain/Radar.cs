namespace Bast.Domain;

/// <summary>
/// Radar de Conteúdo: pesquisa diária de oportunidades por tag (jogo/franquia). Fluxo separado da edição das
/// lives: filas, pastas, configurações e aprovação próprias.
/// </summary>
public sealed class RadarTag
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";

    /// <summary>Sinônimos (um por linha ou separados por ";"): "GTA 6", "Grand Theft Auto VI".</summary>
    public string Synonyms { get; set; } = "";

    /// <summary>Termos que precisam aparecer / que eliminam um resultado (opcionais).</summary>
    public string IncludeTerms { get; set; } = "";
    public string ExcludeTerms { get; set; } = "";

    public bool Active { get; set; } = true;
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }

    public static IReadOnlyList<string> SplitTerms(string text) =>
        (text ?? "").Split([';', '\n', '\r', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(t => t.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>Nome + sinônimos, sem repetição (ignorando maiúsculas): os termos de pesquisa da tag.</summary>
    public IReadOnlyList<string> AllNames => new[] { Name }.Concat(SplitTerms(Synonyms))
        .Where(t => !string.IsNullOrWhiteSpace(t)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>Verdadeiro quando o texto cita a tag por qualquer um dos nomes (sem diferenciar maiúsculas).</summary>
    public bool Matches(string text) =>
        !string.IsNullOrWhiteSpace(text) && AllNames.Any(n => text.Contains(n, StringComparison.OrdinalIgnoreCase));
}

public enum RadarRunStatus
{
    Running,
    Completed,
    Partial,
    Failed,
}

public enum RadarFormat
{
    Horizontal,
    Vertical,
}

public enum RadarSuggestionStatus
{
    Received,
    Saved,
    Ignored,
    InProduction,
}

/// <summary>Uma pesquisa diária (ou manual). Sugestões antigas nunca são reapresentadas como novas.</summary>
public sealed class RadarRun
{
    public Guid Id { get; set; }
    public DateTime StartedAtUtc { get; set; }
    public DateTime? FinishedAtUtc { get; set; }
    public RadarRunStatus Status { get; set; }
    public string Trigger { get; set; } = "agendada";
    public string? Summary { get; set; }
    public string? Error { get; set; }

    /// <summary>Formatos com menos de 5 oportunidades reais, com o motivo (não se inventa sugestão para completar).</summary>
    public string? Shortfall { get; set; }

    public List<RadarSuggestion> Suggestions { get; set; } = [];
}

public sealed class RadarSuggestion
{
    public Guid Id { get; set; }
    public Guid RunId { get; set; }
    public Guid TagId { get; set; }
    public string TagName { get; set; } = "";
    public RadarFormat Format { get; set; }
    public int Rank { get; set; }
    public string Title { get; set; } = "";
    public string Category { get; set; } = "";

    /// <summary>"Internacional" ou "Brasil", com o país quando conhecido (ex.: "Internacional · EUA").</summary>
    public string Market { get; set; } = "";

    /// <summary>Alta, Média ou Baixa.</summary>
    public string Opportunity { get; set; } = "";
    public DateTime DiscoveredAtUtc { get; set; }

    /// <summary>Fontes verificáveis (JSON): url, título, plataforma, métricas quando disponíveis, horário da consulta.</summary>
    public string SourcesJson { get; set; } = "[]";

    /// <summary>Análise da oportunidade (JSON): o que acontece, por que chama atenção, por que agora, concorrência, versão Bast.</summary>
    public string AnalysisJson { get; set; } = "{}";

    /// <summary>Plano de produção (JSON): ideia, gancho, roteiro, cenas, o que gravar, falas, textos, edição, título...</summary>
    public string PlanJson { get; set; } = "{}";

    public RadarSuggestionStatus Status { get; set; } = RadarSuggestionStatus.Received;
    public DateTime? SavedAtUtc { get; set; }
    public DateTime? IgnoredAtUtc { get; set; }
    public Guid? StudioProductionId { get; set; }
}

/// <summary>Estado de uma produção do Estúdio de Ideias (vídeo criado a partir de uma sugestão do Radar).</summary>
public enum StudioStatus
{
    SuggestionReceived,
    Saved,
    AwaitingRecording,
    RecordingDetected,
    AnalyzingFiles,
    Editing,
    Rendering,
    Validating,
    AwaitingUserReview,
    Approved,
    ReleasedForPublishing,
    Published,
    Error,
    NeedsReview,
}

/// <summary>
/// Produção do Estúdio de Ideias. O identificador principal é o código (BAST-AAAAMMDD-NNN); o nome da pasta é
/// só uma referência amigável. Nunca é publicada sem a liberação individual do usuário.
/// </summary>
public sealed class StudioProduction
{
    public Guid Id { get; set; }

    /// <summary>BAST-20261126-001.</summary>
    public string Code { get; set; } = "";
    public Guid SuggestionId { get; set; }
    public string TagName { get; set; } = "";
    public string Title { get; set; } = "";
    public RadarFormat Format { get; set; }

    /// <summary>Roteiro/plano aprovado (cópia do plano da sugestão no momento em que a produção começou).</summary>
    public string PlanJson { get; set; } = "{}";
    public string SourcesJson { get; set; } = "[]";

    /// <summary>Pasta da produção (muda de estágio conforme o fluxo).</summary>
    public string Folder { get; set; } = "";
    public string FolderName { get; set; } = "";

    public StudioStatus Status { get; set; } = StudioStatus.AwaitingRecording;
    public string? StatusDetail { get; set; }

    /// <summary>Arquivos de gravação encontrados (JSON) e cenas do checklist ainda sem gravação.</summary>
    public string FilesJson { get; set; } = "[]";
    public string? MissingScenes { get; set; }

    /// <summary>O usuário marcou que terminou de gravar (botão no painel ou arquivo PRONTO.txt na pasta).</summary>
    public DateTime? RecordingFinishedAtUtc { get; set; }

    /// <summary>Liberação individual para publicar. A publicação automática dos outros fluxos não vale aqui.</summary>
    public DateTime? ReleasedAtUtc { get; set; }

    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}

/// <summary>Configuração do Radar (uma linha).</summary>
public sealed class RadarSettings
{
    public int Id { get; set; } = 1;

    /// <summary>Horário diário da pesquisa, no fuso da agenda (padrão 08:00, America/Sao_Paulo).</summary>
    public string DailyTime { get; set; } = "08:00";

    /// <summary>Pesquisa manual pedida no painel; a tarefa do Claude atende no próximo ciclo.</summary>
    public DateTime? ManualRequestedAtUtc { get; set; }

    /// <summary>Buscas no YouTube por dia (cada uma gasta 100 das 10.000 unidades diárias, compartilhadas com os envios).</summary>
    public int YouTubeSearchBudget { get; set; } = 12;

    /// <summary>Buscas no YouTube já usadas no dia (data local yyyy-MM-dd).</summary>
    public string? SearchesDate { get; set; }
    public int SearchesUsed { get; set; }

    public DateTime UpdatedAtUtc { get; set; }
}

/// <summary>Leitura das métricas públicas de um vídeo numa data: base para calcular o crescimento entre consultas.</summary>
public sealed class RadarVideoSnapshot
{
    public long Id { get; set; }
    public string VideoId { get; set; } = "";
    public DateTime AtUtc { get; set; }
    public long? Views { get; set; }
    public long? Likes { get; set; }
    public long? Comments { get; set; }
}

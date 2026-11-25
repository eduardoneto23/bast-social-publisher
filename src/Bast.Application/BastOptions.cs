namespace Bast.Application;

public sealed class BastOptions
{
    public const string SectionName = "Bast";

    /// <summary>Pasta principal (contém 01 - ENVIAR VERTICAL ... SYSTEM). Obrigatória.</summary>
    public string RootFolder { get; set; } = "";

    /// <summary>Tempo sem alterações para considerar que a cópia de uma pasta terminou.</summary>
    public int StabilitySeconds { get; set; } = 10;

    /// <summary>Intervalo da varredura periódica de 01 - ENVIAR VERTICAL (o monitor de eventos apenas antecipa a varredura).</summary>
    public int ScanIntervalSeconds { get; set; } = 15;

    /// <summary>Caminho completo do ffprobe. Vazio = procurar no PATH.</summary>
    public string FfprobePath { get; set; } = "";

    /// <summary>Caminho completo do ffmpeg. Vazio = procurar no PATH.</summary>
    public string FfmpegPath { get; set; } = "";

    /// <summary>
    /// Caminho completo do cloudflared, usado para o Threads buscar o vídeo. Vazio = SYSTEM\tools\cloudflared.exe
    /// dentro da pasta principal.
    /// </summary>
    public string CloudflaredPath { get; set; } = "";

    /// <summary>Intervalo do ciclo do motor de publicação.</summary>
    public int EngineIntervalSeconds { get; set; } = 10;

    /// <summary>
    /// Modo de simulação: percorre todo o fluxo sem publicar em nenhuma rede.
    /// Use com uma pasta principal separada, pois os vídeos são marcados como publicados (simulados).
    /// </summary>
    public bool SimulationMode { get; set; }

    public string[] VideoExtensions { get; set; } = [".mp4", ".mov", ".m4v", ".webm"];

    /// <summary>
    /// Disco de trabalho da produção (análise, render, entregáveis). Vazio = produção desligada.
    /// Ex.: "G:\BAST PRODUCAO". Nunca recebe o original da gravação.
    /// </summary>
    public string ProductionWorkFolder { get; set; } = "";

    /// <summary>Espaço livre que sempre sobra no disco de trabalho: abaixo disso nenhum job começa.</summary>
    public int ProductionReserveGb { get; set; } = 50;

    /// <summary>Gravações mais curtas que isto são testes e não viram job.</summary>
    public int ProductionMinDurationSeconds { get; set; } = 300;

    /// <summary>Tempo sem mudar tamanho e data para a gravação contar como terminada.</summary>
    public int ProductionStableSeconds { get; set; } = 120;

    /// <summary>Nomes de arquivo ignorados (expressões regulares, sem diferenciar maiúsculas): Replay Buffer etc.</summary>
    public string[] ProductionIgnorePatterns { get; set; } = ["^Replay", "^Screenshot"];

    public int ProductionIntervalSeconds { get; set; } = 15;
}

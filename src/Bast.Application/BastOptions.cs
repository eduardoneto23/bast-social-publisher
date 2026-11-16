namespace Bast.Application;

public sealed class BastOptions
{
    public const string SectionName = "Bast";

    /// <summary>Pasta principal (contém 01 - ENVIAR ... SYSTEM). Obrigatória.</summary>
    public string RootFolder { get; set; } = "";

    /// <summary>Tempo sem alterações para considerar que a cópia de uma pasta terminou.</summary>
    public int StabilitySeconds { get; set; } = 10;

    /// <summary>Intervalo da varredura periódica de 01 - ENVIAR (o monitor de eventos apenas antecipa a varredura).</summary>
    public int ScanIntervalSeconds { get; set; } = 15;

    /// <summary>Caminho completo do ffprobe. Vazio = procurar no PATH.</summary>
    public string FfprobePath { get; set; } = "";

    /// <summary>Caminho completo do ffmpeg. Vazio = procurar no PATH.</summary>
    public string FfmpegPath { get; set; } = "";

    /// <summary>Intervalo do ciclo do motor de publicação.</summary>
    public int EngineIntervalSeconds { get; set; } = 10;

    /// <summary>
    /// Modo de simulação: percorre todo o fluxo sem publicar em nenhuma rede.
    /// Use com uma pasta principal separada, pois os vídeos são marcados como publicados (simulados).
    /// </summary>
    public bool SimulationMode { get; set; }

    public string[] VideoExtensions { get; set; } = [".mp4", ".mov", ".m4v", ".webm"];
}

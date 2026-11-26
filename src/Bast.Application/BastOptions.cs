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

    /// <summary>Faixas de áudio da gravação do OBS: jogo e microfone (a 0 é o mix).</summary>
    public int ProductionGameTrack { get; set; } = 1;
    public int ProductionMicTrack { get; set; } = 2;

    /// <summary>Área da webcam no quadro 1920x1080 da gravação: "x,y,largura,altura".</summary>
    public string ProductionWebcam { get; set; } = "0,0,372,268";

    /// <summary>Modelo do Whisper para a transcrição (instalado em SYSTEM\tools\modelos).</summary>
    public string ProductionWhisperModel { get; set; } = "small";

    /// <summary>Python do ambiente da produção. Vazio = SYSTEM\tools\python-producao\Scripts\python.exe.</summary>
    public string ProductionPython { get; set; } = "";

    /// <summary>
    /// Pasta do Estúdio de Ideias (vídeos criados a partir do Radar de Conteúdo), separada da produção das lives.
    /// Vazio = Estúdio desligado. Ex.: "G:\BAST\ESTUDIO_DE_IDEIAS".
    /// </summary>
    public string StudioFolder { get; set; } = "";

    /// <summary>Endereço do painel usado nos links do Telegram.</summary>
    public string PanelUrl { get; set; } = "http://127.0.0.1:5080/";

    /// <summary>Pasta dos scripts da produção. Vazio = "producao" ao lado do serviço (copiada na publicação).</summary>
    public string ProductionScriptsFolder { get; set; } = "";
}

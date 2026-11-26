using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Nodes;
using Bast.Domain;

namespace Bast.Application.Production;

/// <summary>
/// Etapa 6: thumbnail do horizontal no padrão BAST_THUMBNAIL_V1, feita pelo Claude no Canva (quadros reais do
/// vídeo, tratamento de imagem, até 3 conceitos: acontecimento, reação, curiosidade; validação em tamanho pequeno).
/// O Claude grava "4 - thumbnails\thumbnail.png" e "thumbnail.json" e entrega pela API; aqui confere-se o que o
/// YouTube exige e o que o perfil pede antes de valer.
/// </summary>
public sealed class ThumbnailStep : IExternalStep
{
    public const string StepName = "thumbnails";
    public const string ImageFile = "thumbnail.png";
    public const string InfoFile = "thumbnail.json";
    public const long MaxBytes = 2 * 1024 * 1024;

    public string Name => StepName;
    public string Title => "Thumbnail (BAST_THUMBNAIL_V1) e metadados";
    public ProductionStatus Status => ProductionStatus.GeneratingThumbnails;

    public string WaitingText =>
        "Aguardando a thumbnail pelo Claude no Canva (tarefa \"BAST – Produção\", com o app do Claude aberto).";

    public Task<string> RunAsync(StepContext context, CancellationToken ct) =>
        throw new InvalidOperationException("A thumbnail é feita pelo Claude e entregue pela API; o executor não a roda.");

    public async Task<ExternalStepCheck> CheckAsync(StepContext context, CancellationToken ct)
    {
        var folder = Path.Combine(context.WorkFolder, ProductionFolders.Thumbnails);
        var problems = new List<string>();

        var image = new FileInfo(Path.Combine(folder, ImageFile));
        if (!image.Exists)
        {
            problems.Add($"{ImageFile} não encontrado em 4 - thumbnails.");
        }
        else
        {
            if (image.Length > MaxBytes) problems.Add($"{ImageFile} tem {image.Length / 1048576.0:0.0} MB (o YouTube aceita até 2 MB).");
            var size = await PngSizeAsync(image.FullName, ct);
            if (size is null) problems.Add($"{ImageFile} não é um PNG válido.");
            else if (size != (1280, 720)) problems.Add($"{ImageFile} tem {size.Value.Width}x{size.Value.Height} (o perfil pede 1280x720).");
        }

        JsonNode? info = null;
        var infoPath = Path.Combine(folder, InfoFile);
        if (!File.Exists(infoPath)) problems.Add($"{InfoFile} não encontrado em 4 - thumbnails.");
        else
        {
            try { info = JsonNode.Parse(await File.ReadAllTextAsync(infoPath, ct)); }
            catch (JsonException ex) { problems.Add($"{InfoFile} inválido: {ex.Message}"); }
        }

        if (info is not null)
        {
            var phrase = info["frase"]?.ToString() ?? "";
            var words = phrase.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
            if (words is < 1 or > 5) problems.Add($"Frase da thumbnail com {words} palavra(s) (o perfil pede de 2 a 5; sem texto, use 1 palavra-chave ou explique em \"motivo\").");
            if ((info["conceito"]?.ToString() ?? "") is not ("A" or "B" or "C")) problems.Add("\"conceito\" deve ser A (acontecimento), B (reação) ou C (curiosidade).");
            if (string.IsNullOrWhiteSpace(info["motivo"]?.ToString())) problems.Add("Falta \"motivo\" (por que esta composição foi a escolhida).");
            if (info["segundo"] is null) problems.Add("Falta \"segundo\" (o quadro real do vídeo usado).");
            foreach (var alt in info["alternativas"] as JsonArray ?? [])
            {
                var file = alt?["arquivo"]?.ToString();
                if (!string.IsNullOrWhiteSpace(file) && !File.Exists(Path.Combine(folder, file))) problems.Add($"Alternativa {file} não encontrada.");
            }
        }

        var alternatives = (info?["alternativas"] as JsonArray)?.Count ?? 0;
        var summary = info is null ? "" :
            $"thumbnail 1280x720, conceito {info["conceito"]}, frase \"{info["frase"]}\", quadro de {info["segundo"]} s, {alternatives} alternativa(s).";
        // Pedido explícito de revisão (dúvida sobre a imagem) para o job antes do QA.
        var review = info?["revisao_humana"]?.GetValue<bool>() == true ? "O Claude pediu revisão da thumbnail: " + info["motivo"] : null;
        return new(problems, summary, review);
    }

    /// <summary>Largura e altura de um PNG (cabeçalho IHDR), ou nulo se não for PNG.</summary>
    public static async Task<(int Width, int Height)?> PngSizeAsync(string path, CancellationToken ct)
    {
        var header = new byte[24];
        await using var stream = File.OpenRead(path);
        if (await stream.ReadAsync(header, ct) < 24) return null;
        byte[] signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        if (!header.AsSpan(0, 8).SequenceEqual(signature) || header[12] != 'I' || header[13] != 'H' || header[14] != 'D' || header[15] != 'R') return null;
        return (BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(16, 4)), BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(20, 4)));
    }
}

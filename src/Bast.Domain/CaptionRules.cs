using System.Text;
using System.Text.RegularExpressions;

namespace Bast.Domain;

public sealed record VideoFacts(double DurationSeconds, long FileSizeBytes);

/// <summary>
/// Regras de conteúdo por plataforma. Nada aqui altera a legenda: as funções apenas extraem o título
/// do YouTube e apontam incompatibilidades para que sejam sinalizadas antes da publicação.
/// Limites conferidos na documentação oficial em 2026-11-14 (ver docs/APIS.md).
/// </summary>
public static partial class CaptionRules
{
    private const long Mb = 1024 * 1024;

    /// <summary>Texto enviado às plataformas: o conteúdo integral, apenas com quebras de linha no padrão LF.</summary>
    public static string TextFor(string caption) => caption.Replace("\r\n", "\n").Replace('\r', '\n');

    /// <summary>Título do YouTube: a primeira linha não vazia da legenda.</summary>
    public static string YouTubeTitle(string caption) =>
        TextFor(caption).Split('\n').Select(line => line.Trim()).FirstOrDefault(line => line.Length > 0) ?? "";

    /// <summary>Descrição do YouTube: o conteúdo completo (o título continua nela, como primeira linha).</summary>
    public static string YouTubeDescription(string caption) => TextFor(caption);

    public static IReadOnlyList<string> Validate(Platform platform, string caption, VideoFacts video)
    {
        var text = TextFor(caption);
        var problems = new List<string>();
        var seconds = video.DurationSeconds;

        switch (platform)
        {
            case Platform.Instagram:
                Limit(problems, text.Length, 2200, "caracteres na legenda");
                Limit(problems, HashtagRegex().Count(text), 30, "hashtags");
                Limit(problems, MentionRegex().Count(text), 20, "menções");
                Duration(problems, seconds, 3, 15 * 60, "Reels do Instagram");
                Size(problems, video.FileSizeBytes, 300 * Mb, "Instagram");
                break;

            case Platform.Facebook:
                Duration(problems, seconds, 3, 90, "Reels do Facebook");
                break;

            case Platform.Threads:
                Limit(problems, ThreadsLength(text), 500, "caracteres no texto (emojis contam como bytes UTF-8)");
                Duration(problems, seconds, 0, 5 * 60, "Threads");
                Size(problems, video.FileSizeBytes, 1024 * Mb, "Threads");
                break;

            case Platform.TikTok:
                Limit(problems, text.Length, 2200, "caracteres na legenda");
                break;

            case Platform.YouTube:
                var title = YouTubeTitle(caption);
                if (title.Length == 0) problems.Add("A legenda não tem uma primeira linha para servir de título.");
                Limit(problems, title.Length, 100, "caracteres no título (primeira linha)");
                Limit(problems, Encoding.UTF8.GetByteCount(text), 5000, "bytes na descrição");
                if (text.Contains('<') || text.Contains('>')) problems.Add("O YouTube não aceita os caracteres < e > no título nem na descrição.");
                break;
        }

        return problems;
    }

    /// <summary>Contagem do Threads: cada caractere vale 1, exceto emojis, que valem seu tamanho em bytes UTF-8.</summary>
    public static int ThreadsLength(string text)
    {
        var total = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            total += IsEmojiLike(rune) ? rune.Utf8SequenceLength : 1;
        }

        return total;
    }

    private static bool IsEmojiLike(Rune rune)
    {
        var value = rune.Value;
        return value >= 0x1F000                      // emojis e símbolos dos planos suplementares
               || value is >= 0x2600 and <= 0x27BF   // símbolos diversos e dingbats
               || value is >= 0x2B00 and <= 0x2BFF   // setas e estrelas
               || value is 0x200D or 0xFE0F or 0x20E3; // junções e seletores usados em sequências de emoji
    }

    private static void Limit(List<string> problems, int actual, int max, string what)
    {
        if (actual > max) problems.Add($"{actual} {what}; o limite é {max}.");
    }

    private static void Duration(List<string> problems, double seconds, double min, double max, string where)
    {
        if (seconds < min) problems.Add($"Vídeo de {seconds:0.#} s é curto demais para {where} (mínimo {min:0} s).");
        if (seconds > max) problems.Add($"Vídeo de {seconds:0} s é longo demais para {where} (máximo {max:0} s).");
    }

    private static void Size(List<string> problems, long bytes, long max, string where)
    {
        if (bytes > max) problems.Add($"Arquivo de {bytes / Mb} MB excede o limite do {where} ({max / Mb} MB).");
    }

    [GeneratedRegex(@"(?<![\w&])#\w+")]
    private static partial Regex HashtagRegex();

    [GeneratedRegex(@"(?<![\w@])@[\w.]+")]
    private static partial Regex MentionRegex();
}

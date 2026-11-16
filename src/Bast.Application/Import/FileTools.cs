using System.Security.Cryptography;
using System.Text;

namespace Bast.Application.Import;

public static class FileHasher
{
    public static async Task<string> Sha256Async(string path, CancellationToken ct)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 1 << 20, FileOptions.SequentialScan | FileOptions.Asynchronous);
        var hash = await SHA256.HashDataAsync(stream, ct);
        return Convert.ToHexStringLower(hash);
    }
}

/// <summary>Lê o TXT da legenda preservando o conteúdo integral (emojis, quebras de linha, hashtags, pontuação).</summary>
public static class CaptionReader
{
    static CaptionReader() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    public static string Read(string path)
    {
        var bytes = File.ReadAllBytes(path);

        if (bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble))
        {
            return StrictUtf8.GetString(bytes, 3, bytes.Length - 3);
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
        {
            return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
        {
            return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
        }

        try
        {
            return StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            // TXT antigo salvo como "ANSI" pelo Bloco de Notas.
            return Encoding.GetEncoding(1252).GetString(bytes);
        }
    }

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
}

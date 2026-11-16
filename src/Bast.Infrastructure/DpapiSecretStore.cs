using System.Security.Cryptography;
using System.Text;
using Bast.Application;

namespace Bast.Infrastructure;

/// <summary>
/// Credenciais criptografadas com a DPAPI do Windows, uma por arquivo em SYSTEM\secrets.
/// O escopo é a máquina: o serviço (LocalSystem) e a execução manual leem os mesmos segredos,
/// e os arquivos não podem ser decifrados se copiados para outro computador.
/// </summary>
public sealed class DpapiSecretStore(Workspace workspace) : ISecretStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("BAST SOCIAL PUBLISHER v2 secrets");
    private readonly Lock _gate = new();

    private string Folder => Path.Combine(workspace.SystemPath, "secrets");

    public bool Exists(string key) => File.Exists(PathOf(key));

    public string? Get(string key)
    {
        if (!OperatingSystem.IsWindows()) throw NotWindows();
        lock (_gate)
        {
            var path = PathOf(key);
            if (!File.Exists(path)) return null;
            var clear = ProtectedData.Unprotect(File.ReadAllBytes(path), Entropy, DataProtectionScope.LocalMachine);
            return Encoding.UTF8.GetString(clear);
        }
    }

    public void Set(string key, string value)
    {
        if (!OperatingSystem.IsWindows()) throw NotWindows();
        lock (_gate)
        {
            Directory.CreateDirectory(Folder);
            var cipher = ProtectedData.Protect(Encoding.UTF8.GetBytes(value), Entropy, DataProtectionScope.LocalMachine);
            // Grava em arquivo temporário e troca de uma vez: uma queda não deixa o segredo pela metade.
            var path = PathOf(key);
            var temp = path + ".tmp";
            File.WriteAllBytes(temp, cipher);
            File.Move(temp, path, overwrite: true);
        }
    }

    public void Delete(string key)
    {
        lock (_gate)
        {
            File.Delete(PathOf(key));
        }
    }

    private string PathOf(string key)
    {
        if (key.Length == 0 || key.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_')))
        {
            throw new ArgumentException("Chave de segredo inválida.", nameof(key));
        }

        return Path.Combine(Folder, key + ".bin");
    }

    private static PlatformNotSupportedException NotWindows() => new("O armazenamento seguro local usa a DPAPI do Windows.");
}

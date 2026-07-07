using System.Security.Cryptography;
using System.Text;
using Konscious.Security.Cryptography;

namespace RemoteDesktop.Shared.Security;

/// <summary>
/// Argon2id password hashing for the manual-login path. Argon2id is memory-hard, so a leaked
/// host config is far more expensive to brute-force than a bare SHA/PBKDF hash.
///
/// Stored format (single string, easy to drop in a config file):
///   <c>argon2id$v=19$m=65536,t=3,p=2$&lt;base64 salt&gt;$&lt;base64 hash&gt;</c>
/// </summary>
public static class PasswordHasher
{
    private const int SaltSize = 16;
    private const int HashSize = 32;
    private const int MemoryKiB = 64 * 1024; // 64 MiB
    private const int Iterations = 3;
    private const int Parallelism = 2;

    public static string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var hash = Derive(password, salt, MemoryKiB, Iterations, Parallelism, HashSize);
        return $"argon2id$v=19$m={MemoryKiB},t={Iterations},p={Parallelism}$" +
               $"{Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    public static bool Verify(string password, string encoded)
    {
        try
        {
            var parts = encoded.Split('$');
            if (parts.Length != 5 || parts[0] != "argon2id") return false;

            var pmap = parts[2].Split(',')
                .Select(kv => kv.Split('='))
                .ToDictionary(kv => kv[0], kv => int.Parse(kv[1]));

            var salt = Convert.FromBase64String(parts[3]);
            var expected = Convert.FromBase64String(parts[4]);
            var actual = Derive(password, salt, pmap["m"], pmap["t"], pmap["p"], expected.Length);

            // Constant-time compare to avoid timing oracles.
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch
        {
            return false;
        }
    }

    private static byte[] Derive(string password, byte[] salt, int memKiB, int iterations, int parallelism, int size)
    {
        using var argon = new Argon2id(Encoding.UTF8.GetBytes(password))
        {
            Salt = salt,
            MemorySize = memKiB,
            Iterations = iterations,
            DegreeOfParallelism = parallelism,
        };
        return argon.GetBytes(size);
    }
}

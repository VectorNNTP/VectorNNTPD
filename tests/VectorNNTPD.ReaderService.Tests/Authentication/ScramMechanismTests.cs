using System.Security.Cryptography;
using System.Text;
using VectorNNTPD.ReaderService.Authentication;
using VectorNNTPD.ReaderService.Authentication.Sasl;
using VectorNNTPD.ReaderService.Tests.TestDoubles;

namespace VectorNNTPD.ReaderService.Tests.Authentication;

public sealed class ScramMechanismTests
{
    [Fact]
    public void ValidExchange_ReturnsServerSignature()
    {
        const string password = "pencil";
        const int iterations = 4096;
        var salt = Convert.FromHexString("4142434445464748494A4B4C4D4E4F50");
        var keys = ScramClient.Derive(password, salt, iterations);
        var credential = new ScramStoredCredential(salt, iterations, keys.StoredKey, keys.ServerKey);
        const string clientFirst = "n,,n=user,r=fyko+d2lbbFgONRv9qkxdawL";
        var (state, serverFirst) = ScramMechanism.Begin(clientFirst, credential);
        Assert.Contains("s=", serverFirst, StringComparison.Ordinal);
        Assert.Contains("i=4096", serverFirst, StringComparison.Ordinal);

        var clientFinal = ScramClient.CreateClientFinal(clientFirst, serverFirst, keys.ClientKey, keys.StoredKey);
        var serverFinal = state.TryFinish(clientFinal);
        Assert.NotNull(serverFinal);
        Assert.StartsWith("v=", serverFinal, StringComparison.Ordinal);
        Assert.Equal(serverFinal, ScramClient.ExpectedServerFinal(clientFirst, serverFirst, clientFinal, keys.ServerKey));
    }

    [Fact]
    public void IncorrectProof_Fails()
    {
        const string password = "pencil";
        var salt = RandomNumberGenerator.GetBytes(16);
        var keys = ScramClient.Derive(password, salt, 4096);
        var credential = new ScramStoredCredential(salt, 4096, keys.StoredKey, keys.ServerKey);
        const string clientFirst = "n,,n=user,r=abc123nonce";
        var (state, serverFirst) = ScramMechanism.Begin(clientFirst, credential);
        var clientFinal = ScramClient.CreateClientFinal(clientFirst, serverFirst, keys.ClientKey, keys.StoredKey);
        var proof = clientFinal[(clientFinal.LastIndexOf(",p=", StringComparison.Ordinal) + 3)..];
        var tampered = clientFinal.Replace($",p={proof}", ",p=AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=", StringComparison.Ordinal);
        Assert.NotEqual(clientFinal, tampered);
        Assert.Null(state.TryFinish(tampered));
    }

    [Fact]
    public void MalformedStoredKeyLength_FailsWithoutThrowing()
    {
        const string password = "pencil";
        var salt = RandomNumberGenerator.GetBytes(16);
        var keys = ScramClient.Derive(password, salt, 4096);
        var credential = new ScramStoredCredential(salt, 4096, keys.StoredKey[..16], keys.ServerKey);
        const string clientFirst = "n,,n=user,r=abc123nonce";
        var (state, serverFirst) = ScramMechanism.Begin(clientFirst, credential);
        var clientFinal = ScramClient.CreateClientFinal(clientFirst, serverFirst, keys.ClientKey, keys.StoredKey);
        Assert.Null(state.TryFinish(clientFinal));
    }

    [Fact]
    public void MissingScramFields_AreNotUsable()
    {
        var record = MemoryNntpUserRecordStore.Create(
            "alice",
            "pw",
            allowScram: true,
            scramSalt: ReadOnlyMemory<byte>.Empty,
            scramIterations: 65536);
        Assert.False(record.HasScramMaterial);
    }
}

internal static class ScramClient
{
    public sealed record Keys(byte[] ClientKey, byte[] StoredKey, byte[] ServerKey);

    public static Keys Derive(string password, byte[] salt, int iterations)
    {
        var salted = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password),
            salt,
            iterations,
            HashAlgorithmName.SHA256,
            32);
        var clientKey = Hmac(salted, "Client Key"u8);
        var storedKey = SHA256.HashData(clientKey);
        var serverKey = Hmac(salted, "Server Key"u8);
        return new Keys(clientKey, storedKey, serverKey);
    }

    public static string CreateClientFinal(string clientFirst, string serverFirst, byte[] clientKey, byte[] storedKey)
    {
        var gs2 = clientFirst[..(clientFirst.IndexOf(",,", StringComparison.Ordinal) + 2)];
        var bare = clientFirst[gs2.Length..];
        var combinedNonce = Attribute(serverFirst, 'r');
        var withoutProof = $"c={Convert.ToBase64String(Encoding.ASCII.GetBytes(gs2))},r={combinedNonce}";
        var authMessage = $"{bare},{serverFirst},{withoutProof}";
        var signature = Hmac(storedKey, Encoding.UTF8.GetBytes(authMessage));
        var proof = Xor(clientKey, signature);
        return $"{withoutProof},p={Convert.ToBase64String(proof)}";
    }

    public static string ExpectedServerFinal(string clientFirst, string serverFirst, string clientFinal, byte[] serverKey)
    {
        var gs2 = clientFirst[..(clientFirst.IndexOf(",,", StringComparison.Ordinal) + 2)];
        var bare = clientFirst[gs2.Length..];
        var proof = Attribute(clientFinal, 'p');
        var withoutProof = clientFinal.Replace($",p={proof}", string.Empty, StringComparison.Ordinal);
        var authMessage = $"{bare},{serverFirst},{withoutProof}";
        return $"v={Convert.ToBase64String(Hmac(serverKey, Encoding.UTF8.GetBytes(authMessage)))}";
    }

    private static string Attribute(string message, char key)
    {
        foreach (var part in message.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part.Length >= 2 && part[0] == key && part[1] == '=')
            {
                return part[2..];
            }
        }

        throw new InvalidOperationException($"Missing SCRAM attribute {key}.");
    }

    private static byte[] Hmac(byte[] key, ReadOnlySpan<byte> data)
    {
        using var hmac = IncrementalHash.CreateHMAC(HashAlgorithmName.SHA256, key);
        hmac.AppendData(data);
        return hmac.GetHashAndReset();
    }

    private static byte[] Xor(byte[] left, byte[] right)
    {
        var result = new byte[left.Length];
        for (var i = 0; i < left.Length; i++)
        {
            result[i] = (byte)(left[i] ^ right[i]);
        }

        return result;
    }
}

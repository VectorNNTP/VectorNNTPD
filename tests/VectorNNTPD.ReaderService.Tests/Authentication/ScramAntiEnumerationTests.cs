using System.Text;
using VectorNNTPD.ReaderService.Authentication.Sasl;
using VectorNNTPD.ReaderService.Authentication;
using VectorNNTPD.ReaderService.Tests.Session;
using VectorNNTPD.ReaderService.Tests.TestDoubles;

namespace VectorNNTPD.ReaderService.Tests.Authentication;

[Collection(nameof(NntpCommandLoggerCollection))]
public sealed class ScramAntiEnumerationTests
{
    private const string ClientNonce = "fyko+d2lbbFgONRv9qkxdawL";
    private const string RealPassword = "pencil";

    [Fact]
    public async Task UnknownAccount_Receives383_Then481_WithoutIdentity()
    {
        var dummy = CreateInjectedDummy();
        await using var harness = await AuthHarness.CreateAsync(
            new MemoryNntpUserRecordStore(),
            dummyScram: dummy);
        await AssertDummyPathAsync(harness, "definitely-missing-user", dummy);
    }

    [Fact]
    public async Task DisabledAccount_FollowsUnknownAccountPath()
    {
        var dummy = CreateInjectedDummy();
        var real = CreateRealRecord("disabled-user", enabled: true);
        var disabled = MemoryNntpUserRecordStore.Create(
            "disabled-user",
            RealPassword,
            enabled: false,
            scramSalt: real.ScramSalt,
            scramIterations: real.ScramIterations,
            scramStoredKey: real.ScramStoredKey,
            scramServerKey: real.ScramServerKey);
        await using var harness = await AuthHarness.CreateAsync(Users(disabled), dummyScram: dummy);
        var serverFirst = await AssertDummyPathAsync(harness, "disabled-user", dummy);
        Assert.DoesNotContain(Convert.ToBase64String(disabled.ScramSalt.ToArray()), serverFirst, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ScramDisabledAccount_FollowsUnknownAccountPath()
    {
        var dummy = CreateInjectedDummy();
        var real = CreateRealRecord("noscram-user");
        var denied = MemoryNntpUserRecordStore.Create(
            "noscram-user",
            RealPassword,
            allowScram: false,
            scramSalt: real.ScramSalt,
            scramIterations: real.ScramIterations,
            scramStoredKey: real.ScramStoredKey,
            scramServerKey: real.ScramServerKey);
        await using var harness = await AuthHarness.CreateAsync(Users(denied), dummyScram: dummy);
        var serverFirst = await AssertDummyPathAsync(harness, "noscram-user", dummy);
        Assert.DoesNotContain(Convert.ToBase64String(denied.ScramSalt.ToArray()), serverFirst, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MissingScramMaterial_FollowsUnknownAccountPath()
    {
        var dummy = CreateInjectedDummy();
        var missing = MemoryNntpUserRecordStore.Create("bare-user", RealPassword, allowScram: true);
        Assert.False(missing.HasScramMaterial);
        await using var harness = await AuthHarness.CreateAsync(Users(missing), dummyScram: dummy);
        await AssertDummyPathAsync(harness, "bare-user", dummy);
    }

    [Fact]
    public async Task RealValidScramAccount_StillSucceeds()
    {
        var dummy = CreateInjectedDummy();
        var record = CreateRealRecord("user");
        var keys = ScramClient.Derive(RealPassword, record.ScramSalt.ToArray(), record.ScramIterations);
        await using var harness = await AuthHarness.CreateAsync(Users(record), dummyScram: dummy);
        var session = harness.CreateSession();
        var run = session.RunAsync();
        await harness.ReadGreetingAsync();

        var clientFirst = ClientFirst("user");
        await harness.WriteClientLineAsync(ScramStart(clientFirst));
        var serverFirstLine = await harness.ReadClientLineAsync();
        var serverFirst = DecodeChallenge(serverFirstLine);
        Assert.Contains(Convert.ToBase64String(record.ScramSalt.ToArray()), serverFirst, StringComparison.Ordinal);
        var clientFinal = ScramClient.CreateClientFinal(clientFirst, serverFirst, keys.ClientKey, keys.StoredKey);
        await harness.WriteClientLineAsync(Convert.ToBase64String(Encoding.UTF8.GetBytes(clientFinal)));
        var success = await harness.ReadClientLineAsync();
        Assert.StartsWith("283 ", success, StringComparison.Ordinal);
        Assert.Equal("user", session.Authentication.Username);
        Assert.True(session.Authentication.IsAuthenticated);
        Assert.True(session.Authorization.AuthorizedReader);
        Assert.True(session.Authorization.PostingPermitted);

        await harness.WriteClientLineAsync("QUIT");
        await harness.ReadClientLineAsync();
        await run;
    }

    [Fact]
    public async Task RealAccountWrongPassword_Uses383Then481()
    {
        var dummy = CreateInjectedDummy();
        var record = CreateRealRecord("user");
        var wrong = ScramClient.Derive("wrong-password", record.ScramSalt.ToArray(), record.ScramIterations);
        await using var harness = await AuthHarness.CreateAsync(Users(record), dummyScram: dummy);
        var session = harness.CreateSession();
        var run = session.RunAsync();
        await harness.ReadGreetingAsync();

        var clientFirst = ClientFirst("user");
        await harness.WriteClientLineAsync(ScramStart(clientFirst));
        var serverFirstLine = await harness.ReadClientLineAsync();
        AssertServerFirstShape(serverFirstLine, ClientNonce);
        var serverFirst = DecodeChallenge(serverFirstLine);
        var clientFinal = ScramClient.CreateClientFinal(clientFirst, serverFirst, wrong.ClientKey, wrong.StoredKey);
        await harness.WriteClientLineAsync(Convert.ToBase64String(Encoding.UTF8.GetBytes(clientFinal)));
        Assert.Equal("481 Authentication failed", await harness.ReadClientLineAsync());
        Assert.False(session.Authentication.IsAuthenticated);
        Assert.Null(session.Authentication.Username);

        await harness.WriteClientLineAsync("QUIT");
        await harness.ReadClientLineAsync();
        await run;
    }

    [Fact]
    public async Task ObservableServerFirst_HasSameShape_AcrossNonAuthenticatingPaths()
    {
        var dummy = CreateInjectedDummy();
        var real = CreateRealRecord("real-user");
        var disabled = MemoryNntpUserRecordStore.Create(
            "disabled-user",
            RealPassword,
            enabled: false,
            scramSalt: real.ScramSalt,
            scramIterations: real.ScramIterations,
            scramStoredKey: real.ScramStoredKey,
            scramServerKey: real.ScramServerKey);
        var denied = MemoryNntpUserRecordStore.Create(
            "noscram-user",
            RealPassword,
            allowScram: false,
            scramSalt: real.ScramSalt,
            scramIterations: real.ScramIterations,
            scramStoredKey: real.ScramStoredKey,
            scramServerKey: real.ScramServerKey);
        var store = Users(real, disabled, denied);
        await using var harness = await AuthHarness.CreateAsync(store, dummyScram: dummy);
        var session = harness.CreateSession();
        var run = session.RunAsync();
        await harness.ReadGreetingAsync();

        foreach (var clientFirst in new[] { "missing-user", "disabled-user", "noscram-user", "real-user" }.Select(ClientFirst))
        {
            await harness.WriteClientLineAsync(ScramStart(clientFirst));
            var line = await harness.ReadClientLineAsync();
            AssertServerFirstShape(line, ClientNonce);
            await harness.WriteClientLineAsync("*");
            Assert.StartsWith("481 ", await harness.ReadClientLineAsync(), StringComparison.Ordinal);
        }

        await harness.WriteClientLineAsync("QUIT");
        await harness.ReadClientLineAsync();
        await run;
    }

    [Fact]
    public async Task DummyVerifierProof_CannotAuthenticate()
    {
        var dummy = CreateInjectedDummy();
        await using var harness = await AuthHarness.CreateAsync(
            Users(CreateRealRecord("real-user")),
            dummyScram: dummy);
        await AssertDummyPathAsync(harness, "missing-user", dummy, completeWithDummyKeys: true);
    }

    [Fact]
    public async Task DummyFailure_DoesNotGrantPrivilegesOrUsername()
    {
        var dummy = CreateInjectedDummy();
        await using var harness = await AuthHarness.CreateAsync(new MemoryNntpUserRecordStore(), dummyScram: dummy);
        var session = harness.CreateSession();
        var run = session.RunAsync();
        await harness.ReadGreetingAsync();

        var clientFirst = ClientFirst("ghost");
        await harness.WriteClientLineAsync(ScramStart(clientFirst));
        var serverFirst = DecodeChallenge(await harness.ReadClientLineAsync());
        var dummyKeys = KeysFromDummy(dummy);
        var clientFinal = ScramClient.CreateClientFinal(clientFirst, serverFirst, dummyKeys.ClientKey, dummyKeys.StoredKey);
        await harness.WriteClientLineAsync(Convert.ToBase64String(Encoding.UTF8.GetBytes(clientFinal)));
        Assert.Equal("481 Authentication failed", await harness.ReadClientLineAsync());

        Assert.False(session.Authentication.IsAuthenticated);
        Assert.Null(session.Authentication.Username);
        Assert.False(session.Authorization.AuthorizedReader);
        Assert.False(session.Authorization.PostingPermitted);
        Assert.False(session.Authorization.AuthorizedTransit);
        Assert.False(session.Authorization.ControlCancelPermitted);
        Assert.False(session.HasSaslExchange);
        Assert.Null(session.AccountPolicy);

        await harness.WriteClientLineAsync("QUIT");
        await harness.ReadClientLineAsync();
        await run;
    }

    [Fact]
    public async Task DummyPath_DoesNotLogAccountStateOrSecrets()
    {
        var dummy = CreateInjectedDummy();
        var recording = new RecordingLoggerFactory();
        await using var harness = await AuthHarness.CreateAsync(new MemoryNntpUserRecordStore(), dummyScram: dummy);
        var session = harness.CreateSession(loggerFactory: recording);
        var run = session.RunAsync();
        await harness.ReadGreetingAsync();

        var clientFirst = ClientFirst("ghost");
        await harness.WriteClientLineAsync(ScramStart(clientFirst));
        var serverFirstLine = await harness.ReadClientLineAsync();
        var serverFirst = DecodeChallenge(serverFirstLine);
        var dummyKeys = KeysFromDummy(dummy);
        var clientFinal = ScramClient.CreateClientFinal(clientFirst, serverFirst, dummyKeys.ClientKey, dummyKeys.StoredKey);
        await harness.WriteClientLineAsync(Convert.ToBase64String(Encoding.UTF8.GetBytes(clientFinal)));
        Assert.Equal("481 Authentication failed", await harness.ReadClientLineAsync());

        await harness.WriteClientLineAsync("QUIT");
        await harness.ReadClientLineAsync();
        await run;

        var joined = string.Join('\n', recording.Messages);
        Assert.DoesNotContain("unknown account", joined, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("disabled account", joined, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("not provisioned", joined, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Convert.ToBase64String(dummy.Credential.Salt.ToArray()), joined, StringComparison.Ordinal);
        Assert.DoesNotContain(Convert.ToHexString(dummy.Credential.StoredKey.ToArray()), joined, StringComparison.Ordinal);
        Assert.DoesNotContain(Convert.ToHexString(dummy.Credential.ServerKey.ToArray()), joined, StringComparison.Ordinal);
        Assert.DoesNotContain(serverFirstLine[4..], joined, StringComparison.Ordinal);
        Assert.DoesNotContain("dummy-secret", joined, StringComparison.Ordinal);
    }

    [Fact]
    public void TryGetAuthorized_RejectsDisabledDeniedAndMalformed()
    {
        var usable = CreateRealRecord("alice");
        Assert.True(ScramStoredCredential.TryGetAuthorized(usable, out var real));
        Assert.Equal(usable.ScramIterations, real.IterationCount);

        Assert.False(ScramStoredCredential.TryGetAuthorized(null, out _));
        var disabled = MemoryNntpUserRecordStore.Create(
            "alice",
            RealPassword,
            enabled: false,
            scramSalt: usable.ScramSalt,
            scramIterations: usable.ScramIterations,
            scramStoredKey: usable.ScramStoredKey,
            scramServerKey: usable.ScramServerKey);
        Assert.False(ScramStoredCredential.TryGetAuthorized(disabled, out _));

        var denied = MemoryNntpUserRecordStore.Create(
            "alice",
            RealPassword,
            allowScram: false,
            scramSalt: usable.ScramSalt,
            scramIterations: usable.ScramIterations,
            scramStoredKey: usable.ScramStoredKey,
            scramServerKey: usable.ScramServerKey);
        Assert.False(ScramStoredCredential.TryGetAuthorized(denied, out _));

        var shortKey = MemoryNntpUserRecordStore.Create(
            "alice",
            RealPassword,
            scramSalt: usable.ScramSalt,
            scramIterations: 4096,
            scramStoredKey: usable.ScramStoredKey[..16],
            scramServerKey: usable.ScramServerKey);
        Assert.False(ScramStoredCredential.TryGetAuthorized(shortKey, out _));
    }

    private static async Task<string> AssertDummyPathAsync(
        AuthHarness harness,
        string username,
        ScramDummyVerifier dummy,
        bool completeWithDummyKeys = true)
    {
        var session = harness.CreateSession();
        var run = session.RunAsync();
        await harness.ReadGreetingAsync();

        var clientFirst = ClientFirst(username);
        await harness.WriteClientLineAsync(ScramStart(clientFirst));
        var serverFirstLine = await harness.ReadClientLineAsync();
        AssertServerFirstShape(serverFirstLine, ClientNonce);
        var serverFirst = DecodeChallenge(serverFirstLine);
        Assert.Contains(Convert.ToBase64String(dummy.Credential.Salt.ToArray()), serverFirst, StringComparison.Ordinal);
        Assert.Contains("i=4096", serverFirst, StringComparison.Ordinal);

        var keys = completeWithDummyKeys
            ? KeysFromDummy(dummy)
            : ScramClient.Derive("unrelated", dummy.Credential.Salt.ToArray(), ScramDummyVerifier.IterationCount);
        var clientFinal = ScramClient.CreateClientFinal(clientFirst, serverFirst, keys.ClientKey, keys.StoredKey);
        await harness.WriteClientLineAsync(Convert.ToBase64String(Encoding.UTF8.GetBytes(clientFinal)));
        Assert.Equal("481 Authentication failed", await harness.ReadClientLineAsync());
        Assert.False(session.Authentication.IsAuthenticated);
        Assert.Null(session.Authentication.Username);
        Assert.False(session.HasSaslExchange);

        await harness.WriteClientLineAsync("QUIT");
        await harness.ReadClientLineAsync();
        await run;
        return serverFirst;
    }

    private static void AssertServerFirstShape(string line, string clientNonce)
    {
        Assert.StartsWith("383 ", line, StringComparison.Ordinal);
        var payload = line[4..];
        var bytes = Convert.FromBase64String(payload);
        var serverFirst = Encoding.UTF8.GetString(bytes);
        Assert.StartsWith("r=" + clientNonce, serverFirst, StringComparison.Ordinal);
        Assert.Contains(",s=", serverFirst, StringComparison.Ordinal);
        Assert.Contains(",i=", serverFirst, StringComparison.Ordinal);
        var salt = Attribute(serverFirst, 's');
        Assert.True(Convert.FromBase64String(salt).Length >= 8);
        Assert.True(int.TryParse(Attribute(serverFirst, 'i'), out var iterations) && iterations > 0);
        var nonce = Attribute(serverFirst, 'r');
        Assert.True(nonce.Length > clientNonce.Length);
    }

    private static ScramDummyVerifier CreateInjectedDummy()
    {
        var salt = Convert.FromHexString("5152535455565758595A5B5C5D5E5F60");
        var keys = ScramClient.Derive("dummy-secret", salt, ScramDummyVerifier.IterationCount);
        return ScramDummyVerifier.Create(
            new ScramStoredCredential(salt, ScramDummyVerifier.IterationCount, keys.StoredKey, keys.ServerKey));
    }

    private static NntpUserRecord CreateRealRecord(string name, bool enabled = true)
    {
        var salt = Convert.FromHexString("4142434445464748494A4B4C4D4E4F50");
        var keys = ScramClient.Derive(RealPassword, salt, 4096);
        return MemoryNntpUserRecordStore.Create(
            name,
            RealPassword,
            enabled: enabled,
            scramSalt: salt,
            scramIterations: 4096,
            scramStoredKey: keys.StoredKey,
            scramServerKey: keys.ServerKey);
    }

    private static MemoryNntpUserRecordStore Users(params NntpUserRecord[] records)
    {
        var store = new MemoryNntpUserRecordStore();
        foreach (var record in records)
        {
            store.Add(record);
        }

        return store;
    }

    private static ScramClient.Keys KeysFromDummy(ScramDummyVerifier dummy) =>
        ScramClient.Derive("dummy-secret", dummy.Credential.Salt.ToArray(), dummy.Credential.IterationCount);

    private static string ClientFirst(string username) => $"n,,n={username},r={ClientNonce}";

    private static string ScramStart(string clientFirst) =>
        $"AUTHINFO SASL SCRAM-SHA-256 {Convert.ToBase64String(Encoding.UTF8.GetBytes(clientFirst))}";

    private static string DecodeChallenge(string line) =>
        Encoding.UTF8.GetString(Convert.FromBase64String(line[4..]));

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
}

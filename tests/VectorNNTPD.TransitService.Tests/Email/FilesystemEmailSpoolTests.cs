using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using VectorNNTPD.TransitService.Configuration;
using VectorNNTPD.TransitService.Email;

namespace VectorNNTPD.TransitService.Tests.Email;

public sealed class FilesystemEmailSpoolTests
{
    [Fact]
    public async Task WriteAsync_CreatesDirectoryAndAtomicEml()
    {
        using var harness = new EmailSpoolHarness();
        Assert.False(Directory.Exists(harness.Directory));
        var path = await harness.Spool.WriteAsync(SmtpTransportTests.Item(), CancellationToken.None);
        Assert.True(Directory.Exists(harness.Directory));
        Assert.True(File.Exists(path));
        Assert.EndsWith(".eml", path, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(harness.TmpFiles());
        var name = Path.GetFileNameWithoutExtension(path);
        Assert.True(EmailSpoolFileName.IsId(name));
        Assert.DoesNotContain("hello", name, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("example.com", name, StringComparison.OrdinalIgnoreCase);
        var bytes = await File.ReadAllBytesAsync(path);
        Assert.True(EmailSpoolRecord.TryParse(bytes, out var item));
        Assert.Equal("envelope@example.com", item!.EnvelopeSender.Address);
        Assert.Contains("Subject: hello", Encoding.ASCII.GetString(item.EncodedMessage.Span), StringComparison.Ordinal);
        Assert.StartsWith(EmailSpoolRecord.Magic, Encoding.ASCII.GetString(bytes), StringComparison.Ordinal);
    }

    [Fact]
    public async Task TmpFiles_AreNeverClaimed()
    {
        using var harness = new EmailSpoolHarness();
        harness.Spool.EnsureDirectory();
        var tmp = Path.Join(harness.Directory, EmailSpoolFileName.NewId() + ".tmp");
        await File.WriteAllBytesAsync(tmp, EmailSpoolRecord.Serialize(SmtpTransportTests.Item()));
        Assert.Null(harness.Spool.TryClaimNext());
        Assert.True(File.Exists(tmp));
    }

    [Fact]
    public void NewId_IsOpaqueAndUnique()
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < 64; i++)
        {
            var id = EmailSpoolFileName.NewId();
            Assert.True(EmailSpoolFileName.IsId(id));
            Assert.True(ids.Add(id));
        }

        Assert.False(EmailSpoolFileName.IsId("not-an-id"));
        Assert.False(EmailSpoolFileName.IsId(new string('A', 32)));
    }

    [Fact]
    public async Task Collision_DoesNotOverwriteExistingEml()
    {
        using var harness = new EmailSpoolHarness();
        var first = await harness.Spool.WriteAsync(SmtpTransportTests.Item(), CancellationToken.None);
        var original = await File.ReadAllBytesAsync(first);
        var second = await harness.Spool.WriteAsync(SmtpTransportTests.Item(), CancellationToken.None);
        Assert.NotEqual(first, second);
        Assert.Equal(original, await File.ReadAllBytesAsync(first));
        Assert.Equal(2, harness.EmlFiles().Length);
    }

    [Fact]
    public void RecoverClaims_RenamesWorkBackToEml()
    {
        using var harness = new EmailSpoolHarness();
        harness.Spool.EnsureDirectory();
        var id = EmailSpoolFileName.NewId();
        var wrk = Path.Join(harness.Directory, id + ".wrk");
        File.WriteAllBytes(wrk, EmailSpoolRecord.Serialize(SmtpTransportTests.Item()));
        harness.Spool.RecoverClaims();
        Assert.False(File.Exists(wrk));
        Assert.True(File.Exists(Path.Join(harness.Directory, id + ".eml")));
    }

    [Fact]
    public async Task Restart_DiscoversPreExistingEml()
    {
        using var harness = new EmailSpoolHarness();
        await harness.Spool.WriteAsync(SmtpTransportTests.Item(), CancellationToken.None);
        var restarted = new FilesystemEmailSpool(
            Options.Create(harness.Options),
            NullLogger<FilesystemEmailSpool>.Instance);
        restarted.RecoverClaims();
        var claim = restarted.TryClaimNext();
        Assert.NotNull(claim);
        Assert.Equal("to@example.com", claim!.Item.Recipients[0].Address);
        restarted.Release(claim);
        Assert.Single(harness.EmlFiles());
    }

    [Fact]
    public async Task ConcurrentClaim_IsExclusive()
    {
        using var harness = new EmailSpoolHarness();
        await harness.Spool.WriteAsync(SmtpTransportTests.Item(), CancellationToken.None);
        var claims = new EmailSpoolClaim?[8];
        Parallel.For(0, claims.Length, i =>
        {
            claims[i] = harness.Spool.TryClaimNext();
        });
        Assert.Equal(1, claims.Count(static claim => claim is not null));
        Assert.Empty(harness.EmlFiles());
        Assert.Single(harness.WorkFiles());
    }

    [Fact]
    public async Task MalformedFile_IsQuarantined()
    {
        using var harness = new EmailSpoolHarness();
        harness.Spool.EnsureDirectory();
        var bad = Path.Join(harness.Directory, EmailSpoolFileName.NewId() + ".eml");
        await File.WriteAllTextAsync(bad, "not a spool file");
        Assert.Null(harness.Spool.TryClaimNext());
        Assert.False(File.Exists(bad));
        Assert.Single(harness.FailedFiles());
    }

    [Fact]
    public async Task DeliveredMarker_IsNeverClaimedRecoveredOrRetransmitted()
    {
        using var harness = new EmailSpoolHarness();
        harness.Spool.EnsureDirectory();
        var id = EmailSpoolFileName.NewId();
        var delivered = Path.Join(harness.Directory, id + ".delivered");
        await File.WriteAllBytesAsync(delivered, EmailSpoolRecord.Serialize(SmtpTransportTests.Item()));
        Assert.Null(harness.Spool.TryClaimNext());
        harness.Spool.RecoverClaims();
        var restarted = new FilesystemEmailSpool(
            Options.Create(harness.Options),
            NullLogger<FilesystemEmailSpool>.Instance);
        restarted.RecoverClaims();
        Assert.Null(restarted.TryClaimNext());
        Assert.True(File.Exists(delivered));
        Assert.False(File.Exists(Path.Join(harness.Directory, id + ".eml")));
        Assert.Empty(harness.EmlFiles());
        Assert.Empty(harness.WorkFiles());
    }

    [Fact]
    public async Task FailedDirectory_IsNeverClaimedOrRequeued()
    {
        using var harness = new EmailSpoolHarness();
        harness.Spool.EnsureDirectory();
        var id = EmailSpoolFileName.NewId();
        var failed = Path.Join(harness.Directory, "failed", id + ".eml");
        await File.WriteAllBytesAsync(failed, EmailSpoolRecord.Serialize(SmtpTransportTests.Item()));
        Assert.Null(harness.Spool.TryClaimNext());
        harness.Spool.RecoverClaims();
        var restarted = new FilesystemEmailSpool(
            Options.Create(harness.Options),
            NullLogger<FilesystemEmailSpool>.Instance);
        restarted.RecoverClaims();
        Assert.Null(restarted.TryClaimNext());
        Assert.True(File.Exists(failed));
        Assert.False(File.Exists(Path.Join(harness.Directory, id + ".eml")));
        Assert.Empty(harness.EmlFiles());
        Assert.Single(harness.FailedFiles());
    }

    [Fact]
    public async Task Complete_DeletesWorkFile_WithoutDeliveredMarker()
    {
        using var harness = new EmailSpoolHarness();
        await harness.Spool.WriteAsync(SmtpTransportTests.Item(), CancellationToken.None);
        var claim = harness.Spool.TryClaimNext();
        Assert.NotNull(claim);
        harness.Spool.Complete(claim!);
        Assert.False(File.Exists(claim!.WorkPath));
        Assert.False(File.Exists(Path.Join(harness.Directory, claim.Id + ".delivered")));
        Assert.Empty(harness.EmlFiles());
        Assert.Empty(harness.WorkFiles());
        Assert.Null(harness.Spool.TryClaimNext());
    }

    [Fact]
    public async Task Complete_DeleteFailure_LeavesDeliveredMarker()
    {
        using var harness = new EmailSpoolHarness();
        await harness.Spool.WriteAsync(SmtpTransportTests.Item(), CancellationToken.None);
        var claim = harness.Spool.TryClaimNext();
        Assert.NotNull(claim);
        harness.Spool.CompleteWorkFileDelete = _ => throw new IOException("injected work-file delete failure");
        harness.Spool.Complete(claim!);
        var delivered = Path.Join(harness.Directory, claim!.Id + ".delivered");
        Assert.True(File.Exists(delivered));
        Assert.False(File.Exists(claim.WorkPath));
        Assert.Empty(harness.EmlFiles());
        Assert.Empty(harness.WorkFiles());
        Assert.Null(harness.Spool.TryClaimNext());
    }

    [Fact]
    public async Task WriteAsync_WhenDirectoryIsAFile_Fails()
    {
        using var harness = new EmailSpoolHarness();
        var blocker = Path.Join(Path.GetTempPath(), "vectornntp-email-spool-blocker-" + Guid.NewGuid().ToString("N"));
        await File.WriteAllBytesAsync(blocker, "x"u8.ToArray());
        try
        {
            harness.Options.Spool.Directory = blocker;
            var spool = new FilesystemEmailSpool(
                Options.Create(harness.Options),
                NullLogger<FilesystemEmailSpool>.Instance);
            await Assert.ThrowsAnyAsync<Exception>(
                () => spool.WriteAsync(SmtpTransportTests.Item(), CancellationToken.None));
        }
        finally
        {
            File.Delete(blocker);
        }
    }
}

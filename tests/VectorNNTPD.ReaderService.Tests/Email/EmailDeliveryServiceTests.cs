using VectorNNTPD.ReaderService.Configuration;
using VectorNNTPD.ReaderService.Email;
using VectorNNTPD.ReaderService.Email.Smtp;
using VectorNNTPD.ReaderService.Tests.TestDoubles;

namespace VectorNNTPD.ReaderService.Tests.Email;

public sealed class EmailDeliveryServiceTests
{
    [Fact]
    public async Task Disabled_DoesNotStartWorkerOrTouchSmtp()
    {
        using var harness = new EmailSpoolHarness(enabled: false);
        var transport = new ScriptedSmtpTransport();
        var service = harness.CreateDelivery(transport);
        await service.StartAsync(CancellationToken.None);
        Assert.Null(service.Execution);
        await service.StopAsync(CancellationToken.None);
        Assert.Equal(0, transport.Calls);
        Assert.False(Directory.Exists(harness.Directory));
    }

    [Fact]
    public async Task Worker_DeliversPreExistingEmlAndDeletesIt()
    {
        using var harness = new EmailSpoolHarness();
        await harness.Spool.WriteAsync(SmtpTransportTests.Item(), CancellationToken.None);
        Assert.Single(harness.EmlFiles());
        var transport = new ScriptedSmtpTransport();
        var service = harness.CreateDelivery(transport);
        await service.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => transport.Calls == 1);
        await service.StopAsync(CancellationToken.None);
        Assert.Equal(1, transport.Calls);
        Assert.Empty(harness.EmlFiles());
        Assert.Empty(harness.WorkFiles());
    }

    [Fact]
    public async Task Worker_DoesNotDeliverDeliveredOrFailedFiles()
    {
        using var harness = new EmailSpoolHarness();
        harness.Spool.EnsureDirectory();
        var deliveredId = EmailSpoolFileName.NewId();
        var failedId = EmailSpoolFileName.NewId();
        await File.WriteAllBytesAsync(
            Path.Join(harness.Directory, deliveredId + ".delivered"),
            EmailSpoolRecord.Serialize(SmtpTransportTests.Item()));
        await File.WriteAllBytesAsync(
            Path.Join(harness.Directory, "failed", failedId + ".eml"),
            EmailSpoolRecord.Serialize(SmtpTransportTests.Item()));
        var transport = new ScriptedSmtpTransport();
        var service = harness.CreateDelivery(transport);
        await service.StartAsync(CancellationToken.None);
        Assert.NotNull(service.Execution);
        await service.StopAsync(CancellationToken.None);
        Assert.Equal(0, transport.Calls);
        Assert.True(File.Exists(Path.Join(harness.Directory, deliveredId + ".delivered")));
        Assert.True(File.Exists(Path.Join(harness.Directory, "failed", failedId + ".eml")));
        Assert.Empty(harness.EmlFiles());
    }

    [Fact]
    public async Task TransientFailure_RetainsEml()
    {
        using var harness = new EmailSpoolHarness(
            smtp: new SmtpOptions
            {
                Host = "127.0.0.1",
                MaxAttempts = 1,
                InitialRetryDelay = TimeSpan.Zero,
                MaximumRetryDelay = TimeSpan.Zero,
            });
        await harness.CreateService().SendAsync(EmailServiceTests.TextMessage());
        var transport = new ScriptedSmtpTransport
        {
            Failures = [new SmtpException(SmtpFailureKind.Transient, "4xx") { StatusCode = 451 }],
        };
        var service = harness.CreateDelivery(transport);
        await service.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => transport.Calls >= 1 && harness.EmlFiles().Length == 1);
        await service.StopAsync(CancellationToken.None);
        Assert.Single(harness.EmlFiles());
        Assert.Empty(harness.FailedFiles());
    }

    [Fact]
    public async Task PermanentFailure_MovesToFailed()
    {
        using var harness = new EmailSpoolHarness();
        await harness.CreateService().SendAsync(EmailServiceTests.TextMessage());
        var transport = new ScriptedSmtpTransport
        {
            Failures = [new SmtpException(SmtpFailureKind.Permanent, "5xx") { StatusCode = 550 }],
        };
        var service = harness.CreateDelivery(transport);
        await service.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => transport.Calls == 1 && harness.FailedFiles().Length == 1);
        await service.StopAsync(CancellationToken.None);
        Assert.Empty(harness.EmlFiles());
        Assert.Single(harness.FailedFiles());
    }

    [Fact]
    public async Task Transient_IsRetriedThenSucceeds()
    {
        using var harness = new EmailSpoolHarness(
            smtp: new SmtpOptions
            {
                Host = "127.0.0.1",
                MaxAttempts = 3,
                InitialRetryDelay = TimeSpan.Zero,
                MaximumRetryDelay = TimeSpan.Zero,
            });
        var transport = new ScriptedSmtpTransport
        {
            Failures = [new SmtpException(SmtpFailureKind.Transient, "4xx") { StatusCode = 451 }],
        };
        var service = harness.CreateDelivery(transport);
        await service.StartAsync(CancellationToken.None);
        Assert.Equal(EmailEnqueueStatus.Accepted, (await harness.CreateService().SendAsync(EmailServiceTests.TextMessage())).Status);
        await WaitUntilAsync(() => transport.Calls == 2 && harness.EmlFiles().Length == 0);
        await service.StopAsync(CancellationToken.None);
        Assert.Equal(2, transport.Calls);
    }

    [Fact]
    public async Task Shutdown_StopsAcceptingAndRetainsUndelivered()
    {
        using var harness = new EmailSpoolHarness();
        var transport = new ScriptedSmtpTransport();
        var service = harness.CreateDelivery(transport);
        await harness.Spool.WriteAsync(SmtpTransportTests.Item(), CancellationToken.None);
        await service.StopAsync(CancellationToken.None);
        Assert.False(harness.Spool.IsAccepting);
        Assert.Equal(
            EmailEnqueueStatus.Unavailable,
            (await harness.CreateService().SendAsync(EmailServiceTests.TextMessage())).Status);
        Assert.Single(harness.EmlFiles());
    }

    [Fact]
    public async Task Cancellation_RetainsEml()
    {
        using var harness = new EmailSpoolHarness();
        await harness.CreateService().SendAsync(EmailServiceTests.TextMessage());
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var transport = new ScriptedSmtpTransport
        {
            BeforeSend = async cancellationToken =>
            {
                started.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            },
        };
        var service = harness.CreateDelivery(transport);
        await service.StartAsync(CancellationToken.None);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await service.StopAsync(CancellationToken.None);
        Assert.True(harness.EmlFiles().Length + harness.WorkFiles().Length == 1);
        harness.Spool.RecoverClaims();
        Assert.Single(harness.EmlFiles());
    }

    [Fact]
    public async Task Worker_DeliversThroughFakeSmtp()
    {
        await using var smtp = new FakeSmtpServer();
        using var harness = new EmailSpoolHarness(smtp: smtp.ClientOptions(SmtpSecurityMode.None));
        var transport = SmtpTransportTests.Create(harness.Options.Smtp);
        var service = harness.CreateDelivery(transport);
        await service.StartAsync(CancellationToken.None);
        Assert.Equal(EmailEnqueueStatus.Accepted, (await harness.CreateService().SendAsync(EmailServiceTests.TextMessage())).Status);
        await WaitUntilAsync(() => smtp.LastMessage is not null);
        await service.StopAsync(CancellationToken.None);
        Assert.Contains("Subject: hello", System.Text.Encoding.ASCII.GetString(smtp.LastMessage!), StringComparison.Ordinal);
        Assert.Empty(harness.EmlFiles());
    }

    [Fact]
    public async Task TwoWorkers_DoNotDeliverTheSameFile()
    {
        using var harness = new EmailSpoolHarness();
        await harness.Spool.WriteAsync(SmtpTransportTests.Item(), CancellationToken.None);
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var slow = new ScriptedSmtpTransport
        {
            BeforeSend = async _ =>
            {
                firstStarted.TrySetResult();
                await release.Task;
            },
        };
        var fast = new ScriptedSmtpTransport();
        var a = harness.CreateDelivery(slow);
        var b = harness.CreateDelivery(fast);
        await a.StartAsync(CancellationToken.None);
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await b.StartAsync(CancellationToken.None);
        Assert.Equal(0, fast.Calls);
        release.TrySetResult();
        await WaitUntilAsync(() => slow.Calls == 1);
        await a.StopAsync(CancellationToken.None);
        await b.StopAsync(CancellationToken.None);
        Assert.Equal(1, slow.Calls + fast.Calls);
        Assert.Empty(harness.EmlFiles());
    }

    [Fact]
    public void RetryDelay_IsBounded()
    {
        var smtp = new SmtpOptions
        {
            InitialRetryDelay = TimeSpan.FromSeconds(2),
            MaximumRetryDelay = TimeSpan.FromSeconds(5),
        };
        var delay = EmailRetryDelay.Compute(smtp, attempt: 8, new Random(1));
        Assert.True(delay <= TimeSpan.FromSeconds(5));
        Assert.True(delay >= TimeSpan.Zero);
    }

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!predicate())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException();
            }

            await Task.Delay(20);
        }
    }
}

internal sealed class ScriptedSmtpTransport : ISmtpTransport
{
    public List<SmtpException> Failures { get; init; } = [];

    public Func<CancellationToken, Task>? BeforeSend { get; init; }

    public int Calls { get; private set; }

    public async Task<SmtpSendResult> SendAsync(EmailWorkItem item, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (BeforeSend is not null)
        {
            await BeforeSend(cancellationToken).ConfigureAwait(false);
        }

        cancellationToken.ThrowIfCancellationRequested();
        Calls++;
        if (Failures.Count > 0)
        {
            var failure = Failures[0];
            Failures.RemoveAt(0);
            throw failure;
        }

        return new SmtpSendResult
        {
            Succeeded = true,
            Recipients = [new SmtpRecipientResult(item.Recipients[0], true, 250, "OK")],
            DataStatusCode = 250,
        };
    }
}

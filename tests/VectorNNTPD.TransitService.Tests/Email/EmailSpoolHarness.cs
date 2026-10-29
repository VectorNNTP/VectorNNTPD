using Microsoft.Extensions.Logging.Abstractions;
using VectorNNTPD.TransitService.Configuration;
using VectorNNTPD.TransitService.Email;
using VectorNNTPD.TransitService.Email.Smtp;

namespace VectorNNTPD.TransitService.Tests.Email;

internal sealed class EmailSpoolHarness : IDisposable
{
    public EmailSpoolHarness(bool enabled = true, SmtpOptions? smtp = null)
    {
        Directory = Path.Join(
            Path.GetTempPath(),
            "vectornntp-email-spool",
            Guid.NewGuid().ToString("N"));
        Options = new EmailOptions
        {
            Enabled = enabled,
            DefaultFrom = "from@example.com",
            EnvelopeSender = "envelope@example.com",
            Smtp = smtp ?? new SmtpOptions
            {
                Host = "127.0.0.1",
                Port = 25,
                Security = SmtpSecurityMode.None,
                MaxAttempts = 1,
                InitialRetryDelay = TimeSpan.Zero,
                MaximumRetryDelay = TimeSpan.Zero,
            },
            Spool = new EmailSpoolOptions
            {
                Directory = Directory,
                ScanInterval = TimeSpan.FromMilliseconds(20),
                ShutdownTimeout = TimeSpan.FromSeconds(2),
            },
        };
        Spool = new FilesystemEmailSpool(Microsoft.Extensions.Options.Options.Create(Options), NullLogger<FilesystemEmailSpool>.Instance);
    }

    public string Directory { get; }

    public EmailOptions Options { get; }

    public FilesystemEmailSpool Spool { get; }

    public EmailService CreateService() =>
        new(
            Spool,
            new Rfc5322MessageEncoder(),
            Microsoft.Extensions.Options.Options.Create(Options),
            NullLogger<EmailService>.Instance);

    public EmailDeliveryService CreateDelivery(ISmtpTransport transport) =>
        new(
            Spool,
            transport,
            Microsoft.Extensions.Options.Options.Create(Options),
            NullLogger<EmailDeliveryService>.Instance);

    public string[] EmlFiles() =>
        System.IO.Directory.Exists(Directory)
            ? System.IO.Directory.GetFiles(Directory, "*.eml")
            : [];

    public string[] WorkFiles() =>
        System.IO.Directory.Exists(Directory)
            ? System.IO.Directory.GetFiles(Directory, "*.wrk")
            : [];

    public string[] TmpFiles() =>
        System.IO.Directory.Exists(Directory)
            ? System.IO.Directory.GetFiles(Directory, "*.tmp")
            : [];

    public string[] FailedFiles()
    {
        var failed = Path.Join(Directory, "failed");
        return System.IO.Directory.Exists(failed)
            ? System.IO.Directory.GetFiles(failed, "*.eml")
            : [];
    }

    public void Dispose()
    {
        try
        {
            if (System.IO.Directory.Exists(Directory))
            {
                System.IO.Directory.Delete(Directory, recursive: true);
            }
        }
        catch (IOException ex)
        {
            _ = ex.GetType(); // observed: expected during test teardown/cleanup
        }
        catch (UnauthorizedAccessException ex)
        {
            _ = ex.GetType(); // observed: expected during test teardown/cleanup
        }
    }
}

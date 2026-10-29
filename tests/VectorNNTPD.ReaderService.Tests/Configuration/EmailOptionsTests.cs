using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using VectorNNTPD.ReaderService.Configuration;

namespace VectorNNTPD.ReaderService.Tests.Configuration;

public sealed class EmailOptionsTests
{
    [Fact]
    public void Disabled_EmptyHost_IsValid()
    {
        var options = Bind(
            """
            {
              "Email": {
                "Enabled": false
              }
            }
            """);
        Assert.False(options.Enabled);
        Assert.True(new EmailOptionsValidator().Validate(null, options).Succeeded);
    }

    [Fact]
    public void ProductionAppsettings_IsDisabledAndValid()
    {
        var path = FindProductionAppsettings();
        var configuration = new ConfigurationBuilder()
            .AddJsonFile(path, optional: false)
            .Build();
        var options = new EmailOptions();
        configuration.GetSection(EmailOptions.SectionName).Bind(options);
        options.Smtp ??= new SmtpOptions();
        options.Spool ??= new EmailSpoolOptions();
        Assert.False(options.Enabled);
        Assert.Equal("noreply@usenet.ninja", options.DefaultFrom);
        Assert.Equal(SmtpSecurityMode.StartTls, options.Smtp.Security);
        Assert.True(options.Smtp.RequireTlsForAuthentication);
        Assert.True(string.IsNullOrEmpty(options.Smtp.Password));
        Assert.Equal(EmailSpoolOptions.DefaultDirectory, options.Spool.Directory);
        Assert.True(new EmailOptionsValidator().Validate(null, options).Succeeded);

        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var email = document.RootElement.GetProperty("Email");
        var smtp = email.GetProperty("Smtp");
        Assert.False(smtp.TryGetProperty("EhloHostname", out _));
        Assert.False(smtp.TryGetProperty("DangerousAcceptAnyServerCertificate", out _));
        Assert.False(email.TryGetProperty("Queue", out _));
        Assert.True(email.TryGetProperty("Spool", out _));
    }

    [Fact]
    public void Enabled_RequiresHostAndFrom()
    {
        var options = new EmailOptions
        {
            Enabled = true,
            DefaultFrom = string.Empty,
            Smtp = new SmtpOptions { Host = string.Empty, Port = 587 },
        };
        var result = new EmailOptionsValidator().Validate(null, options);
        Assert.False(result.Succeeded);
        var failures = result.Failures ?? [];
        Assert.Contains(failures, static f => f.Contains("Host", StringComparison.Ordinal));
        Assert.Contains(failures, static f => f.Contains("DefaultFrom", StringComparison.Ordinal));
        Assert.DoesNotContain(failures, static f => f.Contains("secret", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Enabled_AuthWithoutTls_FailsUnlessExplicit()
    {
        var options = ValidEnabled();
        options.Smtp.Username = "user";
        options.Smtp.Password = "password";
        options.Smtp.Security = SmtpSecurityMode.None;
        options.Smtp.RequireTlsForAuthentication = true;
        Assert.False(new EmailOptionsValidator().Validate(null, options).Succeeded);

        options.Smtp.RequireTlsForAuthentication = false;
        Assert.True(new EmailOptionsValidator().Validate(null, options).Succeeded);
    }

    [Fact]
    public void UsernameWithoutPassword_FailsWithoutEchoingSecret()
    {
        var options = ValidEnabled();
        options.Smtp.Username = "user";
        options.Smtp.Password = string.Empty;
        var result = new EmailOptionsValidator().Validate(null, options);
        Assert.False(result.Succeeded);
        Assert.DoesNotContain(result.Failures ?? [], static f => f.Contains("hunter2", StringComparison.Ordinal));
    }

    [Fact]
    public void InvalidPortAndScanInterval_Fail()
    {
        var options = ValidEnabled();
        options.Smtp.Port = 0;
        options.Spool.ScanInterval = TimeSpan.Zero;
        var result = new EmailOptionsValidator().Validate(null, options);
        Assert.False(result.Succeeded);
    }

    [Fact]
    public void Options_InvalidEnabledHost_FailsOnStart()
    {
        var services = new ServiceCollection();
        using var jsonStream = new MemoryStream(Encoding.UTF8.GetBytes(
                """
                {
                  "Email": {
                    "Enabled": true,
                    "DefaultFrom": "noreply@example.com",
                    "Smtp": { "Host": "", "Port": 587, "Security": "StartTls" }
                  }
                }
                """));
        var configuration = new ConfigurationBuilder()
            .AddJsonStream(jsonStream)
            .Build();
        services.AddSingleton<IConfiguration>(configuration);
        services
            .AddOptions<EmailOptions>()
            .BindConfiguration(EmailOptions.SectionName)
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<EmailOptions>, EmailOptionsValidator>();
        using var provider = services.BuildServiceProvider();
        Assert.Throws<OptionsValidationException>(
            () => provider.GetRequiredService<IOptions<EmailOptions>>().Value);
    }

    private static EmailOptions ValidEnabled() =>
        new()
        {
            Enabled = true,
            DefaultFrom = "noreply@example.com",
            EnvelopeSender = "noreply@example.com",
            Smtp = new SmtpOptions
            {
                Host = "smtp.example.com",
                Port = 587,
                Security = SmtpSecurityMode.StartTls,
            },
        };

    private static EmailOptions Bind(string json)
    {
        var options = new EmailOptions();
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        new ConfigurationBuilder()
            .AddJsonStream(stream)
            .Build()
            .GetSection(EmailOptions.SectionName)
            .Bind(options);
        options.Smtp ??= new SmtpOptions();
        options.Spool ??= new EmailSpoolOptions();
        return options;
    }

    private static string FindProductionAppsettings()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Join(dir.FullName, "src", "VectorNNTPD.ReaderService", "VectorNNTPD.ReaderService.json");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        throw new FileNotFoundException("Could not locate src/VectorNNTPD.ReaderService/VectorNNTPD.ReaderService.json.");
    }
}

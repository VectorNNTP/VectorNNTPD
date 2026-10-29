using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using VectorNNTPD.ReaderService.Configuration;

namespace VectorNNTPD.ReaderService.Tests.Configuration;

public sealed class ModerationOptionsTests
{
    [Fact]
    public void Bind_EmptySection_IsValid()
    {
        var options = Bind(
            """
            {
              "Moderation": {
                "Moderators": []
              }
            }
            """);

        Assert.Empty(options.Moderators);
        Assert.True(new ModerationOptionsValidator().Validate(null, options).Succeeded);
    }

    [Fact]
    public void Bind_OmittedSection_IsValid()
    {
        var options = Bind("""{ "Nntpd": { "ServerId": 1 } }""");
        Assert.Empty(options.Moderators);
        Assert.True(new ModerationOptionsValidator().Validate(null, options).Succeeded);
    }

    [Fact]
    public void Bind_LeftoverModerators_FailsValidation()
    {
        var options = Bind(
            """
            {
              "Moderation": {
                "Moderators": [
                  {
                    "Pattern": "comp.example.*",
                    "Address": "moderator@example.com",
                    "Username": "moderator-example"
                  }
                ]
              }
            }
            """);

        var result = new ModerationOptionsValidator().Validate(null, options);
        Assert.True(result.Failed);
        Assert.Contains("nntpmoderators", result.FailureMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void Bind_ProductionAppsettings_KeepsSourceProvenanceWithoutStaticList()
    {
        var configuration = new ConfigurationBuilder()
            .AddJsonFile(FindProductionAppsettings(), optional: false)
            .Build();
        var options = new ModerationOptions();
        configuration.GetSection(ModerationOptions.SectionName).Bind(options);
        options.Moderators ??= [];
        Assert.True(new ModerationOptionsValidator().Validate(null, options).Succeeded);
        Assert.Equal("https://raw.githubusercontent.com/InterNetNews/inn/main/samples/moderators", options.Source.InnUrl);
        Assert.Equal("InnUrl", options.Source.RetrievedFrom);
        Assert.Equal("https://github.com/InterNetNews/inn/blob/main/samples/moderators", options.Source.Url);
        Assert.Empty(options.Moderators);
    }

    [Fact]
    public void Options_LeftoverModerators_FailsOnStart()
    {
        var services = new ServiceCollection();
        using var jsonStream = new MemoryStream(Encoding.UTF8.GetBytes(
                """
                {
                  "Moderation": {
                    "Moderators": [
                      { "Pattern": "comp.example.*", "Address": "mod@example.com", "Username": "mod" }
                    ]
                  }
                }
                """));
        var configuration = new ConfigurationBuilder()
            .AddJsonStream(jsonStream)
            .Build();
        services.AddSingleton<IConfiguration>(configuration);
        services
            .AddOptions<ModerationOptions>()
            .BindConfiguration(ModerationOptions.SectionName)
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<ModerationOptions>, ModerationOptionsValidator>();

        using var provider = services.BuildServiceProvider();
        var resolve = () => provider.GetRequiredService<IOptions<ModerationOptions>>().Value;
        Assert.Throws<OptionsValidationException>(resolve);
    }

    private static ModerationOptions Bind(string json)
    {
        var options = new ModerationOptions();
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        new ConfigurationBuilder()
            .AddJsonStream(stream)
            .Build()
            .GetSection(ModerationOptions.SectionName)
            .Bind(options);
        options.Moderators ??= [];
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

using System.Reflection;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using VectorNNTPD.ReaderService.Configuration;

namespace VectorNNTPD.ReaderService.Tests.Configuration;

public sealed class ControlOptionsTests
{
    private const string AioeFingerprint = "22031AAC51E7C7FD664F1D8090DF6C712322A7F8";
    private const string AioeFingerprintGrouped = "2203 1AAC 51E7 C7FD 664F  1D80 90DF 6C71 2322 A7F8";
    private const string BigEightNewsgroups = "comp.*|humanities.*|misc.*|news.*|rec.*|sci.*|soc.*|talk.*";
    private const int SourceAuthorityCount = 112;
    private const int SourceAuthorizationCount = 317;

    [Fact]
    public void Bind_ControlSection_Succeeds()
    {
        var options = Bind(
            """
            {
              "Control": {
                "PgpAuthorities": {
                  "Source": {
                    "LastModified": "2023-08-05"
                  },
                  "Authorities": [
                    { "Name": "AIOE", "KeyFingerprint": "22031AAC51E7C7FD664F1D8090DF6C712322A7F8" }
                  ]
                }
              }
            }
            """);

        Assert.Equal("2023-08-05", options.PgpAuthorities.Source.LastModified);
        Assert.Single(options.PgpAuthorities.Authorities);
        Assert.Equal("AIOE", options.PgpAuthorities.Authorities[0].Name);
        Assert.True(new ControlOptionsValidator().Validate(null, options).Succeeded);
    }

    [Fact]
    public void Bind_PgpAuthorities_Succeeds()
    {
        var options = Bind(
            """
            {
              "Control": {
                "PgpAuthorities": {
                  "Source": {
                    "Url": "https://downloads.isc.org/pub/usenet/CONFIG/control.ctl",
                    "InnUrl": "https://raw.githubusercontent.com/InterNetNews/inn/main/samples/control.ctl",
                    "LastModified": "2023-08-05",
                    "RetrievedFrom": "InnUrl"
                  },
                  "Authorities": []
                }
              }
            }
            """);

        Assert.Equal("InnUrl", options.PgpAuthorities.Source.RetrievedFrom);
        Assert.Equal(
            "https://downloads.isc.org/pub/usenet/CONFIG/control.ctl",
            options.PgpAuthorities.Source.Url);
        Assert.Equal(
            "https://raw.githubusercontent.com/InterNetNews/inn/main/samples/control.ctl",
            options.PgpAuthorities.Source.InnUrl);
        Assert.Empty(options.PgpAuthorities.Authorities);
    }

    [Fact]
    public void Bind_KnownAuthority_HasExpectedFingerprint()
    {
        var options = BindProduction();
        var aioe = Assert.Single(options.PgpAuthorities.Authorities, a => a.Name == "AIOE");
        Assert.Equal(AioeFingerprint, aioe.KeyFingerprint);
        Assert.Equal("usenet@aioe.org", aioe.Contact);
        Assert.Equal("http://news.aioe.org/hierarchy/aioe.txt", aioe.KeyUrl);
    }

    [Fact]
    public void FingerprintNormalization_PreservesHexBytes()
    {
        Assert.Equal(AioeFingerprint, PgpAuthorityFingerprint.Normalize(AioeFingerprintGrouped));
        Assert.Equal(AioeFingerprint, PgpAuthorityFingerprint.Normalize(AioeFingerprint.ToLowerInvariant()));
        Assert.True(PgpAuthorityFingerprint.TryNormalize(AioeFingerprintGrouped, out var normalized));
        Assert.Equal(AioeFingerprint, normalized);

        var sourceDigits = new string(AioeFingerprintGrouped.Where(static c => !char.IsWhiteSpace(c)).ToArray());
        Assert.Equal(sourceDigits, normalized, ignoreCase: true);
        Assert.False(PgpAuthorityFingerprint.TryNormalize("not-a-fingerprint", out _));
        Assert.False(PgpAuthorityFingerprint.TryNormalize("220", out _));
        Assert.False(PgpAuthorityFingerprint.TryNormalize("   ", out _));
    }

    [Fact]
    public void Bind_PostConfigure_NormalizesGroupedFingerprint()
    {
        var configuration = LoadJson(
            """
            {
              "Control": {
                "PgpAuthorities": {
                  "Authorities": [
                    { "Name": "AIOE", "KeyFingerprint": "2203 1AAC 51E7 C7FD 664F  1D80 90DF 6C71 2322 A7F8" }
                  ]
                }
              }
            }
            """);

        var services = new ServiceCollection();
        services
            .AddOptions<ControlOptions>()
            .Bind(configuration.GetSection(ControlOptions.SectionName))
            .PostConfigure(static options =>
            {
                // codeql[cs/linq/missed-where] TryNormalize out-params; foreach preserves mutate-in-place clarity.
                foreach (var authority in options.PgpAuthorities.Authorities)
                {
                    if (PgpAuthorityFingerprint.TryNormalize(authority.KeyFingerprint, out var normalized))
                    {
                        authority.KeyFingerprint = normalized;
                    }
                }
            });

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<ControlOptions>>().Value;
        Assert.Equal(AioeFingerprint, options.PgpAuthorities.Authorities[0].KeyFingerprint);
    }

    [Fact]
    public void Bind_MultipleHierarchyRules_RemainDistinct()
    {
        var options = BindProduction();
        var de = Assert.Single(options.PgpAuthorities.Authorities, a => a.Name == "DE");
        var deAlt = Assert.Single(options.PgpAuthorities.Authorities, a => a.Name == "DE.ALT");
        Assert.NotSame(de, deAlt);
        Assert.Equal("de.*", de.Authorizations[0].Newsgroups);
        Assert.Equal("de.alt.*", deAlt.Authorizations[0].Newsgroups);

        var bigEight = Assert.Single(
            options.PgpAuthorities.Authorities,
            a => a.Name == "COMP, HUMANITIES, MISC, NEWS, REC, SCI, SOC, TALK");
        Assert.Equal(3, bigEight.Authorizations.Length);
        Assert.All(bigEight.Authorizations, rule => Assert.Equal(BigEightNewsgroups, rule.Newsgroups));
        Assert.Equal(
            ["checkgroups", "newgroup", "rmgroup"],
            bigEight.Authorizations.Select(static r => r.Message).ToArray());

        var maus = Assert.Single(options.PgpAuthorities.Authorities, a => a.Name == "MAUS");
        Assert.Equal(6, maus.Authorizations.Length);
        Assert.Equal(2, maus.Authorizations.Count(static r => r.Message == "checkgroups"));
        Assert.Contains(maus.Authorizations, static r => r.From == "guenter@gst0hb.hb.provi.de");
        Assert.Contains(maus.Authorizations, static r => r.From == "guenter@gst0hb.north.de");
    }

    [Fact]
    public void Validate_MissingOptionalMetadata_DoesNotFail()
    {
        var options = new ControlOptions
        {
            PgpAuthorities = new PgpAuthoritiesOptions
            {
                Authorities =
                [
                    new PgpAuthorityOptions { Name = "LOCAL-TEST" },
                ],
            },
        };

        var result = new ControlOptionsValidator().Validate(null, options);
        Assert.True(result.Succeeded, string.Join("; ", result.Failures ?? []));
    }

    [Fact]
    public void CatalogueTypesAndProductionJson_HaveNoPrivateKeyOrPassphraseConfiguration()
    {
        var secretFragments = new[]
        {
            "PrivateKey",
            "Passphrase",
            "Password",
            "SecretKey",
            "Armored",
            "BEGIN PGP PRIVATE",
        };

        Type[] catalogueTypes =
        [
            typeof(ControlOptions),
            typeof(PgpAuthoritiesOptions),
            typeof(PgpAuthorityOptions),
            typeof(PgpAuthoritySourceOptions),
            typeof(PgpAuthorityAuthorizationOptions),
        ];

        foreach (var type in catalogueTypes)
        {
            foreach (var property in type.GetProperties(BindingFlags.Instance | BindingFlags.Public))
            {
                Assert.DoesNotContain(
                    secretFragments,
                    fragment => property.Name.Contains(fragment, StringComparison.OrdinalIgnoreCase));
            }
        }

        using var document = JsonDocument.Parse(File.ReadAllText(FindProductionAppsettings()));
        var controlJson = document.RootElement.GetProperty("Control").GetRawText();
        foreach (var fragment in secretFragments)
        {
            Assert.DoesNotContain(fragment, controlJson, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void Validate_EmptyAuthorityConfiguration_Succeeds()
    {
        Assert.True(new ControlOptionsValidator().Validate(null, new ControlOptions()).Succeeded);
        Assert.True(
            new ControlOptionsValidator()
                .Validate(null, new ControlOptions { PgpAuthorities = new PgpAuthoritiesOptions() })
                .Succeeded);

        var omitted = Bind("{}");
        Assert.Empty(omitted.PgpAuthorities.Authorities);
        Assert.True(new ControlOptionsValidator().Validate(null, omitted).Succeeded);

        var emptySection = Bind("""{ "Control": { } }""");
        Assert.Empty(emptySection.PgpAuthorities.Authorities);
        Assert.True(new ControlOptionsValidator().Validate(null, emptySection).Succeeded);
    }

    [Fact]
    public void ProductionAppsettings_BindsCompleteCatalogue()
    {
        var options = BindProduction();
        var result = new ControlOptionsValidator().Validate(null, options);
        Assert.True(result.Succeeded, string.Join("; ", result.Failures ?? []));

        Assert.Equal("2023-08-05", options.PgpAuthorities.Source.LastModified);
        Assert.Equal("InnUrl", options.PgpAuthorities.Source.RetrievedFrom);
        Assert.Equal(SourceAuthorityCount, options.PgpAuthorities.Authorities.Length);
        Assert.Equal(
            SourceAuthorizationCount,
            options.PgpAuthorities.Authorities.Sum(static a => a.Authorizations.Length));
        Assert.Equal(55, options.PgpAuthorities.Authorities.Count(static a => a.KeyFingerprint is not null));
        Assert.All(
            options.PgpAuthorities.Authorities.Where(static a => a.KeyFingerprint is not null),
            static a =>
            {
                Assert.True(PgpAuthorityFingerprint.TryNormalize(a.KeyFingerprint, out var normalized));
                Assert.Equal(normalized, a.KeyFingerprint);
            });
    }

    [Fact]
    public void Validate_RejectsNonHexadecimalFingerprint()
    {
        var options = new ControlOptions
        {
            PgpAuthorities = new PgpAuthoritiesOptions
            {
                Authorities =
                [
                    new PgpAuthorityOptions { Name = "AIOE", KeyFingerprint = "not-hex" },
                ],
            },
        };

        var result = new ControlOptionsValidator().Validate(null, options);
        Assert.True(result.Failed);
    }

    private static ControlOptions BindProduction()
    {
        var configuration = new ConfigurationBuilder()
            .AddJsonFile(FindProductionAppsettings(), optional: false)
            .Build();
        var options = new ControlOptions();
        configuration.GetSection(ControlOptions.SectionName).Bind(options);
        return options;
    }

    private static ControlOptions Bind(string json)
    {
        var options = new ControlOptions();
        LoadJson(json).GetSection(ControlOptions.SectionName).Bind(options);
        return options;
    }

    private static IConfigurationRoot LoadJson(string json)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        return new ConfigurationBuilder()
            .AddJsonStream(stream)
            .Build();
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

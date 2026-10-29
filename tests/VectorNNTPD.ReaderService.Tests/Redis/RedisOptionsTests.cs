using Microsoft.Extensions.Configuration;
using VectorNNTPD.ReaderService.Configuration;

namespace VectorNNTPD.ReaderService.Tests.Redis;

public sealed class RedisOptionsTests
{
    [Fact]
    public void DefaultPort_Is6379()
    {
        Assert.Equal(6379, RedisOptions.DefaultPort);
        Assert.Equal(6379, new RedisOptions().Port);
    }

    [Fact]
    public void Bind_ReadsHostArrayAndPort()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["Redis:Host:0"] = "redis-01.example.net",
                    ["Redis:Host:1"] = "redis-02.example.net",
                    ["Redis:Port"] = "6380",
                })
            .Build();

        var options = new RedisOptions();
        configuration.GetSection(RedisOptions.SectionName).Bind(options);

        Assert.Equal(["redis-01.example.net", "redis-02.example.net"], options.Host);
        Assert.Equal(6380, options.Port);
    }

    [Fact]
    public void Bind_OmitsPort_DefaultsTo6379()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["Redis:Host:0"] = "127.0.0.1",
                })
            .Build();

        var options = new RedisOptions();
        configuration.GetSection(RedisOptions.SectionName).Bind(options);
        Assert.Equal(6379, options.Port);
    }

    [Fact]
    public void Validate_Fails_WhenHostMissing()
    {
        var result = new RedisOptionsValidator().Validate(null, new RedisOptions());
        Assert.True(result.Failed);
    }

    [Fact]
    public void Validate_Fails_WhenHostEntryEmpty()
    {
        var result = new RedisOptionsValidator().Validate(null, new RedisOptions { Host = [" "] });
        Assert.True(result.Failed);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(65536)]
    public void Validate_Fails_WhenPortOutOfRange(int port)
    {
        var result = new RedisOptionsValidator().Validate(
            null,
            new RedisOptions { Host = ["127.0.0.1"], Port = port });
        Assert.True(result.Failed);
    }

    [Fact]
    public void Validate_Succeeds_ForValidHosts()
    {
        var result = new RedisOptionsValidator().Validate(
            null,
            new RedisOptions { Host = ["redis-01.example.net", "10.0.0.8"], Port = 6379 });
        Assert.True(result.Succeeded);
    }

}

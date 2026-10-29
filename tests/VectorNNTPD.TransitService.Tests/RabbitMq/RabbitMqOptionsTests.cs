using Microsoft.Extensions.Configuration;
using VectorNNTPD.TransitService.Configuration;
using VectorNNTPD.TransitService.Hosting;
using VectorNNTPD.Common.Messaging.RabbitMq;

namespace VectorNNTPD.TransitService.Tests.RabbitMq;

public sealed class RabbitMqOptionsTests
{
    [Fact]
    public void SectionName_IsRabbitMQ()
    {
        Assert.Equal("RabbitMQ", RabbitMqOptions.SectionName);
    }

    [Fact]
    public void Defaults_MatchBackFillerContract()
    {
        var options = new RabbitMqOptions();

        Assert.Equal(1024, options.WorkRequestMaxPayloadBytes);
        Assert.Equal(60, options.ChannelLeaseTimeoutSeconds);
        Assert.Equal(30, options.RpcTimeoutSeconds);
        Assert.Equal(30, options.ConnectionBlockedTimeoutSeconds);
        Assert.Empty(options.Hosts!);
        Assert.Null(options.Username);
        Assert.Null(options.Password);
        Assert.Equal("/", options.VirtualHost);
        Assert.True(options.EnableSsl);
        Assert.Equal(5672, options.Port);
        Assert.Equal(512, options.ChannelPoolSize);
        Assert.Equal(4, options.MinConnections);
        Assert.Equal(16, options.MaxConnections);
        Assert.Equal(1024, options.MaxPendingLeaseWaiters);
        Assert.Equal(300, options.ConnectionScaleDownIdleSeconds);
        Assert.Equal(30, options.ScaleDownCooldownSeconds);
        Assert.Equal(5, options.NetworkRecoveryIntervalSeconds);
        Assert.Equal(250, options.PoolReconnectBaseDelayMs);
        Assert.Equal(30000, options.PoolReconnectMaxDelayMs);
        Assert.Equal(300, options.MinimumConnectionLifetimeSeconds);
        Assert.Equal(10, options.PublishConfirmTimeoutSeconds);
        Assert.Equal(30, options.MaximumShutdownDrainTimeoutSeconds);
        Assert.Equal(0.75, options.DegradedThreshold);
        Assert.Equal(5, options.UnhealthyThreshold);
        Assert.Equal(60, options.RequestedHeartbeatSeconds);
        Assert.Equal(30, options.SocketTimeoutSeconds);
        Assert.Equal(2047, options.RequestedChannelMax);
        Assert.Null(options.ConsumerPrefetchCount);
        Assert.Null(options.DiagnosticPayloadCorrelationId);
        Assert.NotNull(options.Management);
        Assert.Null(options.Management.BaseUrl);
        Assert.Equal(5, options.Management.RequestTimeoutSeconds);
    }

    [Fact]
    public void Bind_ReadsIdenticalShape()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["RabbitMQ:Hosts:0"] = "rabbit-01.example.net",
                    ["RabbitMQ:Hosts:1"] = "10.0.0.8",
                    ["RabbitMQ:Port"] = "5671",
                    ["RabbitMQ:Username"] = "nntparticles",
                    ["RabbitMQ:Password"] = "unit-test-password-not-real",
                    ["RabbitMQ:VirtualHost"] = "/articles",
                    ["RabbitMQ:EnableSsl"] = "true",
                    ["RabbitMQ:ChannelLeaseTimeoutSeconds"] = "90",
                    ["RabbitMQ:RpcTimeoutSeconds"] = "45",
                    ["RabbitMQ:ConnectionBlockedTimeoutSeconds"] = "40",
                    ["RabbitMQ:WorkRequestMaxPayloadBytes"] = "2048",
                    ["RabbitMQ:ChannelPoolSize"] = "256",
                    ["RabbitMQ:MinConnections"] = "2",
                    ["RabbitMQ:MaxConnections"] = "8",
                    ["RabbitMQ:MaxPendingLeaseWaiters"] = "64",
                    ["RabbitMQ:ConnectionScaleDownIdleSeconds"] = "120",
                    ["RabbitMQ:ScaleDownCooldownSeconds"] = "15",
                    ["RabbitMQ:NetworkRecoveryIntervalSeconds"] = "8",
                    ["RabbitMQ:PoolReconnectBaseDelayMs"] = "100",
                    ["RabbitMQ:PoolReconnectMaxDelayMs"] = "5000",
                    ["RabbitMQ:MinimumConnectionLifetimeSeconds"] = "90",
                    ["RabbitMQ:PublishConfirmTimeoutSeconds"] = "12",
                    ["RabbitMQ:MaximumShutdownDrainTimeoutSeconds"] = "20",
                    ["RabbitMQ:DegradedThreshold"] = "0.5",
                    ["RabbitMQ:UnhealthyThreshold"] = "4",
                    ["RabbitMQ:RequestedHeartbeatSeconds"] = "30",
                    ["RabbitMQ:SocketTimeoutSeconds"] = "15",
                    ["RabbitMQ:RequestedChannelMax"] = "1024",
                    ["RabbitMQ:ConsumerPrefetchCount"] = "50",
                    ["RabbitMQ:DiagnosticPayloadCorrelationId"] = "ee9f7a44-a5d6-4603-ba22-c5e0803fc834",
                    ["RabbitMQ:Management:BaseUrl"] = "http://rabbit-01.example.net:15672",
                    ["RabbitMQ:Management:RequestTimeoutSeconds"] = "5",
                })
            .Build();

        var options = new RabbitMqOptions();
        configuration.GetSection(RabbitMqOptions.SectionName).Bind(options);

        Assert.Equal(["rabbit-01.example.net", "10.0.0.8"], options.Hosts!);
        Assert.Equal(5671, options.Port);
        Assert.Equal("nntparticles", options.Username);
        Assert.Equal("unit-test-password-not-real", options.Password);
        Assert.Equal("/articles", options.VirtualHost);
        Assert.True(options.EnableSsl);
        Assert.Equal(90, options.ChannelLeaseTimeoutSeconds);
        Assert.Equal(45, options.RpcTimeoutSeconds);
        Assert.Equal(40, options.ConnectionBlockedTimeoutSeconds);
        Assert.Equal(2048, options.WorkRequestMaxPayloadBytes);
        Assert.Equal(256, options.ChannelPoolSize);
        Assert.Equal(2, options.MinConnections);
        Assert.Equal(8, options.MaxConnections);
        Assert.Equal(64, options.MaxPendingLeaseWaiters);
        Assert.Equal(120, options.ConnectionScaleDownIdleSeconds);
        Assert.Equal(15, options.ScaleDownCooldownSeconds);
        Assert.Equal(8, options.NetworkRecoveryIntervalSeconds);
        Assert.Equal(100, options.PoolReconnectBaseDelayMs);
        Assert.Equal(5000, options.PoolReconnectMaxDelayMs);
        Assert.Equal(90, options.MinimumConnectionLifetimeSeconds);
        Assert.Equal(12, options.PublishConfirmTimeoutSeconds);
        Assert.Equal(20, options.MaximumShutdownDrainTimeoutSeconds);
        Assert.Equal(0.5, options.DegradedThreshold);
        Assert.Equal(4, options.UnhealthyThreshold);
        Assert.Equal(30, options.RequestedHeartbeatSeconds);
        Assert.Equal(15, options.SocketTimeoutSeconds);
        Assert.Equal(1024, options.RequestedChannelMax);
        Assert.Equal((ushort)50, options.ConsumerPrefetchCount);
        Assert.Equal("ee9f7a44-a5d6-4603-ba22-c5e0803fc834", options.DiagnosticPayloadCorrelationId);
        Assert.Equal("http://rabbit-01.example.net:15672", options.Management.BaseUrl);
        Assert.Equal(5, options.Management.RequestTimeoutSeconds);
    }

    [Fact]
    public void Validate_Succeeds_ForValidHosts()
    {
        var result = new NntpdRabbitMqOptionsValidator().Validate(null, CreateValid());
        Assert.True(result.Succeeded);
    }

    [Fact]
    public void Validate_Fails_WhenHostsMissing()
    {
        var result = new NntpdRabbitMqOptionsValidator().Validate(null, new RabbitMqOptions());
        Assert.True(result.Failed);
        Assert.Contains(result.Failures, static failure => failure.Contains("Hosts", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_Fails_WhenHostEntryEmpty()
    {
        var options = CreateValid();
        options.Hosts = [" "];
        var result = new NntpdRabbitMqOptionsValidator().Validate(null, options);
        Assert.True(result.Failed);
    }

    [Fact]
    public void Validate_Fails_WhenHostIncludesCredentials()
    {
        var options = CreateValid();
        options.Hosts = ["user:pass@broker.example.net"];
        var result = new NntpdRabbitMqOptionsValidator().Validate(null, options);
        Assert.True(result.Failed);
        Assert.Contains(result.Failures, static failure => failure.Contains("credentials", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_Fails_WhenUsernameConfiguredWithoutPassword()
    {
        var options = CreateValid();
        options.Username = "nntparticles";
        options.Password = null;
        var result = new NntpdRabbitMqOptionsValidator().Validate(null, options);
        Assert.True(result.Failed);
        Assert.Contains(result.Failures, static failure => failure.Contains("Password", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(65536)]
    public void Validate_Fails_WhenPortOutOfRange(int port)
    {
        var options = CreateValid();
        options.Port = port;
        var result = new NntpdRabbitMqOptionsValidator().Validate(null, options);
        Assert.True(result.Failed);
    }

    [Fact]
    public void Validate_Fails_WhenMinConnectionsExceedMaxConnections()
    {
        var options = CreateValid();
        options.MinConnections = 8;
        options.MaxConnections = 2;
        var result = new NntpdRabbitMqOptionsValidator().Validate(null, options);
        Assert.True(result.Failed);
    }

    [Fact]
    public void Validate_Fails_WhenLeaseTimeoutLessThanRpcTimeout()
    {
        var options = CreateValid();
        options.ChannelLeaseTimeoutSeconds = 10;
        options.RpcTimeoutSeconds = 30;
        var result = new NntpdRabbitMqOptionsValidator().Validate(null, options);
        Assert.True(result.Failed);
    }

    [Fact]
    public void ToRuntimeOptions_ProjectsValidatedSnapshot()
    {
        var options = CreateValid();
        options.Username = "nntparticles";
        options.Password = "unit-test-password-not-real";
        options.EnableSsl = false;

        var runtime = options.ToRuntimeOptions();

        Assert.Equal(["127.0.0.1"], runtime.Hosts);
        Assert.Equal(5672, runtime.Port);
        Assert.Equal("nntparticles", runtime.Username);
        Assert.Equal("unit-test-password-not-real", runtime.Password);
        Assert.Equal("/", runtime.VirtualHost);
        Assert.False(runtime.EnableSsl);
        Assert.Equal(60, runtime.RequestedHeartbeatSeconds);
    }

    [Fact]
    public void ToRuntimeOptions_TrimsDropsBlankAndDeduplicatesHosts()
    {
        var options = CreateValid();
        options.Hosts = ["  broker.example.net  ", " ", "BROKER.example.net", "10.0.0.8", ""];

        var runtime = options.ToRuntimeOptions();

        Assert.Equal(["broker.example.net", "10.0.0.8"], runtime.Hosts);
    }

    [Fact]
    public void ToRuntimeOptions_TrimsUsername_AndConvertsBlankToNull()
    {
        var options = CreateValid();
        options.Username = "  nntparticles  ";
        options.Password = "unit-test-password-not-real";

        Assert.Equal("nntparticles", options.ToRuntimeOptions().Username);

        options.Username = " \t ";
        Assert.Null(options.ToRuntimeOptions().Username);

        options.Username = null;
        Assert.Null(options.ToRuntimeOptions().Username);
    }

    [Fact]
    public void ToRuntimeOptions_TrimsVirtualHost_AndConvertsBlankToSlash()
    {
        var options = CreateValid();
        options.VirtualHost = "  /articles  ";
        Assert.Equal("/articles", options.ToRuntimeOptions().VirtualHost);

        options.VirtualHost = " \t ";
        Assert.Equal("/", options.ToRuntimeOptions().VirtualHost);

        options.VirtualHost = null;
        Assert.Equal("/", options.ToRuntimeOptions().VirtualHost);
    }

    [Fact]
    public void EnvironmentVariables_DocumentedCredentialNames_AreExact()
    {
        Assert.Equal("VECTOR__RABBITMQ__USERNAME", RabbitMqOptions.UsernameEnvironmentVariable);
        Assert.Equal("VECTOR__RABBITMQ__PASSWORD", RabbitMqOptions.PasswordEnvironmentVariable);
        Assert.Equal("Username", RabbitMqOptions.UsernameConfigurationKey);
        Assert.Equal("Password", RabbitMqOptions.PasswordConfigurationKey);
    }

    [Fact]
    public void EnvironmentVariables_NntpdPrefixedCredentials_BindToRabbitMqSection()
    {
        const string username = "nntparticles";
        const string password = "unit-test-password-not-real";

        var previousUser = Environment.GetEnvironmentVariable(RabbitMqOptions.UsernameEnvironmentVariable);
        var previousPass = Environment.GetEnvironmentVariable(RabbitMqOptions.PasswordEnvironmentVariable);

        try
        {
            Environment.SetEnvironmentVariable(RabbitMqOptions.UsernameEnvironmentVariable, username);
            Environment.SetEnvironmentVariable(RabbitMqOptions.PasswordEnvironmentVariable, password);

            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(
                    new Dictionary<string, string?>
                    {
                        ["RabbitMQ:Hosts:0"] = "127.0.0.1",
                        ["RabbitMQ:Username"] = "from-json-should-be-overridden",
                        ["RabbitMQ:Password"] = "from-json-should-be-overridden",
                    })
                .AddNntpdPrefixedEnvironmentVariables()
                .Build();

            var options = new RabbitMqOptions();
            configuration.GetSection(RabbitMqOptions.SectionName).Bind(options);

            Assert.Equal(username, options.Username);
            Assert.Equal(password, options.Password);
            Assert.Equal(username, configuration[$"{RabbitMqOptions.SectionName}:{RabbitMqOptions.UsernameConfigurationKey}"]);
            Assert.Equal(password, configuration[$"{RabbitMqOptions.SectionName}:{RabbitMqOptions.PasswordConfigurationKey}"]);
        }
        finally
        {
            Environment.SetEnvironmentVariable(RabbitMqOptions.UsernameEnvironmentVariable, previousUser);
            Environment.SetEnvironmentVariable(RabbitMqOptions.PasswordEnvironmentVariable, previousPass);
        }
    }

    [Fact]
    public void Validate_Fails_WhenManagementBaseUrlMissing()
    {
        var options = CreateValid();
        options.Management.BaseUrl = null;
        var result = new NntpdRabbitMqOptionsValidator().Validate(null, options);
        Assert.True(result.Failed);
        Assert.Contains(result.Failures, static failure => failure.Contains("Management:BaseUrl", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_Fails_WhenManagementBaseUrlIncludesApiPath()
    {
        var options = CreateValid();
        options.Management.BaseUrl = "http://127.0.0.1:15672/api";
        var result = new NntpdRabbitMqOptionsValidator().Validate(null, options);
        Assert.True(result.Failed);
        Assert.Contains(result.Failures, static failure => failure.Contains("must not include a path", StringComparison.Ordinal));
    }

    [Fact]
    public void GetDefaultConnectionName_UsesNntpdPrefix()
    {
        Assert.Equal(
            "VectorNNTPD.TransitService:nntpd01.usenet.ninja",
            RabbitMqRuntimeOptions.GetDefaultConnectionName("VectorNNTPD.TransitService", "nntpd01.usenet.ninja"));
    }

    internal static RabbitMqOptions CreateValid() =>
        new()
        {
            Hosts = ["127.0.0.1"],
            EnableSsl = false,
            Username = "guest",
            Password = "guest",
            Management = new RabbitMqManagementOptions
            {
                BaseUrl = "http://127.0.0.1:15672",
                RequestTimeoutSeconds = 5,
            },
        };
}

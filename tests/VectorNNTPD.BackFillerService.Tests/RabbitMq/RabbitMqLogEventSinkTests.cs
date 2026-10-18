using Serilog.Events;
using Serilog.Formatting;
using Serilog.Formatting.Display;
using Serilog.Parsing;
using VectorNNTPD.BackFillerService.Tests.TestDoubles;
using VectorNNTPD.Common.Messaging.RabbitMq;

namespace VectorNNTPD.BackFillerService.Tests.RabbitMq
{
    /// <summary>
    /// Serilog-to-RabbitMQ sink fault containment for format and disposal failures.
    /// </summary>

    public sealed class RabbitMqLogEventSinkTests
    {
        /// <summary>
        /// Formatter exceptions are self-logged and not propagated to Serilog callers.
        /// </summary>

        [Fact]
        public async Task Unexpected_formatter_failure_stays_inside_the_sink()
        {
            var factory = new FakeBackFillerRabbitMqConnectionFactory();
            var service = RabbitMqServiceTests.CreateService(factory);
            await service.StartAsync(CancellationToken.None);
            try
            {
                factory.LastConnection!.DefaultPublishConfirmBehavior = FakePublishConfirmBehavior.Confirm;
                using var sink = new RabbitMqLogEventSink(
                    service,
                    "logs",
                    "backfiller",
                    "VectorNNTPD.BackFillerService",
                    new ThrowingFormatter(),
                    RabbitMqLogEventSink.TextContentType);

                var diagnostic = TestSelfLog.Capture(() => sink.Emit(Event()));

                Assert.Contains("publication failed", diagnostic, StringComparison.Ordinal);
                Assert.Contains("format boom", diagnostic, StringComparison.Ordinal);
            }
            finally
            {
                await service.DisposeAsync();
            }
        }

        /// <summary>
        /// Publish channel disposal failures during sink shutdown stay inside the sink.
        /// </summary>

        [Fact]
        public async Task Unexpected_channel_disposal_failure_stays_inside_the_sink()
        {
            var factory = new FakeBackFillerRabbitMqConnectionFactory();
            var service = RabbitMqServiceTests.CreateService(factory);
            await service.StartAsync(CancellationToken.None);
            try
            {
                var connection = factory.LastConnection!;
                connection.DefaultPublishConfirmBehavior = FakePublishConfirmBehavior.Confirm;
                connection.OnPublishChannelCreated = channel =>
                    channel.DisposeException = new NotSupportedException("channel dispose boom");
                var sink = new RabbitMqLogEventSink(
                    service,
                    "logs",
                    "backfiller",
                    "VectorNNTPD.BackFillerService",
                    new MessageTemplateTextFormatter("{Message}"),
                    RabbitMqLogEventSink.TextContentType);
                sink.Emit(Event());

                var diagnostic = TestSelfLog.Capture(sink.Dispose);

                Assert.Contains("failed to dispose a publish channel", diagnostic, StringComparison.Ordinal);
                Assert.Contains("channel dispose boom", diagnostic, StringComparison.Ordinal);
            }
            finally
            {
                await service.DisposeAsync();
            }
        }

        /// <summary>
        /// Builds a minimal Serilog event driving sink tests.
        /// </summary>

        private static LogEvent Event() =>
            new(
                DateTimeOffset.UtcNow,
                LogEventLevel.Information,
                exception: null,
                new MessageTemplateParser().Parse("marker"),
                []);

        /// <summary>
        /// Serilog formatter double that always throws so the sink must contain the failure.
        /// </summary>
        private sealed class ThrowingFormatter : ITextFormatter
        {
            /// <summary>Always throws <see cref="FormatException"/>; does not write to the output writer.</summary>
            /// <param name="logEvent">Ignored incoming event.</param>
            /// <param name="output">Ignored destination writer.</param>

            public void Format(LogEvent logEvent, TextWriter output) =>
                throw new FormatException("format boom");
        }
    }
}

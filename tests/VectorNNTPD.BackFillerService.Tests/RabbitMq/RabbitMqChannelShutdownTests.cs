using System.Reflection;
using RabbitMQ.Client;
using VectorNNTPD.Common.Messaging.RabbitMq;

namespace VectorNNTPD.BackFillerService.Tests.RabbitMq
{
    /// <summary>
    /// Common manual-ack channel disposal still disposes the client channel when close fails.
    /// A timeout is logged and swallowed. Any other close failure propagates after dispose.
    /// </summary>

    public sealed class RabbitMqChannelShutdownTests
    {
        /// <summary>
        /// A close timeout is swallowed and the channel is still disposed.
        /// </summary>
        [Fact]
        public async Task Close_timeout_is_swallowed_and_the_channel_is_still_disposed()
        {
            var channel = ChannelDouble.Create(new TimeoutException("close timed out"));
            var wrapper = new RabbitMqClientManualAckChannel(channel.Channel, generation: 1);

            await wrapper.DisposeAsync();

            Assert.True(channel.Disposed);
        }

        /// <summary>
        /// An unexpected close failure disposes the channel and then propagates.
        /// </summary>
        [Fact]
        public async Task Unexpected_close_failure_disposes_the_channel_and_propagates()
        {
            var channel = ChannelDouble.Create(new NotSupportedException("close rejected"));
            var wrapper = new RabbitMqClientManualAckChannel(channel.Channel, generation: 1);

            var rejected = await Assert.ThrowsAsync<NotSupportedException>(() => wrapper.DisposeAsync().AsTask());

            Assert.Equal("close rejected", rejected.Message);
            Assert.True(channel.Disposed);
        }

        /// <summary>
        /// IChannel proxy simulating open state, failing close, and disposal tracking.
        /// </summary>

        private class ChannelDouble : DispatchProxy
        {
            /// <summary>Exception thrown from CloseAsync when configured.</summary>
            private Exception? _closeException;

            /// <summary>True after <c>DisposeAsync</c> is invoked on the proxy.</summary>
            public bool Disposed { get; private set; }

            /// <summary>The proxy instance tests pass to channel shutdown. Always reports open.</summary>
            public IChannel Channel { get; private set; } = null!;

            /// <summary>Creates an open proxy whose <c>CloseAsync</c> fails with <paramref name="closeException"/>.</summary>
            /// <param name="closeException">Exception close must throw.</param>
            internal static ChannelDouble Create(Exception closeException)
            {
                IChannel channel = DispatchProxy.Create<IChannel, ChannelDouble>();
                var proxy = AsProxy(channel);
                proxy.Channel = channel;
                proxy._closeException = closeException;
                return proxy;
            }

            /// <summary>Casts the dispatch proxy back to the state holder.</summary>
            /// <param name="channel">Proxy created by <see cref="Create"/>.</param>
            private static ChannelDouble AsProxy(IChannel channel) => (ChannelDouble)channel;

            /// <summary>
            /// Reports the channel open, fails close with the configured exception, and records disposal.
            /// Other operations complete with a default value and do not simulate broker traffic.
            /// </summary>
            protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
            {
                ArgumentNullException.ThrowIfNull(targetMethod);
                if (targetMethod.Name == "get_IsOpen")
                {
                    return true;
                }

                if (targetMethod.Name == "CloseAsync")
                {
                    return Task.FromException(_closeException!);
                }

                if (targetMethod.Name == "DisposeAsync")
                {
                    Disposed = true;
                    return ValueTask.CompletedTask;
                }

                if (targetMethod.ReturnType == typeof(ValueTask))
                {
                    return ValueTask.CompletedTask;
                }

                if (typeof(Task).IsAssignableFrom(targetMethod.ReturnType))
                {
                    return Task.CompletedTask;
                }

                return targetMethod.ReturnType.IsValueType
                    ? Activator.CreateInstance(targetMethod.ReturnType)
                    : null;
            }
        }
    }
}

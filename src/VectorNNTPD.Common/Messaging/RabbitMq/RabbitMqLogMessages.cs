namespace VectorNNTPD.Common.Messaging.RabbitMq
{
    /// <summary>Source-generated RabbitMQ infrastructure log messages. Never includes credentials.</summary>
    internal static partial class RabbitMqLogMessages
    {
        /// <summary>Logs event 2800 (information): a broker connect is starting. Credentials are not included.</summary>
        /// <param name="logger">Logger that receives the event.</param>
        /// <param name="Hosts">Comma-separated configured broker hosts.</param>
        /// <param name="Port">Broker TCP port.</param>
        /// <param name="VirtualHost">AMQP virtual host.</param>
        /// <param name="ConnectionName">Client-provided connection name.</param>
        /// <param name="EnableSsl">Whether the connect enables TLS.</param>
        [LoggerMessage(
            EventId = 2800,
            Level = LogLevel.Information,
            Message = "Connecting to RabbitMQ ({Hosts}, port {Port}, vhost {VirtualHost}, name {ConnectionName}, ssl {EnableSsl})")]
        internal static partial void Connecting(
            ILogger logger,
            string Hosts,
            int Port,
            string VirtualHost,
            string ConnectionName,
            bool EnableSsl);

        /// <summary>Logs event 2801 (information): a usable connection was published.</summary>
        /// <param name="logger">Logger that receives the event.</param>
        /// <param name="Host">Endpoint host reported by the opened connection.</param>
        /// <param name="Port">Endpoint port reported by the opened connection.</param>
        /// <param name="VirtualHost">Virtual host stored on the connection wrapper.</param>
        /// <param name="ConnectionName">Client-provided name from the opened connection.</param>
        /// <param name="Generation">Generation just installed.</param>
        /// <param name="ElapsedMs">Milliseconds from the connect attempt to publication.</param>
        [LoggerMessage(
            EventId = 2801,
            Level = LogLevel.Information,
            Message = "RabbitMQ connection established ({Host}:{Port}, vhost {VirtualHost}, name {ConnectionName}, generation {Generation}, {ElapsedMs} ms)")]
        internal static partial void Connected(
            ILogger logger,
            string Host,
            int Port,
            string VirtualHost,
            string ConnectionName,
            long Generation,
            double ElapsedMs);

        /// <summary>Logs event 2802 (error): the initial connect failed. The exception is attached, not interpolated.</summary>
        /// <param name="logger">Logger that receives the event.</param>
        /// <param name="exception">Failure attached to the log event.</param>
        /// <param name="Hosts">Comma-separated configured broker hosts.</param>
        /// <param name="Port">Broker TCP port.</param>
        /// <param name="VirtualHost">AMQP virtual host.</param>
        /// <param name="ConnectionName">Client-provided connection name.</param>
        /// <param name="ElapsedMs">Milliseconds from the connect attempt to the failure.</param>
        [LoggerMessage(
            EventId = 2802,
            Level = LogLevel.Error,
            Message = "RabbitMQ connection failed ({Hosts}, port {Port}, vhost {VirtualHost}, name {ConnectionName}, {ElapsedMs} ms)")]
        internal static partial void ConnectionFailed(
            ILogger logger,
            Exception exception,
            string Hosts,
            int Port,
            string VirtualHost,
            string ConnectionName,
            double ElapsedMs);

        /// <summary>Logs event 2803 (warning): the current generation reported connection loss.</summary>
        /// <param name="logger">Logger that receives the event.</param>
        /// <param name="Generation">Generation that was current when the loss was accepted.</param>
        /// <param name="ReplyCode">Broker shutdown reply code.</param>
        /// <param name="ReplyText">Broker shutdown reply text.</param>
        /// <param name="Initiator">Shutdown initiator name supplied by the connection wrapper.</param>
        [LoggerMessage(
            EventId = 2803,
            Level = LogLevel.Warning,
            Message = "RabbitMQ connection lost generation={Generation} replyCode={ReplyCode} replyText={ReplyText} initiator={Initiator}")]
        internal static partial void ConnectionLost(
            ILogger logger,
            long Generation,
            ushort ReplyCode,
            string ReplyText,
            string Initiator);

        /// <summary>Logs event 2804 (warning): a RabbitMQ.Client callback threw. This does not by itself request recovery.</summary>
        /// <param name="logger">Logger that receives the event.</param>
        /// <param name="Message">Exception message text. The exception object is not attached.</param>
        [LoggerMessage(
            EventId = 2804,
            Level = LogLevel.Warning,
            Message = "RabbitMQ callback exception: {Message}")]
        internal static partial void CallbackException(ILogger logger, string Message);

        /// <summary>Logs event 2805 (warning): the broker blocked the connection. This method does not pause publishers.</summary>
        /// <param name="logger">Logger that receives the event.</param>
        /// <param name="Reason">Block reason supplied by the broker.</param>
        [LoggerMessage(
            EventId = 2805,
            Level = LogLevel.Warning,
            Message = "RabbitMQ broker blocked the connection: {Reason}")]
        internal static partial void ConnectionBlocked(ILogger logger, string Reason);

        /// <summary>Logs event 2806 (information): the broker unblocked the connection.</summary>
        /// <param name="logger">Logger that receives the event.</param>
        [LoggerMessage(
            EventId = 2806,
            Level = LogLevel.Information,
            Message = "RabbitMQ broker unblocked the connection")]
        internal static partial void ConnectionUnblocked(ILogger logger);

        /// <summary>Logs event 2808 (information): a reconnect attempt is about to wait.</summary>
        /// <param name="logger">Logger that receives the event.</param>
        /// <param name="Attempt">One-based attempt number for the current recovery.</param>
        /// <param name="BackoffMs">Delay in milliseconds before the next connect.</param>
        /// <param name="Generation">Last published generation, or zero when none has been published.</param>
        [LoggerMessage(
            EventId = 2808,
            Level = LogLevel.Information,
            Message = "RabbitMQ reconnect attempt {Attempt} starting in {BackoffMs} ms (last generation {Generation})")]
        internal static partial void ReconnectStarting(ILogger logger, int Attempt, double BackoffMs, long Generation);

        /// <summary>Logs event 2809 (information): reconnect published a new generation.</summary>
        /// <param name="logger">Logger that receives the event.</param>
        /// <param name="Attempt">One-based attempt that succeeded.</param>
        /// <param name="Generation">Generation installed by that attempt.</param>
        [LoggerMessage(
            EventId = 2809,
            Level = LogLevel.Information,
            Message = "RabbitMQ reconnect succeeded after {Attempt} attempt(s); generation {Generation} is current")]
        internal static partial void ReconnectSucceeded(ILogger logger, int Attempt, long Generation);

        /// <summary>Logs event 2810 (error): the first reconnect attempt failed. The exception is not attached.</summary>
        /// <param name="logger">Logger that receives the event.</param>
        /// <param name="Attempt">One-based attempt number. The service logs this event only for attempt 1.</param>
        /// <param name="Reason"><see cref="Exception.Message"/> from the failed attempt.</param>
        [LoggerMessage(
            EventId = 2810,
            Level = LogLevel.Error,
            Message = "RabbitMQ reconnect attempt {Attempt} failed: {Reason}")]
        internal static partial void ReconnectFailed(ILogger logger, int Attempt, string Reason);

        /// <summary>Logs event 2811 (warning): a later announced reconnect attempt failed. Recovery continues.</summary>
        /// <param name="logger">Logger that receives the event.</param>
        /// <param name="Attempt">One-based attempt number.</param>
        /// <param name="Reason"><see cref="Exception.Message"/> from the failed attempt.</param>
        [LoggerMessage(
            EventId = 2811,
            Level = LogLevel.Warning,
            Message = "RabbitMQ still reconnecting after {Attempt} attempt(s): {Reason}")]
        internal static partial void ReconnectStillFailing(ILogger logger, int Attempt, string Reason);

        /// <summary>Logs event 2815 (error): disposing a connection threw. The caller does not rethrow.</summary>
        /// <param name="logger">Logger that receives the event.</param>
        /// <param name="exception">Dispose failure attached to the log event.</param>
        [LoggerMessage(
            EventId = 2815,
            Level = LogLevel.Error,
            Message = "RabbitMQ connection disposal failed")]
        internal static partial void ConnectionDisposeFailed(ILogger logger, Exception exception);

        /// <summary>Logs event 2816 (information): the service finished disposing the current connection.</summary>
        /// <param name="logger">Logger that receives the event.</param>
        [LoggerMessage(
            EventId = 2816,
            Level = LogLevel.Information,
            Message = "RabbitMQ stopped")]
        internal static partial void Stopped(ILogger logger);

        /// <summary>
        /// Logs event 2817 (error): <c>StartAsync</c> failed after a non-cancellation exception.
        /// The partial connection is retired by the caller.
        /// </summary>
        /// <param name="logger">Logger that receives the event.</param>
        /// <param name="exception">Startup failure attached to the log event.</param>
        [LoggerMessage(
            EventId = 2817,
            Level = LogLevel.Error,
            Message = "RabbitMQ connection failed during startup")]
        internal static partial void StartupFailed(ILogger logger, Exception exception);

        /// <summary>Logs event 2818 (error): the factory returned a connection whose <c>IsOpen</c> is false.</summary>
        /// <param name="logger">Logger that receives the event.</param>
        [LoggerMessage(
            EventId = 2818,
            Level = LogLevel.Error,
            Message = "RabbitMQ connection opened but is not usable")]
        internal static partial void ConnectionNotUsable(ILogger logger);

        /// <summary>Logs event 2819 (warning): <c>CloseAsync</c> threw during connection dispose. Dispose still continues.</summary>
        /// <param name="logger">Logger that receives the event.</param>
        /// <param name="exception">Close failure attached to the log event.</param>
        [LoggerMessage(
            EventId = 2819,
            Level = LogLevel.Warning,
            Message = "RabbitMQ connection close failed")]
        internal static partial void ConnectionCloseFailed(ILogger logger, Exception exception);

        /// <summary>
        /// Logs event 2820 (error): a <c>ConnectionReplaced</c> subscriber threw after the generation was published.
        /// The published connection remains current; the exception is not rethrown.
        /// </summary>
        /// <param name="logger">Logger that receives the event.</param>
        /// <param name="exception">Subscriber failure attached to the log event.</param>
        /// <param name="Generation">Generation that was already installed when the handler ran.</param>
        /// <param name="Subscriber">Declaring type and method name of the failing handler, when known.</param>
        [LoggerMessage(
            EventId = 2820,
            Level = LogLevel.Error,
            Message = "A ConnectionReplaced subscriber failed after generation {Generation} was published ({Subscriber})")]
        internal static partial void ConnectionReplacedHandlerFailed(
            ILogger logger,
            Exception exception,
            long Generation,
            string Subscriber);
    }
}

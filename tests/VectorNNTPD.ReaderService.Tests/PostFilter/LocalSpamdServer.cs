using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace VectorNNTPD.ReaderService.Tests.PostFilter;

/// <summary>
/// Loopback SPAMC listener used by ordinary tests. It accepts real TCP connections
/// and speaks the CHECK framing the production client writes.
/// </summary>
internal sealed class LocalSpamdServer : IAsyncDisposable
{
    private const int MaxHeaderBytes = 8192;

    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _accept;
    private readonly ConcurrentQueue<Task> _connections = new();
    private readonly ConcurrentQueue<byte[]> _requests = new();
    private readonly ConcurrentQueue<string> _faults = new();
    private readonly TaskCompletionSource _accepted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly bool _keepOpen;
    private readonly bool _hang;
    private readonly bool _dropAfterRequest;
    private readonly TimeSpan _delay;
    private readonly byte[]? _response;
    private readonly byte[]? _subsequentResponse;
    private int _connectionCount;
    private int _checks;
    private int _inflight;
    private int _peak;

    private LocalSpamdServer(
        TcpListener listener,
        bool keepOpen,
        bool hang,
        bool dropAfterRequest,
        TimeSpan delay,
        byte[]? response,
        byte[]? subsequentResponse)
    {
        _listener = listener;
        _keepOpen = keepOpen;
        _hang = hang;
        _dropAfterRequest = dropAfterRequest;
        _delay = delay;
        _response = response;
        _subsequentResponse = subsequentResponse;
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        _accept = AcceptLoopAsync();
    }

    /// <summary>Gets the loopback port selected by the listener.</summary>
    public int Port { get; }

    /// <summary>Gets how many TCP connections were accepted.</summary>
    public int Connections => Volatile.Read(ref _connectionCount);

    /// <summary>Gets how many complete CHECK bodies were read.</summary>
    public int Checks => Volatile.Read(ref _checks);

    /// <summary>Gets the highest number of connections inside <c>ServeAsync</c> at once.</summary>
    public int PeakConnections => Volatile.Read(ref _peak);

    /// <summary>Completes when the first connection is accepted.</summary>
    public Task Accepted => _accepted.Task;

    /// <summary>Gets CHECK payloads in arrival order.</summary>
    public IReadOnlyList<byte[]> Requests => _requests.ToArray();

    /// <summary>Gets protocol violations observed while reading requests.</summary>
    public IReadOnlyList<string> ProtocolFaults => _faults.ToArray();

    /// <summary>Binds <see cref="IPAddress.Loopback"/> on an ephemeral port and starts accepting.</summary>
    public static async Task<LocalSpamdServer> StartAsync(
        bool keepOpen,
        bool hang = false,
        bool dropAfterRequest = false,
        TimeSpan delay = default,
        byte[]? response = null,
        byte[]? subsequentResponse = null)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var server = new LocalSpamdServer(
            listener,
            keepOpen,
            hang,
            dropAfterRequest,
            delay,
            response,
            subsequentResponse);
        await Task.Yield();
        return server;
    }

    /// <summary>Clears recorded protocol faults after a test has asserted them.</summary>
    public void AcknowledgeProtocolFaults()
    {
        while (_faults.TryDequeue(out _))
        {
            // Drain remaining recorded faults after the test asserted them.
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync().ConfigureAwait(false);
        _listener.Stop();
        try
        {
            await _accept.ConfigureAwait(false);
        }
        catch (OperationCanceledException ex)
        {
            _ = ex.GetType(); // observed: expected during test teardown/cleanup
        }

        foreach (var connection in _connections)
        {
            try
            {
                await connection.ConfigureAwait(false);
            }
            catch (OperationCanceledException ex)
            {
                _ = ex.GetType(); // observed: expected during test teardown/cleanup
            }
        }

        _cts.Dispose();
        if (!_faults.IsEmpty)
        {
            throw new InvalidOperationException(
                "Local SPAMD server observed protocol faults: " + string.Join("; ", ProtocolFaults));
        }
    }

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_cts.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (_cts.IsCancellationRequested)
            {
                _ = ex.GetType(); // observed: accept loop canceled during server stop
                return;
            }

            Interlocked.Increment(ref _connectionCount);
            _accepted.TrySetResult();
            var serve = Task.Run(() => ServeAsync(client), CancellationToken.None);
            _connections.Enqueue(serve);
        }
    }

    private async Task ServeAsync(TcpClient client)
    {
        using (client)
        {
        var inflight = Interlocked.Increment(ref _inflight);
        ObservePeak(inflight);
        try
        {
            await using var stream = client.GetStream();
            do
            {
                if (_hang)
                {
                    await Task.Delay(Timeout.Infinite, _cts.Token).ConfigureAwait(false);
                }

                if (!await TryReadCheckAsync(stream).ConfigureAwait(false))
                {
                    return;
                }

                Interlocked.Increment(ref _checks);
                if (_dropAfterRequest)
                {
                    return;
                }

                if (_delay > TimeSpan.Zero)
                {
                    await Task.Delay(_delay, _cts.Token).ConfigureAwait(false);
                }

                var check = Volatile.Read(ref _checks);
                var bytes = check == 1 || _subsequentResponse is null
                    ? _response ?? "SPAMD/1.1 0 EX_OK\r\nSpam: False ; 0.0 / 5.0\r\n\r\n"u8.ToArray()
                    : _subsequentResponse;
                await stream.WriteAsync(bytes, _cts.Token).ConfigureAwait(false);
                await stream.FlushAsync(_cts.Token).ConfigureAwait(false);
            }
            while (_keepOpen);
        }
        catch (OperationCanceledException ex) when (_cts.IsCancellationRequested)
        {
            _ = ex.GetType(); // observed: expected during test teardown/cleanup
        }
        catch (IOException ex)
        {
            _ = ex.GetType(); // observed: expected during test teardown/cleanup
        }
        finally
        {
            Interlocked.Decrement(ref _inflight);
        }
        }
    }

    private async Task<bool> TryReadCheckAsync(NetworkStream stream)
    {
        var buffer = new byte[MaxHeaderBytes];
        var total = 0;
        var headerEnd = -1;
        while (total < buffer.Length && headerEnd < 0)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(total), _cts.Token).ConfigureAwait(false);
            if (read == 0)
            {
                if (total == 0)
                {
                    return false;
                }

                _faults.Enqueue("unexpected-eof-in-headers");
                return false;
            }

            total += read;
            headerEnd = buffer.AsSpan(0, total).IndexOf("\r\n\r\n"u8);
        }

        if (headerEnd < 0)
        {
            _faults.Enqueue("header-too-long");
            return false;
        }

        var headerText = Encoding.ASCII.GetString(buffer, 0, headerEnd);
        if (!headerText.StartsWith("CHECK SPAMC/", StringComparison.Ordinal)
            && !headerText.StartsWith("SYMBOLS SPAMC/", StringComparison.Ordinal))
        {
            _faults.Enqueue("unexpected-command");
            return false;
        }

        int? length = null;
        foreach (var line in headerText.Split("\r\n"))
        {
            if (!line.StartsWith("Content-length:", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!int.TryParse(line["Content-length:".Length..].Trim(), out var parsed) || parsed < 0)
            {
                _faults.Enqueue("invalid-content-length");
                return false;
            }

            length = parsed;
        }

        if (length is not int contentLength)
        {
            _faults.Enqueue("missing-content-length");
            return false;
        }

        var body = new byte[contentLength];
        var already = total - (headerEnd + 4);
        if (already > contentLength)
        {
            _faults.Enqueue("body-longer-than-content-length");
            return false;
        }

        if (already > 0)
        {
            buffer.AsSpan(headerEnd + 4, already).CopyTo(body);
        }

        var filled = already;
        while (filled < contentLength)
        {
            var read = await stream.ReadAsync(body.AsMemory(filled), _cts.Token).ConfigureAwait(false);
            if (read == 0)
            {
                _faults.Enqueue("short-body");
                return false;
            }

            filled += read;
        }

        _requests.Enqueue(body);
        return true;
    }

    private void ObservePeak(int inflight)
    {
        while (true)
        {
            var peak = Volatile.Read(ref _peak);
            if (inflight <= peak || Interlocked.CompareExchange(ref _peak, inflight, peak) == peak)
            {
                return;
            }
        }
    }
}

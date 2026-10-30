using System.IO.Pipelines;
using VectorNNTPD.Common.Networking.Transport;
using VectorNNTPD.ReaderService.Session.Commands;
using VectorNNTPD.ReaderService.Session.CommandProcessor;

namespace VectorNNTPD.ReaderService.Tests.Session;

/// <summary>
/// Reader-mode command lines longer than <see cref="NntpCommandLineReader.MaxCommandLineBytes"/>
/// are consumed through CRLF and rejected as <c>501 Syntax error</c> without parsing the prefix.
/// </summary>
public sealed class NntpCommandLineLengthTests
{
    [Fact]
    public async Task ExactlyMaxBytes_IsCopiedAndParses()
    {
        var line = GroupLine(NntpCommandLineReader.MaxCommandLineBytes);
        await using var pipe = await CommandPipe.CreateAsync(WithCrlf(line));
        var scratch = new byte[NntpCommandLineReader.MaxCommandLineBytes];

        var length = await NntpCommandLineReader.ReadLineBytesAsync(pipe.Reader, scratch, CancellationToken.None);

        Assert.Equal(NntpCommandLineReader.MaxCommandLineBytes, length);
        var command = NntpCommandParser.Parse(scratch.AsSpan(0, length));
        Assert.True(command.IsValid);
        Assert.Equal(NntpVerb.Group, command.Verb);
    }

    [Fact]
    public async Task OneByteOverMax_IsConsumedAndNotCopiedAsACommand()
    {
        var line = GroupLine(NntpCommandLineReader.MaxCommandLineBytes + 1);
        await using var pipe = await CommandPipe.CreateAsync(WithCrlf(line));
        var scratch = new byte[NntpCommandLineReader.MaxCommandLineBytes];

        var length = await NntpCommandLineReader.ReadLineBytesAsync(pipe.Reader, scratch, CancellationToken.None);

        Assert.Equal(NntpCommandLineReader.OverlongLine, length);
        Assert.True((await pipe.Reader.ReadAsync()).Buffer.IsEmpty);
    }

    [Fact]
    public async Task ValidPrefixPlusExtra_DoesNotReturnThePrefix()
    {
        var line = new byte[NntpCommandLineReader.MaxCommandLineBytes + 6];
        "STAT <a@b.c>"u8.CopyTo(line);
        line.AsSpan("STAT <a@b.c>"u8.Length, NntpCommandLineReader.MaxCommandLineBytes - "STAT <a@b.c>"u8.Length)
            .Fill((byte)' ');
        " extra"u8.CopyTo(line.AsSpan(NntpCommandLineReader.MaxCommandLineBytes));
        await using var pipe = await CommandPipe.CreateAsync(WithCrlf(line));
        var scratch = new byte[NntpCommandLineReader.MaxCommandLineBytes];
        scratch.AsSpan().Fill((byte)'Z');

        var length = await NntpCommandLineReader.ReadLineBytesAsync(pipe.Reader, scratch, CancellationToken.None);

        Assert.Equal(NntpCommandLineReader.OverlongLine, length);
        Assert.Equal((byte)'Z', scratch[0]);
    }

    [Fact]
    public async Task OverlongInvalidPrefix_IsTheSameSentinel()
    {
        var line = new byte[NntpCommandLineReader.MaxCommandLineBytes + 1];
        line.AsSpan(0, NntpCommandLineReader.MaxCommandLineBytes).Fill((byte)'!');
        line[^1] = (byte)'x';
        await using var pipe = await CommandPipe.CreateAsync(WithCrlf(line));
        var scratch = new byte[NntpCommandLineReader.MaxCommandLineBytes];

        var length = await NntpCommandLineReader.ReadLineBytesAsync(pipe.Reader, scratch, CancellationToken.None);

        Assert.Equal(NntpCommandLineReader.OverlongLine, length);
    }

    [Fact]
    public async Task OverlongLineThenQuit_SecondLineParses()
    {
        var line = GroupLine(NntpCommandLineReader.MaxCommandLineBytes + 1);
        var wire = new byte[line.Length + 2 + "QUIT\r\n"u8.Length];
        WithCrlf(line).CopyTo(wire);
        "QUIT\r\n"u8.CopyTo(wire.AsSpan(line.Length + 2));
        await using var pipe = await CommandPipe.CreateAsync(wire);
        var scratch = new byte[NntpCommandLineReader.MaxCommandLineBytes];

        var first = await NntpCommandLineReader.ReadLineBytesAsync(pipe.Reader, scratch, CancellationToken.None);
        var second = await NntpCommandLineReader.ReadLineBytesAsync(pipe.Reader, scratch, CancellationToken.None);

        Assert.Equal(NntpCommandLineReader.OverlongLine, first);
        Assert.Equal(4, second);
        Assert.True(NntpCommandParser.Parse(scratch.AsSpan(0, second)).IsValid);
        Assert.Equal(NntpVerb.Quit, NntpCommandParser.Parse(scratch.AsSpan(0, second)).Verb);
    }

    [Fact]
    public async Task OverlongSplitAcrossReads_StillOverlong()
    {
        var line = GroupLine(NntpCommandLineReader.MaxCommandLineBytes + 1);
        var wire = WithCrlf(line);
        var pipe = new Pipe(NntpPipeOptions.Create());
        var scratch = new byte[NntpCommandLineReader.MaxCommandLineBytes];
        var read = NntpCommandLineReader.ReadLineBytesAsync(pipe.Reader, scratch, CancellationToken.None).AsTask();

        await pipe.Writer.WriteAsync(wire.AsMemory(0, 1000));
        await pipe.Writer.WriteAsync(wire.AsMemory(1000));
        var length = await read.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(NntpCommandLineReader.OverlongLine, length);
        await pipe.Writer.CompleteAsync();
        await pipe.Reader.CompleteAsync();
    }

    [Fact]
    public async Task EmbeddedLf_IsNotADelimiter()
    {
        var line = new byte[NntpCommandLineReader.MaxCommandLineBytes + 1];
        line.AsSpan().Fill((byte)'a');
        line[10] = (byte)'\n';
        line[20] = (byte)'\r';
        await using var pipe = await CommandPipe.CreateAsync(WithCrlf(line));
        var scratch = new byte[NntpCommandLineReader.MaxCommandLineBytes];

        var length = await NntpCommandLineReader.ReadLineBytesAsync(pipe.Reader, scratch, CancellationToken.None);

        Assert.Equal(NntpCommandLineReader.OverlongLine, length);
        Assert.True((await pipe.Reader.ReadAsync()).Buffer.IsEmpty);
    }

    [Fact]
    public async Task NoCrlfBeforeClose_ReturnsEof()
    {
        var line = new byte[NntpCommandLineReader.MaxCommandLineBytes + 100];
        line.AsSpan().Fill((byte)'a');
        var pipe = new Pipe(NntpPipeOptions.Create());
        await pipe.Writer.WriteAsync(line);
        await pipe.Writer.CompleteAsync();
        var scratch = new byte[NntpCommandLineReader.MaxCommandLineBytes];

        var length = await NntpCommandLineReader.ReadLineBytesAsync(pipe.Reader, scratch, CancellationToken.None);

        Assert.Equal(-1, length);
        await pipe.Reader.CompleteAsync();
    }

    private static byte[] GroupLine(int length)
    {
        var line = new byte[length];
        "GROUP "u8.CopyTo(line);
        line.AsSpan("GROUP "u8.Length).Fill((byte)'a');
        return line;
    }

    private static byte[] WithCrlf(byte[] line)
    {
        var wire = new byte[line.Length + 2];
        line.CopyTo(wire);
        wire[^2] = (byte)'\r';
        wire[^1] = (byte)'\n';
        return wire;
    }

    private sealed class CommandPipe : IAsyncDisposable
    {
        private readonly Pipe _pipe;

        private CommandPipe(Pipe pipe) => _pipe = pipe;

        public PipeReader Reader => _pipe.Reader;

        public static async Task<CommandPipe> CreateAsync(byte[] wire)
        {
            var pipe = new Pipe(NntpPipeOptions.Create());
            await pipe.Writer.WriteAsync(wire);
            await pipe.Writer.CompleteAsync();
            return new CommandPipe(pipe);
        }

        public async ValueTask DisposeAsync()
        {
            await _pipe.Reader.CompleteAsync();
            await _pipe.Writer.CompleteAsync();
        }
    }
}

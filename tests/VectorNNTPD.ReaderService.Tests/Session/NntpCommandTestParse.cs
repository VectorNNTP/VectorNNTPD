using System.Text;
using VectorNNTPD.ReaderService.Session;
using VectorNNTPD.ReaderService.Session.Commands;
using VectorNNTPD.ReaderService.Session.CommandProcessor;

namespace VectorNNTPD.ReaderService.Tests.Session;

internal static class NntpCommandTestParse
{
    public static (NntpCommand Command, byte[] Line) Parse(string text)
    {
        var line = Encoding.ASCII.GetBytes(text);
        return (NntpCommandParser.Parse(line), line);
    }

    public static NntpCommand ParseCommand(string text) => Parse(text).Command;

    public static ValueTask DispatchAsync(
        NntpCommandDispatcher dispatcher,
        NntpSession session,
        NntpResponseWriter response,
        string text,
        CancellationToken cancellationToken = default)
    {
        var (command, line) = Parse(text);
        return dispatcher.DispatchAsync(session, command, line, response, cancellationToken);
    }
}

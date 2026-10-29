using System.Text;
using VectorNNTPD.TransitService.Session;
using VectorNNTPD.TransitService.Session.Commands;
using VectorNNTPD.TransitService.Session.CommandProcessor;

namespace VectorNNTPD.TransitService.Tests.Session;

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

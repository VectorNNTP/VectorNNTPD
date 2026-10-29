namespace VectorNNTPD.ReaderService.Tests.Fixtures;

/// <summary>Loads the operator DDL at <c>docs/schema/postfilter.sql</c>.</summary>
internal static class PostFilterSchemaScript
{
    public static string FindPath() => FindDocsSql("postfilter.sql");

    /// <summary>Finds a file under repository <c>docs/</c> or <c>docs/schema/</c>.</summary>
    public static string FindDocsSql(string fileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var schemaCandidate = Path.Join(dir.FullName, "docs", "schema", fileName);
            if (File.Exists(schemaCandidate))
            {
                return schemaCandidate;
            }

            var docsCandidate = Path.Join(dir.FullName, "docs", fileName);
            if (File.Exists(docsCandidate))
            {
                return docsCandidate;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException("docs/" + fileName + " was not found.");
    }

    public static IReadOnlyList<string> ReadStatements() => ReadStatements(FindPath());

    /// <summary>Splits one operator SQL file using the same DELIMITER rules as <c>postfilter.sql</c>.</summary>
    public static IReadOnlyList<string> ReadStatements(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var lines = File.ReadAllLines(path);
        var statements = new List<string>();
        var buffer = new List<string>();
        var delimiter = ";";
        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (TryReadDelimiter(trimmed, out var nextDelimiter))
            {
                Flush(buffer, statements, delimiter);
                delimiter = nextDelimiter;
                continue;
            }

            if (trimmed.StartsWith("-- VECTORNNTP_STMT", StringComparison.Ordinal)
                || trimmed.StartsWith("-- BEGIN TRIGGER", StringComparison.Ordinal)
                || trimmed.StartsWith("-- END TRIGGER", StringComparison.Ordinal))
            {
                Flush(buffer, statements, delimiter);
                continue;
            }

            if (trimmed.StartsWith("--", StringComparison.Ordinal))
            {
                continue;
            }

            buffer.Add(line);
            if (BufferEndsWith(buffer, delimiter))
            {
                Flush(buffer, statements, delimiter);
            }
        }

        Flush(buffer, statements, delimiter);
        return statements;
    }

    private static bool TryReadDelimiter(string trimmed, out string delimiter)
    {
        delimiter = ";";
        const string prefix = "DELIMITER";
        if (!trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (trimmed.Length != prefix.Length
            && !char.IsWhiteSpace(trimmed[prefix.Length]))
        {
            return false;
        }

        var value = trimmed[prefix.Length..].Trim();
        delimiter = value.Length == 0 ? ";" : value;
        return true;
    }

    private static bool BufferEndsWith(List<string> buffer, string delimiter)
    {
        var text = string.Join('\n', buffer).TrimEnd();
        return text.EndsWith(delimiter, StringComparison.Ordinal);
    }

    private static void Flush(List<string> buffer, List<string> statements, string delimiter)
    {
        var text = string.Join('\n', buffer).Trim();
        buffer.Clear();
        if (text.Length == 0)
        {
            return;
        }

        if (text.EndsWith(delimiter, StringComparison.Ordinal))
        {
            text = text[..^delimiter.Length].TrimEnd();
        }

        text = text.TrimEnd(';').Trim();
        if (text.Length == 0)
        {
            return;
        }

        statements.Add(text);
    }
}

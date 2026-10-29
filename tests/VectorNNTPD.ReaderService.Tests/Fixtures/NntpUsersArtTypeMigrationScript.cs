namespace VectorNNTPD.ReaderService.Tests.Fixtures;

/// <summary>Loads <c>docs/schema/nntpusers-account-art-type.sql</c>.</summary>
internal static class NntpUsersArtTypeMigrationScript
{
    public static string FindPath() => PostFilterSchemaScript.FindDocsSql("nntpusers-account-art-type.sql");

    public static IReadOnlyList<string> ReadStatements() =>
        PostFilterSchemaScript.ReadStatements(FindPath());
}

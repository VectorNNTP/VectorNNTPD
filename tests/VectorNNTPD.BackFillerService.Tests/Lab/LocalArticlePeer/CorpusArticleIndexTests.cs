namespace VectorNNTPD.BackFillerService.Tests.Lab.LocalArticlePeer
{
    /// <summary>Path safety for <see cref="CorpusArticleIndex"/> resolution under the corpus root.</summary>
    public sealed class CorpusArticleIndexTests
    {
        [Fact]
        public async Task TryResolve_rejects_rooted_and_traversal_relative_paths()
        {
            var dir = Path.Join(Path.GetTempPath(), "vnntp-corpus-path-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                const string messageId = "<safe@lab.test>";
                await File.WriteAllTextAsync(
                    Path.Join(dir, "article1"),
                    $"Message-ID: {messageId}\r\nFrom: a@b\r\n\r\nbody\r\n");

                var index = await CorpusArticleIndex.BuildAsync(dir);
                Assert.True(index.TryResolve(messageId, out var safePath));
                Assert.True(File.Exists(safePath));
                Assert.StartsWith(index.CorpusRoot, safePath, StringComparison.OrdinalIgnoreCase);

                Assert.False(index.TryResolveUnderCorpusRoot("../outside", out _));
                Assert.False(index.TryResolveUnderCorpusRoot("..\\outside", out _));
                Assert.False(index.TryResolveUnderCorpusRoot("/etc/passwd", out _));
                Assert.False(index.TryResolveUnderCorpusRoot("", out _));
                Assert.False(index.TryResolveUnderCorpusRoot(".", out _));
                if (OperatingSystem.IsWindows())
                {
                    Assert.False(index.TryResolveUnderCorpusRoot("C:\\Windows\\system.ini", out _));
                }

                Assert.True(index.TryResolveUnderCorpusRoot("article1", out var under));
                Assert.Equal(Path.GetFullPath(Path.Join(index.CorpusRoot, "article1")), under);
            }
            finally
            {
                try
                {
                    Directory.Delete(dir, recursive: true);
                }
                catch (IOException ex)
                {
                    _ = ex.GetType();
                }
            }
        }
    }
}

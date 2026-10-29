using System.Diagnostics;
using System.Reflection;
using VectorNNTPD.ReaderService.Logging;

namespace VectorNNTPD.ReaderService.Tests;

public sealed class ApplicationVersionTests
{
    [Fact]
    public void CompiledVersion_IsDateTimeFourPartNumeric_WithoutSourceRevision()
    {
        AssertDateTimeApplicationVersion(typeof(NntpdLoggingExtensions).Assembly);
    }

    private static void AssertDateTimeApplicationVersion(Assembly assembly)
    {
        var version = assembly.GetName().Version;
        Assert.NotNull(version);
        Assert.Equal(1, version.Major);
        Assert.Equal(0, version.Minor);
        Assert.Equal((DateTime.Now.Date - new DateTime(2000, 1, 1)).Days, version.Build);
        Assert.InRange(version.Revision, 0, 43199);

        var expected = $"{version.Major}.{version.Minor}.{version.Build}.{version.Revision}";
        Assert.Matches(@"^1\.0\.\d+\.\d+$", expected);

        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>();
        Assert.NotNull(informational);
        Assert.Equal(expected, informational.InformationalVersion);
        Assert.DoesNotContain("+", informational.InformationalVersion, StringComparison.Ordinal);
        Assert.DoesNotContain("git", informational.InformationalVersion, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("commit", informational.InformationalVersion, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("branch", informational.InformationalVersion, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("-", informational.InformationalVersion, StringComparison.Ordinal);

        var file = FileVersionInfo.GetVersionInfo(assembly.Location);
        Assert.Equal(expected, file.FileVersion);
        Assert.Equal(expected, file.ProductVersion);

        const string copyright = "© 2026 Chris Knipe <cknipe@opticnetworks.net>";
        var copyrightAttribute = assembly.GetCustomAttribute<AssemblyCopyrightAttribute>();
        Assert.NotNull(copyrightAttribute);
        Assert.Equal(copyright, copyrightAttribute.Copyright);
        Assert.Equal(copyright, file.LegalCopyright);
    }
}

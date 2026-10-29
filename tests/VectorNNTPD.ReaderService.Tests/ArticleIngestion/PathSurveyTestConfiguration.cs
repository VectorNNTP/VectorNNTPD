using Microsoft.Extensions.Configuration;
using VectorNNTPD.ReaderService.Logging;

namespace VectorNNTPD.ReaderService.Tests.ArticleIngestion;

internal static class PathSurveyTestConfiguration
{
    public static IConfiguration Create(
        string directory,
        string rollingInterval = "Day",
        int retainedFileCountLimit = 1,
        bool buffered = false,
        long? fileSizeLimitBytes = null,
        bool rollOnFileSizeLimit = false,
        int bufferSize = 1000,
        bool blockWhenFull = true)
    {
        Directory.CreateDirectory(directory);
        return new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Nntpd:LogDir"] = directory,
                [NntpdPathSurveyLogging.SectionName + ":path"] = Path.Join(directory, "inpaths-.log"),
                [NntpdPathSurveyLogging.SectionName + ":rollingInterval"] = rollingInterval,
                [NntpdPathSurveyLogging.SectionName + ":retainedFileCountLimit"] = retainedFileCountLimit.ToString(),
                [NntpdPathSurveyLogging.SectionName + ":buffered"] = buffered ? "true" : "false",
                [NntpdPathSurveyLogging.SectionName + ":rollOnFileSizeLimit"] = rollOnFileSizeLimit ? "true" : "false",
                [NntpdPathSurveyLogging.SectionName + ":fileSizeLimitBytes"] = fileSizeLimitBytes?.ToString(),
                [NntpdPathSurveyLogging.SectionName + ":bufferSize"] = bufferSize.ToString(),
                [NntpdPathSurveyLogging.SectionName + ":blockWhenFull"] = blockWhenFull ? "true" : "false",
            })
            .Build();
    }

    public static string ReadInpathsFile(string directory)
    {
        var files = Directory.GetFiles(directory, "inpaths*")
            .Where(static path => !path.EndsWith(".gz", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (files.Length == 0)
        {
            return string.Empty;
        }

        using var stream = new FileStream(files[0], FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}

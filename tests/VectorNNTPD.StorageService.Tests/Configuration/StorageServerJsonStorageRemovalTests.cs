namespace VectorNNTPD.StorageService.Tests.Configuration
{

    public sealed class StorageServerJsonStorageRemovalTests
    {
        [Fact]
        public void Shipped_configuration_has_segment_pools_and_no_journal()
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !File.Exists(Path.Join(directory.FullName, "VectorNNTPD.slnx")))
            {
                directory = directory.Parent;
            }

            Assert.NotNull(directory);
            var path = Path.Join(directory!.FullName, "src", "VectorNNTPD.StorageService", "VectorNNTPD.StorageService.json");
            var json = File.ReadAllText(path);
            Assert.DoesNotContain("Journal", json, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("\"SegmentPools\"", json, StringComparison.Ordinal);
            Assert.DoesNotContain("SegmentDir", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("SegmentPath", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("\"Storage\"", json, StringComparison.Ordinal);
            Assert.Contains("\"Listener\"", json, StringComparison.Ordinal);
            Assert.Contains("\"BindPortTls\"", json, StringComparison.Ordinal);
            Assert.Contains("\"AcmeStateDir\"", json, StringComparison.Ordinal);
            Assert.DoesNotContain("AcmeDirectoryUrl", json, StringComparison.Ordinal);
            Assert.DoesNotContain("AcmeRenewalThresholdDays", json, StringComparison.Ordinal);
            Assert.DoesNotContain("CloudFlareZoneId", json, StringComparison.Ordinal);
            Assert.DoesNotContain("DnsSuffix", json, StringComparison.Ordinal);
        }
    }
}

using System.Security.Cryptography;
using System.Text.Json;

namespace VectorNNTPD.BackFillerService.Tests.Lab.LocalArticlePeer
{
    /// <summary>
    /// Captures SHA-256 identities for lab reports without recording secrets.
    /// </summary>
    internal static class LabBinaryIdentity
    {
        /// <summary>Snapshot of loaded assemblies and runtime suitable for JSON reports.</summary>
        internal sealed class Snapshot
        {
            public required string CapturedUtc { get; init; }

            public required string RuntimeVersion { get; init; }

            public required string FrameworkDescription { get; init; }

            public required string? BackFillerPath { get; init; }

            public required string? BackFillerSha256 { get; init; }

            public required string? CommonPath { get; init; }

            public required string? CommonSha256 { get; init; }

            public required string PeerAssemblyPath { get; init; }

            public required string PeerAssemblySha256 { get; init; }
        }

        /// <summary>Hashes the file at <paramref name="path"/> as uppercase hex SHA-256.</summary>
        internal static string Sha256File(string path)
        {
            using var stream = File.OpenRead(path);
            return Convert.ToHexString(SHA256.HashData(stream));
        }

        /// <summary>Captures identities of BackFiller, Common, and the test assembly hosting the peer.</summary>
        internal static Snapshot Capture()
        {
            var backFiller = typeof(VectorNNTPD.BackFillerService.Nntp.NntpProviderSession).Assembly.Location;
            var common = typeof(VectorNNTPD.Common.Core.ExceptionHelpers).Assembly.Location;
            var peer = typeof(LocalArticleNntpPeer).Assembly.Location;
            return new Snapshot
            {
                CapturedUtc = DateTimeOffset.UtcNow.ToString("o"),
                RuntimeVersion = Environment.Version.ToString(),
                FrameworkDescription = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
                BackFillerPath = string.IsNullOrEmpty(backFiller) ? null : backFiller,
                BackFillerSha256 = string.IsNullOrEmpty(backFiller) ? null : Sha256File(backFiller),
                CommonPath = string.IsNullOrEmpty(common) ? null : common,
                CommonSha256 = string.IsNullOrEmpty(common) ? null : Sha256File(common),
                PeerAssemblyPath = peer,
                PeerAssemblySha256 = Sha256File(peer),
            };
        }

        /// <summary>Writes <see cref="Capture"/> JSON to <paramref name="path"/>.</summary>
        internal static async Task WriteAsync(string path, CancellationToken cancellationToken)
        {
            var json = JsonSerializer.Serialize(Capture(), new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(path, json, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>SHA-256 of sanitized config text (no credentials expected in input).</summary>
        internal static string Sha256Utf8Text(string text) =>
            Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text)));
    }
}

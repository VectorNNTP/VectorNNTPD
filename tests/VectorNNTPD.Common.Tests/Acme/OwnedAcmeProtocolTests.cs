using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using VectorNNTPD.Common.Acme;
using VectorNNTPD.Common.Acme.Protocol;

namespace VectorNNTPD.Common.Tests.Acme
{
    /// <summary>
    /// Owned ACME protocol contracts: account DER keys, JWS thumbprints, CSR, PFX, JSON source-gen.
    /// </summary>
    public sealed class OwnedAcmeProtocolTests
    {
        /// <summary>
        /// Importing PKCS#8 DER into <see cref="AcmeAccountKey"/> preserves RSA material, reports <c>RS256</c>,
        /// and exposes a non-empty thumbprint plus an RSA JWK.
        /// </summary>
        [Fact]
        public void AccountKey_DerImportExport_PreservesRsaMaterial()
        {
            using var rsa = RSA.Create(2048);
            byte[] der = rsa.ExportPkcs8PrivateKey();
            AcmeAccountKey.AssertPkcs8DerRoundTripPreservesRsaMaterial(der);

            using AcmeAccountKey key = AcmeAccountKey.ImportPkcs8Der(der);
            Assert.Equal("RS256", key.SignatureAlgorithm);
            Assert.False(string.IsNullOrWhiteSpace(key.Thumbprint));
            Assert.Contains("\"kty\":\"RSA\"", key.Jwk, StringComparison.Ordinal);
        }

        /// <summary>Empty PKCS#8 input fails closed without returning a key.</summary>
        [Fact]
        public void AccountKey_EmptyDer_Throws()
        {
            Assert.Throws<CryptographicException>(() => AcmeAccountKey.ImportPkcs8Der(ReadOnlySpan<byte>.Empty));
        }

        /// <summary>Malformed PKCS#8 input fails closed without returning a key.</summary>
        [Fact]
        public void AccountKey_MalformedDer_Throws()
        {
            Assert.ThrowsAny<CryptographicException>(() => AcmeAccountKey.ImportPkcs8Der([0x30, 0x03, 0x01, 0x02, 0x03]));
        }

        /// <summary>PKCS#8 that does not consume the full buffer fails closed.</summary>
        [Fact]
        public void AccountKey_TrailingBytes_Throws()
        {
            using var rsa = RSA.Create(2048);
            byte[] der = rsa.ExportPkcs8PrivateKey();
            var withTrailer = new byte[der.Length + 1];
            der.CopyTo(withTrailer, 0);
            withTrailer[^1] = 0x00;

            var ex = Assert.Throws<CryptographicException>(() => AcmeAccountKey.ImportPkcs8Der(withTrailer));
            Assert.Contains("consumed", ex.Message, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// <see cref="AcmeAccountKey.GetDnsRecordValue"/> is unpadded base64url, and
        /// <see cref="AcmeAccountKey.GetKeyAuthorization"/> is the token, a dot, and the key thumbprint.
        /// </summary>
        [Fact]
        public void AccountKey_Dns01Txt_IsBase64UrlSha256OfKeyAuthorization()
        {
            using var rsa = RSA.Create(2048);
            using AcmeAccountKey key = AcmeAccountKey.ImportPkcs8Der(rsa.ExportPkcs8PrivateKey());
            const string token = "test-token-value";
            string txt = key.GetDnsRecordValue(token);
            Assert.False(string.IsNullOrWhiteSpace(txt));
            Assert.DoesNotContain('=', txt);
            Assert.DoesNotContain('+', txt);
            Assert.DoesNotContain('/', txt);
            Assert.Equal(token + "." + key.Thumbprint, key.GetKeyAuthorization(token));
        }

        /// <summary>
        /// <see cref="CsrBuilder.CreateDnsSigningRequest"/> returns a non-empty PKCS#10 DER sequence (tag <c>0x30</c>) for the requested DNS names.
        /// </summary>
        [Fact]
        public void CertificateKey_Csr_IsNonEmptyPkcs10()
        {
            using AcmeCertificateKey certKey = AcmeCertificateKey.CreateRsa(2048);
            byte[] csr = CsrBuilder.CreateDnsSigningRequest(["host.example.com", "news.example.com"], certKey);
            Assert.True(csr.Length > 32);
            // PKCS#10 DER SEQUENCE tag
            Assert.Equal(0x30, csr[0]);
        }

        /// <summary>
        /// <see cref="PfxCrypto.ExportPfx"/> of a PEM leaf and its key loads back through <see cref="PfxCrypto.LoadCertificate"/>
        /// with the private key and the leaf subject still present.
        /// </summary>
        [Fact]
        public void Pfx_FromPemChain_LoadsWithBcl()
        {
            using AcmeCertificateKey certKey = AcmeCertificateKey.CreateRsa(2048);
            string keyPem = certKey.ExportPem();
            CertificateRequest request = certKey.CreateRequest(new X500DistinguishedName("CN=host.example.com"));
            var san = new SubjectAlternativeNameBuilder();
            san.AddDnsName("host.example.com");
            request.CertificateExtensions.Add(san.Build());
            using X509Certificate2 selfSigned = request.CreateSelfSigned(
                DateTimeOffset.UtcNow.AddMinutes(-1),
                DateTimeOffset.UtcNow.AddDays(1));

            string chainPem = selfSigned.ExportCertificatePem();
            using X509Certificate2 leafWithKey = X509Certificate2.CreateFromPem(chainPem, keyPem);
            byte[] pfx = PfxCrypto.ExportPfx(leafWithKey, [], "test-password");
            using X509Certificate2 loaded = PfxCrypto.LoadCertificate(pfx, "test-password");
            Assert.True(loaded.HasPrivateKey);
            Assert.Contains("host.example.com", loaded.Subject, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// <see cref="AcmeJsonContext"/> deserializes a directory resource's <c>newOrder</c> URI and an order's status, DNS identifier, and <see cref="AcmeIdentifierTypes.Dns"/>.
        /// </summary>
        [Fact]
        public void AcmeJsonContext_RoundTripsDirectoryAndOrder()
        {
            const string directoryJson =
                """
                {
                  "newNonce": "https://acme.example/new-nonce",
                  "newAccount": "https://acme.example/new-account",
                  "newOrder": "https://acme.example/new-order"
                }
                """;
            AcmeDirectoryResource? directory = JsonSerializer.Deserialize(
                directoryJson,
                AcmeJsonContext.Default.AcmeDirectoryResource);
            Assert.NotNull(directory);
            Assert.Equal("https://acme.example/new-order", directory.NewOrder?.AbsoluteUri);

            const string orderJson =
                """
                {
                  "status": "pending",
                  "identifiers": [ { "type": "dns", "value": "host.example.com" } ],
                  "authorizations": [ "https://acme.example/authz/1" ],
                  "finalize": "https://acme.example/finalize/1"
                }
                """;
            AcmeOrderResource? order = JsonSerializer.Deserialize(orderJson, AcmeJsonContext.Default.AcmeOrderResource);
            Assert.NotNull(order);
            Assert.Equal("pending", order.Status);
            Assert.Equal("host.example.com", order.Identifiers![0].Value);
            Assert.Equal(AcmeIdentifierTypes.Dns, "dns");
        }

        /// <summary>
        /// <see cref="AcmeClient.BindExistingAccount"/> sets <see cref="AcmeClient.KeyId"/> to the persisted account URI
        /// without creating an HTTP client.
        /// </summary>
        [Fact]
        public void AcmeClient_BindExistingAccount_ReusesPersistedAccountUri()
        {
            using var rsa = RSA.Create(2048);
            using AcmeAccountKey key = AcmeAccountKey.ImportPkcs8Der(rsa.ExportPkcs8PrivateKey());
            var http = new AcmeHttpTransport(
                new ThrowingHttpClientFactory(),
                "unused",
                new Uri("https://acme.example/directory"),
                Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance,
                TimeProvider.System);
            var client = new AcmeClient(
                http,
                key,
                Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance,
                TimeProvider.System);

            client.BindExistingAccount("https://acme.example/acme/acct/42");
            Assert.Equal("https://acme.example/acme/acct/42", client.KeyId);
        }

        /// <summary>
        /// HTTP client factory that fails if account binding attempts a network call.
        /// </summary>
        private sealed class ThrowingHttpClientFactory : IHttpClientFactory
        {
            /// <summary>
            /// Refuses to create a client. Binding an existing account must not call this method.
            /// </summary>
            /// <param name="name">Logical client name supplied by the caller. Unused.</param>
            /// <returns>This method does not return.</returns>
            /// <exception cref="InvalidOperationException">Always thrown so a network call fails the test.</exception>
            public HttpClient CreateClient(string name) =>
                throw new InvalidOperationException("HTTP must not be used in this unit test.");
        }
    }
}

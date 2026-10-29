using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace VectorNNTPD.Common.Acme
{
    /// <summary>Validates PKCS#12/PFX certificate material before reuse or promotion.</summary>
    internal static class CertificateValidator
    {
        /// <summary>
        /// Validates a password-protected PFX and returns usability / renewal status.
        /// </summary>
        /// <param name="pfxBytes">PKCS#12 bytes.</param>
        /// <param name="password">PFX password. Not included in <see cref="CertificateStatus.Reason"/>.</param>
        /// <param name="requiredDomains">DNS names that must appear as SANs.</param>
        /// <param name="renewalThreshold">Window before not-after in which a usable certificate is marked due for renewal. Must be positive; this method does not check that.</param>
        /// <param name="now">UTC time used for validity and renewal. <see langword="null"/> uses <see cref="DateTimeOffset.UtcNow"/>.</param>
        /// <returns>Usable material, or a non-usable status whose reason is a category such as <c>parse_failed</c>, <c>missing_private_key</c>, <c>missing_san</c>, <c>expired</c>, or <c>key_mismatch</c>.</returns>
        /// <remarks>Never includes the password in returned reasons.</remarks>
        internal static CertificateStatus ValidatePfx(
            ReadOnlySpan<byte> pfxBytes,
            string password,
            IReadOnlyList<string> requiredDomains,
            TimeSpan renewalThreshold,
            DateTimeOffset? now = null)
        {
            ArgumentNullException.ThrowIfNull(password);
            ArgumentNullException.ThrowIfNull(requiredDomains);

            var current = now ?? DateTimeOffset.UtcNow;

            X509Certificate2 leaf;
            try
            {
                leaf = PfxCrypto.LoadCertificate(pfxBytes, password);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return new CertificateStatus(
                    Usable: false,
                    DueForRenewal: true,
                    Material: null,
                    Reason: $"parse_failed:{ex.GetType().Name}");
            }

            using (leaf)
            {
                try
                {
                    if (!leaf.HasPrivateKey)
                    {
                        return new CertificateStatus(false, true, null, "missing_private_key");
                    }

                    using var privateKey = leaf.GetRSAPrivateKey()
                        ?? throw new AcmeCertificateException("key_mismatch", "unsupported_key_type");

                    var domains = GetDnsNames(leaf);
                    AssertRequiredDomains(domains, requiredDomains);
                    AssertValidityWindow(leaf, current);
                    AssertKeyMatch(leaf, privateKey);
                    AssertServerAuth(leaf);

                    var material = new CertificateMaterial(
                        PfxBytes: pfxBytes.ToArray(),
                        Domains: domains.OrderBy(static d => d, StringComparer.Ordinal).ToArray(),
                        NotBefore: leaf.NotBefore.ToUniversalTime(),
                        NotAfter: leaf.NotAfter.ToUniversalTime());

                    var due = current >= material.NotAfter - renewalThreshold;
                    return new CertificateStatus(
                        Usable: true,
                        DueForRenewal: due,
                        Material: material,
                        Reason: due ? "renewal_threshold" : "ok");
                }
                catch (AcmeCertificateException ex)
                {
                    return new CertificateStatus(
                        Usable: false,
                        DueForRenewal: true,
                        Material: null,
                        Reason: ex.Category);
                }
            }
        }

        /// <summary>Validates and returns material, throwing on failure.</summary>
        /// <param name="pfxBytes">PKCS#12 bytes.</param>
        /// <param name="password">PFX password.</param>
        /// <param name="requiredDomains">DNS names that must appear as SANs.</param>
        /// <param name="renewalThreshold">Passed through to <see cref="ValidatePfx"/>.</param>
        /// <param name="now">UTC time used for validity. <see langword="null"/> uses <see cref="DateTimeOffset.UtcNow"/>.</param>
        /// <returns>The validated material. A certificate inside the renewal window is still returned.</returns>
        /// <exception cref="AcmeCertificateException">Thrown with category <c>invalid_certificate</c> when <see cref="ValidatePfx"/> reports the material unusable.</exception>
        internal static CertificateMaterial RequireValidPfx(
            ReadOnlySpan<byte> pfxBytes,
            string password,
            IReadOnlyList<string> requiredDomains,
            TimeSpan renewalThreshold,
            DateTimeOffset? now = null)
        {
            var status = ValidatePfx(pfxBytes, password, requiredDomains, renewalThreshold, now);
            if (!status.Usable || status.Material is null)
            {
                throw new AcmeCertificateException("invalid_certificate", status.Reason);
            }

            return status.Material;
        }

        /// <summary>Collects DNS SAN values, lowercased. Throws <see cref="AcmeCertificateException"/> category <c>missing_san</c> when the extension is absent or has no DNS names.</summary>
        /// <param name="cert">Leaf to inspect. Not disposed.</param>
        /// <returns>The DNS names, compared case-insensitively.</returns>
        private static HashSet<string> GetDnsNames(X509Certificate2 cert)
        {
            var extension = cert.Extensions.OfType<X509SubjectAlternativeNameExtension>().FirstOrDefault()
                ?? throw new AcmeCertificateException("missing_san", "no_san_extension");

            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var dnsName in extension.EnumerateDnsNames())
            {
                names.Add(dnsName.ToLowerInvariant());
            }

            if (names.Count == 0)
            {
                throw new AcmeCertificateException("missing_san", "empty_san");
            }

            return names;
        }

        /// <summary>Throws <see cref="AcmeCertificateException"/> category <c>missing_san</c> when any required name is absent.</summary>
        /// <param name="present">DNS names from the leaf.</param>
        /// <param name="required">Names that must all be present. Comparison is case-insensitive.</param>
        private static void AssertRequiredDomains(HashSet<string> present, IReadOnlyList<string> required)
        {
            foreach (var name in required)
            {
                if (!present.Contains(name))
                {
                    throw new AcmeCertificateException("missing_san", "required_identity_absent");
                }
            }
        }

        /// <summary>
        /// Throws category <c>not_yet_valid</c> when <paramref name="now"/> is before not-before,
        /// or <c>expired</c> when <paramref name="now"/> is at or after not-after.
        /// </summary>
        /// <param name="cert">Leaf whose validity window is checked.</param>
        /// <param name="now">UTC instant to compare.</param>
        private static void AssertValidityWindow(X509Certificate2 cert, DateTimeOffset now)
        {
            var notBefore = cert.NotBefore.ToUniversalTime();
            var notAfter = cert.NotAfter.ToUniversalTime();

            if (now < notBefore)
            {
                throw new AcmeCertificateException("not_yet_valid", "not_before");
            }

            if (now >= notAfter)
            {
                throw new AcmeCertificateException("expired", "not_after");
            }
        }

        /// <summary>
        /// Throws category <c>key_mismatch</c> when the certificate public key and <paramref name="privateKey"/>
        /// do not export the same subject public key info.
        /// </summary>
        /// <param name="cert">Leaf that must contain an RSA public key.</param>
        /// <param name="privateKey">Private key loaded from the same PFX.</param>
        private static void AssertKeyMatch(X509Certificate2 cert, RSA privateKey)
        {
            using var certPublic = cert.GetRSAPublicKey()
                ?? throw new AcmeCertificateException("key_mismatch", "unsupported_key_type");

            var certSpki = certPublic.ExportSubjectPublicKeyInfo();
            var keySpki = privateKey.ExportSubjectPublicKeyInfo();
            if (!CryptographicOperations.FixedTimeEquals(certSpki, keySpki))
            {
                throw new AcmeCertificateException("key_mismatch", "public_key_differ");
            }
        }

        /// <summary>
        /// When an EKU extension is present, requires serverAuth (<c>1.3.6.1.5.5.7.3.1</c>).
        /// A missing EKU extension is accepted.
        /// </summary>
        /// <param name="cert">Leaf to inspect.</param>
        private static void AssertServerAuth(X509Certificate2 cert)
        {
            var eku = cert.Extensions.OfType<X509EnhancedKeyUsageExtension>().FirstOrDefault();
            if (eku is null)
            {
                return;
            }

            if (!eku.EnhancedKeyUsages.Cast<Oid>().Any(static oid => oid.Value == "1.3.6.1.5.5.7.3.1"))
            {
                throw new AcmeCertificateException("not_server_auth", "eku_missing_server_auth");
            }
        }
    }
}

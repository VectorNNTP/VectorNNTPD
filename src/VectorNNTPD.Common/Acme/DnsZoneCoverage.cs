namespace VectorNNTPD.Common.Acme
{
    /// <summary>
    /// Ensures every certificate identity falls under the configured DNS apex (<c>DnsSuffix</c>).
    /// </summary>
    internal static class DnsZoneCoverage
    {
        /// <summary>Normalizes a DNS hostname (lowercase, no trailing dots).</summary>
        /// <param name="name">Hostname to trim, strip of trailing dots, and lowercase.</param>
        /// <param name="settingName">Name included in <see cref="AcmeConfigurationException"/> when <paramref name="name"/> is empty, a wildcard, or has an empty label.</param>
        /// <returns>The normalized hostname.</returns>
        /// <exception cref="AcmeConfigurationException">Thrown when the name is empty, contains <c>*</c>, or has an empty label.</exception>
        internal static string NormalizeDnsHostname(string name, string settingName = "dns_name")
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(name);

            var cleaned = name.Trim().TrimEnd('.').ToLowerInvariant();
            if (cleaned.Length == 0)
            {
                throw new AcmeConfigurationException("invalid_dns_name", $"{settingName} empty");
            }

            if (cleaned.Contains('*', StringComparison.Ordinal))
            {
                throw new AcmeConfigurationException("invalid_dns_name", $"{settingName} wildcard");
            }

            var labels = cleaned.Split('.');
            if (labels.Any(static label => label.Length == 0))
            {
                throw new AcmeConfigurationException("invalid_dns_name", $"{settingName} empty_label");
            }

            return cleaned;
        }

        /// <summary>
        /// Returns whether <paramref name="hostname"/> is <paramref name="zoneApex"/> or a subdomain thereof
        /// (label-boundary aware).
        /// </summary>
        /// <param name="zoneApex">Configured DNS apex. Normalized before comparison.</param>
        /// <param name="hostname">Certificate identity. Normalized before comparison.</param>
        /// <returns><see langword="true"/> when the host equals the apex or ends with <c>.{apex}</c>.</returns>
        private static bool ZoneCoversHostname(string zoneApex, string hostname)
        {
            var zone = NormalizeDnsHostname(zoneApex, "zone_apex");
            var host = NormalizeDnsHostname(hostname, "hostname");
            return host == zone || host.EndsWith("." + zone, StringComparison.Ordinal);
        }

        /// <summary>Fails closed unless every identity is inside <paramref name="zoneApex"/>.</summary>
        /// <param name="identities">Certificate DNS names. Empty input fails.</param>
        /// <param name="zoneApex">Configured DNS apex (<c>DnsSuffix</c>).</param>
        /// <exception cref="AcmeConfigurationException">Thrown when the list is empty or any identity is outside the apex.</exception>
        internal static void RequireIdentitiesInDnsZone(IReadOnlyList<string> identities, string zoneApex)
        {
            ArgumentNullException.ThrowIfNull(identities);

            var apex = NormalizeDnsHostname(zoneApex, "DNSSuffix");
            if (identities.Count == 0)
            {
                throw new AcmeConfigurationException("missing_identities", "no certificate identities");
            }

            foreach (var host in identities.Select(static raw => NormalizeDnsHostname(raw, "certificate_identity")))
            {
                if (!ZoneCoversHostname(apex, host))
                {
                    throw new AcmeConfigurationException(
                        "identity_outside_zone",
                        $"{host} not under DNSSuffix={apex}");
                }
            }
        }
    }
}

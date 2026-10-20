using VectorNNTPD.BackFillerService.Accounts;
using VectorNNTPD.BackFillerService.Tests.Fixtures;

namespace VectorNNTPD.BackFillerService.Tests.Accounts
{
    /// <summary>
    /// Maps database rows to BackFillerProviderDefinition with expected defaults.
    /// </summary>

    public sealed class ProviderAccountMapperTests
    {
        /// <summary>
        /// Contract: Map projects a valid row onto the phase4 provider definition.
        /// </summary>

        [Fact]
        public void Map_projects_a_valid_row_onto_the_phase4_provider_definition()
        {
            var mapped = ProviderAccountMapper.Map([ProviderAccountTestRows.Create()]);

            var provider = Assert.Single(mapped.Providers);
            Assert.Empty(mapped.Rejected);
            Assert.Equal("Giganews", provider.Backbone);
            Assert.Equal("news.example.test", provider.Host);
            Assert.Equal(563, provider.Port);
            Assert.True(provider.UseTls);
            Assert.Equal("nntp-user", provider.Username);
            Assert.Equal(ProviderAccountTestRows.SecretPassword, provider.Password);
            Assert.Equal(0, provider.MinSessions);
            Assert.Equal(4, provider.MaxSessions);
            Assert.Equal(0, provider.KeepAliveSeconds);
        }

        /// <summary>
        /// Contract: Map preserves mysql keepalive on the provider definition.
        /// </summary>

        [Fact]
        public void Map_preserves_mysql_keepalive_on_the_provider_definition()
        {
            var mapped = ProviderAccountMapper.Map([ProviderAccountTestRows.Create(keepAlive: 42)]);

            var provider = Assert.Single(mapped.Providers);
            Assert.Equal((byte)42, provider.KeepAliveSeconds);
            Assert.True(provider.DateKeepAliveEnabled);
        }

        /// <summary>
        /// Contract: Map canonicalizes backbone case.
        /// </summary>

        [Theory]
        [InlineData("giganews", "Giganews")]
        [InlineData(" Eweka ", "Eweka")]
        public void Map_canonicalizes_backbone_case(string raw, string canonical)
        {
            var mapped = ProviderAccountMapper.Map([ProviderAccountTestRows.Create(backbone: raw)]);

            Assert.Equal(canonical, Assert.Single(mapped.Providers).Backbone);
            Assert.Empty(mapped.Rejected);
        }

        /// <summary>
        /// Contract: Map rejects unknown backbone without publishing it.
        /// </summary>

        [Fact]
        public void Map_rejects_unknown_backbone_without_publishing_it()
        {
            var mapped = ProviderAccountMapper.Map(
            [
                ProviderAccountTestRows.Create(backbone: "UnknownProvider"),
                ProviderAccountTestRows.Create(backbone: "Eweka", hostname: "eweka.example.test"),
            ]);

            Assert.Equal("Eweka", Assert.Single(mapped.Providers).Backbone);
            var rejected = Assert.Single(mapped.Rejected);
            Assert.Equal("UnknownProvider", rejected.Backbone);
            Assert.Equal("Unknown or unsupported backbone.", rejected.Reason);
        }

        /// <summary>
        /// Contract: Map keeps the first valid row for a duplicate backbone.
        /// </summary>

        [Fact]
        public void Map_keeps_the_first_valid_row_for_a_duplicate_backbone()
        {
            var mapped = ProviderAccountMapper.Map(
            [
                ProviderAccountTestRows.Create(hostname: "first.example.test", maxConnections: 2),
                ProviderAccountTestRows.Create(hostname: "second.example.test", maxConnections: 8),
            ]);

            var provider = Assert.Single(mapped.Providers);
            Assert.Equal("first.example.test", provider.Host);
            Assert.Equal(2, provider.MaxSessions);
            Assert.Equal("Giganews", Assert.Single(mapped.Rejected).Backbone);
            Assert.Contains("Duplicate", Assert.Single(mapped.Rejected).Reason, StringComparison.Ordinal);
        }

        /// <summary>
        /// Contract: Map rejects missing hostname.
        /// </summary>

        [Theory]
        [InlineData("", "Hostname is required.")]
        [InlineData("   ", "Hostname is required.")]
        public void Map_rejects_missing_hostname(string hostname, string reason)
        {
            var mapped = ProviderAccountMapper.Map([ProviderAccountTestRows.Create(hostname: hostname)]);

            Assert.Empty(mapped.Providers);
            Assert.Equal(reason, Assert.Single(mapped.Rejected).Reason);
        }

        /// <summary>
        /// Contract: Map rejects invalid port.
        /// </summary>

        [Theory]
        [InlineData(0)]
        [InlineData(65536)]
        public void Map_rejects_invalid_port(int port)
        {
            var mapped = ProviderAccountMapper.Map([ProviderAccountTestRows.Create(port: port)]);

            Assert.Empty(mapped.Providers);
            Assert.Equal("Port must be between 1 and 65535.", Assert.Single(mapped.Rejected).Reason);
        }

        /// <summary>
        /// Contract: Map rejects max connections below one.
        /// </summary>

        [Theory]
        [InlineData(0)]
        [InlineData(-1)]
        public void Map_rejects_max_connections_below_one(int maxConnections)
        {
            var mapped = ProviderAccountMapper.Map([ProviderAccountTestRows.Create(maxConnections: maxConnections)]);

            Assert.Empty(mapped.Providers);
            Assert.Equal("MaxConnections must be at least 1.", Assert.Single(mapped.Rejected).Reason);
        }

        /// <summary>
        /// Contract: Map rejects a non-null blank username (empty string is not SQL NULL).
        /// </summary>
        [Fact]
        public void Map_rejects_blank_username_when_credential_cell_is_present()
        {
            var mapped = ProviderAccountMapper.Map([ProviderAccountTestRows.Create(username: " ")]);

            Assert.Empty(mapped.Providers);
            Assert.Equal("Username is required.", Assert.Single(mapped.Rejected).Reason);
        }

        /// <summary>
        /// Contract: a blank username is still rejected when the password cell is SQL NULL.
        /// </summary>
        [Fact]
        public void Map_rejects_blank_username_even_when_password_is_sql_null()
        {
            var mapped = ProviderAccountMapper.Map(
                [ProviderAccountTestRows.Create(username: " ", password: null)]);

            Assert.Empty(mapped.Providers);
            Assert.Equal("Username is required.", Assert.Single(mapped.Rejected).Reason);
        }

        /// <summary>
        /// Contract: a SQL NULL username disables authentication and still publishes the account.
        /// </summary>
        [Fact]
        public void Map_null_username_disables_authentication_without_rejecting_the_row()
        {
            var mapped = ProviderAccountMapper.Map(
                [ProviderAccountTestRows.Create(username: null, password: ProviderAccountTestRows.SecretPassword)]);

            var provider = Assert.Single(mapped.Providers);
            Assert.Empty(mapped.Rejected);
            Assert.Null(provider.Username);
            Assert.Null(provider.Password);
            Assert.False(provider.RequiresAuthentication);
        }

        /// <summary>
        /// Contract: Map rejects username with control characters.
        /// </summary>

        [Theory]
        [InlineData("user\rname")]
        [InlineData("user\nname")]
        [InlineData("user\0name")]
        [InlineData("user\x7Fname")]
        public void Map_rejects_username_with_control_characters(string username)
        {
            var mapped = ProviderAccountMapper.Map([ProviderAccountTestRows.Create(username: username)]);

            Assert.Empty(mapped.Providers);
            Assert.Equal("Username contains invalid control characters.", Assert.Single(mapped.Rejected).Reason);
        }

        /// <summary>
        /// Contract: a SQL NULL password disables authentication without rejecting the row or logging the password.
        /// </summary>
        [Fact]
        public void Map_null_password_disables_authentication_without_rejecting_the_row()
        {
            var mapped = ProviderAccountMapper.Map(
                [ProviderAccountTestRows.Create(username: "nntp-user", password: null)]);

            var provider = Assert.Single(mapped.Providers);
            Assert.Empty(mapped.Rejected);
            Assert.Null(provider.Username);
            Assert.Null(provider.Password);
            Assert.False(provider.RequiresAuthentication);
        }

        /// <summary>
        /// Contract: Map rejects a non-null blank password (empty string is not SQL NULL).
        /// </summary>
        [Fact]
        public void Map_rejects_blank_password_when_credential_cell_is_present()
        {
            var mapped = ProviderAccountMapper.Map([ProviderAccountTestRows.Create(password: " ")]);

            Assert.Empty(mapped.Providers);
            Assert.Equal("Password is required.", Assert.Single(mapped.Rejected).Reason);
            Assert.DoesNotContain(
                ProviderAccountTestRows.SecretPassword,
                Assert.Single(mapped.Rejected).Reason,
                StringComparison.Ordinal);
        }

        /// <summary>
        /// Contract: a blank password is still rejected when the username cell is SQL NULL.
        /// </summary>
        [Fact]
        public void Map_rejects_blank_password_even_when_username_is_sql_null()
        {
            var mapped = ProviderAccountMapper.Map(
                [ProviderAccountTestRows.Create(username: null, password: " ")]);

            Assert.Empty(mapped.Providers);
            Assert.Equal("Password is required.", Assert.Single(mapped.Rejected).Reason);
        }

        /// <summary>
        /// Contract: control characters in a present credential reject the row even when the other cell is SQL NULL.
        /// </summary>
        [Theory]
        [InlineData("user\nname", null, "Username contains invalid control characters.")]
        [InlineData(null, "pass\nword", "Password contains invalid control characters.")]
        public void Map_rejects_control_characters_even_when_other_credential_is_sql_null(
            string? username,
            string? password,
            string reason)
        {
            var mapped = ProviderAccountMapper.Map(
                [ProviderAccountTestRows.Create(username: username, password: password)]);

            Assert.Empty(mapped.Providers);
            Assert.Equal(reason, Assert.Single(mapped.Rejected).Reason);
            Assert.DoesNotContain("pass\nword", Assert.Single(mapped.Rejected).Reason, StringComparison.Ordinal);
        }

        /// <summary>
        /// Contract: both credentials present preserve authentication configuration.
        /// </summary>
        [Fact]
        public void Map_both_credentials_present_preserves_authentication()
        {
            var mapped = ProviderAccountMapper.Map([ProviderAccountTestRows.Create()]);

            var provider = Assert.Single(mapped.Providers);
            Assert.Equal("nntp-user", provider.Username);
            Assert.Equal(ProviderAccountTestRows.SecretPassword, provider.Password);
            Assert.True(provider.RequiresAuthentication);
        }

        /// <summary>
        /// Contract: Map rejects password with control characters.
        /// </summary>

        [Theory]
        [InlineData("pass\rword")]
        [InlineData("pass\nword")]
        [InlineData("pass\0word")]
        [InlineData("pass\x7Fword")]
        public void Map_rejects_password_with_control_characters(string password)
        {
            var mapped = ProviderAccountMapper.Map([ProviderAccountTestRows.Create(password: password)]);

            Assert.Empty(mapped.Providers);
            Assert.Equal("Password contains invalid control characters.", Assert.Single(mapped.Rejected).Reason);
        }

        /// <summary>
        /// Contract: a NULL required non-credential field rejects that row while a later valid row is published.
        /// </summary>
        [Fact]
        public void Map_rejects_null_required_non_credential_but_keeps_later_valid_row()
        {
            var mapped = ProviderAccountMapper.Map(
            [
                ProviderAccountTestRows.Create(backbone: "Giganews", hostname: null),
                ProviderAccountTestRows.Create(backbone: "Eweka", hostname: "eweka.example.test"),
            ]);

            Assert.Equal("Eweka", Assert.Single(mapped.Providers).Backbone);
            var rejected = Assert.Single(mapped.Rejected);
            Assert.Equal("Giganews", rejected.Backbone);
            Assert.Equal("Hostname is required.", rejected.Reason);
        }

        /// <summary>
        /// Contract: invalid port/SSL null cells reject only that row.
        /// </summary>
        [Fact]
        public void Map_rejects_null_port_and_usessl_without_affecting_other_rows()
        {
            var mapped = ProviderAccountMapper.Map(
            [
                ProviderAccountTestRows.Create(backbone: "Giganews", port: null),
                ProviderAccountTestRows.Create(backbone: "Eweka", useSsl: null, hostname: "eweka.example.test"),
                ProviderAccountTestRows.Create(backbone: "Abavia", hostname: "abavia.example.test"),
            ]);

            Assert.Equal("Abavia", Assert.Single(mapped.Providers).Backbone);
            Assert.Equal(2, mapped.Rejected.Count);
            Assert.Contains(mapped.Rejected, static r => r.Reason == "Port must be between 1 and 65535.");
            Assert.Contains(mapped.Rejected, static r => r.Reason == "UseSsl must be 'y' or 'n'.");
        }

        /// <summary>
        /// Contract: Map projects usessl onto tls.
        /// </summary>

        [Theory]
        [InlineData("y", true)]
        [InlineData("n", false)]
        public void Map_projects_usessl_onto_tls(string useSsl, bool useTls)
        {
            var mapped = ProviderAccountMapper.Map([ProviderAccountTestRows.Create(useSsl: useSsl, port: 119)]);

            Assert.Equal(useTls, Assert.Single(mapped.Providers).UseTls);
        }

        /// <summary>
        /// Contract: Map rejects invalid usessl.
        /// </summary>

        [Theory]
        [InlineData("Y")]
        [InlineData("yes")]
        [InlineData("1")]
        [InlineData("")]
        public void Map_rejects_invalid_usessl(string useSsl)
        {
            var mapped = ProviderAccountMapper.Map([ProviderAccountTestRows.Create(useSsl: useSsl)]);

            Assert.Empty(mapped.Providers);
            Assert.Equal("UseSsl must be 'y' or 'n'.", Assert.Single(mapped.Rejected).Reason);
        }

        /// <summary>
        /// Contract: Map allows an empty provider result.
        /// </summary>

        [Fact]
        public void Map_allows_an_empty_provider_result()
        {
            var mapped = ProviderAccountMapper.Map([]);

            Assert.Empty(mapped.Providers);
            Assert.Empty(mapped.Rejected);
        }

        /// <summary>
        /// Contract: Map rejects a blank backbone.
        /// </summary>

        [Fact]
        public void Map_rejects_a_blank_backbone()
        {
            var mapped = ProviderAccountMapper.Map([ProviderAccountTestRows.Create(backbone: " ")]);

            Assert.Empty(mapped.Providers);
            var rejected = Assert.Single(mapped.Rejected);
            Assert.Equal(" ", rejected.Backbone);
            Assert.Equal("Unknown or unsupported backbone.", rejected.Reason);
        }
    }
}

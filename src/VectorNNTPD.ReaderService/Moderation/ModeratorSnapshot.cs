using System.Text;
using VectorNNTPD.ReaderService.Newsgroups;

namespace VectorNNTPD.ReaderService.Moderation
{
    /// <summary>
    /// Immutable first-match moderator catalogue compiled from <c>nntpmoderators</c>.
    /// </summary>
    /// <remarks>
    /// Lookups do not query MySQL. The compiled table is safe for concurrent POST use.
    /// Username comparison is ordinal (same as AUTHINFO). Approved identities compare
    /// ASCII case-insensitively after mailbox extraction.
    /// </remarks>
    public sealed class ModeratorSnapshot : IModeratorAuthorization
    {
        /// <summary>Empty catalogue. Every resolve and approval fails.</summary>
        public static ModeratorSnapshot Empty { get; } = new([]);

        private readonly ModeratorRule[] _rules;

        private ModeratorSnapshot(ModeratorRule[] rules)
        {
            _rules = rules;
        }

        /// <summary>Gets the number of compiled rules (tests).</summary>
        public int Count => _rules.Length;

        /// <inheritdoc />
        public bool IsAuthenticatedModerator(string? authenticatedUsername)
        {
            if (string.IsNullOrEmpty(authenticatedUsername))
            {
                return false;
            }

            for (var i = 0; i < _rules.Length; i++)
            {
                if (!string.IsNullOrEmpty(_rules[i].Username)
                    && string.Equals(_rules[i].Username, authenticatedUsername, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        /// <inheritdoc />
        public bool TryResolve(ReadOnlySpan<byte> newsgroup, out ModeratorIdentity identity)
        {
            for (var i = 0; i < _rules.Length; i++)
            {
                var rule = _rules[i];
                if (NntpWildmat.IsMatchValidated(newsgroup, rule.PatternBytes))
                {
                    identity = new ModeratorIdentity(
                        rule.Pattern,
                        ModeratorAddressTemplate.Expand(rule.Address, newsgroup),
                        rule.Username);
                    return true;
                }
            }

            identity = default;
            return false;
        }

        /// <inheritdoc />
        public bool CanApprove(
            string? authenticatedUsername,
            ReadOnlySpan<byte> approvedIdentity,
            ReadOnlySpan<byte> newsgroup)
        {
            if (string.IsNullOrEmpty(authenticatedUsername)
                || !TryResolve(newsgroup, out var identity)
                || string.IsNullOrEmpty(identity.Username)
                || !string.Equals(authenticatedUsername, identity.Username, StringComparison.Ordinal))
            {
                return false;
            }

            return MailboxesEqual(approvedIdentity, identity.Address);
        }

        /// <inheritdoc />
        public bool TryAuthorizeApproval(
            string? authenticatedUsername,
            ReadOnlySpan<string> approvedIdentities,
            ReadOnlySpan<string> moderatedGroups,
            out string? failureDetail)
        {
            if (string.IsNullOrEmpty(authenticatedUsername))
            {
                failureDetail = "unauthenticated approval";
                return false;
            }

            if (approvedIdentities.IsEmpty)
            {
                failureDetail = "missing Approved";
                return false;
            }

            if (moderatedGroups.IsEmpty)
            {
                failureDetail = "no moderated groups";
                return false;
            }

            Span<byte> groupBytes = stackalloc byte[128];
            for (var i = 0; i < moderatedGroups.Length; i++)
            {
                var group = moderatedGroups[i];
                if (group.Length is < 1 or > 128)
                {
                    failureDetail = "malformed newsgroup name";
                    return false;
                }

                var written = Encoding.ASCII.GetBytes(group, groupBytes);
                if (!TryResolve(groupBytes[..written], out var identity))
                {
                    failureDetail = "unresolved moderator";
                    return false;
                }

                if (string.IsNullOrEmpty(identity.Username)
                    || !string.Equals(authenticatedUsername, identity.Username, StringComparison.Ordinal))
                {
                    failureDetail = "unauthorized moderator principal";
                    return false;
                }

                if (!ContainsIdentity(approvedIdentities, identity.Address))
                {
                    failureDetail = "Approved identity not authorized for group";
                    return false;
                }
            }

            for (var i = 0; i < approvedIdentities.Length; i++)
            {
                if (!IdentityCoversAModeratedGroup(approvedIdentities[i], moderatedGroups))
                {
                    failureDetail = "conflicting Approved";
                    return false;
                }
            }

            failureDetail = null;
            return true;
        }

        /// <summary>Compiles enabled rows in the supplied order (must already be <c>moderator_id ASC</c>).</summary>
        public static ModeratorSnapshot Create(IReadOnlyList<NntpModeratorRow> rows)
        {
            ArgumentNullException.ThrowIfNull(rows);
            if (rows.Count == 0)
            {
                return Empty;
            }

            var rules = new List<ModeratorRule>(rows.Count);
            for (var i = 0; i < rows.Count; i++)
            {
                var row = rows[i];
                if (row is null)
                {
                    continue;
                }

                var pattern = row.GroupPattern.Trim();
                var address = row.ModeratorAddress.Trim();
                var username = row.AccountName.Trim();
                if (pattern.Length == 0 || address.Length == 0)
                {
                    continue;
                }

                var patternBytes = Encoding.ASCII.GetBytes(pattern);
                if (!NntpWildmat.TryValidate(patternBytes) || !ModeratorAddressTemplate.TryValidate(address))
                {
                    continue;
                }

                rules.Add(new ModeratorRule(pattern, patternBytes, address, username));
            }

            return rules.Count == 0 ? Empty : new ModeratorSnapshot([.. rules]);
        }

        private bool IdentityCoversAModeratedGroup(string approvedIdentity, ReadOnlySpan<string> moderatedGroups)
        {
            Span<byte> groupBytes = stackalloc byte[128];
            for (var i = 0; i < moderatedGroups.Length; i++)
            {
                var group = moderatedGroups[i];
                if (group.Length is < 1 or > 128)
                {
                    continue;
                }

                var written = Encoding.ASCII.GetBytes(group, groupBytes);
                if (TryResolve(groupBytes[..written], out var identity)
                    && MailboxesEqual(approvedIdentity, identity.Address))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool ContainsIdentity(ReadOnlySpan<string> identities, string expected)
        {
            for (var i = 0; i < identities.Length; i++)
            {
                if (MailboxesEqual(identities[i], expected))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool MailboxesEqual(ReadOnlySpan<char> left, ReadOnlySpan<char> right)
        {
            var a = Trim(left);
            var b = Trim(right);
            if (a.Length != b.Length)
            {
                return false;
            }

            for (var i = 0; i < a.Length; i++)
            {
                if (FoldChar(a[i]) != FoldChar(b[i]))
                {
                    return false;
                }
            }

            return true;
        }

        private static bool MailboxesEqual(ReadOnlySpan<byte> left, ReadOnlySpan<char> right)
        {
            var a = Trim(left);
            var b = Trim(right);
            if (a.Length != b.Length)
            {
                return false;
            }

            for (var i = 0; i < a.Length; i++)
            {
                if (FoldByte(a[i]) != FoldChar(b[i]))
                {
                    return false;
                }
            }

            return true;
        }

        private static ReadOnlySpan<char> Trim(ReadOnlySpan<char> value)
        {
            var start = 0;
            var end = value.Length;
            while (start < end && char.IsWhiteSpace(value[start]))
            {
                start++;
            }

            while (end > start && char.IsWhiteSpace(value[end - 1]))
            {
                end--;
            }

            return value[start..end];
        }

        private static ReadOnlySpan<byte> Trim(ReadOnlySpan<byte> value)
        {
            var start = 0;
            var end = value.Length;
            while (start < end && value[start] is (byte)' ' or (byte)'\t')
            {
                start++;
            }

            while (end > start && value[end - 1] is (byte)' ' or (byte)'\t')
            {
                end--;
            }

            return value[start..end];
        }

        private static char FoldChar(char c) =>
            c is >= 'a' and <= 'z' ? (char)(c - 32) : c;

        private static char FoldByte(byte b) =>
            (char)(b is >= (byte)'a' and <= (byte)'z' ? b - 32 : b);

        private readonly record struct ModeratorRule(
            string Pattern,
            byte[] PatternBytes,
            string Address,
            string Username);
    }
}

namespace VectorNNTPD.TransitService.Email.Smtp
{
    /// <summary>Server capabilities advertised by EHLO (RFC 5321).</summary>
    public sealed class SmtpCapabilities
    {
        /// <summary>Gets whether STARTTLS (RFC 3207) is advertised.</summary>
        public bool StartTls { get; init; }

        /// <summary>Gets whether 8BITMIME is advertised.</summary>
        public bool EightBitMime { get; init; }

        /// <summary>Gets whether SMTPUTF8 (RFC 6531) is advertised.</summary>
        public bool SmtpUtf8 { get; init; }

        /// <summary>Gets the advertised SIZE limit in octets, when present.</summary>
        public long? MaxSize { get; init; }

        /// <summary>Gets AUTH mechanism names in server order (uppercased).</summary>
        public IReadOnlyList<string> AuthMechanisms { get; init; } = [];

        /// <summary>Parses EHLO reply lines after the greeting line.</summary>
        public static SmtpCapabilities Parse(SmtpResponse ehlo)
        {
            ArgumentNullException.ThrowIfNull(ehlo);
            var startTls = false;
            var eightBit = false;
            var utf8 = false;
            long? size = null;
            var auth = new List<string>();

            foreach (var line in ehlo.Lines)
            {
                var keyword = Keyword(line);
                if (keyword.Equals("STARTTLS", StringComparison.OrdinalIgnoreCase))
                {
                    startTls = true;
                }
                else if (keyword.Equals("8BITMIME", StringComparison.OrdinalIgnoreCase))
                {
                    eightBit = true;
                }
                else if (keyword.Equals("SMTPUTF8", StringComparison.OrdinalIgnoreCase))
                {
                    utf8 = true;
                }
                else if (keyword.Equals("SIZE", StringComparison.OrdinalIgnoreCase))
                {
                    var rest = Rest(line);
                    if (long.TryParse(rest, out var parsed) && parsed > 0)
                    {
                        size = parsed;
                    }
                }
                else if (keyword.Equals("AUTH", StringComparison.OrdinalIgnoreCase))
                {
                    foreach (var mechanism in Rest(line).Split(' ', StringSplitOptions.RemoveEmptyEntries))
                    {
                        auth.Add(mechanism.ToUpperInvariant());
                    }
                }
            }

            return new SmtpCapabilities
            {
                StartTls = startTls,
                EightBitMime = eightBit,
                SmtpUtf8 = utf8,
                MaxSize = size,
                AuthMechanisms = auth,
            };
        }

        /// <summary>Returns whether <paramref name="mechanism"/> was advertised.</summary>
        public bool SupportsAuth(string mechanism)
        {
            foreach (var advertised in AuthMechanisms)
            {
                if (advertised.Equals(mechanism, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private static string Keyword(string line)
        {
            var span = line.AsSpan().Trim();
            var space = span.IndexOf(' ');
            return space < 0 ? span.ToString() : span[..space].ToString();
        }

        private static string Rest(string line)
        {
            var span = line.AsSpan().Trim();
            var space = span.IndexOf(' ');
            return space < 0 ? string.Empty : span[(space + 1)..].Trim().ToString();
        }
    }
}

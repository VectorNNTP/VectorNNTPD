using System.Globalization;

namespace VectorNNTPD.TransitService.Configuration
{
    /// <summary>
    /// Parses and formats <see cref="TransitMessageTypes"/> from Diablo <c>ArtTypeConv</c> names.
    /// </summary>
    public static class TransitMessageTypesParser
    {
        /// <summary>Canonical default when <c>MessageTypes</c> is omitted.</summary>
        public static readonly string[] DefaultConfiguredValues = ["default"];

        private static readonly (string Name, TransitMessageTypes Value)[] Tokens =
        [
            ("none", TransitMessageTypes.None),
            ("default", TransitMessageTypes.Default),
            ("control", TransitMessageTypes.Control),
            ("cancel", TransitMessageTypes.Cancel),
            ("mime", TransitMessageTypes.Mime),
            ("binary", TransitMessageTypes.Binary),
            ("binaries", TransitMessageTypes.Binary),
            ("uuencode", TransitMessageTypes.UuEncode),
            ("base64", TransitMessageTypes.Base64),
            ("yenc", TransitMessageTypes.Yenc),
            ("bommanews", TransitMessageTypes.BommaNews),
            ("unidata", TransitMessageTypes.UniData),
            ("multipart", TransitMessageTypes.Multipart),
            ("html", TransitMessageTypes.Html),
            ("ps", TransitMessageTypes.PostScript),
            ("binhex", TransitMessageTypes.BinHex),
            ("partial", TransitMessageTypes.Partial),
            ("pgp", TransitMessageTypes.PgpMessage),
            ("all", TransitMessageTypes.All),
        ];

        /// <summary>
        /// Parses a JSON string-array of Diablo names into flags.
        /// </summary>
        /// <remarks>
        /// Comparison is case-insensitive. Surrounding whitespace on each value is ignored.
        /// Duplicate names are OR-ed (harmless). Unknown values fail.
        /// </remarks>
        public static bool TryParse(
            IReadOnlyList<string>? values,
            out TransitMessageTypes types,
            out string? error)
        {
            types = TransitMessageTypes.None;
            error = null;
            if (values is null || values.Count == 0)
            {
                types = TransitMessageTypes.Default;
                return true;
            }

            for (var i = 0; i < values.Count; i++)
            {
                var raw = values[i];
                if (raw is null || !TryParseToken(raw, out var flag))
                {
                    error = string.Create(
                        CultureInfo.InvariantCulture,
                        $"[{i}] is not a recognised MessageTypes value.");
                    types = TransitMessageTypes.None;
                    return false;
                }

                types |= flag;
            }

            return true;
        }

        /// <summary>Parses one Diablo token (case-insensitive, trimmed).</summary>
        public static bool TryParseToken(string value, out TransitMessageTypes type)
        {
            ArgumentNullException.ThrowIfNull(value);
            type = TransitMessageTypes.None;
            var token = value.AsSpan().Trim();
            if (token.IsEmpty)
            {
                return false;
            }

            foreach (var (name, flag) in Tokens)
            {
                if (token.Equals(name, StringComparison.OrdinalIgnoreCase))
                {
                    type = flag;
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Returns canonical lowercase Diablo names for diagnostics (never includes passwords).
        /// </summary>
        public static IReadOnlyList<string> ToCanonicalNames(TransitMessageTypes types)
        {
            if (types == TransitMessageTypes.None)
            {
                return ["none"];
            }

            if (types == TransitMessageTypes.All)
            {
                return ["all"];
            }

            var names = new List<string>();
            foreach (var (name, flag) in Tokens)
            {
                if (flag is TransitMessageTypes.None or TransitMessageTypes.All)
                {
                    continue;
                }

                if (name.Equals("binaries", StringComparison.Ordinal))
                {
                    continue;
                }

                if (types.HasFlag(flag))
                {
                    names.Add(name);
                }
            }

            return names;
        }
    }
}

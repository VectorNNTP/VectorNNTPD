using System.Text;

namespace VectorNNTPD.BackFillerService.Nntp
{
    /// <summary>
    /// Formats exception text for <see cref="ArticleRetrievalResult.Reason"/>, UnexpectedFailure pipeline errors,
    /// and related failure diagnostics.
    /// </summary>
    /// <remarks>
    /// Reasons are operator-facing and must not embed credentials or article bodies.
    /// Callers supply only transport/protocol or handler exceptions whose messages are already scrubbed of secrets.
    /// Non-duplicate inner-exception type/message pairs may be appended after <c> &lt;- </c> when they add context
    /// (for example a blank outer message with a useful <see cref="System.Net.Sockets.SocketException"/> inner).
    /// Typical <see cref="IOException"/> wrappers already embed the socket text in <see cref="Exception.Message"/>;
    /// those inners are skipped to avoid repetition.
    /// </remarks>
    internal static class NntpExceptionDiagnostics
    {
        /// <summary>Maximum characters in the returned reason, including type names and any truncation marker.</summary>
        private const int MaxReasonLength = 256;

        /// <summary>Maximum inner exceptions considered after the outer exception.</summary>
        private const int MaxInnerDepth = 3;

        /// <summary>Marker appended when the formatted reason is truncated to <see cref="MaxReasonLength"/>.</summary>
        private const string TruncationMarker = "...";

        /// <summary>
        /// Builds a non-empty diagnostic reason from an exception type, message, and useful inner context.
        /// </summary>
        /// <param name="exception">Caught transport, protocol, or handler exception.</param>
        /// <returns>
        /// <c>{TypeName}</c>, <c>{TypeName}: {message}</c>, and optionally
        /// <c> &lt;- {InnerType}: {inner message}</c> segments when an inner exception adds non-duplicate text.
        /// The full string is at most <see cref="MaxReasonLength"/> characters; longer text ends with <c>...</c>.
        /// </returns>
        internal static string FormatReason(Exception exception)
        {
            ArgumentNullException.ThrowIfNull(exception);

            var builder = new StringBuilder(capacity: 128);
            AppendSegment(builder, exception);

            var seen = new HashSet<Exception>(ReferenceEqualityComparer.Instance) { exception };
            var inner = exception.InnerException;
            for (var depth = 0; inner is not null && depth < MaxInnerDepth; depth++)
            {
                if (!seen.Add(inner))
                {
                    break;
                }

                if (ShouldAppendInner(builder, inner))
                {
                    builder.Append(" <- ");
                    AppendSegment(builder, inner);
                }

                inner = inner.InnerException;
            }

            return Truncate(builder.ToString());
        }

        /// <summary>Appends <c>{Type}</c> or <c>{Type}: {trimmed message}</c> for one exception.</summary>
        private static void AppendSegment(StringBuilder builder, Exception exception)
        {
            builder.Append(exception.GetType().Name);
            if (string.IsNullOrWhiteSpace(exception.Message))
            {
                return;
            }

            builder.Append(": ");
            builder.Append(exception.Message.Trim());
        }

        /// <summary>
        /// Returns whether <paramref name="inner"/> adds type/message text not already present in <paramref name="builder"/>.
        /// </summary>
        private static bool ShouldAppendInner(StringBuilder builder, Exception inner)
        {
            if (!string.IsNullOrWhiteSpace(inner.Message))
            {
                var message = inner.Message.Trim();
                // Typical IOException wrappers already embed the socket/TLS detail in Message.
                if (ContainsOrdinal(builder, message))
                {
                    return false;
                }

                return true;
            }

            // Blank inner message: only the type name could add information.
            return !ContainsOrdinal(builder, inner.GetType().Name);
        }

        /// <summary>Ordinal substring search over the current builder contents.</summary>
        private static bool ContainsOrdinal(StringBuilder builder, string value)
        {
            if (value.Length == 0 || builder.Length < value.Length)
            {
                return false;
            }

            return builder.ToString().Contains(value, StringComparison.Ordinal);
        }

        /// <summary>Caps the reason at <see cref="MaxReasonLength"/>, appending <see cref="TruncationMarker"/> when cut.</summary>
        private static string Truncate(string reason)
        {
            if (reason.Length <= MaxReasonLength)
            {
                return reason;
            }

            var keep = MaxReasonLength - TruncationMarker.Length;
            if (keep <= 0)
            {
                return TruncationMarker[..MaxReasonLength];
            }

            return string.Concat(reason.AsSpan(0, keep), TruncationMarker);
        }

        /// <summary>Reference equality for exception cycle detection along <see cref="Exception.InnerException"/> chains.</summary>
        private sealed class ReferenceEqualityComparer : IEqualityComparer<Exception>
        {
            internal static readonly ReferenceEqualityComparer Instance = new();

            public bool Equals(Exception? x, Exception? y) => ReferenceEquals(x, y);

            public int GetHashCode(Exception obj) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
        }
    }
}

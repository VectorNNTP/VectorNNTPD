using VectorNNTPD.ReaderService.Configuration;

namespace VectorNNTPD.ReaderService.Ninpaths
{
    /// <summary>
    /// Resolves <c>Nntpd:Top1000</c> recipients. Missing, null, empty, and
    /// whitespace-only entries disable or skip; remaining mailboxes enable reporting.
    /// </summary>
    internal static class NinpathsTop1000
    {
        /// <summary>Returns distinct trimmed mailboxes, ignoring whitespace-only entries.</summary>
        public static string[] GetRecipients(NntpdOptions options)
        {
            ArgumentNullException.ThrowIfNull(options);
            if (options.Top1000 is null || options.Top1000.Length == 0)
            {
                return [];
            }

            List<string>? list = null;
            foreach (var raw in options.Top1000)
            {
                if (!EmailOptionsValidator.TryValidateMailbox(raw, out var mailbox))
                {
                    continue;
                }

                list ??= new List<string>(options.Top1000.Length);
                var exists = false;
                for (var i = 0; i < list.Count; i++)
                {
                    if (string.Equals(list[i], mailbox, StringComparison.OrdinalIgnoreCase))
                    {
                        exists = true;
                        break;
                    }
                }

                if (!exists)
                {
                    list.Add(mailbox);
                }
            }

            return list is null ? [] : [.. list];
        }

        /// <summary>True when at least one usable Top1000 mailbox is configured.</summary>
        public static bool IsEnabled(NntpdOptions options) => GetRecipients(options).Length > 0;
    }
}

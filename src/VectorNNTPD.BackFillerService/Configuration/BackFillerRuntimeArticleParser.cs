using VectorNNTPD.Common.Articles.Parsing;

namespace VectorNNTPD.BackFillerService.Configuration
{
    /// <summary>
    /// Supplies the DI fallback <see cref="NntpArticleParser"/> bound to the current
    /// <see cref="BackFillerRuntimeOptions.Fqdn"/>.
    /// </summary>
    /// <remarks>
    /// The runtime FQDN is provisional from the config file until
    /// <see cref="BackFillerRuntimeOptions.ApplySharedDnsIdentity"/> runs. This type rebuilds the
    /// parser when that FQDN changes so construction-time consumers do not keep the stale identity.
    /// </remarks>
    internal sealed class BackFillerRuntimeArticleParser
    {
        /// <summary>Runtime snapshot whose <see cref="BackFillerRuntimeOptions.Fqdn"/> drives the parser.</summary>
        private readonly BackFillerRuntimeOptions _runtime;

        /// <summary>Guards rebuild of <see cref="_parser"/> when the FQDN changes.</summary>
        private readonly Lock _gate = new();

        /// <summary>FQDN last used to construct <see cref="_parser"/>.</summary>
        private string _fqdn;

        /// <summary>Parser for <see cref="_fqdn"/>.</summary>
        private NntpArticleParser _parser;

        /// <summary>Creates a parser bound to the current runtime FQDN.</summary>
        /// <param name="runtime">Host runtime identity. Not null.</param>
        /// <exception cref="ArgumentNullException"><paramref name="runtime"/> is null.</exception>
        internal BackFillerRuntimeArticleParser(BackFillerRuntimeOptions runtime)
        {
            ArgumentNullException.ThrowIfNull(runtime);
            _runtime = runtime;
            _fqdn = runtime.Fqdn;
            _parser = new NntpArticleParser(_fqdn);
        }

        /// <summary>
        /// Gets a parser whose local identity matches <see cref="BackFillerRuntimeOptions.Fqdn"/> right now.
        /// </summary>
        internal NntpArticleParser Current
        {
            get
            {
                var fqdn = _runtime.Fqdn;
                if (string.Equals(_fqdn, fqdn, StringComparison.Ordinal))
                {
                    return _parser;
                }

                lock (_gate)
                {
                    if (!string.Equals(_fqdn, fqdn, StringComparison.Ordinal))
                    {
                        _fqdn = fqdn;
                        _parser = new NntpArticleParser(_fqdn);
                    }

                    return _parser;
                }
            }
        }
    }
}

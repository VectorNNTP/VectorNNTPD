namespace VectorNNTPD.TransitService.Newsgroups
{
    /// <summary>Malformed or incomplete newsgroup catalogue data that must not be published.</summary>
    public sealed class NewsgroupCatalogueException : Exception
    {
        /// <summary>Initializes a new instance of the <see cref="NewsgroupCatalogueException"/> class.</summary>
        public NewsgroupCatalogueException(string message)
            : base(message)
        {
        }

        /// <summary>Initializes a new instance of the <see cref="NewsgroupCatalogueException"/> class.</summary>
        public NewsgroupCatalogueException(string message, Exception innerException)
            : base(message, innerException)
        {
        }
    }
}

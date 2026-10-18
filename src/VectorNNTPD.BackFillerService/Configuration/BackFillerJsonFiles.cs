namespace VectorNNTPD.BackFillerService.Configuration
{
    /// <summary>
    /// BackFiller filenames passed to Common <c>ApplicationJsonConfiguration</c>.
    /// </summary>
    /// <remarks>
    /// <see cref="SharedRabbitMqFileName"/> is the only RabbitMQ JSON file this host registers.
    /// The loader does not substitute another filename when that file is absent.
    /// </remarks>
    internal static class BackFillerJsonFiles
    {
        /// <summary>RabbitMQ connectivity file copied to the BackFiller output directory.</summary>
        internal const string SharedRabbitMqFileName = "VectorNNTPD.RabbitMQ.json";
    }
}

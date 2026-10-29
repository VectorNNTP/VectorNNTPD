namespace VectorNNTPD.TransitService.ArticleIngestion
{
    /// <summary>
    /// Emits an already-decided INN <c>news</c> event.
    /// </summary>
    /// <remarks>
    /// Production uses a dedicated Serilog pipeline
    /// (<see cref="SerilogNewsLogWriter"/>). File lifecycle is Serilog's
    /// responsibility. Implementations must throw on write failure rather than
    /// pretending the event was recorded.
    /// </remarks>
    public interface INewsLogWriter
    {
        /// <summary>
        /// Appends one complete INN <c>news</c> line for <paramref name="evt"/>.
        /// </summary>
        /// <param name="evt">Already-decided news event.</param>
        /// <exception cref="IOException">The line could not be written.</exception>
        void Write(in NewsLogEvent evt);

        /// <summary>Flushes buffered news lines when the implementation buffers independently of Serilog.</summary>
        void Flush();
    }
}

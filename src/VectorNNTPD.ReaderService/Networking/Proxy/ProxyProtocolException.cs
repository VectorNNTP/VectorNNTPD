namespace VectorNNTPD.ReaderService.Networking.Proxy
{
    /// <summary>Base failure while consuming a required PROXY protocol preamble.</summary>
    public class ProxyProtocolException : IOException
    {
        /// <summary>Initializes a new instance of the <see cref="ProxyProtocolException"/> class.</summary>
        public ProxyProtocolException(string message)
            : base(message)
        {
        }

        /// <summary>Initializes a new instance of the <see cref="ProxyProtocolException"/> class.</summary>
        public ProxyProtocolException(string message, Exception innerException)
            : base(message, innerException)
        {
        }
    }

    /// <summary>Malformed, incomplete, or otherwise invalid PROXY protocol header.</summary>
    public sealed class ProxyProtocolMalformedException : ProxyProtocolException
    {
        /// <summary>Initializes a new instance of the <see cref="ProxyProtocolMalformedException"/> class.</summary>
        public ProxyProtocolMalformedException(string message)
            : base(message)
        {
        }
    }

    /// <summary>Timed out waiting for a complete PROXY protocol preamble.</summary>
    public sealed class ProxyProtocolTimeoutException : ProxyProtocolException
    {
        /// <summary>Initializes a new instance of the <see cref="ProxyProtocolTimeoutException"/> class.</summary>
        public ProxyProtocolTimeoutException(string message)
            : base(message)
        {
        }
    }
}

namespace VectorNNTPD.TransitService.Ninpaths
{
    /// <summary>
    /// Constants matching current INN <c>backends/ninpaths.c</c> (version 3.1.1).
    /// </summary>
    internal static class NinpathsConstants
    {
        /// <summary>Dump format version written in the <c>!!NINP</c> header.</summary>
        public const string Version = "3.1.1";

        /// <summary>INN <c>MAXLINE</c>: <c>fgets</c> payload is this size including the terminator slot.</summary>
        public const int MaxLine = 1024;

        /// <summary>INN <c>HASH_TBL</c>.</summary>
        public const int HashTableSize = 65536;

        /// <summary>INN <c>MAXHOST</c> including the C string terminator.</summary>
        public const int MaxHost = 128;

        /// <summary>INN <c>RECLINE</c> dump line soft wrap.</summary>
        public const int RecordLineSoftLimit = 120;

        /// <summary>FNV prime used by INN <c>hash()</c>.</summary>
        public const ulong HashPrime = 16777619;

        /// <summary>Maximum host token length (<c>MAXHOST - 1</c>).</summary>
        public const int MaxHostChars = MaxHost - 1;

        /// <summary>Maximum bytes <c>fgets(buf, MAXLINE)</c> stores before the NUL.</summary>
        public const int MaxFgetsChars = MaxLine - 1;

        /// <summary>Bounded pending completed-file queue (daily files, not Path records).</summary>
        public const int PendingFileCapacity = 8;

        /// <summary>Sequential read buffer for completed Path-survey files.</summary>
        public const int ReadBufferSize = 64 * 1024;

        /// <summary>sendinpaths subject prefix including the trailing space before the pathhost.</summary>
        public const string EmailSubjectPrefix = "inpaths ";
    }
}

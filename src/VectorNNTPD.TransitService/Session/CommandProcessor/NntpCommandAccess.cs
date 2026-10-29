namespace VectorNNTPD.TransitService.Session.CommandProcessor
{
    /// <summary>Authorization requirements evaluated by the command dispatcher before invoking a handler.</summary>
    [Flags]
    public enum NntpCommandAccess
    {
        /// <summary>Command is allowed without authentication (public/pre-auth).</summary>
        Public = 0,

        /// <summary>Peer must be authenticated (<c>480</c> if not).</summary>
        RequiresAuthentication = 1,

        /// <summary>Peer must have reader authorization (<c>502</c> if authenticated but not authorized).</summary>
        RequiresReader = 2,

        /// <summary>Peer must have transit authorization (<c>502</c> if not).</summary>
        RequiresTransit = 4,

        /// <summary>Peer must have posting permission (in addition to reader where applicable).</summary>
        RequiresPosting = 8,

        /// <summary>Session must be in reader mode.</summary>
        RequiresReaderMode = 16,

        /// <summary>Session must be in stream mode.</summary>
        RequiresStreamMode = 32,

        /// <summary>Peer must have streaming permission to enter stream/transit mode.</summary>
        RequiresStreaming = 64,
    }
}

namespace VectorNNTPD.TransitService.Session.Commands.Posting
{
    /// <summary>Source-generated POST accept/reject diagnostics. Does not log article bodies.</summary>
    internal static partial class PostLogMessages
    {
        [LoggerMessage(
            EventId = 1720,
            Level = LogLevel.Information,
            Message = "[{Client}] POST accepted Message-ID={MessageId} newsgroups={Newsgroups} size={Size}")]
        public static partial void Accepted(
            ILogger logger,
            string Client,
            string MessageId,
            string Newsgroups,
            int Size);

        [LoggerMessage(
            EventId = 1722,
            Level = LogLevel.Information,
            Message = "[{Client}] POST submitted for moderation Message-ID={MessageId} newsgroups={Newsgroups} target={TargetGroup} moderator={ModeratorAddress} user={Username} size={Size}")]
        public static partial void SubmittedForModeration(
            ILogger logger,
            string Client,
            string MessageId,
            string Newsgroups,
            string TargetGroup,
            string ModeratorAddress,
            string Username,
            int Size);

        [LoggerMessage(
            EventId = 1721,
            Level = LogLevel.Information,
            Message = "[{Client}] POST rejected category={Category} Message-ID={MessageId} newsgroups={Newsgroups} size={Size} detail={Detail}")]
        public static partial void Rejected(
            ILogger logger,
            string Client,
            PostingFailureCategory Category,
            string MessageId,
            string Newsgroups,
            int Size,
            string Detail);
    }
}

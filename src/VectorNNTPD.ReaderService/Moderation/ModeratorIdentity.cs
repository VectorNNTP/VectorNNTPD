namespace VectorNNTPD.ReaderService.Moderation
{
    /// <summary>
    /// Resolved moderator route for one newsgroup: expected <c>Approved:</c> identity and
    /// authorized AUTHINFO principal.
    /// </summary>
    /// <param name="Pattern">Wildmat that matched the newsgroup.</param>
    /// <param name="Address">Resolved routing mailbox (static or INN <c>%s</c> expansion).</param>
    /// <param name="Username">AUTHINFO username authorized to inject that approval, or empty for routing-only.</param>
    public readonly record struct ModeratorIdentity(string Pattern, string Address, string Username);
}

namespace VectorNNTPD.TransitService.PostFilter
{
    /// <summary>Publishes the current immutable PostFilter snapshot.</summary>
    internal interface IPostFilterPolicySource
    {
        /// <summary>Gets the last-known-good snapshot. Throws when none has been published.</summary>
        PostFilterPolicySnapshot Current { get; }
    }
}

namespace VectorNNTPD.StorageService.Ingress
{

    /// <summary>
    /// Bytes held for one STORE whose body has been admitted and not yet queued.
    /// </summary>
    /// <param name="Id">Reservation identity. Zero is not a reservation.</param>
    /// <param name="Bytes">Reserved article length.</param>
    public readonly record struct QueueReservation(long Id, int Bytes)
    {
        /// <summary>Gets whether this value was issued by the queue.</summary>
        public bool IsActive => Id > 0 && Bytes > 0;
    }
}

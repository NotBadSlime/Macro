namespace InputApiProbe;

internal interface ICaptureStrategy : IDisposable
{
    string Id { get; }

    /// <summary>
    /// Raised on the strategy's own thread with the QPC taken at the first instruction of its callback
    /// (for polling: right after the call that observed the key down) when the key goes down.
    /// </summary>
    event Action<long>? Captured;

    void Start();
    void Stop();
}

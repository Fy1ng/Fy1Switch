using Fy1Switch.Contracts;

namespace Fy1Switch.Worker;

internal interface IDisplayProvider : IDisposable
{
    Task<DiscoverySnapshot> DiscoverAsync(CancellationToken cancellationToken);
    Task<PowerObservation> ReadAsync(string targetKey, string fingerprint, CancellationToken cancellationToken);
    Task<PowerWriteResult> SetAsync(string targetKey, string fingerprint, PowerIntent intent, CancellationToken cancellationToken);
}

internal sealed record PowerWriteResult(bool WriteAccepted, PowerObservation Observation);

internal sealed class DisplayProviderException(string errorCode, bool? writeAccepted = null) : Exception(errorCode)
{
    public string ErrorCode { get; } = errorCode;
    public bool? WriteAccepted { get; } = writeAccepted;
}

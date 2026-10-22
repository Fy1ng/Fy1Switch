namespace Fy1Switch.Contracts;

public sealed record RemoteCommand(Guid RequestId, Guid DeviceId, string Command, DateTimeOffset ExpiresAt, Guid? TargetId = null, PowerIntent? Intent = null, Guid? OperationId = null, int Version = 1);
public sealed record RemoteStatus(Guid DeviceId, Guid? TargetId, string? DisplayName, PowerObservation Observation, bool Available, bool Busy, string? TailscaleEndpoint = null);
public sealed record RemoteReply(Guid RequestId, bool Success, string? ErrorCode = null, RemoteStatus? Status = null, OperationRecord? Operation = null, int Version = 1);
public sealed record PairRequest(string Code, string ClientName);
public sealed record PairReply(Guid DeviceId, Guid ClientId, string Token);
public sealed record PairBundle(int Version, Guid DeviceId, string Endpoint, string CertificatePin, string Code, DateTimeOffset ExpiresAt, string? TailscaleEndpoint = null);
public sealed record RelayEnvelope(Guid Id, string Kind, string? DeviceToken, RemoteCommand? Request = null, PairRequest? Pair = null);
public sealed record RelayResponse(Guid Id, int StatusCode, RemoteReply? Reply = null, PairReply? Pair = null, string? ErrorCode = null);

using System;

namespace Fy1Switch.PowerDisplay;

public readonly record struct PowerReadObservation(bool Succeeded, int? Value, int? ErrorCode, DateTimeOffset ObservedAt)
{
    public bool IsKnownPowerValue => Succeeded && Value is >= 1 and <= 5;

    public static PowerReadObservation FromNative(bool succeeded, uint current, int errorCode) =>
        new(succeeded, succeeded ? unchecked((int)current) : null, succeeded ? null : errorCode, DateTimeOffset.UtcNow);
}

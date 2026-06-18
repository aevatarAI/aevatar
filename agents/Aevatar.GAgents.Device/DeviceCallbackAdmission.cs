using System.Globalization;

namespace Aevatar.GAgents.Device;

public sealed record DeviceCallbackAdmission(
    string RegistrationId,
    string MessageId,
    string DeliveryId,
    string SignedBodyTimestampValue,
    DateTimeOffset SignedBodyTimestamp,
    DateTimeOffset OccurredAt)
{
    public const string OperationPrefix = "device-event";

    public string OperationId => $"{OperationPrefix}:{RegistrationId.Trim()}:{DeliveryId.Trim()}";

    public bool IsFreshAt(DateTimeOffset now, TimeSpan freshnessWindow)
    {
        var skew = now >= SignedBodyTimestamp
            ? now - SignedBodyTimestamp
            : SignedBodyTimestamp - now;

        return skew <= freshnessWindow;
    }

    public static bool TryParseSignedBodyTimestamp(string value, out DateTimeOffset timestamp)
    {
        if (DateTimeOffset.TryParse(
                value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out timestamp))
        {
            timestamp = timestamp.ToUniversalTime();
            return true;
        }

        timestamp = default;
        return false;
    }
}

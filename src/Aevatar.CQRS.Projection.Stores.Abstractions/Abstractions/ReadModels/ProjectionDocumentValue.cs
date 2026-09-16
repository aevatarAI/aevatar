using System.Reflection;
using Google.Protobuf.Reflection;

namespace Aevatar.CQRS.Projection.Stores.Abstractions;

public sealed class ProjectionDocumentValue
{
    public static ProjectionDocumentValue Empty { get; } = new(ProjectionDocumentValueKind.None, null);

    public ProjectionDocumentValueKind Kind { get; }

    public object? RawValue { get; }

    private ProjectionDocumentValue(ProjectionDocumentValueKind kind, object? rawValue)
    {
        Kind = kind;
        RawValue = rawValue;
    }

    public static ProjectionDocumentValue FromString(string? value) =>
        new(ProjectionDocumentValueKind.String, value ?? string.Empty);

    public static ProjectionDocumentValue FromStrings(IEnumerable<string?> values) =>
        new(
            ProjectionDocumentValueKind.StringList,
            values?.Select(x => x ?? string.Empty).ToArray() ?? []);

    /// <summary>
    /// Builds the exact-match value for a protobuf enum field. Document stores persist proto
    /// enums in their protobuf-JSON form (the proto value name, e.g.
    /// <c>WORKFLOW_SAGA_STATUS_COMPENSATING</c>), not the C# member name, so filters must
    /// carry the same form for every store to agree.
    /// </summary>
    public static ProjectionDocumentValue FromProtoEnum<TEnum>(TEnum value)
        where TEnum : struct, Enum =>
        FromString(ResolveProtoEnumName(value));

    /// <summary>
    /// Resolves the protobuf-JSON name of an enum value: the proto value name declared through
    /// <see cref="OriginalNameAttribute"/>, or the C# member name for non-proto enums.
    /// </summary>
    public static string ResolveProtoEnumName(Enum value)
    {
        var memberName = value.ToString();
        return value.GetType().GetField(memberName)?.GetCustomAttribute<OriginalNameAttribute>()?.Name
               ?? memberName;
    }

    public static ProjectionDocumentValue FromInt64(long value) =>
        new(ProjectionDocumentValueKind.Int64, value);

    public static ProjectionDocumentValue FromInt64s(IEnumerable<long> values) =>
        new(ProjectionDocumentValueKind.Int64List, values?.ToArray() ?? []);

    public static ProjectionDocumentValue FromDouble(double value) =>
        new(ProjectionDocumentValueKind.Double, value);

    public static ProjectionDocumentValue FromDoubles(IEnumerable<double> values) =>
        new(ProjectionDocumentValueKind.DoubleList, values?.ToArray() ?? []);

    public static ProjectionDocumentValue FromBool(bool value) =>
        new(ProjectionDocumentValueKind.Bool, value);

    public static ProjectionDocumentValue FromBools(IEnumerable<bool> values) =>
        new(ProjectionDocumentValueKind.BoolList, values?.ToArray() ?? []);

    public static ProjectionDocumentValue FromDateTime(DateTime value) =>
        new(ProjectionDocumentValueKind.DateTime, value.Kind == DateTimeKind.Utc ? value : value.ToUniversalTime());

    public static ProjectionDocumentValue FromDateTimes(IEnumerable<DateTime> values) =>
        new(
            ProjectionDocumentValueKind.DateTimeList,
            values?.Select(x => x.Kind == DateTimeKind.Utc ? x : x.ToUniversalTime()).ToArray() ?? []);
}

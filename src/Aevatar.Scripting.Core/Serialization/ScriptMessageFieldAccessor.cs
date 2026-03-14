using Google.Protobuf;
using Google.Protobuf.Reflection;
using System.Globalization;

namespace Aevatar.Scripting.Core.Serialization;

public static class ScriptMessageFieldAccessor
{
    public static string? ReadScalarString(IMessage message, string fieldName)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (string.IsNullOrWhiteSpace(fieldName))
            return null;

        var field = message.Descriptor.Fields.InDeclarationOrder().FirstOrDefault(candidate =>
            string.Equals(candidate.Name, fieldName, StringComparison.Ordinal));
        if (field == null || field.IsMap || field.IsRepeated || field.FieldType == FieldType.Message)
            return null;

        var raw = field.Accessor.GetValue(message);
        if (raw == null)
            return null;

        return raw switch
        {
            string text => text,
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
            _ => raw.ToString(),
        };
    }
}

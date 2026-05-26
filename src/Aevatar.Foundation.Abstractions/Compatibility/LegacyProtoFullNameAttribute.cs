namespace Aevatar.Foundation.Abstractions.Compatibility;

[AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
public sealed class LegacyProtoFullNameAttribute : Attribute
{
    public LegacyProtoFullNameAttribute(string fullName)
    {
        FullName = fullName ?? throw new ArgumentNullException(nameof(fullName));
    }

    public string FullName { get; }
}

namespace Aevatar.Foundation.Abstractions.Compatibility;

[AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
public sealed class LegacyClrTypeNameAttribute : Attribute
{
    public LegacyClrTypeNameAttribute(string fullName)
    {
        FullName = fullName ?? throw new ArgumentNullException(nameof(fullName));
    }

    public string FullName { get; }
}

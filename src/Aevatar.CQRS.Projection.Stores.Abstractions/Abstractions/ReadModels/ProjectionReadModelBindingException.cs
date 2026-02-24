namespace Aevatar.CQRS.Projection.Stores.Abstractions;

public sealed class ProjectionReadModelBindingException : InvalidOperationException
{
    public ProjectionReadModelBindingException(
        Type readModelType,
        string bindingKey,
        string bindingValue,
        string reason)
        : base(BuildMessage(readModelType, bindingKey, bindingValue, reason))
    {
        ReadModelType = readModelType;
        BindingKey = bindingKey;
        BindingValue = bindingValue;
        Reason = reason;
    }

    public Type ReadModelType { get; }

    public string BindingKey { get; }

    public string BindingValue { get; }

    public string Reason { get; }

    private static string BuildMessage(
        Type readModelType,
        string bindingKey,
        string bindingValue,
        string reason) =>
        $"Read-model binding is invalid for '{readModelType.FullName}'. " +
        $"key='{bindingKey}', value='{bindingValue}', reason='{reason}'.";
}

namespace Aevatar.AI.Application.CodexExecution;

public sealed class ManagedCodexOpaqueSecret
{
    private readonly string _value;

    public ManagedCodexOpaqueSecret(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        _value = value;
    }

    public T Use<T>(Func<string, T> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        return action(_value);
    }

    public Task<T> UseAsync<T>(Func<string, Task<T>> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        return action(_value);
    }

    public override string ToString() => "***REDACTED***";
}

namespace Aevatar.CQRS.Sagas.Abstractions.Runtime;

public sealed class SagaConcurrencyException : InvalidOperationException
{
    public SagaConcurrencyException(string message)
        : base(message)
    {
    }
}

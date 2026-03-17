using Aevatar.Tools.Cli.Studio.Domain.Models;

namespace Aevatar.Tools.Cli.Studio.Application.Exceptions;

public sealed class StudioValidationException : Exception
{
    public StudioValidationException(string message, IReadOnlyList<ValidationFinding> findings)
        : base(message)
    {
        Findings = findings;
    }

    public IReadOnlyList<ValidationFinding> Findings { get; }
}

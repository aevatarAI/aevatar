using Aevatar.Tools.Cli.Studio.Domain.Models;

namespace Aevatar.Tools.Cli.Studio.Host.Contracts;

public sealed record ValidationErrorResponse(
    string Message,
    IReadOnlyList<ValidationFinding> Findings);

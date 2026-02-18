namespace Aevatar.CQRS.Core.Abstractions.Queries;

public interface IExecutionTemplateQueryService
{
    IReadOnlyList<string> ListTemplates();
}

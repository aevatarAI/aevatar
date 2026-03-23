using Aevatar.Studio.Application;
using Aevatar.Studio.Infrastructure.ScopeResolution;
using Aevatar.Studio.Application.Studio.Contracts;
using Aevatar.Studio.Application.Studio.Services;
using Microsoft.AspNetCore.Mvc;

namespace Aevatar.Studio.Hosting.Controllers;

[ApiController]
[Route("api/editor")]
public sealed class EditorController : ControllerBase
{
    private readonly WorkflowEditorService _editorService;

    public EditorController(WorkflowEditorService editorService)
    {
        _editorService = editorService;
    }

    [HttpPost("parse-yaml")]
    public ActionResult<ParseYamlResponse> ParseYaml([FromBody] ParseYamlRequest request) =>
        Ok(_editorService.ParseYaml(request));

    [HttpPost("serialize-yaml")]
    public ActionResult<SerializeYamlResponse> SerializeYaml([FromBody] SerializeYamlRequest request) =>
        Ok(_editorService.SerializeYaml(request));

    [HttpPost("validate")]
    public ActionResult<ValidateWorkflowResponse> Validate([FromBody] ValidateWorkflowRequest request) =>
        Ok(_editorService.Validate(request));

    [HttpPost("normalize")]
    public ActionResult<NormalizeWorkflowResponse> Normalize([FromBody] NormalizeWorkflowRequest request) =>
        Ok(_editorService.Normalize(request));

    [HttpPost("diff")]
    public ActionResult<DiffWorkflowResponse> Diff([FromBody] DiffWorkflowRequest request) =>
        Ok(_editorService.Diff(request));
}

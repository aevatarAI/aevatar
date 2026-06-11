using Aevatar.Studio.Application;
using Aevatar.Studio.Application.Studio.Abstractions;
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
    public ActionResult<ParseYamlHttpResponse> ParseYaml([FromBody] ParseYamlRequest request) =>
        Ok(ParseYamlHttpResponse.FromApplicationResponse(_editorService.ParseYaml(request)));

    [HttpPost("serialize-yaml")]
    public ActionResult<SerializeYamlResponse> SerializeYaml([FromBody] SerializeYamlHttpRequest request) =>
        Ok(_editorService.SerializeYaml(request.ToApplicationRequest()));

    [HttpPost("validate")]
    public ActionResult<ValidateWorkflowResponse> Validate([FromBody] ValidateWorkflowHttpRequest request) =>
        Ok(_editorService.Validate(request.ToApplicationRequest()));

    [HttpPost("normalize")]
    public ActionResult<NormalizeWorkflowResponse> Normalize([FromBody] NormalizeWorkflowHttpRequest request) =>
        Ok(_editorService.Normalize(request.ToApplicationRequest()));

    [HttpPost("diff")]
    public ActionResult<DiffWorkflowResponse> Diff([FromBody] DiffWorkflowRequest request) =>
        Ok(_editorService.Diff(request));
}

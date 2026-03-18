using Aevatar.AI.Abstractions;
using Aevatar.GAgentService.Abstractions;
using Google.Protobuf.Reflection;

namespace Aevatar.GAgentService.Hosting.Demo;

internal static class GAgentServiceDemoDefinitions
{
    public static IReadOnlyList<GAgentServiceDemoDefinition> All { get; } =
    [
        new(
            "demo-uppercase",
            "Demo Uppercase",
            "builtin-v1",
            "demo_uppercase",
            """
            name: demo_uppercase
            description: Convert the incoming prompt to uppercase.
            steps:
              - id: uppercase
                type: transform
                parameters:
                  op: uppercase
            """),
        new(
            "demo-count-lines",
            "Demo Count Lines",
            "builtin-v1",
            "demo_count_lines",
            """
            name: demo_count_lines
            description: Count non-empty lines from the incoming prompt.
            steps:
              - id: count_lines
                type: transform
                parameters:
                  op: count
            """),
        new(
            "demo-take-first-three",
            "Demo Take First Three",
            "builtin-v1",
            "demo_take_first_three",
            """
            name: demo_take_first_three
            description: Return the first three lines from the incoming prompt.
            steps:
              - id: take_first_three
                type: transform
                parameters:
                  op: take
                  n: "3"
            """),
    ];

    public static ServiceEndpointSpec CreateEndpointSpec(GAgentServiceDemoDefinition definition) =>
        new()
        {
            EndpointId = "chat",
            DisplayName = definition.DisplayName,
            Kind = ServiceEndpointKind.Chat,
            RequestTypeUrl = GetTypeUrl(ChatRequestEvent.Descriptor),
            ResponseTypeUrl = GetTypeUrl(ChatResponseEvent.Descriptor),
            Description = definition.WorkflowName,
        };

    private static string GetTypeUrl(MessageDescriptor descriptor) =>
        $"type.googleapis.com/{descriptor.FullName}";
}

internal sealed record GAgentServiceDemoDefinition(
    string ServiceId,
    string DisplayName,
    string RevisionId,
    string WorkflowName,
    string WorkflowYaml);

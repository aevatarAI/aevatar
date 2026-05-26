namespace Aevatar.ChatRouting.Core;

public sealed class ChatRoutingOptions
{
    public ChatRoutingDefaultsOptions Defaults { get; init; } = new();
}

public sealed class ChatRoutingDefaultsOptions
{
    public string FallbackModel { get; init; } = string.Empty;

    public string DefaultForwardToModelToolSetName { get; set; } = string.Empty;
}

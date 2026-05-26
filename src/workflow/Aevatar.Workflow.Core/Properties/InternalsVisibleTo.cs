using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("Aevatar.Workflow.Core.Tests")]
[assembly: InternalsVisibleTo("Aevatar.Integration.Tests")]
[assembly: InternalsVisibleTo("Aevatar.Workflow.Host.Api.Tests")]
// The scheduled-agent package owns the social_media `twitter_publish` workflow module (issue
// #216): its implementation needs to read the per-request NyxID api-key + Lark delivery target
// out of the workflow execution context's items / request-metadata bag to call NyxID proxies
// and surface the result back to the originating chat. Those bag accessors are internal by
// design (they are not a free-form public extension surface), so the package is granted
// internals-visible the same way the workflow test projects are. The legacy
// Aevatar.GAgents.ChannelRuntime.Tests assembly name is preserved post split (the test project
// kept its name for stability across the issue #263 channelruntime split).
[assembly: InternalsVisibleTo("Aevatar.GAgents.Scheduled")]
[assembly: InternalsVisibleTo("Aevatar.GAgents.ChannelRuntime.Tests")]

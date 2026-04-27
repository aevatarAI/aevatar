using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("Aevatar.Workflow.Core.Tests")]
[assembly: InternalsVisibleTo("Aevatar.Integration.Tests")]
[assembly: InternalsVisibleTo("Aevatar.Workflow.Host.Api.Tests")]
// ChannelRuntime owns the social_media `twitter_publish` workflow module (issue #216): its
// implementation needs to read the per-request NyxID api-key + Lark delivery target out of the
// workflow execution context's items / request-metadata bag to call NyxID proxies and surface
// the result back to the originating chat. Those bag accessors are internal by design (they are
// not a free-form public extension surface), so the channel-runtime module is granted
// internals-visible the same way the workflow test projects are.
[assembly: InternalsVisibleTo("Aevatar.GAgents.ChannelRuntime")]
[assembly: InternalsVisibleTo("Aevatar.GAgents.ChannelRuntime.Tests")]

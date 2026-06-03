using System.Runtime.CompilerServices;
using Aevatar.CQRS.Projection.Core.Orchestration;

[assembly: InternalsVisibleTo("Aevatar.CQRS.Projection.Core.Tests")]
[assembly: TypeForwardedTo(typeof(CommittedStateEventEnvelope))]

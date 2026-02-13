// ─────────────────────────────────────────────────────────────
// InternalVisibility - assembly internals visibility.
// Grants Aevatar.Runtime and Aevatar.Core.Tests access to internal members.
// ─────────────────────────────────────────────────────────────

using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("Aevatar.Runtime")]
[assembly: InternalsVisibleTo("Aevatar.Runtime.Orleans")]
[assembly: InternalsVisibleTo("Aevatar.Core.Tests")]

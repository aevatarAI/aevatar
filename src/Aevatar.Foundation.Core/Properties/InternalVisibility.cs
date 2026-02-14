// ─────────────────────────────────────────────────────────────
// InternalVisibility - assembly internals visibility.
// Grants Aevatar.Foundation.Runtime and Aevatar.Foundation.Core.Tests access to internal members.
// ─────────────────────────────────────────────────────────────

using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("Aevatar.Foundation.Runtime")]
[assembly: InternalsVisibleTo("Aevatar.Foundation.Core.Tests")]

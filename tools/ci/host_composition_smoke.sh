#!/usr/bin/env bash
#
# Host composition smoke — fails fast if Mainnet.Host.Api can't pass
# ValidateOnBuild.
#
# Aevatar.Mainnet.Host.Api enables ValidateOnBuild + ValidateScopes so the
# container reports missing registrations at startup rather than the first
# request. That's the right policy for a production host, but it means a
# missing IProjectionDocumentReader<TDoc, string> (or any other transitive
# Singleton dependency) is invisible to module-level unit tests and only
# surfaces when the pod starts.
#
# This smoke runs MainnetHostCompositionTests, which actually builds the
# full Aevatar.Mainnet.Host.Api container with InMemory providers. Cheap
# (~30s build + 1s test) so it runs on every PR.

set -euo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd -- "${SCRIPT_DIR}/../.." && pwd)"
cd "${REPO_ROOT}"

dotnet test test/Aevatar.Hosting.Tests/Aevatar.Hosting.Tests.csproj \
  --nologo \
  --tl:off \
  -p:UseSharedCompilation=false \
  -p:NuGetAudit=false \
  --filter "FullyQualifiedName~MainnetHostCompositionTests"

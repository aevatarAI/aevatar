#!/usr/bin/env bash
#
# Studio projection read-model registration guard.
#
# Every concrete IProjectionReadModel<TDoc> implementation under
# src/Aevatar.Studio.Projection/ReadModels/ must be registered in
# src/Aevatar.Studio.Hosting/StudioProjectionReadModelServiceCollectionExtensions.cs
# with all three of:
#
#   - RegisterElasticsearch<TDoc>(...)
#   - RegisterInMemory<TDoc>(...)
#   - HasDocumentReaderForProvider<TDoc>(...)   (inside HasAllStudioDocumentReaders)
#
# Without these, IProjectionDocumentReader<TDoc, string> stays unresolved in DI,
# and Mainnet host startup fails ValidateOnBuild as soon as a query port that
# consumes it is pulled into the container. The Studio module's own unit tests
# stub IProjectionDocumentReader<TDoc, string> directly, so they pass, and
# the gap only surfaces at host startup — which is exactly what bit ADR-0017
# step 6+9 (StudioTeamCurrentStateDocument).
#
# Failure mode this prevents: a new TDoc + projector + query port lands in
# Studio.Projection without the hosting registration; module tests stay green;
# Mainnet host can't start.

set -euo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd -- "${SCRIPT_DIR}/../.." && pwd)"
cd "${REPO_ROOT}"

readmodel_dir="src/Aevatar.Studio.Projection/ReadModels"
hosting_file="src/Aevatar.Studio.Hosting/StudioProjectionReadModelServiceCollectionExtensions.cs"

if [ ! -d "${readmodel_dir}" ] || [ ! -f "${hosting_file}" ]; then
  echo "studio_projection_readmodel_registration_guard: skipping (paths absent)"
  exit 0
fi

# Extract every TDoc named in `IProjectionReadModel<TDoc>` under the read-model
# directory. The `<` after `IProjectionReadModel` excludes interface-member
# references like `string IProjectionReadModel.ActorId`. Generic doc-comment
# references use `{TDoc}` not `<TDoc>` so they don't match either.
docs="$(
  rg --type cs -No --no-filename --pcre2 \
     '\bIProjectionReadModel<\s*(\w+)\s*>' \
     "${readmodel_dir}" \
     -r '$1' \
  | sort -u || true
)"

if [ -z "${docs}" ]; then
  echo "studio_projection_readmodel_registration_guard: no IProjectionReadModel<TDoc> declarations found (skipping)"
  exit 0
fi

missing=""
while IFS= read -r doc; do
  [ -z "${doc}" ] && continue
  for pattern in \
    "RegisterElasticsearch<${doc}>" \
    "RegisterInMemory<${doc}>" \
    "HasDocumentReaderForProvider<${doc}>" \
  ; do
    if ! rg -q --fixed-strings "${pattern}" "${hosting_file}"; then
      missing+="  ${doc}: ${pattern}(...) is not called"$'\n'
    fi
  done
done <<< "${docs}"

if [ -n "${missing}" ]; then
  echo "studio_projection_readmodel_registration_guard: incomplete read-model registration in ${hosting_file}:"
  echo
  echo "${missing}"
  echo "Each Studio IProjectionReadModel<TDoc> must be wired through all three:"
  echo "  - RegisterElasticsearch<TDoc>(services, configuration)"
  echo "  - RegisterInMemory<TDoc>(services)"
  echo "  - HasDocumentReaderForProvider<TDoc>(services, providerKind)   (inside HasAllStudioDocumentReaders)"
  echo
  echo "Without these, IProjectionDocumentReader<TDoc, string> stays unresolved and"
  echo "Mainnet host startup fails ValidateOnBuild as soon as a query port pulls it in."
  exit 1
fi

echo "studio_projection_readmodel_registration_guard: ok"

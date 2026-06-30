#!/usr/bin/env bash

set -euo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd -- "${SCRIPT_DIR}/../../.." && pwd)"
ARCHITECTURE_GUARDS="${REPO_ROOT}/tools/ci/architecture_guards.sh"
TMP_DIR="$(mktemp -d)"
trap 'rm -rf "${TMP_DIR}"' EXIT

fixture_dir="${TMP_DIR}/src"
mkdir -p "${fixture_dir}"

cat > "${fixture_dir}/QueryPortWithBusinessListAsync.cs" <<'CS'
using System.Threading.Tasks;

public sealed class QueryPortWithBusinessListAsync
{
    private readonly IServiceRunQueryPort _serviceRunQueryPort;
    private readonly IProjectionDocumentReader<FooDocument, string> _documentReader;

    public QueryPortWithBusinessListAsync(
        IServiceRunQueryPort serviceRunQueryPort,
        IProjectionDocumentReader<FooDocument, string> documentReader)
    {
        _serviceRunQueryPort = serviceRunQueryPort;
        _documentReader = documentReader;
    }

    public Task RunAsync()
    {
        _documentReader.QueryAsync("foo");
        return _serviceRunQueryPort.ListAsync();
    }
}
CS

cat > "${fixture_dir}/ReaderFieldWithListAsync.cs" <<'CS'
using System.Threading.Tasks;

public sealed class ReaderFieldWithListAsync
{
    private readonly IProjectionDocumentReader<FooDocument, string> _documentReader;

    public ReaderFieldWithListAsync(IProjectionDocumentReader<FooDocument, string> documentReader)
    {
        _documentReader = documentReader;
    }

    public Task RunAsync()
    {
        return _documentReader.ListAsync();
    }
}
CS

cat > "${fixture_dir}/ReaderParameterWithListAsync.cs" <<'CS'
using System.Threading.Tasks;

public sealed class ReaderParameterWithListAsync
{
    public Task RunAsync(IProjectionDocumentReader<FooDocument, string> documentReader)
    {
        return documentReader.ListAsync();
    }
}
CS

report="$(
  AEVATAR_ARCHITECTURE_GUARDS_RUN_PROJECTION_DOCUMENT_READER_SCAN_ONLY=1 \
    bash "${ARCHITECTURE_GUARDS}" "${fixture_dir}"
)"

if ! printf '%s\n' "${report}" | rg -q "ReaderFieldWithListAsync.cs:14:.*_documentReader\\.ListAsync\\("; then
  echo "Expected guard to report ListAsync on an IProjectionDocumentReader field."
  printf '%s\n' "${report}"
  exit 1
fi

if ! printf '%s\n' "${report}" | rg -q "ReaderParameterWithListAsync.cs:7:.*documentReader\\.ListAsync\\("; then
  echo "Expected guard to report ListAsync on an IProjectionDocumentReader parameter."
  printf '%s\n' "${report}"
  exit 1
fi

if printf '%s\n' "${report}" | rg -q "_serviceRunQueryPort\\.ListAsync\\("; then
  echo "Guard must not report business query port ListAsync calls."
  printf '%s\n' "${report}"
  exit 1
fi

if rg -q "reader/document field|dot prefix pattern|reader/document/projection" "${ARCHITECTURE_GUARDS}"; then
  echo "Architecture guard comment should describe typed IProjectionDocumentReader variables, not name/path heuristics."
  exit 1
fi

echo "projection document reader ListAsync guard tests passed"

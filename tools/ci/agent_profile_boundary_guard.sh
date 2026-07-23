#!/usr/bin/env bash

set -euo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd -- "${SCRIPT_DIR}/../.." && pwd)"

strip_csharp_comments_and_inactive_code() {
  perl -0777 -pe '
    s{^[ \t]*\#if[ \t]+false\b.*?^[ \t]*\#endif\b[^\r\n]*(?:\r?\n|\z)}{}gms;
    s{
      (\$*""".*?"""|@\$?"(?:""|[^"])*"|\$?@"(?:""|[^"])*"|\$?"(?:\\.|[^"\\])*")
      |/\*.*?\*/
      |//[^\r\n]*
    }{defined $1 ? $1 : ""}gsex;
  '
}

strip_csharp_strings() {
  perl -0777 -pe '
    s{\$*""".*?"""|@\$?"(?:""|[^"])*"|\$?@"(?:""|[^"])*"|\$?"(?:\\.|[^"\\])*"}{""}gse;
  '
}

list_source_files() {
  local path=""
  for path in "$@"; do
    if [[ -f "${path}" ]]; then
      printf '%s\n' "${path}"
    elif [[ -d "${path}" ]]; then
      rg --files "${path}" -g '*.cs' -g '*.proto'
    fi
  done
  return 0
}

scan_prepared_code() {
  local pattern="$1"
  shift
  local file=""
  local hits=""
  local line=""

  while IFS= read -r file; do
    [[ -n "${file}" ]] || continue
    hits="$(
      strip_csharp_comments_and_inactive_code < "${file}" \
        | strip_csharp_strings \
        | rg -n -P "${pattern}" \
        || true
    )"
    if [[ -z "${hits}" ]]; then
      continue
    fi
    while IFS= read -r line; do
      printf '%s:%s\n' "${file}" "${line}"
    done <<< "${hits}"
  done < <(list_source_files "$@")
  return 0
}

scan_application_latest_contracts() {
  local application_root="$1"
  local file=""
  local prepared=""
  local hits=""
  local line=""

  while IFS= read -r file; do
    [[ -n "${file}" ]] || continue
    prepared="$(strip_csharp_comments_and_inactive_code < "${file}")"

    hits="$(printf '%s\n' "${prepared}" | rg -n -i -P '"latest"' || true)"
    while IFS= read -r line; do
      [[ -n "${line}" ]] && printf '%s:%s\n' "${file}" "${line}"
    done <<< "${hits}"

    hits="$(
      printf '%s\n' "${prepared}" \
        | strip_csharp_strings \
        | rg -n -i -P '\blatest(?:_?version)?\b' \
        || true
    )"
    while IFS= read -r line; do
      [[ -n "${line}" ]] && printf '%s:%s\n' "${file}" "${line}"
    done <<< "${hits}"
  done < <(list_source_files "${application_root}")
  return 0
}

extract_csharp_method_body() {
  local method_name="$1"
  METHOD_NAME="${method_name}" perl -0777 -ne '
    my $method = $ENV{"METHOD_NAME"};
    if ($_ !~ /\b\Q$method\E\s*\([^)]*\)\s*(=>|\{)/sg) {
      exit 1;
    }
    my $delimiter = $1;
    my $body_start = pos($_);
    if ($delimiter eq "=>") {
      my $remainder = substr($_, $body_start);
      if ($remainder =~ /\A(.*?);/s) {
        print $1;
        exit 0;
      }
      exit 1;
    }

    my $open = $body_start - 1;
    my $depth = 0;
    for (my $index = $open; $index < length($_); $index++) {
      my $character = substr($_, $index, 1);
      if ($character eq "{") {
        $depth++;
      } elsif ($character eq "}") {
        $depth--;
        if ($depth == 0) {
          print substr($_, $open + 1, $index - $open - 1);
          exit 0;
        }
      }
    }
    exit 1;
  '
}

extract_csharp_top_level_code() {
  perl -0777 -ne '
    my $depth = 0;
    for (my $index = 0; $index < length($_); $index++) {
      my $character = substr($_, $index, 1);
      if ($character eq "{") {
        $depth++;
        print "\n" if $depth == 1;
      } elsif ($character eq "}") {
        exit 1 if $depth == 0;
        $depth--;
        print "\n" if $depth == 0;
      } elsif ($depth == 0 || $character eq "\n") {
        print $character;
      }
    }
    exit($depth == 0 ? 0 : 1);
  '
}

count_pattern_matches() {
  local pattern="$1"
  local matches=""
  matches="$(rg -o -P "${pattern}" || true)"
  if [[ -z "${matches}" ]]; then
    echo 0
    return 0
  fi
  printf '%s\n' "${matches}" | wc -l | tr -d '[:space:]'
}

extract_tool_schema() {
  local tool_file="$1"
  awk '
    !capture && /public[[:space:]]+string[[:space:]]+ParametersSchema[[:space:]]*=>[[:space:]]*"""/ {
      capture = 1
      next
    }
    capture && /^[[:space:]]*""";[[:space:]]*$/ { exit }
    capture { print }
  ' "${tool_file}"
}

report_violation() {
  local evidence="$1"
  local message="$2"
  [[ -n "${evidence}" ]] || return 0
  printf '%s\n%s\n' "${evidence}" "${message}"
  violations=$((violations + 1))
}

run_guard() (
  local scan_root="$1"
  cd "${scan_root}"

  local profile_provider_file="src/platform/Aevatar.GAgentService.Projection/AgentProfiles/AgentProfileDocumentMetadataProviders.cs"
  local audit_translator_file="src/platform/Aevatar.GAgentService.Projection/Audit/AgentProfileAuditCommittedEventTranslators.cs"
  local application_root="src/platform/Aevatar.GAgentService.Application/AgentProfiles"
  local hosting_root="src/platform/Aevatar.GAgentService.Hosting/AgentProfiles"
  local tool_root="src/Aevatar.AI.ToolProviders.AgentCatalog/AgentProfiles"
  local exact_adapter_file="src/Aevatar.AI.ToolProviders.Ornn/AgentProfiles/OrnnExactAgentProfileSkillResolver.cs"
  local ornn_client_file="src/Aevatar.AI.ToolProviders.Ornn/OrnnSkillClient.cs"
  local tool_file="${tool_root}/AgentProfilesTool.cs"

  local core_root="src/platform/Aevatar.GAgentService.Core/AgentProfiles"
  local projection_root="src/platform/Aevatar.GAgentService.Projection/AgentProfiles"
  local profile_semantic_roots=(
    "${core_root}" "${application_root}" "${projection_root}" "${audit_translator_file}")
  local profile_surface_roots=(
    "${profile_semantic_roots[@]}" "${hosting_root}" "${tool_root}")
  local core_projection_roots=(
    "${core_root}" "${projection_root}" "${audit_translator_file}")
  local query_ingress_roots=(
    "${application_root}" "${projection_root}" "${hosting_root}" "${tool_root}")
  local required_paths=(
    "${profile_semantic_roots[@]}" "${hosting_root}" "${tool_root}"
    "${exact_adapter_file}" "${ornn_client_file}" "${tool_file}")

  local violations=0
  local required_path="" file="" line_number="" content="" hits=""

  for required_path in "${required_paths[@]}"; do
    if [[ ! -e "${required_path}" ]]; then
      echo "Agent Profile boundary guard input is missing: ${required_path}"
      violations=$((violations + 1))
    fi
  done
  if (( violations > 0 )); then
    return 1
  fi

  hits=""
  while IFS= read -r file; do
    while IFS=: read -r line_number content; do
      [[ -n "${line_number}" ]] || continue
      if [[ "${file}" == "${profile_provider_file}" ]] &&
         [[ "${content}" =~ ^[[:space:]]*public[[:space:]]+DocumentIndexMetadata[[:space:]]+Metadata[[:space:]]+\{[[:space:]]+get\;[[:space:]]+\}[[:space:]]+=[[:space:]]+new\([[:space:]]*$ ]]; then
        continue
      fi
      hits+="${file}:${line_number}:${content}"$'\n'
    done < <(
      strip_csharp_comments_and_inactive_code < "${file}" \
        | rg -n '\b(Metadata|Headers|Items|AsyncLocal)\b' \
        || true
    )
  done < <(list_source_files "${profile_semantic_roots[@]}")
  hits="${hits%$'\n'}"
  report_violation "${hits}" \
    "Agent Profile Core/Application/Projection code must keep stable semantics typed; only the exact projection document Metadata contract is allowed."

  hits="$(
    scan_prepared_code \
      'static[[:space:]][^;\n]*(CurrentAgentProfile|CurrentProfile|AgentProfileCurrent|ProfileContext)' \
      "${profile_semantic_roots[@]}"
  )"
  report_violation "${hits}" \
    "Static current Agent Profile context is forbidden. Profile authority must remain actor/read-model owned."

  hits="$(
    scan_prepared_code \
      '(?i)\b(?:private|protected|internal|public)\s+static\s+(?:readonly\s+)?(?=[^();\n]*(?:AgentProfile|Profile(?:Identity|Context|State|Binding)))[^();\n]+\s+[A-Za-z_][A-Za-z0-9_]*\s*(?:=|;)' \
      "${profile_semantic_roots[@]}"
  )"
  report_violation "${hits}" \
    "Static typed Agent Profile state is forbidden. Profile authority must remain actor/read-model owned."

  hits="$(
    scan_prepared_code \
      '(?i)private\s+(?:static\s+)?(?:readonly\s+)?(?:(?:(?:Concurrent|Immutable|Sorted|Frozen)?Dictionary|I(?:ReadOnly)?Dictionary|HashSet|Queue)<[^>\n]*(?:AgentProfile|Profile|Binding)[^>\n]*>\s+[A-Za-z_][A-Za-z0-9_]*|(?:(?:Concurrent|Immutable|Sorted|Frozen)?Dictionary|I(?:ReadOnly)?Dictionary|HashSet|Queue)<[^>\n]+>\s+[A-Za-z_][A-Za-z0-9_]*(?:profile|binding)[A-Za-z0-9_]*)\s*(?:=|;)' \
      "${profile_surface_roots[@]}"
  )"
  report_violation "${hits}" \
    "Private service-level collections must not hold Agent Profile or binding facts. Use actor-owned state or read models."

  hits="$(
    scan_prepared_code \
      'Aevatar\.AI\.ToolProviders\.Ornn|Ornn(SkillClient|RemoteSkillFetcher|Search|SkillFetcher)|System\.Net\.Http|Microsoft\.AspNetCore|Http(Client|Request|Response)|IRemoteSkillFetcher|SearchSkillsAsync|GetSkillJsonAsync' \
      "${core_projection_roots[@]}"
  )"
  report_violation "${hits}" \
    "Agent Profile Core/Projection must not depend on Ornn, HTTP, remote fetchers, or skill-search/name lookup paths."

  hits="$(
    scan_prepared_code \
      '(?i)\b(GetSkillJsonAsync|SearchSkillsAsync|IRemoteSkillFetcher|nameOrId|idOrName|inlineSkill)\b' \
      "${application_root}"
  )"
  hits+="$(scan_application_latest_contracts "${application_root}")"
  report_violation "${hits}" \
    "Agent Profile Application accepts exact skill references only; name/latest/inline lookup and name-capable fetchers are forbidden."

  local client_code="" detail_body="" json_body=""
  local detail_signature='(?s)\binternal\s+Task<OrnnExactSkillReadResult<OrnnExactSkillDetail>>\s+GetExactSkillDetailAsync\s*\([^)]*\)\s*(?:=>|\{)'
  local json_signature='(?s)\binternal\s+Task<OrnnExactSkillReadResult<OrnnSkillJson>>\s+GetExactSkillJsonAsync\s*\([^)]*\)\s*(?:=>|\{)'
  local detail_pattern='(?s)\bGetExactAsync<OrnnExactSkillDetail>\s*\(\s*accessToken\s*,\s*\$"/api/v1/skills/\{Uri\.EscapeDataString\(guid\)\}\?version=\{Uri\.EscapeDataString\(literalVersion\)\}"'
  local json_pattern='(?s)\bGetExactAsync<OrnnSkillJson>\s*\(\s*accessToken\s*,\s*\$"/api/v1/skills/\{Uri\.EscapeDataString\(guid\)\}/json\?version=\{Uri\.EscapeDataString\(literalVersion\)\}"'
  client_code="$(strip_csharp_comments_and_inactive_code < "${ornn_client_file}")"
  if ! printf '%s\n' "${client_code}" | rg -q -U -P "${detail_signature}" ||
     ! detail_body="$(printf '%s\n' "${client_code}" | extract_csharp_method_body GetExactSkillDetailAsync)" ||
     ! printf '%s\n' "${detail_body}" | rg -q -U -P "${detail_pattern}"; then
    echo "${ornn_client_file}"
    echo "The executable exact Ornn Profile detail read must use the literal ?version= endpoint form."
    violations=$((violations + 1))
  fi
  if ! printf '%s\n' "${client_code}" | rg -q -U -P "${json_signature}" ||
     ! json_body="$(printf '%s\n' "${client_code}" | extract_csharp_method_body GetExactSkillJsonAsync)" ||
     ! printf '%s\n' "${json_body}" | rg -q -U -P "${json_pattern}"; then
    echo "${ornn_client_file}"
    echo "The executable exact Ornn Profile JSON read must use the literal ?version= endpoint form."
    violations=$((violations + 1))
  fi

  local adapter_code=""
  local resolver_body="" resolver_structure="" resolver_top_level=""
  local detail_call_count=0 json_call_count=0
  adapter_code="$(strip_csharp_comments_and_inactive_code < "${exact_adapter_file}")"
  if ! resolver_body="$(printf '%s\n' "${adapter_code}" | extract_csharp_method_body ResolveAsync)"; then
    echo "${exact_adapter_file}"
    echo "The exact Ornn Profile adapter must declare an executable ResolveAsync body."
    violations=$((violations + 1))
  elif ! resolver_structure="$(printf '%s\n' "${resolver_body}" | strip_csharp_strings)" ||
       ! resolver_top_level="$(printf '%s\n' "${resolver_structure}" | extract_csharp_top_level_code)"; then
    echo "${exact_adapter_file}"
    echo "The exact Ornn Profile adapter must declare a structurally valid ResolveAsync body."
    violations=$((violations + 1))
  else
    detail_call_count="$(
      printf '%s\n' "${resolver_top_level}" \
        | count_pattern_matches '\b_client\.GetExactSkillDetailAsync\s*\('
    )"
    json_call_count="$(
      printf '%s\n' "${resolver_top_level}" \
        | count_pattern_matches '\b_client\.GetExactSkillJsonAsync\s*\('
    )"
    if (( detail_call_count != 1 )); then
      echo "${exact_adapter_file}"
      echo "ResolveAsync must execute the exact-version detail read exactly once as a direct top-level call."
      violations=$((violations + 1))
    elif ! printf '%s\n' "${resolver_top_level}" \
      | rg -q -P '\bvar[[:space:]]+detailRead[[:space:]]*=[[:space:]]*await[[:space:]]+_client\.GetExactSkillDetailAsync\('; then
      echo "${exact_adapter_file}"
      echo "ResolveAsync must execute the exact-version detail read."
      violations=$((violations + 1))
    fi
    if (( json_call_count != 1 )); then
      echo "${exact_adapter_file}"
      echo "ResolveAsync must execute the exact-version JSON read exactly once as a direct top-level call."
      violations=$((violations + 1))
    elif ! printf '%s\n' "${resolver_top_level}" \
      | rg -q -P '\bvar[[:space:]]+jsonRead[[:space:]]*=[[:space:]]*await[[:space:]]+_client\.GetExactSkillJsonAsync\('; then
      echo "${exact_adapter_file}"
      echo "ResolveAsync must execute the exact-version JSON read."
      violations=$((violations + 1))
    fi
    if printf '%s\n' "${resolver_structure}" | rg -q -P '\bif\s*\(\s*false\s*\)'; then
      echo "${exact_adapter_file}"
      echo "ResolveAsync exact reads must not be hidden in dead conditional code."
      violations=$((violations + 1))
    fi
  fi

  hits="$(
    printf '%s\n' "${adapter_code}" \
      | rg -n -P '\b(GetSkillJsonAsync|SearchSkillsAsync|GetSkillSetAsync|IRemoteSkillFetcher)\b' \
      || true
  )"
  [[ -z "${hits}" ]] || hits="${exact_adapter_file}:${hits}"
  report_violation "${hits}" \
    "The exact Ornn Profile adapter must not call name-capable, search, set, or generic remote fetch paths."

  hits="$(
    scan_prepared_code \
      'ProjectionActivation|IProjectionPortActivationService|IProjectionPortReleaseService|\b(?:I?ActorRuntime|I?EventStore|FileEventStore|ReplayAsync|EventReplay)\b|event[[:space:]_-]*replay|RebuildAsync|PrimeAsync|Priming|Ensure[A-Za-z0-9_]*Projection|Attach[A-Za-z0-9_]*Projection|ActivateAsync' \
      "${query_ingress_roots[@]}"
  )"
  report_violation "${hits}" \
    "Agent Profile query and ingress surfaces must read materialized models only; activation, runtime, event-store, replay, and priming APIs are forbidden."

  local tool_schema="" schema_keys="" schema_key="" normalized_key=""
  local forbidden_schema_keys=""
  tool_schema="$(extract_tool_schema "${tool_file}")"
  if [[ -z "${tool_schema}" ]]; then
    echo "${tool_file}"
    echo "The agent_profiles ParametersSchema JSON block was not found."
    violations=$((violations + 1))
  elif ! printf '%s\n' "${tool_schema}" | jq -e . >/dev/null 2>&1; then
    echo "${tool_file}"
    echo "The agent_profiles ParametersSchema block must be valid JSON."
    violations=$((violations + 1))
  else
    schema_keys="$(
      printf '%s\n' "${tool_schema}" \
        | jq -r '.. | objects | select(has("properties")) | .properties | keys[]'
    )"
    while IFS= read -r schema_key; do
      [[ -n "${schema_key}" ]] || continue
      normalized_key="$(
        printf '%s' "${schema_key}" \
          | tr '[:upper:]' '[:lower:]' \
          | tr -cd '[:alnum:]'
      )"
      case "${normalized_key}" in
        ownerid|ownersubject|ownersubjectid|subjectid|scopeid|profileid|systemauthority|systemauthorityid|platformid|apikey|cookie|password|authorization|secret|clientsecret|oauthcode|*sealed*|*credential*|*token*|*bearer*)
          forbidden_schema_keys+="${schema_key}"$'\n'
          ;;
      esac
    done <<< "${schema_keys}"
    forbidden_schema_keys="${forbidden_schema_keys%$'\n'}"
    if [[ -n "${forbidden_schema_keys}" ]]; then
      report_violation "${tool_file}: forbidden schema properties:
${forbidden_schema_keys}" \
        "The agent_profiles tool schema must not accept owner subjects/ids, scope/Profile ids, system authority, sealed content, or credentials."
    fi
  fi

  if (( violations > 0 )); then
    return 1
  fi

  echo "Agent Profile Phase 1 boundary guards passed."
)

write_lines() {
  local file="$1"
  shift
  printf '%s\n' "$@" > "${file}"
}

write_tool_schema() {
  local file="$1"
  local extra_property="$2"
  local extra_line=""
  if [[ -n "${extra_property}" ]]; then
    printf -v extra_line \
      ',\n        "%s": { "type": "string" }' \
      "${extra_property}"
  fi
  cat > "${file}" <<CS
public sealed class AgentProfilesTool
{
    public string ParametersSchema => """
    {
      "type": "object",
      "properties": {
        "action": { "type": "string" },
        "owner_handle": { "type": "string" },
        "skill": { "type": "object", "properties": {
          "skill_guid": { "type": "string" }${extra_line}
        }}
      }
    }
    """;
}
CS
}

write_valid_fixture() {
  local root="$1"
  local core="${root}/src/platform/Aevatar.GAgentService.Core/AgentProfiles"
  local application="${root}/src/platform/Aevatar.GAgentService.Application/AgentProfiles"
  local projection="${root}/src/platform/Aevatar.GAgentService.Projection/AgentProfiles"
  local audit="${root}/src/platform/Aevatar.GAgentService.Projection/Audit"
  local hosting="${root}/src/platform/Aevatar.GAgentService.Hosting/AgentProfiles"
  local ornn="${root}/src/Aevatar.AI.ToolProviders.Ornn"
  local tool="${root}/src/Aevatar.AI.ToolProviders.AgentCatalog/AgentProfiles"
  mkdir -p "${core}" "${application}" "${projection}" "${audit}" "${hosting}" "${ornn}/AgentProfiles" "${tool}"

  write_lines "${core}/AgentProfile.cs" 'public sealed class AgentProfileDefinition { }'
  write_lines "${application}/AgentProfileService.cs" \
    'public sealed class AgentProfileService {' \
    '  // The latest wording in a comment is harmless.' \
    '  private readonly Dictionary<string, string> _cache = new();' \
    '  private readonly IReadOnlyDictionary<string, string> _timestamps = new Dictionary<string, string>();' \
    '  private readonly ImmutableDictionary<string, string> _lookup = ImmutableDictionary<string, string>.Empty;' \
    '  private string latestReadModelTimestamp = "";' \
    '  public string DescribeForbiddenInfrastructure() => "IActorRuntime FileEventStore ReplayAsync EventReplay";' \
    '  public string Describe() => "The latest committed Profile is shown to the caller."; }'
  write_lines "${projection}/AgentProfileDocumentMetadataProviders.cs" \
    'public sealed class AgentProfileOwnerDocumentMetadataProvider {' \
    '  public DocumentIndexMetadata Metadata { get; } = new(' \
    '    "agent-profile-management"); }'
  write_lines "${projection}/AgentProfileProjector.cs" 'public sealed class AgentProfileProjector { }'
  write_lines "${audit}/AgentProfileAuditCommittedEventTranslators.cs" \
    'public sealed class AgentProfileAuditCommittedEventTranslator { }'
  write_lines "${hosting}/AgentProfileEndpoints.cs" 'public sealed class AgentProfileEndpoints { }'
  write_lines "${tool}/AgentProfilesToolSource.cs" 'public sealed class AgentProfilesToolSource { }'
  write_lines "${ornn}/OrnnSkillClient.cs" \
    'public sealed class OrnnSkillClient {' \
    '  internal Task<OrnnExactSkillReadResult<OrnnExactSkillDetail>> GetExactSkillDetailAsync(string accessToken, string guid, string literalVersion, CancellationToken ct = default) =>' \
    '    GetExactAsync<OrnnExactSkillDetail>(accessToken,' \
    '      $"/api/v1/skills/{Uri.EscapeDataString(guid)}?version={Uri.EscapeDataString(literalVersion)}", guid, ct);' \
    '  internal Task<OrnnExactSkillReadResult<OrnnSkillJson>> GetExactSkillJsonAsync(string accessToken, string guid, string literalVersion, CancellationToken ct = default) =>' \
    '    GetExactAsync<OrnnSkillJson>(accessToken,' \
    '      $"/api/v1/skills/{Uri.EscapeDataString(guid)}/json?version={Uri.EscapeDataString(literalVersion)}", guid, ct); }'
  write_lines "${ornn}/AgentProfiles/OrnnExactAgentProfileSkillResolver.cs" \
    'public sealed class OrnnExactAgentProfileSkillResolver {' \
    '  public async Task<ExactOrnnSkillResolutionResult> ResolveAsync(CancellationToken ct = default) {' \
    '    var detailRead = await _client.GetExactSkillDetailAsync(token, guid, version, ct);' \
    '    var jsonRead = await _client.GetExactSkillJsonAsync(token, guid, version, ct);' \
    '    return Combine(detailRead, jsonRead); } }'
  write_tool_schema "${tool}/AgentProfilesTool.cs" ""
}

run_self_tests() {
  local temp_dir=""
  temp_dir="$(mktemp -d)"
  trap 'rm -rf "${temp_dir}"' RETURN

  local base="${temp_dir}/base" case_root="" output="" failures=0

  write_valid_fixture "${base}"

  expect_pass() {
    local label="$1"
    local root="$2"
    if ! output="$(run_guard "${root}" 2>&1)"; then
      echo "agent_profile_boundary_guard self-test expected PASS: ${label}" >&2
      echo "${output}" >&2
      failures=$((failures + 1))
    fi
  }

  expect_fail() {
    local label="$1"
    local root="$2"
    local expected_output="${3:-}"
    if output="$(run_guard "${root}" 2>&1)"; then
      echo "agent_profile_boundary_guard self-test expected FAIL: ${label}" >&2
      echo "${output}" >&2
      failures=$((failures + 1))
    elif [[ -n "${expected_output}" && "${output}" != *"${expected_output}"* ]]; then
      echo "agent_profile_boundary_guard self-test missed expected diagnostic: ${label}" >&2
      echo "Expected: ${expected_output}" >&2
      echo "${output}" >&2
      failures=$((failures + 1))
    fi
  }

  fresh_case() {
    local label="$1"
    case_root="${temp_dir}/${label}"
    cp -R "${base}" "${case_root}"
  }

  expect_pass "legal metadata, unrelated collections, and harmless semantic strings" "${base}"

  fresh_case "typed-static-profile-state"
  printf '%s\n' 'private static AgentProfileIdentity? _current;' \
    >> "${case_root}/src/platform/Aevatar.GAgentService.Application/AgentProfiles/AgentProfileService.cs"
  expect_fail "typed static Profile state" "${case_root}" "Static typed Agent Profile state"

  fresh_case "profile-fact-collection"
  printf '%s\n' 'private readonly Dictionary<string, string> _profileBindings = new();' \
    >> "${case_root}/src/platform/Aevatar.GAgentService.Application/AgentProfiles/AgentProfileService.cs"
  expect_fail "Profile fact collection" "${case_root}" "Private service-level collections"

  local collection_label="" collection_probe=""
  while IFS='|' read -r collection_label collection_probe; do
    fresh_case "profile-fact-collection-${collection_label}"
    printf '%s\n' "${collection_probe}" \
      >> "${case_root}/src/platform/Aevatar.GAgentService.Application/AgentProfiles/AgentProfileService.cs"
    expect_fail "Profile fact collection ${collection_label}" "${case_root}" \
      "Private service-level collections"
  done <<'CASES'
idictionary|private readonly IDictionary<string, string> _profileBindings;
readonly-dictionary|private readonly IReadOnlyDictionary<string, AgentProfileIdentity> _index;
immutable-dictionary|private readonly ImmutableDictionary<string, string> _profileFacts;
CASES

  local provider_file="src/platform/Aevatar.GAgentService.Projection/AgentProfiles/AgentProfileDocumentMetadataProviders.cs"
  local provider_probe="" provider_label=""
  while IFS='|' read -r provider_label provider_probe; do
    fresh_case "provider-${provider_label}"
    printf '%s\n' "${provider_probe}" >> "${case_root}/${provider_file}"
    expect_fail "metadata provider ${provider_label}" "${case_root}" "only the exact projection document Metadata contract"
  done <<'CASES'
async-local|private static readonly AsyncLocal<string?> Current = new();
items|private readonly Dictionary<string, string> Items = new();
headers|private string Headers => "forbidden";
unrelated-metadata|public string Metadata => "forbidden";
CASES

  local remote_file="" remote_probe="" remote_label=""
  while IFS='|' read -r remote_label remote_file remote_probe; do
    fresh_case "remote-${remote_label}"
    printf '%s\n' "${remote_probe}" >> "${case_root}/${remote_file}"
    expect_fail "Core/Projection remote dependency ${remote_label}" "${case_root}" "Core/Projection must not depend"
  done <<'CASES'
http|src/platform/Aevatar.GAgentService.Core/AgentProfiles/AgentProfile.cs|private readonly HttpClient _client;
ornn|src/platform/Aevatar.GAgentService.Projection/AgentProfiles/AgentProfileProjector.cs|private readonly OrnnSkillClient _client;
name-fetch|src/platform/Aevatar.GAgentService.Projection/AgentProfiles/AgentProfileProjector.cs|private Task GetSkillJsonAsync();
search|src/platform/Aevatar.GAgentService.Core/AgentProfiles/AgentProfile.cs|private Task SearchSkillsAsync();
CASES

  local application_file="src/platform/Aevatar.GAgentService.Application/AgentProfiles/AgentProfileService.cs"
  local application_probe="" application_label=""
  while IFS='|' read -r application_label application_probe; do
    fresh_case "application-${application_label}"
    printf '%s\n' "${application_probe}" >> "${case_root}/${application_file}"
    expect_fail "Application exact-reference ${application_label}" "${case_root}" "Application accepts exact skill references only"
  done <<'CASES'
name-or-id|private string nameOrId = "skill";
latest-identifier-bare|private string latest = "1.0";
latest-identifier-camel|private string latestVersion = "1.0";
latest-identifier-snake|private string latest_version = "1.0";
latest-contract|private string Version = "latest";
inline-skill|private string inlineSkill = "content";
name-fetch|private Task GetSkillJsonAsync();
CASES

  fresh_case "client-block-bodied"
  write_lines "${case_root}/src/Aevatar.AI.ToolProviders.Ornn/OrnnSkillClient.cs" \
    'public sealed class OrnnSkillClient {' \
    '  internal Task<OrnnExactSkillReadResult<OrnnExactSkillDetail>> GetExactSkillDetailAsync(string accessToken, string guid, string literalVersion, CancellationToken ct = default) {' \
    '    return GetExactAsync<OrnnExactSkillDetail>(accessToken,' \
    '      $"/api/v1/skills/{Uri.EscapeDataString(guid)}?version={Uri.EscapeDataString(literalVersion)}", guid, ct); }' \
    '  internal Task<OrnnExactSkillReadResult<OrnnSkillJson>> GetExactSkillJsonAsync(string accessToken, string guid, string literalVersion, CancellationToken ct = default) {' \
    '    return GetExactAsync<OrnnSkillJson>(accessToken,' \
    '      $"/api/v1/skills/{Uri.EscapeDataString(guid)}/json?version={Uri.EscapeDataString(literalVersion)}", guid, ct); } }'
  expect_pass "block-bodied exact client methods" "${case_root}"

  fresh_case "client-comment-decoy"
  write_lines "${case_root}/src/Aevatar.AI.ToolProviders.Ornn/OrnnSkillClient.cs" \
    'public sealed class OrnnSkillClient {' \
    '  // GetExactSkillDetailAsync => $"/api/v1/skills/{Uri.EscapeDataString(guid)}?version={Uri.EscapeDataString(literalVersion)}"' \
    '  // GetExactSkillJsonAsync => $"/api/v1/skills/{Uri.EscapeDataString(guid)}/json?version={Uri.EscapeDataString(literalVersion)}"' \
    '}'
  expect_fail "exact client comment decoys" "${case_root}" "executable exact Ornn Profile"

  fresh_case "client-inactive-decoy"
  {
    printf '%s\n' '#if false'
    sed -n '/internal Task<OrnnExactSkillReadResult/,/^}/p' \
      "${base}/src/Aevatar.AI.ToolProviders.Ornn/OrnnSkillClient.cs"
    printf '%s\n' '#endif' 'public sealed class OrnnSkillClient { }'
  } > "${case_root}/src/Aevatar.AI.ToolProviders.Ornn/OrnnSkillClient.cs"
  expect_fail "exact client inactive decoys" "${case_root}" "executable exact Ornn Profile"

  fresh_case "adapter-comment-decoy"
  write_lines "${case_root}/src/Aevatar.AI.ToolProviders.Ornn/AgentProfiles/OrnnExactAgentProfileSkillResolver.cs" \
    'public sealed class OrnnExactAgentProfileSkillResolver {' \
    '  public async Task<ExactOrnnSkillResolutionResult> ResolveAsync(CancellationToken ct = default) {' \
    '    // var detailRead = await _client.GetExactSkillDetailAsync(token, guid, version, ct);' \
    '    // var jsonRead = await _client.GetExactSkillJsonAsync(token, guid, version, ct);' \
    '    return default!; } }'
  expect_fail "exact adapter comment decoys" "${case_root}" "ResolveAsync must execute"

  fresh_case "adapter-dead-helper-decoy"
  write_lines "${case_root}/src/Aevatar.AI.ToolProviders.Ornn/AgentProfiles/OrnnExactAgentProfileSkillResolver.cs" \
    'public sealed class OrnnExactAgentProfileSkillResolver {' \
    '  public async Task<ExactOrnnSkillResolutionResult> ResolveAsync(CancellationToken ct = default) { return default!; }' \
    '  private async Task DeadDecoy(CancellationToken ct) {' \
    '    var detailRead = await _client.GetExactSkillDetailAsync(token, guid, version, ct);' \
    '    var jsonRead = await _client.GetExactSkillJsonAsync(token, guid, version, ct); } }'
  expect_fail "exact adapter dead helper decoys" "${case_root}" "ResolveAsync must execute"

  fresh_case "adapter-dead-local-function-decoy"
  write_lines "${case_root}/src/Aevatar.AI.ToolProviders.Ornn/AgentProfiles/OrnnExactAgentProfileSkillResolver.cs" \
    'public sealed class OrnnExactAgentProfileSkillResolver {' \
    '  public async Task<ExactOrnnSkillResolutionResult> ResolveAsync(CancellationToken ct = default) {' \
    '    async Task DeadDecoy(CancellationToken localCt) {' \
    '      var detailRead = await _client.GetExactSkillDetailAsync(token, guid, version, localCt);' \
    '      var jsonRead = await _client.GetExactSkillJsonAsync(token, guid, version, localCt); }' \
    '    return default!; } }'
  expect_fail "exact adapter dead local-function decoys" "${case_root}" "ResolveAsync must execute"

  fresh_case "adapter-duplicate-top-level-calls"
  write_lines "${case_root}/src/Aevatar.AI.ToolProviders.Ornn/AgentProfiles/OrnnExactAgentProfileSkillResolver.cs" \
    'public sealed class OrnnExactAgentProfileSkillResolver {' \
    '  public async Task<ExactOrnnSkillResolutionResult> ResolveAsync(CancellationToken ct = default) {' \
    '    var detailRead = await _client.GetExactSkillDetailAsync(token, guid, version, ct);' \
    '    var duplicateDetailRead = await _client.GetExactSkillDetailAsync(token, guid, version, ct);' \
    '    var jsonRead = await _client.GetExactSkillJsonAsync(token, guid, version, ct);' \
    '    return Combine(detailRead, jsonRead); } }'
  expect_fail "exact adapter duplicate top-level calls" "${case_root}" "exactly once"

  fresh_case "adapter-name-capable"
  printf '%s\n' 'private Task GetSkillJsonAsync() => _client.GetSkillJsonAsync("name");' \
    >> "${case_root}/src/Aevatar.AI.ToolProviders.Ornn/AgentProfiles/OrnnExactAgentProfileSkillResolver.cs"
  expect_fail "exact adapter name-capable fetch" "${case_root}" "must not call name-capable"

  local ingress_label="" ingress_file="" ingress_probe=""
  while IFS='|' read -r ingress_label ingress_file ingress_probe; do
    fresh_case "ingress-${ingress_label}"
    printf '%s\n' "${ingress_probe}" >> "${case_root}/${ingress_file}"
    expect_fail "query ingress ${ingress_label}" "${case_root}" "query and ingress surfaces"
  done <<'CASES'
lookup-service|src/platform/Aevatar.GAgentService.Application/AgentProfiles/AgentProfileLookupService.cs|public sealed class AgentProfileLookupService { private readonly IActorRuntime _runtime; }
actor-runtime|src/platform/Aevatar.GAgentService.Application/AgentProfiles/AgentProfileLookupService.cs|public sealed class AgentProfileLookupService { private readonly ActorRuntime _runtime; }
projection|src/platform/Aevatar.GAgentService.Projection/AgentProfiles/AgentProfileProjector.cs|private Task ProjectionActivation();
host-endpoint|src/platform/Aevatar.GAgentService.Hosting/AgentProfiles/AgentProfileEndpoints.cs|private Task PrimeAsync();
tool|src/Aevatar.AI.ToolProviders.AgentCatalog/AgentProfiles/AgentProfilesToolSource.cs|private readonly IEventStore _eventStore;
file-event-store|src/Aevatar.AI.ToolProviders.AgentCatalog/AgentProfiles/AgentProfilesToolSource.cs|private readonly FileEventStore _eventStore;
event-store|src/Aevatar.AI.ToolProviders.AgentCatalog/AgentProfiles/AgentProfilesToolSource.cs|private readonly EventStore _eventStore;
replay-async|src/platform/Aevatar.GAgentService.Hosting/AgentProfiles/AgentProfileEndpoints.cs|private Task ReplayAsync();
event-replay|src/platform/Aevatar.GAgentService.Hosting/AgentProfiles/AgentProfileEndpoints.cs|private readonly EventReplay _eventReplay;
CASES

  local schema_alias=""
  for schema_alias in scopeId subjectId systemAuthority owner_id ownerSubject \
    profileId platformId sealed_content sealedContent credential accessToken \
    api_key apiKey cookie password authorization secret clientSecret oauth_code oauthCode; do
    fresh_case "schema-${schema_alias}"
    write_tool_schema \
      "${case_root}/src/Aevatar.AI.ToolProviders.AgentCatalog/AgentProfiles/AgentProfilesTool.cs" \
      "${schema_alias}"
    expect_fail "tool schema alias ${schema_alias}" "${case_root}" "forbidden schema properties"
  done

  if (( failures > 0 )); then
    return 1
  fi

  echo "Agent Profile Phase 1 boundary guard self-tests passed."
}

case "${1:-}" in
  "")
    run_guard "${REPO_ROOT}"
    ;;
  --self-test)
    (( $# == 1 )) || { echo "Usage: $0 --self-test" >&2; exit 2; }
    run_self_tests
    ;;
  --scan-root)
    (( $# == 2 )) || { echo "Usage: $0 --scan-root <repository-root>" >&2; exit 2; }
    run_guard "$2"
    ;;
  *)
    echo "Usage: $0 [--self-test|--scan-root <repository-root>]" >&2
    exit 2
    ;;
esac

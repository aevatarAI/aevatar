#!/usr/bin/env bash

set -euo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd -- "${SCRIPT_DIR}/../.." && pwd)"
AUTHORITY_CHECKER_PROJECT="${SCRIPT_DIR}/Aevatar.AgentProfileBoundaryGuard.Tool/Aevatar.AgentProfileBoundaryGuard.Tool.csproj"
AUTHORITY_CHECKER_DLL="${SCRIPT_DIR}/Aevatar.AgentProfileBoundaryGuard.Tool/bin/Debug/net10.0/Aevatar.AgentProfileBoundaryGuard.Tool.dll"
AUTHORITY_CHECKER_BUILT=false

build_authority_syntax_checker() {
  if [[ "${AUTHORITY_CHECKER_BUILT}" == "true" ]]; then
    return 0
  fi
  if ! dotnet build "${AUTHORITY_CHECKER_PROJECT}" \
       --nologo --no-restore --configuration Debug --verbosity quiet; then
    echo "Agent Profile authority syntax checker build failed." >&2
    return 2
  fi
  if [[ ! -f "${AUTHORITY_CHECKER_DLL}" ]]; then
    echo "Agent Profile authority syntax checker output is missing." >&2
    return 2
  fi
  AUTHORITY_CHECKER_BUILT=true
}

run_authority_syntax_checker() {
  local -a arguments=(check)
  local root=""
  for root in "$@"; do
    arguments+=(--scan-root "${root}")
  done
  dotnet "${AUTHORITY_CHECKER_DLL}" "${arguments[@]}"
}

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

scan_prepared_multiline_code() {
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
        | rg -n -U -P "${pattern}" \
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

scan_normalized_identifiers() {
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
        | NORMALIZED_PATTERN="${pattern}" perl -ne '
            my $pattern = qr/$ENV{"NORMALIZED_PATTERN"}/;
            while (/\b[A-Za-z_][A-Za-z0-9_]*\b/g) {
              my $identifier = $&;
              (my $normalized = lc $identifier) =~ s/_//g;
              if ($normalized =~ $pattern) {
                print "$.:$_";
                last;
              }
            }
          '
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

extract_csharp_executable_code() {
  perl -0777 -ne '
    exit 1 if /(?m)^[ \t]*#/;
    s{^[ \t]*(?:async[ \t]+)?[A-Za-z_][A-Za-z0-9_.?]*(?:[ \t]*<[^;\r\n{}]+>)?[ \t]+[A-Za-z_][A-Za-z0-9_]*[ \t]*\([^;\r\n{}]*\)[ \t]*=>[^;\r\n]*;}{}gm;
    my $depth = 0;
    my @live_try = ();
    my $statement_prefix = "";
    my $terminated = 0;
    for (my $index = 0; $index < length($_); $index++) {
      my $character = substr($_, $index, 1);
      if ($character eq "{") {
        my $is_live_try = 0;
        if ($depth == 0) {
          my $prefix = substr($_, 0, $index);
          $is_live_try = 1 if $prefix =~ /(?:\A|[;}\r\n])\s*try\s*\z/s;
        }
        push @live_try, $is_live_try;
        $depth++;
        $statement_prefix = "";
        print "\n" unless $terminated;
      } elsif ($character eq "}") {
        exit 1 if $depth == 0;
        $depth--;
        pop @live_try;
        $statement_prefix = "";
        print "\n" unless $terminated;
      } elsif (!$terminated &&
               ($depth == 0 || ($depth == 1 && $live_try[0]) || $character eq "\n")) {
        my $is_executable = $depth == 0 || ($depth == 1 && $live_try[0]);
        if ($is_executable && $statement_prefix !~ /\S/) {
          my $remaining = substr($_, $index);
          # Label reachability is intentionally outside this structural scanner.
          exit 1 if $remaining =~ /\A@?[A-Za-z_][A-Za-z0-9_]*\s*:(?!:)/;
          exit 1 if $remaining =~ /\Agoto\b/;
          if ($remaining =~ /\A(?:return|throw)\b/) {
            $terminated = 1;
            next;
          }
        }
        print $character;
        if ($is_executable) {
          $statement_prefix = $character eq ";"
            ? ""
            : $statement_prefix . $character;
        } elsif ($character eq "\n") {
          $statement_prefix .= $character;
        }
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

check_actor_delivery_provenance_contracts() {
  local envelope_proto_file="$1"
  local delivery_semantics_file="$2"
  local publish_context_file="$3"
  local local_dispatch_file="$4"
  local orleans_dispatch_file="$5"
  local actor_invariants_file="$6"
  local failures=0 code="" body="" stamp_body=""

  code="$(strip_csharp_comments_and_inactive_code < "${envelope_proto_file}")"
  if ! printf '%s\n' "${code}" | rg -q -U -P \
       '\bmessage\s+EnvelopeDeliveryProvenance\s*\{[^}]*\bstring\s+authenticated_actor_id\s*=\s*1\s*;' ||
     ! printf '%s\n' "${code}" | rg -q -U -P \
       '\bmessage\s+EnvelopeRuntime\s*\{[^}]*\bEnvelopeDeliveryProvenance\s+delivery_provenance\s*=\s*9\s*;'; then
    echo "${envelope_proto_file}:EnvelopeRuntime.delivery_provenance"
    echo "EventEnvelope runtime must expose typed delivery provenance for the authenticated Actor origin."
    failures=$((failures + 1))
  fi

  code="$(
    strip_csharp_comments_and_inactive_code < "${delivery_semantics_file}" | strip_csharp_strings
  )"
  if ! body="$(printf '%s\n' "${code}" | extract_csharp_method_body CloneForRawDispatch)" ||
     ! printf '%s\n' "${body}" | rg -q -U -P \
       '\A\s*ArgumentNullException\.ThrowIfNull\s*\(\s*envelope\s*\)\s*;\s*var\s+(?<admitted>[A-Za-z_][A-Za-z0-9_]*)\s*=\s*envelope\.Clone\s*\(\s*\)\s*;\s*if\s*\(\s*\k<admitted>\.Runtime\s+is\s+not\s+null\s*\)\s*\k<admitted>\.Runtime\.DeliveryProvenance\s*=\s*null\s*;\s*return\s+\k<admitted>\s*;\s*\z'; then
    echo "${delivery_semantics_file}:CloneForRawDispatch"
    echo "Raw dispatch provenance must clear and return the same admitted envelope clone."
    failures=$((failures + 1))
  fi
  if ! stamp_body="$(printf '%s\n' "${code}" | extract_csharp_method_body StampAuthenticatedActorOrigin)" ||
     ! printf '%s\n' "${stamp_body}" | rg -q -U -P \
       '\bDeliveryProvenance\s*=\s*new\s+EnvelopeDeliveryProvenance\b[\s\S]*\bAuthenticatedActorId\s*=\s*actorId\s*,'; then
    echo "${delivery_semantics_file}:StampAuthenticatedActorOrigin"
    echo "Actor-bound provenance must stamp only the bound Actor id."
    failures=$((failures + 1))
  fi

  local dispatch_file="" admitted_delivery_pattern=""
  for dispatch_file in "${local_dispatch_file}" "${orleans_dispatch_file}"; do
    code="$(
      strip_csharp_comments_and_inactive_code < "${dispatch_file}" | strip_csharp_strings
    )"
    if [[ "${dispatch_file}" == "${local_dispatch_file}" ]]; then
      admitted_delivery_pattern='\bAcceptDispatchedEnvelope\s*\(\s*EnvelopeDeliveryProvenanceSemantics\.CloneForRawDispatch\s*\(\s*envelope\s*\)\s*\)'
    else
      admitted_delivery_pattern='\bProduceAsync\s*\(\s*EnvelopeDeliveryProvenanceSemantics\.CloneForRawDispatch\s*\(\s*envelope\s*\)\s*,'
    fi
    if ! body="$(printf '%s\n' "${code}" | extract_csharp_method_body DispatchAsync)" ||
       ! printf '%s\n' "${body}" | rg -q -U -P "${admitted_delivery_pattern}"; then
      echo "${dispatch_file}:DispatchAsync"
      echo "Raw Local and Orleans dispatch admission must deliver the cleared clone instead of the caller envelope."
      failures=$((failures + 1))
    fi
  done

  code="$(
    strip_csharp_comments_and_inactive_code < "${publish_context_file}" | strip_csharp_strings
  )"
  if ! body="$(printf '%s\n' "${code}" | extract_csharp_method_body ApplyOutboundPublishContext)" ||
     ! printf '%s\n' "${body}" | rg -q -U -P \
       '\bpropagationPolicy\.Apply\s*\([^;]+;[\s\S]*\bEnvelopeDeliveryProvenanceSemantics\.StampAuthenticatedActorOrigin\s*\(\s*envelope\s*,\s*sourceActorId\s*\)'; then
    echo "${publish_context_file}:ApplyOutboundPublishContext"
    echo "Actor-bound publishing must stamp runtime-authenticated origin after propagation."
    failures=$((failures + 1))
  fi

  code="$(
    strip_csharp_comments_and_inactive_code < "${actor_invariants_file}" | strip_csharp_strings
  )"
  if ! body="$(printf '%s\n' "${code}" | extract_csharp_method_body RequireProtocolPublisher)" ||
     ! printf '%s\n' "${body}" | rg -q -U -P \
       '\bif\s*\(\s*!string\.Equals\s*\(\s*envelope\?\.Route\?\.PublisherActorId\s*,\s*expected\s*,\s*StringComparison\.Ordinal\s*\)\s*\|\|\s*!string\.Equals\s*\(\s*envelope\?\.Runtime\?\.DeliveryProvenance\?\.AuthenticatedActorId\s*,\s*expected\s*,\s*StringComparison\.Ordinal\s*\)\s*\)\s*\{\s*throw\s+Error\s*\([^;]+;'; then
    echo "${actor_invariants_file}:RequireProtocolPublisher.fail-closed"
    echo "Agent Profile protocol authority must fail closed when either required Actor identity mismatches."
    failures=$((failures + 1))
  fi

  (( failures == 0 ))
}

run_guard() (
  local scan_root="$1"
  local check_delivery_provenance="${2:-true}"
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
  local profile_actor_file="${core_root}/AgentProfileGAgent.cs"
  local namespace_actor_file="${core_root}/AgentProfileNamespaceGAgent.cs"
  local actor_invariants_file="${core_root}/AgentProfileActorInvariants.cs"
  local retention_policy_file="${core_root}/AgentProfileOperationRetentionPolicy.cs"
  local envelope_proto_file="src/Aevatar.Foundation.Abstractions/agent_messages.proto"
  local delivery_semantics_file="src/Aevatar.Foundation.Runtime/Propagation/EnvelopeDeliveryProvenanceSemantics.cs"
  local publish_context_file="src/Aevatar.Foundation.Runtime/Propagation/EnvelopePublishContextHelpers.cs"
  local local_dispatch_file="src/Aevatar.Foundation.Runtime.Implementations.Local/Actors/LocalActorDispatchPort.cs"
  local orleans_dispatch_file="src/Aevatar.Foundation.Runtime.Implementations.Orleans/Actors/OrleansActorDispatchPort.cs"
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
    "${exact_adapter_file}" "${ornn_client_file}" "${tool_file}"
    "${profile_actor_file}" "${namespace_actor_file}" "${actor_invariants_file}"
    "${retention_policy_file}" "${envelope_proto_file}" "${delivery_semantics_file}"
    "${publish_context_file}" "${local_dispatch_file}" "${orleans_dispatch_file}")

  local violations=0
  local required_path="" file="" line_number="" content="" hits="" normalized_hits=""

  for required_path in "${required_paths[@]}"; do
    if [[ ! -e "${required_path}" ]]; then
      echo "Agent Profile boundary guard input is missing: ${required_path}"
      violations=$((violations + 1))
    fi
  done
  if (( violations > 0 )); then
    return 1
  fi

  local provenance_output=""
  if [[ "${check_delivery_provenance}" == "true" ]] &&
     ! provenance_output="$(check_actor_delivery_provenance_contracts \
       "${envelope_proto_file}" \
       "${delivery_semantics_file}" \
       "${publish_context_file}" \
       "${local_dispatch_file}" \
       "${orleans_dispatch_file}" \
       "${actor_invariants_file}")"; then
    printf '%s\n' "${provenance_output}"
    violations=$((violations + 1))
  fi

  local handler_label="" handler_file="" handler_method=""
  local actor_code="" handler_body="" proof_body="" compact_body=""
  while IFS='|' read -r handler_label handler_file handler_method; do
    actor_code="$(
      strip_csharp_comments_and_inactive_code < "${handler_file}" \
        | strip_csharp_strings
    )"
    if ! handler_body="$(
        printf '%s\n' "${actor_code}" | extract_csharp_method_body "${handler_method}"
      )" ||
       ! printf '%s\n' "${handler_body}" | rg -q -U -P \
         '\A\s*ArgumentNullException\.ThrowIfNull\s*\(\s*command\s*\)\s*;\s*RequireIngressProof\s*\(\s*command\s*\)\s*;\s*var\s+operation\s*=\s*AgentProfileActorInvariants\.RequireOperation\s*\(\s*command\.Operation\s*\)\s*;'; then
      echo "${handler_file}:${handler_method}"
      echo "Every external Agent Profile command handler must verify its ingress proof before parsing the operation."
      violations=$((violations + 1))
    fi
  done <<CASES
create|${namespace_actor_file}|HandleCreateAsync
update|${profile_actor_file}|HandleUpdateDraftAsync
upsert|${profile_actor_file}|HandleUpsertSkillBindingAsync
remove|${profile_actor_file}|HandleRemoveSkillBindingAsync
publish|${profile_actor_file}|HandlePublishAsync
CASES

  while IFS='|' read -r handler_label handler_file; do
    actor_code="$(
      strip_csharp_comments_and_inactive_code < "${handler_file}" \
        | strip_csharp_strings
    )"
    if ! proof_body="$(
        printf '%s\n' "${actor_code}" | extract_csharp_method_body RequireIngressProof
      )" ||
       ! printf '%s\n' "${proof_body}" | rg -q -U -P \
         '\A\s*if\s*\(\s*!\s*_ingressProofVerifier\.Verify\s*\(\s*Id\s*,\s*command\s*\)\s*\)'; then
      echo "${handler_file}:RequireIngressProof"
      echo "Each Agent Profile Actor must delegate ingress proof validation to IAgentProfileIngressProofVerifier."
      violations=$((violations + 1))
    fi
  done <<CASES
profile|${profile_actor_file}
namespace|${namespace_actor_file}
CASES

  local retention_member=""
  while IFS='|' read -r handler_label handler_file retention_member; do
    actor_code="$(
      strip_csharp_comments_and_inactive_code < "${handler_file}" \
        | strip_csharp_strings
    )"
    if ! compact_body="$(
        printf '%s\n' "${actor_code}" | extract_csharp_method_body CompactOperations
      )" ||
       ! printf '%s\n' "${compact_body}" | rg -q -U -P \
         "\\bAgentProfileOperationRetentionPolicy\\.${retention_member}\\b"; then
      echo "${handler_file}:CompactOperations"
      echo "Both Agent Profile Actors must compact operation state through AgentProfileOperationRetentionPolicy."
      violations=$((violations + 1))
    fi
  done <<CASES
profile|${profile_actor_file}|MaxRetainedProfileMutationOperations
namespace|${namespace_actor_file}|MaxRetainedNamespaceTerminalOperations
CASES

  local retention_policy_code=""
  retention_policy_code="$(
    strip_csharp_comments_and_inactive_code < "${retention_policy_file}" \
      | strip_csharp_strings
  )"
  if ! printf '%s\n' "${retention_policy_code}" | rg -q -U -P \
       '\bpublic\s+const\s+int\s+MaxRetainedProfileMutationOperations\s*=\s*256\s*;' ||
     ! printf '%s\n' "${retention_policy_code}" | rg -q -U -P \
       '\bpublic\s+const\s+int\s+MaxRetainedNamespaceTerminalOperations\s*=\s*1_?024\s*;'; then
    echo "${retention_policy_file}"
    echo "AgentProfileOperationRetentionPolicy must declare the exact 256/1024 operation retention bounds."
    violations=$((violations + 1))
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
        | strip_csharp_strings \
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
    scan_prepared_multiline_code \
      '(?i)\b(?:private|protected|internal|public)\s+static\s+(?:readonly\s+)?(?=[^;=({\n]*(?:AgentProfile|Profile(?:Identity|Context|State|Binding)))[^;=({\n]+\s+[A-Za-z_][A-Za-z0-9_]*\s*(?:=|;|=>|\{\s*(?:(?:public|private|protected|internal)\s+)?(?:get|set|init)\s*;)' \
      "${profile_semantic_roots[@]}"
  )"
  report_violation "${hits}" \
    "Static typed Agent Profile state is forbidden. Profile authority must remain actor/read-model owned."

  hits="$(
    scan_prepared_multiline_code \
      '(?is)\bprivate\s+(?:static\s+)?(?:readonly\s+)?(?=[^;={}()]*(?:(?:Concurrent|Immutable|Sorted|Frozen)?Dictionary|I(?:ReadOnly)?Dictionary|HashSet|Queue)\s*<)(?=[^;={}()]*(?:AgentProfile|Profile|Binding))[^;={}()]+\s+[A-Za-z_][A-Za-z0-9_]*\s*(?:=|;)' \
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
      '(?i)\b(GetSkillJsonAsync|SearchSkillsAsync|IRemoteSkillFetcher)\b' \
      "${application_root}"
  )"
  normalized_hits="$(
    scan_normalized_identifiers \
      '(?:nameorid|idorname|inlineskill)' \
      "${application_root}"
  )"
  if [[ -n "${normalized_hits}" ]]; then
    [[ -z "${hits}" ]] || hits+=$'\n'
    hits+="${normalized_hits}"
  fi
  hits+="$(scan_application_latest_contracts "${application_root}")"
  report_violation "${hits}" \
    "Agent Profile Application accepts exact skill references only; name/latest/inline lookup and name-capable fetchers are forbidden."

  local client_code="" detail_body="" json_body=""
  local detail_signature='(?s)\binternal\s+Task<OrnnExactSkillReadResult<OrnnExactSkillDetail>>\s+GetExactSkillDetailAsync\s*\([^)]*\)\s*(?:=>|\{)'
  local json_signature='(?s)\binternal\s+Task<OrnnExactSkillReadResult<OrnnSkillJson>>\s+GetExactSkillJsonAsync\s*\([^)]*\)\s*(?:=>|\{)'
  local live_client_prefix='(?:(?![^;{}]*(?:\b(?:return|if|else|switch|for|foreach|while|do|goto|throw|try|catch|finally)\b|=>)[^;{}]*;)[^;{}]+;)*'
  local detail_pattern='(?s)\A'"${live_client_prefix}"'\s*(?:return\s+)?GetExactAsync<OrnnExactSkillDetail>\s*\(\s*accessToken\s*,\s*\$"/api/v1/skills/\{Uri\.EscapeDataString\(guid\)\}\?version=\{Uri\.EscapeDataString\(literalVersion\)\}"\s*,\s*guid\s*,\s*ct\s*\)\s*;?\s*\z'
  local json_pattern='(?s)\A'"${live_client_prefix}"'\s*(?:return\s+)?GetExactAsync<OrnnSkillJson>\s*\(\s*accessToken\s*,\s*\$"/api/v1/skills/\{Uri\.EscapeDataString\(guid\)\}/json\?version=\{Uri\.EscapeDataString\(literalVersion\)\}"\s*,\s*guid\s*,\s*ct\s*\)\s*;?\s*\z'
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
  local resolver_body="" resolver_structure="" resolver_executable=""
  local detail_call_count=0 json_call_count=0
  adapter_code="$(strip_csharp_comments_and_inactive_code < "${exact_adapter_file}")"
  if ! resolver_body="$(printf '%s\n' "${adapter_code}" | extract_csharp_method_body ResolveAsync)"; then
    echo "${exact_adapter_file}"
    echo "The exact Ornn Profile adapter must declare an executable ResolveAsync body."
    violations=$((violations + 1))
  elif ! resolver_structure="$(printf '%s\n' "${resolver_body}" | strip_csharp_strings)" ||
       ! resolver_executable="$(printf '%s\n' "${resolver_structure}" | extract_csharp_executable_code)"; then
    echo "${exact_adapter_file}"
    echo "The exact Ornn Profile adapter must declare a structurally valid ResolveAsync body."
    violations=$((violations + 1))
  else
    detail_call_count="$(
      printf '%s\n' "${resolver_executable}" \
        | count_pattern_matches '\b_client\.GetExactSkillDetailAsync\s*\('
    )"
    json_call_count="$(
      printf '%s\n' "${resolver_executable}" \
        | count_pattern_matches '\b_client\.GetExactSkillJsonAsync\s*\('
    )"
    if (( detail_call_count != 1 )); then
      echo "${exact_adapter_file}"
      echo "ResolveAsync must execute the exact-version detail read exactly once as a direct executable call."
      violations=$((violations + 1))
    elif ! printf '%s\n' "${resolver_executable}" \
      | rg -q -P '\bvar[[:space:]]+detailRead[[:space:]]*=[[:space:]]*await[[:space:]]+_client\.GetExactSkillDetailAsync\('; then
      echo "${exact_adapter_file}"
      echo "ResolveAsync must execute the exact-version detail read."
      violations=$((violations + 1))
    fi
    if (( json_call_count != 1 )); then
      echo "${exact_adapter_file}"
      echo "ResolveAsync must execute the exact-version JSON read exactly once as a direct executable call."
      violations=$((violations + 1))
    elif ! printf '%s\n' "${resolver_executable}" \
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
      | strip_csharp_strings \
      | rg -n -P '\b(GetSkillJsonAsync|SearchSkillsAsync|GetSkillSetAsync|IRemoteSkillFetcher)\b' \
      || true
  )"
  [[ -z "${hits}" ]] || hits="${exact_adapter_file}:${hits}"
  report_violation "${hits}" \
    "The exact Ornn Profile adapter must not call name-capable, search, set, or generic remote fetch paths."

  hits="$(
    scan_prepared_code \
      'ProjectionActivation|IProjectionPortActivationService|IProjectionPortReleaseService|\b(?:[A-Za-z_][A-Za-z0-9_]*)?(?:ActorRuntime|EventStore)\b|\b(?:ReplayAsync|EventReplay)\b|event[[:space:]_-]*replay|RebuildAsync|PrimeAsync|Priming|Ensure[A-Za-z0-9_]*Projection|Attach[A-Za-z0-9_]*Projection|ActivateAsync' \
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
        *ownerid*|*ownersubject*|*subjectid*|*scopeid*|*profileid*|*systemauthority*|*platformid*|*apikey*|*cookie*|*password*|*authorization*|*secret*|*oauthcode*|*sealed*|*credential*|*token*|*bearer*)
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

run_guard_with_authority_syntax() {
  local root="$1"
  local shell_status=0 syntax_status=0

  run_guard "${root}" || shell_status=$?
  run_authority_syntax_checker "${root}" || syntax_status=$?

  if (( syntax_status == 2 )); then
    return 2
  fi
  if (( shell_status != 0 || syntax_status != 0 )); then
    return 1
  fi
}

write_lines() {
  local file="$1"
  shift
  printf '%s\n' "$@" > "${file}"
}

write_tool_schema() {
  local file="$1"
  shift
  local extra_property="" extra_line="" extra_lines=""
  for extra_property in "$@"; do
    [[ -n "${extra_property}" ]] || continue
    printf -v extra_line \
      ',\n        "%s": { "type": "string" }' \
      "${extra_property}"
    extra_lines+="${extra_line}"
  done
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
          "skill_guid": { "type": "string" }${extra_lines}
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
  local foundation_abstractions="${root}/src/Aevatar.Foundation.Abstractions"
  local runtime_propagation="${root}/src/Aevatar.Foundation.Runtime/Propagation"
  local local_runtime="${root}/src/Aevatar.Foundation.Runtime.Implementations.Local/Actors"
  local orleans_runtime="${root}/src/Aevatar.Foundation.Runtime.Implementations.Orleans/Actors"
  mkdir -p "${core}" "${application}" "${projection}" "${audit}" "${hosting}" \
    "${ornn}/AgentProfiles" "${tool}" "${foundation_abstractions}" \
    "${runtime_propagation}" "${local_runtime}" "${orleans_runtime}"

  write_lines "${core}/AgentProfile.cs" 'public sealed class AgentProfileDefinition { }'
  write_lines "${foundation_abstractions}/agent_messages.proto" \
    'message EnvelopeDeliveryProvenance {' \
    '  string authenticated_actor_id = 1;' \
    '}' \
    'message EnvelopeRuntime {' \
    '  EnvelopeDeliveryProvenance delivery_provenance = 9;' \
    '}'
  write_lines "${runtime_propagation}/EnvelopeDeliveryProvenanceSemantics.cs" \
    'public static class EnvelopeDeliveryProvenanceSemantics {' \
    '  public static EventEnvelope CloneForRawDispatch(EventEnvelope envelope) {' \
    '    ArgumentNullException.ThrowIfNull(envelope);' \
    '    var admitted = envelope.Clone();' \
    '    if (admitted.Runtime is not null)' \
    '      admitted.Runtime.DeliveryProvenance = null;' \
    '    return admitted;' \
    '  }' \
    '  public static void StampAuthenticatedActorOrigin(EventEnvelope envelope, string actorId) {' \
    '    envelope.EnsureRuntime().DeliveryProvenance = new EnvelopeDeliveryProvenance {' \
    '      AuthenticatedActorId = actorId,' \
    '    };' \
    '  }' \
    '}'
  write_lines "${runtime_propagation}/EnvelopePublishContextHelpers.cs" \
    'public static class EnvelopePublishContextHelpers {' \
    '  public static void ApplyOutboundPublishContext(EventEnvelope envelope, string sourceActorId) {' \
    '    propagationPolicy.Apply(envelope, sourceEnvelope);' \
    '    EnvelopeDeliveryProvenanceSemantics.StampAuthenticatedActorOrigin(envelope, sourceActorId);' \
    '  }' \
    '}'
  write_lines "${local_runtime}/LocalActorDispatchPort.cs" \
    'public sealed class LocalActorDispatchPort {' \
    '  public Task DispatchAsync(EventEnvelope envelope) {' \
    '    target.AcceptDispatchedEnvelope(' \
    '      EnvelopeDeliveryProvenanceSemantics.CloneForRawDispatch(envelope));' \
    '    return Task.CompletedTask;' \
    '  }' \
    '}'
  write_lines "${orleans_runtime}/OrleansActorDispatchPort.cs" \
    'public sealed class OrleansActorDispatchPort {' \
    '  public async Task DispatchAsync(EventEnvelope envelope) {' \
    '    await stream.ProduceAsync(' \
    '      EnvelopeDeliveryProvenanceSemantics.CloneForRawDispatch(envelope),' \
    '      ct);' \
    '  }' \
    '}'
  write_lines "${core}/AgentProfileOperationRetentionPolicy.cs" \
    'public static class AgentProfileOperationRetentionPolicy {' \
    '  public const int MaxRetainedProfileMutationOperations = 256;' \
    '  public const int MaxRetainedNamespaceTerminalOperations = 1024;' \
    '}'
  write_lines "${core}/AgentProfileNamespaceGAgent.cs" \
    'public sealed class AgentProfileNamespaceGAgent {' \
    '  public async Task HandleCreateAsync(CreateAgentProfileCommand command) {' \
    '    ArgumentNullException.ThrowIfNull(command);' \
    '    RequireIngressProof(command);' \
    '    var operation = AgentProfileActorInvariants.RequireOperation(command.Operation);' \
    '    await PersistAsync(operation);' \
    '  }' \
    '  public async Task HandleInitializedAsync(AgentProfileInitializedContinuation continuation) {' \
    '    ArgumentNullException.ThrowIfNull(continuation);' \
    '    var profileActorId = AgentProfileActorInvariants.RequireActorId(' \
    '      continuation.ProfileActorId,' \
    '      "profile_actor_id");' \
    '    AgentProfileActorInvariants.RequireProtocolPublisher(ActiveInboundEnvelope, profileActorId);' \
    '    var operation = AgentProfileActorInvariants.RequireOperation(continuation.Operation);' \
    '    await PersistAsync(operation);' \
    '  }' \
    '  public async Task HandleInitializationRejectedAsync(AgentProfileInitializationRejectedContinuation continuation) {' \
    '    ArgumentNullException.ThrowIfNull(continuation);' \
    '    var profileActorId = AgentProfileActorInvariants.RequireActorId(' \
    '      continuation.ProfileActorId,' \
    '      "profile_actor_id");' \
    '    AgentProfileActorInvariants.RequireProtocolPublisher(ActiveInboundEnvelope, profileActorId);' \
    '    var operation = AgentProfileActorInvariants.RequireOperation(continuation.Operation);' \
    '    await PersistAsync(operation);' \
    '  }' \
    '  public async Task HandleObservePublishedSummaryAsync(ObserveAgentProfilePublishedSummaryCommand command) {' \
    '    ArgumentNullException.ThrowIfNull(command);' \
    '    AgentProfileIdentity identity;' \
    '    AgentProfilePublishedSummary summary;' \
    '    try {' \
    '      identity = AgentProfileDeterminism.NormalizeIdentity(command.Identity);' \
    '      summary = AgentProfileDeterminism.NormalizePublishedSummary(command.Summary);' \
    '    } catch (AgentProfileContractValidationException) {' \
    '      throw AgentProfileActorInvariants.Error("PROFILE_PUBLISHED_SUMMARY_MISMATCH", "The published summary identity is invalid.");' \
    '    }' \
    '    var entry = FindProfile(identity.ProfileId);' \
    '    if (entry is null ||' \
    '        entry.Status != AgentProfileProvisioningStatus.Active ||' \
    '        !AgentProfileActorInvariants.SameIdentity(entry.Identity, identity) ||' \
    '        !AgentProfileActorInvariants.SameReference(summary.Reference, entry.Identity.Reference))' \
    '    {' \
    '      throw AgentProfileActorInvariants.Error("PROFILE_PUBLISHED_SUMMARY_MISMATCH", "The published summary does not belong to the mapped Profile.");' \
    '    }' \
    '    AgentProfileActorInvariants.RequireProtocolPublisher(ActiveInboundEnvelope, entry.ProfileActorId);' \
    '    var operation = AgentProfileActorInvariants.RequireOperation(command.Operation);' \
    '    await PersistAsync(operation);' \
    '  }' \
    '  private static void CompactOperations(AgentProfileNamespaceState state) {' \
    '    var removeCount = state.Operations.Count -' \
    '      AgentProfileOperationRetentionPolicy.MaxRetainedNamespaceTerminalOperations;' \
    '    RemoveOldestTerminalOperations(state, removeCount);' \
    '  }' \
    '  private void RequireIngressProof(IMessage command) {' \
    '    if (!_ingressProofVerifier.Verify(Id, command))' \
    '      throw new InvalidOperationException();' \
    '  }' \
    '}'
  write_lines "${core}/AgentProfileGAgent.cs" \
    'public sealed class AgentProfileGAgent {' \
    '  public async Task HandleInitializeAsync(InitializeAgentProfileCommand command) {' \
    '    ArgumentNullException.ThrowIfNull(command);' \
    '    var namespaceActorId = State.Identity is null' \
    '      ? AgentProfileActorIds.Namespace' \
    '      : AgentProfileActorInvariants.RequireActorId(' \
    '        State.NamespaceActorId,' \
    '        "state.namespace_actor_id");' \
    '    AgentProfileActorInvariants.RequireProtocolPublisher(ActiveInboundEnvelope, namespaceActorId);' \
    '    var operation = AgentProfileActorInvariants.RequireOperation(command.Operation);' \
    '    await PersistAsync(operation);' \
    '  }' \
    '  public async Task HandleUpdateDraftAsync(UpdateAgentProfileDraftCommand command) {' \
    '    ArgumentNullException.ThrowIfNull(command);' \
    '    RequireIngressProof(command);' \
    '    var operation = AgentProfileActorInvariants.RequireOperation(command.Operation);' \
    '    await PersistAsync(operation);' \
    '  }' \
    '  public async Task HandleUpsertSkillBindingAsync(UpsertAgentProfileSkillBindingCommand command) {' \
    '    ArgumentNullException.ThrowIfNull(command);' \
    '    RequireIngressProof(command);' \
    '    var operation = AgentProfileActorInvariants.RequireOperation(command.Operation);' \
    '    await PersistAsync(operation);' \
    '  }' \
    '  public async Task HandleRemoveSkillBindingAsync(RemoveAgentProfileSkillBindingCommand command) {' \
    '    ArgumentNullException.ThrowIfNull(command);' \
    '    RequireIngressProof(command);' \
    '    var operation = AgentProfileActorInvariants.RequireOperation(command.Operation);' \
    '    await PersistAsync(operation);' \
    '  }' \
    '  public async Task HandlePublishAsync(PublishAgentProfileCommand command) {' \
    '    ArgumentNullException.ThrowIfNull(command);' \
    '    RequireIngressProof(command);' \
    '    var operation = AgentProfileActorInvariants.RequireOperation(command.Operation);' \
    '    await PersistAsync(operation);' \
    '  }' \
    '  private static void CompactOperations(AgentProfileState state) {' \
    '    var retained = state.Operations.TakeLast(' \
    '      AgentProfileOperationRetentionPolicy.MaxRetainedProfileMutationOperations);' \
    '    ReplaceOperations(state, retained);' \
    '  }' \
    '  private void RequireIngressProof(IMessage command) {' \
    '    if (!_ingressProofVerifier.Verify(Id, command))' \
    '      throw new InvalidOperationException();' \
    '  }' \
    '}'
  write_lines "${core}/AgentProfileActorInvariants.cs" \
    'internal static class AgentProfileActorInvariants {' \
    '  public static void RequireProtocolPublisher(EventEnvelope? envelope, string expected) {' \
    '    if (!string.Equals(envelope?.Route?.PublisherActorId, expected, StringComparison.Ordinal) ||' \
    '        !string.Equals(envelope?.Runtime?.DeliveryProvenance?.AuthenticatedActorId, expected, StringComparison.Ordinal))' \
    '    {' \
    '      throw Error("PROFILE_PROTOCOL_PUBLISHER_MISMATCH", "Publisher mismatch.");' \
    '    }' \
    '  }' \
    '}'
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
    local check_delivery_provenance="${3:-false}"
    if ! output="$(run_guard "${root}" "${check_delivery_provenance}" 2>&1)"; then
      echo "agent_profile_boundary_guard self-test expected PASS: ${label}" >&2
      echo "${output}" >&2
      failures=$((failures + 1))
    fi
  }

  expect_fail() {
    local label="$1"
    local root="$2"
    local expected_output="${3:-}"
    if output="$(run_guard "${root}" false 2>&1)"; then
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

  expect_fail_cases() {
    local label="$1"
    local root="$2"
    shift 2
    local check_delivery_provenance=false
    if [[ "${1:-}" == "--check-delivery-provenance" ]]; then
      check_delivery_provenance=true
      shift
    fi
    local -a expectations=("$@")
    local expectation="" case_label="" expected_hit="" expected_diagnostic=""
    local other_case_label="" other_expected_hit="" other_expected_diagnostic=""
    local expectation_index=0 other_expectation_index=0
    for ((expectation_index = 0; expectation_index < ${#expectations[@]}; expectation_index++)); do
      IFS='|' read -r case_label expected_hit expected_diagnostic \
        <<< "${expectations[expectation_index]}"
      for ((other_expectation_index = expectation_index + 1;
            other_expectation_index < ${#expectations[@]};
            other_expectation_index++)); do
        IFS='|' read -r other_case_label other_expected_hit other_expected_diagnostic \
          <<< "${expectations[other_expectation_index]}"
        if [[ "${expected_hit}" == *"${other_expected_hit}"* ||
              "${other_expected_hit}" == *"${expected_hit}"* ]]; then
          echo "agent_profile_boundary_guard self-test overlapping expected hits: ${case_label} / ${other_case_label}" >&2
          echo "Expected hits must not contain one another: ${expected_hit} / ${other_expected_hit}" >&2
          failures=$((failures + 1))
          return
        fi
      done
    done

    if output="$(run_guard "${root}" "${check_delivery_provenance}" 2>&1)"; then
      echo "agent_profile_boundary_guard self-test expected FAIL: ${label}" >&2
      echo "${output}" >&2
      failures=$((failures + 1))
      return
    fi

    for expectation in "${expectations[@]}"; do
      IFS='|' read -r case_label expected_hit expected_diagnostic <<< "${expectation}"
      if [[ "${output}" != *"${expected_hit}"* ]]; then
        echo "agent_profile_boundary_guard self-test missed expected hit: ${case_label}" >&2
        echo "Expected hit: ${expected_hit}" >&2
        echo "${output}" >&2
        failures=$((failures + 1))
      fi
      if [[ "${output}" != *"${expected_diagnostic}"* ]]; then
        echo "agent_profile_boundary_guard self-test missed expected diagnostic: ${case_label}" >&2
        echo "Expected: ${expected_diagnostic}" >&2
        echo "${output}" >&2
        failures=$((failures + 1))
      fi
    done
  }

  fresh_case() {
    local label="$1"
    case_root="${temp_dir}/${label}"
    cp -R "${base}" "${case_root}"
  }

  local structured_profile_file="src/platform/Aevatar.GAgentService.Core/AgentProfiles/AgentProfileGAgent.cs"
  local structured_namespace_file="src/platform/Aevatar.GAgentService.Core/AgentProfiles/AgentProfileNamespaceGAgent.cs"
  local structured_message="Handler must match the canonical pre-authority statements, exact authority call, and immediate operation parse."
  local -a structured_roots=() structured_expected_hits=()

  record_structured_case() {
    local relative_file="$1"
    local method_name="$2"
    structured_roots+=("${case_root}")
    structured_expected_hits+=(
      "VIOLATION|${case_root}|${relative_file}:${method_name}.authority-order|${structured_message}")
  }

  fresh_case "structured-valid-decoys"
  perl -0777 -pi -e '
    s{(public sealed class AgentProfileGAgent \{\n)}{$1  private const string HandlerText = "HandleInitializeAsync State.Operations.Insert";\n#if false\n  State.Operations.Insert(0, new AgentProfileOperationFact());\n#endif\n};
    s{AgentProfileActorInvariants\.RequireProtocolPublisher\(ActiveInboundEnvelope, namespaceActorId\);}{// State.Operations.Insert(0, new AgentProfileOperationFact());\n    AgentProfileActorInvariants.RequireProtocolPublisher(ActiveInboundEnvelope, namespaceActorId);};
  ' "${case_root}/${structured_profile_file}"
  local structured_valid_root="${case_root}"
  if output="$(run_authority_syntax_checker "${structured_valid_root}" 2>&1)"; then
    if ! printf '%s\n' "${output}" | rg -q -F -x -- "PASS|${structured_valid_root}"; then
      echo "agent_profile_boundary_guard self-test missed structured PASS diagnostic." >&2
      echo "${output}" >&2
      failures=$((failures + 1))
    fi
  else
    local structured_valid_status=$?
    echo "agent_profile_boundary_guard self-test expected structured PASS, got exit ${structured_valid_status}." >&2
    echo "${output}" >&2
    failures=$((failures + 1))
  fi

  fresh_case "structured-raw-string-handler-decoy"
  perl -0777 -pi -e '
    s{(\bHandleInitializeAsync\s*\([^)]*\)\s*\{[\s\S]*?)(AgentProfileActorInvariants\.RequireProtocolPublisher\s*\(\s*ActiveInboundEnvelope\s*,)}{$1State.Operations.Insert(0, new AgentProfileOperationFact());\n    $2};
    s~(public sealed class AgentProfileGAgent \{\n)~$1  private const string AuthorityHandlerDecoy = """\npublic async Task HandleInitializeAsync(InitializeAgentProfileCommand command) {\n    ArgumentNullException.ThrowIfNull(command);\n    var namespaceActorId = State.Identity is null\n        ? AgentProfileActorIds.Namespace\n        : AgentProfileActorInvariants.RequireActorId(\n            State.NamespaceActorId,\n            "state.namespace_actor_id");\n    AgentProfileActorInvariants.RequireProtocolPublisher(ActiveInboundEnvelope, namespaceActorId);\n    var operation = AgentProfileActorInvariants.RequireOperation(command.Operation);\n}\n""";\n~;
  ' "${case_root}/${structured_profile_file}"
  record_structured_case "${structured_profile_file}" "HandleInitializeAsync"

  fresh_case "structured-member-scope-decoys"
  perl -0777 -pi -e '
    s{(\bHandleInitializeAsync\s*\([^)]*\)\s*\{[\s\S]*?)(AgentProfileActorInvariants\.RequireProtocolPublisher\s*\(\s*ActiveInboundEnvelope\s*,)}{$1State.Operations.Insert(0, new AgentProfileOperationFact());\n    $2};
    s~public sealed class AgentProfileGAgent \{\n~public sealed class OtherAgent {\n  public async Task HandleInitializeAsync(InitializeAgentProfileCommand command) {\n    await PersistAsync(command);\n  }\n}\npublic sealed class AgentProfileGAgent {\n  private sealed class NestedAgent {\n    public async Task HandleInitializeAsync(InitializeAgentProfileCommand command) {\n      await PersistAsync(command);\n    }\n  }\n  private void DeclareLocalHandler() {\n    async Task HandleInitializeAsync(InitializeAgentProfileCommand command) {\n      await PersistAsync(command);\n    }\n  }\n~;
  ' "${case_root}/${structured_profile_file}"
  record_structured_case "${structured_profile_file}" "HandleInitializeAsync"

  fresh_case "structured-wrong-first-authority"
  perl -0777 -pi -e '
    s{AgentProfileActorInvariants\.RequireProtocolPublisher\(ActiveInboundEnvelope, namespaceActorId\);}{AgentProfileActorInvariants.RequireProtocolPublisher(ActiveInboundEnvelope, command.NamespaceActorId);\n    State.Operations.Insert(0, new AgentProfileOperationFact());\n    AgentProfileActorInvariants.RequireProtocolPublisher(ActiveInboundEnvelope, namespaceActorId);};
  ' "${case_root}/${structured_profile_file}"
  record_structured_case "${structured_profile_file}" "HandleInitializeAsync"

  fresh_case "structured-executable-interpolation"
  perl -0777 -pi -e '
    s{"state\.namespace_actor_id"}{\$"{State.Operations.FirstOrDefault(candidate => candidate.Operation.OperationId == command.Operation.OperationId)}"};
  ' "${case_root}/${structured_profile_file}"
  record_structured_case "${structured_profile_file}" "HandleInitializeAsync"

  fresh_case "structured-inline-replay"
  perl -0777 -pi -e '
    s{(\bHandleInitializeAsync\s*\([^)]*\)\s*\{[\s\S]*?)(AgentProfileActorInvariants\.RequireProtocolPublisher\s*\(\s*ActiveInboundEnvelope\s*,)}{$1var replay = State.Operations.FirstOrDefault(candidate => candidate.Operation.OperationId == command.Operation.OperationId);\n    $2};
  ' "${case_root}/${structured_profile_file}"
  record_structured_case "${structured_profile_file}" "HandleInitializeAsync"

  fresh_case "structured-state-insert"
  perl -0777 -pi -e '
    s{(\bHandleInitializedAsync\s*\([^)]*\)\s*\{[\s\S]*?)(AgentProfileActorInvariants\.RequireProtocolPublisher\s*\(\s*ActiveInboundEnvelope\s*,)}{$1State.Operations.Insert(0, new AgentProfileOperationFact());\n    $2};
  ' "${case_root}/${structured_namespace_file}"
  record_structured_case "${structured_namespace_file}" "HandleInitializedAsync"

  fresh_case "structured-direct-assignment"
  perl -0777 -pi -e '
    s{(\bHandleInitializationRejectedAsync\s*\([^)]*\)\s*\{[\s\S]*?)(AgentProfileActorInvariants\.RequireProtocolPublisher\s*\(\s*ActiveInboundEnvelope\s*,)}{$1State.LastPublishedRevision = continuation.DraftRevision;\n    $2};
  ' "${case_root}/${structured_namespace_file}"
  record_structured_case "${structured_namespace_file}" "HandleInitializationRejectedAsync"

  fresh_case "structured-send-effect"
  perl -0777 -pi -e '
    s{(\bHandleInitializationRejectedAsync\s*\([^)]*\)\s*\{[\s\S]*?)(AgentProfileActorInvariants\.RequireProtocolPublisher\s*\(\s*ActiveInboundEnvelope\s*,)}{$1await SendToAsync(profileActorId, continuation);\n    $2};
  ' "${case_root}/${structured_namespace_file}"
  record_structured_case "${structured_namespace_file}" "HandleInitializationRejectedAsync"

  fresh_case "structured-persist-effect"
  perl -0777 -pi -e '
    s{(\bHandleObservePublishedSummaryAsync\s*\([^)]*\)\s*\{[\s\S]*?)(AgentProfileActorInvariants\.RequireProtocolPublisher\s*\(\s*ActiveInboundEnvelope\s*,)}{$1await PersistAsync(command.Operation);\n    $2};
  ' "${case_root}/${structured_namespace_file}"
  record_structured_case "${structured_namespace_file}" "HandleObservePublishedSummaryAsync"

  fresh_case "structured-missing-initialize"
  perl -0777 -pi -e '
    s{AgentProfileActorInvariants\.RequireProtocolPublisher\(ActiveInboundEnvelope, namespaceActorId\);}{};
  ' "${case_root}/${structured_profile_file}"
  record_structured_case "${structured_profile_file}" "HandleInitializeAsync"

  fresh_case "structured-missing-initialized"
  perl -0777 -pi -e '
    s{(\bHandleInitializedAsync\s*\([^)]*\)\s*\{[\s\S]*?)AgentProfileActorInvariants\.RequireProtocolPublisher\(ActiveInboundEnvelope, profileActorId\);}{$1};
  ' "${case_root}/${structured_namespace_file}"
  record_structured_case "${structured_namespace_file}" "HandleInitializedAsync"

  fresh_case "structured-missing-rejected"
  perl -0777 -pi -e '
    s{(\bHandleInitializationRejectedAsync\s*\([^)]*\)\s*\{[\s\S]*?)AgentProfileActorInvariants\.RequireProtocolPublisher\(ActiveInboundEnvelope, profileActorId\);}{$1};
  ' "${case_root}/${structured_namespace_file}"
  record_structured_case "${structured_namespace_file}" "HandleInitializationRejectedAsync"

  fresh_case "structured-missing-summary"
  perl -0777 -pi -e '
    s{AgentProfileActorInvariants\.RequireProtocolPublisher\(ActiveInboundEnvelope, entry\.ProfileActorId\);}{};
  ' "${case_root}/${structured_namespace_file}"
  record_structured_case "${structured_namespace_file}" "HandleObservePublishedSummaryAsync"

  fresh_case "structured-duplicate-target-class"
  printf '%s\n' \
    'public sealed class AgentProfileGAgent {' \
    '  public async Task HandleInitializeAsync(InitializeAgentProfileCommand command) {' \
    '    await PersistAsync(command);' \
    '  }' \
    '}' >> "${case_root}/${structured_profile_file}"
  record_structured_case "${structured_profile_file}" "HandleInitializeAsync"

  fresh_case "structured-duplicate-target-method"
  perl -0777 -pi -e '
    s{(public sealed class AgentProfileGAgent \{\n)}{$1  public async Task HandleInitializeAsync(InitializeAgentProfileCommand command) {\n    await PersistAsync(command);\n  }\n};
  ' "${case_root}/${structured_profile_file}"
  record_structured_case "${structured_profile_file}" "HandleInitializeAsync"

  fresh_case "structured-wrong-signature"
  perl -0777 -pi -e '
    s{HandleInitializeAsync\(InitializeAgentProfileCommand command\)}{HandleInitializeAsync(OtherInitializeCommand command)};
  ' "${case_root}/${structured_profile_file}"
  record_structured_case "${structured_profile_file}" "HandleInitializeAsync"

  fresh_case "structured-expression-body"
  write_lines "${case_root}/${structured_profile_file}" \
    'public sealed class AgentProfileGAgent {' \
    '  public async Task HandleInitializeAsync(InitializeAgentProfileCommand command) =>' \
    '    await PersistAsync(command);' \
    '}'
  record_structured_case "${structured_profile_file}" "HandleInitializeAsync"

  fresh_case "structured-parse-error"
  perl -0777 -pi -e 's/\A/}/' "${case_root}/${structured_profile_file}"
  record_structured_case "${structured_profile_file}" "HandleInitializeAsync"

  local expected_index=0 other_expected_index=0
  local expected_hit="" other_expected_hit=""
  for ((expected_index = 0; expected_index < ${#structured_expected_hits[@]}; expected_index++)); do
    expected_hit="${structured_expected_hits[expected_index]}"
    for ((other_expected_index = expected_index + 1;
          other_expected_index < ${#structured_expected_hits[@]};
          other_expected_index++)); do
      other_expected_hit="${structured_expected_hits[other_expected_index]}"
      if [[ "${expected_hit}" == *"${other_expected_hit}"* ||
            "${other_expected_hit}" == *"${expected_hit}"* ]]; then
        echo "agent_profile_boundary_guard self-test overlapping structured expected hits." >&2
        echo "Expected hits must not contain one another: ${expected_hit} / ${other_expected_hit}" >&2
        failures=$((failures + 1))
      fi
    done
  done

  local structured_batch_status=0
  if output="$(run_authority_syntax_checker "${structured_roots[@]}" 2>&1)"; then
    echo "agent_profile_boundary_guard self-test expected structured batch exit 1." >&2
    echo "${output}" >&2
    failures=$((failures + 1))
  else
    structured_batch_status=$?
    if (( structured_batch_status != 1 )); then
      echo "agent_profile_boundary_guard self-test expected structured batch exit 1, got ${structured_batch_status}." >&2
      echo "${output}" >&2
      failures=$((failures + 1))
    fi
  fi
  for expected_hit in "${structured_expected_hits[@]}"; do
    if ! printf '%s\n' "${output}" | rg -q -F -x -- "${expected_hit}"; then
      echo "agent_profile_boundary_guard self-test missed structured diagnostic: ${expected_hit}" >&2
      echo "${output}" >&2
      failures=$((failures + 1))
    fi
  done

  expect_pass "legal metadata, unrelated collections, and harmless semantic strings" "${base}" true

  fresh_case "semantic-identifier-strings"
  printf '%s\n' \
    'public string DescribeContextBags() => "Metadata Headers Items AsyncLocal";' \
    >> "${case_root}/src/platform/Aevatar.GAgentService.Application/AgentProfiles/AgentProfileService.cs"
  expect_pass "semantic identifiers inside C# strings" "${case_root}"

  local handler_label="" handler_file="" handler_method=""
  fresh_case "ingress-proof-handlers"
  while IFS='|' read -r handler_label handler_file handler_method; do
    HANDLER_METHOD="${handler_method}" perl -0777 -pi -e '
      my $method = $ENV{"HANDLER_METHOD"};
      s{(\b\Q$method\E\s*\([^)]*\)\s*\{\s*ArgumentNullException\.ThrowIfNull\(command\);\s*)RequireIngressProof\(command\);}{$1}s;
    ' "${case_root}/${handler_file}"
  done <<'CASES'
create|src/platform/Aevatar.GAgentService.Core/AgentProfiles/AgentProfileNamespaceGAgent.cs|HandleCreateAsync
update|src/platform/Aevatar.GAgentService.Core/AgentProfiles/AgentProfileGAgent.cs|HandleUpdateDraftAsync
upsert|src/platform/Aevatar.GAgentService.Core/AgentProfiles/AgentProfileGAgent.cs|HandleUpsertSkillBindingAsync
remove|src/platform/Aevatar.GAgentService.Core/AgentProfiles/AgentProfileGAgent.cs|HandleRemoveSkillBindingAsync
publish|src/platform/Aevatar.GAgentService.Core/AgentProfiles/AgentProfileGAgent.cs|HandlePublishAsync
CASES
  expect_fail_cases "external handler ingress proofs" "${case_root}" \
    "external handler proof create|src/platform/Aevatar.GAgentService.Core/AgentProfiles/AgentProfileNamespaceGAgent.cs:HandleCreateAsync|Every external Agent Profile command handler must verify its ingress proof before parsing the operation." \
    "external handler proof update|src/platform/Aevatar.GAgentService.Core/AgentProfiles/AgentProfileGAgent.cs:HandleUpdateDraftAsync|Every external Agent Profile command handler must verify its ingress proof before parsing the operation." \
    "external handler proof upsert|src/platform/Aevatar.GAgentService.Core/AgentProfiles/AgentProfileGAgent.cs:HandleUpsertSkillBindingAsync|Every external Agent Profile command handler must verify its ingress proof before parsing the operation." \
    "external handler proof remove|src/platform/Aevatar.GAgentService.Core/AgentProfiles/AgentProfileGAgent.cs:HandleRemoveSkillBindingAsync|Every external Agent Profile command handler must verify its ingress proof before parsing the operation." \
    "external handler proof publish|src/platform/Aevatar.GAgentService.Core/AgentProfiles/AgentProfileGAgent.cs:HandlePublishAsync|Every external Agent Profile command handler must verify its ingress proof before parsing the operation."

  fresh_case "actor-delivery-provenance"
  perl -0777 -pi -e \
    's/EnvelopeDeliveryProvenance delivery_provenance = 9;/string delivery_provenance = 9;/' \
    "${case_root}/src/Aevatar.Foundation.Abstractions/agent_messages.proto"
  perl -0777 -pi -e \
    's/admitted\.Runtime\.DeliveryProvenance = null;//; s{envelope\.EnsureRuntime\(\)\.DeliveryProvenance = new EnvelopeDeliveryProvenance \{\s*AuthenticatedActorId = actorId,\s*\};}{envelope.EnsureRuntime().SourceActorId = actorId;}s' \
    "${case_root}/src/Aevatar.Foundation.Runtime/Propagation/EnvelopeDeliveryProvenanceSemantics.cs"
  perl -0777 -pi -e \
    's/EnvelopeDeliveryProvenanceSemantics\.StampAuthenticatedActorOrigin\(envelope, sourceActorId\);//' \
    "${case_root}/src/Aevatar.Foundation.Runtime/Propagation/EnvelopePublishContextHelpers.cs"
  perl -0777 -pi -e \
    's/EnvelopeDeliveryProvenanceSemantics\.CloneForRawDispatch\(envelope\)/envelope.Clone()/g' \
    "${case_root}/src/Aevatar.Foundation.Runtime.Implementations.Local/Actors/LocalActorDispatchPort.cs" \
    "${case_root}/src/Aevatar.Foundation.Runtime.Implementations.Orleans/Actors/OrleansActorDispatchPort.cs"
  perl -0777 -pi -e \
    's/\.Runtime\?\.DeliveryProvenance\?\.AuthenticatedActorId/.Runtime?.SourceActorId/' \
    "${case_root}/src/platform/Aevatar.GAgentService.Core/AgentProfiles/AgentProfileActorInvariants.cs"
  expect_fail_cases "runtime-authenticated Actor delivery provenance" "${case_root}" \
    --check-delivery-provenance \
    "typed delivery provenance contract|src/Aevatar.Foundation.Abstractions/agent_messages.proto:EnvelopeRuntime.delivery_provenance|EventEnvelope runtime must expose typed delivery provenance for the authenticated Actor origin." \
    "raw delivery provenance clone|src/Aevatar.Foundation.Runtime/Propagation/EnvelopeDeliveryProvenanceSemantics.cs:CloneForRawDispatch|Raw dispatch provenance must clear and return the same admitted envelope clone." \
    "bound Actor provenance stamp|src/Aevatar.Foundation.Runtime/Propagation/EnvelopeDeliveryProvenanceSemantics.cs:StampAuthenticatedActorOrigin|Actor-bound provenance must stamp only the bound Actor id." \
    "Local raw dispatch admission|src/Aevatar.Foundation.Runtime.Implementations.Local/Actors/LocalActorDispatchPort.cs:DispatchAsync|Raw Local and Orleans dispatch admission must deliver the cleared clone instead of the caller envelope." \
    "Orleans raw dispatch admission|src/Aevatar.Foundation.Runtime.Implementations.Orleans/Actors/OrleansActorDispatchPort.cs:DispatchAsync|Raw Local and Orleans dispatch admission must deliver the cleared clone instead of the caller envelope." \
    "bound Actor publish context|src/Aevatar.Foundation.Runtime/Propagation/EnvelopePublishContextHelpers.cs:ApplyOutboundPublishContext|Actor-bound publishing must stamp runtime-authenticated origin after propagation." \
    "protocol invariant comparison|src/platform/Aevatar.GAgentService.Core/AgentProfiles/AgentProfileActorInvariants.cs:RequireProtocolPublisher.fail-closed|Agent Profile protocol authority must fail closed when either required Actor identity mismatches."

  fresh_case "actor-delivery-clone-binding"
  perl -0777 -pi -e \
    's/admitted\.Runtime\.DeliveryProvenance = null;/envelope.Runtime.DeliveryProvenance = null;/' \
    "${case_root}/src/Aevatar.Foundation.Runtime/Propagation/EnvelopeDeliveryProvenanceSemantics.cs"
  expect_fail_cases "raw dispatch clone, clear, and return binding" "${case_root}" \
    --check-delivery-provenance \
    "raw dispatch admitted clone binding|src/Aevatar.Foundation.Runtime/Propagation/EnvelopeDeliveryProvenanceSemantics.cs:CloneForRawDispatch|Raw dispatch provenance must clear and return the same admitted envelope clone."

  fresh_case "actor-delivery-clone-reassignment"
  perl -0777 -pi -e \
    's/admitted\.Runtime\.DeliveryProvenance = null;/admitted.Runtime.DeliveryProvenance = null;\n    admitted = envelope.Clone();/' \
    "${case_root}/src/Aevatar.Foundation.Runtime/Propagation/EnvelopeDeliveryProvenanceSemantics.cs"
  expect_fail_cases "raw dispatch admitted clone cannot be replaced" "${case_root}" \
    --check-delivery-provenance \
    "raw dispatch admitted clone reassignment|src/Aevatar.Foundation.Runtime/Propagation/EnvelopeDeliveryProvenanceSemantics.cs:CloneForRawDispatch|Raw dispatch provenance must clear and return the same admitted envelope clone."

  fresh_case "actor-delivery-dispatch-consumption"
  perl -0777 -pi -e '
    s{target\.AcceptDispatchedEnvelope\(\s*EnvelopeDeliveryProvenanceSemantics\.CloneForRawDispatch\(envelope\)\s*\);}{EnvelopeDeliveryProvenanceSemantics.CloneForRawDispatch(envelope);\n    target.AcceptDispatchedEnvelope(envelope);}s;
  ' "${case_root}/src/Aevatar.Foundation.Runtime.Implementations.Local/Actors/LocalActorDispatchPort.cs"
  perl -0777 -pi -e '
    s{await stream\.ProduceAsync\(\s*EnvelopeDeliveryProvenanceSemantics\.CloneForRawDispatch\(envelope\),\s*ct\);}{EnvelopeDeliveryProvenanceSemantics.CloneForRawDispatch(envelope);\n    await stream.ProduceAsync(envelope, ct);}s;
  ' "${case_root}/src/Aevatar.Foundation.Runtime.Implementations.Orleans/Actors/OrleansActorDispatchPort.cs"
  expect_fail_cases "raw dispatch must consume admitted clone" "${case_root}" \
    --check-delivery-provenance \
    "Local raw dispatch admitted clone consumption|src/Aevatar.Foundation.Runtime.Implementations.Local/Actors/LocalActorDispatchPort.cs:DispatchAsync|Raw Local and Orleans dispatch admission must deliver the cleared clone instead of the caller envelope." \
    "Orleans raw dispatch admitted clone consumption|src/Aevatar.Foundation.Runtime.Implementations.Orleans/Actors/OrleansActorDispatchPort.cs:DispatchAsync|Raw Local and Orleans dispatch admission must deliver the cleared clone instead of the caller envelope."

  fresh_case "actor-delivery-fail-closed-dual-predicate"
  perl -0777 -pi -e 's/\|\|/&&/' \
    "${case_root}/src/platform/Aevatar.GAgentService.Core/AgentProfiles/AgentProfileActorInvariants.cs"
  expect_fail_cases "fail-closed dual protocol publisher predicate" "${case_root}" \
    --check-delivery-provenance \
    "protocol publisher fail-closed predicate|src/platform/Aevatar.GAgentService.Core/AgentProfiles/AgentProfileActorInvariants.cs:RequireProtocolPublisher.fail-closed|Agent Profile protocol authority must fail closed when either required Actor identity mismatches."

  fresh_case "actor-delivery-fail-closed-branch"
  perl -0777 -pi -e 's/throw Error\([^;]+;/return;/s' \
    "${case_root}/src/platform/Aevatar.GAgentService.Core/AgentProfiles/AgentProfileActorInvariants.cs"
  expect_fail_cases "protocol publisher mismatch branch must throw" "${case_root}" \
    --check-delivery-provenance \
    "protocol publisher throwing mismatch branch|src/platform/Aevatar.GAgentService.Core/AgentProfiles/AgentProfileActorInvariants.cs:RequireProtocolPublisher.fail-closed|Agent Profile protocol authority must fail closed when either required Actor identity mismatches."

  fresh_case "actor-contract-violations"
  perl -0777 -pi -e '
    s{RequireIngressProof\(command\);\s*(var operation = AgentProfileActorInvariants\.RequireOperation\(command\.Operation\);)}{$1\n    RequireIngressProof(command);}s;
  ' "${case_root}/src/platform/Aevatar.GAgentService.Core/AgentProfiles/AgentProfileNamespaceGAgent.cs"

  local verifier_label="" verifier_file=""
  while IFS='|' read -r verifier_label verifier_file; do
    perl -0777 -pi -e \
      's/_ingressProofVerifier\.Verify\(Id, command\)/true/g' \
      "${case_root}/${verifier_file}"
  done <<'CASES'
profile|src/platform/Aevatar.GAgentService.Core/AgentProfiles/AgentProfileGAgent.cs
namespace|src/platform/Aevatar.GAgentService.Core/AgentProfiles/AgentProfileNamespaceGAgent.cs
CASES

  local retention_label="" retention_file="" retention_member="" retention_literal=""
  while IFS='|' read -r retention_label retention_file retention_member retention_literal; do
    RETENTION_MEMBER="${retention_member}" RETENTION_LITERAL="${retention_literal}" \
      perl -0777 -pi -e '
        my $member = $ENV{"RETENTION_MEMBER"};
        my $literal = $ENV{"RETENTION_LITERAL"};
        s/AgentProfileOperationRetentionPolicy\.\Q$member\E/$literal/g;
      ' "${case_root}/${retention_file}"
  done <<'CASES'
profile|src/platform/Aevatar.GAgentService.Core/AgentProfiles/AgentProfileGAgent.cs|MaxRetainedProfileMutationOperations|256
namespace|src/platform/Aevatar.GAgentService.Core/AgentProfiles/AgentProfileNamespaceGAgent.cs|MaxRetainedNamespaceTerminalOperations|1024
CASES
  expect_fail_cases "Actor proof ordering, verifier, and retention contracts" "${case_root}" \
    "external handler proof after operation parsing|src/platform/Aevatar.GAgentService.Core/AgentProfiles/AgentProfileNamespaceGAgent.cs:HandleCreateAsync|Every external Agent Profile command handler must verify its ingress proof before parsing the operation." \
    "Actor proof verifier profile|src/platform/Aevatar.GAgentService.Core/AgentProfiles/AgentProfileGAgent.cs:RequireIngressProof|Each Agent Profile Actor must delegate ingress proof validation to IAgentProfileIngressProofVerifier." \
    "Actor proof verifier namespace|src/platform/Aevatar.GAgentService.Core/AgentProfiles/AgentProfileNamespaceGAgent.cs:RequireIngressProof|Each Agent Profile Actor must delegate ingress proof validation to IAgentProfileIngressProofVerifier." \
    "Actor retention policy profile|src/platform/Aevatar.GAgentService.Core/AgentProfiles/AgentProfileGAgent.cs:CompactOperations|Both Agent Profile Actors must compact operation state through AgentProfileOperationRetentionPolicy." \
    "Actor retention policy namespace|src/platform/Aevatar.GAgentService.Core/AgentProfiles/AgentProfileNamespaceGAgent.cs:CompactOperations|Both Agent Profile Actors must compact operation state through AgentProfileOperationRetentionPolicy."

  local retention_constant="" retention_value="" replacement_value=""
  # The production guard intentionally emits one policy-level hit for either bad
  # constant, so these two cases remain separate to prove each bound independently.
  while IFS='|' read -r retention_constant retention_value replacement_value; do
    fresh_case "retention-policy-${retention_constant}"
    RETENTION_CONSTANT="${retention_constant}" RETENTION_VALUE="${retention_value}" \
      REPLACEMENT_VALUE="${replacement_value}" perl -0777 -pi -e '
        my $constant = $ENV{"RETENTION_CONSTANT"};
        my $value = $ENV{"RETENTION_VALUE"};
        my $replacement = $ENV{"REPLACEMENT_VALUE"};
        s/(\b\Q$constant\E\s*=\s*)\Q$value\E\b/$1$replacement/g;
      ' "${case_root}/src/platform/Aevatar.GAgentService.Core/AgentProfiles/AgentProfileOperationRetentionPolicy.cs"
    expect_fail "retention policy constant ${retention_constant}" "${case_root}" \
      "must declare the exact 256/1024 operation retention bounds"
  done <<'CASES'
MaxRetainedProfileMutationOperations|256|255
MaxRetainedNamespaceTerminalOperations|1024|1023
CASES

  fresh_case "semantic-state-violations"
  local semantic_state_file="src/platform/Aevatar.GAgentService.Application/AgentProfiles/AgentProfileService.cs"
  printf '%s\n' 'private static AgentProfileIdentity? _current;' \
    >> "${case_root}/${semantic_state_file}"
  printf '%s\n' \
    'private static AgentProfileIdentity? Current' \
    '{' \
    '  get;' \
    '  set;' \
    '}' \
    >> "${case_root}/${semantic_state_file}"
  printf '%s\n' 'private readonly Dictionary<string, string> _profileBindings = new();' \
    >> "${case_root}/${semantic_state_file}"

  local collection_label="" collection_probe=""
  while IFS='|' read -r collection_label collection_probe; do
    printf '%s\n' "${collection_probe}" \
      >> "${case_root}/${semantic_state_file}"
  done <<'CASES'
idictionary|private readonly IDictionary<string, string> _profileBindings;
readonly-dictionary|private readonly IReadOnlyDictionary<string, AgentProfileIdentity> _index;
immutable-dictionary|private readonly ImmutableDictionary<string, string> _profileFacts;
nested-generic|private readonly Dictionary<string, IReadOnlyList<AgentProfileIdentity>> _index = new();
CASES

  printf '%s\n' \
    'private readonly Dictionary<string,' \
    '  IReadOnlyList<AgentProfileIdentity>> _wrappedIndex = new();' \
    >> "${case_root}/${semantic_state_file}"

  local provider_file="src/platform/Aevatar.GAgentService.Projection/AgentProfiles/AgentProfileDocumentMetadataProviders.cs"
  local provider_probe="" provider_label=""
  while IFS='|' read -r provider_label provider_probe; do
    printf '%s\n' "${provider_probe}" >> "${case_root}/${provider_file}"
  done <<'CASES'
async-local|private static readonly AsyncLocal<string?> Current = new();
items|private readonly Dictionary<string, string> Items = new();
headers|private string Headers => "forbidden";
unrelated-metadata|public string Metadata => "forbidden";
CASES
  expect_fail_cases "typed state, Profile collections, and context bags" "${case_root}" \
    "typed static Profile state|AgentProfileIdentity? _current|Static typed Agent Profile state is forbidden." \
    "typed static Profile auto-property|AgentProfileIdentity? Current|Static typed Agent Profile state is forbidden." \
    "Profile fact collection|private readonly Dictionary<string, string> _profileBindings = new();|Private service-level collections must not hold Agent Profile or binding facts." \
    "Profile fact collection idictionary|private readonly IDictionary<string, string> _profileBindings;|Private service-level collections must not hold Agent Profile or binding facts." \
    "Profile fact collection readonly-dictionary|IReadOnlyDictionary<string, AgentProfileIdentity> _index|Private service-level collections must not hold Agent Profile or binding facts." \
    "Profile fact collection immutable-dictionary|ImmutableDictionary<string, string> _profileFacts|Private service-level collections must not hold Agent Profile or binding facts." \
    "Profile fact collection nested-generic|Dictionary<string, IReadOnlyList<AgentProfileIdentity>> _index|Private service-level collections must not hold Agent Profile or binding facts." \
    "Profile fact collection wrapped nested generic|_wrappedIndex = new();|Private service-level collections must not hold Agent Profile or binding facts." \
    "metadata provider async-local|AsyncLocal<string?> Current|only the exact projection document Metadata contract is allowed." \
    "metadata provider items|Dictionary<string, string> Items|only the exact projection document Metadata contract is allowed." \
    "metadata provider headers|string Headers|only the exact projection document Metadata contract is allowed." \
    "metadata provider unrelated-metadata|string Metadata|only the exact projection document Metadata contract is allowed."

  local remote_file="" remote_probe="" remote_label=""
  fresh_case "dependency-reference-ingress-violations"
  local dependency_case_root="${case_root}"
  while IFS='|' read -r remote_label remote_file remote_probe; do
    printf '%s\n' "${remote_probe}" >> "${dependency_case_root}/${remote_file}"
  done <<'CASES'
http|src/platform/Aevatar.GAgentService.Core/AgentProfiles/AgentProfile.cs|private readonly HttpClient _client;
ornn|src/platform/Aevatar.GAgentService.Projection/AgentProfiles/AgentProfileProjector.cs|private readonly OrnnSkillClient _client;
name-fetch|src/platform/Aevatar.GAgentService.Projection/AgentProfiles/AgentProfileProjector.cs|private Task GetSkillJsonAsync() => ProjectionOnly();
search|src/platform/Aevatar.GAgentService.Core/AgentProfiles/AgentProfile.cs|private Task SearchSkillsAsync();
CASES

  local application_file="src/platform/Aevatar.GAgentService.Application/AgentProfiles/AgentProfileService.cs"
  local application_probe="" application_label=""
  while IFS='|' read -r application_label application_probe; do
    printf '%s\n' "${application_probe}" >> "${dependency_case_root}/${application_file}"
  done <<'CASES'
name-or-id|private string nameOrId = "skill";
name-or-id-snake|private string name_or_id = "skill";
latest-identifier-bare|private string latest = "1.0";
latest-identifier-camel|private string latestVersion = "1.0";
latest-identifier-snake|private string latest_version = "1.0";
latest-contract|private string Version = "latest";
inline-skill|private string inlineSkill = "content";
inline-skill-snake|private string inline_skill = "content";
name-fetch|private Task GetSkillJsonAsync() => ApplicationOnly();
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

  fresh_case "client-preliminary-live-statements"
  write_lines "${case_root}/src/Aevatar.AI.ToolProviders.Ornn/OrnnSkillClient.cs" \
    'public sealed class OrnnSkillClient {' \
    '  internal Task<OrnnExactSkillReadResult<OrnnExactSkillDetail>> GetExactSkillDetailAsync(string accessToken, string guid, string literalVersion, CancellationToken ct = default) {' \
    '    ct.ThrowIfCancellationRequested();' \
    '    RecordExactRead(guid, literalVersion);' \
    '    return GetExactAsync<OrnnExactSkillDetail>(accessToken,' \
    '      $"/api/v1/skills/{Uri.EscapeDataString(guid)}?version={Uri.EscapeDataString(literalVersion)}", guid, ct); }' \
    '  internal Task<OrnnExactSkillReadResult<OrnnSkillJson>> GetExactSkillJsonAsync(string accessToken, string guid, string literalVersion, CancellationToken ct = default) {' \
    '    ct.ThrowIfCancellationRequested();' \
    '    RecordExactRead(guid, literalVersion);' \
    '    return GetExactAsync<OrnnSkillJson>(accessToken,' \
    '      $"/api/v1/skills/{Uri.EscapeDataString(guid)}/json?version={Uri.EscapeDataString(literalVersion)}", guid, ct); } }'
  expect_pass "exact client preliminary live statements" "${case_root}"

  fresh_case "client-local-function-return-decoy"
  write_lines "${case_root}/src/Aevatar.AI.ToolProviders.Ornn/OrnnSkillClient.cs" \
    'public sealed class OrnnSkillClient {' \
    '  internal Task<OrnnExactSkillReadResult<OrnnExactSkillDetail>> GetExactSkillDetailAsync(string accessToken, string guid, string literalVersion, CancellationToken ct = default) {' \
    '    Task<OrnnExactSkillReadResult<OrnnExactSkillDetail>> ExactDecoy() =>' \
    '      GetExactAsync<OrnnExactSkillDetail>(accessToken, $"/api/v1/skills/{Uri.EscapeDataString(guid)}?version={Uri.EscapeDataString(literalVersion)}", guid, ct);' \
    '    return GetExactAsync<OrnnExactSkillDetail>(accessToken, "/api/v1/skills/live", guid, ct); }' \
    '  internal Task<OrnnExactSkillReadResult<OrnnSkillJson>> GetExactSkillJsonAsync(string accessToken, string guid, string literalVersion, CancellationToken ct = default) =>' \
    '    GetExactAsync<OrnnSkillJson>(accessToken, $"/api/v1/skills/{Uri.EscapeDataString(guid)}/json?version={Uri.EscapeDataString(literalVersion)}", guid, ct); }'
  expect_fail "exact client local-function return decoy" "${case_root}" \
    "executable exact Ornn Profile detail read"

  fresh_case "client-dead-conditional-return-decoy"
  write_lines "${case_root}/src/Aevatar.AI.ToolProviders.Ornn/OrnnSkillClient.cs" \
    'public sealed class OrnnSkillClient {' \
    '  internal Task<OrnnExactSkillReadResult<OrnnExactSkillDetail>> GetExactSkillDetailAsync(string accessToken, string guid, string literalVersion, CancellationToken ct = default) {' \
    '    if (false) return GetExactAsync<OrnnExactSkillDetail>(accessToken, $"/api/v1/skills/{Uri.EscapeDataString(guid)}?version={Uri.EscapeDataString(literalVersion)}", guid, ct);' \
    '    return GetExactAsync<OrnnExactSkillDetail>(accessToken, "/api/v1/skills/live", guid, ct); }' \
    '  internal Task<OrnnExactSkillReadResult<OrnnSkillJson>> GetExactSkillJsonAsync(string accessToken, string guid, string literalVersion, CancellationToken ct = default) =>' \
    '    GetExactAsync<OrnnSkillJson>(accessToken, $"/api/v1/skills/{Uri.EscapeDataString(guid)}/json?version={Uri.EscapeDataString(literalVersion)}", guid, ct); }'
  expect_fail "exact client dead-conditional return decoy" "${case_root}" \
    "executable exact Ornn Profile detail read"

  fresh_case "client-unreachable-tail-decoy"
  write_lines "${case_root}/src/Aevatar.AI.ToolProviders.Ornn/OrnnSkillClient.cs" \
    'public sealed class OrnnSkillClient {' \
    '  internal Task<OrnnExactSkillReadResult<OrnnExactSkillDetail>> GetExactSkillDetailAsync(string accessToken, string guid, string literalVersion, CancellationToken ct = default) {' \
    '    return GetExactAsync<OrnnExactSkillDetail>(accessToken, "/api/v1/skills/live", guid, ct);' \
    '    GetExactAsync<OrnnExactSkillDetail>(accessToken, $"/api/v1/skills/{Uri.EscapeDataString(guid)}?version={Uri.EscapeDataString(literalVersion)}", guid, ct); }' \
    '  internal Task<OrnnExactSkillReadResult<OrnnSkillJson>> GetExactSkillJsonAsync(string accessToken, string guid, string literalVersion, CancellationToken ct = default) =>' \
    '    GetExactAsync<OrnnSkillJson>(accessToken, $"/api/v1/skills/{Uri.EscapeDataString(guid)}/json?version={Uri.EscapeDataString(literalVersion)}", guid, ct); }'
  expect_fail "exact client unreachable-tail decoy" "${case_root}" \
    "executable exact Ornn Profile detail read"

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

  fresh_case "adapter-live-try"
  write_lines "${case_root}/src/Aevatar.AI.ToolProviders.Ornn/AgentProfiles/OrnnExactAgentProfileSkillResolver.cs" \
    'public sealed class OrnnExactAgentProfileSkillResolver {' \
    '  public async Task<ExactOrnnSkillResolutionResult> ResolveAsync(CancellationToken ct = default) {' \
    '    try {' \
    '      var detailRead = await _client.GetExactSkillDetailAsync(token, guid, version, ct);' \
    '      var jsonRead = await _client.GetExactSkillJsonAsync(token, guid, version, ct);' \
    '      return Combine(detailRead, jsonRead);' \
    '    } catch {' \
    '      return default!;' \
    '    } } }'
  expect_pass "exact adapter reads inside live try" "${case_root}"

  fresh_case "adapter-unreachable-tail-decoy"
  write_lines "${case_root}/src/Aevatar.AI.ToolProviders.Ornn/AgentProfiles/OrnnExactAgentProfileSkillResolver.cs" \
    'public sealed class OrnnExactAgentProfileSkillResolver {' \
    '  public async Task<ExactOrnnSkillResolutionResult> ResolveAsync(CancellationToken ct = default) {' \
    '    return default!;' \
    '    var detailRead = await _client.GetExactSkillDetailAsync(token, guid, version, ct);' \
    '    var jsonRead = await _client.GetExactSkillJsonAsync(token, guid, version, ct);' \
    '  } }'
  expect_fail "exact adapter unreachable tail decoys" "${case_root}" \
    "ResolveAsync must execute"

  fresh_case "adapter-throw-unreachable-tail-decoy"
  write_lines "${case_root}/src/Aevatar.AI.ToolProviders.Ornn/AgentProfiles/OrnnExactAgentProfileSkillResolver.cs" \
    'public sealed class OrnnExactAgentProfileSkillResolver {' \
    '  public async Task<ExactOrnnSkillResolutionResult> ResolveAsync(CancellationToken ct = default) {' \
    '    throw new InvalidOperationException();' \
    '    var detailRead = await _client.GetExactSkillDetailAsync(token, guid, version, ct);' \
    '    var jsonRead = await _client.GetExactSkillJsonAsync(token, guid, version, ct);' \
    '  } }'
  expect_fail "exact adapter throw-unreachable tail decoys" "${case_root}" \
    "ResolveAsync must execute"

  fresh_case "adapter-pragma-throw-unreachable-tail-decoy"
  write_lines "${case_root}/src/Aevatar.AI.ToolProviders.Ornn/AgentProfiles/OrnnExactAgentProfileSkillResolver.cs" \
    'public sealed class OrnnExactAgentProfileSkillResolver {' \
    '  public async Task<ExactOrnnSkillResolutionResult> ResolveAsync(CancellationToken ct = default) {' \
    '    #pragma warning disable CS0162' \
    '    throw new InvalidOperationException();' \
    '    var detailRead = await _client.GetExactSkillDetailAsync(token, guid, version, ct);' \
    '    var jsonRead = await _client.GetExactSkillJsonAsync(token, guid, version, ct);' \
    '  } }'
  expect_fail "exact adapter pragma-hidden throw-unreachable tail decoys" "${case_root}" \
    "structurally valid"

  fresh_case "adapter-labeled-throw-unreachable-tail-decoy"
  write_lines "${case_root}/src/Aevatar.AI.ToolProviders.Ornn/AgentProfiles/OrnnExactAgentProfileSkillResolver.cs" \
    'public sealed class OrnnExactAgentProfileSkillResolver {' \
    '  public async Task<ExactOrnnSkillResolutionResult> ResolveAsync(CancellationToken ct = default) {' \
    '    Entry:' \
    '    throw new InvalidOperationException();' \
    '    var detailRead = await _client.GetExactSkillDetailAsync(token, guid, version, ct);' \
    '    var jsonRead = await _client.GetExactSkillJsonAsync(token, guid, version, ct);' \
    '  } }'
  expect_fail "exact adapter labeled throw-unreachable tail decoys" "${case_root}" \
    "structurally valid"

  fresh_case "adapter-goto-unreachable-tail-decoy"
  write_lines "${case_root}/src/Aevatar.AI.ToolProviders.Ornn/AgentProfiles/OrnnExactAgentProfileSkillResolver.cs" \
    'public sealed class OrnnExactAgentProfileSkillResolver {' \
    '  public async Task<ExactOrnnSkillResolutionResult> ResolveAsync(CancellationToken ct = default) {' \
    '    goto AfterReads;' \
    '    var detailRead = await _client.GetExactSkillDetailAsync(token, guid, version, ct);' \
    '    var jsonRead = await _client.GetExactSkillJsonAsync(token, guid, version, ct);' \
    '    AfterReads:' \
    '    return default!;' \
    '  } }'
  expect_fail "exact adapter goto-unreachable tail decoys" "${case_root}" \
    "structurally valid"

  fresh_case "adapter-pragma-goto-unreachable-tail-decoy"
  write_lines "${case_root}/src/Aevatar.AI.ToolProviders.Ornn/AgentProfiles/OrnnExactAgentProfileSkillResolver.cs" \
    'public sealed class OrnnExactAgentProfileSkillResolver {' \
    '  public async Task<ExactOrnnSkillResolutionResult> ResolveAsync(CancellationToken ct = default) {' \
    '    #pragma warning disable CS0162' \
    '    goto AfterReads;' \
    '    var detailRead = await _client.GetExactSkillDetailAsync(token, guid, version, ct);' \
    '    var jsonRead = await _client.GetExactSkillJsonAsync(token, guid, version, ct);' \
    '    #pragma warning restore CS0162' \
    '    AfterReads:' \
    '    return default!;' \
    '  } }'
  expect_fail "exact adapter pragma-hidden goto-unreachable tail decoys" "${case_root}" \
    "structurally valid"

  fresh_case "adapter-labeled-goto-unreachable-tail-decoy"
  write_lines "${case_root}/src/Aevatar.AI.ToolProviders.Ornn/AgentProfiles/OrnnExactAgentProfileSkillResolver.cs" \
    'public sealed class OrnnExactAgentProfileSkillResolver {' \
    '  public async Task<ExactOrnnSkillResolutionResult> ResolveAsync(CancellationToken ct = default) {' \
    '    Entry:' \
    '    goto AfterReads;' \
    '    var detailRead = await _client.GetExactSkillDetailAsync(token, guid, version, ct);' \
    '    var jsonRead = await _client.GetExactSkillJsonAsync(token, guid, version, ct);' \
    '    AfterReads:' \
    '    return default!;' \
    '  } }'
  expect_fail "exact adapter labeled goto-unreachable tail decoys" "${case_root}" \
    "structurally valid"

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

  fresh_case "adapter-banned-method-strings"
  printf '%s\n' \
    'private const string Diagnostic = "GetSkillJsonAsync SearchSkillsAsync GetSkillSetAsync IRemoteSkillFetcher";' \
    >> "${case_root}/src/Aevatar.AI.ToolProviders.Ornn/AgentProfiles/OrnnExactAgentProfileSkillResolver.cs"
  expect_pass "exact adapter banned method names inside C# strings" "${case_root}"

  local ingress_label="" ingress_file="" ingress_probe=""
  while IFS='|' read -r ingress_label ingress_file ingress_probe; do
    printf '%s\n' "${ingress_probe}" >> "${dependency_case_root}/${ingress_file}"
  done <<'CASES'
lookup-service|src/platform/Aevatar.GAgentService.Application/AgentProfiles/AgentProfileLookupService.cs|public sealed class AgentProfileLookupService { private readonly IActorRuntime _runtime; }
actor-runtime|src/platform/Aevatar.GAgentService.Application/AgentProfiles/AgentProfileLookupService.cs|public sealed class AgentProfileLookupService { private readonly ActorRuntime _runtime; }
prefixed-actor-runtime|src/platform/Aevatar.GAgentService.Application/AgentProfiles/AgentProfileLookupService.cs|public sealed class AgentProfileLookupService { private readonly OrleansActorRuntime _runtime; }
projection|src/platform/Aevatar.GAgentService.Projection/AgentProfiles/AgentProfileProjector.cs|private Task ProjectionActivation();
host-endpoint|src/platform/Aevatar.GAgentService.Hosting/AgentProfiles/AgentProfileEndpoints.cs|private Task PrimeAsync();
tool|src/Aevatar.AI.ToolProviders.AgentCatalog/AgentProfiles/AgentProfilesToolSource.cs|private readonly IEventStore _eventStore;
file-event-store|src/Aevatar.AI.ToolProviders.AgentCatalog/AgentProfiles/AgentProfilesToolSource.cs|private readonly FileEventStore _eventStore;
event-store|src/Aevatar.AI.ToolProviders.AgentCatalog/AgentProfiles/AgentProfilesToolSource.cs|private readonly EventStore _eventStore;
prefixed-event-store|src/Aevatar.AI.ToolProviders.AgentCatalog/AgentProfiles/AgentProfilesToolSource.cs|private readonly GarnetEventStore _eventStore;
replay-async|src/platform/Aevatar.GAgentService.Hosting/AgentProfiles/AgentProfileEndpoints.cs|private Task ReplayAsync();
event-replay|src/platform/Aevatar.GAgentService.Hosting/AgentProfiles/AgentProfileEndpoints.cs|private readonly EventReplay _eventReplay;
CASES

  local schema_alias=""
  local schema_aliases=(
    scopeId subjectId systemAuthority owner_id ownerSubject profileId platformId
    sealed_content sealedContent credential accessToken api_key apiKey cookie
    password authorization secret clientSecret oauth_code oauthCode caller_scope_id
    session_cookie api_secret
  )
  write_tool_schema \
    "${dependency_case_root}/src/Aevatar.AI.ToolProviders.AgentCatalog/AgentProfiles/AgentProfilesTool.cs" \
    "${schema_aliases[@]}"
  expect_fail_cases "remote, exact-reference, query-ingress, and schema violations" \
    "${dependency_case_root}" \
    "Core/Projection remote dependency http|HttpClient _client|Agent Profile Core/Projection must not depend on Ornn, HTTP, remote fetchers, or skill-search/name lookup paths." \
    "Core/Projection remote dependency ornn|OrnnSkillClient _client|Agent Profile Core/Projection must not depend on Ornn, HTTP, remote fetchers, or skill-search/name lookup paths." \
    "Core/Projection remote dependency name-fetch|ProjectionOnly|Agent Profile Core/Projection must not depend on Ornn, HTTP, remote fetchers, or skill-search/name lookup paths." \
    "Core/Projection remote dependency search|SearchSkillsAsync|Agent Profile Core/Projection must not depend on Ornn, HTTP, remote fetchers, or skill-search/name lookup paths." \
    "Application exact-reference name-or-id|nameOrId|Agent Profile Application accepts exact skill references only; name/latest/inline lookup and name-capable fetchers are forbidden." \
    "Application exact-reference name-or-id-snake|name_or_id|Agent Profile Application accepts exact skill references only; name/latest/inline lookup and name-capable fetchers are forbidden." \
    "Application exact-reference latest-identifier-bare|string latest =|Agent Profile Application accepts exact skill references only; name/latest/inline lookup and name-capable fetchers are forbidden." \
    "Application exact-reference latest-identifier-camel|latestVersion|Agent Profile Application accepts exact skill references only; name/latest/inline lookup and name-capable fetchers are forbidden." \
    "Application exact-reference latest-identifier-snake|latest_version|Agent Profile Application accepts exact skill references only; name/latest/inline lookup and name-capable fetchers are forbidden." \
    "Application exact-reference latest-contract|Version = \"latest\"|Agent Profile Application accepts exact skill references only; name/latest/inline lookup and name-capable fetchers are forbidden." \
    "Application exact-reference inline-skill|inlineSkill|Agent Profile Application accepts exact skill references only; name/latest/inline lookup and name-capable fetchers are forbidden." \
    "Application exact-reference inline-skill-snake|inline_skill|Agent Profile Application accepts exact skill references only; name/latest/inline lookup and name-capable fetchers are forbidden." \
    "Application exact-reference name-fetch|ApplicationOnly|Agent Profile Application accepts exact skill references only; name/latest/inline lookup and name-capable fetchers are forbidden." \
    "query ingress lookup-service|IActorRuntime _runtime|Agent Profile query and ingress surfaces must read materialized models only; activation, runtime, event-store, replay, and priming APIs are forbidden." \
    "query ingress actor-runtime|private readonly ActorRuntime _runtime|Agent Profile query and ingress surfaces must read materialized models only; activation, runtime, event-store, replay, and priming APIs are forbidden." \
    "query ingress prefixed-actor-runtime|OrleansActorRuntime _runtime|Agent Profile query and ingress surfaces must read materialized models only; activation, runtime, event-store, replay, and priming APIs are forbidden." \
    "query ingress projection|ProjectionActivation|Agent Profile query and ingress surfaces must read materialized models only; activation, runtime, event-store, replay, and priming APIs are forbidden." \
    "query ingress host-endpoint|PrimeAsync|Agent Profile query and ingress surfaces must read materialized models only; activation, runtime, event-store, replay, and priming APIs are forbidden." \
    "query ingress tool|IEventStore _eventStore|Agent Profile query and ingress surfaces must read materialized models only; activation, runtime, event-store, replay, and priming APIs are forbidden." \
    "query ingress file-event-store|FileEventStore _eventStore|Agent Profile query and ingress surfaces must read materialized models only; activation, runtime, event-store, replay, and priming APIs are forbidden." \
    "query ingress event-store|private readonly EventStore _eventStore|Agent Profile query and ingress surfaces must read materialized models only; activation, runtime, event-store, replay, and priming APIs are forbidden." \
    "query ingress prefixed-event-store|GarnetEventStore _eventStore|Agent Profile query and ingress surfaces must read materialized models only; activation, runtime, event-store, replay, and priming APIs are forbidden." \
    "query ingress replay-async|ReplayAsync|Agent Profile query and ingress surfaces must read materialized models only; activation, runtime, event-store, replay, and priming APIs are forbidden." \
    "query ingress event-replay|EventReplay _eventReplay|Agent Profile query and ingress surfaces must read materialized models only; activation, runtime, event-store, replay, and priming APIs are forbidden." \
    "tool schema aliases|forbidden schema properties|The agent_profiles tool schema must not accept owner subjects/ids, scope/Profile ids, system authority, sealed content, or credentials."
  for schema_alias in "${schema_aliases[@]}"; do
    if ! printf '%s\n' "${output}" | rg -q -F -x -- "${schema_alias}"; then
      echo "agent_profile_boundary_guard self-test missed schema alias: ${schema_alias}" >&2
      echo "${output}" >&2
      failures=$((failures + 1))
    fi
  done

  if (( failures > 0 )); then
    return 1
  fi

  echo "Agent Profile Phase 1 boundary guard self-tests passed."
}

case "${1:-}" in
  "")
    build_authority_syntax_checker || exit $?
    run_guard_with_authority_syntax "${REPO_ROOT}"
    ;;
  --self-test)
    (( $# == 1 )) || { echo "Usage: $0 --self-test" >&2; exit 2; }
    build_authority_syntax_checker || exit $?
    run_self_tests
    ;;
  --scan-root)
    (( $# == 2 )) || { echo "Usage: $0 --scan-root <repository-root>" >&2; exit 2; }
    build_authority_syntax_checker || exit $?
    run_guard_with_authority_syntax "$2"
    ;;
  *)
    echo "Usage: $0 [--self-test|--scan-root <repository-root>]" >&2
    exit 2
    ;;
esac

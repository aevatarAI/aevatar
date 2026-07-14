local C = {}
local config = require("devloop.config")

local error_facts = require("contract.error_facts")
local forge_validators = require("devloop.forge_validators")
local base_ids = require("devloop.base_ids")
local strings = require("contract.strings")
local transition_version = require("contract.transition_version")

function C.read_env_command(name)
  return config.read_env_command(name)
end
function C.read_env(name, exec)
  return config.read_env(name, exec)
end

local max_key_len = 200
local max_dedup_len = 512
local max_title_len = 240
local max_body_len = 12000
local max_comments_len = 12000
local max_meta_reason_len = 2000
local max_framing_len = 1000
local max_impl_output_len = 2000
local max_blocking_gap_len = 240
local max_review_ledger_len = 1200
local max_pr_issue_context_len = 3000
local max_update_key_len = 50
local max_version_key_len = 40
local max_worktree_prefix_len = 90
local max_branch_len = 160
local max_pr_title_len = 240
local max_judgment_prefix_len = 120
local action_label = "⟦FKST:ACTION⟧"
local intake_label = "⟦FKST:INTAKE⟧"
local class_label = "⟦FKST:CLASS⟧"
local reason_label = "⟦FKST:REASON⟧"
local verdict_label = "⟦FKST:VERDICT⟧"
local reply_label = "⟦FKST:REPLY⟧"
local untrusted_issue_data_begin = "BEGIN UNTRUSTED ISSUE DATA"
local untrusted_issue_data_end = "END UNTRUSTED ISSUE DATA"
local test_bot_login = "fkst-test-bot"

local enabled_label = "fkst-dev:enabled"
local tracking_label = "fkst-dev:tracking"
local hold_label = "fkst-dev:hold"
local thinking_label = "fkst-dev:thinking"
local ready_label = "fkst-dev:ready"
local implementing_label = "fkst-dev:implementing"
local awaiting_pr_label = "fkst-dev:awaiting-pr"
local pr_open_label = "fkst-dev:pr-open"
local reviewing_label = "fkst-dev:reviewing"
local merge_ready_label = "fkst-dev:merge-ready"
local merging_label = "fkst-dev:merging"
local merged_label = "fkst-dev:merged"
local fixing_label = "fkst-dev:fixing"
local review_meta_label = "fkst-dev:review-meta"
local impl_failed_label = "fkst-dev:impl-failed"
local blocked_label = "fkst-dev:blocked"
local blocked_on_dependency_label = "fkst-dev:blocked-on-dependency"

local label_colors = {
  [enabled_label] = "1D76DB",
  [tracking_label] = "C5DEF5",
  [thinking_label] = "8250DF",
  [ready_label] = "0E8A16",
  [implementing_label] = "FBCA04",
  [pr_open_label] = "006B75",
  [reviewing_label] = "5319E7",
  [merge_ready_label] = "2EA44F",
  [merging_label] = "C2E0C6",
  [merged_label] = "8957E5",
  [fixing_label] = "D93F0B",
  [review_meta_label] = "BFD4F2",
  [impl_failed_label] = "B60205",
  [blocked_label] = "1B1F23",
  [blocked_on_dependency_label] = "E99695",
}

function C.parse_name_only_paths(stdout)
  local paths = {}
  local seen = {}
  for line in tostring(stdout or ""):gmatch("[^\r\n]+") do
    local path = line:gsub("^%s+", ""):gsub("%s+$", "")
    if path ~= "" and not seen[path] then
      table.insert(paths, path)
      seen[path] = true
    end
  end
  table.sort(paths)
  return paths
end

local trusted_bot_login_current = nil
local comment_body
local comment_author_login
local is_trusted_comment

local function shell_single_quote(value)
  return "'" .. tostring(value):gsub("'", "'\\''") .. "'"
end

local function trim(value)
  return tostring(value or ""):gsub("^%s+", ""):gsub("%s+$", "")
end

local function neutralize_fkst_markers(value)
  local neutralized = tostring(value or ""):gsub("<!%-%- fkst:", "&lt;!-- fkst:")
  return neutralized
end

local one_line = error_facts.one_line

local is_bounded_string = strings.is_bounded_string
local decimal_checksum = strings.decimal_checksum


local function has_value(values, expected)
  if type(values) ~= "table" then
    return false
  end
  for _, value in ipairs(values) do
    if value == expected then
      return true
    end
  end
  return false
end

local function is_review_meta_action(value)
  return value == "fix"
    or value == "block"
    or value == "spec-amendment"
    or value == "continue"
    or value == "spec-gap"
end

local function fix_reflection_checkpoint_round()
  return 3
end

local is_path_safe_key = strings.is_path_safe_key

-- A GitHub App's author login is "<slug>[bot]" via the REST API but bare
-- "<slug>" via GraphQL. Strip the suffix so callers comparing against a
-- configured bot login match regardless of which API populated the field.
-- Nil-safe (nil in → nil out) and a no-op for ordinary user logins (which never
-- end in "[bot]"), so claim_owner() and author comparisons keep their existing
-- nil semantics when the bot login is unconfigured.
function C.strip_bot_login_suffix(login)
  if login == nil then
    return nil
  end
  return (tostring(login):gsub("%[bot%]$", ""))
end

function C.configure_trusted_bot_login(login)
  if login == nil or tostring(login) == "" then
    trusted_bot_login_current = nil
    return nil
  end
  trusted_bot_login_current = C.strip_bot_login_suffix(login)
  return trusted_bot_login_current
end

function C.assert_trusted_bot_configured()
  local login = C.read_env("FKST_GITHUB_BOT_LOGIN")
  if login ~= nil then
    C.configure_trusted_bot_login(login)
  end

  if C.read_env("FKST_GITHUB_WRITE") == "1" and trusted_bot_login_current == nil then
    error("github-devloop: FKST_GITHUB_BOT_LOGIN is required when FKST_GITHUB_WRITE=1")
  end
  return trusted_bot_login_current
end

local dedup_key = base_ids.dedup_key

function C.safe_updated_at(M, updated_at)
  local safe = strings.sanitize_key(updated_at, max_key_len):sub(1, max_update_key_len):gsub("/+$", "")
  if safe == "" then
    return "empty"
  end
  return safe
end

function C.safe_pr_review_repo_segment(repo)
  local safe = base_ids.safe_repo(repo):gsub("/", "-"):gsub("%-+", "-")
  safe = safe:gsub("^%-+", ""):gsub("%-+$", "")
  if safe == "" then
    safe = "repo"
  end
  local suffix = "-" .. decimal_checksum(repo)
  local limit = 48
  if #safe > limit or safe:sub(-#suffix) ~= suffix then
    safe = safe:sub(1, limit - #suffix):gsub("%-+$", "") .. suffix
  end
  return safe
end

function C.is_opted_in(labels)
  if type(labels) ~= "table" then
    return false
  end

  for _, label in ipairs(labels) do
    if tostring(label) == enabled_label then
      return true
    end
  end
  return false
end

function C.is_intake_held(labels)
  return has_value(labels, hold_label)
end

function C.safe_head_segment(head_sha)
  if not forge_validators.is_git_sha(head_sha) then
    error("github-devloop: invalid head sha")
  end
  return tostring(head_sha)
end

function C.pr_review_proposal_id(repo, pr_number, version, head_sha)
  if not forge_validators.is_positive_pr_number(pr_number) then
    error("github-devloop: invalid pr number")
  end
  if head_sha == nil then
    error("github-devloop: missing reviewed head sha")
  end
  return "github-devloop/pr-review/"
    .. C.safe_pr_review_repo_segment(repo)
    .. "/"
    .. base_ids.safe_issue(pr_number)
    .. "/"
    .. transition_version.safe_version_segment(version)
    .. "/"
    .. C.safe_head_segment(head_sha)
end

function C.parse_pr_review_proposal_id(id)
  if type(id) ~= "string" then
    return nil
  end

  local rest = id:match("^github%-devloop/pr%-review/(.+)$")
  if rest == nil then
    return nil
  end

  local head_sha = rest:match("/([^/]+)$")
  local without_head = head_sha and rest:sub(1, #rest - #head_sha - 1) or nil
  local version = without_head and without_head:match("/([^/]+)$") or nil
  local without_version = version and without_head:sub(1, #without_head - #version - 1) or nil
  local pr_number = without_version and without_version:match("/([^/]+)$") or nil
  local repo = pr_number and without_version:sub(1, #without_version - #pr_number - 1) or nil
  if repo == nil or repo == "" or pr_number == nil or pr_number == "" or version == nil or version == "" or head_sha == nil or head_sha == "" then
    return nil
  end
  if not forge_validators.is_positive_pr_number(pr_number) then
    return nil
  end
  if not forge_validators.is_git_sha(head_sha) then
    return nil
  end
  if not is_path_safe_key(repo, 64)
    or base_ids.safe_issue(pr_number) ~= pr_number
    or transition_version.safe_version_segment(version) ~= version
    or C.safe_head_segment(head_sha) ~= head_sha then
    return nil
  end
  return repo, pr_number, version, head_sha
end

function C.parse_pr_source_ref(source_ref)
  if type(source_ref) ~= "table" or source_ref.kind ~= "external" then
    return nil
  end
  local ref = tostring(source_ref.ref or "")
  local pr_number = ref:match("#pr/(%d+)$")
  local repo = pr_number and ref:sub(1, #ref - #("#pr/" .. pr_number)) or nil
  if repo == nil or repo == "" or not forge_validators.is_positive_pr_number(pr_number) then
    return nil
  end
  if base_ids.safe_repo(repo) == "" then
    return nil
  end
  return repo, pr_number
end

function C.parse_issue_source_ref(source_ref)
  if type(source_ref) ~= "table" or source_ref.kind ~= "external" then
    return nil
  end
  local ref = tostring(source_ref.ref or "")
  local issue_number = ref:match("#issue/(%d+)$")
  local repo = issue_number and ref:sub(1, #ref - #("#issue/" .. issue_number)) or nil
  if repo == nil or repo == "" or not forge_validators.is_positive_pr_number(issue_number) then
    return nil
  end
  if not base_ids.issue_ref_round_trips(repo, issue_number) then
    return nil
  end
  return repo, issue_number
end

function C.is_safe_proposal_ref(proposal_id, dedup_key)
  if not is_path_safe_key(proposal_id, max_key_len) then
    return false
  end
  if not is_path_safe_key(dedup_key, max_dedup_len) then
    return false
  end

  local repo, issue_number = base_ids.parse_proposal_id(proposal_id)
  if repo == nil or issue_number == nil then
    return false
  end
  return base_ids.issue_ref_round_trips(repo, issue_number)
end

function C.is_safe_consensus_result_ref(proposal_id, dedup_key)
  if not is_path_safe_key(proposal_id, max_key_len) then
    return false
  end
  if not is_bounded_string(dedup_key, max_dedup_len) then
    return false
  end

  local inner_dedup_key = dedup_key:match("^consensus:(.+)$") or dedup_key
  if not is_path_safe_key(inner_dedup_key, max_dedup_len) then
    return false
  end

  local repo, issue_number = base_ids.parse_proposal_id(proposal_id)
  if repo == nil or issue_number == nil then
    return false
  end
  return base_ids.issue_ref_round_trips(repo, issue_number)
end

function C.is_safe_pr_review_result_ref(proposal_id, dedup_key)
  if not is_path_safe_key(proposal_id, max_key_len) then
    return false
  end
  if not is_bounded_string(dedup_key, max_dedup_len) then
    return false
  end

  local inner_dedup_key = dedup_key:match("^consensus:(.+)$") or dedup_key
  if not is_path_safe_key(inner_dedup_key, max_dedup_len) then
    return false
  end

  local repo, pr_number = C.parse_pr_review_proposal_id(proposal_id)
  return repo ~= nil and pr_number ~= nil
end

function C.proposal_dedup_key(proposal_id, updated_at)
  return tostring(proposal_id) .. "/" .. C.safe_updated_at(M, updated_at)
end

function C.intake_dedup_key(M, proposal_id, updated_at)
  return dedup_key({
    "intake",
    tostring(proposal_id),
    C.safe_updated_at(M, updated_at or "unknown"),
  })
end

function C.intake_candidate_delivery_dedup_key(M, proposal_id, effect_id, delivery_version)
  return dedup_key({
    "intake-candidate",
    tostring(proposal_id),
    tostring(effect_id),
    C.safe_updated_at(M, delivery_version or "unknown"),
  })
end

function C.implement_version_mismatch_key(expected_version, current_version)
  return dedup_key({
    "ivm",
    decimal_checksum(table.concat({
      "expected=" .. tostring(expected_version or ""),
      "current=" .. tostring(current_version or ""),
    }, "\n")),
  })
end

function C.intake_decision_dedup_key(proposal_id, current, reintake_command)
  local reintake_created_at = "none"
  if reintake_command ~= nil then
    reintake_created_at = tostring(reintake_command.created_at or "unknown")
  end
  return dedup_key({
    tostring(proposal_id),
    "intake",
    decimal_checksum(table.concat({
      "title=" .. tostring(current and current.title or ""),
      "body=" .. tostring(current and current.body or ""),
      "reintake_created_at=" .. reintake_created_at,
    }, "\n")),
  })
end

function C.ci_selfheal_once_key(M, repo, pr_number, head_sha)
  return dedup_key({
    "github-devloop",
    "ci-selfheal",
    base_ids.safe_repo(repo),
    "pr",
    base_ids.safe_issue(pr_number),
    C.safe_head_segment(head_sha),
  })
end

function C.ci_missing_status_first_observed_key(M, repo, pr_number, head_sha)
  return dedup_key({
    "github-devloop",
    "ci-missing-status-observed",
    base_ids.safe_repo(repo),
    "pr",
    base_ids.safe_issue(pr_number),
    C.safe_head_segment(head_sha),
  })
end

function C.observe_lock_key(repo, issue_number)
  return "github-devloop/transition/" .. base_ids.safe_repo(repo) .. "/issue/" .. base_ids.safe_issue(issue_number)
end

function C.transition_lock_key(proposal_id)
  local repo, issue_number = base_ids.parse_proposal_id(proposal_id)
  if repo == nil then
    return nil
  end
  return C.observe_lock_key(repo, issue_number)
end

function C.result_lock_key(proposal_id)
  return C.transition_lock_key(proposal_id)
end

function C.review_result_lock_key(issue_proposal_id)
  return C.transition_lock_key(issue_proposal_id)
end

function C.review_lock_key(proposal_id)
  return C.transition_lock_key(proposal_id)
end

function C.loop_lock_key(proposal_id)
  return C.transition_lock_key(proposal_id)
end

function C.implement_lock_key(proposal_id)
  return C.transition_lock_key(proposal_id)
end

function C.safe_issue_slug(repo, issue_number)
  local slug = strings.sanitize_key(tostring(repo or "") .. "-" .. tostring(issue_number or ""), false):gsub("/", "-")
  slug = slug:gsub("%-+", "-"):gsub("^%-+", ""):gsub("%-+$", "")
  if slug == "" then
    slug = "issue"
  end
  if #slug > max_worktree_prefix_len then
    slug = slug:sub(1, max_worktree_prefix_len):gsub("%-+$", "")
  end
  if slug == "" then
    return "issue"
  end
  return slug
end

function C.implement_branch(repo, issue_number, impl_version)
  local safe_repo = base_ids.safe_repo(repo)
  local safe_issue = base_ids.safe_issue(issue_number)
  local safe_version = strings.sanitize_key(impl_version, false):gsub("[/#]", "-"):gsub("%-+", "-")
  safe_version = safe_version:gsub("^%-+", ""):gsub("%-+$", ""):gsub("%.+$", "")
  if safe_version == "" then
    safe_version = "version"
  end

  local prefix = "devloop/issue/" .. safe_repo .. "/" .. safe_issue .. "/"
  local suffix = "-" .. decimal_checksum(tostring(repo) .. "#" .. tostring(issue_number) .. "#" .. tostring(impl_version))
  local version_limit = max_branch_len - #prefix - #suffix
  if version_limit < 12 then
    version_limit = 12
  end
  if #safe_version > version_limit then
    safe_version = safe_version:sub(1, version_limit):gsub("%-+$", ""):gsub("%.+$", "")
  end
  if safe_version == "" then
    safe_version = "version"
  end

  local branch = prefix .. safe_version .. suffix
  if not forge_validators.is_git_ref_safe(branch) or #branch > max_branch_len then
    error("github-devloop: invalid deterministic implementation branch")
  end
  return branch
end

function C.implement_worktree_path(runtime_root, repo, issue_number, impl_version)
  local root = trim(runtime_root)
  if root == "" or root:find("[\r\n]") ~= nil then
    error("github-devloop: invalid FKST_RUNTIME_ROOT")
  end
  local slug = C.safe_issue_slug(repo, issue_number)
  local suffix = decimal_checksum(tostring(repo) .. "#" .. tostring(issue_number) .. "#" .. tostring(impl_version))
  return root:gsub("/+$", "") .. "/worktrees/devloop-" .. slug .. "-" .. suffix
end

function C.path_under_runtime_root(runtime_root, path)
  local root = trim(runtime_root)
  local target = trim(path)
  if root == "" or root:find("[\r\n]") ~= nil then
    error("github-devloop: invalid FKST_RUNTIME_ROOT")
  end
  if target == "" or target:find("[\r\n]") ~= nil then
    return false
  end
  root = root:gsub("/+$", "")
  target = target:gsub("/+$", "")
  return target == root or target:sub(1, #root + 1) == root .. "/"
end

function C.read_runtime_root_cmd()
  return 'printf %s "$FKST_RUNTIME_ROOT"'
end

function C.mkdir_p_cmd(path)
  local value = tostring(path or "")
  if value == "" or value:find("[\r\n]") ~= nil then
    error("github-devloop: invalid directory path")
  end
  return "mkdir -p " .. shell_single_quote(value)
end

function C.judgment_worktree_path(runtime_root, role, identity)
  local root = trim(runtime_root)
  if root == "" or root:find("[\r\n]") ~= nil then
    error("github-devloop: invalid FKST_RUNTIME_ROOT")
  end
  local slug = strings.sanitize_key(tostring(role or "") .. "-" .. tostring(identity or ""), false):gsub("/", "-")
  slug = slug:gsub("%-+", "-"):gsub("^%-+", ""):gsub("%-+$", ""):gsub("%.+$", "")
  if slug == "" then
    slug = "judgment"
  end
  if #slug > max_judgment_prefix_len then
    slug = slug:sub(1, max_judgment_prefix_len):gsub("%-+$", ""):gsub("%.+$", "")
  end
  if slug == "" then
    slug = "judgment"
  end
  local suffix = decimal_checksum(tostring(role) .. "#" .. tostring(identity))
  return root:gsub("/+$", "") .. "/judgment-worktrees/github-devloop-" .. slug .. "-" .. suffix
end

function C.judgment_worktree_with_exec(exec_sync_fn, role, identity)
  local runtime = exec_sync_fn({ cmd = C.read_runtime_root_cmd(), timeout = 30 })
  if runtime.exit_code ~= 0 then
    error("github-devloop: FKST_RUNTIME_ROOT read failed: " .. tostring(runtime.stderr))
  end
  local worktree = C.judgment_worktree_path(runtime.stdout, role, identity)
  local mkdir = exec_sync_fn({ cmd = C.mkdir_p_cmd(worktree), timeout = 30 })
  if mkdir.exit_code ~= 0 then
    error("github-devloop: judgment scratch directory setup failed: " .. tostring(mkdir.stderr))
  end
  return worktree
end


function C.max_body_len(M)
  return max_body_len
end

function C.render_template(template, vars)
  if type(template) ~= "string" then
    error("github-devloop: template must be a string")
  end
  if type(vars) ~= "table" then
    error("github-devloop: template vars must be a table")
  end

  return (template:gsub("{{([%w_]+)}}", function(name)
    local value = vars[name]
    if value == nil then
      error("github-devloop: missing template var " .. name)
    end
    return tostring(value)
  end))
end

function C.neutralize_untrusted_prompt_text(text)
  local value = tostring(text or "")

  local function neutralize_line(line)
    local sentinel_line = line:match("^%s*[+%- ]?%s*(.+)$") or line
    if sentinel_line:match("^%s*" .. action_label) ~= nil
      or sentinel_line:match("^%s*" .. reason_label) ~= nil
      or sentinel_line:match("^%s*" .. intake_label) ~= nil
      or sentinel_line:match("^%s*" .. class_label) ~= nil
      or sentinel_line:match("^%s*" .. verdict_label) ~= nil
      or sentinel_line:match("^%s*" .. reply_label) ~= nil
      or trim(line) == untrusted_issue_data_begin
      or trim(line) == untrusted_issue_data_end
      or trim(sentinel_line) == untrusted_issue_data_begin
      or trim(sentinel_line) == untrusted_issue_data_end
      or line:find("<!%-%- fkst:") ~= nil
      or line:find("&lt;!%-%- fkst:") ~= nil then
      return "> " .. line
    end
    return line
  end

  local output = {}
  local start = 1
  while true do
    local newline = value:find("\n", start, true)
    if newline == nil then
      table.insert(output, neutralize_line(value:sub(start)))
      break
    end

    table.insert(output, neutralize_line(value:sub(start, newline - 1)))
    table.insert(output, "\n")
    start = newline + 1
  end

  return table.concat(output)
end

function C.quote_untrusted_prompt_text(M, text)
  local value = neutralize_fkst_markers(text)
  local output = {}
  local start = 1
  while true do
    local newline = value:find("\n", start, true)
    if newline == nil then
      table.insert(output, "> " .. value:sub(start))
      break
    end

    table.insert(output, "> " .. value:sub(start, newline - 1))
    table.insert(output, "\n")
    start = newline + 1
  end

  return table.concat(output)
end

function C.neutralize_untrusted_comment_text(text)
  local value = tostring(text or "")

  local function neutralize_line(line)
    if line:find("<!-- fkst:", 1, true) ~= nil then
      return neutralize_fkst_markers(line)
    end
    return line
  end

  local output = {}
  local start = 1
  while true do
    local newline = value:find("\n", start, true)
    if newline == nil then
      table.insert(output, neutralize_line(value:sub(start)))
      break
    end

    table.insert(output, neutralize_line(value:sub(start, newline - 1)))
    table.insert(output, "\n")
    start = newline + 1
  end

  return table.concat(output)
end

function C.gh_exec_opts(M, cmd_or_opts, timeout)
  local opts = {}
  if type(cmd_or_opts) == "table" then
    for key, value in pairs(cmd_or_opts) do
      opts[key] = value
    end
  else
    opts.cmd = cmd_or_opts
  end
  opts.timeout = opts.timeout or timeout or 30
  return opts
end

function C.trusted_bot_login()
  return trusted_bot_login_current or test_bot_login
end

C._max_key_len = max_key_len
C._max_dedup_len = max_dedup_len
C._max_title_len = max_title_len
C._max_body_len = max_body_len
C._max_comments_len = max_comments_len
C._max_meta_reason_len = max_meta_reason_len
C._max_framing_len = max_framing_len
C._max_impl_output_len = max_impl_output_len
C._max_blocking_gap_len = max_blocking_gap_len
C._max_review_ledger_len = max_review_ledger_len
C._max_pr_issue_context_len = max_pr_issue_context_len
C._max_pr_title_len = max_pr_title_len
C._action_label = action_label
C._intake_label = intake_label
C._class_label = class_label
C._reason_label = reason_label
C._verdict_label = verdict_label
C._reply_label = reply_label
C._untrusted_issue_data_begin = untrusted_issue_data_begin
C._untrusted_issue_data_end = untrusted_issue_data_end
C._test_bot_login = test_bot_login
C._enabled_label = enabled_label
C._tracking_label = tracking_label
C._hold_label = hold_label
C._thinking_label = thinking_label
C._ready_label = ready_label
C._implementing_label = implementing_label
C._awaiting_pr_label = awaiting_pr_label
C._pr_open_label = pr_open_label
C._reviewing_label = reviewing_label
C._merge_ready_label = merge_ready_label
C._merging_label = merging_label
C._merged_label = merged_label
C._fixing_label = fixing_label
C._review_meta_label = review_meta_label
C._impl_failed_label = impl_failed_label
C._blocked_label = blocked_label
C._blocked_on_dependency_label = blocked_on_dependency_label
C._label_colors = label_colors
C._shell_single_quote = shell_single_quote
C._neutralize_fkst_markers = neutralize_fkst_markers
C._one_line = one_line
C._is_bounded_string = is_bounded_string
C._has_value = has_value
C._is_review_meta_action = is_review_meta_action
C.fix_reflection_checkpoint_round = fix_reflection_checkpoint_round
C._is_path_safe_key = is_path_safe_key
C._is_positive_pr_number = forge_validators.is_positive_pr_number

return C

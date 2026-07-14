local M = {}
local codex = require("workflow.codex")
local env = require("workflow.env")
local error_facts = require("contract.error_facts")
local strings = require("contract.strings")


local default_angles = { "minimal", "structural", "delete" }
local max_angles = 4
local max_key_len = 200
local max_title_len = 240
local max_body_len = 12000
local max_context_len = 8000
local max_content_fetch_len = 4000
local max_reply_len = 2000
local max_framing_len = 1000
local max_gap_len = 240
local max_gaps = 4
local max_narrowed_question_len = 2000
local max_digest_len = 600
local max_prior_round_digests = 12
local max_scratch_slug_len = 120
local stale_generation_context_error_class = "stale_generation_context"
local verdict_label = "⟦FKST:VERDICT⟧"
local reply_label = "⟦FKST:REPLY⟧"
local gap_label = "⟦FKST:GAP⟧"
local allowed_env = {
  FKST_OUTPUT_LANG = true,
}

local function read_env_command(name)
  if not allowed_env[name] then
    error("consensus: env name is not allowed")
  end
  return 'printf %s "$' .. name .. '"'
end
M.read_env_command = read_env_command
M.read_env = env.read_env(read_env_command)
function M.error_fingerprint(error_class, queue, dept, message)
  return error_facts.error_fingerprint(error_class, queue, dept, message)
end
function M.error_class_from_message(message)
  local text = tostring(message or "")
  local class = text:match("consensus: ([%w%-]+):")
    or text:match("consensus: ([%w%-]+) failed:")
  return class or "caught-failure"
end
function M.log_error_fact(level, dept, tag, error_class, queue, message, context)
  local fields = error_facts.error_fact_fields(error_class, queue, dept, message, context)
  table.insert(fields, "queue=" .. error_facts.one_line(queue))
  table.insert(fields, "error=" .. error_facts.one_line(message))
  log[level or "warn"]("consensus dept=" .. error_facts.one_line(dept) .. " tag=" .. error_facts.one_line(tag or "FAILURE") .. " " .. table.concat(fields, " "))
end
local event_source_ref = error_facts.event_source_ref
function M.wrap_pipeline_failure(dept, fn)
  return function(event)
    local ok, err = pcall(fn, event)
    if ok then
      return err
    end
    M.log_error_fact("error", dept, "FAILURE", M.error_class_from_message(err), type(event) == "table" and event.queue or nil, err, {
      source_ref = event_source_ref(event),
      attempt = type(event) == "table" and event.attempt or nil,
    })
    error(err, 0)
  end
end
function M.verdict_mode(proposal)
  if type(proposal) == "table" and proposal.verdict_mode == "gate" then
    return "gate"
  end
  return "converge"
end
local function trim(value)
  return tostring(value or ""):gsub("^%s+", ""):gsub("%s+$", "")
end
local is_bounded_string = strings.is_bounded_string
local is_path_safe_key = strings.is_path_safe_key
local function manifest_paths(manifest)
  local paths = {}
  for line in (tostring(manifest or "") .. "\n"):gmatch("([^\n]*)\n") do
    local path = line:match(":%s*(/.+)%s*$")
    if path ~= nil then
      table.insert(paths, path)
    end
  end
  return paths
end

local function assert_manifest_files_readable(manifest)
  local paths = manifest_paths(manifest)
  if #paths == 0 then
    error("consensus: runtime context manifest has no readable file paths")
  end
  local has_notice = false
  for _, path in ipairs(paths) do
    local notice_suffix = "/UNTRUSTED-NOTICE.txt"
    local path_text = tostring(path)
    if path_text:sub(-#notice_suffix) == notice_suffix then
      has_notice = true
    end
    local handle = io.open(path, "r")
    if handle == nil then
      error("consensus: error_class=" .. stale_generation_context_error_class .. " runtime context manifest file is unreadable")
    end
    handle:close()
  end
  if not has_notice then
    error("consensus: runtime context manifest notice is missing")
  end
end

local function has_source_ref(value)
  return type(value) == "table"
    and is_bounded_string(value.kind, max_key_len)
    and is_bounded_string(value.ref, max_key_len)
end

local function has_content_fetch(proposal)
  return type(proposal) == "table"
    and type(proposal.content_fetch) == "string"
    and proposal.content_fetch ~= ""
end

local function resolve_content_manifest(content_fetch)
  local value = tostring(content_fetch or "")
  local key = value:match("^runtime%-cache:(.+)$")
  if key == nil then
    return value
  end
  if not is_path_safe_key(key, max_key_len) then
    error("consensus: invalid runtime context cache key")
  end
  local manifest = cache_get(key)
  if type(manifest) ~= "string" or manifest == "" then
    error("consensus: error_class=" .. stale_generation_context_error_class .. " runtime context cache miss")
  end
  if #manifest > max_content_fetch_len then
    error("consensus: runtime context manifest is overlong")
  end
  assert_manifest_files_readable(manifest)
  return manifest
end

function M.stale_generation_context_error_class()
  return stale_generation_context_error_class
end

function M.is_stale_generation_context_error(err)
  local text = tostring(err or "")
  if text:find("error_class=" .. stale_generation_context_error_class, 1, true) ~= nil then
    return true
  end
  if text:find("runtime context cache miss", 1, true) ~= nil then
    return true
  end
  return text:find("runtime context manifest file is unreadable", 1, true) ~= nil
end

local function normalize_round(value)
  if value == nil then
    return 0
  end
  local number = tonumber(value)
  if number == nil or number < 0 or number ~= math.floor(number) or number > 100000 then
    return nil
  end
  return number
end
local function bounded(value, limit)
  local text = trim(value)
  if #text > limit then
    return text:sub(1, limit)
  end
  return text
end

local decimal_checksum = strings.decimal_checksum

local function shell_single_quote(value)
  return "'" .. tostring(value):gsub("'", "'\\''") .. "'"
end

local function runtime_root_path(runtime_root)
  local root = trim(runtime_root)
  if root == "" or root:find("[\r\n]") ~= nil then
    error("consensus: invalid FKST_RUNTIME_ROOT")
  end
  return root:gsub("/+$", "")
end

local function scratch_segment(value)
  local safe = tostring(value or ""):gsub("[^%w._-]", "-")
  safe = safe:gsub("%-+", "-"):gsub("^%-+", ""):gsub("%-+$", ""):gsub("%.+$", "")
  if safe == "" then
    safe = "judgment"
  end
  if #safe > max_scratch_slug_len then
    safe = safe:sub(1, max_scratch_slug_len):gsub("%-+$", ""):gsub("%.+$", "")
  end
  if safe == "" then
    return "judgment"
  end
  return safe
end

local function is_verdict(value)
  return value == "approve"
    or value == "comment"
    or value == "reject"
    or value == "abstain"
    or value == "invalid"
end

local function valid_digest_item(item)
  if type(item) ~= "table" then
    return false
  end
  if not is_bounded_string(item.angle, max_key_len) or item.angle:find("%c") ~= nil then
    return false
  end
  if not is_verdict(item.verdict) then
    return false
  end
  if item.reply ~= nil and #tostring(item.reply) > max_digest_len then
    return false
  end
  if item.digest ~= nil and #tostring(item.digest) > max_digest_len then
    return false
  end
  return true
end

local function valid_prior_round_digests(value)
  if value == nil then
    return true
  end
  if type(value) ~= "table" or #value > max_prior_round_digests then
    return false
  end
  for _, item in ipairs(value) do
    if not valid_digest_item(item) then
      return false
    end
  end
  return true
end

local function normalized_angles(proposal)
  if type(proposal.angles) ~= "table" then
    return default_angles
  end

  local angles = {}
  for _, angle in ipairs(proposal.angles) do
    -- angle is untrusted (event-overridable); reject multi-line / control chars so it
    -- cannot inject a line-start sentinel into the rendered prompt.
    if not is_bounded_string(angle, max_key_len) or angle:find("%c") ~= nil then
      return nil
    end
    table.insert(angles, angle)
  end
  if #angles == 0 or #angles > max_angles then
    return nil
  end
  return angles
end

function M.is_eligible(proposal)
  if type(proposal) ~= "table" then
    return false
  end
  if proposal.schema ~= "consensus.proposal.v1" then
    return false
  end
  if not is_path_safe_key(proposal.proposal_id, max_key_len) then
    return false
  end
  if not is_path_safe_key(proposal.dedup_key, max_key_len) then
    return false
  end
  if not has_source_ref(proposal.source_ref) then
    return false
  end
  if not is_bounded_string(proposal.title, max_title_len) then
    return false
  end
  if not is_bounded_string(proposal.body, max_body_len) then
    return false
  end
  if proposal.context ~= nil and not is_bounded_string(proposal.context, max_context_len) then
    return false
  end
  if proposal.content_fetch ~= nil and not is_bounded_string(proposal.content_fetch, max_content_fetch_len) then
    return false
  end
  if normalize_round(proposal.round) == nil then
    return false
  end
  if proposal.convergence_question ~= nil
    and not is_bounded_string(proposal.convergence_question, max_narrowed_question_len) then
    return false
  end
  if not valid_prior_round_digests(proposal.prior_round_digests) then
    return false
  end
  return normalized_angles(proposal) ~= nil
end

function M.angles(proposal)
  return normalized_angles(proposal)
end

function M.render_template(template, vars)
  if type(template) ~= "string" then
    error("consensus: template must be a string")
  end
  if type(vars) ~= "table" then
    error("consensus: template vars must be a table")
  end

  return (template:gsub("{{([%w_]+)}}", function(name)
    local value = vars[name]
    if value == nil then
      error("consensus: missing template var " .. name)
    end
    return tostring(value)
  end))
end

function M.output_language(exec)
  local lang = trim(M.read_env("FKST_OUTPUT_LANG", exec))
  if lang == "zh" then
    return "zh"
  end
  return "en"
end

local function locale_text(key, vars)
  if type(t) ~= "function" then
    error("consensus: i18n catalog primitive t is unavailable")
  end
  return t(key, vars)
end

function M.prompt_preamble(proposal, exec)
  local language_line = locale_text("consensus.prompt_preamble.language." .. M.output_language(exec))

  -- Slots supersede GitHub issues #142 and #145: env-driven language selection plus
  -- harness-first judgment are fixed context, not verdict/parser protocol.
  local lines = {
    language_line,
    locale_text("consensus.prompt_preamble.judgment_harness"),
  }

  if has_content_fetch(proposal) then
    table.insert(lines, locale_text("consensus.prompt_preamble.history"))
  end

  return table.concat(lines, "\n")
end

function M.render_prompt_template(template, vars, proposal, exec)
  return M.prompt_preamble(proposal, exec) .. "\n\n" .. M.render_template(template, vars)
end

-- Keyed by dedup_key (which versions the proposal), not proposal_id, so an updated
-- proposal re-derives consensus instead of being silently skipped.
function M.reached_cache_key(dedup_key)
  if not is_path_safe_key(dedup_key, max_key_len) then
    error("consensus: invalid dedup_key")
  end
  return "consensus/reached/" .. tostring(dedup_key)
end

function M.read_runtime_root_cmd()
  return 'printf %s "$FKST_RUNTIME_ROOT"'
end

function M.judgment_scratch_worktree(runtime_root, kind, identity)
  local slug = scratch_segment(kind) .. "-" .. scratch_segment(identity)
  local suffix = decimal_checksum(tostring(kind) .. "#" .. tostring(identity))
  return runtime_root_path(runtime_root) .. "/judgment-worktrees/consensus-" .. slug .. "-" .. suffix
end

M.judgment_codex_opts = codex.judgment_codex_opts

function M.mkdir_p_cmd(path)
  local value = tostring(path or "")
  if value == "" or value:find("[\r\n]") ~= nil then
    error("consensus: invalid directory path")
  end
  return "mkdir -p " .. shell_single_quote(value)
end

-- Fail-closed parse. A genuine answer is an ADJACENT pair: exactly one clean verdict line
-- immediately followed by exactly one reply line (the prompt asks for line one = verdict,
-- line two = reply). The verdict sentinel must be followed by one whitelist word on its
-- own line (rejects the prompt echo "approve|abstain", "approve/reject",
-- "approve-ish"); the reply sentinel must be anchored at line start. A proposal body/context
-- is untrusted and may be echoed into stdout, so requiring a UNIQUE ADJACENT pair closes both
-- duplicate injection (a second clean sentinel pair) and orphan pairing (a lone echoed reply
-- attached to a verdict that lacked its own reply). Overlong replies are NOT truncated here;
-- aggregate() rejects them so we never raise a partial body.
function M.parse_angle_output(stdout, verdict_mode)
  local text = tostring(stdout or "")
  local mode = verdict_mode == "gate" and "gate" or "converge"

  local verdict = nil
  local verdict_count = 0
  local verdict_index = nil
  local reply = nil
  local reply_count = 0
  local reply_index = nil
  local gap = nil
  local gap_count = 0
  local gap_index = nil
  local index = 0
  for line in (text .. "\n"):gmatch("(.-)\n") do
    index = index + 1

    local token = line:match("^%s*" .. verdict_label .. "%s*(%a+)%s*$")
    if token ~= nil then
      local lowered = token:lower()
      if lowered == "approve"
        or lowered == "abstain"
        or (mode == "gate" and (lowered == "comment" or lowered == "reject")) then
        verdict = lowered
        verdict_count = verdict_count + 1
        verdict_index = index
      end
    end

    local captured = line:match("^%s*" .. reply_label .. "%s*(.+)$")
    if captured ~= nil then
      captured = trim(captured)
      if captured ~= "" then
        reply = captured
        reply_count = reply_count + 1
        reply_index = index
      end
    end

    local captured_gap = line:match("^%s*" .. gap_label .. "%s*(.+)$")
    if captured_gap ~= nil then
      captured_gap = trim(captured_gap)
      if captured_gap ~= "" then
        gap = captured_gap
        gap_count = gap_count + 1
        gap_index = index
      end
    end
  end

  if verdict_count ~= 1 or reply_count ~= 1 then
    return nil
  end
  if reply_index ~= verdict_index + 1 then
    return nil
  end
  if verdict == "reject" then
    if gap_count ~= 1 or gap_index ~= reply_index + 1 or not is_bounded_string(gap, max_gap_len) then
      return nil
    end
  elseif gap_count ~= 0 then
    return nil
  end

  return {
    verdict = verdict,
    reply = reply,
    blocking_gap = gap,
  }
end

local function review_gap_list(angle_results)
  local gaps = {}
  for _, result in ipairs(angle_results) do
    if result.verdict == "reject" then
      if not is_bounded_string(result.blocking_gap, max_gap_len) then
        return nil
      end
      table.insert(gaps, result.blocking_gap)
      if #gaps > max_gaps then
        return nil
      end
    end
  end
  return gaps
end

function M.all_angles_succeeded(angle_results)
  if type(angle_results) ~= "table" or #angle_results == 0 then
    return false
  end
  for _, result in ipairs(angle_results) do
    if type(result) ~= "table" or result.exit_code ~= 0 then
      return false
    end
  end
  return true
end

function M.aggregate(angle_results, verdict_mode)
  if type(angle_results) ~= "table" or #angle_results == 0 then
    return nil
  end
  local mode = verdict_mode == "gate" and "gate" or "converge"
  local first_verdict = nil
  local has_approve = false
  local has_reject = false

  for _, result in ipairs(angle_results) do
    if type(result) ~= "table" or result.exit_code ~= 0 then
      return nil
    end
    if not is_bounded_string(result.reply, max_reply_len) then
      return nil
    end
    if mode == "converge" then
      if result.verdict ~= "approve" then
        return nil
      end
    elseif result.verdict ~= "approve"
      and result.verdict ~= "comment"
      and result.verdict ~= "reject"
      and result.verdict ~= "abstain" then
      return nil
    end
    if mode == "gate" then
      if result.verdict == "approve" then
        has_approve = true
      elseif result.verdict == "reject" then
        has_reject = true
      end
    end
    if mode == "converge" and first_verdict == nil then
      first_verdict = result.verdict
    elseif mode == "converge" and result.verdict ~= first_verdict then
      return nil
    end
  end

  if mode == "gate" then
    if has_reject then
      local gaps = review_gap_list(angle_results)
      if gaps == nil or #gaps == 0 then
        return nil
      end
      return {
        decision = "reject",
        blocking_gaps = gaps,
      }
    end
    if has_approve then
      return {
        decision = "approve",
      }
    end
    return nil
  end
  return "approve"
end

function M.angle_digests(angle_results)
  local digests = {}
  for _, result in ipairs(angle_results or {}) do
    local verdict = result.verdict
    if not is_verdict(verdict) then
      verdict = "invalid"
    end
    local reply = bounded(result.reply or "", max_digest_len)
    local raw = bounded(result.stdout or "", max_digest_len)
    local digest = reply
    if digest == "" then
      digest = raw
    end
    if digest == "" then
      digest = "No parseable angle reply."
    end
    table.insert(digests, {
      angle = bounded(result.angle or "unknown", max_key_len),
      verdict = verdict,
      reply = reply,
      digest = bounded(digest, max_digest_len),
    })
  end
  return digests
end

function M.parse_meta_judge_output(stdout, verdict_mode)
  local text = tostring(stdout or "")
  local mode = verdict_mode == "gate" and "gate" or "converge"
  local parsed = nil
  local count = 0
  for line in (text .. "\n"):gmatch("(.-)\n") do
    local kind, value = line:match("^%s*([Rr][Ee][Aa][Cc][Hh][Ee][Dd])%s*:%s*(.+)%s*$")
    if kind == nil then
      kind, value = line:match("^%s*([Cc][Oo][Nn][Vv][Ee][Rr][Gg][Ee])%s*:%s*(.+)%s*$")
    end
    if kind == nil then
      kind, value = line:match("^%s*(⟦FKST:PLAN⟧)%s+(.+)%s*$")
    end
    if kind ~= nil then
      value = bounded(value, max_narrowed_question_len)
      if value ~= "" then
        count = count + 1
        local lowered = kind:lower()
        if lowered == "reached" then
          -- decision must be an EXACT whitespace-delimited `approve`
          -- token followed by a non-empty framing; `approve/reject`,
          -- `approve-ish`, or a bare `approve` (no framing) fail closed to
          -- nil so the caller converges instead of fabricating a reached.
          local first, framing = value:match("^(%S+)%s+(.+)$")
          local decision = first and first:lower() or nil
          if (decision == "approve" or (mode == "gate" and decision == "reject"))
            and framing ~= nil and framing ~= "" then
            parsed = {
              kind = "reached",
              decision = decision,
              framing = value,
            }
          else
            parsed = nil
          end
        else
          if kind == "⟦FKST:PLAN⟧" then
            parsed = {
              kind = "plan",
              plan = value,
              narrowed_question = value,
            }
          else
            parsed = {
              kind = "converge",
              narrowed_question = value,
            }
          end
        end
      end
    end
  end

  if count ~= 1 then
    return nil
  end
  return parsed
end

function M.default_narrowed_question(proposal, angle_results)
  local parts = {}
  for _, item in ipairs(M.angle_digests(angle_results)) do
    table.insert(parts, tostring(item.angle) .. "=" .. tostring(item.verdict))
  end
  local question = "Resolve the concrete disagreement for proposal " .. tostring(proposal.proposal_id)
    .. " and decide whether the current framing can be approved."
  if #parts > 0 then
    question = question .. " Angle verdicts: " .. table.concat(parts, ", ") .. "."
  end
  return bounded(question, max_narrowed_question_len)
end

function M.build_reached_payload(proposal, decision, angle_results, framing)
  if type(proposal) ~= "table" then
    error("consensus: proposal must be a table")
  end
  local clean_decision = decision
  local decision_meta = nil
  if type(decision) == "table" then
    clean_decision = decision.decision
    decision_meta = decision
  end
  if clean_decision ~= "approve" and clean_decision ~= "reject" then
    error("consensus: invalid decision")
  end
  if not has_source_ref(proposal.source_ref) then
    error("consensus: missing source_ref")
  end

  local clean_results = {}
  local body_lines = {}
  local advisory_lines = {}
  local clean_framing = nil
  local clean_gaps = nil
  if type(framing) == "string" then
    clean_framing = bounded(framing, max_framing_len)
  end
  if clean_framing == "" then
    clean_framing = nil
  end
  if type(decision_meta) == "table" and type(decision_meta.blocking_gaps) == "table" then
    clean_gaps = {}
    for _, gap in ipairs(decision_meta.blocking_gaps) do
      if not is_bounded_string(gap, max_gap_len) then
        error("consensus: invalid blocking gap")
      end
      table.insert(clean_gaps, bounded(gap, max_gap_len))
      if #clean_gaps > max_gaps then
        error("consensus: too many blocking gaps")
      end
    end
    if #clean_gaps == 0 then
      clean_gaps = nil
    end
  end
  for _, result in ipairs(angle_results or {}) do
    table.insert(clean_results, {
      angle = result.angle,
      verdict = is_verdict(result.verdict) and result.verdict or "invalid",
    })
    local target = (clean_decision == "approve" and result.verdict == "comment") and advisory_lines or body_lines
    table.insert(target, tostring(result.angle) .. ":")
    table.insert(target, bounded(result.reply, max_reply_len))
    table.insert(target, "")
  end

  if #body_lines > 0 then
    table.remove(body_lines)
  end
  if #advisory_lines > 0 then
    table.remove(advisory_lines)
    if #body_lines > 0 then
      table.insert(body_lines, "")
    end
    table.insert(body_lines, "Advisory (non-blocking):")
    for _, line in ipairs(advisory_lines) do
      table.insert(body_lines, line)
    end
  end

  local payload = {
    schema = "consensus.consensus_reached.v1",
    proposal_id = proposal.proposal_id,
    decision = clean_decision,
    framing = clean_framing,
    body = table.concat(body_lines, "\n"),
    angle_results = clean_results,
    dedup_key = "consensus:" .. tostring(proposal.dedup_key),
    source_ref = {
      kind = proposal.source_ref.kind,
      ref = proposal.source_ref.ref,
    },
  }
  if proposal.effect_version ~= nil then
    payload.effect_version = tostring(proposal.effect_version)
  end
  if clean_gaps ~= nil then
    payload.blocking_gaps = clean_gaps
    payload.blocking_gap = clean_gaps[1]
  end
  return payload
end

function M.build_converge_payload(proposal, narrowed_question, angle_results)
  if type(proposal) ~= "table" then
    error("consensus: proposal must be a table")
  end
  if not has_source_ref(proposal.source_ref) then
    error("consensus: missing source_ref")
  end

  local payload = {
    schema = "consensus.consensus_converge.v1",
    proposal_id = proposal.proposal_id,
    round = tonumber(proposal.round) or 0,
    narrowed_question = bounded(narrowed_question, max_narrowed_question_len),
    angle_digests = M.angle_digests(angle_results),
    dedup_key = "consensus:" .. tostring(proposal.dedup_key),
    source_ref = {
      kind = proposal.source_ref.kind,
      ref = proposal.source_ref.ref,
    },
  }
  if proposal.effect_version ~= nil then
    payload.effect_version = tostring(proposal.effect_version)
  end
  return payload
end

require("core.prompt_rendering").install(M, {
  verdict_label = verdict_label,
  reply_label = reply_label,
  gap_label = gap_label,
  max_key_len = max_key_len,
  max_digest_len = max_digest_len,
  is_bounded_string = is_bounded_string,
  has_content_fetch = has_content_fetch,
  resolve_content_manifest = resolve_content_manifest,
})

return M

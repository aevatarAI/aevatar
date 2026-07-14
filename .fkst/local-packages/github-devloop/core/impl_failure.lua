local parsers_misc = require("devloop.parsers.misc")
local m_facts = require("devloop.markers.facts")
local S = {}
local strings = require("contract.strings")

local max_impl_auto_retry_attempts = 2
local max_impl_retry_attempts = 100000
local auto_retryable_reasons = {
  ["codex-failed"] = true,
  ["non-descendant-head"] = true,
}

local function marker_attr(marker, name)
  return marker:match(name .. '="([^"]*)"')
end

local function valid_attempt(value)
  local n = tonumber(value)
  if n == nil or n < 1 or n ~= math.floor(n) or n > max_impl_retry_attempts then
    return nil
  end
  return n
end

function S.install(M)
M._max_impl_retry_attempts = max_impl_retry_attempts
M._max_impl_auto_retry_attempts = max_impl_auto_retry_attempts

function M.impl_failure_marker(proposal_id, dedup_key, reason, attempt)
  local safe_reason = strings.sanitize_key(reason or "failed", M._max_key_len):gsub("/", "-")
  local attempt_field = ""
  if attempt ~= nil then
    local n = valid_attempt(attempt)
    if n == nil then
      error("github-devloop: invalid impl failure attempt")
    end
    attempt_field = '" attempt="' .. tostring(n)
  end
  return '<!-- fkst:github-devloop:impl-failure:v1 proposal="' .. tostring(proposal_id)
    .. '" reason="' .. safe_reason
    .. attempt_field
    .. '" dedup="' .. tostring(dedup_key)
    .. '" -->'
end

function M.impl_failure_fact(comments, proposal_id, dedup_key)
  if type(comments) ~= "table" then
    return nil
  end
  local best = nil
  local marker_pattern = "<!%-%- fkst:github%-devloop:impl%-failure:v1.-%-%->"
  for _, comment in ipairs(parsers_misc._trusted_marker_comments(M, comments)) do
    for marker in parsers_misc._comment_body(M, comment):gmatch(marker_pattern) do
      local marker_proposal = marker_attr(marker, "proposal")
      local marker_dedup = marker_attr(marker, "dedup")
      local reason = marker_attr(marker, "reason")
      if marker_proposal == tostring(proposal_id)
        and marker_dedup == tostring(dedup_key)
        and reason ~= nil
        and strings.is_bounded_string(reason, M._max_key_len) then
        local attempt = valid_attempt(marker_attr(marker, "attempt")) or 1
        local fact = {
          proposal_id = marker_proposal,
          dedup_key = marker_dedup,
          reason = reason,
          attempt = attempt,
          comment_created_at = parsers_misc._comment_created_at(M, comment),
        }
        if best == nil or fact.attempt > best.attempt then
          best = fact
        end
      end
    end
  end
  return best
end

function M.has_impl_failure_marker(comments, proposal_id, dedup_key)
  return M.impl_failure_fact(comments, proposal_id, dedup_key) ~= nil
end

function M.impl_failure_retry_allowed(fact)
  return type(fact) == "table"
    and auto_retryable_reasons[fact.reason] == true
    and tonumber(fact.attempt or 1) < max_impl_auto_retry_attempts
end

function M.next_impl_retry_attempt(fact)
  if not M.impl_failure_retry_allowed(fact) then
    return nil
  end
  return tonumber(fact.attempt or 1) + 1
end

function M.implementation_base_version(version)
  return tostring(version or ""):gsub("/reimplement/%d+$", "")
end

function M.implementation_retry_attempt(version)
  return valid_attempt(tostring(version or ""):match("/reimplement/(%d+)$"))
end

-- The `implementing` marker version is the ALREADY-wrapped ready dedup_key
-- ("ready/<inner>"), because build_devloop_ready_payload applies the
-- _dedup_key({"ready", ...}) wrapper when the ready event is first raised. A
-- liveness re-drive that re-raises devloop_ready must therefore pass the INNER
-- (unwrapped) version, so build_devloop_ready_payload reproduces exactly the
-- frozen marker version on re-wrap. Passing the wrapped version double-wraps it
-- ("ready/ready/<inner>") and the implement receiver rejects it as
-- skip-stale(version-mismatch) forever (issue #718 / #373). Fail closed if the
-- expected prefix is absent, so a malformed marker surfaces rather than
-- silently re-introducing a mismatch.
function M.ready_payload_inner_version(version)
  local text = tostring(version or "")
  local inner, replaced = text:gsub("^ready/", "", 1)
  if replaced == 0 then
    error("github-devloop: implementing marker version lacks the expected 'ready/' prefix: " .. text)
  end
  return inner
end

function M.implementation_attempt_version(version, attempt)
  local base = M.implementation_base_version(version)
  local n = tonumber(attempt)
  if n == nil or n <= 1 then
    return base
  end
  if n ~= math.floor(n) or n > max_impl_retry_attempts then
    error("github-devloop: invalid implementation attempt version")
  end
  return base .. "/reimplement/" .. tostring(n)
end

function M.has_implementation_fact_marker(comments, proposal_id, dedup_key)
  return m_facts.has_implementing_marker(M, comments, proposal_id, dedup_key)
    or M.has_impl_failure_marker(comments, proposal_id, dedup_key)
end
end

return S

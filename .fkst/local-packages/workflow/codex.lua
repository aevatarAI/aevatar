local M = {}

local function copy_opts(opts)
  local out = {}
  for key, value in pairs(type(opts) == "table" and opts or {}) do
    out[key] = value
  end
  return out
end

function M.judgment_codex_opts(prompt, worktree)
  return {
    prompt = prompt,
    worktree = worktree,
    sandbox = "read-only",
  }
end

function M.unrestricted_codex_opts(prompt, worktree)
  return {
    prompt = prompt,
    worktree = worktree,
  }
end

local function identity_parts(identity_or_role, proposal_id, dedup_key)
  if type(identity_or_role) == "table" then
    return identity_or_role.role, identity_or_role.proposal_id, identity_or_role.dedup_key
  end
  return identity_or_role, proposal_id, dedup_key
end

local function run_lease_expired(run)
  -- A running record whose lease deadline is already past is NOT live: it is a
  -- dead/hung run awaiting reap, and a redrive must reactivate it (start a
  -- replacement), never defer to it. lease_expires_at_ms is data on the run
  -- record (milliseconds), not a magic constant; now() is seconds.
  local lease = run.lease_expires_at_ms
  if type(lease) ~= "number" or type(now) ~= "function" then
    return false
  end
  return lease < now() * 1000
end

local function run_matches(run, role, proposal_id, dedup_key)
  return type(run) == "table"
    and tostring(run.role or "") == tostring(role or "")
    and tostring(run.proposal_id or "") == tostring(proposal_id or "")
    and tostring(run.dedup_key or "") == tostring(dedup_key or "")
    and tostring(run.status or "") == "running"
    and not run_lease_expired(run)
end

function M.live_run_active(identity_or_role, proposal_id, dedup_key)
  local role
  role, proposal_id, dedup_key = identity_parts(identity_or_role, proposal_id, dedup_key)
  if role == nil or proposal_id == nil or dedup_key == nil then
    return false
  end
  -- Observational precheck only: concurrent first dispatches can race here. The
  -- atomic one-live-run invariant belongs to the substrate live_run_key admission
  -- follow-on; this wrapper closes redrive/redelivery forks by using one identity
  -- for both the guard and the spawn opts.
  if type(fkst) ~= "table" or type(fkst.codex_runs) ~= "function" then
    return false
  end
  local ok, status = pcall(fkst.codex_runs)
  if not ok or type(status) ~= "table" or type(status.running) ~= "table" then
    return false
  end
  for _, run in ipairs(status.running) do
    if run_matches(run, role, proposal_id, dedup_key) then
      return true
    end
  end
  return false
end

function M.dispatch(identity, opts)
  if type(identity) ~= "table" then
    error("workflow.codex: dispatch identity must be a convergence identity")
  end
  local role, proposal_id, dedup_key = identity_parts(identity)
  if role == nil or proposal_id == nil or dedup_key == nil then
    error("workflow.codex: dispatch identity is incomplete")
  end
  if M.live_run_active(identity) then
    return {
      deferred = true,
      reason = "live-run-active",
      identity = identity,
    }
  end
  local dispatch_opts = copy_opts(opts)
  local sync = dispatch_opts.sync == true
  dispatch_opts.sync = nil
  dispatch_opts.role = role
  dispatch_opts.proposal_id = proposal_id
  dispatch_opts.dedup_key = dedup_key
  if sync then
    return spawn_codex_sync(dispatch_opts)
  end
  return spawn_codex(dispatch_opts)
end

return M

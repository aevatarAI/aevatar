local S = {}

local function copy_map(map)
  local out = {}
  for key, value in pairs(map or {}) do
    if type(value) == "table" then
      out[key] = copy_map(value)
    else
      out[key] = value
    end
  end
  return out
end

local function devloop_liveness_policy()
  return {
    liveness_resolver_families = {
      ["converge-round"] = {
        ["converge-round"] = true,
      },
      ["dependency-hold"] = {
        ["dependency-wait"] = true,
        ["dependency-cycle"] = true,
        ["dependency-unresolvable"] = true,
      },
      ["implement-attempt"] = {
        ["implement-attempt"] = true,
      },
      ["merge-gate-wait"] = {
        ["merge-gate-wait"] = true,
      },
      ["review-converge-round"] = {
        ["review-converge-round"] = true,
      },
      ["child-state"] = {
        state = true,
      },
    },
    allowed_signal_surfaces = {
      ["issue-comment-stream"] = true,
      ["pr-comment-stream"] = true,
    },
    signal_max_age_optional_resolvers = {
      ["implement-attempt"] = true,
    },
  }
end

local function devloop_restart_liveness_policy()
  return {
    codex_run = {
      primitive = "fkst.codex_runs",
      status = "running",
      on_error = "defer",
      indeterminate_timeout = "row-budget",
    },
    child_workflow_wait = {
      live_marker = "state:v1",
      delegation_marker = "pr-delegation:v1",
      signal_family = "state",
      signal_resolver = "child-state",
      surface = "pr-comment-stream",
    },
  }
end

function S.policy()
  return copy_map(devloop_liveness_policy())
end

function S.restart_policy()
  return copy_map(devloop_restart_liveness_policy())
end

function S.with_restart_policy(resolved)
  local out = copy_map(resolved or {})
  local policy = devloop_restart_liveness_policy()
  for key, value in pairs(policy) do
    out[key] = copy_map(value)
  end
  return out
end

function S.install(M, resolved)
  local policy = devloop_liveness_policy()
  for key, value in pairs(resolved or {}) do
    policy[key] = value
  end
  policy.restart_package_name = M.restart_package_name
  policy.restart_source_root = M.restart_source_root
  local shared = require("workflow.liveness.shared").install(M, policy)
  require("workflow.liveness.contract").install(M, shared, {
    pr_recovery = {
      allowed = {
        not_mergeable = {
          to_state = "fixing",
          queue = "devloop_fixing",
        },
      },
    },
  })
  require("devloop.liveness.signal").install(M, shared)
  require("devloop.liveness.timeout").install(M, shared)
end

return S

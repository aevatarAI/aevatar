-- devloop.di.providers: builds the production `all_caps` capability set for the DI composition
-- root, then projects a per-department view through the sealed projector.
--
-- The devloop capability modules are already self-contained require-able units (the ambient-M
-- self-containment arc), so a capability handle is just a narrow role-scoped view over
-- require("devloop.<mod>") -- no ambient composed M is needed to build caps. Departments declare
-- narrow roles in spec.caps.requires; build_department projects only those. See
-- docs/superpowers/specs/2026-07-02-di-refactor-retire-ambient-m-design.md.

local select_caps = require("devloop.di.select_caps")

local M = {}

-- Pick a named subset of a module's functions into a role handle. Keeps each capability narrow:
-- a role exposes only the methods that authority legitimately owns, so a test fakes exactly it.
local function role_of(module, names)
  local handle = {}
  for _, name in ipairs(names) do
    handle[name] = module[name]
  end
  return handle
end

-- build_all(env) -> the full capability set the composition root holds. env carries process-level
-- inputs (clock, ids, config, and the forge run handle for egress). No department ever receives
-- this table directly; build_department projects a sealed narrow view.
function M.build_all(env)
  env = env or {}
  local logging = require("devloop.logging")
  local state = require("devloop.state")
  local entity = require("devloop.entity")

  local egress = env.egress or {}

  return {
    -- Logging is cross-cutting and low-risk; a broad handle is fine (GPT-converged).
    log = logging,

    -- State authority split by what power the role grants.
    state = {
      read = role_of(state, {
        "current_state", "is_state", "is_state_label", "has_label", "state_label",
        "state_order", "state_successors", "stage_rank", "version_order_key",
        "version_updated_at", "versioned_transition_status",
      }),
      cas = role_of(state, {
        "cas_outcome", "state_marker", "state_label_changes", "compare_state_marker_order",
      }),
      lifecycle = role_of(state, {
        "restart_transition_table", "lifecycle_state_set",
      }),
    },

    entity = {
      reader = role_of(entity, {
        "current_entity_state", "issue_source_ref", "pr_source_ref", "pr_native_origin",
        "linked_pr_surface_snapshot", "parse_entity_proposal_id", "parse_pr_proposal_id",
      }),
    },

    egress = {
      gh = egress.github or egress.gh,
      git = egress.git,
    },

    clock = env.clock,
    ids = env.ids,
    config = env.config,
  }
end

-- build_department(dept_mod, all_caps, opts) -> the department's engine table {spec, pipeline}.
-- dept_mod is `require("departments.<name>")` returning { spec, cap_deps, make_department }.
-- The projection is sealed to the declared cap_deps.
function M.build_department(dept_mod, all_caps, opts)
  opts = opts or {}
  assert(type(dept_mod) == "table" and type(dept_mod.make_department) == "function",
    "department module must return { spec, cap_deps, make_department }")
  local cap_deps = dept_mod.cap_deps or (dept_mod.spec and dept_mod.spec.caps and dept_mod.spec.caps.requires)
  local caps = select_caps.project(all_caps, cap_deps or {}, { department = opts.department or "?" })
  return dept_mod.make_department(caps)
end

return M

-- devloop.di.capdefs: the declared capability role taxonomy.
--
-- Role-based, not source-module-based: a capability is one coherent authority a test would
-- naturally fake, between one god-bundle and 167 one-method handles. A department declares the
-- narrow roles it needs in `spec.caps.requires`; validate.lua checks the declared paths against
-- this set, and select_caps.lua projects only those. See
-- docs/superpowers/specs/2026-07-02-di-refactor-retire-ambient-m-design.md.

local M = {}

-- Known capability role paths (dotted). Each names an authority, not a module. Grouping mirrors
-- the SPEC taxonomy; providers.lua wires production handles for each from the self-contained
-- devloop modules / forge ports.
M.paths = {
  "log",                 -- structured logging (cross-cutting, low-risk)
  "state.read",          -- read-only state / version inspection
  "state.cas",           -- versioned compare-and-swap lifecycle mutation
  "state.lifecycle",     -- boot / migrate / recover (root-only usually)
  "commands.emit",       -- emit / enqueue commands
  "commands.registry",   -- register command handlers / metadata
  "entity.reader",       -- resolve domain entities (read)
  "entity.writer",       -- mutate entity records (given sparingly)
  "egress.gh",           -- GitHub external effects (forge.github)
  "egress.git",          -- Git external effects (forge.git)
  "clock",               -- deterministic time
  "ids",                 -- deterministic id generation
  "config",              -- read-only package config (typed slices)
}

local _set = {}
for _, p in ipairs(M.paths) do
  _set[p] = true
end

function M.is_known(path)
  return _set[path] == true
end

return M

local S = {}
local C = {}

local modules = {
  "devloop.commands.support",
  "devloop.commands.validators",
  "devloop.commands.issue_reads",
  "devloop.commands.observe_lists",
  "devloop.commands.prs",
  "devloop.commands.git_ops",
}

-- Aggregate facade: expose every submodule's methods on the commands module itself, so a
-- department can `require("devloop.commands").gh_pr_view_observe(...)` without knowing which
-- submodule owns it. The egress submodules are self-contained modules (methods are module-level
-- C functions); merge their non-install keys here.
for _, module_name in ipairs(modules) do
  local mod = require(module_name)
  for key, value in pairs(mod) do
    if key ~= "install" then
      C[key] = value
    end
  end
end

-- Migration compat scaffold: still loop-bind every submodule's methods onto the composed core M
-- so readers not yet rewired to require("devloop.commands") keep working. Deleted once the
-- G-DEVLOOP-INSTALLER ratchet shows zero commands reads through the ambient M.
function S.install(M)
  for _, module_name in ipairs(modules) do
    require(module_name).install(M)
  end
end

C.install = S.install

return C

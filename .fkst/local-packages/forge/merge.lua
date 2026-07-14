local S = {}
local shared = require("forge.merge.shared")
local ci_gate = require("forge.merge.ci_gate")
local self_heal = require("forge.merge.self_heal")
local verified_merge = require("forge.merge.verified_merge")

function S.install(M)
local shared_helpers = shared.install(M)
local ci_gate_exports = ci_gate.install(M, shared_helpers)
self_heal.install(M, shared_helpers, ci_gate_exports)
verified_merge.install(M, shared_helpers, ci_gate_exports)
end

return S

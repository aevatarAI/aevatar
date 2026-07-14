local strings = require("contract.strings")
local S = {}

function S.install(M)
local function validate_effect_once_opts(opts)
  if type(opts) ~= "table" then
    error("github-devloop: saga.effect_once requires opts")
  end
  if not strings.is_bounded_string(opts.effect_id, M._max_dedup_len) then
    error("github-devloop: saga.effect_once requires a stable effect_id")
  end
  if type(opts.completion_check) ~= "function" then
    error("github-devloop: saga.effect_once requires completion_check")
  end
  if type(opts.perform) ~= "function" then
    error("github-devloop: saga.effect_once requires perform")
  end
end

function M.effect_once(opts)
  validate_effect_once_opts(opts)
  if opts.completion_check() then
    return {
      effect_id = opts.effect_id,
      action = "skip",
    }
  end
  return {
    effect_id = opts.effect_id,
    action = "perform",
    result = opts.perform(),
  }
end

end

return S

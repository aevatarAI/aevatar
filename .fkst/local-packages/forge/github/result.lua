local S = {}

function S.gh_result(fn)
  local ok, result_or_error = pcall(fn)
  if ok then
    return result_or_error
  end
  if type(result_or_error) == "table" and result_or_error.result ~= nil then
    return result_or_error.result
  end
  error(result_or_error)
end

return S

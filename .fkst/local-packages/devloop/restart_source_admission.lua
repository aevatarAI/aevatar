local M = {}

function M.exact_source_state(source_states, source_state)
  if type(source_states) ~= "table" or #source_states ~= 1 or source_states[1] ~= source_state then
    return false
  end
  for key in pairs(source_states) do
    if key ~= 1 then
      return false
    end
  end
  return true
end

function M.dense_unique_state_set(source_states)
  if type(source_states) ~= "table" then
    return nil
  end
  local states = {}
  local count = 0
  for key, state_name in pairs(source_states) do
    if type(key) ~= "number" or key < 1 or key % 1 ~= 0
      or type(state_name) ~= "string" or state_name == ""
      or states[state_name] == true then
      return nil
    end
    states[state_name] = true
    count = count + 1
  end
  if count == 0 or count ~= #source_states then
    return nil
  end
  for index = 1, count do
    if source_states[index] == nil then
      return nil
    end
  end
  return states
end

return M

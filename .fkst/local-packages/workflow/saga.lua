-- workflow.saga: shared department shape for event-level idempotent sagas.
-- Contract: done(event) must be cheap, side-effect-free, and re-derived from
-- the durable fact source. It may cache immutable event decoding, but it must
-- never cache mutable durable facts for act(event).
--
-- act(event) must re-derive mutable durable facts inside its own fenced
-- critical section and re-check completion before each write-class effect.
-- At-least-once idempotency belongs at the write boundary, not at the earlier
-- done probe.
local S = {}

local function always_accept(_event)
  return true
end

local function validate_consumes(consumes)
  if type(consumes) ~= "table" or #consumes == 0 then
    error("workflow.saga: department requires non-empty consumes")
  end
end

local function validate_spec(spec)
  if type(spec) ~= "table" then
    error("workflow.saga: department requires spec")
  end
  validate_consumes(spec.consumes)
end

local function validate_handlers(handlers)
  if type(handlers) ~= "table" then
    error("workflow.saga: department requires handlers")
  end
  if type(handlers.done) ~= "function" then
    error("workflow.saga: department requires done")
  end
  if type(handlers.act) ~= "function" then
    error("workflow.saga: department requires act")
  end
end

local function spec_from_spec(spec)
  return {
    consumes = spec.consumes,
    produces = spec.produces,
    stall_window = spec.stall_window,
    retry = spec.retry,
    fanout = spec.fanout,
    ephemeral = spec.ephemeral,
    published_seam = spec.published_seam,
  }
end

function S.department(spec, handlers)
  validate_spec(spec)
  validate_handlers(handlers)

  local accept = handlers.accept or always_accept
  local function raw(event)
    if not accept(event) then
      if type(handlers.on_skip_foreign) == "function" then
        handlers.on_skip_foreign(event)
      end
      return nil
    end
    if handlers.done(event) then
      if type(handlers.on_skip) == "function" then
        handlers.on_skip(event)
      end
      return nil
    end
    return handlers.act(event)
  end

  local name = handlers.name or "workflow.saga"
  local wrapped = raw
  if type(handlers.wrap) == "function" then
    wrapped = handlers.wrap(name, raw)
  end
  _G.pipeline = wrapped

  return {
    spec = spec_from_spec(spec),
    pipeline = wrapped,
  }
end

return S

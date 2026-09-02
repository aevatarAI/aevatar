# Cap text-edit fallback streaming edits when CardKit create fails

## Goal

When Lark CardKit card-create fails mid-turn and the reply streams via the text-edit fallback, cap interim edits (freeze after N, deliver complete text on the final flush) so long replies are not truncated by the per-message edit cap / reply-token expiry. The fallback currently inherits CardKit's unbounded interim cadence.

## Requirements

- TBD

## Acceptance Criteria

- [ ] TBD

## Notes

- Keep `prd.md` focused on requirements, constraints, and acceptance criteria.
- Lightweight tasks can remain PRD-only.
- For complex tasks, add `design.md` for technical design and `implement.md` for execution planning before `task.py start`.

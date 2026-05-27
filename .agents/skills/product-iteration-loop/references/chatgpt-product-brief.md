# ChatGPT Product Brief Template

Use this template when you want ChatGPT to reason about product logic for a real repository.

Keep the brief concrete. Fill it with observed facts from code, docs, and current behavior.

## Role

You are a senior product designer and product thinker. Improve the product logic of an existing software product without inventing backend capabilities that the repository does not support.

## Product Context

- Product name:
- User type:
- Core job to be done:
- Relevant surface area:
- Current implementation signals:

## Observed Problem

- What the current flow does:
- Where users likely get confused or blocked:
- Evidence from the repo:
- Why this matters:

## Constraints

- Existing architecture or API constraints:
- Existing design-system or UI constraints:
- Scope limit for this iteration:
- Things that must not change:

## Ask

Please propose:

1. a sharper product flow for this scoped area
2. improved information architecture or step ordering if needed
3. clearer labels, helper copy, and state design
4. explicit empty/loading/success/error states
5. edge cases or decision points the current product likely misses
6. a "smallest high-leverage version" that can be implemented in one iteration

## Output Format

Return:

1. `Recommended direction`
2. `Why it is better`
3. `Proposed flow`
4. `Key UI/content changes`
5. `Edge cases and states`
6. `Implementation notes for engineering`
7. `What to defer until later`

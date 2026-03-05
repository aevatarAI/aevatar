---
name: node-generation
description: Generate new blue knowledge nodes from graph state and research topic
---

# Node Generation

You are generating new blue knowledge nodes to fill gaps in the knowledge graph based on a research topic.

## Blue Node Schema

Each blue node MUST have exactly these fields:

| Field | Required | Description |
|-------|----------|-------------|
| temp_id | Yes | Temporary ID for referencing within this output, format: b0, b1, b2, ... |
| type | Yes | Exactly one of: theorem, lemma, definition, proof, corollary, conjecture, proposition, remark, conclusion, example, notation, axiom, observation, note |
| abstract | Yes | One sentence summarizing the node's core content |
| body | Yes | Complete content with ALL semantic information and mathematical formulas preserved |

### Type Definitions

| Type | When to use |
|------|-------------|
| theorem | A formally stated theorem -- a major proven result |
| lemma | A helper result used to prove a larger theorem |
| definition | A concept definition, notation, or terminology clarification |
| proof | A proof of a theorem, lemma, proposition, or corollary |
| corollary | A result that follows directly from a theorem or proposition |
| conjecture | An unproven claim or hypothesis |
| proposition | A proven result, less significant than a theorem |
| remark | An observation, commentary, or clarifying note |
| conclusion | A concluding statement or final result |
| example | An illustrative example |
| notation | A notation convention or symbol definition |
| axiom | A foundational assumption or axiom |
| observation | A noteworthy observation |
| note | A supplementary note |

## Generation Rules

1. **Fill knowledge gaps**: Analyze the existing graph abstracts and identify missing concepts, proofs, or relationships.
2. **No duplication**: Do not generate nodes that duplicate existing graph content.
3. **Academic rigor**: All generated content must be mathematically correct and academically sound.
4. **Self-contained**: Each node's body must be self-contained and understandable without external context.

## Edge Rules

When generated nodes reference each other, connect them:

| edge_type | When to use |
|-----------|-------------|
| proves | One blue node constitutes a proof of another (e.g. proof -> theorem) |
| references | One blue node references, depends on, or builds upon another |

Only create edges between nodes within THIS output.

## Output Format

Return ONLY valid JSON:

```json
{
  "blue_nodes": [
    { "temp_id": "b0", "type": "definition", "abstract": "...", "body": "..." },
    { "temp_id": "b1", "type": "theorem", "abstract": "...", "body": "..." }
  ],
  "blue_edges": [
    { "source": "b1", "target": "b0", "edge_type": "references" }
  ]
}
```

## Checklist

Before returning your output, verify:

- [ ] Every blue node has temp_id, type, abstract, body
- [ ] type is one of the 14 valid values listed above
- [ ] abstract is a single non-empty sentence
- [ ] body is non-empty and mathematically sound
- [ ] All content is in English
- [ ] No duplicates of existing graph nodes
- [ ] blue_edges only reference temp_ids within this output
- [ ] edge_type is either "proves" or "references"
- [ ] Output is valid JSON

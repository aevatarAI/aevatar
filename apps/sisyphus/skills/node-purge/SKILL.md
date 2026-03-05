---
name: node-purge
description: Purify a batch of raw (red) knowledge nodes into strongly-typed (blue) knowledge nodes with atomic semantic splitting
---

# Node Purge (Batch Mode)

You are purifying a **batch** of raw (red) knowledge nodes into strongly-typed (blue) knowledge nodes. You will receive multiple red nodes at once and must return results for ALL of them.

## Blue Node Schema

Each blue node MUST have exactly these fields:

| Field | Required | Description |
|-------|----------|-------------|
| temp_id | Yes | Temporary ID for referencing within this node's output, format: b0, b1, b2, ... (resets per red node) |
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

## Splitting Rules

1. **Semantic atomicity**: Each blue node must express exactly ONE concept. If the red node contains multiple distinct concepts, split them.
2. **Progressive relationships**: If the content has a building/progressive structure (e.g. a definition followed by a theorem that uses it), split into separate nodes and connect them with edges.
3. **Single concept**: If the red node is already a single atomic concept, produce exactly one blue node. Do NOT split unnecessarily.
4. **No information loss**: After splitting, the union of all blue nodes' body fields MUST cover ALL information from the original red node. Nothing may be dropped.

## Edge Rules

When splitting produces multiple blue nodes for the same red node, connect related ones:

| edge_type | When to use |
|-----------|-------------|
| proves | One blue node constitutes a proof of another (e.g. proof -> theorem) |
| references | One blue node references, depends on, or builds upon another |

Only create edges between blue nodes from the SAME red node. Not every pair needs a connection.

## Language

All output MUST be in English. If the red node content is in another language, translate it.

## Batch Output Format

You receive multiple red nodes separated by `--- Red Node N of M ---` headers. Each red node has a `KG ID` field.

Return ONLY valid JSON with results for ALL red nodes:

```json
{
  "results": [
    {
      "kg_id": "KG-xxx-yyy",
      "blue_nodes": [
        { "temp_id": "b0", "type": "definition", "abstract": "...", "body": "..." },
        { "temp_id": "b1", "type": "theorem", "abstract": "...", "body": "..." }
      ],
      "blue_edges": [
        { "source": "b1", "target": "b0", "edge_type": "references" }
      ]
    },
    {
      "kg_id": "KG-aaa-bbb",
      "blue_nodes": [
        { "temp_id": "b0", "type": "proof", "abstract": "...", "body": "..." }
      ],
      "blue_edges": []
    }
  ]
}
```

Rules:
- The `results` array must contain one entry per red node, matched by `kg_id`
- Each entry's `blue_nodes` must be non-empty (at least one blue node per red node)
- `temp_id` starts at b0 and increments WITHIN each entry (resets per red node)
- `blue_edges` source/target must reference temp_ids from the SAME entry's blue_nodes
- edge_type must be "proves" or "references"
- Return results for ALL red nodes in the batch — do not skip any

## Checklist

Before returning your output, verify:

- [ ] Every red node from the input has a matching `kg_id` entry in results
- [ ] Every blue node has temp_id, type, abstract, body
- [ ] type is one of the 14 valid values listed above
- [ ] abstract is a single non-empty sentence
- [ ] body is non-empty and preserves all semantic information and math formulas from the red node
- [ ] All content is in English
- [ ] No information from any red node was lost
- [ ] blue_edges only reference temp_ids within the same entry
- [ ] edge_type is either "proves" or "references"
- [ ] Output is valid JSON

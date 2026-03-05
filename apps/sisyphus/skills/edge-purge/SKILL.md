---
name: edge-purge
description: Map a batch of raw (red) edges to strongly-typed (blue) edges between purified blue node groups
---

# Edge Purge (Batch Mode)

You are mapping a **batch** of raw (red) edges to strongly-typed (blue) edges between purified blue node groups. You will receive multiple red edges at once and must return results for ALL of them.

## Context

For each red edge, you receive:
1. **Original red edge**: source red node -> target red node, with edge_type
2. **Source blue node group UUIDs**: the 1..N blue nodes purified from the source red node
3. **Target blue node group UUIDs**: the 1..N blue nodes purified from the target red node

## Red-to-Blue Edge Type Mapping

| Red edge_type | Meaning | Blue edge_type |
|---------------|---------|----------------|
| inference_proof_anchor | Proof anchored to its statement | proves |
| inference_ref | General reference/dependency | references |

## Rules

1. **Anchor on the red edge**: Your job is to map the original red-to-red relationship into the blue layer. Do NOT invent new relationships that did not exist in the red layer.
2. **Selective mapping**: Not every source-blue x target-blue pair needs an edge. Only create edges that are semantically justified based on the blue nodes' content.
3. **Read the content**: Examine each blue node's type, abstract, and body to determine which specific connections make sense.
4. **Empty is valid**: If no meaningful blue-to-blue connection exists for a red edge, return an empty blue_edges array for that entry.

## Batch Output Format

You receive multiple red edges separated by `--- Red Edge N of M ---` headers. Each edge has `Source KG ID` and `Target KG ID` fields.

Return ONLY valid JSON with results for ALL red edges:

```json
{
  "results": [
    {
      "source_kg_id": "KG-xxx-yyy",
      "target_kg_id": "KG-aaa-bbb",
      "blue_edges": [
        { "source_id": "<source-blue-uuid>", "target_id": "<target-blue-uuid>", "edge_type": "references" }
      ]
    },
    {
      "source_kg_id": "KG-ccc-ddd",
      "target_kg_id": "KG-eee-fff",
      "blue_edges": []
    }
  ]
}
```

Rules:
- The `results` array must contain one entry per red edge, matched by `source_kg_id` + `target_kg_id`
- source_id must be a UUID from the Source Blue Node Group for that edge
- target_id must be a UUID from the Target Blue Node Group for that edge
- edge_type must be "proves" or "references"
- If no connections are needed for a red edge: return `"blue_edges": []`
- Return results for ALL red edges in the batch — do not skip any

## Checklist

Before returning your output, verify:

- [ ] Every red edge from the input has a matching entry in results (by source_kg_id + target_kg_id)
- [ ] Every source_id exists in the corresponding source blue node group
- [ ] Every target_id exists in the corresponding target blue node group
- [ ] edge_type is either "proves" or "references"
- [ ] Edges are semantically justified by the blue nodes' content
- [ ] Output is valid JSON

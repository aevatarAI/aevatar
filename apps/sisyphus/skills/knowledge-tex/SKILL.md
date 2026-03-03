---
name: knowledge-tex
description: Generate standardized TeX knowledge nodes for the Sisyphus knowledge graph with enforced structure, word limits, and explicit cross-references
---

# Knowledge Node TeX

You are an **Agent** producing knowledge for the Sisyphus knowledge graph. Every piece of knowledge you produce — whether extracted from a paper or derived through research — **must** be a structured `.tex` document following this specification exactly.

## When to Invoke

Whenever you need to produce knowledge nodes, including:

- Extracting claims, theorems, methods, or results from academic papers (Paper Ingestion)
- Producing new hypotheses, inferences, or observations during research (Research Session)
- Updating or revising existing knowledge nodes (Knowledge Review)

**Always invoke this skill before generating any knowledge node.** Do not produce free-form JSON claims or unstructured text.

---

## Node Template

Every knowledge node must follow this template exactly. Do not omit required fields. Do not add fields not listed here.

```latex
% --- Metadata (all required) ---
\nodeid{<id>}
\nodetype{<type>}
\confidence{<0.0-1.0>}
\source{<ingestion|research>}
\sourceref{<paper-node-id|session-id>}

% --- Abstract (REQUIRED, max 150 words) ---
\begin{abstract}
<A single self-contained paragraph summarizing the core knowledge claim.
 A reader must understand the node without reading the body.>
\end{abstract}

% --- Body ---
\begin{document}

\section{Claim}
<The complete statement of the knowledge claim. REQUIRED, max 300 words.>

\section{Evidence}
<Supporting evidence, reasoning, or experimental results. REQUIRED, max 500 words.
 Use \noderef and \cite here to reference sources.>

\section{Context}
<Supplementary context: assumptions, scope, applicability conditions.
 OPTIONAL, max 300 words. Omit this section entirely if not needed.>

\section{Formal}
<Formal/mathematical statement. OPTIONAL, max 200 words.
 Only include for theorem, method, or result nodes that have formal notation.
 Omit this section entirely if not needed.>

\end{document}
```

---

## Metadata Fields

| Field              | Required | Value                     | Notes                                                                                             |
| ------------------ | -------- | ------------------------- | ------------------------------------------------------------------------------------------------- |
| `\nodeid{...}`     | Yes      | UUID or temp ID           | Use system-assigned UUID. When creating new nodes in a batch, use temp IDs: `t0`, `t1`, `t2`, ... |
| `\nodetype{...}`   | Yes      | One of the types below    | Must exactly match a valid type                                                                   |
| `\confidence{...}` | Yes      | Float `0.0` to `1.0`      | Your confidence in the claim's correctness                                                        |
| `\source{...}`     | Yes      | `ingestion` or `research` | `ingestion` = extracted from a paper, `research` = produced by research reasoning                 |
| `\sourceref{...}`  | Yes      | ID string                 | Paper Ingestion: the `source_paper` node ID. Research: the session ID                             |

---

## Node Types

Use **exactly** one of these values for `\nodetype{}`:

| Type           | When to use                                                                         |
| -------------- | ----------------------------------------------------------------------------------- |
| `hypothesis`   | An unverified claim or conjecture that needs further evidence                       |
| `fact`         | A confirmed, well-established piece of knowledge                                    |
| `inference`    | A conclusion derived by reasoning from other nodes                                  |
| `definition`   | A concept definition or terminology clarification                                   |
| `theorem`      | A formally stated theorem, lemma, corollary, or proposition                         |
| `method`       | An algorithm, technique, methodology, or experimental procedure                     |
| `result`       | An experimental result, measurement, or empirical observation                       |
| `observation`  | An insight or qualitative observation that does not rise to a formal claim          |
| `source_paper` | Paper metadata node (only created by the ingestion pipeline, never by you manually) |

**Choosing the right type:**

- If the claim is speculative or lacks strong evidence → `hypothesis`
- If the claim is well-supported and widely accepted → `fact`
- If you derived the claim by reasoning from existing nodes → `inference`
- If the original text presents it as a theorem/lemma/proof → `theorem`
- If it describes how to do something → `method`
- If it reports numbers, benchmarks, or experimental outcomes → `result`

---

## Cross-References

### Referencing other knowledge nodes: `\noderef`

```latex
\noderef[<relation>]{<target-node-id>}
```

Use `\noderef` **inside** the `Evidence` or `Context` sections to declare how this node relates to another node. The application layer will parse these into graph edges automatically.

**Available relations:**

| Relation       | Meaning                                              | Example use                                      |
| -------------- | ---------------------------------------------------- | ------------------------------------------------ |
| `supports`     | This node provides evidence supporting the target    | Experimental result supports a hypothesis        |
| `contradicts`  | This node presents evidence contradicting the target | New finding disproves an earlier claim           |
| `extends`      | This node generalizes or builds upon the target      | Extending a theorem to a broader domain          |
| `depends_on`   | This node's validity requires the target to be true  | An inference that depends on a definition        |
| `derived_from` | This node was logically derived from the target      | A corollary derived from a theorem               |
| `proves`       | This node constitutes a proof of the target          | A proof node linked to its theorem               |
| `evaluates`    | This node evaluates or benchmarks the target         | A result node evaluating a method                |
| `formalizes`   | This node provides a formal statement of the target  | A mathematical formulation of an intuitive claim |

**Rules:**

- Every node (except the very first node in a session) must include **at least one** `\noderef` in its Evidence section
- Use the **correct relation** — do not default to `supports` for everything
- Reference by node ID: either a real UUID (`node-7c3d9e`) or a temp ID (`t0`) from the current batch
- You may reference multiple nodes: `\noderef[depends_on]{node-abc} and \noderef[extends]{node-def}`

### Referencing external literature: `\cite`

```latex
\cite{<bibtex-key>}
```

Use `\cite` to reference papers from the bibliography of the source paper. The bibtex key must match an entry in the `source_paper` node's Bibliography section.

---

## Word Limits

These limits are **hard constraints**. Exceeding them will cause validation failure.

| Section                                      | Required | Max words |
| -------------------------------------------- | -------- | --------- |
| `\begin{abstract}...\end{abstract}`          | **Yes**  | **150**   |
| `\section{Claim}`                            | **Yes**  | **300**   |
| `\section{Evidence}`                         | **Yes**  | **500**   |
| `\section{Context}`                          | No       | 300       |
| `\section{Formal}`                           | No       | 200       |
| **Entire node body** (all sections combined) | —        | **1200**  |

**If you find yourself exceeding a limit**, split the knowledge into two separate nodes rather than cramming everything into one.

---

## Abstract Quality Rules

The abstract is the most critical field — it is used for context projection when the graph grows large. Other agents will read **only your abstract** to decide whether to look at your full node.

**Good abstract:**

- Self-contained: understandable without reading the body
- Specific: states the actual claim, not just the topic
- Concise: uses every word to convey information

**Bad abstract (do NOT write like this):**

- "This node discusses the relationship between X and Y." ← Too vague, says nothing specific
- "Based on the evidence presented below, we conclude that..." ← Not self-contained, references body
- Copy-pasting the first sentence of the Claim section ← Redundant, wastes the abstract

---

## Output Format

When producing nodes, output them as a JSON array where each element has `temp_id` and `tex_content`:

```json
{
  "nodes": [
    {
      "temp_id": "t0",
      "tex_content": "\\nodeid{t0}\n\\nodetype{definition}\n\\confidence{0.95}\n\\source{research}\n\\sourceref{session-abc}\n\n\\begin{abstract}\n...\n\\end{abstract}\n\n\\begin{document}\n\n\\section{Claim}\n...\n\n\\section{Evidence}\n...\n\n\\end{document}"
    },
    {
      "temp_id": "t1",
      "tex_content": "\\nodeid{t1}\n\\nodetype{inference}\n..."
    }
  ]
}
```

**Rules:**

- Temp IDs start at `t0` and increment: `t0`, `t1`, `t2`, ...
- Within a batch, nodes may reference each other's temp IDs via `\noderef`
- The `\nodeid` in the tex content must match the `temp_id` in the JSON wrapper
- Produce **1 to 8 nodes per batch**, quality over quantity

---

## Scenario-Specific Guidance

### Paper Ingestion

You are reading a chunk of an academic paper and extracting knowledge.

- Set `\source{ingestion}` and `\sourceref{<paper-node-id>}` (provided in your prompt)
- Extract **distinct knowledge claims** — do not create one node per paragraph; identify the actual claims
- Prefer `fact`, `theorem`, `method`, `result` types — papers contain established knowledge
- Use `\cite{key}` to reference the paper's own bibliography entries
- Skip trivial content: acknowledgements, formatting descriptions, future work vagueness
- For theorems with proofs, create separate nodes: one `theorem` node + one node with `\noderef[proves]{theorem-id}`

### Research Session

You are conducting autonomous research and producing new knowledge.

- Set `\source{research}` and `\sourceref{<session-id>}` (provided in your prompt)
- Prefer `hypothesis`, `inference`, `observation` types — research produces new, often unverified knowledge
- Every node **must** connect to existing graph knowledge via `\noderef`
- Set `\confidence` conservatively — hypotheses should be 0.4-0.7, inferences 0.6-0.85
- If you cannot find gaps to fill or new claims to make, signal saturation rather than producing low-quality nodes

---

## Examples

### Example 1: Theorem extracted from a paper

```latex
\nodeid{t0}
\nodetype{theorem}
\confidence{0.98}
\source{ingestion}
\sourceref{paper-2f8a3b}

\begin{abstract}
The Transformer model achieves 28.4 BLEU on WMT 2014 English-to-German
and 41.0 BLEU on English-to-French translation, surpassing all prior
architectures while requiring significantly less training time.
\end{abstract}

\begin{document}

\section{Claim}
The Transformer architecture, based solely on attention mechanisms without
recurrence or convolution, achieves 28.4 BLEU on WMT 2014 English-to-German,
improving over the best prior results by over 2 BLEU. On English-to-French,
it achieves 41.0 BLEU, surpassing all previously published single models.

\section{Evidence}
Results reported in Table 2 of the source paper demonstrate consistent
improvements across both language pairs. Training completed in 3.5 days on
8 P100 GPUs, compared to weeks for prior state-of-the-art \cite{wu2016google}.
The model's self-attention layers replace recurrence entirely, enabling
greater parallelization.

\section{Formal}
$\text{BLEU}_{\text{en} \to \text{de}} = 28.4, \quad
 \text{BLEU}_{\text{en} \to \text{fr}} = 41.0$

\end{document}
```

### Example 2: Inference produced during research

```latex
\nodeid{t0}
\nodetype{inference}
\confidence{0.72}
\source{research}
\sourceref{session-5f6g7h}

\begin{abstract}
Self-attention's quadratic complexity in sequence length limits Transformer
applicability to long documents. Sparse attention patterns reducing complexity
to O(n sqrt(n)) preserve comparable performance for sequences up to 8192 tokens.
\end{abstract}

\begin{document}

\section{Claim}
The O(n^2) memory and time complexity of self-attention in the original
Transformer creates a practical bottleneck for long sequences. Sparse
attention variants achieve sub-quadratic complexity while retaining
most representational capacity.

\section{Evidence}
This inference derives from the attention design in
\noderef[derived_from]{node-7c3d9e} and is supported by benchmarks in
\noderef[supports]{node-d4e5f6} showing structured sparsity achieves 95%+
of dense attention performance on long-range tasks.

\section{Context}
Particularly relevant for knowledge graph applications where summarizing
thousands of nodes requires processing long concatenated contexts.

\end{document}
```

### Example 3: Definition produced during research

```latex
\nodeid{t0}
\nodetype{definition}
\confidence{0.95}
\source{research}
\sourceref{session-5f6g7h}

\begin{abstract}
Scaled dot-product attention computes a weighted sum of value vectors,
where weights are determined by the softmax of query-key dot products
scaled by the inverse square root of the key dimension.
\end{abstract}

\begin{document}

\section{Claim}
Scaled dot-product attention is defined as
Attention(Q, K, V) = softmax(QK^T / sqrt(d_k)) V, where Q, K, V are
the query, key, and value matrices respectively, and d_k is the
dimensionality of the key vectors. The scaling factor prevents the
dot products from growing too large in magnitude, which would push
the softmax into regions with extremely small gradients.

\section{Evidence}
This definition is established in \noderef[derived_from]{node-7c3d9e}
as the fundamental building block of the Transformer architecture.

\section{Formal}
$\text{Attention}(Q, K, V) = \text{softmax}\!\left(\frac{QK^T}{\sqrt{d_k}}\right) V$

\end{document}
```

---

## Checklist

Before submitting your nodes, verify each one against this checklist:

- [ ] `\nodeid`, `\nodetype`, `\confidence`, `\source`, `\sourceref` are all present
- [ ] `\nodetype` is one of the 9 valid types listed above
- [ ] `\confidence` is a float between 0.0 and 1.0
- [ ] `\begin{abstract}...\end{abstract}` is present and ≤150 words
- [ ] Abstract is self-contained (understandable without reading the body)
- [ ] `\section{Claim}` is present and ≤300 words
- [ ] `\section{Evidence}` is present and ≤500 words
- [ ] At least one `\noderef[relation]{id}` exists (unless this is the first node in the graph)
- [ ] All `\noderef` relations use a valid relation type
- [ ] All referenced node IDs are either real UUIDs or temp IDs from the current batch
- [ ] Optional sections (`Context`, `Formal`) are omitted entirely if empty — do not include empty sections
- [ ] Total body word count ≤1200
- [ ] Output is valid JSON with `temp_id` and `tex_content` fields

---

## Constraints

- This skill defines output format only. It does not read or write files.
- Do not invent node IDs — use temp IDs (`t0`, `t1`, ...) for new nodes and real UUIDs (from your prompt context) for existing nodes.
- Do not create `source_paper` nodes — those are created by the application layer during ingestion.
- Do not produce nodes for trivial or redundant content. Quality over quantity.
- If the source material does not contain enough substance for a valid node, skip it rather than padding.

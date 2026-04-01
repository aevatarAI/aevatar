/**
 * A single node from the knowledge graph representing a mathematical statement or construct.
 * Each node contains TeX content in its properties that will be sanitized, repaired if broken,
 * and rendered into the appropriate amsthm environment in the final LaTeX document.
 */
export interface GraphNode {
  /**
   * Unique node identifier (UUID)
   */
  id: string;

  /**
   * Node type from whitelist: theorem, lemma, definition, proof, corollary, conjecture,
   * proposition, remark, conclusion, example, notation, axiom, observation, note
   */
  type: string;

  /**
   * Node properties including 'body' (TeX content) and 'abstract' (summary).
   * The 'body' field contains the raw LaTeX source for this node. Broken or malformed
   * TeX is automatically sanitized and repaired to guarantee successful compilation.
   */
  properties: Record<string, unknown>;
}

/**
 * A directed edge between two knowledge graph nodes, representing a dependency relationship.
 * Edges are used for topological sorting (to determine document order) and for generating
 * cross-references in the final LaTeX document.
 */
export interface GraphEdge {
  /**
   * Source node ID — the node that references or proves the target
   */
  source: string;

  /**
   * Target node ID — the node being referenced or proved
   */
  target: string;

  /**
   * Edge relationship type: 'proves' or 'references'.
   * 'proves' indicates the source is a proof of the target.
   * 'references' indicates the source cites or depends on the target.
   */
  edge_type: string;
}

/**
 * Request body for the paper compilation endpoint.
 * Contains the knowledge graph nodes and edges to compile into a LaTeX research paper.
 * The compilation pipeline: sanitize TeX → topologically sort by edges → generate LaTeX → compile to PDF with tectonic.
 */
export interface CompileRequest {
  /**
   * Array of black (verified) knowledge graph nodes to compile into a paper.
   * Must be non-empty and contain at most 10,000 nodes.
   */
  nodes: GraphNode[];

  /**
   * Array of edges defining dependencies between nodes.
   * Used for topological ordering (so referenced nodes appear before referencing nodes)
   * and for generating cross-reference links in the compiled document.
   */
  edges: GraphEdge[];
}

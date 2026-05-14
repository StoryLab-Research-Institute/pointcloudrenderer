# Performance Roadmap

Two high-impact optimisations deferred from the initial BVH implementation.
They share prerequisites and are best implemented together.

---

## Scale context

Target clouds span hundreds of metres with 2 m maximum node side length, giving
roughly 2,500+ leaf nodes in the horizontal plane and ~5,000+ total BVH nodes.
At this scale, hundreds to low-thousands of nodes will be selected per frame.
**Draw call count is the dominant bottleneck on Quest 3** at this node count —
2c should be prioritised; 2a is a follow-on if `SelectNodes` remains a hotspot
after 2c lands.

---

## 2c — GPU Indirect Draw (merged buffer) *(highest priority)*

**Win:** Reduces N draw calls (one per selected node) to a single
`DrawProceduralIndirect`. On Quest 3 (Snapdragon XR2 Gen 2) mobile draw-call
overhead is significant — with hundreds of nodes selected this could save
several ms GPU-side. Likely the highest single-impact change for Quest 3.

**How:** At load time, allocate one global `GraphicsBuffer` containing all
nodes' points concatenated, recording each node's byte offset. Each frame,
after node selection, populate an indirect-args `GraphicsBuffer` (one entry per
selected node: vertex count + offset) via a `CommandBuffer` fill. Replace the
per-node `DrawProcedural` loop with a single `DrawProceduralIndirect` call.
The CPU-side loop becomes simpler; complexity moves to the GPU.

**Prerequisite:** BVH nodes must be in a flat indexed layout — per-node
`ComputeBuffer`s replaced with offsets into the global buffer, and the managed
`BVHNode` class tree replaced with a flat `NativeArray<NodeData>`. This is the
same restructuring needed for 2a. **This refactor is the highest-risk step —
validate it in isolation before proceeding.**

---

## 2a — Jobs + Burst for per-node maths *(follow-on)*

**Win:** Potential 2–4× reduction in `SelectNodes` CPU time. Worth doing only
if profiling shows `SelectNodes` is still a hotspot after 2c lands.

**Note on scope:** The existing heap traversal in `HeapPush` already frustum-culls
inline as nodes are pushed, so there is no clean "frustum cull first, then
parallelise" split — the traversal order is data-dependent and the culling is
an opportunistic early-exit within it. The practical parallelisation target is
the **selected node list** (the final draw frontier, hundreds to low-thousands
of nodes), not the full BVH tree.

**How:** After the serial heap traversal produces `_selectedIndices`, run an
`IJobParallelFor` over those selected nodes to compute foveation T and LOD
scale in parallel. Results feed the indirect-args buffer fill for 2c. Burst
compiles the job to ARM64 SIMD automatically.

**Prerequisite:** Same flat `NativeArray<NodeData>` layout as 2c.

---

## Combined implementation path

1. Restructure `BVHAsset` load to allocate one global `GraphicsBuffer` and
   record per-node offsets and counts in a `NativeArray<NodeData>` struct.
2. Replace managed `BVHNode` class with an index into that array everywhere in
   `PointCloudRenderer`. **Validate thoroughly before continuing.**
3. Each frame, after the serial heap traversal, use the selected-node index list
   to populate an indirect-args `GraphicsBuffer` (vertex count + buffer offset
   per selected node) via `CommandBuffer` fill (2c).
4. Replace the `DrawProcedural` loop with `DrawProceduralIndirect` (2c).
5. Profile. If `SelectNodes` is still a bottleneck, move per-node foveation/LOD
   maths into an `IJobParallelFor` over the selected set (2a).

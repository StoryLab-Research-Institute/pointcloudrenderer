# Performance Roadmap

Two high-impact optimisations deferred from the initial BVH implementation.
They share prerequisites and are best implemented together.

---

## 2a — Jobs + Burst for per-node maths

**Win:** 2–4× reduction in `SelectNodes` CPU time. On Quest 3 this could move from
~3–4 ms to under 1 ms for a large cloud.

**How:** The BVH traversal order is data-dependent and stays serial, but the
per-node work — `TransformBounds`, `TestAABBFrustum`, screen-error calculation,
foveation T — is independent once the node is known. Run these in an
`IJobParallelFor` over all BVH nodes each frame, writing results into
`NativeArray<float>` (screen error) and `NativeArray<bool>` (frustum pass).
The serial traversal loop then reads pre-computed values instead of computing
inline. Burst compiles the job to ARM64 SIMD automatically.

**Prerequisite:** BVH nodes must be in a flat `NativeArray` (struct-of-arrays),
not a managed class tree. This is the same restructuring needed for 2c.

---

## 2c — GPU Indirect Draw (merged buffer)

**Win:** Reduces N draw calls (one per selected node) to a single
`DrawProceduralIndirect`. On Quest 3 (Snapdragon XR2 Gen 2) mobile draw-call
overhead is significant — with 50–200 nodes selected this could save 1–2 ms
GPU-side. Likely the highest single-impact change for Quest 3.

**How:** At load time, allocate one global `GraphicsBuffer` containing all
nodes' points concatenated, recording each node's byte offset. Each frame,
after node selection, populate an indirect-args `GraphicsBuffer` (one entry per
selected node: vertex count + offset) via a small compute shader or
`CommandBuffer` fill. Replace the per-node `DrawProcedural` loop with a single
`DrawProceduralIndirect` call. The CPU-side loop becomes simpler; complexity
moves to the GPU.

**Prerequisite:** Same flat indexed layout as 2a — per-node `ComputeBuffer`s
must be replaced with offsets into the global buffer.

---

## Combined implementation path

1. Restructure `BVHAsset` load to allocate one global `GraphicsBuffer` and
   record per-node offsets and counts in a `NativeArray<NodeData>` struct.
2. Replace managed `BVHNode` class with an index into that array everywhere in
   `PointCloudRenderer`.
3. Write the `IJobParallelFor` for per-node frustum + error + foveation (2a).
4. After job completion, write a compact selected-node index list and use it to
   fill the indirect-args buffer (2c).
5. Replace the `DrawProcedural` loop with `DrawProceduralIndirect`.

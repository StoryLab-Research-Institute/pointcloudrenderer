using UnityEngine;

namespace StoryLabResearch.PointCloud
{
    public class BVHNode
    {
        public Bounds  Bounds;    // kept for cold-path sphere calc in RefreshCullingGroup
        public Vector3 BoundsMin; // plain fields — no interop cost on hot path
        public Vector3 BoundsSize;
        public int Depth;
        public BVHNode Left;
        public BVHNode Right;

        public int   PointCount;         // points stored at this node level (subsampled at internal nodes)
        public int   OriginalCount;      // pre-subsampling count (equals PointCount for leaves)
        public int   GlobalBufferOffset; // offset in points into BVHAsset.GlobalPointBuffer
        public float LodScaleBase;       // sqrt(OriginalCount / PointCount) — precomputed at load time

        public bool IsLoaded => PointCount > 0;
        public bool IsLeaf   => Left == null && Right == null;
    }
}

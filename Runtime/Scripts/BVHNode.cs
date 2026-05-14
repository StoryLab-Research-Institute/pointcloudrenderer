using UnityEngine;

namespace StoryLabResearch.PointCloud
{
    public class BVHNode
    {
        public Bounds Bounds;
        public int Depth;
        public BVHNode Left;
        public BVHNode Right;

        // PropertyBlock holds _BoundsMin, _BoundsSize, and _PointOffset for this node.
        // Points themselves live in BVHAsset.GlobalPointBuffer (no per-node ComputeBuffer).
        public MaterialPropertyBlock PropertyBlock;
        public int PointCount;        // points stored at this node level (subsampled at internal nodes)
        public int OriginalCount;     // pre-subsampling count (equals PointCount for leaves)
        public int GlobalBufferOffset; // offset in points into BVHAsset.GlobalPointBuffer

        public int IndexInRenderer = -1; // set by PointCloudRenderer during node-array rebuild

        public bool IsLoaded => PropertyBlock != null;
        public bool IsLeaf   => Left == null && Right == null;

        public void ReleaseBuffers()
        {
            PropertyBlock = null;
            PointCount    = 0;
            OriginalCount = 0;
        }
    }
}

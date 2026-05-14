using UnityEngine;

namespace StoryLabResearch.PointCloud
{
    public class BVHNode
    {
        public Bounds Bounds;
        public int Depth;
        public BVHNode Left;
        public BVHNode Right;

        // Single interleaved buffer: uint3 per point (12 bytes).
        //   word0 = (uint16_y << 16) | uint16_x  — XY quantized [0,65535] relative to node bounds
        //   word1 = RGB24 in bits 0-23            — bits 24-31 unused
        //   word2 = uint16_z in bits 0-15         — Z quantized, upper 16 bits spare
        public ComputeBuffer PointBuffer; // stride 12, uint3 per point
        public MaterialPropertyBlock PropertyBlock;
        public int PointCount;        // points stored at this node level (subsampled at internal nodes)
        public int OriginalCount;     // pre-subsampling count (equals PointCount for leaves)

        public int IndexInRenderer = -1; // set by PointCloudRenderer during node-array rebuild

        public bool IsLoaded => PointBuffer != null;
        public bool IsLeaf   => Left == null && Right == null;

        public void ReleaseBuffers()
        {
            PointBuffer?.Release();
            PointBuffer   = null;
            PropertyBlock = null;
            PointCount    = 0;
            OriginalCount = 0;
        }
    }
}

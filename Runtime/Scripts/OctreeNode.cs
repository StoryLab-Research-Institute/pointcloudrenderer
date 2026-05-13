using UnityEngine;

namespace StoryLabResearch.PointCloud
{
    public class OctreeNode
    {
        public Bounds Bounds;
        public int Depth;
        public OctreeNode[] Children; // [8], null entry = no child for that octant

        // Single interleaved buffer: uint3 per point (12 bytes).
        //   word0 = (uint16_y << 16) | uint16_x  — XY quantized [0,65535] relative to node bounds
        //   word1 = (octant << 24) | RGB24        — octant in bits 24-26, colour in bits 0-23
        //   word2 = uint16_z in bits 0-15
        public ComputeBuffer PointBuffer; // stride 12, uint3 per point
        public MaterialPropertyBlock PropertyBlock;
        public int TotalPointCount;

        public int[] OctantPointCounts;    // [8] — kept points per octant
        public int[] OctantOriginalCounts; // [8] — pre-subsampling count (equals OctantPointCounts for leaves)

        public bool IsLoaded => PointBuffer != null;
        public bool IsLeaf => Children == null;

        public void ReleaseBuffers()
        {
            PointBuffer?.Release();
            PointBuffer = null;
            PropertyBlock = null;
            OctantPointCounts = null;
            OctantOriginalCounts = null;
            TotalPointCount = 0;
        }
    }
}

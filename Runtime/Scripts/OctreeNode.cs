using UnityEngine;

namespace StoryLabResearch.PointCloud
{
    public class OctreeNode
    {
        public Bounds Bounds;
        public int Depth;
        public OctreeNode[] Children; // [8], null entry = no child for that octant

        // Merged buffers: all octants concatenated in order 0-7.
        // OctantIndexBuffer stores a per-point uint octant index for shader masking.
        public ComputeBuffer MergedPositionBuffer;  // stride 12, float3 per point
        public ComputeBuffer MergedColorBuffer;     // stride 4,  uint RGBA8 per point
        public ComputeBuffer OctantIndexBuffer;     // stride 4,  uint octant index per point
        public MaterialPropertyBlock PropertyBlock;
        public int TotalPointCount;

        public int[] OctantPointCounts;    // [8] — kept points per octant
        public int[] OctantOriginalCounts; // [8] — pre-subsampling count (equals OctantPointCounts for leaves)
        public int[] OctantStarts;         // [8] — start index in merged buffer per octant

        public bool IsLoaded => MergedPositionBuffer != null;
        public bool IsLeaf => Children == null;

        public void ReleaseBuffers()
        {
            MergedPositionBuffer?.Release();
            MergedColorBuffer?.Release();
            OctantIndexBuffer?.Release();
            MergedPositionBuffer = null;
            MergedColorBuffer = null;
            OctantIndexBuffer = null;
            PropertyBlock = null;
            OctantPointCounts = null;
            OctantOriginalCounts = null;
            OctantStarts = null;
            TotalPointCount = 0;
        }
    }
}

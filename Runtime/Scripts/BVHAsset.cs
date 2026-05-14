using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace StoryLabResearch.PointCloud
{
    [CreateAssetMenu(fileName = "BVHAsset", menuName = "StoryLab PointCloud/BVH Asset")]
    public class BVHAsset : ScriptableObject
    {
        [Serializable]
        private struct NodeMetadata
        {
            public Bounds Bounds;
            public int    Depth;
            public int    PointCount;     // kept after subsampling
            public int    OriginalCount;  // before subsampling (equals PointCount for leaves)
            public int    ByteLength;     // byte count in _pointData for this node
            public int    LeftIndex;      // index into _nodeMetadata, or -1
            public int    RightIndex;     // index into _nodeMetadata, or -1
        }

        // Flat per-node data exposed to PointCloudRenderer for indirect draw.
        public struct NodeData
        {
            public Bounds Bounds;
            public int    PointCount;
            public int    OriginalCount;
            public int    GlobalBufferOffset; // offset in points (not bytes) into GlobalPointBuffer
        }

        [SerializeField, HideInInspector] private NodeMetadata[] _nodeMetadata;
        [SerializeField, HideInInspector] private byte[]         _pointData;

        private BVHNode[]      _nodes;
        private bool           _loaded;

        // Global merged GPU buffer: all nodes' points concatenated (uint3 per point, 12 bytes each).
        public GraphicsBuffer  GlobalPointBuffer { get; private set; }
        // Flat array parallel to the BVH node array, indexed by BVHNode.IndexInRenderer.
        public NodeData[]      NodeDataArray     { get; private set; }

        public BVHNode Root => (_nodes != null && _nodes.Length > 0) ? _nodes[0] : null;

        public void Load()
        {
            if (_loaded) return;
            if (_nodeMetadata == null || _nodeMetadata.Length == 0) return;
            if (_pointData == null || _pointData.Length == 0)
            {
                Debug.LogError($"[BVHAsset] '{name}' has no embedded point data.");
                return;
            }

            int count = _nodeMetadata.Length;
            _nodes = new BVHNode[count];

            // First pass: create node objects.
            for (int i = 0; i < count; i++)
            {
                var meta = _nodeMetadata[i];
                _nodes[i] = new BVHNode
                {
                    Bounds        = meta.Bounds,
                    Depth         = meta.Depth,
                    PointCount    = meta.PointCount,
                    OriginalCount = meta.OriginalCount,
                };
            }

            // Second pass: wire up children.
            for (int i = 0; i < count; i++)
            {
                var meta = _nodeMetadata[i];
                if (meta.LeftIndex  >= 0) _nodes[i].Left  = _nodes[meta.LeftIndex];
                if (meta.RightIndex >= 0) _nodes[i].Right = _nodes[meta.RightIndex];
            }

            // Third pass: count total points and allocate the global buffer.
            // Binary layout: for each node, PointCount uint3 records (12 bytes each).
            //   word0 = (uint16_y << 16) | uint16_x  — XY quantized relative to node bounds
            //   word1 = RGB24 in bits 0-23            — bits 24-31 unused/zero
            //   word2 = uint16_z in bits 0-15         — Z quantized, upper 16 bits spare
            int totalPoints = 0;
            for (int i = 0; i < count; i++)
                totalPoints += _nodeMetadata[i].PointCount;

            // stride=12 (uint3), Raw allows vertex/index/structured access needed for indirect.
            GlobalPointBuffer = new GraphicsBuffer(
                GraphicsBuffer.Target.Raw,
                totalPoints * 3, // element count in uint (4 bytes each), 3 uints per point
                sizeof(uint));

            NodeDataArray = new NodeData[count];

            // Fourth pass: upload each node's points into the global buffer at its offset.
            int fileOffset   = 0;
            int bufferOffset = 0; // in points
            for (int i = 0; i < count; i++)
            {
                var meta = _nodeMetadata[i];
                var node = _nodes[i];

                int nodeOffset = bufferOffset;

                if (meta.PointCount > 0 && meta.ByteLength > 0)
                {
                    int pts    = meta.PointCount;
                    var packed = new uint[pts * 3];
                    Buffer.BlockCopy(_pointData, fileOffset, packed, 0, meta.ByteLength);
                    GlobalPointBuffer.SetData(packed, 0, bufferOffset * 3, pts * 3);
                    bufferOffset += pts;
                }

                fileOffset += meta.ByteLength;

                NodeDataArray[i] = new NodeData
                {
                    Bounds              = meta.Bounds,
                    PointCount          = meta.PointCount,
                    OriginalCount       = meta.OriginalCount,
                    GlobalBufferOffset  = nodeOffset,
                };

                // PropertyBlock carries per-node uniforms: bounds and offset into the global buffer.
                // _Points is bound once on the material; the shader uses _PointOffset to find this node's data.
                node.GlobalBufferOffset = nodeOffset;
                var bn = meta.Bounds;
                node.PropertyBlock = new MaterialPropertyBlock();
                node.PropertyBlock.SetVector("_BoundsMin",   new Vector4(bn.min.x,  bn.min.y,  bn.min.z,  0));
                node.PropertyBlock.SetVector("_BoundsSize",  new Vector4(bn.size.x, bn.size.y, bn.size.z, 0));
                node.PropertyBlock.SetInt("_PointOffset", nodeOffset);
            }

            _loaded = true;
        }

        public void Unload()
        {
            if (!_loaded || _nodes == null) return;
            foreach (var node in _nodes)
                node.ReleaseBuffers();
            GlobalPointBuffer?.Release();
            GlobalPointBuffer = null;
            NodeDataArray     = null;
            _nodes  = null;
            _loaded = false;
        }

        // Called by BVHBuilder to populate the asset before embedding it as a sub-asset.
        [Serializable]
        public struct PublicNodeMetadata
        {
            public Bounds Bounds;
            public int    Depth;
            public int    PointCount;
            public int    OriginalCount;
            public int    ByteLength;
            public int    LeftIndex;
            public int    RightIndex;
        }

        public void SetData(PublicNodeMetadata[] metadata, byte[] pointData)
        {
            _pointData = pointData;
            int count  = metadata.Length;
            _nodeMetadata = new NodeMetadata[count];
            for (int i = 0; i < count; i++)
            {
                _nodeMetadata[i] = new NodeMetadata
                {
                    Bounds        = metadata[i].Bounds,
                    Depth         = metadata[i].Depth,
                    PointCount    = metadata[i].PointCount,
                    OriginalCount = metadata[i].OriginalCount,
                    ByteLength    = metadata[i].ByteLength,
                    LeftIndex     = metadata[i].LeftIndex,
                    RightIndex    = metadata[i].RightIndex,
                };
            }
            _loaded = false;
            _nodes  = null;
        }

        private void OnDisable() => Unload();
    }
}

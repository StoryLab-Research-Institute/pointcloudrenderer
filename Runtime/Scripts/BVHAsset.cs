using System;
using UnityEngine;

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

        [SerializeField, HideInInspector] private NodeMetadata[] _nodeMetadata;
        [SerializeField, HideInInspector] private byte[]         _pointData;

        private BVHNode[] _nodes;
        private bool      _loaded;

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

            // Third pass: upload GPU buffers.
            // Binary layout: for each node, PointCount uint3 records (12 bytes each).
            //   word0 = (uint16_y << 16) | uint16_x  — XY quantized relative to node bounds
            //   word1 = RGB24 in bits 0-23            — bits 24-31 unused/zero
            //   word2 = uint16_z in bits 0-15         — Z quantized, upper 16 bits spare
            int fileOffset = 0;
            for (int i = 0; i < count; i++)
            {
                var meta = _nodeMetadata[i];
                var node = _nodes[i];

                if (meta.PointCount == 0 || meta.ByteLength == 0)
                {
                    fileOffset += meta.ByteLength;
                    continue;
                }

                int pts    = meta.PointCount;
                var packed = new uint[pts * 3];
                Buffer.BlockCopy(_pointData, fileOffset, packed, 0, meta.ByteLength);
                fileOffset += meta.ByteLength;

                node.PointBuffer = new ComputeBuffer(pts, 12);
                node.PointBuffer.SetData(packed);

                var bn = meta.Bounds;
                node.PropertyBlock = new MaterialPropertyBlock();
                node.PropertyBlock.SetBuffer("_Points",     node.PointBuffer);
                node.PropertyBlock.SetVector("_BoundsMin",  new Vector4(bn.min.x,  bn.min.y,  bn.min.z,  0));
                node.PropertyBlock.SetVector("_BoundsSize", new Vector4(bn.size.x, bn.size.y, bn.size.z, 0));
            }

            _loaded = true;
        }

        public void Unload()
        {
            if (!_loaded || _nodes == null) return;
            foreach (var node in _nodes)
                node.ReleaseBuffers();
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

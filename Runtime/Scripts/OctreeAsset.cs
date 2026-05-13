using System;
using UnityEngine;

namespace StoryLabResearch.PointCloud
{
    [CreateAssetMenu(fileName = "OctreeAsset", menuName = "StoryLab PointCloud/Octree Asset")]
    public class OctreeAsset : ScriptableObject
    {
        [Serializable]
        private struct NodeMetadata
        {
            public Bounds Bounds;
            public int Depth;
            public int[] OctantPointCounts;    // [8] — kept after subsampling
            public int[] OctantOriginalCounts; // [8] — before subsampling (equals OctantPointCounts for leaves)
            public int[] OctantPosLengths;     // [8]
            public int[] OctantColLengths;     // [8]
            public int[] ChildIndices;         // [8], -1 = absent
        }

        [SerializeField, HideInInspector] private NodeMetadata[] _nodeMetadata;
        // Raw point binary data, embedded directly in the asset (no external .bin file).
        [SerializeField, HideInInspector] private byte[] _pointData;

        private OctreeNode[] _nodes;
        private bool _loaded;

        public OctreeNode Root => (_nodes != null && _nodes.Length > 0) ? _nodes[0] : null;

        public void Load()
        {
            if (_loaded) return;
            if (_nodeMetadata == null || _nodeMetadata.Length == 0) return;
            if (_pointData == null || _pointData.Length == 0)
            {
                Debug.LogError($"[OctreeAsset] '{name}' has no embedded point data.");
                return;
            }

            var fileBytes = _pointData;
            int count = _nodeMetadata.Length;
            _nodes = new OctreeNode[count];

            // First pass: create node objects.
            for (int i = 0; i < count; i++)
            {
                var meta = _nodeMetadata[i];
                _nodes[i] = new OctreeNode
                {
                    Bounds = meta.Bounds,
                    Depth  = meta.Depth,
                };
            }

            // Second pass: wire up children.
            for (int i = 0; i < count; i++)
            {
                var childIndices = _nodeMetadata[i].ChildIndices;
                bool hasAnyChild = false;
                for (int o = 0; o < 8; o++)
                    if (childIndices[o] >= 0) { hasAnyChild = true; break; }

                if (hasAnyChild)
                {
                    _nodes[i].Children = new OctreeNode[8];
                    for (int o = 0; o < 8; o++)
                        _nodes[i].Children[o] = childIndices[o] >= 0 ? _nodes[childIndices[o]] : null;
                }
            }

            // Third pass: build merged GPU buffers.
            // Binary layout: for each node, for each octant 0-7: uint3 per point (12 bytes).
            //   word0 = (uint16_y << 16) | uint16_x  — XY quantized relative to node bounds
            //   word1 = RGB24 in bits 0-23            — octant (0-7) injected into bits 24-26 at load time
            //   word2 = uint16_z in bits 0-15         — Z quantized, upper 16 bits spare
            int fileOffset = 0;
            for (int i = 0; i < count; i++)
            {
                var meta = _nodeMetadata[i];
                var node = _nodes[i];

                node.OctantPointCounts    = new int[8];
                node.OctantOriginalCounts = new int[8];

                int total = 0;
                for (int o = 0; o < 8; o++)
                {
                    node.OctantPointCounts[o]    = meta.OctantPointCounts[o];
                    node.OctantOriginalCounts[o] = meta.OctantOriginalCounts != null
                        ? meta.OctantOriginalCounts[o]
                        : meta.OctantPointCounts[o];
                    total += meta.OctantPointCounts[o];
                }
                node.TotalPointCount = total;

                if (total == 0)
                {
                    for (int o = 0; o < 8; o++)
                        fileOffset += meta.OctantPosLengths[o] + meta.OctantColLengths[o];
                    continue;
                }

                var octantFileOffsets = new int[8];
                octantFileOffsets[0] = fileOffset;
                for (int o = 1; o < 8; o++)
                    octantFileOffsets[o] = octantFileOffsets[o - 1]
                        + meta.OctantPosLengths[o - 1] + meta.OctantColLengths[o - 1];

                var packed   = new uint[total * 3];
                int writeIdx = 0;
                for (int o = 0; o < 8; o++)
                {
                    int pts    = meta.OctantPointCounts[o];
                    int posLen = meta.OctantPosLengths[o];

                    if (pts > 0 && posLen > 0)
                    {
                        int  base_      = octantFileOffsets[o];
                        uint octantBits = (uint)o << 24;
                        for (int p = 0; p < pts; p++)
                        {
                            int b = base_ + p * 12;
                            packed[writeIdx * 3 + 0] = BitConverter.ToUInt32(fileBytes, b + 0);
                            packed[writeIdx * 3 + 1] = BitConverter.ToUInt32(fileBytes, b + 4) | octantBits;
                            packed[writeIdx * 3 + 2] = BitConverter.ToUInt32(fileBytes, b + 8);
                            writeIdx++;
                        }
                    }

                    fileOffset += meta.OctantPosLengths[o] + meta.OctantColLengths[o];
                }

                node.PointBuffer = new ComputeBuffer(total, 12);
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

        [Serializable]
        public struct PublicNodeMetadata
        {
            public Bounds Bounds;
            public int Depth;
            public int[] OctantPointCounts;
            public int[] OctantOriginalCounts;
            public int[] OctantPosLengths;
            public int[] OctantColLengths;
            public int[] ChildIndices;
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
                    Bounds               = metadata[i].Bounds,
                    Depth                = metadata[i].Depth,
                    OctantPointCounts    = metadata[i].OctantPointCounts,
                    OctantOriginalCounts = metadata[i].OctantOriginalCounts,
                    OctantPosLengths     = metadata[i].OctantPosLengths,
                    OctantColLengths     = metadata[i].OctantColLengths,
                    ChildIndices         = metadata[i].ChildIndices,
                };
            }

            _loaded = false;
            _nodes  = null;
        }

        private void OnDisable()
        {
            Unload();
        }
    }
}

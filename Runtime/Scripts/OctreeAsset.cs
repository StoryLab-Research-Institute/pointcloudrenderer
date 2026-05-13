using System;
using System.IO;
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
        [SerializeField, HideInInspector] private string _dataPath;

        private OctreeNode[] _nodes;
        private bool _loaded;

        public OctreeNode Root => (_nodes != null && _nodes.Length > 0) ? _nodes[0] : null;
        public int MaxNodeDepth { get; private set; }

        public void Load()
        {
            if (_loaded) return;
            if (_nodeMetadata == null || _nodeMetadata.Length == 0) return;

            var fullPath = Path.GetFullPath(Path.Combine(Application.dataPath, "..", _dataPath));
            if (!File.Exists(fullPath))
            {
                Debug.LogError($"[OctreeAsset] Point data file not found: {fullPath}");
                return;
            }

            byte[] fileBytes;
            try { fileBytes = File.ReadAllBytes(fullPath); }
            catch (Exception e)
            {
                Debug.LogError($"[OctreeAsset] Failed to read point data: {e.Message}");
                return;
            }

            int count = _nodeMetadata.Length;
            _nodes = new OctreeNode[count];

            // First pass: create node objects.
            for (int i = 0; i < count; i++)
            {
                var meta = _nodeMetadata[i];
                _nodes[i] = new OctreeNode
                {
                    Bounds = meta.Bounds,
                    Depth = meta.Depth,
                };
            }

            // Second pass: wire up children. ChildIndices[o] = node index for octant o, -1 = absent.
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
                // Children stays null for leaves.
            }

            // Third pass: build merged GPU buffers.
            // Binary layout: for each node, for each octant 0-7: posBytes then colBytes.
            // All octants are merged into one buffer per node; a per-point octant index buffer
            // lets the shader mask out octants covered by selected children (bitmask).
            int fileOffset = 0;
            for (int i = 0; i < count; i++)
            {
                var meta = _nodeMetadata[i];
                var node = _nodes[i];

                node.OctantPointCounts = new int[8];
                node.OctantOriginalCounts = new int[8];
                node.OctantStarts = new int[8];

                int total = 0;
                for (int o = 0; o < 8; o++)
                {
                    node.OctantStarts[o] = total;
                    node.OctantPointCounts[o] = meta.OctantPointCounts[o];
                    node.OctantOriginalCounts[o] = meta.OctantOriginalCounts != null
                        ? meta.OctantOriginalCounts[o]
                        : meta.OctantPointCounts[o]; // fallback for assets built before this change
                    total += meta.OctantPointCounts[o];
                }
                node.TotalPointCount = total;

                if (total == 0)
                {
                    for (int o = 0; o < 8; o++)
                        fileOffset += meta.OctantPosLengths[o] + meta.OctantColLengths[o];
                    continue;
                }

                // Compute per-octant file offsets, then merge into flat CPU arrays.
                var octantFileOffsets = new int[8];
                octantFileOffsets[0] = fileOffset;
                for (int o = 1; o < 8; o++)
                    octantFileOffsets[o] = octantFileOffsets[o - 1]
                        + meta.OctantPosLengths[o - 1] + meta.OctantColLengths[o - 1];

                var mergedPos = new byte[total * 12];
                var mergedCol = new byte[total * 4];
                var octantIndices = new uint[total];

                int writeIdx = 0;
                for (int o = 0; o < 8; o++)
                {
                    int pts = meta.OctantPointCounts[o];
                    int posLen = meta.OctantPosLengths[o];
                    int colLen = meta.OctantColLengths[o];

                    if (pts > 0 && posLen > 0)
                    {
                        Buffer.BlockCopy(fileBytes, octantFileOffsets[o],
                            mergedPos, writeIdx * 12, posLen);
                        Buffer.BlockCopy(fileBytes, octantFileOffsets[o] + posLen,
                            mergedCol, writeIdx * 4, colLen);
                        for (int p = 0; p < pts; p++)
                            octantIndices[writeIdx + p] = (uint)o;
                        writeIdx += pts;
                    }

                    fileOffset += posLen + colLen;
                }

                node.MergedPositionBuffer = new ComputeBuffer(total, 12);
                node.MergedPositionBuffer.SetData(mergedPos);

                node.MergedColorBuffer = new ComputeBuffer(total, 4);
                node.MergedColorBuffer.SetData(mergedCol);

                node.OctantIndexBuffer = new ComputeBuffer(total, 4);
                node.OctantIndexBuffer.SetData(octantIndices);

                node.PropertyBlock = new MaterialPropertyBlock();
                node.PropertyBlock.SetBuffer("_Positions", node.MergedPositionBuffer);
                node.PropertyBlock.SetBuffer("_ColorsPacked", node.MergedColorBuffer);
                node.PropertyBlock.SetBuffer("_OctantIndices", node.OctantIndexBuffer);
            }

            MaxNodeDepth = 0;
            foreach (var node in _nodes)
                if (node.Depth > MaxNodeDepth) MaxNodeDepth = node.Depth;

            _loaded = true;
        }

        public void Unload()
        {
            if (!_loaded || _nodes == null) return;
            foreach (var node in _nodes)
                node.ReleaseBuffers();
            _nodes = null;
            _loaded = false;
        }

        public void PrepareView(Vector3 position, Quaternion orientation, float fovDegrees)
        {
            Load();
        }

        [Serializable]
        public struct PublicNodeMetadata
        {
            public Bounds Bounds;
            public int Depth;
            public int[] OctantPointCounts;    // [8] — kept after subsampling
            public int[] OctantOriginalCounts; // [8] — before subsampling
            public int[] OctantPosLengths;     // [8]
            public int[] OctantColLengths;     // [8]
            public int[] ChildIndices;         // [8], -1 = absent
        }

        public void SetDataFromPublic(PublicNodeMetadata[] metadata, string dataPath)
        {
            _dataPath = dataPath;
            int count = metadata.Length;
            _nodeMetadata = new NodeMetadata[count];

            for (int i = 0; i < count; i++)
            {
                _nodeMetadata[i] = new NodeMetadata
                {
                    Bounds = metadata[i].Bounds,
                    Depth = metadata[i].Depth,
                    OctantPointCounts = metadata[i].OctantPointCounts,
                    OctantOriginalCounts = metadata[i].OctantOriginalCounts,
                    OctantPosLengths = metadata[i].OctantPosLengths,
                    OctantColLengths = metadata[i].OctantColLengths,
                    ChildIndices = metadata[i].ChildIndices,
                };
            }

            _loaded = false;
            _nodes = null;
        }

        private void OnDisable()
        {
            Unload();
        }
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace StoryLabResearch.PointCloud
{
    public static class OctreeBuilder
    {
        // Points kept per octant at each internal node level.
        // Total internal node points = up to 8 * TargetPointsPerOctant.
        private const int TargetPointsPerOctant = 2048;
        private const int MaxDepth = 12;

        [MenuItem("Assets/StoryLab PointCloud/Build Octree from Mesh")]
        private static void BuildOctreeFromSelection()
        {
            var mesh = Selection.activeObject as Mesh;
            if (mesh == null)
            {
                EditorUtility.DisplayDialog("Build Octree", "Select a Mesh asset first.", "OK");
                return;
            }
            BuildFromMesh(mesh, Matrix4x4.identity);
        }

        [MenuItem("Assets/StoryLab PointCloud/Build Octree from Mesh", true)]
        private static bool BuildOctreeFromSelectionValidate() => Selection.activeObject is Mesh;

        public static OctreeAsset BuildFromMesh(Mesh mesh, Matrix4x4 localToWorld)
        {
            var savePath = EditorUtility.SaveFilePanelInProject(
                "Save Octree Asset", mesh.name + "_Octree", "asset", "Save octree asset");
            if (string.IsNullOrEmpty(savePath)) return null;

            try
            {
                EditorUtility.DisplayProgressBar("Building Octree", "Reading mesh data...", 0f);

                var localVerts = mesh.vertices;
                var colors32 = mesh.colors32;
                int totalPoints = localVerts.Length;

                var positions = new Vector3[totalPoints];
                var colors = new uint[totalPoints];

                for (int i = 0; i < totalPoints; i++)
                {
                    positions[i] = localToWorld.MultiplyPoint3x4(localVerts[i]);
                    if (colors32 != null && colors32.Length == totalPoints)
                    {
                        var c = colors32[i];
                        colors[i] = (uint)c.r | ((uint)c.g << 8) | ((uint)c.b << 16) | ((uint)c.a << 24);
                    }
                    else
                    {
                        colors[i] = 0xFFFFFFFF;
                    }
                }

                var rootBounds = ComputeBounds(positions, 0, totalPoints);
                EditorUtility.DisplayProgressBar("Building Octree", "Building tree...", 0.05f);

                var indices = new int[totalPoints];
                for (int i = 0; i < totalPoints; i++) indices[i] = i;

                var nodeMetaList = new List<OctreeAsset.PublicNodeMetadata>();
                var binChunks = new List<byte[]>(); // flat list of per-octant byte chunks in node order

                BuildNode(positions, colors, indices, 0, totalPoints,
                    rootBounds, 0, nodeMetaList, binChunks, totalPoints);

                EditorUtility.DisplayProgressBar("Building Octree", "Writing point data...", 0.92f);

                var binPath = Path.ChangeExtension(savePath, null) + ".asset.bin";
                var binFullPath = Path.GetFullPath(Path.Combine(Application.dataPath, "..", binPath));

                using (var fs = new FileStream(binFullPath, FileMode.Create, FileAccess.Write))
                    foreach (var chunk in binChunks)
                        if (chunk != null) fs.Write(chunk, 0, chunk.Length);

                EditorUtility.DisplayProgressBar("Building Octree", "Creating asset...", 0.97f);

                var asset = ScriptableObject.CreateInstance<OctreeAsset>();
                asset.SetDataFromPublic(nodeMetaList.ToArray(), binPath);

                AssetDatabase.CreateAsset(asset, savePath);
                AssetDatabase.SaveAssets();

                Debug.Log($"[OctreeBuilder] Built {nodeMetaList.Count} nodes from {totalPoints} points.");
                return asset;
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }
        }

        private static void BuildNode(
            Vector3[] positions, uint[] colors,
            int[] indices, int start, int count,
            Bounds bounds, int depth,
            List<OctreeAsset.PublicNodeMetadata> metaList,
            List<byte[]> binChunks,
            int totalPoints)
        {
            EditorUtility.DisplayProgressBar("Building Octree",
                $"Depth {depth}: {count} points", 0.05f + 0.87f * (1f - (float)count / totalPoints));

            int nodeIndex = metaList.Count;
            metaList.Add(default);
            // Reserve 16 chunk slots (2 per octant: pointBytes + null colour placeholder).
            // Colour is fused into word1 of the point data, so the second slot is always null.
            int chunkBase = binChunks.Count;
            for (int o = 0; o < 8; o++) { binChunks.Add(null); binChunks.Add(null); }

            var octantPtCounts = new int[8];
            var octantOrigCounts = new int[8];
            var octantPosLengths = new int[8];
            var octantColLengths = new int[8];
            var childIndices = new int[8];
            for (int o = 0; o < 8; o++) childIndices[o] = -1;

            var center = bounds.center;

            // Leaf: all points go into octant buckets with no subsampling.
            // We still split by octant so the renderer can selectively draw octants.
            if (count <= TargetPointsPerOctant * 8 || depth >= MaxDepth)
            {
                // Partition all points into octants.
                var octantLists = new List<int>[8];
                for (int o = 0; o < 8; o++) octantLists[o] = new List<int>();
                for (int i = start; i < start + count; i++)
                    octantLists[GetOctant(positions[indices[i]], center)].Add(indices[i]);

                for (int o = 0; o < 8; o++)
                {
                    if (octantLists[o].Count == 0) continue;
                    WritePointData(positions, colors, octantLists[o].ToArray(), 0, octantLists[o].Count,
                        bounds, out var pointBytes);
                    octantPtCounts[o] = octantLists[o].Count;
                    octantOrigCounts[o] = octantLists[o].Count;
                    octantPosLengths[o] = pointBytes.Length;
                    octantColLengths[o] = 0;
                    binChunks[chunkBase + o * 2]     = pointBytes;
                    binChunks[chunkBase + o * 2 + 1] = null;
                }

                metaList[nodeIndex] = new OctreeAsset.PublicNodeMetadata
                {
                    Bounds = bounds,
                    Depth = depth,
                    OctantPointCounts = octantPtCounts,
                    OctantOriginalCounts = octantOrigCounts,
                    OctantPosLengths = octantPosLengths,
                    OctantColLengths = octantColLengths,
                    ChildIndices = childIndices,
                };
                return;
            }

            // Internal node: for each octant, grid-subsample up to TargetPointsPerOctant
            // points to keep at this level. The remaining points recurse into a child node.
            var allOctantLists = new List<int>[8];
            for (int o = 0; o < 8; o++) allOctantLists[o] = new List<int>();
            for (int i = start; i < start + count; i++)
                allOctantLists[GetOctant(positions[indices[i]], center)].Add(indices[i]);

            int gridRes = Math.Max(1, (int)Math.Cbrt(TargetPointsPerOctant));

            for (int o = 0; o < 8; o++)
            {
                var octList = allOctantLists[o];
                if (octList.Count == 0) continue;

                var octBounds = GetOctantBounds(bounds, o);
                var octArr = octList.ToArray();

                // Grid-subsample this octant to get the representative points for this level.
                var kept = GridSubsample(positions, octArr, 0, octArr.Length, octBounds, gridRes, TargetPointsPerOctant);

                WritePointData(positions, colors, kept, 0, kept.Length,
                    bounds, out var pointBytes);
                octantPtCounts[o] = kept.Length;
                octantOrigCounts[o] = octArr.Length;
                octantPosLengths[o] = pointBytes.Length;
                octantColLengths[o] = 0;
                binChunks[chunkBase + o * 2]     = pointBytes;
                binChunks[chunkBase + o * 2 + 1] = null;

                // Remaining points recurse into a child node.
                var keptSet = new HashSet<int>(kept);
                var remaining = new List<int>(octArr.Length - kept.Length);
                foreach (var idx in octArr)
                    if (!keptSet.Contains(idx)) remaining.Add(idx);

                if (remaining.Count > 0)
                {
                    childIndices[o] = metaList.Count;
                    var remainArr = remaining.ToArray();
                    BuildNode(positions, colors, remainArr, 0, remainArr.Length,
                        octBounds, depth + 1, metaList, binChunks, totalPoints);
                }
            }

            metaList[nodeIndex] = new OctreeAsset.PublicNodeMetadata
            {
                Bounds = bounds,
                Depth = depth,
                OctantPointCounts = octantPtCounts,
                OctantOriginalCounts = octantOrigCounts,
                OctantPosLengths = octantPosLengths,
                OctantColLengths = octantColLengths,
                ChildIndices = childIndices,
            };
        }

        private static int[] GridSubsample(Vector3[] positions, int[] indices, int start, int count,
            Bounds bounds, int gridRes, int targetCount)
        {
            var cellOccupied = new HashSet<long>();
            var result = new List<int>(targetCount);
            var boundsMin = bounds.min;
            var boundsSize = bounds.size;
            float invX = gridRes / (boundsSize.x + 1e-6f);
            float invY = gridRes / (boundsSize.y + 1e-6f);
            float invZ = gridRes / (boundsSize.z + 1e-6f);

            for (int i = start; i < start + count && result.Count < targetCount; i++)
            {
                int idx = indices[i];
                var p = positions[idx] - boundsMin;
                int cx = Mathf.Clamp((int)(p.x * invX), 0, gridRes - 1);
                int cy = Mathf.Clamp((int)(p.y * invY), 0, gridRes - 1);
                int cz = Mathf.Clamp((int)(p.z * invZ), 0, gridRes - 1);
                long key = cx + gridRes * (cy + (long)gridRes * cz);
                if (cellOccupied.Add(key))
                    result.Add(idx);
            }
            return result.ToArray();
        }

        private static int GetOctant(Vector3 pos, Vector3 center)
            => ((pos.x >= center.x) ? 1 : 0)
             | ((pos.y >= center.y) ? 2 : 0)
             | ((pos.z >= center.z) ? 4 : 0);

        private static Bounds GetOctantBounds(Bounds parent, int octant)
        {
            var half = parent.extents;
            var min = parent.min;
            var childMin = new Vector3(
                min.x + ((octant & 1) != 0 ? half.x : 0f),
                min.y + ((octant & 2) != 0 ? half.y : 0f),
                min.z + ((octant & 4) != 0 ? half.z : 0f));
            return new Bounds(childMin + half * 0.5f, half);
        }

        private static Bounds ComputeBounds(Vector3[] positions, int start, int count)
        {
            if (count == 0) return new Bounds();
            var min = positions[start];
            var max = positions[start];
            for (int i = start + 1; i < start + count; i++)
            {
                min = Vector3.Min(min, positions[i]);
                max = Vector3.Max(max, positions[i]);
            }
            var b = new Bounds();
            b.SetMinMax(min, max);
            return b;
        }

        private static void WritePointData(Vector3[] positions, uint[] colors, int[] indices,
            int start, int count, Bounds nodeBounds, out byte[] pointBytes)
        {
            // Pack each point as uint3 (12 bytes):
            //   .x = (uint16_y << 16) | uint16_x  — X and Y quantized to node bounds
            //   .y = (octant << 24) | RGB24        — octant in top byte (derived at load), RGB in low 3
            //   .z = uint16_z in low 16 bits       — Z quantized to node bounds, upper 16 spare
            // Positions quantized as uint16 unorm relative to node bounds (0=min, 65535=max).
            // Octant is not stored here — it's derived at load time from the octant metadata structure.
            // Color alpha is dropped: point size is driven by _LodSizeScale from the subsampling ratio.
            var bMin = nodeBounds.min;
            var bSize = nodeBounds.size;
            float invX = bSize.x > 0 ? 65535f / bSize.x : 0f;
            float invY = bSize.y > 0 ? 65535f / bSize.y : 0f;
            float invZ = bSize.z > 0 ? 65535f / bSize.z : 0f;

            pointBytes = new byte[count * 12];
            for (int i = 0; i < count; i++)
            {
                int idx = indices[start + i];
                var p = positions[idx] - bMin;
                uint qx = (uint)Mathf.Clamp(Mathf.RoundToInt(p.x * invX), 0, 65535);
                uint qy = (uint)Mathf.Clamp(Mathf.RoundToInt(p.y * invY), 0, 65535);
                uint qz = (uint)Mathf.Clamp(Mathf.RoundToInt(p.z * invZ), 0, 65535);

                uint col = colors[idx];
                uint rgb = col & 0x00FFFFFFu; // strip alpha entirely

                uint word0 = (qy << 16) | qx;
                uint word1 = rgb;             // octant written at load time into top byte
                uint word2 = qz;              // upper 16 bits spare

                int b = i * 12;
                pointBytes[b + 0]  = (byte)(word0);
                pointBytes[b + 1]  = (byte)(word0 >> 8);
                pointBytes[b + 2]  = (byte)(word0 >> 16);
                pointBytes[b + 3]  = (byte)(word0 >> 24);
                pointBytes[b + 4]  = (byte)(word1);
                pointBytes[b + 5]  = (byte)(word1 >> 8);
                pointBytes[b + 6]  = (byte)(word1 >> 16);
                pointBytes[b + 7]  = (byte)(word1 >> 24);
                pointBytes[b + 8]  = (byte)(word2);
                pointBytes[b + 9]  = (byte)(word2 >> 8);
                pointBytes[b + 10] = (byte)(word2 >> 16);
                pointBytes[b + 11] = (byte)(word2 >> 24);
            }
        }
    }
}

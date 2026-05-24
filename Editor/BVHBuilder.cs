using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace StoryLabResearch.PointCloud
{
    public static class BVHBuilder
    {
        // Points kept at each internal node level after grid-subsampling.
        private const int TargetPointsPerNode = 16384;
        private const int MaxDepth            = 24; // binary tree goes deeper than octree for same data

        public static BVHAsset BuildFromPointsEmbedded(
            Vector3[] positions, uint[] colors,
            float minPointSpacing = 0f, float maxNodeSideLength = 0f)
        {
            try
            {
                return BuildInternal(positions, colors, minPointSpacing, maxNodeSideLength);
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }
        }

        private static BVHAsset BuildInternal(
            Vector3[] positions, uint[] colors,
            float minPointSpacing, float maxNodeSideLength)
        {
            int totalPoints = positions.Length;

            int[] indices;
            if (minPointSpacing > 0f)
            {
                EditorUtility.DisplayProgressBar("Building BVH", "Pre-thinning points...", 0.02f);
                var allIndices = new int[totalPoints];
                for (int i = 0; i < totalPoints; i++) allIndices[i] = i;
                indices = SphereExclusionSubsample(positions, allIndices, minPointSpacing);
                Debug.Log($"[BVHBuilder] Pre-thin: {totalPoints} → {indices.Length} points " +
                          $"(spacing {minPointSpacing:F4}m).");
                totalPoints = indices.Length;
            }
            else
            {
                indices = new int[totalPoints];
                for (int i = 0; i < totalPoints; i++) indices[i] = i;
            }

            EditorUtility.DisplayProgressBar("Building BVH", "Building tree...", 0.05f);

            var metaList  = new List<BVHAsset.PublicNodeMetadata>();
            var binChunks = new List<byte[]>();

            BuildNode(positions, colors, indices, 0, totalPoints,
                0, metaList, binChunks, totalPoints, maxNodeSideLength);

            EditorUtility.DisplayProgressBar("Building BVH", "Assembling point data...", 0.92f);

            int totalBytes = 0;
            foreach (var chunk in binChunks)
                if (chunk != null) totalBytes += chunk.Length;
            var pointData = new byte[totalBytes];
            int writePos  = 0;
            foreach (var chunk in binChunks)
            {
                if (chunk == null) continue;
                Buffer.BlockCopy(chunk, 0, pointData, writePos, chunk.Length);
                writePos += chunk.Length;
            }

            EditorUtility.DisplayProgressBar("Building BVH", "Creating asset...", 0.97f);

            var asset = ScriptableObject.CreateInstance<BVHAsset>();
            asset.name = "BVHAsset";
            asset.SetData(metaList.ToArray(), pointData);

            Debug.Log($"[BVHBuilder] Built {metaList.Count} nodes, {totalBytes / 1024 / 1024} MB embedded.");
            return asset;
        }

        // Recursive BVH node builder using longest-axis median split.
        // indices[start .. start+count) are the points for this node.
        private static void BuildNode(
            Vector3[] positions, uint[] colors,
            int[] indices, int start, int count,
            int depth,
            List<BVHAsset.PublicNodeMetadata> metaList,
            List<byte[]> binChunks,
            int totalPoints, float maxNodeSideLength)
        {
            EditorUtility.DisplayProgressBar("Building BVH",
                $"Depth {depth}: {count} points", 0.05f + 0.87f * (1f - (float)count / totalPoints));

            int nodeIndex = metaList.Count;
            metaList.Add(default);
            int chunkIndex = binChunks.Count;
            binChunks.Add(null);

            var bounds = ComputeBoundsFromIndices(positions, indices, start, count);

            // Leaf condition: below point threshold, at max depth, and not oversized.
            // maxNodeSideLength forces a spatial split even when point count is low,
            // so that sparse large-area nodes get tighter bounds for culling and LOD accuracy.
            bool tooDeep    = depth >= MaxDepth;
            bool smallEnough = count <= TargetPointsPerNode;
            bool spatiallySmall = maxNodeSideLength <= 0f ||
                (bounds.size.x <= maxNodeSideLength &&
                 bounds.size.y <= maxNodeSideLength &&
                 bounds.size.z <= maxNodeSideLength);

            if ((smallEnough && spatiallySmall) || tooDeep)
            {
                // True leaf: store all points with no subsampling.
                WritePointData(positions, colors, indices, start, count, bounds, out var leafBytes);
                binChunks[chunkIndex] = leafBytes;

                metaList[nodeIndex] = new BVHAsset.PublicNodeMetadata
                {
                    Bounds        = bounds,
                    Depth         = depth,
                    PointCount    = count,
                    OriginalCount = count,
                    ByteLength    = leafBytes.Length,
                    LeftIndex     = -1,
                    RightIndex    = -1,
                };
                return;
            }

            // Internal node: grid-subsample to get representatives for this level,
            // then split the full point set left/right on the longest axis median.
            int gridRes = Math.Max(1, (int)Math.Cbrt(TargetPointsPerNode));
            var kept    = GridSubsample(positions, indices, start, count, bounds, gridRes, TargetPointsPerNode);

            WritePointData(positions, colors, kept, 0, kept.Length, bounds, out var nodeBytes);
            binChunks[chunkIndex] = nodeBytes;

            // Partition on longest axis at spatial median.
            var extents   = bounds.extents;
            int splitAxis = extents.x >= extents.y && extents.x >= extents.z ? 0
                          : extents.y >= extents.z                           ? 1
                          :                                                    2;
            float splitVal = bounds.center[splitAxis];

            // Partition indices[start..start+count) in-place: left side < splitVal, right >= splitVal.
            int lo = start, hi = start + count - 1;
            while (lo <= hi)
            {
                if (positions[indices[lo]][splitAxis] < splitVal) { lo++; continue; }
                (indices[lo], indices[hi]) = (indices[hi], indices[lo]);
                hi--;
            }
            int leftCount  = lo - start;
            int rightCount = count - leftCount;

            // Degenerate split: all points ended up on one side. Avoid infinite recursion by
            // forcing a 50/50 split at the midpoint index instead.
            if (leftCount == 0 || rightCount == 0)
            {
                leftCount  = count / 2;
                rightCount = count - leftCount;
            }

            int leftIndex  = -1;
            int rightIndex = -1;

            if (leftCount > 0)
            {
                leftIndex = metaList.Count;
                BuildNode(positions, colors, indices, start, leftCount,
                    depth + 1, metaList, binChunks, totalPoints, maxNodeSideLength);
            }

            if (rightCount > 0)
            {
                rightIndex = metaList.Count;
                BuildNode(positions, colors, indices, start + leftCount, rightCount,
                    depth + 1, metaList, binChunks, totalPoints, maxNodeSideLength);
            }

            metaList[nodeIndex] = new BVHAsset.PublicNodeMetadata
            {
                Bounds        = bounds,
                Depth         = depth,
                PointCount    = kept.Length,
                OriginalCount = count,
                ByteLength    = nodeBytes.Length,
                LeftIndex     = leftIndex,
                RightIndex    = rightIndex,
            };
        }

        // Grid subsample: keeps at most one point per cell of a uniform grid over bounds.
        // Used for LOD representative selection at internal BVH nodes.
        private static int[] GridSubsample(Vector3[] positions, int[] indices, int start, int count,
            Bounds bounds, int gridRes, int targetCount)
        {
            var cellOccupied = new HashSet<long>();
            var result       = new List<int>(targetCount);
            var bMin         = bounds.min;
            var bSize        = bounds.size;
            float invX = gridRes / (bSize.x + 1e-6f);
            float invY = gridRes / (bSize.y + 1e-6f);
            float invZ = gridRes / (bSize.z + 1e-6f);

            for (int i = start; i < start + count && result.Count < targetCount; i++)
            {
                int idx = indices[i];
                var p   = positions[idx] - bMin;
                int cx  = Mathf.Clamp((int)(p.x * invX), 0, gridRes - 1);
                int cy  = Mathf.Clamp((int)(p.y * invY), 0, gridRes - 1);
                int cz  = Mathf.Clamp((int)(p.z * invZ), 0, gridRes - 1);
                long key = cx + gridRes * (cy + (long)gridRes * cz);
                if (cellOccupied.Add(key))
                    result.Add(idx);
            }
            return result.ToArray();
        }

        // Sphere-exclusion spatial resampling (equivalent to CloudCompare resampleCloudSpatially).
        //
        // For each point in input order, keep it if no already-kept point lies within minSpacing.
        // Uses a voxel hash (cell side = minSpacing) as an acceleration structure: only the 27
        // cells surrounding a candidate need to be checked. This eliminates the periodic grid
        // artefacts produced by the previous GlobalGridSubsample approach, because kept points
        // are irregular — there is no axis-aligned cell boundary that can produce coherent stripes.
        private static int[] SphereExclusionSubsample(Vector3[] positions, int[] indices, float minSpacing)
        {
            float minSpacingSq = minSpacing * minSpacing;
            float inv          = 1f / minSpacing;

            // Maps voxel key → list of already-kept point positions in that voxel.
            var voxelMap = new Dictionary<long, List<Vector3>>(indices.Length / 8);
            var result   = new List<int>(indices.Length / 4);

            for (int i = 0; i < indices.Length; i++)
            {
                Vector3 p  = positions[indices[i]];
                long    vx = (long)Math.Floor(p.x * inv);
                long    vy = (long)Math.Floor(p.y * inv);
                long    vz = (long)Math.Floor(p.z * inv);

                // Check all 27 neighbouring voxels for a point within minSpacing.
                bool tooClose = false;
                for (int dx = -1; dx <= 1 && !tooClose; dx++)
                for (int dy = -1; dy <= 1 && !tooClose; dy++)
                for (int dz = -1; dz <= 1 && !tooClose; dz++)
                {
                    long nkey = (vx + dx) * 2_000_003L + (vy + dy) * 1_999_979L + (vz + dz);
                    if (!voxelMap.TryGetValue(nkey, out var bucket)) continue;
                    foreach (var kept in bucket)
                    {
                        float sqDist = (p.x - kept.x) * (p.x - kept.x)
                                     + (p.y - kept.y) * (p.y - kept.y)
                                     + (p.z - kept.z) * (p.z - kept.z);
                        if (sqDist < minSpacingSq) { tooClose = true; break; }
                    }
                }

                if (tooClose) continue;

                // Keep this point and register it in the voxel map.
                long key = vx * 2_000_003L + vy * 1_999_979L + vz;
                if (!voxelMap.TryGetValue(key, out var slot))
                {
                    slot = new List<Vector3>(2);
                    voxelMap[key] = slot;
                }
                slot.Add(p);
                result.Add(indices[i]);
            }

            return result.ToArray();
        }

        private static Bounds ComputeBoundsFromRange(Vector3[] positions, int start, int count)
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

        private static Bounds ComputeBoundsFromIndices(Vector3[] positions, int[] indices, int start, int count)
        {
            if (count == 0) return new Bounds();
            var min = positions[indices[start]];
            var max = positions[indices[start]];
            for (int i = start + 1; i < start + count; i++)
            {
                min = Vector3.Min(min, positions[indices[i]]);
                max = Vector3.Max(max, positions[indices[i]]);
            }
            var b = new Bounds();
            b.SetMinMax(min, max);
            return b;
        }

        // Pack each point as uint3 (12 bytes):
        //   word0 = (uint16_y << 16) | uint16_x  — XY quantized [0,65535] to node bounds
        //   word1 = RGB24 in bits 0-23            — bits 24-31 zero/spare
        //   word2 = uint16_z in bits 0-15         — Z quantized, upper 16 bits spare
        private static void WritePointData(Vector3[] positions, uint[] colors, int[] indices,
            int start, int count, Bounds nodeBounds, out byte[] pointBytes)
        {
            var   bMin = nodeBounds.min;
            var   bSize = nodeBounds.size;
            float invX = bSize.x > 0 ? 65535f / bSize.x : 0f;
            float invY = bSize.y > 0 ? 65535f / bSize.y : 0f;
            float invZ = bSize.z > 0 ? 65535f / bSize.z : 0f;

            pointBytes = new byte[count * 12];
            for (int i = 0; i < count; i++)
            {
                int idx = indices[start + i];
                var p   = positions[idx] - bMin;
                uint qx = (uint)Mathf.Clamp(Mathf.RoundToInt(p.x * invX), 0, 65535);
                uint qy = (uint)Mathf.Clamp(Mathf.RoundToInt(p.y * invY), 0, 65535);
                uint qz = (uint)Mathf.Clamp(Mathf.RoundToInt(p.z * invZ), 0, 65535);

                uint rgb   = colors[idx] & 0x00FFFFFFu; // strip alpha
                uint word0 = (qy << 16) | qx;
                uint word1 = rgb;
                uint word2 = qz;

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

using UnityEngine;
using UnityEditor;
using UnityEditor.AssetImporters;
using System;
using System.Collections.Generic;
using System.IO;

namespace StoryLabResearch.PointCloud
{
    [ScriptedImporter(7, "ply")]
    class PlyImporter : ScriptedImporter
    {
        public static readonly string SHADER_PATH = "Packages/com.storylabresearch.pointcloudrenderer.octree/runtime/shaders/";

        public enum EAxisPreset
        {
            // PLY has no axis convention — these presets cover the most common real-world cases.
            ZUpRightHanded,  // Photogrammetry / LiDAR standard (RealityCapture, Metashape, RiSCAN).
                             // PLY (x,y,z) → Unity (-x, z, y). Negates X to correct handedness.
            ZUpLeftHanded,   // CloudCompare default export and some other tools.
                             // PLY (x,y,z) → Unity (x, z, y). No handedness correction.
            YUpRightHanded,  // Blender / DCC right-handed Y-up.
                             // PLY (x,y,z) → Unity (x, y, -z).
            None,            // Pass-through — raw PLY values used as-is.
            Custom,          // Use the AxisX/AxisY/AxisZ fields below.
        }

        public enum EAxis { PosX, NegX, PosY, NegY, PosZ, NegZ }

        [Tooltip("Maps PLY axes to Unity world axes. " +
                 "Most photogrammetry and LiDAR tools export Z-up right-handed.")]
        public EAxisPreset AxisPreset = EAxisPreset.ZUpRightHanded;

        [Tooltip("Which PLY axis (±) maps to Unity +X. Only used when AxisPreset is Custom.")]
        public EAxis AxisX = EAxis.PosX;
        [Tooltip("Which PLY axis (±) maps to Unity +Y. Only used when AxisPreset is Custom.")]
        public EAxis AxisY = EAxis.PosZ;
        [Tooltip("Which PLY axis (±) maps to Unity +Z. Only used when AxisPreset is Custom.")]
        public EAxis AxisZ = EAxis.PosY;

        public float Rescale = 1.0f;
        public bool ApplySRGBCorrection;

        [Tooltip("Optional shared import properties asset. When set, overrides all settings below.")]
        public PointCloudImportProperties ImportProperties;

        // Inline settings — used when ImportProperties is null.
        [Tooltip("Quality tier (PC / Mac / Consoles).")]
        public PlatformImportTier Quality;

        [Tooltip("Performance tier (Android / Quest).")]
        public PlatformImportTier Performance;

        // Resolved accessors — read from ImportProperties if set, else inline fields.
        private PlatformImportTier R_Quality     => ImportProperties != null ? ImportProperties.Quality     : Quality;
        private PlatformImportTier R_Performance => ImportProperties != null ? ImportProperties.Performance : Performance;

        public override void OnImportAsset(AssetImportContext context)
        {
            // Clean up any old sidecar .bin files left in Assets/ by earlier import versions.
            DeleteOldSidecarBins(context.assetPath);

            ReadPointData(context.assetPath, out var positions, out var colors);
            if (positions == null) return;

            ApplyAxisSwizzle(positions);

            var name            = Path.GetFileNameWithoutExtension(context.assetPath);
            var qualityTier     = R_Quality;
            var performanceTier = R_Performance;

            var qualityAsset = BVHBuilder.BuildFromPointsEmbedded(
                positions, colors, qualityTier.MinPointSpacing, qualityTier.MaxNodeSideLength);
            if (qualityAsset == null) return;

            var performanceAsset = BVHBuilder.BuildFromPointsEmbedded(
                positions, colors, performanceTier.MinPointSpacing, performanceTier.MaxNodeSideLength);
            if (performanceAsset == null) return;

            qualityAsset.name     = name + "_Quality";
            performanceAsset.name = name + "_Performance";

            // Resolve materials for each tier.
            var qualityMat     = ResolveMaterial(context, qualityTier,     name + "_Quality",     "material_quality");
            var performanceMat = ResolveMaterial(context, performanceTier, name + "_Performance", "material_performance");

            // Build the prefab.
            var go = new GameObject(name);
            var renderer = go.AddComponent<PointCloudRenderer>();

            renderer.SetImportedAsset(
                new PerPlatformAssets    { Quality = qualityAsset,  Performance = performanceAsset },
                new PerPlatformMaterials { Quality = qualityMat,    Performance = performanceMat },
                new PerPlatformRenderProperties
                {
                    Quality     = qualityTier.RenderProperties,
                    Performance = performanceTier.RenderProperties,
                });

            context.AddObjectToAsset("bvh_quality",     qualityAsset);
            context.AddObjectToAsset("bvh_performance", performanceAsset);
            context.AddObjectToAsset("prefab", go);
            context.SetMainObject(go);
        }

        // Resolves the material for a tier:
        //   Shared      — returns the source material directly (no copy).
        //   Instantiated — embeds a copy as a sub-asset inside the .ply import.
        //   Extracted   — writes a standalone .mat next to the .ply and returns a reference to it.
        //                  If the .mat already exists it is reused without overwriting.
        private Material ResolveMaterial(AssetImportContext context,
            PlatformImportTier tier, string assetName, string subAssetKey)
        {
            var sourceMat = tier.Material != null ? tier.Material : GetDefaultOctreeMaterial();

            switch (tier.MaterialMode)
            {
                case PointCloudImportProperties.EMaterialMode.Instantiated:
                {
                    var copy = new Material(sourceMat) { name = assetName };
                    context.AddObjectToAsset(subAssetKey, copy);
                    return copy;
                }

                case PointCloudImportProperties.EMaterialMode.Extracted:
                {
                    // Sidecar path: MyCloud_Quality.mat / MyCloud_Performance.mat next to the .ply.
                    var matAssetPath = Path.ChangeExtension(context.assetPath, null) + "_" + assetName + ".mat";

                    var existing = AssetDatabase.LoadAssetAtPath<Material>(matAssetPath);
                    if (existing != null)
                    {
                        context.DependsOnArtifact(matAssetPath);
                        return existing;
                    }

                    // Sidecar does not yet exist. AssetDatabase.CreateAsset is forbidden inside a
                    // ScriptedImporter, so defer the write to after this import completes.
                    // This import run embeds the material; Unity will re-import automatically when
                    // the new .mat appears, at which point the extracted file will be used.
                    var copy = new Material(sourceMat) { name = assetName };
                    var capturedPath = matAssetPath;
                    var capturedCopy = copy;
                    EditorApplication.delayCall += () =>
                    {
                        if (AssetDatabase.LoadAssetAtPath<Material>(capturedPath) != null) return;
                        AssetDatabase.CreateAsset(capturedCopy, capturedPath);
                        AssetDatabase.ImportAsset(capturedPath, ImportAssetOptions.ForceSynchronousImport);
                    };

                    context.AddObjectToAsset(subAssetKey, copy);
                    return copy;
                }

                default: // Shared
                    return sourceMat;
            }
        }

        private void ApplyAxisSwizzle(Vector3[] positions)
        {
            // Resolve the three signed-axis selectors for the active preset.
            EAxis ax, ay, az;
            switch (AxisPreset)
            {
                case EAxisPreset.ZUpRightHanded: ax = EAxis.NegX; ay = EAxis.PosZ; az = EAxis.PosY; break;
                case EAxisPreset.ZUpLeftHanded:  ax = EAxis.PosX; ay = EAxis.PosZ; az = EAxis.PosY; break;
                case EAxisPreset.YUpRightHanded: ax = EAxis.PosX; ay = EAxis.PosY; az = EAxis.NegZ; break;
                case EAxisPreset.None:           return;
                default: /* Custom */            ax = AxisX; ay = AxisY; az = AxisZ; break;
            }

            for (int i = 0; i < positions.Length; i++)
            {
                var p = positions[i];
                positions[i] = new Vector3(
                    SampleAxis(p, ax),
                    SampleAxis(p, ay),
                    SampleAxis(p, az));
            }
        }

        private static float SampleAxis(Vector3 p, EAxis axis)
        {
            switch (axis)
            {
                case EAxis.PosX: return  p.x;
                case EAxis.NegX: return -p.x;
                case EAxis.PosY: return  p.y;
                case EAxis.NegY: return -p.y;
                case EAxis.PosZ: return  p.z;
                default:         return -p.z; // NegZ
            }
        }

        private void ReadPointData(string path, out Vector3[] positions, out uint[] colors)
        {
            try
            {
                var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                var header = ReadDataHeader(new StreamReader(stream));
                var body = ReadDataBody(header, new BinaryReader(stream));
                stream.Close();

                int count = body.vertices.Count;
                positions = new Vector3[count];
                colors = new uint[count];

                for (int i = 0; i < count; i++)
                {
                    positions[i] = body.vertices[i];
                    var c = body.colors[i];
                    colors[i] = (uint)c.r | ((uint)c.g << 8) | ((uint)c.b << 16);
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"[PlyImporter] Failed reading {path}: {e.Message}");
                positions = null;
                colors = null;
            }
        }

        static Material GetDefaultOctreeMaterial()
        {
            var path = SHADER_PATH + "URP/DefaultOctreePointCloud.mat";
            var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (mat == null)
                throw new Exception($"Could not find default octree material at '{path}'.");
            return mat;
        }

        // Remove legacy sidecar .bin files that used to live next to the .ply in Assets/.
        // These are no longer written; bins now live in Library/PointCloudBins/.
        static void DeleteOldSidecarBins(string plyAssetPath)
        {
            var projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            var baseName    = Path.ChangeExtension(plyAssetPath, null);

            string[] candidates = {
                Path.Combine(projectRoot, baseName + ".asset.bin"),
                Path.Combine(projectRoot, baseName + "quality.asset.bin"),
                Path.Combine(projectRoot, baseName + "performance.asset.bin"),
            };

            foreach (var full in candidates)
            {
                if (!File.Exists(full)) continue;
                File.Delete(full);
                var meta = full + ".meta";
                if (File.Exists(meta)) File.Delete(meta);
            }
        }

        #region Internal data structure
        // https://github.com/keijiro/Pcx
        // Code used under the UnLicense

        enum DataProperty
        {
            Invalid,
            R8, G8, B8, A8,
            R16, G16, B16, A16,
            SingleX, SingleY, SingleZ,
            DoubleX, DoubleY, DoubleZ,
            Data8, Data16, Data32, Data64
        }

        static int GetPropertySize(DataProperty p)
        {
            switch (p)
            {
                case DataProperty.R8: return 1;
                case DataProperty.G8: return 1;
                case DataProperty.B8: return 1;
                case DataProperty.A8: return 1;
                case DataProperty.R16: return 2;
                case DataProperty.G16: return 2;
                case DataProperty.B16: return 2;
                case DataProperty.A16: return 2;
                case DataProperty.SingleX: return 4;
                case DataProperty.SingleY: return 4;
                case DataProperty.SingleZ: return 4;
                case DataProperty.DoubleX: return 8;
                case DataProperty.DoubleY: return 8;
                case DataProperty.DoubleZ: return 8;
                case DataProperty.Data8: return 1;
                case DataProperty.Data16: return 2;
                case DataProperty.Data32: return 4;
                case DataProperty.Data64: return 8;
                case DataProperty.Invalid: break;
                default: break;
            }
            return 0;
        }

        class DataHeader
        {
            public List<DataProperty> properties = new();
            public int vertexCount = -1;
        }

        class DataBody
        {
            public List<Vector3> vertices;
            public List<Color32> colors;

            public DataBody(int vertexCount)
            {
                vertices = new List<Vector3>(vertexCount);
                colors = new List<Color32>(vertexCount);
            }

            public void AddPoint(
                float x, float y, float z,
                byte r, byte g, byte b, byte a,
                float rescale = 1.0f,
                bool applySRGBCorrection = false
            )
            {
                vertices.Add(new Vector3(x, y, z) * rescale);
                if (applySRGBCorrection) colors.Add(new Color32(CorrectSRGB(r), CorrectSRGB(g), CorrectSRGB(b), a));
                else colors.Add(new Color32(r, g, b, a));
            }

            private byte CorrectSRGB(byte val)
            {
                var floatVal = (float)val / 255;
                var correctedFloatVal = Mathf.Pow(floatVal, 1.0f / 2.2f);
                return (byte)Mathf.RoundToInt(correctedFloatVal * 255);
            }
        }

        #endregion

        #region Reader implementation

        DataHeader ReadDataHeader(StreamReader reader)
        {
            var data = new DataHeader();
            var readCount = 0;

            var line = reader.ReadLine();
            readCount += line.Length + 1;
            if (line != "ply")
                throw new ArgumentException("Magic number ('ply') mismatch.");

            line = reader.ReadLine();
            readCount += line.Length + 1;
            if (line != "format binary_little_endian 1.0")
                throw new ArgumentException(
                    "Invalid data format ('" + line + "'). " +
                    "Should be binary/little endian.");

            for (var skip = false; ;)
            {
                line = reader.ReadLine();
                readCount += line.Length + 1;
                if (line == "end_header") break;
                var col = line.Split();

                if (col[0] == "element")
                {
                    if (col[1] == "vertex")
                    {
                        data.vertexCount = Convert.ToInt32(col[2]);
                        skip = false;
                    }
                    else
                    {
                        skip = true;
                    }
                }

                if (skip) continue;

                if (col[0] == "property")
                {
                    var prop = DataProperty.Invalid;

                    switch (col[2])
                    {
                        case "red": prop = DataProperty.R8; break;
                        case "green": prop = DataProperty.G8; break;
                        case "blue": prop = DataProperty.B8; break;
                        case "alpha": prop = DataProperty.A8; break;
                        case "x": prop = DataProperty.SingleX; break;
                        case "y": prop = DataProperty.SingleY; break;
                        case "z": prop = DataProperty.SingleZ; break;
                    }

                    if (col[1] == "char" || col[1] == "uchar" ||
                        col[1] == "int8" || col[1] == "uint8")
                    {
                        if (prop == DataProperty.Invalid)
                            prop = DataProperty.Data8;
                        else if (GetPropertySize(prop) != 1)
                            throw new ArgumentException("Invalid property type ('" + line + "').");
                    }
                    else if (col[1] == "short" || col[1] == "ushort" ||
                                col[1] == "int16" || col[1] == "uint16")
                    {
                        switch (prop)
                        {
                            case DataProperty.Invalid: prop = DataProperty.Data16; break;
                            case DataProperty.R8: prop = DataProperty.R16; break;
                            case DataProperty.G8: prop = DataProperty.G16; break;
                            case DataProperty.B8: prop = DataProperty.B16; break;
                            case DataProperty.A8: prop = DataProperty.A16; break;
                        }
                        if (GetPropertySize(prop) != 2)
                            throw new ArgumentException("Invalid property type ('" + line + "').");
                    }
                    else if (col[1] == "int" || col[1] == "uint" || col[1] == "float" ||
                                col[1] == "int32" || col[1] == "uint32" || col[1] == "float32")
                    {
                        if (prop == DataProperty.Invalid)
                            prop = DataProperty.Data32;
                        else if (GetPropertySize(prop) != 4)
                            throw new ArgumentException("Invalid property type ('" + line + "').");
                    }
                    else if (col[1] == "int64" || col[1] == "uint64" ||
                                col[1] == "double" || col[1] == "float64")
                    {
                        switch (prop)
                        {
                            case DataProperty.Invalid: prop = DataProperty.Data64; break;
                            case DataProperty.SingleX: prop = DataProperty.DoubleX; break;
                            case DataProperty.SingleY: prop = DataProperty.DoubleY; break;
                            case DataProperty.SingleZ: prop = DataProperty.DoubleZ; break;
                        }
                        if (GetPropertySize(prop) != 8)
                            throw new ArgumentException("Invalid property type ('" + line + "').");
                    }
                    else
                    {
                        throw new ArgumentException("Unsupported property type ('" + line + "').");
                    }

                    data.properties.Add(prop);
                }
            }

            reader.BaseStream.Position = readCount;
            return data;
        }

        DataBody ReadDataBody(DataHeader header, BinaryReader reader)
        {
            var data = new DataBody(header.vertexCount);

            float x = 0, y = 0, z = 0;
            Byte r = 255, g = 255, b = 255, a = 255;

            for (var i = 0; i < header.vertexCount; i++)
            {
                foreach (var prop in header.properties)
                {
                    switch (prop)
                    {
                        case DataProperty.R8: r = reader.ReadByte(); break;
                        case DataProperty.G8: g = reader.ReadByte(); break;
                        case DataProperty.B8: b = reader.ReadByte(); break;
                        case DataProperty.A8: a = reader.ReadByte(); break;

                        case DataProperty.R16: r = (byte)(reader.ReadUInt16() >> 8); break;
                        case DataProperty.G16: g = (byte)(reader.ReadUInt16() >> 8); break;
                        case DataProperty.B16: b = (byte)(reader.ReadUInt16() >> 8); break;
                        case DataProperty.A16: a = (byte)(reader.ReadUInt16() >> 8); break;

                        case DataProperty.SingleX: x = reader.ReadSingle(); break;
                        case DataProperty.SingleY: y = reader.ReadSingle(); break;
                        case DataProperty.SingleZ: z = reader.ReadSingle(); break;

                        case DataProperty.DoubleX: x = (float)reader.ReadDouble(); break;
                        case DataProperty.DoubleY: y = (float)reader.ReadDouble(); break;
                        case DataProperty.DoubleZ: z = (float)reader.ReadDouble(); break;

                        case DataProperty.Data8: reader.ReadByte(); break;
                        case DataProperty.Data16: reader.BaseStream.Position += 2; break;
                        case DataProperty.Data32: reader.BaseStream.Position += 4; break;
                        case DataProperty.Data64: reader.BaseStream.Position += 8; break;
                    }
                }

                data.AddPoint(x, y, z, r, g, b, a, Rescale, ApplySRGBCorrection);
            }

            return data;
        }
        #endregion
    }

}

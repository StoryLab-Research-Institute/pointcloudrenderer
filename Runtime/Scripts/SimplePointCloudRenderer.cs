using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.Rendering;

namespace StoryLabResearch.PointCloud
{
    // Test harness: validates the URP Octree shader and PointCloudRenderFeature against an
    // existing imported mesh. Not part of the final octree pipeline.
    [ExecuteAlways]
    [RequireComponent(typeof(MeshFilter))]
    public class SimplePointCloudRenderer : MonoBehaviour, IPointCloudDrawable
    {
        [SerializeField] private Material _material;
        [SerializeField] private MeshFilter _source;

        private ComputeBuffer _positionBuffer;
        private ComputeBuffer _colorBuffer;
        private MaterialPropertyBlock _propertyBlock;
        private int _pointCount;

        // Positions are baked into the buffer in world space at OnEnable time.
        // Moving this GameObject after enable will not update the rendered positions.
        public Bounds WorldBounds { get; private set; }

        private void OnEnable()
        {
            if (_source == null)
                _source = GetComponent<MeshFilter>();

            var mesh = _source.sharedMesh;
            if (mesh == null) return;

            var localVertices = mesh.vertices;
            _pointCount = localVertices.Length;

            // Transform positions to world space and write as tightly-packed float3.
            var positions = new Point3[_pointCount];
            var matrix = transform.localToWorldMatrix;
            for (int i = 0; i < _pointCount; i++)
            {
                var wp = matrix.MultiplyPoint3x4(localVertices[i]);
                positions[i] = new Point3(wp.x, wp.y, wp.z);
            }

            var colors32 = mesh.colors32;
            var packedColors = new uint[_pointCount];
            if (colors32 != null && colors32.Length == _pointCount)
            {
                for (int i = 0; i < _pointCount; i++)
                {
                    var c = colors32[i];
                    packedColors[i] = (uint)c.r | ((uint)c.g << 8) | ((uint)c.b << 16) | ((uint)c.a << 24);
                }
            }
            else
            {
                for (int i = 0; i < _pointCount; i++)
                    packedColors[i] = 0xFFFFFFFF;
            }

            _positionBuffer = new ComputeBuffer(_pointCount, 12);
            _positionBuffer.SetData(positions);

            _colorBuffer = new ComputeBuffer(_pointCount, 4);
            _colorBuffer.SetData(packedColors);

            _propertyBlock = new MaterialPropertyBlock();
            _propertyBlock.SetBuffer("_Positions", _positionBuffer);
            _propertyBlock.SetBuffer("_ColorsPacked", _colorBuffer);

            WorldBounds = TransformBounds(mesh.bounds, matrix);

            PointCloudRenderFeature.PointCloudRenderPass.Register(this);
        }

        private void OnDisable()
        {
            PointCloudRenderFeature.PointCloudRenderPass.Deregister(this);

            _positionBuffer?.Release();
            _positionBuffer = null;

            _colorBuffer?.Release();
            _colorBuffer = null;
        }

        public void Draw(CommandBuffer cmd)
        {
            cmd.DrawProcedural(Matrix4x4.identity, _material, 0,
                MeshTopology.Triangles, _pointCount * 6, 1, _propertyBlock);
        }

        private static Bounds TransformBounds(Bounds localBounds, Matrix4x4 matrix)
        {
            var center = matrix.MultiplyPoint3x4(localBounds.center);
            var extents = localBounds.extents;
            // Transform all 8 corners and compute enclosing world-space bounds.
            var worldBounds = new Bounds(center, Vector3.zero);
            for (int i = 0; i < 8; i++)
            {
                var corner = center + matrix.MultiplyVector(new Vector3(
                    ((i & 1) == 0 ? -1f : 1f) * extents.x,
                    ((i & 2) == 0 ? -1f : 1f) * extents.y,
                    ((i & 4) == 0 ? -1f : 1f) * extents.z));
                worldBounds.Encapsulate(corner);
            }
            return worldBounds;
        }

        // Tightly-packed float3 matching the shader's StructuredBuffer<float3> stride of 12 bytes.
        [StructLayout(LayoutKind.Sequential)]
        private struct Point3
        {
            public float x, y, z;
            public Point3(float x, float y, float z) { this.x = x; this.y = y; this.z = z; }
        }
    }
}

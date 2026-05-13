using UnityEditor;
using UnityEngine;

namespace StoryLabResearch.PointCloud
{
    [CustomEditor(typeof(SimplePointCloudRenderer))]
    public class SimplePointCloudRendererEditor : Editor
    {
        // Draw the source mesh as a fully-transparent gizmo so the scene view picker
        // can hit it. Gizmos.DrawMesh participates in HandleUtility.PickGameObject,
        // which is what the scene view uses when you click to select.
        [DrawGizmo(GizmoType.NotInSelectionHierarchy | GizmoType.InSelectionHierarchy)]
        static void DrawPickingGizmo(SimplePointCloudRenderer renderer, GizmoType gizmoType)
        {
            var mf = renderer.GetComponent<MeshFilter>();
            if (mf == null || mf.sharedMesh == null) return;

            var prev = Gizmos.color;
            Gizmos.color = new Color(0, 0, 0, 0);
            Gizmos.matrix = renderer.transform.localToWorldMatrix;
            Gizmos.DrawMesh(mf.sharedMesh);
            Gizmos.matrix = Matrix4x4.identity;
            Gizmos.color = prev;
        }
    }
}

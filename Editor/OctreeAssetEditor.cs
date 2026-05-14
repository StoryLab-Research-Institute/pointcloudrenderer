using UnityEditor;
using UnityEngine;

namespace StoryLabResearch.PointCloud
{
    [CustomEditor(typeof(OctreeAsset))]
    public class OctreeAssetEditor : Editor
    {
        public override void OnInspectorGUI()
        {
            var metaProp = serializedObject.FindProperty("_nodeMetadata");

            EditorGUILayout.LabelField("Octree Asset", EditorStyles.boldLabel);

            if (metaProp != null)
                EditorGUILayout.LabelField("Node count", metaProp.arraySize.ToString());
        }
    }
}

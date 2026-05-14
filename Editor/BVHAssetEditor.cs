using UnityEditor;

namespace StoryLabResearch.PointCloud
{
    [CustomEditor(typeof(BVHAsset))]
    public class BVHAssetEditor : Editor
    {
        public override void OnInspectorGUI()
        {
            var metaProp = serializedObject.FindProperty("_nodeMetadata");

            EditorGUILayout.LabelField("BVH Asset", EditorStyles.boldLabel);

            if (metaProp != null)
                EditorGUILayout.LabelField("Node count", metaProp.arraySize.ToString());
        }
    }
}

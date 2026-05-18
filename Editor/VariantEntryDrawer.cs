using UnityEditor;
using UnityEngine;

namespace StoryLabResearch.PointCloud
{
    [CustomPropertyDrawer(typeof(PointCloudRenderer.VariantEntry))]
    public class VariantEntryDrawer : PropertyDrawer
    {
        public override float GetPropertyHeight(SerializedProperty property, GUIContent label)
        {
            return EditorGUI.GetPropertyHeight(property, label, property.isExpanded);
        }

        public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
        {
            var variantProp = property.FindPropertyRelative(nameof(PointCloudRenderer.VariantEntry.Variant));
            var variant     = variantProp?.objectReferenceValue as PointCloudVariant;

            string entryLabel = variant != null && !string.IsNullOrWhiteSpace(variant.VariantName)
                ? variant.VariantName
                : label.text; // fallback to "Element N"

            label = new GUIContent(entryLabel, label.tooltip);
            EditorGUI.PropertyField(position, property, label, property.isExpanded);
        }
    }
}

using UnityEngine;
using UnityEditor;
using UnityEditor.AssetImporters;
using UnityEditorInternal;

namespace StoryLabResearch.PointCloud
{
    [CustomEditor(typeof(PlyImporter))]
    public class PlyImporterInspector : ScriptedImporterEditor
    {
        SerializedProperty _axisPreset;
        SerializedProperty _axisX;
        SerializedProperty _axisY;
        SerializedProperty _axisZ;
        SerializedProperty _rescale;
        SerializedProperty _applySRGBCorrection;
        SerializedProperty _importProperties;
        SerializedProperty _variants;

        ReorderableList _variantList;

        public override void OnEnable()
        {
            base.OnEnable();

            _axisPreset          = serializedObject.FindProperty(nameof(PlyImporter.AxisPreset));
            _axisX               = serializedObject.FindProperty(nameof(PlyImporter.AxisX));
            _axisY               = serializedObject.FindProperty(nameof(PlyImporter.AxisY));
            _axisZ               = serializedObject.FindProperty(nameof(PlyImporter.AxisZ));
            _rescale             = serializedObject.FindProperty(nameof(PlyImporter.Rescale));
            _applySRGBCorrection = serializedObject.FindProperty(nameof(PlyImporter.ApplySRGBCorrection));
            _importProperties    = serializedObject.FindProperty(nameof(PlyImporter.ImportProperties));
            _variants            = serializedObject.FindProperty(nameof(PlyImporter.Variants));

            _variantList = new ReorderableList(serializedObject, _variants,
                draggable: true, displayHeader: true,
                displayAddButton: true, displayRemoveButton: true)
            {
                drawHeaderCallback  = rect => EditorGUI.LabelField(rect, "Variants  (index 0 = highest priority)"),
                drawElementCallback = DrawVariantElement,
                elementHeight       = EditorGUIUtility.singleLineHeight + 2f,
            };
        }

        private void DrawVariantElement(Rect rect, int index, bool isActive, bool isFocused)
        {
            var element = _variants.GetArrayElementAtIndex(index);
            rect.y      += 1f;
            rect.height  = EditorGUIUtility.singleLineHeight;

            var variant = element.objectReferenceValue as PointCloudVariant;
            string label = variant != null
                ? $"[{index}]  {variant.VariantName}  —  {variant.Platforms}"
                : $"[{index}]  (none)";

            EditorGUI.ObjectField(rect, element, typeof(PointCloudVariant), new GUIContent(label));
        }

        public override void OnInspectorGUI()
        {
            EditorGUILayout.LabelField("Coordinates", EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(_axisPreset, new GUIContent("Axis Preset",
                "Maps PLY axes to Unity world axes. Most photogrammetry and LiDAR tools export Z-up right-handed."));

            var preset = (PlyImporter.EAxisPreset)_axisPreset.intValue;
            if (preset == PlyImporter.EAxisPreset.Custom)
            {
                EditorGUI.indentLevel++;
                EditorGUILayout.PropertyField(_axisX, new GUIContent("Unity +X  ←  PLY"));
                EditorGUILayout.PropertyField(_axisY, new GUIContent("Unity +Y  ←  PLY"));
                EditorGUILayout.PropertyField(_axisZ, new GUIContent("Unity +Z  ←  PLY"));
                EditorGUI.indentLevel--;
            }
            else
            {
                string mapping = preset switch
                {
                    PlyImporter.EAxisPreset.ZUpLeftHanded  => "Unity (+X, +Y, +Z)  ←  PLY (+X, +Z, +Y)",
                    PlyImporter.EAxisPreset.ZUpRightHanded => "Unity (+X, +Y, +Z)  ←  PLY (−X, +Z, +Y)",
                    PlyImporter.EAxisPreset.YUpRightHanded => "Unity (+X, +Y, +Z)  ←  PLY (+X, +Y, −Z)",
                    _                                      => "Pass-through — PLY axes used as-is",
                };
                EditorGUILayout.HelpBox(mapping, MessageType.None);
            }

            EditorGUILayout.PropertyField(_rescale);
            EditorGUILayout.PropertyField(_applySRGBCorrection);
            EditorGUILayout.Space();

            EditorGUILayout.PropertyField(_importProperties,
                new GUIContent("Import Properties",
                    "Optional shared import properties asset. When set, the variant list below is ignored."));

            if (_importProperties.objectReferenceValue != null)
            {
                EditorGUILayout.HelpBox(
                    "Variants are driven by the Import Properties asset above. " +
                    "Clear it to use the per-asset variant list below.",
                    MessageType.Info);
            }
            else
            {
                EditorGUILayout.Space();
                _variantList.DoLayoutList();

                if (_variants.arraySize == 0)
                    EditorGUILayout.HelpBox(
                        "No variants configured. Add at least one PointCloudVariant to import this asset.",
                        MessageType.Warning);
            }

            serializedObject.ApplyModifiedProperties();
            ApplyRevertGUI();
        }
    }
}

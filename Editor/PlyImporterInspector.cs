using UnityEngine;
using UnityEditor.AssetImporters;
using UnityEditor;

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
        SerializedProperty _quality;
        SerializedProperty _performance;

        bool _qualityFoldout     = true;
        bool _performanceFoldout = true;

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
            _quality             = serializedObject.FindProperty(nameof(PlyImporter.Quality));
            _performance         = serializedObject.FindProperty(nameof(PlyImporter.Performance));
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
                // Show the resolved mapping as a read-only hint.
                string mapping = preset switch
                {
                    PlyImporter.EAxisPreset.ZUpRightHanded => "Unity (+X, +Y, +Z)  ←  PLY (−X, +Z, +Y)",
                    PlyImporter.EAxisPreset.ZUpLeftHanded  => "Unity (+X, +Y, +Z)  ←  PLY (+X, +Z, +Y)",
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
                    "Optional shared import properties asset. When set, all settings below are ignored."));

            if (_importProperties.objectReferenceValue != null)
            {
                EditorGUILayout.HelpBox(
                    "Settings are driven by the Import Properties asset above. " +
                    "Clear it to use per-asset inline settings.",
                    MessageType.Info);
            }
            else
            {
                EditorGUILayout.Space();
                DrawTierSection("Quality  (PC / Mac / Consoles)", _quality, ref _qualityFoldout);
                EditorGUILayout.Space();
                DrawTierSection("Performance  (Android / Quest)", _performance, ref _performanceFoldout);
            }

            serializedObject.ApplyModifiedProperties();
            ApplyRevertGUI();
        }

        private void DrawTierSection(string label, SerializedProperty tier, ref bool foldout)
        {
            foldout = EditorGUILayout.BeginFoldoutHeaderGroup(foldout, label);
            if (foldout)
            {
                EditorGUI.indentLevel++;

                EditorGUILayout.LabelField("Point Processing", EditorStyles.boldLabel);
                var spacingProp = tier.FindPropertyRelative(nameof(PlatformImportTier.MinPointSpacing));
                float newSpacing = EditorGUILayout.FloatField(
                    new GUIContent("Min Point Spacing",
                        "Cull points closer together than this world-space distance. 0 = disabled."),
                    spacingProp.floatValue);
                spacingProp.floatValue = Mathf.Max(0f, newSpacing);

                EditorGUILayout.Space();

                EditorGUILayout.LabelField("Material", EditorStyles.boldLabel);
                var modeProp = tier.FindPropertyRelative(nameof(PlatformImportTier.MaterialMode));
                EditorGUILayout.PropertyField(modeProp, new GUIContent("Mode"));

                var matProp = tier.FindPropertyRelative(nameof(PlatformImportTier.Material));
                var mode = (PointCloudImportProperties.EMaterialMode)modeProp.intValue;
                switch (mode)
                {
                    case PointCloudImportProperties.EMaterialMode.Shared:
                        EditorGUILayout.PropertyField(matProp,
                            new GUIContent("Material", "Used directly. Leave empty for the pipeline default."));
                        EditorGUILayout.HelpBox(
                            "The material reference is used as-is. Edits affect every cloud sharing it.",
                            MessageType.None);
                        break;
                    case PointCloudImportProperties.EMaterialMode.Instantiated:
                        EditorGUILayout.PropertyField(matProp,
                            new GUIContent("Source Material", "Material to copy. Leave empty for the pipeline default."));
                        EditorGUILayout.HelpBox(
                            "A copy is embedded inside this asset. Edit it via the sub-asset in the Project window.",
                            MessageType.None);
                        break;
                    case PointCloudImportProperties.EMaterialMode.Extracted:
                        EditorGUILayout.PropertyField(matProp,
                            new GUIContent("Source Material", "Material to copy. Leave empty for the pipeline default."));
                        EditorGUILayout.HelpBox(
                            "A copy is written as a standalone .mat file next to the .ply. " +
                            "Assign it as the Shared material on other clouds to reuse it.",
                            MessageType.None);
                        break;
                }

                EditorGUILayout.Space();

                EditorGUILayout.LabelField("Render Properties", EditorStyles.boldLabel);
                var renderProp = tier.FindPropertyRelative(nameof(PlatformImportTier.RenderProperties));
                EditorGUILayout.PropertyField(renderProp,
                    new GUIContent("Render Properties",
                        "Render settings applied to the imported OctreeRenderer for this tier. " +
                        "Leave unset to use OctreeRenderer built-in defaults."));

                EditorGUI.indentLevel--;
            }
            EditorGUILayout.EndFoldoutHeaderGroup();
        }
    }
}

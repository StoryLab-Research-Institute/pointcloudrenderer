using UnityEngine;
using UnityEditor.AssetImporters;
using UnityEditor;

namespace StoryLabResearch.PointCloud
{
    [CustomEditor(typeof(PlyImporter))]
    public class PlyImporterInspector : ScriptedImporterEditor
    {
        SerializedProperty _rescale;
        SerializedProperty _applySRGBCorrection;

        SerializedProperty _containerType;
        string[] _containerTypeNames;

        SerializedProperty _subsampleMode;
        SerializedProperty _subsampleValue;

        SerializedProperty _materialMode;
        SerializedProperty _customMaterialOverride;
        SerializedProperty _extractUniqueMaterial;

        SerializedProperty _debugRangeColors;
        GUIContent _debugRangeLabel = new("Debug Range");
        SerializedProperty _debugRange;

        public override void OnEnable()
        {
            base.OnEnable();

            _rescale = serializedObject.FindProperty(nameof(PlyImporter.Rescale));
            _applySRGBCorrection = serializedObject.FindProperty(nameof(PlyImporter.ApplySRGBCorrection));

            _containerType = serializedObject.FindProperty(nameof(PlyImporter.ContainerType));
            _containerTypeNames = System.Enum.GetNames(typeof(PlyImporter.EAssetContainerType));

            _subsampleMode = serializedObject.FindProperty(nameof(PlyImporter.SubsampleMode));
            _subsampleValue = serializedObject.FindProperty(nameof(PlyImporter.SubsampleValue));

            _materialMode = serializedObject.FindProperty(nameof(PlyImporter.MaterialMode));
            _customMaterialOverride = serializedObject.FindProperty(nameof(PlyImporter.CustomMaterialOverride));
            _extractUniqueMaterial = serializedObject.FindProperty(nameof(PlyImporter.ExtractUniqueMaterial));

            _debugRangeColors = serializedObject.FindProperty(nameof(PlyImporter.DebugRangeColors));
            _debugRange = serializedObject.FindProperty(nameof(PlyImporter.DebugRange));
        }

        public override void OnInspectorGUI()
        {
            EditorGUILayout.PropertyField(_rescale);
            EditorGUILayout.PropertyField(_applySRGBCorrection);

            _containerType.intValue = EditorGUILayout.Popup(
                "Container Type", _containerType.intValue, _containerTypeNames);

            EditorGUILayout.Space();

            if (_containerType.intValue == (int)PlyImporter.EAssetContainerType.PointMesh)
            {
                EditorGUILayout.PropertyField(_subsampleMode);
                if (_subsampleMode.intValue == (int)PointMeshSubsampler.ESubsampleMode.Random)
                    _subsampleValue.floatValue = EditorGUILayout.Slider("Subsample Factor", _subsampleValue.floatValue, 0f, 1f);
                else if (_subsampleMode.intValue == (int)PointMeshSubsampler.ESubsampleMode.SpatialFast
                      || _subsampleMode.intValue == (int)PointMeshSubsampler.ESubsampleMode.SpatialThreePass
                      || _subsampleMode.intValue == (int)PointMeshSubsampler.ESubsampleMode.SpatialExact)
                    _subsampleValue.floatValue = EditorGUILayout.FloatField("Minimum Distance", Mathf.Max(_subsampleValue.floatValue, 0f));

                EditorGUILayout.Space();

                EditorGUILayout.PropertyField(_materialMode);
                switch (_materialMode.intValue)
                {
                    case (int)PlyImporter.EMaterialMode.Unique:
                        EditorGUILayout.HelpBox("Create a unique material for this asset.\r\n\r\nBy default the material will be extracted to allow you to edit its properties. The extracted material will not be destroyed if you choose later not to extract it.", MessageType.Info);
                        EditorGUILayout.PropertyField(_extractUniqueMaterial);
                        break;
                    case (int)PlyImporter.EMaterialMode.Custom:
                        EditorGUILayout.HelpBox("Apply a custom user-selected material to this asset.\r\n\r\nThis can allow you to share a material with custom properties across many point cloud assets for better rendering efficiency.", MessageType.Info);
                        EditorGUILayout.PropertyField(_customMaterialOverride);
                        break;
                    default:
                        EditorGUILayout.HelpBox("Use the default shared point cloud rendering material, for more efficient rendering if you do not need to edit material properties.\r\n\r\nEditing the default material will affect all current and future point cloud assets set to this mode, and should be avoided - if you need custom properties, create a custom material instead.", MessageType.Warning);
                        break;
                }
            }

            EditorGUILayout.Space();

            EditorGUILayout.PropertyField(_debugRangeColors);
            if (_debugRangeColors.boolValue)
                _debugRange.intValue = Mathf.Max(0, EditorGUILayout.IntField(_debugRangeLabel, _debugRange.intValue));

            serializedObject.ApplyModifiedProperties();
            ApplyRevertGUI();
        }
    }
}

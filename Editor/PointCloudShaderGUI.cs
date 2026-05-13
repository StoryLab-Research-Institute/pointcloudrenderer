using UnityEditor;

namespace StoryLabResearch.PointCloud
{
    public class PointCloudShaderGUI : ShaderGUI
    {
        public override void OnGUI(MaterialEditor materialEditor, MaterialProperty[] properties)
        {
            MaterialProperty _PointSize = FindProperty("_PointSize", properties);
            materialEditor.ShaderProperty(_PointSize, _PointSize.displayName);

            MaterialProperty _ColorMode = FindProperty("_ColorMode", properties);
            materialEditor.ShaderProperty(_ColorMode, _ColorMode.displayName);

            if (_ColorMode.floatValue > 0) // Solid color mode
            {
                MaterialProperty _Color = FindProperty("_Color", properties);
                materialEditor.ShaderProperty(_Color, _Color.displayName);

                if (_ColorMode.floatValue > 1) // Blend mode
                {
                    MaterialProperty _ColorBlend = FindProperty("_ColorBlend", properties);
                    materialEditor.ShaderProperty(_ColorBlend, _ColorBlend.displayName);
                }
            }

            EditorGUILayout.Space();

            MaterialProperty _PointShape = FindProperty("_PointShape", properties, false);
            if (_PointShape != null)
                materialEditor.ShaderProperty(_PointShape, _PointShape.displayName);
        }
    }
}

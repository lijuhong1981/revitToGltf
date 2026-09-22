using System;
using Autodesk.Revit.DB;
using RevitToGltf.Models;
using RevitToGltf.Pipeline;

namespace RevitToGltf.Extraction
{
    /// <summary>
    /// 材质提取器：将Revit材质映射为glTF材质参数。
    /// 颜色优先级：渲染外观(真实观感) → 图形颜色(material.Color) → 默认灰。
    /// 贴图存在时由TextureExtractor外置图片并设置TextureUri。
    /// </summary>
    public static class MaterialExtractor
    {
        private static int s_scaleDiagCount;

        public static void Apply(RevitPrimitive primitive, Material material, Document doc, GltfExportContext context)
        {
            context.MaterialCount++;
            if (material == null)
            {
                primitive.MaterialName = "Default";
                primitive.BaseColor = new float[] { 0.8f, 0.8f, 0.8f, 1f };
                return;
            }

            primitive.MaterialName = material.Name;
            primitive.DoubleSided = true;

            // 贴图：解析渲染外观中的贴图图片并外置
            string textureUri = TextureExtractor.Extract(material, doc, context);
            if (!string.IsNullOrEmpty(textureUri))
            {
                primitive.TextureUri = textureUri;
                UV scale = TextureExtractor.GetRealWorldScale(material, doc);
                primitive.TextureRealWorldScaleU = scale.U;
                primitive.TextureRealWorldScaleV = scale.V;
                if (s_scaleDiagCount < 5)
                {
                    s_scaleDiagCount++;
                    context.Log(string.Format("贴图真实世界缩放[{0}]: U={1:F3}ft V={2:F3}ft", material.Name, scale.U, scale.V));
                }
            }

            // 颜色：优先取外观渲染色，其次取图形颜色
            float[] appearanceColor = TextureExtractor.GetAppearanceDiffuseColor(material, doc);
            if (appearanceColor != null)
            {
                primitive.BaseColor = appearanceColor;
            }
            else if (material.Color != null)
            {
                Color color = material.Color;
                primitive.BaseColor = new float[]
                {
                    color.Red / 255f,
                    color.Green / 255f,
                    color.Blue / 255f,
                    1f
                };
            }

            // 透明度：材质透明度(0~100) → glTF alpha
            if (material.Transparency > 0)
                primitive.BaseColor[3] = (float)(1.0 - material.Transparency / 100.0);
        }
    }
}

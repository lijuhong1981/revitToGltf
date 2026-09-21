using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Visual;
using RevitToGltf.Models;
using RevitToGltf.Pipeline;

namespace RevitToGltf.Extraction
{
    /// <summary>
    /// 贴图提取器：
    /// 从材质的渲染外观(AppearanceAssetElement.GetRenderingAsset())递归查找贴图图片路径，
    /// 复制到输出目录 textures/ 下（按内容哈希命名，天然去重），返回相对URI。
    ///
    /// 这是解决"IFC导出丢贴图"痛点的核心环节：Revit渲染外观里保存着原始图片路径，
    /// 直接拷贝文件即可，不经任何中间格式转换。
    /// </summary>
    public static class TextureExtractor
    {
        public const string TextureFolder = "textures";

        /// <summary>已复制贴图缓存：输出目录|源路径 → 相对URI（同一会话不同输出目录需各自复制）</summary>
        private static readonly Dictionary<string, string> CopiedTextures = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// 提取材质贴图并复制到输出目录
        /// @return 相对URI（如 textures/a1b2c3.png），无贴图返回null
        /// </summary>
        public static string Extract(Material material, Document doc, GltfExportContext context)
        {
            try
            {
                Asset renderingAsset = GetAppearanceAsset(material, doc);
                if (renderingAsset == null)
                    return null;
                context.AppearanceAssetCount++;

                // 递归查找贴图文件路径（外观资产里图片存在名称含bitmap的字符串属性中）
                string bitmapPath = FindBitmapPath(renderingAsset);
                if (string.IsNullOrEmpty(bitmapPath) || !File.Exists(bitmapPath))
                    return null;

                string uri = CopyTexture(bitmapPath, context);
                context.BitmapTextureCount++;
                return uri;
            }
            catch (Exception ex)
            {
                context.Log(string.Format("贴图提取失败(材质 {0}): {1}", material.Name, ex.Message));
                return null;
            }
        }

        /// <summary>取得材质的渲染外观资产（无外观资产或读取失败返回null）</summary>
        private static Asset GetAppearanceAsset(Material material, Document doc)
        {
            if (material.AppearanceAssetId == null || material.AppearanceAssetId == ElementId.InvalidElementId)
                return null;
            AppearanceAssetElement assetElement = doc.GetElement(material.AppearanceAssetId) as AppearanceAssetElement;
            if (assetElement == null)
                return null;
            return assetElement.GetRenderingAsset();
        }

        /// <summary>
        /// 读取渲染外观的漫反射颜色（generic_diffuse）
        /// @return [r,g,b,1] 或 null
        /// </summary>
        public static float[] GetAppearanceDiffuseColor(Material material, Document doc)
        {
            try
            {
                Asset asset = GetAppearanceAsset(material, doc);
                if (asset == null)
                    return null;

                AssetProperty diffuse = asset.FindByName("generic_diffuse");
                var colorProp = diffuse as AssetPropertyDoubleArray4d;
                if (colorProp != null)
                {
                    IList<double> values = colorProp.GetValueAsDoubles();
                    if (values != null && values.Count >= 3)
                        return new float[] { (float)values[0], (float)values[1], (float)values[2], 1f };
                }

                // 部分材质把颜色放在 diffuse_color 之类的命名下，做一次模糊匹配
                AssetProperty colorBySearch = FindColorProperty(asset);
                if (colorBySearch != null)
                {
                    IList<double> values = (colorBySearch as AssetPropertyDoubleArray4d).GetValueAsDoubles();
                    if (values != null && values.Count >= 3)
                        return new float[] { (float)values[0], (float)values[1], (float)values[2], 1f };
                }
                return null;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>递归查找外观资产中的贴图文件路径（兼容unifiedbitmap_Bitmap等命名）</summary>
        private static string FindBitmapPath(Asset asset)
        {
            if (asset == null) return null;
            for (int i = 0; i < asset.Size; i++)
            {
                AssetProperty property = asset.Get(i);
                if (property == null) continue;

                var stringProperty = property as AssetPropertyString;
                if (stringProperty != null
                    && property.Name.IndexOf("bitmap", StringComparison.OrdinalIgnoreCase) >= 0
                    && !string.IsNullOrEmpty(stringProperty.Value))
                {
                    return stringProperty.Value;
                }

                if (property.Type == AssetPropertyType.Asset)
                {
                    string nested = FindBitmapPath(property as Asset);
                    if (!string.IsNullOrEmpty(nested)) return nested;
                }
            }
            return null;
        }

        /// <summary>模糊查找颜色属性（名称含color且为四元组）</summary>
        private static AssetProperty FindColorProperty(Asset asset)
        {
            if (asset == null) return null;
            for (int i = 0; i < asset.Size; i++)
            {
                AssetProperty property = asset.Get(i);
                if (property == null) continue;

                if (property.Type == AssetPropertyType.Double4
                    && property.Name.IndexOf("color", StringComparison.OrdinalIgnoreCase) >= 0)
                    return property;

                if (property.Type == AssetPropertyType.Asset)
                {
                    AssetProperty nested = FindColorProperty(property as Asset);
                    if (nested != null) return nested;
                }
            }
            return null;
        }

        /// <summary>复制贴图文件到输出目录（按内容哈希命名去重，缓存按输出目录区分）</summary>
        private static string CopyTexture(string sourcePath, GltfExportContext context)
        {
            string cacheKey = context.OutputDirectory + "|" + sourcePath;
            string cached;
            if (CopiedTextures.TryGetValue(cacheKey, out cached))
                return cached;

            byte[] content = File.ReadAllBytes(sourcePath);
            string hash;
            using (var sha1 = SHA1.Create())
            {
                byte[] digest = sha1.ComputeHash(content);
                hash = BitConverter.ToString(digest).Replace("-", "").Substring(0, 16).ToLowerInvariant();
            }

            string extension = Path.GetExtension(sourcePath);
            if (string.IsNullOrEmpty(extension)) extension = ".png";
            string relativeUri = TextureFolder + "/" + hash + extension.ToLowerInvariant();

            string targetDirectory = Path.Combine(context.OutputDirectory, TextureFolder);
            Directory.CreateDirectory(targetDirectory);
            string targetPath = Path.Combine(context.OutputDirectory, relativeUri.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(targetPath))
            {
                File.WriteAllBytes(targetPath, content);
                context.TextureFileCount++;
                context.Log(string.Format("贴图写出: {0} ({1}KB)", relativeUri, content.Length / 1024));
            }

            CopiedTextures[cacheKey] = relativeUri;
            return relativeUri;
        }
    }
}

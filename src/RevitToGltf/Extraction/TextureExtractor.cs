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

        /// <summary>诊断用：记录上次导出上下文，用于每次导出时重置外观资产结构诊断计数</summary>
        private static GltfExportContext s_lastDiagContext;
        private static int s_diagCount;

        /// <summary>
        /// 解析材质贴图的源文件绝对路径（不复制、不转格式）。
        /// @return 绝对路径，无贴图返回 null。分离模式由调用方调 CopyTexture 复制到 textures/；
        /// 内嵌模式直接使用该路径读取字节。
        /// </summary>
        public static string Extract(Material material, Document doc, GltfExportContext context)
        {
            try
            {
                Asset renderingAsset = GetAppearanceAsset(material, doc);
                if (renderingAsset == null)
                    return null;
                context.AppearanceAssetCount++;

                // 每次导出（新 context）重置诊断计数，确保反复测试也能看到外观资产结构
                if (!ReferenceEquals(context, s_lastDiagContext))
                {
                    s_lastDiagContext = context;
                    s_diagCount = 0;
                    context.Log("贴图解析器: v2 支持 unifiedbitmap 管道分隔/正反斜杠混用路径");
                }
                if (s_diagCount < 3)
                {
                    s_diagCount++;
                    context.Log(string.Format("外观资产结构[{0}]: {1}", material.Name, DescribeAsset(renderingAsset)));
                }

                // 递归查找贴图文件路径（外观资产里图片存在名称含bitmap的字符串属性中）
                string bitmapPath = ResolveBitmapPath(FindBitmapPath(renderingAsset));
                if (string.IsNullOrEmpty(bitmapPath) || !File.Exists(bitmapPath))
                    return null;

                context.BitmapTextureCount++;
                return bitmapPath;
            }
            catch (Exception ex)
            {
                context.Log(string.Format("贴图提取失败(材质 {0}): {1}", material.Name, ex.Message));
                return null;
            }
        }

        /// <summary>贴图格式是否可直接内嵌进 glTF（glTF 2.0 仅定义 image/png 与 image/jpeg）</summary>
        public static bool IsEmbeddable(string path)
        {
            switch (Path.GetExtension(path).ToLowerInvariant())
            {
                case ".png":
                case ".jpg":
                case ".jpeg":
                    return true;
                default:
                    return false;
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

        /// <summary>
        /// 递归查找外观资产中的贴图文件路径（兼容 unifiedbitmap_Bitmap 等命名）。
        /// Revit 外观资产里贴图通常挂在 Reference 类型的连接资产上（如 generic_diffuse →
        /// unifiedbitmap_Bitmap），因此除嵌套 Asset 外还需跟随连接资产（GetSingleConnectedAsset）递归。
        /// </summary>
        private static string FindBitmapPath(Asset asset)
        {
            if (asset == null) return null;
            for (int i = 0; i < asset.Size; i++)
            {
                AssetProperty property = asset.Get(i);
                if (property == null) continue;

                var stringProperty = property as AssetPropertyString;
                if (stringProperty != null && !string.IsNullOrEmpty(stringProperty.Value))
                {
                    // 真实贴图以 unifiedbitmap_Bitmap 等含 bitmap 的命名给出；
                    // thumbnail/swatch 是 UI 预览图标（也常 .png 结尾），必须排除，否则会先于真实贴图被误命中
                    bool isBitmapProperty = property.Name.IndexOf("bitmap", StringComparison.OrdinalIgnoreCase) >= 0;
                    bool isIconProperty = property.Name.IndexOf("thumbnail", StringComparison.OrdinalIgnoreCase) >= 0
                                       || property.Name.IndexOf("swatch", StringComparison.OrdinalIgnoreCase) >= 0;
                    if (isBitmapProperty || (!isIconProperty && LooksLikeImagePath(stringProperty.Value)))
                        return stringProperty.Value;
                }

                // 连接资产：贴图通常作为"连接资产"挂在属性上（如 generic_diffuse）。
                // 其 Type 未必是 Reference，必须按 NumberOfConnectedProperties 遍历才能取到。
                for (int c = 0; c < property.NumberOfConnectedProperties; c++)
                {
                    try
                    {
                        string nested = FindBitmapPath(property.GetConnectedProperty(c) as Asset);
                        if (!string.IsNullOrEmpty(nested)) return nested;
                    }
                    catch { /* 读取连接资产失败时忽略 */ }
                }

                // 嵌套资产
                if (property.Type == AssetPropertyType.Asset)
                {
                    string nested = FindBitmapPath(property as Asset);
                    if (!string.IsNullOrEmpty(nested)) return nested;
                }
            }
            return null;
        }

        /// <summary>
        /// 读取贴图真实世界缩放（单位：英尺/贴图重复一次）。
        /// 用于把 face.Project 得到的英尺制参数域坐标换算成"贴图重复次数"，避免贴图被拉伸到整面。
        /// 无外观资产或无贴图时返回 (1,1)（即每 1 英尺重复一次）。
        /// </summary>
        public static UV GetRealWorldScale(Material material, Document doc)
        {
            try
            {
                Asset appearance = GetAppearanceAsset(material, doc);
                Asset bitmapAsset = FindBitmapAsset(appearance);
                if (bitmapAsset == null) return new UV(1.0, 1.0);

                double sx = ReadDistance(bitmapAsset, "texture_RealWorldScaleX");
                double sy = ReadDistance(bitmapAsset, "texture_RealWorldScaleY");
                if (sx <= 1e-6) sx = 1.0;
                if (sy <= 1e-6) sy = 1.0;
                return new UV(sx, sy);
            }
            catch
            {
                return new UV(1.0, 1.0);
            }
        }

        /// <summary>递归定位含 unifiedbitmap_Bitmap 的贴图资产（连接资产或嵌套资产），无则返回 null</summary>
        private static Asset FindBitmapAsset(Asset asset)
        {
            if (asset == null) return null;
            for (int i = 0; i < asset.Size; i++)
            {
                AssetProperty property = asset.Get(i);
                if (property == null) continue;

                // 该资产直接含名为 *bitmap 的字符串属性 → 即贴图资产
                if (property.Name.IndexOf("bitmap", StringComparison.OrdinalIgnoreCase) >= 0
                    && property is AssetPropertyString)
                    return asset;

                for (int c = 0; c < property.NumberOfConnectedProperties; c++)
                {
                    try
                    {
                        Asset nested = FindBitmapAsset(property.GetConnectedProperty(c) as Asset);
                        if (nested != null) return nested;
                    }
                    catch { }
                }

                if (property.Type == AssetPropertyType.Asset)
                {
                    Asset nested = FindBitmapAsset(property as Asset);
                    if (nested != null) return nested;
                }
            }
            return null;
        }

        /// <summary>读取距离属性值并换算为英尺；属性缺失或非距离/双精度类型返回 0</summary>
        private static double ReadDistance(Asset asset, string name)
        {
            AssetProperty p = asset.FindByName(name);
            if (p == null) return 0.0;
            var d = p as AssetPropertyDistance;
            if (d != null)
                // AssetPropertyDistance.Value 的单位随 DisplayUnitType 变化（厘米/米等），
                // 需按 DisplayUnitType 转成英尺（Revit 内部长度单位）后再参与 UV 换算。
                return UnitUtils.ConvertToInternalUnits(d.Value, d.DisplayUnitType);
            var dbl = p as AssetPropertyDouble;
            if (dbl != null) return dbl.Value;
            return 0.0;
        }

        /// <summary>值是否像图片文件路径（按扩展名判断）</summary>
        private static bool LooksLikeImagePath(string value)
        {
            if (string.IsNullOrEmpty(value)) return false;
            switch (Path.GetExtension(value).ToLowerInvariant())
            {
                case ".png": case ".jpg": case ".jpeg": case ".bmp":
                case ".tif": case ".tiff": case ".gif": case ".tga": case ".dds":
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>Autodesk 材质库贴图根目录候选（库材质常存相对路径，需拼回根目录）</summary>
        private static readonly string[] s_textureRoots = new[]
        {
            @"C:\Program Files (x86)\Common Files\Autodesk Shared\Materials\Textures",
            @"C:\Program Files\Common Files\Autodesk Shared\Materials\Textures"
        };

        /// <summary>
        /// 解析贴图路径：库材质的 unifiedbitmap_Bitmap 可能是
        /// "path1|path2|path3"（同一贴图的多个分辨率），且正反斜杠混用（如 Textures/\1/Mats）。
        /// 取第一段、统一为反斜杠、折叠连续分隔符后再判断存在性；相对路径拼到 Autodesk 材质库根目录。
        /// </summary>
        private static string ResolveBitmapPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;

            // 取第一段（管道前）
            string first = path;
            int pipe = first.IndexOf('|');
            if (pipe >= 0) first = first.Substring(0, pipe);
            first = first.Trim();

            // 统一为反斜杠并折叠连续分隔符（Textures/\1/Mats → Textures\1\Mats）
            first = first.Replace('/', '\\');
            while (first.IndexOf("\\\\", StringComparison.Ordinal) >= 0)
                first = first.Replace("\\\\", "\\");

            if (File.Exists(first)) return first;

            // 绝对路径但文件不存在：原样返回，由上层 File.Exists 判空
            if (Path.IsPathRooted(first)) return first;

            foreach (string root in s_textureRoots)
            {
                string full = Path.Combine(root, first.TrimStart('\\'));
                if (File.Exists(full)) return full;
            }
            return first;
        }

        /// <summary>把外观资产属性树序列化为单行字符串，便于日志诊断贴图为何提取不到</summary>
        private static string DescribeAsset(Asset asset)
        {
            if (asset == null) return "null";
            var parts = new List<string>();
            for (int i = 0; i < asset.Size; i++)
            {
                AssetProperty p = asset.Get(i);
                if (p == null) continue;

                string v = "";
                var sp = p as AssetPropertyString;
                if (sp != null) v = "=\"" + (sp.Value ?? "") + "\"";
                else if (p.Type == AssetPropertyType.Asset) v = "=" + DescribeAsset(p as Asset);
                for (int c = 0; c < p.NumberOfConnectedProperties; c++)
                {
                    try { v += "~" + DescribeAsset(p.GetConnectedProperty(c) as Asset); }
                    catch { v += "~(err)"; }
                }
                parts.Add(p.Name + ":" + p.Type + v);
            }
            return "{" + string.Join(", ", parts) + "}";
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

        /// <summary>复制贴图文件到输出目录（按内容哈希命名去重，缓存按输出目录区分）。
        /// 开启归一化时哈希基于归一化后的字节（相近尺寸的同图能多去重一次）。</summary>
        public static string CopyTexture(string sourcePath, GltfExportContext context)
        {
            string cacheKey = context.OutputDirectory + "|" + sourcePath;
            string cached;
            if (CopiedTextures.TryGetValue(cacheKey, out cached))
                return cached;

            byte[] content = GetTextureBytes(sourcePath, context);
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

        /// <summary>贴图 2 的幂上限：与 modelTo3DTiles 的 ATLAS_MAX_SIZE 对齐</summary>
        private const int MaxTextureSize = 2048;

        /// <summary>归一化结果缓存：归一化开关|源路径 → 字节（仅 NPOT 贴图入缓存；同一会话不同开关不串扰）</summary>
        private static readonly Dictionary<string, byte[]> NormalizedTextures = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// 读取贴图字节：开启归一化且为 PNG/JPEG 的非 2 的幂贴图时，先重采样到最近 2 的幂（上限 2048）。
        /// Cesium 的 glTF 纹理路径对 REPEAT+mipmap 的 NPOT 贴图会无条件用 canvas 放大到下一 2 的幂
        /// （画质差、像素最多 4 倍、每次加载都做），转换期预处理为最近 2 的幂可三重收益。
        /// 分离（写 textures/）与内嵌（进 .bin/.glb）两路共用；任何失败回退原始字节，绝不阻断导出。
        /// </summary>
        public static byte[] GetTextureBytes(string sourcePath, GltfExportContext context)
        {
            byte[] content = File.ReadAllBytes(sourcePath);
            if (!context.NormalizeTextures || !IsEmbeddable(sourcePath))
                return content;

            string cacheKey = (context.NormalizeTextures ? "N|" : "R|") + sourcePath;
            byte[] normalized;
            if (NormalizedTextures.TryGetValue(cacheKey, out normalized))
            {
                // 缓存命中（同一会话先导过其它格式）：贴图仍是归一化后的，计入本文件统计
                context.NormalizedTextureCount++;
                return normalized;
            }

            normalized = NormalizeToPowerOfTwo(content, Path.GetExtension(sourcePath), context);
            if (normalized != null)
            {
                NormalizedTextures[cacheKey] = normalized;
                return normalized;
            }
            return content;
        }

        /// <summary>
        /// GDI+ 重采样到最近 2 的幂（每边独立，上限 2048）。已是 2 的幂返回 null（无需处理）。
        /// jpg 保持 jpg（质量 90）、png 保持 png，避免格式漂移导致体积暴涨；
        /// 解码/编码失败返回 null，由调用方回退原始字节。
        /// </summary>
        private static byte[] NormalizeToPowerOfTwo(byte[] content, string extension, GltfExportContext context)
        {
            try
            {
                using (var ms = new MemoryStream(content))
                using (System.Drawing.Image image = System.Drawing.Image.FromStream(ms))
                {
                    int width = image.Width, height = image.Height;
                    int targetWidth = Math.Min(NearestPowerOfTwo(width), MaxTextureSize);
                    int targetHeight = Math.Min(NearestPowerOfTwo(height), MaxTextureSize);
                    if ((IsPowerOfTwo(width) && IsPowerOfTwo(height))
                        || (targetWidth == width && targetHeight == height))
                        return null;

                    using (var resized = new System.Drawing.Bitmap(targetWidth, targetHeight))
                    using (var g = System.Drawing.Graphics.FromImage(resized))
                    {
                        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.HighQuality;
                        g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
                        // TileFlipXY 防止重采样读取越界时边缘与透明背景混合出现半透明镶边
                        using (var attrs = new System.Drawing.Imaging.ImageAttributes())
                        {
                            attrs.SetWrapMode(System.Drawing.Drawing2D.WrapMode.TileFlipXY);
                            g.DrawImage(image,
                                new System.Drawing.Rectangle(0, 0, targetWidth, targetHeight),
                                0, 0, width, height, System.Drawing.GraphicsUnit.Pixel, attrs);
                        }

                        using (var outMs = new MemoryStream())
                        {
                            bool isJpeg = extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase)
                                       || extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase);
                            if (isJpeg)
                            {
                                System.Drawing.Imaging.ImageCodecInfo jpeg = FindJpegEncoder();
                                if (jpeg != null)
                                {
                                    using (var eps = new System.Drawing.Imaging.EncoderParameters(1))
                                    {
                                        eps.Param[0] = new System.Drawing.Imaging.EncoderParameter(
                                            System.Drawing.Imaging.Encoder.Quality, 90L);
                                        resized.Save(outMs, jpeg, eps);
                                    }
                                }
                                else
                                {
                                    resized.Save(outMs, System.Drawing.Imaging.ImageFormat.Jpeg);
                                }
                            }
                            else
                            {
                                resized.Save(outMs, System.Drawing.Imaging.ImageFormat.Png);
                            }

                            context.NormalizedTextureCount++;
                            context.Log(string.Format("贴图归一化: {0} {1}×{2} → {3}×{4}",
                                Path.GetExtension(extension), width, height, targetWidth, targetHeight));
                            return outMs.ToArray();
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                context.Log(string.Format("贴图归一化失败({0}): {1}，使用原始贴图", extension, ex.Message));
                return null;
            }
        }

        private static System.Drawing.Imaging.ImageCodecInfo FindJpegEncoder()
        {
            foreach (System.Drawing.Imaging.ImageCodecInfo codec in System.Drawing.Imaging.ImageCodecInfo.GetImageEncoders())
                if (codec.MimeType == "image/jpeg")
                    return codec;
            return null;
        }

        private static bool IsPowerOfTwo(int n)
        {
            return n > 0 && (n & (n - 1)) == 0;
        }

        /// <summary>最近的 2 次幂（≥1），如 600→512、800→1024</summary>
        private static int NearestPowerOfTwo(int n)
        {
            return (int)Math.Max(1, Math.Pow(2, Math.Round(Math.Log(n, 2))));
        }
    }
}

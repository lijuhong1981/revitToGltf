using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using RevitToGltf.Models;
using RevitToGltf.Pipeline;

namespace RevitToGltf.Extraction
{
    /// <summary>
    /// 几何提取器：遍历文档元素提取三角网格并按材质分组。
    /// 要点：
    /// - 支持项目文档(rvt)与族文档(rfa)，嵌套族几何通过GeometryInstance展开为世界坐标；
    /// - 单位由Revit内部英尺换算为米（×0.3048），坐标系保持Revit的Z-up；
    /// - 逐面三角化，平面法线由三角形叉积计算，UV按面的投影参数域归一化到[0,1]。
    /// </summary>
    public static class GeometryExtractor
    {
        /// <summary>英尺 → 米</summary>
        public const double FeetToMeter = 0.3048;

        /// <summary>顶点焊接量化精度：米 × 10000，即 0.1mm。同一面内共享点的浮点漂移在此范围内即合并。</summary>
        private const double WeldScale = 10000.0;

        /// <summary>变换一致性诊断日志计数（限制输出，避免刷屏）。</summary>
        private static int s_transformLogCount = 0;

        /// <summary>尺寸签名诊断日志计数（限制输出，避免刷屏）。</summary>
        private static int s_sigLogCount = 0;

        /// <summary>结构柱放置诊断是否已执行（每次导出仅一次）。</summary>
        private static bool s_placementDiagDone = false;

        /// <summary>
        /// 按指定精度档位与元素范围提取几何。
        /// settings为null时用Fine档（与默认一致）；elementIds为null时提取全模型非类型元素，
        /// 非空时仅提取指定元素（支持视图可见/选中子集）。
        /// </summary>
        public static ExtractResult Extract(Document doc, GltfExportContext context,
            GeometryDetailSettings settings, ICollection<ElementId> elementIds)
        {
            if (settings == null)
                settings = new GeometryDetailSettings("model", ViewDetailLevel.Fine, null);

            var result = new ExtractResult();
            var options = new Options
            {
                ComputeReferences = false,
                IncludeNonVisibleObjects = false,
                DetailLevel = settings.DetailLevel
            };

            LogPlacementDiagnostics(doc, context);

            FilteredElementCollector collector;
            if (elementIds == null)
            {
                collector = new FilteredElementCollector(doc).WhereElementIsNotElementType();
            }
            else
            {
                if (elementIds.Count == 0)
                {
                    context.Log("元素范围为空，跳过几何提取。");
                    return result;
                }
                collector = new FilteredElementCollector(doc, elementIds).WhereElementIsNotElementType();
            }

            // 一次性物化：FilteredElementCollector 是惰性迭代器，先取总数才能算百分比
            var elements = new List<Element>(collector);
            int total = elements.Count;
            if (total == 0)
            {
                context.Log("元素范围为空，跳过几何提取。");
                return result;
            }
            context.ReportNow(0, string.Format("几何提取 0/{0}", total));

            // 分组键 "符号Id|实际尺寸签名" → 共享网格下标；-1 表示该组已探测为无几何
            var sharedMeshByKey = new Dictionary<string, int>();

            int index = 0;
            foreach (Element element in elements)
            {
                index++;
                context.ElementCount++;
                if (settings.LogProgressEvery > 0 && context.ElementCount % settings.LogProgressEvery == 0)
                    context.Log(string.Format("进度: 已处理元素 {0} 个, 三角形 {1}", context.ElementCount, context.TriangleCount));

                context.ThrowIfCancelled();
                context.Report(index * 100 / total,
                    string.Format("几何提取 {0}/{1}　三角形 {2:N0}", index, total, context.TriangleCount));
                try
                {
                    if (settings.EnableInstancing
                        && TryExtractInstance(element as FamilyInstance, options, result, sharedMeshByKey, context, settings))
                        continue;

                    ExtractUnique(element, options, result, context, settings);
                }
                catch (Exception ex)
                {
                    context.SkippedElementCount++;
                    context.Log(string.Format("跳过元素 {0}({1}): {2}", element.Id.IntegerValue, element.Name, ex.Message));
                }
            }

            context.SharedMeshCount = result.SharedMeshes.Count;
            context.InstanceCount = result.Instances.Count;
            context.ExpandedTriangleCount = ComputeExpandedTriangleCount(result);

            context.Log(string.Format("几何提取完成[{0}]: 元素 {1} 个, 含几何 {2} 个, 三角形 {3} 个, 跳过 {4} 个",
                settings.TierName, context.ElementCount, context.MeshElementCount, context.TriangleCount, context.SkippedElementCount));
            if (context.SharedMeshCount > 0)
                context.Log(string.Format("实例化: 共享网格 {0} 个, 实例 {1} 个, 去重后三角形 {2:N0} 个, 展开三角形 {3:N0} 个 (省 {4:0.0}%)",
                    context.SharedMeshCount, context.InstanceCount, context.TriangleCount, context.ExpandedTriangleCount,
                    (1 - (double)context.TriangleCount / Math.Max(1, context.ExpandedTriangleCount)) * 100));

            TrimPrimitives(result, context);
            return result;
        }

        /// <summary>
        /// 结构柱放置诊断（每次导出一次）：定位柱类构件 Z 向偏移的坐标参考来源。
        /// 列出全部标高，并按类别/名称取样，对比 GetTotalTransform 原 Z、修正 Z、基标高与真实几何 Z 范围。
        /// </summary>
        private static void LogPlacementDiagnostics(Document doc, GltfExportContext context)
        {
            if (s_placementDiagDone) return;
            s_placementDiagDone = true;
            try
            {
                var projPos = doc.ActiveProjectLocation.GetProjectPosition(XYZ.Zero);
                context.Log(string.Format("项目坐标: 内部原点共享坐标 E={0:0.00}m N={1:0.00}m 高程={2:0.00}m",
                    projPos.EastWest * FeetToMeter, projPos.NorthSouth * FeetToMeter, projPos.Elevation * FeetToMeter));

                var levels = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>()
                    .OrderBy(l => l.Elevation)
                    .Select(l => string.Format("{0}({1:0.00}ft)", l.Name, l.Elevation));
                context.Log(string.Format("标高列表({0}): {1}", levels.Count(), string.Join(" | ", levels)));

                var opts = new Options { ComputeReferences = false, IncludeNonVisibleObjects = false, DetailLevel = ViewDetailLevel.Fine };

                // 取样：结构柱 + 结构框架 + 按名称补采（柱Z6/扶梯/腋角等）
                var sample = new List<FamilyInstance>();
                sample.AddRange(new FilteredElementCollector(doc)
                    .OfCategory(BuiltInCategory.OST_StructuralColumns)
                    .WhereElementIsNotElementType().Cast<FamilyInstance>().Take(5));
                sample.AddRange(new FilteredElementCollector(doc)
                    .OfCategory(BuiltInCategory.OST_StructuralFraming)
                    .WhereElementIsNotElementType().Cast<FamilyInstance>().Take(3));
                foreach (string kw in new[] { "柱Z", "扶梯", "腋角" })
                    sample.AddRange(new FilteredElementCollector(doc)
                        .WhereElementIsNotElementType().OfClass(typeof(FamilyInstance))
                        .Cast<FamilyInstance>().Where(fi => fi.Name != null && fi.Name.Contains(kw)).Take(4));

                var seen = new HashSet<ElementId>();
                foreach (FamilyInstance fi in sample)
                {
                    if (fi == null || !seen.Add(fi.Id)) continue;
                    Transform t = fi.GetTotalTransform();
                    if (t == null) continue;
                    Transform corrected = CorrectPlacementZ(fi, t);

                    string loc = "无Location";
                    var lp = fi.Location as LocationPoint;
                    if (lp != null) loc = string.Format("Point.Z={0:0.00}ft", lp.Point.Z);
                    var lc = fi.Location as LocationCurve;
                    if (lc != null && lc.Curve != null)
                    {
                        XYZ p0 = lc.Curve.GetEndPoint(0), p1 = lc.Curve.GetEndPoint(1);
                        loc = string.Format("Curve.Z={0:0.00}→{1:0.00}ft", p0.Z, p1.Z);
                    }

                    string lvls = "无标高";
                    double baseOffset = 0;
                    try
                    {
                        Parameter bp = fi.get_Parameter(BuiltInParameter.FAMILY_BASE_LEVEL_PARAM);
                        Level bl = bp != null ? doc.GetElement(bp.AsElementId()) as Level : null;
                        lvls = string.Format("基标高={0}({1:0.00}ft)", bl != null ? bl.Name : "?", bl != null ? bl.Elevation : 0);
                        Parameter off = fi.get_Parameter(BuiltInParameter.FAMILY_BASE_LEVEL_OFFSET_PARAM);
                        baseOffset = off != null ? off.AsDouble() : 0;
                    }
                    catch { }

                    string geomZ = "无几何";
                    try
                    {
                        GeometryElement ge = fi.get_Geometry(opts);
                        if (ge != null)
                            foreach (GeometryObject o in ge)
                            {
                                var gi = o as GeometryInstance;
                                if (gi != null)
                                {
                                    GeometryElement g = gi.GetInstanceGeometry();
                                    BoundingBoxXYZ bb = g != null ? ComputeBBox(g) : null;
                                    if (bb != null) geomZ = string.Format("InstGeom bbox.Z={0:0.00}→{1:0.00}ft", bb.Min.Z, bb.Max.Z);
                                    break;
                                }
                                var s = o as Solid;
                                if (s != null)
                                {
                                    BoundingBoxXYZ bb = s.GetBoundingBox();
                                    if (bb != null) geomZ = string.Format("直接Solid bbox.Z={0:0.00}→{1:0.00}ft", bb.Min.Z, bb.Max.Z);
                                    break;
                                }
                            }
                    }
                    catch { }

                    context.Log(string.Format("放置诊断[{0}]: 原Z={1:0.00}ft 修正Z={2:0.00}ft | {3} | {4} | 底偏移={5:0.00}ft | {6}",
                        fi.Name, t.Origin.Z, corrected.Origin.Z, loc, lvls, baseOffset, geomZ));
                }
            }
            catch (Exception ex)
            {
                context.Log("放置诊断失败: " + ex.Message);
            }
        }

        /// <summary>
        /// 尝试按"族符号 + 实际尺寸"分组实例化。仅当元素是可复用的 FamilyInstance
        /// （非就地族）时处理：同一 (符号,尺寸) 组只提取一次局部几何，组内其余实例
        /// 仅记录各自的放置变换。
        ///
        /// 为什么按尺寸分组而非按符号：结构柱/梁、门/窗、止步块等族的高度/长度/宽度
        /// 由实例参数（底/顶标高、端点、宽度）驱动，同一符号的不同实例尺寸各异，
        /// Symbol.get_Geometry() 只返回族默认尺寸的"短桩"。故必须按"实际尺寸"分组，
        /// 并从真实实例几何逆变换回局部坐标，才能既去重又不失真。
        /// 返回 true 表示该元素已按实例处理（含无几何、记为跳过的情形）。
        /// </summary>
        private static bool TryExtractInstance(FamilyInstance instance, Options options, ExtractResult result,
            Dictionary<string, int> sharedMeshByKey, GltfExportContext context, GeometryDetailSettings settings)
        {
            if (instance == null || instance.Symbol == null
                || instance.Symbol.Family == null || instance.Symbol.Family.IsInPlace)
                return false;

            // 实例变换在此取一次，供"提取共享网格"与"记录实例矩阵"共用，保证两者严格一致。
            // 用 GetTotalTransform() 而非 GetOriginalGeometry()：后者返回的几何带族内偏移，
            // 与 GetTotalTransform() 叠加会把构件移出车站。
            Transform transform = instance.GetTotalTransform();
            if (transform == null || Math.Abs(transform.Determinant) < 1e-12)
                return false;   // 变换不可逆时退回唯一构件路径（世界坐标，不共享）

            // 点式实例（结构柱等）的 GetTotalTransform().Origin.Z 可能为 0（相对于标高），
            // 而真实几何由基标高+底偏移驱动。用基标高修正 Z，保证逆变换把几何还原回族原点；
            // 否则局部网格落在模型坐标，被矩阵缩放后柱子会落到车站下方。
            transform = CorrectPlacementZ(instance, transform);

            // 真实实例几何(模型坐标)。取一次，供尺寸签名与首次提取共用，避免重复 get_Geometry。
            GeometryElement instGeom = instance.get_Geometry(options);

            // 诊断：顶层 GeometryInstance 的变换是否与 GetTotalTransform 一致（决定逆变换是否可靠）。
            if (s_transformLogCount < 10 && instGeom != null)
            {
                s_transformLogCount++;
                double maxDelta = 0;
                foreach (GeometryObject obj in instGeom)
                {
                    var gi = obj as GeometryInstance;
                    if (gi == null) continue;
                    double d = (gi.Transform.Origin - transform.Origin).GetLength();
                    if (d > maxDelta) maxDelta = d;
                }
                context.Log(string.Format("变换一致性[{0}]: 顶层GeometryInstance与GetTotalTransform原点最大差 {1:0.000}m",
                    instance.Symbol.Name, maxDelta * FeetToMeter));
            }

            // 真实实例几何逆变换到族坐标后求包围盒——直接得到与旋转/镜像无关的族尺寸。
            // 注意不能用"模型轴向盒逆变换"：任意角度旋转下模型轴向盒会被撑大，无法还原。
            BoundingBoxXYZ famBox = ComputeFamilyBox(instGeom, transform.Inverse);
            if (famBox == null)
                return false;   // 无实心几何，退回唯一路径（随后记为跳过或唯一）

            string sig = BoxSignature(famBox);
            string key = instance.Symbol.Id.IntegerValue + "|" + sig;

            if (s_sigLogCount < 30)
            {
                s_sigLogCount++;
                context.Log(string.Format("尺寸签名[{0}]: box={1:0.000}×{2:0.000}×{3:0.000}m sig={4}",
                    instance.Symbol.Name,
                    FeetToMeter * (famBox.Max.X - famBox.Min.X),
                    FeetToMeter * (famBox.Max.Y - famBox.Min.Y),
                    FeetToMeter * (famBox.Max.Z - famBox.Min.Z),
                    sig));
            }

            int meshIndex;
            if (sharedMeshByKey.TryGetValue(key, out meshIndex))
            {
                if (meshIndex < 0)
                {
                    context.SkippedElementCount++;
                    return true;
                }
            }
            else
            {
                // 首次遇到该(符号,尺寸)组：提取真实尺寸几何，逆变换回局部坐标。
                var primitives = new Dictionary<string, RevitPrimitive>();
                ExtractFamilyGeometry(instGeom, transform.Inverse, instance, primitives, context, settings.TriangulateLevelOfDetail);

                var valid = primitives.Values.Where(p => p.VertexCount > 0).ToList();
                if (valid.Count == 0)
                {
                    sharedMeshByKey[key] = -1;
                    context.SkippedElementCount++;
                    return true;
                }

                var shared = new RevitSharedMesh
                {
                    SymbolId = instance.Symbol.Id.IntegerValue,
                    SymbolName = instance.Symbol.Name
                };
                shared.Primitives.AddRange(valid);
                meshIndex = result.SharedMeshes.Count;
                result.SharedMeshes.Add(shared);
                sharedMeshByKey[key] = meshIndex;
                foreach (RevitPrimitive prim in shared.Primitives)
                    context.TriangleCount += prim.Indices.Count / 3;
            }

            result.Instances.Add(new RevitInstance
            {
                Key = instance.UniqueId,
                Name = instance.Name,
                ElementId = instance.Id.IntegerValue,
                SharedMeshIndex = meshIndex,
                Matrix = ToGlMatrix(transform)
            });
            context.MeshElementCount++;
            return true;
        }

        /// <summary>提取唯一构件（系统族/就地族等）：世界坐标几何，一构件一网格，不参与实例化</summary>
        private static void ExtractUnique(Element element, Options options, ExtractResult result,
            GltfExportContext context, GeometryDetailSettings settings)
        {
            GeometryElement geometry = element.get_Geometry(options);
            if (geometry == null)
            {
                context.SkippedElementCount++;
                return;
            }

            var primitives = new Dictionary<string, RevitPrimitive>();
            ExtractGeometry(geometry, Transform.Identity, element, primitives, context, settings.TriangulateLevelOfDetail);

            var valid = primitives.Values.Where(p => p.VertexCount > 0).ToList();
            if (valid.Count == 0)
            {
                context.SkippedElementCount++;
                return;
            }

            var node = new RevitElementNode
            {
                Key = element.UniqueId,
                Name = element.Name,
                ElementId = element.Id.IntegerValue,
                ParentKey = null
            };
            node.Primitives.AddRange(valid);
            result.UniqueNodes.Add(node);
            context.MeshElementCount++;
            foreach (RevitPrimitive prim in node.Primitives)
                context.TriangleCount += prim.Indices.Count / 3;
        }

        /// <summary>
        /// Revit Transform → glTF 4x4 列主序矩阵。顶点已按英尺→米换算（AddVertex），
        /// 故 BasisX/Y/Z 只保留旋转/镜像（单位向量，不再乘 0.3048），仅平移分量乘以
        /// FeetToMeter。若基向量也乘 0.3048，会与顶点换算叠加成双重缩放（构件缩小 0.3048 倍）。
        /// 镜像实例行列式为负，由 doubleSided 材质与法线矩阵正确渲染。
        /// </summary>
        private static float[] ToGlMatrix(Transform t)
        {
            if (t == null) t = Transform.Identity;
            double s = FeetToMeter;
            return new float[]
            {
                (float)t.BasisX.X, (float)t.BasisX.Y, (float)t.BasisX.Z, 0f,
                (float)t.BasisY.X, (float)t.BasisY.Y, (float)t.BasisY.Z, 0f,
                (float)t.BasisZ.X, (float)t.BasisZ.Y, (float)t.BasisZ.Z, 0f,
                (float)(t.Origin.X * s), (float)(t.Origin.Y * s), (float)(t.Origin.Z * s), 1f
            };
        }

        /// <summary>
        /// 修正实例放置变换的 Z。结构柱等由标高托管的点式实例，GetTotalTransform().Origin.Z
        /// 可能返回 0（相对于标高的局部值），而真实几何位置由「基标高 + 底偏移」驱动。
        /// 用基标高修正 Z，使 transform.Inverse 能把几何正确还原回族原点。无基标高的线式
        /// 实例（梁、圈梁等）原样返回，不受影响。
        /// </summary>
        private static Transform CorrectPlacementZ(FamilyInstance instance, Transform transform)
        {
            try
            {
                Parameter bp = instance.get_Parameter(BuiltInParameter.FAMILY_BASE_LEVEL_PARAM);
                if (bp == null || bp.StorageType != StorageType.ElementId) return transform;
                Level baseLevel = instance.Document.GetElement(bp.AsElementId()) as Level;
                if (baseLevel == null) return transform;

                double baseOffset = 0;
                Parameter off = instance.get_Parameter(BuiltInParameter.FAMILY_BASE_LEVEL_OFFSET_PARAM);
                if (off != null && off.StorageType == StorageType.Double) baseOffset = off.AsDouble();

                double z = baseLevel.Elevation + baseOffset;
                if (Math.Abs(transform.Origin.Z - z) > 1e-6)
                {
                    transform.Origin = new XYZ(transform.Origin.X, transform.Origin.Y, z);
                }
            }
            catch { }
            return transform;
        }

        /// <summary>
        /// 递归计算 GeometryElement 的包围盒：遍历实心体(Solid)并展开 GeometryInstance。
        /// 比 GeometryElement.GetBoundingBox() 可靠——后者对只含 GeometryInstance 的几何
        /// 可能返回 null。返回 null 表示无实心几何。
        /// </summary>
        private static BoundingBoxXYZ ComputeBBox(GeometryElement geom)
        {
            if (geom == null) return null;
            BoundingBoxXYZ total = null;
            foreach (GeometryObject obj in geom)
            {
                var solid = obj as Solid;
                if (solid != null)
                {
                    if (solid.Volume <= 1e-9) continue;
                    BoundingBoxXYZ b = solid.GetBoundingBox();
                    if (b == null) continue;
                    total = MergeBBox(total, b);
                    continue;
                }

                var gi = obj as GeometryInstance;
                if (gi != null)
                {
                    // GetInstanceGeometry 已应用实例变换，返回模型(或宿主)坐标下的几何
                    GeometryElement g = gi.GetInstanceGeometry();
                    if (g != null)
                        total = MergeBBox(total, ComputeBBox(g));
                }
            }
            return total;
        }

        /// <summary>合并两个包围盒（允许任一为 null）。</summary>
        private static BoundingBoxXYZ MergeBBox(BoundingBoxXYZ a, BoundingBoxXYZ b)
        {
            if (b == null) return a;
            if (a == null) return b;
            a.Min = new XYZ(Math.Min(a.Min.X, b.Min.X), Math.Min(a.Min.Y, b.Min.Y), Math.Min(a.Min.Z, b.Min.Z));
            a.Max = new XYZ(Math.Max(a.Max.X, b.Max.X), Math.Max(a.Max.Y, b.Max.Y), Math.Max(a.Max.Z, b.Max.Z));
            return a;
        }

        /// <summary>
        /// 把族坐标包围盒量化为毫米整数签名（min+max 六值，1mm 容差），吸收逆变换的
        /// 浮点噪声。同一符号下签名相同 ⇒ 局部几何尺寸相同 ⇒ 可共用一份网格。
        /// </summary>
        private static string BoxSignature(BoundingBoxXYZ famBox)
        {
            return string.Format("{0}x{1}x{2}x{3}x{4}x{5}",
                (long)Math.Round(famBox.Min.X * FeetToMeter * 1000),
                (long)Math.Round(famBox.Min.Y * FeetToMeter * 1000),
                (long)Math.Round(famBox.Min.Z * FeetToMeter * 1000),
                (long)Math.Round(famBox.Max.X * FeetToMeter * 1000),
                (long)Math.Round(famBox.Max.Y * FeetToMeter * 1000),
                (long)Math.Round(famBox.Max.Z * FeetToMeter * 1000));
        }

        /// <summary>
        /// 把实例的真实几何(模型坐标)整体变换到族坐标后求包围盒，得到与旋转/镜像无关
        /// 的真实族尺寸。顶层 GeometryInstance 用 GetInstanceGeometry(toFamily) 直接取
        /// 族坐标几何；顶层若直接是 Solid 则逐盒变换。对比"模型轴向盒逆变换"：后者在
        /// 任意角度旋转下轴向盒被撑大，逆变换无法还原族尺寸。
        /// </summary>
        private static BoundingBoxXYZ ComputeFamilyBox(GeometryElement geometry, Transform toFamily)
        {
            if (geometry == null) return null;
            BoundingBoxXYZ total = null;
            foreach (GeometryObject obj in geometry)
            {
                var gi = obj as GeometryInstance;
                if (gi != null)
                {
                    GeometryElement g = gi.GetInstanceGeometry(toFamily);
                    if (g != null)
                        total = MergeBBox(total, ComputeBBox(g));
                    continue;
                }

                var solid = obj as Solid;
                if (solid != null)
                {
                    if (solid.Volume <= 1e-9) continue;
                    BoundingBoxXYZ b = solid.GetBoundingBox();
                    if (b == null) continue;
                    total = MergeBBox(total, TransformBBox(b, toFamily));
                }
            }
            return total;
        }

        /// <summary>把包围盒 8 角点经变换后求轴向包围盒。</summary>
        private static BoundingBoxXYZ TransformBBox(BoundingBoxXYZ b, Transform xform)
        {
            double minX = double.MaxValue, minY = double.MaxValue, minZ = double.MaxValue;
            double maxX = double.MinValue, maxY = double.MinValue, maxZ = double.MinValue;
            for (int i = 0; i < 8; i++)
            {
                double x = (i & 1) == 0 ? b.Min.X : b.Max.X;
                double y = (i & 2) == 0 ? b.Min.Y : b.Max.Y;
                double z = (i & 4) == 0 ? b.Min.Z : b.Max.Z;
                XYZ p = xform.OfPoint(new XYZ(x, y, z));
                if (p.X < minX) minX = p.X;
                if (p.Y < minY) minY = p.Y;
                if (p.Z < minZ) minZ = p.Z;
                if (p.X > maxX) maxX = p.X;
                if (p.Y > maxY) maxY = p.Y;
                if (p.Z > maxZ) maxZ = p.Z;
            }
            return new BoundingBoxXYZ { Min = new XYZ(minX, minY, minZ), Max = new XYZ(maxX, maxY, maxZ) };
        }

        /// <summary>
        /// 提取实例的真实几何并整体变换到局部坐标（toFamily = 实例变换的逆）。
        /// 与 ExtractGeometry 的区别：对嵌套 GeometryInstance 不把变换重置为 Identity，
        /// 而是把同一 toFamily 施加到 GetInstanceGeometry 的结果上——因为这里的输入是
        /// 模型坐标几何，需统一逆变换回局部坐标才能被多个实例的放置矩阵复用。
        /// </summary>
        private static void ExtractFamilyGeometry(GeometryElement geometry, Transform toFamily, Element element,
            Dictionary<string, RevitPrimitive> primitives, GltfExportContext context, double? triangulateLod)
        {
            foreach (GeometryObject obj in geometry)
            {
                var solid = obj as Solid;
                if (solid != null)
                {
                    if (solid.Volume > 1e-9 && solid.Faces.Size > 0)
                        ExtractSolid(solid, toFamily, element.Document, primitives, context, triangulateLod);
                    continue;
                }

                var inst = obj as GeometryInstance;
                if (inst != null)
                {
                    GeometryElement g = inst.GetInstanceGeometry();
                    if (g != null)
                        ExtractFamilyGeometry(g, toFamily, element, primitives, context, triangulateLod);
                    continue;
                }

                var mesh = obj as Mesh;
                if (mesh != null)
                    ExtractMesh(mesh, toFamily, element.Document, primitives, context);
            }
        }

        /// <summary>不实例化时的展开三角形总数（共享网格按其实例数重复计入），用于量化实例化节省。</summary>
        private static long ComputeExpandedTriangleCount(ExtractResult result)
        {
            long total = 0;
            foreach (RevitElementNode node in result.UniqueNodes)
                foreach (RevitPrimitive prim in node.Primitives)
                    total += prim.Indices.Count / 3;

            int meshCount = result.SharedMeshes.Count;
            var meshTriangles = new long[meshCount];
            var instancesPerMesh = new int[meshCount];
            for (int i = 0; i < meshCount; i++)
                foreach (RevitPrimitive prim in result.SharedMeshes[i].Primitives)
                    meshTriangles[i] += prim.Indices.Count / 3;
            foreach (RevitInstance inst in result.Instances)
                if (inst.SharedMeshIndex >= 0 && inst.SharedMeshIndex < meshCount)
                    instancesPerMesh[inst.SharedMeshIndex]++;
            for (int i = 0; i < meshCount; i++)
                total += meshTriangles[i] * instancesPerMesh[i];

            return total;
        }

        /// <summary>
        /// 释放各图元 List 的翻倍冗余容量。List&lt;T&gt; 增长时按 2 倍扩容，
        /// 容量可能达到实际长度的一半以上冗余；千万级三角形下这部分很可观，
        /// 在写出前收回能显著降低峰值内存。
        /// </summary>
        private static void TrimPrimitives(ExtractResult result, GltfExportContext context)
        {
            long before = GC.GetTotalMemory(false);
            foreach (RevitElementNode node in result.UniqueNodes)
                foreach (RevitPrimitive primitive in node.Primitives)
                    TrimPrimitive(primitive);
            foreach (RevitSharedMesh mesh in result.SharedMeshes)
                foreach (RevitPrimitive primitive in mesh.Primitives)
                    TrimPrimitive(primitive);
            long after = GC.GetTotalMemory(false);
            context.Log(string.Format("几何内存整理: {0:0.0}MB → {1:0.0}MB", before / 1048576.0, after / 1048576.0));
        }

        private static void TrimPrimitive(RevitPrimitive primitive)
        {
            primitive.Positions.TrimExcess();
            primitive.Normals.TrimExcess();
            primitive.Uvs.TrimExcess();
            primitive.Indices.TrimExcess();
        }

        /// <summary>
        /// 递归提取几何对象。
        /// GeometryInstance 使用 GetInstanceGeometry()（已应用实例变换、含嵌套），故不再累乘变换。
        /// </summary>
        private static void ExtractGeometry(GeometryElement geometry, Transform transform, Element element,
            Dictionary<string, RevitPrimitive> primitives, GltfExportContext context, double? triangulateLod)
        {
            foreach (GeometryObject obj in geometry)
            {
                var solid = obj as Solid;
                if (solid != null)
                {
                    if (solid.Volume > 1e-9 && solid.Faces.Size > 0)
                        ExtractSolid(solid, transform, element.Document, primitives, context, triangulateLod);
                    continue;
                }

                var instance = obj as GeometryInstance;
                if (instance != null)
                {
                    GeometryElement instanceGeometry = instance.GetInstanceGeometry();
                    if (instanceGeometry != null)
                        ExtractGeometry(instanceGeometry, Transform.Identity, element, primitives, context, triangulateLod);
                    continue;
                }

                var mesh = obj as Mesh;
                if (mesh != null)
                    ExtractMesh(mesh, transform, element.Document, primitives, context);
            }
        }

        /// <summary>按精度档三角化面；指定精度值对该面失败时回退默认三角化</summary>
        private static Mesh TriangulateFace(Face face, double? triangulateLod)
        {
            if (!triangulateLod.HasValue)
                return face.Triangulate();
            try
            {
                return face.Triangulate(triangulateLod.Value);
            }
            catch
            {
                // 个别面不接受该精度值时回退默认三角化，保证导出不中断
                return face.Triangulate();
            }
        }

        private static void ExtractSolid(Solid solid, Transform transform, Document doc,
            Dictionary<string, RevitPrimitive> primitives, GltfExportContext context, double? triangulateLod)
        {
            int faceIndex = 0;
            foreach (Face face in solid.Faces)
            {
                // 单个复杂构件可能有成千上万个面，按面粒度检查取消以保证响应
                if ((++faceIndex & 31) == 0)
                    context.ThrowIfCancelled();

                Material material = doc.GetElement(face.MaterialElementId) as Material;
                RevitPrimitive primitive = GetOrCreatePrimitive(primitives, material, doc, context);

                Mesh mesh = TriangulateFace(face, triangulateLod);
                if (mesh == null || mesh.NumTriangles == 0) continue;

                // 逐三角形投影到参数域并收集UV，统一求包围盒后归一化到[0,1]
                var triangles = new List<XYZ[]>();
                var triangleUvs = new List<UV[]>();
                double minU = double.MaxValue, maxU = double.MinValue;
                double minV = double.MaxValue, maxV = double.MinValue;

                for (int t = 0; t < mesh.NumTriangles; t++)
                {
                    var triangle = mesh.get_Triangle(t);
                    var points = new XYZ[3];
                    var uvs = new UV[3];
                    for (int j = 0; j < 3; j++)
                    {
                        XYZ point = transform.OfPoint(triangle.get_Vertex(j));
                        points[j] = point;
                        UV uv = ProjectUV(face, point);
                        uvs[j] = uv;
                        if (uv.U < minU) minU = uv.U;
                        if (uv.U > maxU) maxU = uv.U;
                        if (uv.V < minV) minV = uv.V;
                        if (uv.V > maxV) maxV = uv.V;
                    }
                    triangles.Add(points);
                    triangleUvs.Add(uvs);
                }

                double rangeU = maxU - minU;
                double rangeV = maxV - minV;

                // 顶点焊接：同一面内相邻三角形共用边顶点，按量化位置去重，
                // 把非索引展开的 3× 顶点冗余（2452 万三角形 → 7356 万顶点的主因）压回共享顶点。
                // 仅按位置去重即可——同一面内法线一致、UV 按面统一归一化，共享点三者均一致；
                // 跨面不合并（各面 UV 归一化与法线不同），天然保留硬边与纹理接缝。
                var weld = new Dictionary<(int, int, int), uint>();

                for (int t = 0; t < triangles.Count; t++)
                {
                    XYZ p0 = triangles[t][0], p1 = triangles[t][1], p2 = triangles[t][2];
                    XYZ normal = (p1 - p0).CrossProduct(p2 - p0);
                    normal = normal.GetLength() > 1e-12 ? normal.Normalize() : XYZ.BasisZ;

                    for (int j = 0; j < 3; j++)
                    {
                        UV uv = triangleUvs[t][j];
                        var normalized = new UV(
                            Math.Abs(rangeU) > 1e-12 ? (uv.U - minU) / rangeU : 0,
                            Math.Abs(rangeV) > 1e-12 ? (uv.V - minV) / rangeV : 0);
                        primitive.Indices.Add(GetOrAddVertex(primitive, weld, triangles[t][j], normal, normalized));
                    }
                }
            }
        }

        /// <summary>提取自由网格（非Solid的Mesh几何），UV按三角形三点铺满[0,1]</summary>
        private static void ExtractMesh(Mesh mesh, Transform transform, Document doc,
            Dictionary<string, RevitPrimitive> primitives, GltfExportContext context)
        {
            RevitPrimitive primitive = GetOrCreatePrimitive(primitives, null, doc, context);
            for (int t = 0; t < mesh.NumTriangles; t++)
            {
                var triangle = mesh.get_Triangle(t);
                XYZ p0 = transform.OfPoint(triangle.get_Vertex(0));
                XYZ p1 = transform.OfPoint(triangle.get_Vertex(1));
                XYZ p2 = transform.OfPoint(triangle.get_Vertex(2));
                XYZ normal = (p1 - p0).CrossProduct(p2 - p0);
                normal = normal.GetLength() > 1e-12 ? normal.Normalize() : XYZ.BasisZ;

                uint baseIndex = (uint)(primitive.Positions.Count / 3);
                AddVertex(primitive, p0, normal, new UV(0, 0));
                AddVertex(primitive, p1, normal, new UV(1, 0));
                AddVertex(primitive, p2, normal, new UV(0, 1));
                primitive.Indices.Add(baseIndex);
                primitive.Indices.Add(baseIndex + 1);
                primitive.Indices.Add(baseIndex + 2);
            }
        }

        /// <summary>将点投影到面的参数域得到UV（Revit的Face.Project返回IntersectionResult）</summary>
        private static UV ProjectUV(Face face, XYZ point)
        {
            try
            {
                var result = face.Project(point);
                if (result != null && result.UVPoint != null)
                    return result.UVPoint;
            }
            catch
            {
                // 投影失败（点不在面上等）时退化为(0,0)
            }
            return new UV(0, 0);
        }

        /// <summary>顶点写入并按英尺→米换算</summary>
        private static void AddVertex(RevitPrimitive primitive, XYZ point, XYZ normal, UV uv)
        {
            primitive.Positions.Add((float)(point.X * FeetToMeter));
            primitive.Positions.Add((float)(point.Y * FeetToMeter));
            primitive.Positions.Add((float)(point.Z * FeetToMeter));
            primitive.Normals.Add((float)normal.X);
            primitive.Normals.Add((float)normal.Y);
            primitive.Normals.Add((float)normal.Z);
            primitive.Uvs.Add((float)uv.U);
            primitive.Uvs.Add((float)uv.V);
        }

        /// <summary>
        /// 按量化位置在本面焊接字典内查找或写入顶点，返回顶点索引。
        /// 字典按面新建、逐面丢弃，故不会跨面合并——共享点只在同一面内复用其首次写下的法线与UV。
        /// 曲线面的UV接缝（同一位置对应两个UV）会取其一，属可接受近似，不影响几何正确性。
        /// </summary>
        private static uint GetOrAddVertex(RevitPrimitive primitive, Dictionary<(int, int, int), uint> weld,
            XYZ point, XYZ normal, UV uv)
        {
            var key = (
                (int)Math.Round(point.X * FeetToMeter * WeldScale),
                (int)Math.Round(point.Y * FeetToMeter * WeldScale),
                (int)Math.Round(point.Z * FeetToMeter * WeldScale));

            if (weld.TryGetValue(key, out uint index))
                return index;

            index = (uint)(primitive.Positions.Count / 3);
            AddVertex(primitive, point, normal, uv);
            weld[key] = index;
            return index;
        }

        /// <summary>按材质ID取得(或创建)图元分组，材质信息由MaterialExtractor填充</summary>
        private static RevitPrimitive GetOrCreatePrimitive(Dictionary<string, RevitPrimitive> primitives,
            Material material, Document doc, GltfExportContext context)
        {
            int materialId = material != null ? material.Id.IntegerValue : -1;
            string key = materialId.ToString();
            RevitPrimitive primitive;
            if (!primitives.TryGetValue(key, out primitive))
            {
                primitive = new RevitPrimitive { MaterialId = materialId };
                MaterialExtractor.Apply(primitive, material, doc, context);
                primitives[key] = primitive;
            }
            return primitive;
        }
    }

    /// <summary>
    /// 几何精度档位：DetailLevel 控制 get_Geometry 返回哪些面，TriangulateLevelOfDetail 控制三角化密度(null=沿用无参默认)。
    /// </summary>
    public class GeometryDetailSettings
    {
        /// <summary>档位名（用于日志标识，如 "model" / "lod_fine_0.20"）</summary>
        public string TierName { get; }

        /// <summary>视图详细程度（Coarse/Medium/Fine）</summary>
        public ViewDetailLevel DetailLevel { get; }

        /// <summary>三角化精度 0~1（null=无参 Triangulate()）</summary>
        public double? TriangulateLevelOfDetail { get; }

        /// <summary>每处理 N 个元素打一条进度日志，0=不打</summary>
        public int LogProgressEvery { get; set; }

        /// <summary>是否启用族实例化（默认true；LOD测试需关闭以测量原始三角化量）</summary>
        public bool EnableInstancing { get; set; } = true;

        public GeometryDetailSettings(string tierName, ViewDetailLevel detailLevel, double? triangulateLevelOfDetail)
        {
            TierName = tierName;
            DetailLevel = detailLevel;
            TriangulateLevelOfDetail = triangulateLevelOfDetail;
        }
    }
}

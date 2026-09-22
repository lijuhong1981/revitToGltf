using System;
using System.Collections.Generic;
using System.Globalization;
using Autodesk.Revit.DB;
using RevitToGltf.Models;
using RevitToGltf.Pipeline;

namespace RevitToGltf.Extraction
{
    /// <summary>
    /// 元数据采集器：从 Revit 元素读取 BIM 语义信息（类别/族/类型/标高/实例参数），
    /// 组装成 ElementMetadata。Key 用 UniqueId，与 glTF 节点名对齐。
    /// 采集失败时降级为身份字段（key/name/elementId），不中断导出。
    /// </summary>
    public static class MetadataCollector
    {
        /// <summary>诊断日志计数（限制输出，避免刷屏）</summary>
        private static int s_logCount = 0;

        /// <summary>采集唯一构件（系统族/就地族等）的元数据</summary>
        public static ElementMetadata FromElement(Element element, GltfExportContext context)
        {
            var entry = new ElementMetadata
            {
                Key = element.UniqueId,
                ElementId = element.Id.IntegerValue,
                Name = element.Name
            };
            FillCommon(element, entry, context);
            return entry;
        }

        /// <summary>采集族实例的元数据（含放置矩阵，与 glTF nodes[].matrix 同值）</summary>
        public static ElementMetadata FromInstance(FamilyInstance instance, float[] glMatrix, GltfExportContext context)
        {
            var entry = new ElementMetadata
            {
                Key = instance.UniqueId,
                ElementId = instance.Id.IntegerValue,
                Name = instance.Name,
                IsInstance = true,
                Matrix = glMatrix
            };
            FillCommon(instance, entry, context);
            return entry;
        }

        /// <summary>填充通用字段：类别、族/类型、标高、实例参数。任何一步失败都不影响身份字段。</summary>
        private static void FillCommon(Element element, ElementMetadata entry, GltfExportContext context)
        {
            try
            {
                // 类别
                if (element.Category != null)
                    entry.Category = element.Category.Name;

                // 族 / 类型：族实例走 Symbol；系统族走 GetTypeId 的 ElementType
                var fi = element as FamilyInstance;
                if (fi != null && fi.Symbol != null)
                {
                    if (fi.Symbol.Family != null)
                        entry.Family = fi.Symbol.Family.Name;
                    entry.TypeName = fi.Symbol.Name;
                }
                else
                {
                    ElementId typeId = element.GetTypeId();
                    var type = typeId != null && typeId != ElementId.InvalidElementId
                        ? element.Document.GetElement(typeId) as ElementType
                        : null;
                    if (type != null)
                    {
                        entry.Family = type.FamilyName;
                        entry.TypeName = type.Name;
                    }
                }

                // 标高
                entry.Level = GetLevelName(element);

                // 实例参数：全量收集（值转字符串，无值跳过）
                var parameters = new Dictionary<string, string>();
                foreach (Parameter p in element.Parameters)
                {
                    string value = ParameterToString(p, element.Document);
                    if (value == null || p.Definition == null) continue;
                    parameters[p.Definition.Name] = value;
                }
                if (parameters.Count > 0)
                    entry.Parameters = parameters;
            }
            catch (Exception ex)
            {
                if (s_logCount++ < 10)
                    context.Log(string.Format("元数据采集降级[{0}]: {1}", entry.Name ?? "(未命名)", ex.Message));
            }
        }

        /// <summary>取标高名：通用 LevelId；族实例再回退基标高参数。取不到返回 null。</summary>
        private static string GetLevelName(Element element)
        {
            ElementId levelId = element.LevelId;
            Level level = levelId != null && levelId != ElementId.InvalidElementId
                ? element.Document.GetElement(levelId) as Level
                : null;
            if (level != null)
                return level.Name;

            var fi = element as FamilyInstance;
            if (fi != null)
            {
                Parameter bp = fi.get_Parameter(BuiltInParameter.FAMILY_BASE_LEVEL_PARAM);
                if (bp != null && bp.StorageType == StorageType.ElementId)
                {
                    Level baseLevel = element.Document.GetElement(bp.AsElementId()) as Level;
                    if (baseLevel != null)
                        return baseLevel.Name;
                }
            }
            return null;
        }

        /// <summary>参数 → 显示字符串；无值/读取失败返回 null</summary>
        private static string ParameterToString(Parameter p, Document doc)
        {
            if (p == null || p.Definition == null)
                return null;
            try
            {
                switch (p.StorageType)
                {
                    case StorageType.String:
                        return p.AsString();
                    case StorageType.Integer:
                        {
                            string s = p.AsValueString();
                            return string.IsNullOrEmpty(s) ? p.AsInteger().ToString() : s;
                        }
                    case StorageType.Double:
                        {
                            string s = p.AsValueString();
                            return string.IsNullOrEmpty(s)
                                ? p.AsDouble().ToString(CultureInfo.InvariantCulture)
                                : s;
                        }
                    case StorageType.ElementId:
                        {
                            ElementId id = p.AsElementId();
                            if (id == null || id == ElementId.InvalidElementId)
                                return null;
                            Element e = doc.GetElement(id);
                            return e != null ? e.Name : id.IntegerValue.ToString();
                        }
                    default:
                        return null;
                }
            }
            catch
            {
                return null;
            }
        }
    }
}

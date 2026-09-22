using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using Newtonsoft.Json;
using RevitToGltf.Extraction;
using RevitToGltf.Models;
using RevitToGltf.Pipeline;

namespace RevitToGltf.Output
{
    /// <summary>
    /// .metadata 写出器：把采集到的构件元数据序列化为缩进 JSON。
    /// 独立于 GltfWriter——元数据是旁路产物，写出失败不应影响 glTF 主产物。
    /// </summary>
    public static class MetadataWriter
    {
        /// <summary>
        /// 写出 .metadata。
        /// @return 写出的构件条数；异常向上抛，由调用方决定降级方式。
        /// </summary>
        public static int Export(string metadataPath, ExtractResult result, GltfExportContext context,
            Document doc, string scopeName, ViewDetailLevel detailLevel, double? triangulateLod, bool binary)
        {
            var elements = result.Metadata.OrderBy(m => m.ElementId).ToList();

            var document = new MetadataDocument
            {
                Schema = "revitToGltf-metadata",
                Version = 1,
                Generator = "revitToGltf",
                ExportedAt = DateTime.Now.ToString("o"),
                Source = new SourceInfo { Title = doc.Title, PathName = doc.PathName },
                ExportSettings = new ExportSettings
                {
                    Scope = scopeName,
                    DetailLevel = detailLevel.ToString(),
                    Triangulate = triangulateLod.HasValue ? (double?)Math.Round(triangulateLod.Value, 2) : null,
                    Format = binary ? "glb" : "gltf"
                },
                CoordinateSystem = new CoordinateSystem { UpAxis = "Z", Unit = "meters" },
                Counts = new Counts
                {
                    Elements = elements.Count,
                    UniqueNodes = result.UniqueNodes.Count,
                    Instances = result.Instances.Count,
                    SharedMeshes = result.SharedMeshes.Count,
                    Triangles = context.TriangleCount,
                    Vertices = context.VertexCount
                },
                Levels = CollectLevels(doc),
                Elements = elements
            };

            string json = JsonConvert.SerializeObject(document, Formatting.Indented,
                new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore });
            File.WriteAllText(metadataPath, json);
            return elements.Count;
        }

        /// <summary>项目标高表（名称 + 米制高程，按高程升序）</summary>
        private static List<LevelInfo> CollectLevels(Document doc)
        {
            try
            {
                return new FilteredElementCollector(doc)
                    .OfClass(typeof(Level))
                    .Cast<Level>()
                    .OrderBy(l => l.Elevation)
                    .Select(l => new LevelInfo
                    {
                        Name = l.Name,
                        Elevation = Math.Round(l.Elevation * GeometryExtractor.FeetToMeter, 3)
                    })
                    .ToList();
            }
            catch
            {
                return new List<LevelInfo>();
            }
        }

        private class MetadataDocument
        {
            public string Schema { get; set; }
            public int Version { get; set; }
            public string Generator { get; set; }
            public string ExportedAt { get; set; }
            public SourceInfo Source { get; set; }
            public ExportSettings ExportSettings { get; set; }
            public CoordinateSystem CoordinateSystem { get; set; }
            public Counts Counts { get; set; }
            public List<LevelInfo> Levels { get; set; }
            public List<ElementMetadata> Elements { get; set; }
        }

        private class SourceInfo
        {
            public string Title { get; set; }
            public string PathName { get; set; }
        }

        private class ExportSettings
        {
            public string Scope { get; set; }
            public string DetailLevel { get; set; }
            public double? Triangulate { get; set; }
            public string Format { get; set; }
        }

        private class CoordinateSystem
        {
            public string UpAxis { get; set; }
            public string Unit { get; set; }
        }

        private class Counts
        {
            public int Elements { get; set; }
            public int UniqueNodes { get; set; }
            public int Instances { get; set; }
            public int SharedMeshes { get; set; }
            public long Triangles { get; set; }
            public long Vertices { get; set; }
        }

        private class LevelInfo
        {
            public string Name { get; set; }
            public double Elevation { get; set; }
        }
    }
}

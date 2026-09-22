using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RevitToGltf.Models;
using RevitToGltf.Pipeline;

namespace RevitToGltf.Output
{
    /// <summary>
    /// glTF 2.0 写出器（外部 .bin + 外部贴图）。
    /// 顶点为世界坐标（已含嵌套族变换），故节点不写变换；
    /// 使用uint32索引规避65535顶点上限；含NORMAL/TEXCOORD_0与贴图引用。
    /// 节点 name 保持 Revit UniqueId，构件名与元素ID写入 extras 供查看器展示。
    ///
    /// 内存策略：.bin 直接流式写入文件而非先攒在 MemoryStream 里 —— 千万级三角形时
    /// MemoryStream 会与提取结果叠加一份完整副本，是 OutOfMemoryException 的主因。
    /// 每个图元写完后立刻释放其顶点数组，让占用随写出过程递减。
    /// 偏移量/长度一律用 long：.bin 可能超过 int.MaxValue（2GB）。
    /// </summary>
    public static class GltfWriter
    {
        private const int ComponentTypeFloat = 5126;
        private const int ComponentTypeUnsignedShort = 5123;
        private const int ComponentTypeUnsignedInt = 5125;
        private const int TargetArrayBuffer = 34962;
        private const int TargetElementArrayBuffer = 34963;
        private const int FileBufferSize = 1 << 20;

        public static void Export(string gltfPath, ExtractResult result, GltfExportContext context, bool asGlb)
        {
            // .glb 的中间二进制写临时文件：.glb 完成后会删除它，若与同名 .gltf 共用一个 .bin
            // 会误删 .gltf 需要的 .bin（先导 .gltf 再导同名 .glb 时，.gltf 就缺 .bin 了）。
            string binPath = asGlb
                ? Path.Combine(Path.GetTempPath(), "revitToGltf-" + Guid.NewGuid().ToString("N") + ".bin")
                : Path.ChangeExtension(gltfPath, ".bin");

            var bufferViews = new JArray();
            var accessors = new JArray();
            var meshes = new JArray();
            var materials = new JArray();
            var gltfNodes = new JArray();
            var images = new JArray();
            var textures = new JArray();
            var samplers = new JArray();

            var materialIndexMap = new Dictionary<int, int>();
            var textureIndexMap = new Dictionary<string, int>();

            // 采样器：UV 为真实世界平铺坐标（常超出 [0,1]），用 REPEAT 让贴图按真实尺寸循环重复；
            // 若用 CLAMP_TO_EDGE 会把超出部分钳成边缘像素，导致整张贴图拉伸成单张、平铺失效。
            samplers.Add(new JObject
            {
                ["magFilter"] = 9729,   // LINEAR
                ["minFilter"] = 9987,   // LINEAR_MIPMAP_LINEAR
                ["wrapS"] = 10497,      // REPEAT
                ["wrapT"] = 10497
            });

            long binaryLength;
            long vertexTotal = 0;

            try
            {
                using (var binary = new FileStream(binPath, FileMode.Create, FileAccess.Write,
                           FileShare.None, FileBufferSize))
                {
                    int totalMeshes = result.SharedMeshes.Count + result.UniqueNodes.Count;
                    int writtenMeshes = 0;
                    context.ReportNow(0, string.Format("写出 glTF 0/{0}", totalMeshes));

                    // 1. 共享网格：每个族符号写一次
                    var sharedMeshIndex = new int[result.SharedMeshes.Count];
                    for (int i = 0; i < result.SharedMeshes.Count; i++)
                    {
                        context.ThrowIfCancelled();
                        sharedMeshIndex[i] = WriteMesh(binary, result.SharedMeshes[i].Primitives,
                            bufferViews, accessors, meshes, materials, images, textures,
                            materialIndexMap, textureIndexMap, ref vertexTotal);
                        writtenMeshes++;
                        context.Report(writtenMeshes * 100 / Math.Max(1, totalMeshes),
                            string.Format("写出 glTF {0}/{1}", writtenMeshes, totalMeshes));
                    }

                    // 2. 唯一构件：一构件一网格（世界坐标）
                    foreach (RevitElementNode node in result.UniqueNodes)
                    {
                        context.ThrowIfCancelled();
                        int meshIndex = WriteMesh(binary, node.Primitives,
                            bufferViews, accessors, meshes, materials, images, textures,
                            materialIndexMap, textureIndexMap, ref vertexTotal);
                        gltfNodes.Add(new JObject
                        {
                            // 节点名 = Revit UniqueId，作为稳定的匹配键
                            ["name"] = node.Key,
                            ["mesh"] = meshIndex,
                            ["extras"] = BuildExtras(node.ElementId, node.Name)
                        });
                        writtenMeshes++;
                        context.Report(writtenMeshes * 100 / Math.Max(1, totalMeshes),
                            string.Format("写出 glTF {0}/{1}", writtenMeshes, totalMeshes));
                    }

                    // 3. 实例节点：引用共享网格 + 放置矩阵
                    foreach (RevitInstance inst in result.Instances)
                    {
                        gltfNodes.Add(new JObject
                        {
                            ["name"] = inst.Key,
                            ["mesh"] = sharedMeshIndex[inst.SharedMeshIndex],
                            ["matrix"] = new JArray(inst.Matrix),
                            ["extras"] = BuildExtras(inst.ElementId, inst.Name)
                        });
                    }

                    binary.Flush();
                    binaryLength = binary.Length;
                }
            }
            catch
            {
                // 半成品 .bin 直接删除，避免残留一个看似完整的文件
                TryDelete(binPath);
                throw;
            }

            context.VertexCount = vertexTotal;

            // Z-up → Y-up：glTF 2.0 规定 +Y 朝上，Revit 为 Z-up，故新增一个根节点做 -90°(绕X轴) 旋转，
            // 所有构件节点挂其下。顶点与实例矩阵仍保持原 Z-up 世界坐标不变，由根节点统一翻转。
            // 旋转矩阵为 proper rotation(det=+1)，法线与三角形绕向无需额外处理。
            var rootNode = new JObject
            {
                ["name"] = "RevitToGltf_Root_YUp",
                ["matrix"] = new JArray(new double[]
                {
                    1, 0,  0, 0,   // 列0：原 +X → 新 +X
                    0, 0, -1, 0,   // 列1：原 +Y → 新 -Z
                    0, 1,  0, 0,   // 列2：原 +Z → 新 +Y
                    0, 0,  0, 1
                }),
                ["children"] = new JArray(Enumerable.Range(1, gltfNodes.Count))
            };

            var nodes = new JArray();
            nodes.Add(rootNode);
            foreach (JToken node in gltfNodes) nodes.Add(node);

            // 场景根只挂 Y-up 根节点，其余节点作为其子节点（glTF 保持扁平层级）
            var gltf = new JObject
            {
                ["asset"] = new JObject
                {
                    ["version"] = "2.0",
                    ["generator"] = "revitToGltf"
                },
                ["scene"] = 0,
                ["scenes"] = new JArray { new JObject { ["nodes"] = new JArray { 0 } } },
                ["nodes"] = nodes,
                ["meshes"] = meshes,
                ["materials"] = materials,
                ["buffers"] = new JArray { BuildBuffer(gltfPath, binaryLength, asGlb) },
                ["bufferViews"] = bufferViews,
                ["accessors"] = accessors
            };

            if (images.Count > 0)
            {
                gltf["images"] = images;
                gltf["textures"] = textures;
                gltf["samplers"] = samplers;
            }

            try
            {
                if (asGlb)
                {
                    WriteGlb(gltfPath, binPath, gltf, binaryLength);
                    TryDelete(binPath);   // .bin 已内嵌进 .glb，删除临时文件
                }
                else
                {
                    File.WriteAllText(gltfPath, gltf.ToString(Formatting.Indented));
                }
            }
            catch
            {
                TryDelete(binPath);
                throw;
            }

            context.Log(string.Format("glTF写出完成: 节点 {0} 个, 网格 {1} 个, 材质 {2} 个, 贴图 {3} 个, 顶点 {4:N0} 个, 二进制 {5:0.0}MB",
                gltfNodes.Count, meshes.Count, materials.Count, images.Count, vertexTotal, binaryLength / 1048576.0));
            context.Log(string.Format("材质贴图诊断: 处理材质 {0} 个, 含渲染外观资产 {1} 个, 含位图贴图 {2} 个",
                context.MaterialCount, context.AppearanceAssetCount, context.BitmapTextureCount));
        }

        /// <summary>写一个网格（一组图元）的二进制数据与mesh定义，返回mesh索引。顶点数据写完即释放。</summary>
        private static int WriteMesh(Stream binary, List<RevitPrimitive> primitives,
            JArray bufferViews, JArray accessors, JArray meshes, JArray materials,
            JArray images, JArray textures, Dictionary<int, int> materialIndexMap,
            Dictionary<string, int> textureIndexMap, ref long vertexTotal)
        {
            var primitivesJson = new JArray();
            foreach (RevitPrimitive primitive in primitives)
            {
                if (primitive.VertexCount == 0) continue;

                int materialIndex = GetMaterialIndex(primitive, materials, materialIndexMap, textures, images, textureIndexMap, binary, bufferViews);

                // 顶点数据：位置 / 法线 / UV / 索引，各bufferView按4字节对齐
                int positionAccessor = WriteVec3Accessor(binary, bufferViews, accessors, primitive.Positions, true);
                int normalAccessor = WriteVec3Accessor(binary, bufferViews, accessors, primitive.Normals, false);
                int uvAccessor = -1;
                if (primitive.Uvs.Count == primitive.VertexCount * 2)
                    uvAccessor = WriteVec2Accessor(binary, bufferViews, accessors, primitive.Uvs);
                int indexAccessor = WriteIndexAccessor(binary, bufferViews, accessors, primitive.Indices);

                vertexTotal += primitive.VertexCount;

                var attributes = new JObject { ["POSITION"] = positionAccessor };
                if (normalAccessor >= 0) attributes["NORMAL"] = normalAccessor;
                if (uvAccessor >= 0) attributes["TEXCOORD_0"] = uvAccessor;

                primitivesJson.Add(new JObject
                {
                    ["attributes"] = attributes,
                    ["indices"] = indexAccessor,
                    ["material"] = materialIndex,
                    ["mode"] = 4  // TRIANGLES
                });

                // 顶点数据已落盘，立即释放；否则整个写出阶段都维持峰值内存
                ReleasePrimitive(primitive);
            }

            meshes.Add(new JObject { ["primitives"] = primitivesJson });
            return meshes.Count - 1;
        }

        /// <summary>节点 extras：元素ID + 构件名（供查看器展示）</summary>
        private static JObject BuildExtras(int elementId, string name)
        {
            var extras = new JObject { ["elementId"] = elementId };
            if (!string.IsNullOrEmpty(name))
                extras["name"] = name;
            return extras;
        }

        /// <summary>把已写入 .bin 的顶点数据从内存中放掉（换成空表，原数组交给 GC）</summary>
        private static void ReleasePrimitive(RevitPrimitive primitive)
        {
            primitive.Positions = new List<float>();
            primitive.Normals = new List<float>();
            primitive.Uvs = new List<float>();
            primitive.Indices = new List<uint>();
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path)) File.Delete(path);
            }
            catch
            {
                // 删除失败不影响原始异常向上抛出
            }
        }

        /// <summary>buffer 定义：.glb 内嵌（无 uri），.gltf 引用外部 .bin</summary>
        private static JObject BuildBuffer(string gltfPath, long byteLength, bool asGlb)
        {
            var buffer = new JObject { ["byteLength"] = byteLength };
            if (!asGlb)
                buffer["uri"] = Path.GetFileNameWithoutExtension(gltfPath) + ".bin";
            return buffer;
        }

        /// <summary>
        /// 写出 .glb（二进制容器）：12 字节头 + JSON chunk + BIN chunk。
        /// 几何先流式写入临时 .bin，再整体拷入 .glb 的 BIN chunk 后删除临时文件 ——
        /// 既保留几何阶段的内存策略，又得到单文件产物。
        /// </summary>
        private static void WriteGlb(string glbPath, string binPath, JObject gltf, long binLength)
        {
            byte[] jsonBytes = Encoding.UTF8.GetBytes(gltf.ToString(Formatting.None));
            int jsonPad = (4 - jsonBytes.Length % 4) % 4;
            uint jsonChunkLen = (uint)(jsonBytes.Length + jsonPad);

            int binPad = (int)((4 - binLength % 4) % 4);
            uint binChunkLen = (uint)(binLength + binPad);

            uint totalLength = 12u + 8u + jsonChunkLen + 8u + binChunkLen;

            using (var fs = new FileStream(glbPath, FileMode.Create, FileAccess.Write, FileShare.None, FileBufferSize))
            {
                WriteUInt32(fs, 0x46546C67u);   // "glTF"
                WriteUInt32(fs, 2u);            // 版本
                WriteUInt32(fs, totalLength);   // 文件总长

                WriteUInt32(fs, jsonChunkLen);
                WriteUInt32(fs, 0x4E4F534Au);   // "JSON"
                fs.Write(jsonBytes, 0, jsonBytes.Length);
                for (int i = 0; i < jsonPad; i++) fs.WriteByte(0x20);   // 空格补齐 4 字节

                WriteUInt32(fs, binChunkLen);
                WriteUInt32(fs, 0x004E4942u);   // "BIN\0"
                using (var bin = new FileStream(binPath, FileMode.Open, FileAccess.Read, FileShare.Read, FileBufferSize))
                    bin.CopyTo(fs);
                for (int i = 0; i < binPad; i++) fs.WriteByte(0x00);    // 零补齐 4 字节
            }
        }

        /// <summary>材质去重并生成glTF材质定义（含baseColorTexture引用；贴图可为外置URI或内嵌bufferView）</summary>
        private static int GetMaterialIndex(RevitPrimitive primitive, JArray materials,
            Dictionary<int, int> materialIndexMap, JArray textures, JArray images,
            Dictionary<string, int> textureIndexMap, Stream binary, JArray bufferViews)
        {
            int index;
            if (materialIndexMap.TryGetValue(primitive.MaterialId, out index))
                return index;

            var pbr = new JObject
            {
                ["baseColorFactor"] = new JArray(primitive.BaseColor),
                ["metallicFactor"] = 0.0,
                ["roughnessFactor"] = 1.0
            };

            if (!string.IsNullOrEmpty(primitive.TextureUri))
            {
                // 分离模式：贴图为外部文件（textures/xxx.png）
                int textureIndex;
                if (!textureIndexMap.TryGetValue(primitive.TextureUri, out textureIndex))
                {
                    images.Add(new JObject { ["uri"] = primitive.TextureUri });
                    textures.Add(new JObject { ["source"] = images.Count - 1, ["sampler"] = 0 });
                    textureIndex = textures.Count - 1;
                    textureIndexMap[primitive.TextureUri] = textureIndex;
                }
                pbr["baseColorTexture"] = new JObject { ["index"] = textureIndex };
            }
            else if (!string.IsNullOrEmpty(primitive.TextureSourcePath))
            {
                // 内嵌模式：读字节写进 .bin 缓冲，image 用 bufferView + mimeType 引用（.glb/.gltf 通用）
                int textureIndex;
                if (!textureIndexMap.TryGetValue(primitive.TextureSourcePath, out textureIndex))
                {
                    byte[] bytes = File.ReadAllBytes(primitive.TextureSourcePath);
                    long byteOffset = AlignTo4(binary);
                    binary.Write(bytes, 0, bytes.Length);
                    bufferViews.Add(new JObject
                    {
                        ["buffer"] = 0,
                        ["byteOffset"] = byteOffset,
                        ["byteLength"] = (long)bytes.Length
                    });
                    images.Add(new JObject
                    {
                        ["bufferView"] = bufferViews.Count - 1,
                        ["mimeType"] = GetImageMimeType(primitive.TextureSourcePath)
                    });
                    textures.Add(new JObject { ["source"] = images.Count - 1, ["sampler"] = 0 });
                    textureIndex = textures.Count - 1;
                    textureIndexMap[primitive.TextureSourcePath] = textureIndex;
                }
                pbr["baseColorTexture"] = new JObject { ["index"] = textureIndex };
            }

            var material = new JObject
            {
                ["name"] = primitive.MaterialName ?? ("Material_" + primitive.MaterialId),
                ["pbrMetallicRoughness"] = pbr,
                ["doubleSided"] = primitive.DoubleSided
            };
            if (primitive.BaseColor.Length > 3 && primitive.BaseColor[3] < 0.999f)
                material["alphaMode"] = "BLEND";

            materials.Add(material);
            index = materials.Count - 1;
            materialIndexMap[primitive.MaterialId] = index;
            return index;
        }

        /// <summary>根据扩展名返回 glTF 支持的图片 MIME（内嵌贴图用）</summary>
        private static string GetImageMimeType(string path)
        {
            switch (Path.GetExtension(path).ToLowerInvariant())
            {
                case ".jpg":
                case ".jpeg":
                    return "image/jpeg";
                default:
                    return "image/png";
            }
        }

        /// <summary>写入VEC3数据(位置或法线)，返回accessor索引；数据为空返回-1</summary>
        private static int WriteVec3Accessor(Stream binary, JArray bufferViews, JArray accessors,
            List<float> values, bool computeBounds)
        {
            if (values.Count == 0) return -1;
            int count = values.Count / 3;
            long byteOffset = AlignTo4(binary);
            foreach (float value in values)
                WriteFloat(binary, value);

            bufferViews.Add(new JObject
            {
                ["buffer"] = 0,
                ["byteOffset"] = byteOffset,
                ["byteLength"] = (long)count * 12,
                ["target"] = TargetArrayBuffer
            });

            var accessor = new JObject
            {
                ["bufferView"] = bufferViews.Count - 1,
                ["componentType"] = ComponentTypeFloat,
                ["count"] = count,
                ["type"] = "VEC3"
            };
            if (computeBounds)
            {
                var min = new float[] { float.MaxValue, float.MaxValue, float.MaxValue };
                var max = new float[] { float.MinValue, float.MinValue, float.MinValue };
                for (int i = 0; i < values.Count; i += 3)
                {
                    for (int axis = 0; axis < 3; axis++)
                    {
                        min[axis] = Math.Min(min[axis], values[i + axis]);
                        max[axis] = Math.Max(max[axis], values[i + axis]);
                    }
                }
                accessor["min"] = new JArray(min.Select(v => (object)v));
                accessor["max"] = new JArray(max.Select(v => (object)v));
            }
            accessors.Add(accessor);
            return accessors.Count - 1;
        }

        /// <summary>写入VEC2数据(UV)</summary>
        private static int WriteVec2Accessor(Stream binary, JArray bufferViews, JArray accessors,
            List<float> values)
        {
            if (values.Count == 0) return -1;
            int count = values.Count / 2;
            long byteOffset = AlignTo4(binary);
            foreach (float value in values)
                WriteFloat(binary, value);

            bufferViews.Add(new JObject
            {
                ["buffer"] = 0,
                ["byteOffset"] = byteOffset,
                ["byteLength"] = (long)count * 8,
                ["target"] = TargetArrayBuffer
            });
            accessors.Add(new JObject
            {
                ["bufferView"] = bufferViews.Count - 1,
                ["componentType"] = ComponentTypeFloat,
                ["count"] = count,
                ["type"] = "VEC2"
            });
            return accessors.Count - 1;
        }

        /// <summary>写入索引：顶点数小于65536用uint16，否则uint32（规避65535溢出）</summary>
        private static int WriteIndexAccessor(Stream binary, JArray bufferViews, JArray accessors,
            List<uint> indices)
        {
            if (indices.Count == 0) return -1;
            bool useShort = indices.Max() < 65536;
            long byteOffset = AlignTo4(binary);
            foreach (uint index in indices)
            {
                if (useShort)
                {
                    binary.WriteByte((byte)(index & 0xFF));
                    binary.WriteByte((byte)((index >> 8) & 0xFF));
                }
                else
                {
                    WriteUInt32(binary, index);
                }
            }

            long byteLength = binary.Length - byteOffset;
            bufferViews.Add(new JObject
            {
                ["buffer"] = 0,
                ["byteOffset"] = byteOffset,
                ["byteLength"] = byteLength,
                ["target"] = TargetElementArrayBuffer
            });
            accessors.Add(new JObject
            {
                ["bufferView"] = bufferViews.Count - 1,
                ["componentType"] = useShort ? ComponentTypeUnsignedShort : ComponentTypeUnsignedInt,
                ["count"] = indices.Count,
                ["type"] = "SCALAR"
            });
            return accessors.Count - 1;
        }

        private static long AlignTo4(Stream stream)
        {
            while (stream.Length % 4 != 0)
                stream.WriteByte(0);
            return stream.Length;
        }

        private static void WriteFloat(Stream stream, float value)
        {
            byte[] bytes = BitConverter.GetBytes(value);
            stream.Write(bytes, 0, bytes.Length);
        }

        private static void WriteUInt32(Stream stream, uint value)
        {
            byte[] bytes = BitConverter.GetBytes(value);
            stream.Write(bytes, 0, bytes.Length);
        }
    }
}

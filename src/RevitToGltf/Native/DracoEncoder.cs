using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

namespace RevitToGltf.Native
{
    /// <summary>
    /// Draco 编码 P/Invoke 包装：把图元几何(position/normal/uv/index)编码为
    /// KHR_draco_mesh_compression 扩展所需的 .drc 字节流。
    /// 原生 DracoWrapper.dll 与本程序集同目录——.NET 默认搜索路径不含 Addins 目录，
    /// 故首次调用前用完整路径显式 LoadLibrary 预加载。
    /// 编码失败一律返回 null，由调用方回退未压缩路径——压缩绝不阻断导出。
    /// </summary>
    internal static class DracoEncoder
    {
        // 量化位数取自 modelTo3DTiles 的实践教训：
        // 位置 16bit（世界坐标大跨度下 11bit 会有厘米级网格误差，与邻接构件接缝错位显形；
        //   本插件按构件分 mesh、量化范围是构件自身包围盒，16bit 误差通常亚毫米）；
        // UV 14bit（真实世界平铺 UV 跨度大，10bit 量化步长导致面内贴图扭曲显形）。
        internal const int QuantBitsPosition = 16;
        internal const int QuantBitsNormal = 10;
        internal const int QuantBitsTexcoord = 14;

        /// <summary>压缩等级 0~10，越大体积越小、耗时越长（7 为常用平衡点）</summary>
        internal const int CompressionLevel = 7;

        /// <summary>超大单图元跳过压缩（对齐 modelTo3DTiles 的 WASM 编码器安全上限）</summary>
        internal const int MaxTriangles = 1000000;

        private static int s_loadState;   // 0=未尝试 1=成功 -1=失败

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr LoadLibrary(string fileName);

        [DllImport("DracoWrapper.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int DracoEncodeMesh(
            float[] positions, int vertexCount,
            float[] normals, float[] uvs,
            uint[] indices, int indexCount,
            int compressionLevel, int quantPosition, int quantNormal, int quantTexcoord,
            byte[] outBuffer, int outCapacity);

        /// <summary>原生 DracoWrapper.dll 是否可用（缺失时导出自动回退未压缩）</summary>
        internal static bool Available
        {
            get
            {
                EnsureLoaded();
                return s_loadState == 1;
            }
        }

        private static void EnsureLoaded()
        {
            if (s_loadState != 0)
                return;
            try
            {
                string dir = Path.GetDirectoryName(typeof(DracoEncoder).Assembly.Location);
                if (!string.IsNullOrEmpty(dir)
                    && LoadLibrary(Path.Combine(dir, "DracoWrapper.dll")) != IntPtr.Zero)
                    s_loadState = 1;
                else
                    s_loadState = -1;
            }
            catch
            {
                s_loadState = -1;
            }
        }

        /// <summary>
        /// 编码一个图元；成功返回压缩字节，失败返回 null（调用方回退未压缩写出）。
        /// </summary>
        internal static byte[] Encode(List<float> positions, List<float> normals, List<float> uvs, List<uint> indices)
        {
            int vertexCount = positions.Count / 3;
            if (vertexCount == 0 || indices.Count < 3 || indices.Count % 3 != 0)
                return null;
            if (!Available)
                return null;

            long rawBytes = (long)(positions.Count + normals.Count + uvs.Count + indices.Count) * 4;
            int capacity = (int)Math.Min(int.MaxValue - 4096L, rawBytes + 4096);
            var buffer = new byte[capacity];

            // 法线/UV 与顶点数不匹配时按缺失处理（包装层跳过对应属性）
            float[] normalArg = normals.Count == vertexCount * 3 ? normals.ToArray() : null;
            float[] uvArg = uvs.Count == vertexCount * 2 ? uvs.ToArray() : null;

            int size = DracoEncodeMesh(
                positions.ToArray(), vertexCount, normalArg, uvArg,
                indices.ToArray(), indices.Count,
                CompressionLevel, QuantBitsPosition, QuantBitsNormal, QuantBitsTexcoord,
                buffer, capacity);
            if (size <= 0)
                return null;

            var result = new byte[size];
            Array.Copy(buffer, result, size);
            return result;
        }
    }
}

using System;
using System.IO;


namespace RevitToGltf.Pipeline
{
    /// <summary>导出上下文：承载输出目录、日志与统计</summary>
    public class GltfExportContext
    {
        public string OutputDirectory { get; set; }
        public int ElementCount { get; set; }
        public int MeshElementCount { get; set; }
        public long TriangleCount { get; set; }

        /// <summary>写出阶段累计的顶点数。写出时图元顶点数组会被即时释放，
        /// 因此不能再从 nodes 反查，需在此留档</summary>
        public long VertexCount { get; set; }
        public long TextureFileCount { get; set; }
        public int SkippedElementCount { get; set; }
        public int MaterialCount { get; set; }
        public int AppearanceAssetCount { get; set; }
        public int BitmapTextureCount { get; set; }

        /// <summary>贴图是否分离到 textures/ 目录（默认 true）。false = 内嵌进 .bin/.glb</summary>
        public bool SeparateTextures { get; set; } = true;

        /// <summary>是否把非 2 的幂 PNG/JPEG 贴图重采样到最近 2 的幂（上限 2048）。
        /// Cesium 对 REPEAT+mipmap 的 NPOT 贴图会强制 canvas 放大到下一 2 的幂，预处理可省显存提画质</summary>
        public bool NormalizeTextures { get; set; } = true;

        /// <summary>统计：归一化的贴图数</summary>
        public int NormalizedTextureCount { get; set; }

        /// <summary>是否启用 Draco 几何压缩（KHR_draco_mesh_compression；输出需查看器支持解码）</summary>
        public bool DracoEnabled { get; set; }

        /// <summary>Draco：编码失败的图元数（大于 0 时熔断，后续图元不再尝试）</summary>
        public int DracoFailedCount { get; set; }

        /// <summary>Draco：因超出安全上限而跳过的图元数</summary>
        public int DracoSkippedCount { get; set; }

        /// <summary>Draco：成功压缩的图元数</summary>
        public int DracoPrimitiveCount { get; set; }

        /// <summary>Draco：压缩前几何字节数估算（未压缩写出的理论大小）</summary>
        public long DracoRawBytes { get; set; }

        /// <summary>Draco：压缩后字节数</summary>
        public long DracoCompressedBytes { get; set; }

        /// <summary>Draco：任一图元实际压缩成功（决定顶层是否声明扩展）</summary>
        public bool DracoAnyUsed { get; set; }

        /// <summary>实例化统计：共享网格个数</summary>
        public int SharedMeshCount { get; set; }

        /// <summary>实例化统计：族实例个数</summary>
        public int InstanceCount { get; set; }

        /// <summary>不实例化时展开的三角形总数（用于量化实例化节省）</summary>
        public long ExpandedTriangleCount { get; set; }

        private readonly StreamWriter _log;

        /// <summary>进度回调：(百分比 0~100, 描述文本)。由命令行接进度窗体；为 null 时不做任何事</summary>
        public Action<int, string> OnProgress;

        /// <summary>取消检查回调：返回 true 表示用户请求中止</summary>
        public Func<bool> ShouldCancel;

        private DateTime _lastProgressAt = DateTime.MinValue;

        public GltfExportContext(string outputDirectory, StreamWriter log)
        {
            OutputDirectory = outputDirectory;
            _log = log;
        }

        public void Log(string message)
        {
            if (_log != null)
            {
                _log.WriteLine(string.Format("[{0:HH:mm:ss}] {1}", DateTime.Now, message));
                _log.Flush();
            }
        }

        /// <summary>
        /// 上报进度。节流：距上次上报不足 150ms 的直接丢弃，
        /// 避免在高频循环里因为刷新界面而拖慢提取。
        /// </summary>
        public void Report(int percent, string message)
        {
            Action<int, string> handler = OnProgress;
            if (handler == null) return;

            DateTime now = DateTime.Now;
            if ((now - _lastProgressAt).TotalMilliseconds < 150) return;
            _lastProgressAt = now;
            handler(Clamp(percent), message);
        }

        /// <summary>强制上报（不受节流限制），用于阶段切换等关键节点</summary>
        public void ReportNow(int percent, string message)
        {
            Action<int, string> handler = OnProgress;
            _lastProgressAt = DateTime.Now;
            if (handler != null) handler(Clamp(percent), message);
        }

        /// <summary>用户已请求取消时抛出，由命令行捕获后收尾</summary>
        public void ThrowIfCancelled()
        {
            Func<bool> check = ShouldCancel;
            if (check != null && check())
                throw new OperationCanceledException("用户取消了导出。");
        }

        private static int Clamp(int percent)
        {
            if (percent < 0) return 0;
            if (percent > 100) return 100;
            return percent;
        }
    }
}

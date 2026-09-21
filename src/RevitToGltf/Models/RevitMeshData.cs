using System.Collections.Generic;
using Autodesk.Revit.DB;

namespace RevitToGltf.Models
{
    /// <summary>
    /// 构件级网格数据：一个Revit元素对应一个节点，节点内按材质分组图元。
    /// 几何单位为米（已从Revit内部英尺换算），坐标系保持Revit的Z-up。
    /// </summary>
    public class RevitElementNode
    {
        /// <summary>稳定唯一键（Revit UniqueId），作为glTF节点名</summary>
        public string Key { get; set; }

        /// <summary>元素名称（用于展示）</summary>
        public string Name { get; set; }

        /// <summary>元素ID</summary>
        public int ElementId { get; set; }

        /// <summary>父节点Key（层级结构，可为null表示顶层）</summary>
        public string ParentKey { get; set; }

        /// <summary>按材质分组的几何图元</summary>
        public List<RevitPrimitive> Primitives { get; set; } = new List<RevitPrimitive>();

        /// <summary>是否含有有效几何（无几何的构件不进glTF）</summary>
        public bool HasGeometry { get { return Primitives.Count > 0; } }
    }

    /// <summary>
    /// 单一材质下的三角网格。顶点/UV/索引平行数组（非索引展开，索引顺序写入），
    /// 使用uint32索引以规避65535顶点上限。
    /// </summary>
    public class RevitPrimitive
    {
        /// <summary>Revit材质元素ID（-1表示无材质）</summary>
        public int MaterialId { get; set; }

        /// <summary>材质名</summary>
        public string MaterialName { get; set; }

        /// <summary>基础色 [r,g,b,a]，取自材质外观色</summary>
        public float[] BaseColor { get; set; } = new float[] { 0.8f, 0.8f, 0.8f, 1f };

        /// <summary>是否双面材质</summary>
        public bool DoubleSided { get; set; } = true;

        /// <summary>顶点坐标，扁平 [x,y,z,...]</summary>
        public List<float> Positions { get; set; } = new List<float>();

        /// <summary>顶点法线，扁平 [x,y,z,...]（与Positions等长）</summary>
        public List<float> Normals { get; set; } = new List<float>();

        /// <summary>顶点UV，扁平 [u,v,...]（无贴图时为空）</summary>
        public List<float> Uvs { get; set; } = new List<float>();

        /// <summary>三角形索引</summary>
        public List<uint> Indices { get; set; } = new List<uint>();

        /// <summary>贴图相对路径（textures/xxx.png），null表示使用BaseColor</summary>
        public string TextureUri { get; set; }

        public int VertexCount { get { return Positions.Count / 3; } }
    }

    /// <summary>
    /// 可实例化的共享网格：一个族符号的局部坐标几何（未应用实例放置变换），按材质分组。
    /// 同一符号的所有实例共用这一份几何，靠各自实例的 matrix 放置到世界坐标。
    /// </summary>
    public class RevitSharedMesh
    {
        /// <summary>族符号 ElementId（分组键，诊断用）</summary>
        public int SymbolId { get; set; }

        /// <summary>族符号名</summary>
        public string SymbolName { get; set; }

        /// <summary>按材质分组的几何图元（局部坐标）</summary>
        public List<RevitPrimitive> Primitives { get; set; } = new List<RevitPrimitive>();
    }

    /// <summary>共享网格的一个实例：引用共享网格 + 放置变换 + 元素元数据</summary>
    public class RevitInstance
    {
        /// <summary>稳定唯一键（Revit UniqueId），作为glTF节点名</summary>
        public string Key { get; set; }

        /// <summary>元素名称（用于展示）</summary>
        public string Name { get; set; }

        /// <summary>元素ID</summary>
        public int ElementId { get; set; }

        /// <summary>指向 ExtractResult.SharedMeshes 的下标</summary>
        public int SharedMeshIndex { get; set; }

        /// <summary>4x4 放置变换（glTF列主序，米），含平移/旋转/镜像</summary>
        public float[] Matrix { get; set; }
    }

    /// <summary>几何提取结果：唯一构件（每构件一网格）+ 可实例化的共享网格与其实例</summary>
    public class ExtractResult
    {
        /// <summary>唯一构件（系统族/就地族等，世界坐标，一构件一网格）</summary>
        public List<RevitElementNode> UniqueNodes { get; } = new List<RevitElementNode>();

        /// <summary>共享网格（族符号，局部坐标，每个符号一份）</summary>
        public List<RevitSharedMesh> SharedMeshes { get; } = new List<RevitSharedMesh>();

        /// <summary>共享网格的实例（每个族实例一条）</summary>
        public List<RevitInstance> Instances { get; } = new List<RevitInstance>();

        /// <summary>是否含任何有效几何</summary>
        public bool HasGeometry { get { return UniqueNodes.Count > 0 || SharedMeshes.Count > 0; } }
    }
}

using System.Collections.Generic;
using Newtonsoft.Json;

namespace RevitToGltf.Models
{
    /// <summary>
    /// 构件级元数据：BIM 语义信息，与 glTF 节点一一对应（Key = Revit UniqueId = glTF 节点 extras.uniqueId）。
    /// 仅在勾选「导出元数据」时采集，由 MetadataWriter 序列化进 .metadata。
    /// </summary>
    public class ElementMetadata
    {
        /// <summary>稳定唯一键（Revit UniqueId），与 glTF 节点 extras.uniqueId 一致</summary>
        public string Key { get; set; }

        /// <summary>元素ID</summary>
        public int ElementId { get; set; }

        /// <summary>元素名称</summary>
        public string Name { get; set; }

        /// <summary>类别名（如"墙"、"结构柱"）</summary>
        public string Category { get; set; }

        /// <summary>族名（族实例为 Symbol.Family.Name；系统族为 ElementType.FamilyName）</summary>
        public string Family { get; set; }

        /// <summary>类型名（族类型 / 系统族类型）</summary>
        [JsonProperty("type")]
        public string TypeName { get; set; }

        /// <summary>标高名（无标高则省略）</summary>
        public string Level { get; set; }

        /// <summary>是否为可实例化的族实例（唯一构件为 false）</summary>
        public bool IsInstance { get; set; }

        /// <summary>4x4 放置矩阵（glTF 列主序，米；仅实例有，与 glTF nodes[].matrix 同值）</summary>
        public float[] Matrix { get; set; }

        /// <summary>实例参数（参数名 → 显示值字符串），全量导出；无值参数不写入</summary>
        public Dictionary<string, string> Parameters { get; set; }
    }
}

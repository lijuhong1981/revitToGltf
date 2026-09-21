using System;
using System.Reflection;
using Autodesk.Revit.UI;

namespace RevitToGltf.Commands
{
    /// <summary>Revit 应用入口：注册"导出 glTF"相关按钮</summary>
    public class App : IExternalApplication
    {
        private const string TabName = "模型转换";
        private const string PanelName = "glTF";

        public Result OnStartup(UIControlledApplication application)
        {
            try
            {
                application.CreateRibbonTab(TabName);
            }
            catch
            {
                // 选项卡已存在（多个插件共用）时忽略
            }

            RibbonPanel panel;
            try
            {
                panel = application.CreateRibbonPanel(TabName, PanelName);
            }
            catch
            {
                panel = application.GetRibbonPanels(TabName)[0];
            }

            string assemblyPath = Assembly.GetExecutingAssembly().Location;

            var exportButtonData = new PushButtonData(
                "ExportGltf",
                "导出\nglTF",
                assemblyPath,
                "RevitToGltf.Commands.ExportGltfCommand")
            {
                ToolTip = "将当前模型导出为 glTF 2.0（保留几何、材质与贴图）",
                LongDescription = "单个弹窗内设置导出范围（全模型/视图可见/选中构件）、输出目录、\n" +
                                  "DetailLevel（Coarse/Medium/Fine）与 Triangulate 精度（0~1，滑动条）。\n" +
                                  "提取几何/材质/贴图，写出 .gltf + .bin + textures/（文件名含精度参数）。\n" +
                                  "节点名保持 Revit UniqueId，构件名与元素ID写入 extras。"
            };
            panel.AddItem(exportButtonData);

            var lodButtonData = new PushButtonData(
                "ExportLodTest",
                "LOD 精度\n测试导出",
                assemblyPath,
                "RevitToGltf.Commands.ExportLodTestCommand")
            {
                ToolTip = "自选 DetailLevel 与三角化精度，导出单个 glTF 用于 LOD 对比测试",
                LongDescription = "弹出设置对话框，选择 DetailLevel（Coarse/Medium/Fine）与 Triangulate 精度（0~1，留空=默认），\n" +
                                  "每次导出一个 glTF（如 lod_fine_default.gltf / lod_medium_0.20.gltf）。\n" +
                                  "每次运行统计追加写入 lod-test.log，方便手动设不同精度逐档对比。\n" +
                                  "仅导几何，不生成 meta.json、不调用 3D Tiles 转换器。\n" +
                                  "支持只导当前视图可见构件或选中构件以加速测试。"
            };
            panel.AddItem(lodButtonData);

            return Result.Succeeded;
        }

        public Result OnShutdown(UIControlledApplication application)
        {
            return Result.Succeeded;
        }
    }
}

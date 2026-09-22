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
                                  "可选勾选「导出元数据」生成同名 .metadata（构件 BIM 信息，含类别/族/类型/标高/实例参数）。\n" +
                                  "节点名保持 Revit UniqueId，构件名与元素ID写入 extras。"
            };
            panel.AddItem(exportButtonData);

            return Result.Succeeded;
        }

        public Result OnShutdown(UIControlledApplication application)
        {
            return Result.Succeeded;
        }
    }
}

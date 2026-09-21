using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitToGltf.Extraction;
using RevitToGltf.Models;
using RevitToGltf.Output;
using RevitToGltf.Pipeline;

namespace RevitToGltf.Commands
{
    /// <summary>
    /// LOD 精度测试导出命令：由用户选择 DetailLevel（Coarse/Medium/Fine）与 Triangulate（三角化精度 0~1，留空=默认），
    /// 每次导出单个 glTF，用于手动设置不同精度逐档对比三角形数/文件大小/视觉差异。
    /// 仅导几何，不生成 meta.json、不调用 3D Tiles 转换器。
    /// 输出为 <根目录>\lod_<detail>_<tri>.gltf（如 lod_fine_default.gltf / lod_medium_0.20.gltf），
    /// 每次运行统计追加写入 lod-test.log。
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class ExportLodTestCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIDocument uiDocument = commandData.Application.ActiveUIDocument;
            if (uiDocument == null)
            {
                message = "请先打开一个 Revit 模型文档。";
                return Result.Failed;
            }
            Document doc = uiDocument.Document;

            // 1. 选择导出范围
            ICollection<ElementId> scopeIds;
            string scopeName;
            if (!SelectScope(uiDocument, doc, out scopeIds, out scopeName))
                return Result.Cancelled;

            // 2. 选择输出目录
            string outputDirectory = SelectOutputDirectory(doc);
            if (string.IsNullOrEmpty(outputDirectory))
                return Result.Cancelled;
            Directory.CreateDirectory(outputDirectory);

            // 3. 选择精度设置（DetailLevel + Triangulate）
            ViewDetailLevel detailLevel;
            double? triangulateLod;
            string tierName;
            if (!SelectSettings(out detailLevel, out triangulateLod, out tierName))
                return Result.Cancelled;

            var settings = new GeometryDetailSettings(tierName, detailLevel, triangulateLod)
            {
                LogProgressEvery = 2000,
                EnableInstancing = false
            };
            string logPath = Path.Combine(outputDirectory, "lod-test.log");
            string gltfPath = Path.Combine(outputDirectory, tierName + ".gltf");

            string summaryLine = null;
            using (var log = new StreamWriter(logPath, true, Encoding.UTF8))
            {
                var context = new GltfExportContext(outputDirectory, log);
                context.Log(string.Empty);
                context.Log(string.Format("===== LOD 单档导出: {0} (Detail={1}, Tri={2}) =====",
                    doc.Title, detailLevel, TriText(triangulateLod)));
                context.Log(string.Format("范围: {0}", scopeName));
                context.Log(string.Format("输出文件: {0}", gltfPath));

                context.ResetGeometryCounters();
                var extractWatch = Stopwatch.StartNew();
                ExtractResult result;
                try
                {
                    result = GeometryExtractor.Extract(doc, context, settings, scopeIds);
                }
                catch (Exception ex)
                {
                    context.Log(string.Format("提取失败: {0}", ex.Message));
                    TaskDialog.Show("提取失败", ex.Message);
                    return Result.Failed;
                }
                extractWatch.Stop();

                if (!result.HasGeometry)
                {
                    context.Log(string.Format("无几何输出（Detail={0} 对该模型/范围无可用几何）。", detailLevel));
                    TaskDialog.Show("无几何输出", "该精度设置下没有提取到任何几何，请调整 DetailLevel 或范围后重试。");
                    return Result.Succeeded;
                }

                var writeWatch = Stopwatch.StartNew();
                try
                {
                    GltfWriter.Export(gltfPath, result, context);
                }
                catch (Exception ex)
                {
                    context.Log(string.Format("写出失败: {0}", ex.Message));
                    TaskDialog.Show("写出失败", ex.Message);
                    return Result.Failed;
                }
                writeWatch.Stop();

                // 顶点数在写出过程中由写出器累计（图元数组写完即释放，无法再从 nodes 反查）
                long vertexCount = context.VertexCount;
                long sizeBytes = FileSize(gltfPath) + FileSize(Path.ChangeExtension(gltfPath, ".bin"));
                double sizeMb = sizeBytes / 1048576.0;

                summaryLine = string.Format(
                    "[LOD] {0,-20} | Detail={1,-6} | Tri={2,6} | 元素 {3} | 含几何 {4} | 三角形 {5:N0} | 顶点 {6:N0} | gltf+bin {7:0.0}MB | 提取 {8:0.0}s | 写出 {9:0.0}s",
                    tierName, detailLevel, TriText(triangulateLod),
                    context.ElementCount, context.MeshElementCount, context.TriangleCount, vertexCount,
                    sizeMb, extractWatch.Elapsed.TotalSeconds, writeWatch.Elapsed.TotalSeconds);
                context.Log(summaryLine);
            }

            var dialog = new TaskDialog("LOD 单档导出完成");
            dialog.MainInstruction = "导出统计（详见 lod-test.log）";
            dialog.MainContent = summaryLine ?? "（无内容）";
            dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "打开输出目录");
            dialog.CommonButtons = TaskDialogCommonButtons.Close;
            if (dialog.Show() == TaskDialogResult.CommandLink1)
                Process.Start("explorer.exe", outputDirectory);

            AppSettings.SaveLastOutputDirectory(outputDirectory);
            return Result.Succeeded;
        }

        /// <summary>选择几何导出范围：全模型 / 当前视图可见 / 仅选中构件</summary>
        private static bool SelectScope(UIDocument uiDocument, Document doc,
            out ICollection<ElementId> scopeIds, out string scopeName)
        {
            scopeIds = null;
            scopeName = null;

            var dialog = new TaskDialog("选择导出范围");
            dialog.MainInstruction = "选择 LOD 测试的几何范围";
            dialog.MainContent = "全模型最慢，建议首次用小范围（视图可见或选中构件）验证趋势。";
            dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "全模型", "全量导出（最慢）");
            dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink2, "当前视图可见", "仅当前视图可见构件（推荐）");
            dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink3, "仅选中构件", "仅当前选中构件（最快）");
            dialog.CommonButtons = TaskDialogCommonButtons.Cancel;

            TaskDialogResult result = dialog.Show();
            if (result == TaskDialogResult.CommandLink1)
            {
                scopeIds = null;
                scopeName = "全模型";
                return true;
            }
            if (result == TaskDialogResult.CommandLink2)
            {
                ICollection<ElementId> ids = new FilteredElementCollector(doc, doc.ActiveView.Id)
                    .WhereElementIsNotElementType().ToElementIds();
                if (ids.Count == 0)
                {
                    TaskDialog.Show("范围为空", "当前视图没有可导出的构件。");
                    return false;
                }
                scopeIds = ids;
                scopeName = string.Format("视图可见 {0} 个构件 ({1})", ids.Count, doc.ActiveView.Name);
                return true;
            }
            if (result == TaskDialogResult.CommandLink3)
            {
                ICollection<ElementId> ids = uiDocument.Selection.GetElementIds();
                if (ids.Count == 0)
                {
                    TaskDialog.Show("范围为空", "请先选中构件再选择该范围。");
                    return false;
                }
                scopeIds = ids;
                scopeName = string.Format("选中 {0} 个构件", ids.Count);
                return true;
            }
            return false;
        }

        /// <summary>选择输出目录（优先上次选择的目录，否则默认桌面下的 项目名_lodtest）</summary>
        private static string SelectOutputDirectory(Document doc)
        {
            string projectName = Path.GetFileNameWithoutExtension(doc.PathName);
            if (string.IsNullOrEmpty(projectName)) projectName = doc.Title;

            string defaultDir = AppSettings.LoadLastOutputDirectory()
                ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), projectName + "_lodtest");

            using (var dialog = new FolderBrowserDialog())
            {
                dialog.Description = "选择 LOD 测试输出目录";
                dialog.SelectedPath = defaultDir;
                return dialog.ShowDialog() == DialogResult.OK ? dialog.SelectedPath : null;
            }
        }

        /// <summary>
        /// 弹出精度设置对话框：DetailLevel（Coarse/Medium/Fine）下拉 + Triangulate（0~1，留空=默认）文本框。
        /// 输入非法数值时对话框保持打开并提示。
        /// </summary>
        private static bool SelectSettings(out ViewDetailLevel detailLevel, out double? triangulateLod, out string tierName)
        {
            detailLevel = ViewDetailLevel.Fine;
            triangulateLod = null;
            tierName = null;

            ViewDetailLevel chosen = ViewDetailLevel.Fine;
            double? chosenTri = null;

            using (var form = new System.Windows.Forms.Form())
            {
                form.Text = "LOD 精度设置";
                form.ClientSize = new System.Drawing.Size(400, 205);
                form.FormBorderStyle = FormBorderStyle.FixedDialog;
                form.MaximizeBox = false;
                form.MinimizeBox = false;
                form.StartPosition = FormStartPosition.CenterScreen;

                var detailLabel = new Label { Text = "DetailLevel（视图详细程度）:", Left = 12, Top = 15, Width = 370 };
                var detailBox = new System.Windows.Forms.ComboBox
                {
                    Left = 12,
                    Top = 40,
                    Width = 200,
                    DropDownStyle = ComboBoxStyle.DropDownList
                };
                detailBox.Items.Add("Fine（精细）");
                detailBox.Items.Add("Medium（中等）");
                detailBox.Items.Add("Coarse（粗略）");
                detailBox.SelectedIndex = 0;

                var triLabel = new Label { Text = "Triangulate（三角化精度 0~1）:", Left = 12, Top = 75, Width = 370 };
                var triBox = new System.Windows.Forms.TextBox { Left = 12, Top = 100, Width = 120 };
                var triHint = new Label
                {
                    Text = "留空 = 默认（最精细，与正式导出一致）",
                    Left = 145,
                    Top = 104,
                    Width = 245,
                    ForeColor = System.Drawing.Color.Gray
                };

                var okButton = new Button { Text = "导出", Left = 200, Top = 155, Width = 85, Height = 28 };
                var cancelButton = new Button
                {
                    Text = "取消",
                    Left = 300,
                    Top = 155,
                    Width = 85,
                    Height = 28,
                    DialogResult = DialogResult.Cancel
                };

                okButton.Click += (s, e) =>
                {
                    string t = triBox.Text.Trim();
                    if (t.Length > 0)
                    {
                        double v;
                        bool ok = double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out v);
                        if (!ok) ok = double.TryParse(t, out v);
                        if (!ok || v < 0 || v > 1)
                        {
                            MessageBox.Show(form, "Triangulate 需为 0~1 之间的数字（如 0.2），或留空使用默认。",
                                "数值无效", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                            return;
                        }
                        chosenTri = v;
                    }
                    else
                    {
                        chosenTri = null;
                    }
                    chosen = detailBox.SelectedIndex == 0 ? ViewDetailLevel.Fine
                           : detailBox.SelectedIndex == 1 ? ViewDetailLevel.Medium
                           : ViewDetailLevel.Coarse;
                    form.DialogResult = DialogResult.OK;
                    form.Close();
                };

                form.Controls.Add(detailLabel);
                form.Controls.Add(detailBox);
                form.Controls.Add(triLabel);
                form.Controls.Add(triBox);
                form.Controls.Add(triHint);
                form.Controls.Add(okButton);
                form.Controls.Add(cancelButton);
                form.AcceptButton = okButton;
                form.CancelButton = cancelButton;

                if (form.ShowDialog() != DialogResult.OK)
                    return false;
            }

            detailLevel = chosen;
            triangulateLod = chosenTri;

            string detailName = detailLevel == ViewDetailLevel.Fine ? "fine"
                              : detailLevel == ViewDetailLevel.Medium ? "medium" : "coarse";
            string triName = triangulateLod.HasValue
                ? triangulateLod.Value.ToString("0.00", CultureInfo.InvariantCulture)
                : "default";
            tierName = "lod_" + detailName + "_" + triName;
            return true;
        }

        /// <summary>Triangulate 值的显示文本（null=默认）</summary>
        private static string TriText(double? triangulateLod)
        {
            return triangulateLod.HasValue ? triangulateLod.Value.ToString("0.00", CultureInfo.InvariantCulture) : "默认";
        }

        private static long FileSize(string path)
        {
            return File.Exists(path) ? new FileInfo(path).Length : 0;
        }
    }
}

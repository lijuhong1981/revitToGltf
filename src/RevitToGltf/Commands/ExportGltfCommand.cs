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
    /// 导出 glTF 命令：单个弹窗内完成全部设置——导出范围（全模型/视图可见/选中构件）、输出目录、
    /// DetailLevel（Coarse/Medium/Fine）与 Triangulate（三角化精度 0~1）滑动条。
    /// 按所选精度提取几何/材质/贴图，写出单个 glTF（.gltf + .bin + textures/）。
    /// 可选勾选「导出元数据」生成同名 .metadata（项目信息 + 构件 BIM 信息）；贴图默认分离到 textures/，
    /// 取消「贴图分离」后 PNG/JPEG 贴图内嵌进 .bin/.glb。不调用 3D Tiles 转换器。
    /// 输出为 <根目录>\<项目名>_<detail>_<tri>.gltf（如 ljdd_fine_1.00.gltf），统计追加写入 gltf-export.log。
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class ExportGltfCommand : IExternalCommand
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

            // 1. 单个弹窗完成全部设置：范围 + 文件名 + 输出目录 + DetailLevel + Triangulate + 格式(.gltf/.glb) + 元数据
            ICollection<ElementId> scopeIds;
            string scopeName;
            string outputDirectory;
            string fileName;
            ViewDetailLevel detailLevel;
            double? triangulateLod;
            bool binary;
            bool exportMetadata;
            bool separateTextures;
            if (!SelectSettings(uiDocument, doc, out scopeIds, out scopeName,
                out outputDirectory, out fileName, out detailLevel, out triangulateLod, out binary, out exportMetadata, out separateTextures))
                return Result.Cancelled;
            Directory.CreateDirectory(outputDirectory);

            string gltfPath = Path.Combine(outputDirectory, fileName + (binary ? ".glb" : ".gltf"));
            string metadataPath = Path.Combine(outputDirectory, fileName + ".metadata");
            string logPath = Path.Combine(outputDirectory, "gltf-export.log");

            var settings = new GeometryDetailSettings(fileName, detailLevel, triangulateLod)
            {
                LogProgressEvery = 2000,
                ExportMetadata = exportMetadata
            };

            string summaryLine = null;
            string failure = null;      // 非空表示失败，需弹窗提示
            bool cancelled = false;
            bool writing = false;       // 切换进度条权重：提取 0~85%，写出 85~100%
            double extractSeconds = 0;
            double writeSeconds = 0;

            var progress = new ExportProgressForm("导出 glTF");
            try
            {
                progress.Show();
                Application.DoEvents();

                using (var log = new StreamWriter(logPath, true, Encoding.UTF8))
                {
                    var context = new GltfExportContext(outputDirectory, log)
                    {
                        OnProgress = (percent, text) => progress.Report(
                            writing ? 85 + percent * 15 / 100 : percent * 85 / 100, text),
                        ShouldCancel = () => progress.Cancelled,
                        SeparateTextures = separateTextures
                    };
                    context.Log(string.Empty);
                    context.Log(string.Format("===== 导出 glTF: {0} ({1}) =====", doc.Title, doc.PathName));
                    context.Log(string.Format("范围: {0}", scopeName));
                    context.Log(string.Format("精度: Detail={0}, Tri={1}", detailLevel, TriText(triangulateLod)));
                    context.Log(string.Format("贴图: {0}", separateTextures ? "分离到 textures/" : "内嵌进 bin/glb"));
                    context.Log(string.Format("输出文件: {0}", gltfPath));
                    if (exportMetadata)
                        context.Log(string.Format("输出元数据: {0}", metadataPath));

                    var extractWatch = Stopwatch.StartNew();
                    ExtractResult result = null;
                    try
                    {
                        result = GeometryExtractor.Extract(doc, context, settings, scopeIds);
                    }
                    catch (OperationCanceledException)
                    {
                        cancelled = true;
                        context.Log("用户取消了导出。");
                    }
                    catch (Exception ex)
                    {
                        context.Log(string.Format("提取失败: {0}", ex.Message));
                        failure = "提取失败：" + ex.Message;
                    }
                    extractWatch.Stop();
                    extractSeconds = extractWatch.Elapsed.TotalSeconds;

                    if (!cancelled && failure == null && result != null && !result.HasGeometry)
                    {
                        context.Log("无几何输出（该模型/范围无可用几何）。");
                        failure = "没有提取到任何几何，请调整 DetailLevel 或范围后重试。";
                    }

                    if (!cancelled && failure == null && result != null && result.HasGeometry)
                    {
                        writing = true;
                        var writeWatch = Stopwatch.StartNew();
                        try
                        {
                            GltfWriter.Export(gltfPath, result, context, binary);
                        }
                        catch (OperationCanceledException)
                        {
                            cancelled = true;
                            context.Log("用户取消了导出。");
                        }
                        catch (Exception ex)
                        {
                            context.Log(string.Format("写出失败: {0}", ex.Message));
                            failure = "写出失败：" + ex.Message;
                        }
                        writeWatch.Stop();
                        writeSeconds = writeWatch.Elapsed.TotalSeconds;

                        if (!cancelled && failure == null)
                        {
                            // 顶点数在写出过程中由写出器累计（图元数组写完即释放，无法再从 result 反查）
                            long vertexCount = context.VertexCount;
                            long sizeBytes = FileSize(gltfPath)
                                + (binary ? 0 : FileSize(Path.ChangeExtension(gltfPath, ".bin")));
                            double sizeMb = sizeBytes / 1048576.0;

                            string formatTag = binary ? "glb" : "gltf";
                            string sizeTag = binary ? "glb" : "gltf+bin";
                            summaryLine = string.Format(
                                "[{0}] {1} | Detail={2} | Tri={3} | 元素 {4} | 含几何 {5} | 三角形 {6:N0} | 顶点 {7:N0} | {8} {9:0.0}MB | 提取 {10:0.0}s | 写出 {11:0.0}s",
                                formatTag, fileName, detailLevel, TriText(triangulateLod),
                                context.ElementCount, context.MeshElementCount, context.TriangleCount, vertexCount,
                                sizeTag, sizeMb, extractSeconds, writeSeconds);
                            if (context.SharedMeshCount > 0)
                                summaryLine += string.Format("\n实例化: 共享网格 {0} 个 / 实例 {1} 个, 展开三角形 {2:N0} → 去重 {3:N0} (省 {4:0.0}%)",
                                    context.SharedMeshCount, context.InstanceCount,
                                    context.ExpandedTriangleCount, context.TriangleCount,
                                    (1 - (double)context.TriangleCount / Math.Max(1, context.ExpandedTriangleCount)) * 100);

                            if (exportMetadata)
                            {
                                try
                                {
                                    int metaCount = MetadataWriter.Export(metadataPath, result, context, doc,
                                        scopeName, detailLevel, triangulateLod, binary);
                                    summaryLine += string.Format("\n元数据: {0} 条 → {1} ({2:0.0}KB)",
                                        metaCount, Path.GetFileName(metadataPath), FileSize(metadataPath) / 1024.0);
                                }
                                catch (Exception ex)
                                {
                                    context.Log(string.Format("元数据写出失败({0}): {1}", Path.GetFileName(metadataPath), ex.Message));
                                    summaryLine += "\n元数据: 写出失败（详见日志）";
                                }
                            }
                            context.Log(summaryLine);
                        }
                    }
                }
            }
            finally
            {
                if (!progress.IsDisposed)
                {
                    progress.Close();
                    progress.Dispose();
                }
            }

            if (cancelled)
            {
                TaskDialog.Show("导出已取消",
                    string.Format("已在提取 {0:0.0}s / 写出 {1:0.0}s 后中止。\n已写出的文件可能不完整，建议删除后重新导出。",
                        extractSeconds, writeSeconds));
                return Result.Cancelled;
            }

            if (failure != null)
            {
                TaskDialog.Show("导出失败", failure);
                return Result.Failed;
            }

            var dialog = new TaskDialog("导出 glTF 完成");
            dialog.MainInstruction = "导出统计（详见 gltf-export.log）";
            dialog.MainContent = summaryLine ?? "（无内容）";
            dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "打开输出目录");
            dialog.CommonButtons = TaskDialogCommonButtons.Close;
            if (dialog.Show() == TaskDialogResult.CommandLink1)
                Process.Start("explorer.exe", outputDirectory);

            return Result.Succeeded;
        }

        /// <summary>
        /// 单个弹窗完成全部设置：导出范围、文件名、输出目录、DetailLevel、Triangulate。
        /// </summary>
        private static bool SelectSettings(
            UIDocument uiDocument, Document doc,
            out ICollection<ElementId> scopeIds, out string scopeName,
            out string outputDirectory, out string fileName,
            out ViewDetailLevel detailLevel, out double? triangulateLod, out bool binary,
            out bool exportMetadata, out bool separateTextures)
        {
            scopeIds = null;
            scopeName = null;
            outputDirectory = null;
            fileName = null;
            detailLevel = ViewDetailLevel.Fine;
            triangulateLod = null;
            binary = false;
            exportMetadata = false;
            separateTextures = true;

            string projectName = Path.GetFileNameWithoutExtension(doc.PathName);
            if (string.IsNullOrEmpty(projectName)) projectName = doc.Title;

            AppSettings.Settings saved = AppSettings.Load();

            string chosenDir = (!string.IsNullOrWhiteSpace(saved.LastOutputDirectory) && Directory.Exists(saved.LastOutputDirectory))
                ? saved.LastOutputDirectory
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), projectName + "_gltf");

            int scopeChoice = saved.Scope; // 0=全模型 1=视图可见 2=选中构件
            if (scopeChoice < 0 || scopeChoice > 2) scopeChoice = 1;
            ViewDetailLevel chosenDetail = ParseDetail(saved.DetailLevel);
            double? chosenTri = saved.UseCustomTriangulate
                ? (double?)Math.Min(1.0, Math.Max(0.0, saved.TriangulateLod))
                : null;    // null=使用 Revit 默认三角化（不勾选）
            string chosenFile = null;
            bool chosenBinary = saved.Binary;    // false=.gltf true=.glb
            bool chosenMeta = saved.ExportMetadata;       // 是否导出元数据 .metadata
            bool chosenSeparateTex = saved.SeparateTextures; // 贴图是否分离到 textures/（默认勾选）
            bool nameEdited = false;      // 用户手工改过文件名后，滑动条不再自动改写
            bool suppressNameSync = false; // 程序化赋值时抑制 TextChanged 的"已编辑"标记

            using (var form = new System.Windows.Forms.Form())
            {
                form.Text = "导出 glTF 设置";
                form.ClientSize = new System.Drawing.Size(1100, 672);
                form.FormBorderStyle = FormBorderStyle.FixedDialog;
                form.MaximizeBox = false;
                form.MinimizeBox = false;
                form.StartPosition = FormStartPosition.CenterScreen;

                // ---- 导出范围（水平一行）。
                //      注意：RadioButton.AutoSize 必须显式设为 false 并同时给出 Width 与 Height ——
                //      宽度需按最长的一项（"仅选中构件（最快）"，10 字）留足，否则会折行而第二行装不下。 ----
                const int RadioHeight = 48;
                var scopeGroup = new GroupBox { Text = "导出范围", Left = 12, Top = 12, Width = 1076, Height = 118 };
                var radioFull = new RadioButton
                {
                    Text = "全模型（最慢）", Left = 25, Top = 58, Width = 280, Height = RadioHeight, AutoSize = false,
                    Checked = (scopeChoice == 0),
                    BackColor = System.Drawing.Color.Transparent
                };
                var radioView = new RadioButton
                {
                    Text = "当前视图可见（推荐）", Left = 325, Top = 58, Width = 340, Height = RadioHeight, AutoSize = false, Checked = (scopeChoice == 1),
                    BackColor = System.Drawing.Color.Transparent
                };
                var radioSel = new RadioButton
                {
                    Text = "仅选中构件（最快）", Left = 690, Top = 58, Width = 380, Height = RadioHeight, AutoSize = false,
                    Checked = (scopeChoice == 2),
                    BackColor = System.Drawing.Color.Transparent
                };
                scopeGroup.Controls.Add(radioFull);
                scopeGroup.Controls.Add(radioView);
                scopeGroup.Controls.Add(radioSel);

                // ---- 文件名（不含扩展名）。默认 <项目名>_<detail>_<tri>，随滑动条同步；
                //      一旦用户手工改动过，就不再自动改写。 ----
                Func<string> triSlug = () => chosenTri.HasValue
                    ? chosenTri.Value.ToString("0.00", CultureInfo.InvariantCulture) : "default";
                Func<string> defaultName = () => projectName + "_" + DetailSlug(chosenDetail) + "_" + triSlug();
                var nameLabel = new Label { Text = "文件名:", Left = 12, Top = 532, Width = 160 };
                var nameBox = new System.Windows.Forms.TextBox
                {
                    Left = 12,
                    Top = 560,
                    Width = 920,
                    Height = 32,
                    Text = defaultName()
                };
                nameBox.TextChanged += (s, e) =>
                {
                    if (!suppressNameSync) nameEdited = true;
                };
                Action syncName = () =>
                {
                    if (nameEdited) return;
                    suppressNameSync = true;
                    nameBox.Text = defaultName();
                    suppressNameSync = false;
                };

                // ---- DetailLevel：只有 3 档，用单选比滑条更直观，且高度更矮（滑条含刻度约 84px） ----
                var detailLabel = new Label { Text = "DetailLevel（视图详细程度）:", Left = 12, Top = 142, Width = 600 };
                var radioCoarse = new RadioButton
                {
                    Text = "Coarse（最粗）", Left = 12, Top = 180, Width = 230, Height = RadioHeight, AutoSize = false,
                    Checked = (chosenDetail == ViewDetailLevel.Coarse),
                    BackColor = System.Drawing.Color.Transparent
                };
                var radioMedium = new RadioButton
                {
                    Text = "Medium（中等）", Left = 262, Top = 180, Width = 230, Height = RadioHeight, AutoSize = false,
                    Checked = (chosenDetail == ViewDetailLevel.Medium),
                    BackColor = System.Drawing.Color.Transparent
                };
                var radioFine = new RadioButton
                {
                    Text = "Fine（最细）", Left = 512, Top = 180, Width = 230, Height = RadioHeight, AutoSize = false, Checked = (chosenDetail == ViewDetailLevel.Fine),
                    BackColor = System.Drawing.Color.Transparent
                };
                radioCoarse.CheckedChanged += (s, e) => { if (radioCoarse.Checked) { chosenDetail = ViewDetailLevel.Coarse; syncName(); } };
                radioMedium.CheckedChanged += (s, e) => { if (radioMedium.Checked) { chosenDetail = ViewDetailLevel.Medium; syncName(); } };
                radioFine.CheckedChanged += (s, e) => { if (radioFine.Checked) { chosenDetail = ViewDetailLevel.Fine; syncName(); } };

                // 注意：TrackBar 在高 DPI 下实际高度约 64px（TickStyle.None），下一行需按 ~70px 预留。
                // 勾选 = 用自定义精度(0~1)；不勾选 = 用 Revit 默认三角化（三角形更少、体积更小）。
                var useTriCheck = new CheckBox
                {
                    Text = "使用自定义三角化精度（不勾选 = Revit 默认，三角形更少、体积更小）",
                    Left = 12,
                    Top = 248,
                    Width = 880,
                    AutoSize = true,
                    Checked = chosenTri.HasValue
                };
                var triTrack = new TrackBar
                {
                    Left = 12,
                    Top = 276,
                    Width = 880,
                    Minimum = 0,
                    Maximum = 100,
                    TickFrequency = 10,
                    SmallChange = 1,
                    LargeChange = 10,
                    Value = chosenTri.HasValue ? (int)Math.Round(chosenTri.Value * 100) : 100,
                    TickStyle = TickStyle.None,
                    Enabled = chosenTri.HasValue
                };
                var triValue = new Label { Text = TriText(chosenTri), Left = 908, Top = 286, Width = 180 };
                useTriCheck.CheckedChanged += (s, e) =>
                {
                    triTrack.Enabled = useTriCheck.Checked;
                    if (useTriCheck.Checked)
                    {
                        chosenTri = triTrack.Value / 100.0;
                        triValue.Text = chosenTri.Value.ToString("0.00", CultureInfo.InvariantCulture);
                    }
                    else
                    {
                        chosenTri = null;
                        triValue.Text = "default";
                    }
                    syncName();
                };
                triTrack.ValueChanged += (s, e) =>
                {
                    chosenTri = triTrack.Value / 100.0;
                    triValue.Text = chosenTri.Value.ToString("0.00", CultureInfo.InvariantCulture);
                    syncName();
                };

                // ---- 输出目录 ----
                var dirLabel = new Label { Text = "输出目录:", Left = 12, Top = 458, Width = 160 };
                var dirBox = new System.Windows.Forms.TextBox
                {
                    Left = 12,
                    Top = 486,
                    Width = 920,
                    Height = 32,
                    Text = chosenDir,
                    ReadOnly = true
                };
                var browseButton = new Button { Text = "浏览...", Left = 948, Top = 484, Width = 140, Height = 38 };
                browseButton.Click += (s, e) =>
                {
                    using (var dlg = new System.Windows.Forms.FolderBrowserDialog())
                    {
                        dlg.Description = "选择 glTF 输出目录";
                        dlg.SelectedPath = string.IsNullOrEmpty(dirBox.Text) ? chosenDir : dirBox.Text;
                        if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                            dirBox.Text = dlg.SelectedPath;
                    }
                };

                // ---- 输出格式：.gltf（JSON + 外部 .bin + 贴图）或 .glb（单文件二进制） ----
                var fmtLabel = new Label { Text = "格式:", Left = 12, Top = 346, Width = 60 };
                var radioGltf = new RadioButton
                {
                    Text = ".gltf（JSON + 外部.bin）",
                    AutoSize = true,
                    BackColor = System.Drawing.Color.Transparent,
                    Checked = !chosenBinary
                };
                var radioGlb = new RadioButton
                {
                    Text = ".glb（单文件二进制）",
                    AutoSize = true,
                    BackColor = System.Drawing.Color.Transparent,
                    Checked = chosenBinary
                };
                radioGltf.CheckedChanged += (s, e) => { if (radioGltf.Checked) chosenBinary = false; };
                radioGlb.CheckedChanged += (s, e) => { if (radioGlb.Checked) chosenBinary = true; };
                // FlowLayoutPanel 按文字实际宽度排布单选，避免高 DPI 下固定宽度截断描述文字
                var fmtPanel = new System.Windows.Forms.FlowLayoutPanel
                {
                    Left = 80,
                    Top = 340,
                    Width = 1000,
                    Height = 34,
                    FlowDirection = FlowDirection.LeftToRight,
                    WrapContents = false
                };
                fmtPanel.Controls.Add(radioGltf);
                fmtPanel.Controls.Add(radioGlb);

                // 贴图分离：独立一行，放在格式选择下方、导出元数据上方
                var separateTexCheck = new CheckBox
                {
                    Text = "贴图分离（不勾选 = 内嵌进 .bin/.glb）",
                    Left = 12,
                    Top = 388,
                    AutoSize = true,
                    Checked = chosenSeparateTex,
                    BackColor = System.Drawing.Color.Transparent
                };
                separateTexCheck.CheckedChanged += (s, e) => { chosenSeparateTex = separateTexCheck.Checked; };

                // 导出元数据：独立一行（不放同一 FlowLayoutPanel，避免与格式单选同行）
                var exportMetaCheck = new CheckBox
                {
                    Text = "导出元数据（同名 .metadata）",
                    Left = 12,
                    Top = 424,
                    AutoSize = true,
                    Checked = chosenMeta,
                    BackColor = System.Drawing.Color.Transparent
                };
                exportMetaCheck.CheckedChanged += (s, e) => { chosenMeta = exportMetaCheck.Checked; };

                var okButton = new Button { Text = "导出", Left = 828, Top = 604, Width = 120, Height = 42 };
                var cancelButton = new Button
                {
                    Text = "取消",
                    Left = 960,
                    Top = 604,
                    Width = 120,
                    Height = 42,
                    DialogResult = DialogResult.Cancel
                };
                okButton.Click += (s, e) =>
                {
                    scopeChoice = radioSel.Checked ? 2 : (radioFull.Checked ? 0 : 1);

                    if (scopeChoice == 2)
                    {
                        ICollection<ElementId> selIds = uiDocument.Selection.GetElementIds();
                        if (selIds.Count == 0)
                        {
                            MessageBox.Show(form, "请先选中构件，再选择「仅选中构件」。", "范围为空",
                                MessageBoxButtons.OK, MessageBoxIcon.Warning);
                            return;
                        }
                    }
                    if (string.IsNullOrWhiteSpace(dirBox.Text))
                    {
                        MessageBox.Show(form, "请选择输出目录。", "目录为空",
                            MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        return;
                    }

                    string candidate = nameBox.Text.Trim();
                    if (string.IsNullOrEmpty(candidate))
                    {
                        MessageBox.Show(form, "请输入输出文件名。", "文件名为空",
                            MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        return;
                    }
                    if (candidate.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                    {
                        MessageBox.Show(form, "文件名含有非法字符（不能包含 \\ / : * ? \" < > |）。", "文件名非法",
                            MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        return;
                    }

                    chosenDir = dirBox.Text;
                    chosenFile = candidate;
                    AppSettings.Save(new AppSettings.Settings
                    {
                        LastOutputDirectory = chosenDir,
                        Scope = scopeChoice,
                        DetailLevel = DetailSlug(chosenDetail),
                        UseCustomTriangulate = chosenTri.HasValue,
                        TriangulateLod = chosenTri ?? 1.0,
                        Binary = chosenBinary,
                        ExportMetadata = chosenMeta,
                        SeparateTextures = chosenSeparateTex
                    });
                    form.DialogResult = DialogResult.OK;
                    form.Close();
                };

                form.Controls.Add(scopeGroup);
                form.Controls.Add(nameLabel);
                form.Controls.Add(nameBox);
                form.Controls.Add(dirLabel);
                form.Controls.Add(dirBox);
                form.Controls.Add(browseButton);
                form.Controls.Add(fmtLabel);
                form.Controls.Add(fmtPanel);
                form.Controls.Add(exportMetaCheck);
                form.Controls.Add(separateTexCheck);
                form.Controls.Add(detailLabel);
                form.Controls.Add(radioCoarse);
                form.Controls.Add(radioMedium);
                form.Controls.Add(radioFine);
                form.Controls.Add(useTriCheck);
                form.Controls.Add(triTrack);
                form.Controls.Add(triValue);
                form.Controls.Add(okButton);
                form.Controls.Add(cancelButton);
                form.AcceptButton = okButton;
                form.CancelButton = cancelButton;

                if (form.ShowDialog() != DialogResult.OK)
                    return false;
            }

            // 计算最终范围
            switch (scopeChoice)
            {
                case 0:
                    scopeIds = null;
                    scopeName = "全模型";
                    break;
                case 2:
                    scopeIds = uiDocument.Selection.GetElementIds();
                    scopeName = string.Format("选中 {0} 个构件", scopeIds.Count);
                    break;
                default:
                    scopeIds = new FilteredElementCollector(doc, doc.ActiveView.Id)
                        .WhereElementIsNotElementType().ToElementIds();
                    if (scopeIds.Count == 0)
                    {
                        TaskDialog.Show("范围为空", "当前视图没有可导出的构件。");
                        return false;
                    }
                    scopeName = string.Format("视图可见 {0} 个构件 ({1})", scopeIds.Count, doc.ActiveView.Name);
                    break;
            }

            outputDirectory = chosenDir;
            fileName = chosenFile;
            detailLevel = chosenDetail;
            triangulateLod = chosenTri;
            binary = chosenBinary;
            exportMetadata = chosenMeta;
            separateTextures = chosenSeparateTex;
            return true;
        }

        /// <summary>DetailLevel 文件名段（小写英文）</summary>
        private static string DetailSlug(ViewDetailLevel detail)
        {
            if (detail == ViewDetailLevel.Fine) return "fine";
            if (detail == ViewDetailLevel.Medium) return "medium";
            return "coarse";
        }

        /// <summary>持久化字符串 → ViewDetailLevel（非法值回退 Fine）</summary>
        private static ViewDetailLevel ParseDetail(string slug)
        {
            if (slug == "coarse") return ViewDetailLevel.Coarse;
            if (slug == "medium") return ViewDetailLevel.Medium;
            return ViewDetailLevel.Fine;
        }

        /// <summary>三角化精度显示文本（null=Revit 默认）</summary>
        private static string TriText(double? triangulateLod)
        {
            return triangulateLod.HasValue
                ? triangulateLod.Value.ToString("0.00", CultureInfo.InvariantCulture)
                : "default";
        }

        private static long FileSize(string path)
        {
            return File.Exists(path) ? new FileInfo(path).Length : 0;
        }
    }
}

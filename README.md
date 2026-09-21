# revitToGltf

Revit 模型导出 glTF 2.0 的 Revit 插件。保留**几何、材质与贴图**，输出可直接在 glTF 查看器 / three.js / Babylon.js 中加载。

## 背景

这是从 [revitTo3DTiles](https://github.com/lijuhong1981/revitTo3DTiles) 独立出来的 glTF 导出层：只负责 Revit → glTF（几何 + 材质 + 贴图），不做 BIM 语义 sidecar，也不做 3D Tiles 转换。既可作为独立插件使用，也作为后续精度调优的演进主场。

```
Revit 文档
  └─ 几何 + 材质 + 贴图 → glTF 2.0（.gltf + .bin + textures/）
```

贴图直接取自 Revit 渲染外观（`AppearanceAssetElement`）中记录的原始图片文件，不经中间格式转换，因此不受 IFC 贴图丢失问题影响。

## 架构

| 模块 | 职责 |
|---|---|
| `Extraction/GeometryExtractor` | 遍历文档元素提取三角网格，支持嵌套族变换展开，英尺→米换算，DetailLevel/Triangulate 精度控制 |
| `Extraction/MaterialExtractor` | 材质颜色（渲染外观优先）与透明度 |
| `Extraction/TextureExtractor` | 从渲染外观提取贴图图片并按内容哈希外置去重 |
| `Output/GltfWriter` | glTF 2.0 写出（POSITION/NORMAL/TEXCOORD_0，uint32 索引，构件名/元素ID写入 extras） |
| `Pipeline/GltfExportContext` | 导出上下文（输出目录/日志/统计） |

## 下载安装

无需编译，直接使用发布包。从 [GitHub Releases](https://github.com/lijuhong1981/revitToGltf/releases) 下载 `revitToGltf-v0.1.0.zip`，解压得到：

- `RevitToGltf.dll`
- `RevitToGltf.addin`

将两个文件一起复制到：

```
C:\ProgramData\Autodesk\Revit\Addins\2020\
```

重启 Revit 后，功能区出现 **模型转换 → glTF** 面板（含「导出 glTF」按钮）。

## 构建

**依赖**：Revit 2020 + Visual Studio 2019（.NET 桌面开发工作负载）

1. 打开 `revitToGltf.sln`
2. 确认已安装 `.NET Framework 4.7.2 目标包`
3. Revit 安装目录默认为 `D:\Program Files\Autodesk\Revit 2020`，不同请修改 `RevitToGltf.csproj` 中的 `RevitInstallDir`，或用 `/p:RevitInstallDir="..."` 覆盖
4. 生成解决方案

**命令行构建**：

```bash
msbuild revitToGltf.sln -p:Configuration=Release
```

## 从源码部署

1. 将 `bin\Debug\RevitToGltf.dll`（或 Release）复制到：
   ```
   C:\ProgramData\Autodesk\Revit\Addins\2020\
   ```
2. 同目录放置 `RevitToGltf.addin` 清单文件

启动 Revit 后，功能区出现 **模型转换 → glTF** 面板，含一个按钮：

- **导出 glTF**：单个弹窗设置范围/输出目录/DetailLevel/Triangulate，全流程导出

## 使用

**导出 glTF**：

1. 打开 `.rvt` 模型
2. 点击 **导出 glTF**
3. 在单个弹窗内完成全部设置：
   - 导出范围（全模型 / 当前视图可见 / 选中构件）
   - 输出目录（默认桌面下 `<项目名>_gltf`）
   - DetailLevel（Coarse/Medium/Fine）与 Triangulate（0~1）滑动条
   - 输出格式（.gltf / .glb）
4. 点击「导出」，等待提取完成，弹出统计信息

**输出结构**：

```
<项目名>_gltf/
├── <项目名>_<detail>_<tri>.gltf   # glTF 入口，如 ljdd_fine_1.00.gltf（节点 extras 含构件名与元素ID）
├── <项目名>_<detail>_<tri>.bin    # 几何二进制（选 .glb 时内嵌进 .glb，不单独生成）
├── textures/                      # 外部贴图
└── gltf-export.log                # 导出日志
```

## 已知限制

- UV 为按面归一化（每个面的贴图铺满一次），未使用贴图的真实世界缩放
- 贴图提取依赖 Revit 渲染外观中的图片路径，程序化纹理（渐变、噪波等）无图片可提取
- 提取阶段为同步执行，超大模型（百万级构件）Revit 界面会暂时无响应

## License

MIT

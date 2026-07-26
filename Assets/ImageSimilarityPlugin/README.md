# Image Similarity Plugin

Unity Editor 图片相似度检测工具，基于 MobileNetV2 深度学习模型 + 余弦相似度算法，帮助快速找出并清理项目中的重复/相似图片资产。

## 功能

- **分组扫描**：扫描任意文件夹，自动将相似图片分组展示
- **以图搜图**：给定一张图片，搜索项目中所有相似图片，支持外部 API 调用
- **相似图片选择器**：导入新图片时自动弹出，让用户选择使用已有图片替代导入
- 图形化结果展示，按相似组排列，支持缩略图预览
- 缩略图水平滚动，保宽高比显示，支持任意组内图片数量
- 点击缩略图打开大图预览窗口，查看文件详细信息
- 批量勾选并删除重复资产（支持 Ctrl+Z 撤销）
- 自动选择重复项：按文件大小 + 文件名启发式识别副本
- **引用替换**：保留一张图，自动替换所有 Prefab 中 Image.sprite 的引用
- **特征缓存**：扫描后自动保存特征向量，后续查询秒出结果（无需重新推理）
- **增量更新**：图片变更时只重新提取修改/新增部分，而非全量重扫
- **缓存过期检测**：自动对比文件修改时间，提前告知用户哪些图片需要更新
- **持久化 Python 会话**：后台常驻 TF 进程，消除每次操作的模型加载开销
- 支持 JPG / PNG / BMP / GIF / TIFF / WebP 格式
- 跨平台支持（Windows / macOS）

## 环境要求

| 依赖           | 版本        |
| ------------ | --------- |
| Python       | >= 3.6    |
| TensorFlow   | >= 2.6.0  |
| NumPy        | >= 1.19.5 |
| Pillow       | >= 8.4.0  |
| scikit-learn | >= 0.24.2 |
| tqdm         | >= 4.65.0 |

工具会在首次使用时自动检测环境，并支持**一键安装**缺失的 Python 依赖。

## 安装

将整个 `ImageSimilarityPlugin/` 文件夹复制到你的 Unity 项目的 `Assets/` 目录下即可。

```
你的项目/
└── Assets/
    └── ImageSimilarityPlugin/
        ├── Editor/       ← C# 编辑器脚本
        ├── Python/       ← Python 引擎脚本
        └── README.md
```

## 使用

### 1. 打开工具

Unity 菜单栏 → **Tools → 查找相似图片**

首次打开会自动检测 Python 环境和依赖包状态。如果缺少依赖，点击 **"安装依赖"** 按钮即可自动通过 pip 安装。

窗口顶部有两个 Tab：

| Tab | 功能 |
|-----|------|
| **分组扫描** | 扫描文件夹，自动将相似图片分组（原有功能） |
| **以图搜图** | 选择一张图片，搜索项目中所有相似图片 |

### 2. 分组扫描（Tab 1）

配置参数：

| 参数    | 说明                 | 默认值       |
| ----- | ------------------ | --------- |
| 文件夹   | 要扫描的目录             | `Assets/` |
| 相似度阈值 | 余弦相似度阈值（0~1），越高越严格 | 0.80      |
| 递归子目录 | 是否扫描子文件夹           | 是         |
| 线程数   | 并行处理线程数            | 4         |

点击 **"开始扫描"**，完成后展示所有相似图片组。扫描同时会自动保存**特征缓存**，使后续的以图搜图查询秒出结果。

### 3. 以图搜图（Tab 2）

1. 点击 **"从项目中选择..."** → 弹出 Unity 原生资源选择器 → 搜索/选择一张 Sprite 图片
2. 设置目标文件夹和相似度阈值
3. 点击 **"开始搜索"**
4. 结果按相似度降序排列，每条显示缩略图、文件信息、相似度分数条

首次查询会提取所有目标图片的特征（约 5~10 秒），之后自动命中缓存，查询仅需约 2 秒。

### 4. 相似图片选择器（外部 API）

导入流程等外部模块可通过公开 API 弹出选择器：

```csharp
// 在导入流程中调用
ImageSimilarityQuery.ShowPicker(
    queryImagePath: newImagePath,
    folderPath: Application.dataPath,
    threshold: 0.85f,
    topK: 30,
    onPicked: selectedPath =>
    {
        if (selectedPath != null)
        {
            // 用户选择了已有图片 → 用它替代导入
            string assetPath = PluginUtils.AbsoluteToAssetPath(selectedPath);
            var sprite = AssetDatabase.LoadAssetAtPath<Sprite>(assetPath);
            prefabImage.sprite = sprite;
        }
        else
        {
            // 用户关闭窗口 → 继续正常导入流程
        }
    }
);
```

选择器窗口展示查询图预览（含文件大小、像素尺寸）和相似图片列表（含缩略图、路径、文件大小、像素尺寸、相似度分数条）。用户可点击某张图片选择，或点击底部按钮/关闭窗口表示不选用。

### 5. 右键菜单

在 Project 窗口**右键点击任意图片** → **Assets → 查找相似图片**，直接弹出选择器窗口。

### 6. 删除重复

勾选要删除的图片 → 点击 **"删除 N 个选中资产"** → 确认后通过 `AssetDatabase.DeleteAsset` 删除（可 Ctrl+Z 撤销）。

### 7. 引用替换

在预览窗口中：

1. 选择要**保留**的图片
2. 点击 **"保留此图片并替换所有引用"**
3. 工具会自动查找所有引用了同组其他图片的 Prefab，将其中的 `Image.sprite` 替换为保留图

## 外部 API

```csharp
// 异步查询 — 返回 PythonRunner 实例（可取消）
public static PythonRunner ImageSimilarityQuery.QueryAsync(
    string queryImagePath,    // 查询图片绝对路径
    string folderPath,        // 搜索目标文件夹
    float threshold = 0.80f,
    int topK = 50,
    bool recursive = true,
    int workers = 4,
    Action<QueryResultData> onComplete = null,
    Action<string> onError = null);

// 弹出选择器窗口 — 返回窗口实例
public static SimilarImagePickerWindow ImageSimilarityQuery.ShowPicker(
    string queryImagePath,
    string folderPath,
    float threshold = 0.80f,
    int topK = 30,
    Action<string> onPicked = null,    // 选中图片路径（null=未选）
    Action onCancelled = null);

// 特征缓存目录
public static string ImageSimilarityQuery.CacheDir { get; }
```

## 顶部状态栏

| 状态           | 颜色  | 含义                         |
| ------------ | --- | -------------------------- |
| `Python 3.x` | 🟢  | Python 已就绪                 |
| `未找到 Python` | 🔴  | 需要手动配置 Python 路径           |
| `依赖已就绪`      | 🟢  | TensorFlow 等包已安装           |
| `缺少依赖`       | 🔴  | 点击"安装依赖"自动安装               |
| `FR2 已就绪`    | 🟢  | Find Reference 2 已安装且有缓存数据 |
| `FR2 缓存为空`   | 🟡  | FR2 已安装但未扫描，打开一次 FR2 窗口即可  |
| `FR2 未安装`    | 🔴  | FR2 未安装（可选，不影响基本功能）        |

## FR2 集成（可选）

如果项目中安装了 [Find Reference 2](https://assetstore.unity.com/packages/tools/utilities/find-reference-2-59064)：

- 缩略图右上角会显示蓝色引用数角标
- "保留此图片并替换所有引用"功能会使用 FR2 的秒级引用查询

## 特征缓存

扫描或首次查询时自动在以下目录保存特征向量：

```
{TEMP}/DefaultCompany/SameImageSearch/ImageSimilarityPlugin/features/
├── {folder_hash}.npy     ← (N, 1280) float32 特征数组
└── {folder_hash}.json    ← 路径清单 + mtime + 元数据
```

### 增量更新

切换文件夹或重新打开窗口时，工具会自动检测文件变更（通过 mtime 对比），并在界面上显示黄色警告条：

> ⚠️ 特征缓存可能过期：490 张未变  3 张已修改  5 张新增  [重新扫描]

点击"重新扫描"后，**只重新提取修改和新增的图片**，未变的直接复用缓存：

| 场景 | 之前 | 之后 |
|------|------|------|
| 500 张图，3 张修改 + 5 张新增 | 全量重扫 500 张 (~20s) | 增量推理 8 张 (~1s) |

- 首次扫描/查询：提取所有图片特征并保存（100 张图约 3~5 秒）
- 后续查询：命中缓存时直接加载（< 1 秒），仅需推理查询图自身 + 变更部分
- 缓存按文件夹路径哈希存储，不同目录互不干扰
- 要强制重建缓存：重新运行一次分组扫描 或 手动删除 `features/` 目录

### 参考性能

| 目标文件夹 | 首次（无缓存） | 后续（缓存命中，未变） | 后续（8 张变更） |
|------------|---------------|---------------------|-----------------|
| 100 张 | ~3-5 秒 | < 1 秒 | ~1 秒 |
| 500 张 | ~15-25 秒 | ~1-2 秒 | ~2-3 秒 |
| 2000 张 | ~1-2 分钟 | ~3-5 秒 | ~5-10 秒 |

## 持久化 Python 会话

工具会在窗口打开时自动启动一个后台 Python 服务器（`query_server.py`），通过 stdin/stdout JSON 协议通信。该服务器保持 TensorFlow 模型长驻内存，避免每次查询都重新加载 MobileNetV2（节省约 2 秒启动开销）。当服务器不可用时，自动回退到独立子进程模式。

## 文件结构

```
ImageSimilarityPlugin/
├── Editor/
│   ├── ScanResultData.cs             数据结构（ScanResultData / DuplicateGroup / QueryResultData / SimilarImage）
│   ├── PluginUtils.cs                共享工具方法
│   ├── PythonLocator.cs              跨平台 Python 检测
│   ├── PythonRunner.cs               Python 子进程管理 + 持久化会话调度
│   ├── PythonSession.cs              持久化 Python 会话（stdin/stdout JSON 通信）
│   ├── DependencyInstaller.cs        pip 依赖安装
│   ├── FR2Integration.cs             FR2 反射集成
│   ├── ImageSimilarityQuery.cs       公开静态 API + 菜单入口
│   ├── SimilarityWindow.cs           主窗口（分组扫描 + 以图搜图）
│   ├── ImagePreviewWindow.cs         大图预览窗口
│   └── SimilarImagePickerWindow.cs   相似图片选择器窗口
├── Python/
│   ├── feature_extractor.py          特征提取引擎 + 特征缓存
│   ├── duplicate_detector_cli.py     分组扫描 CLI
│   ├── image_query_cli.py            以图搜图 CLI
│   ├── query_server.py               持久化查询服务器（常驻进程，免重复加载 TF）
│   └── requirements.txt              pip 依赖清单
└── README.md
```

## 常见问题

**Q: 扫描很慢？**
A: 首次运行需要下载 MobileNetV2 预训练模型（约 14MB），之后从缓存加载。特征提取在 CPU 上约 35-50ms/张图（4 线程）。后续利用增量更新，只处理变更图片。增加线程数可提高速度。

**Q: 图片修改后结果不准？**
A: 工具会自动检测文件 mtime 变化，在界面上显示黄色警告条提示"特征缓存可能过期"。点击"重新扫描"即可增量更新缓存。

**Q: 以图搜图第一次慢，第二次快？**
A: 正常现象。首次需要提取所有目标图片的特征并缓存。之后直接加载缓存（<1 秒），且具备增量更新能力——只重提取变更图片。

**Q: 缩略图变形？**
A: 工具从原始文件字节加载图片以保证宽高比，不受 Unity 导入的 2 次幂纹理影响。

**Q: 扫描后没找到相似图片？**
A: 尝试降低相似度阈值（如 0.75）。阈值越高越严格。

**Q: 删除的图片能恢复吗？**
A: 通过 AssetDatabase 删除的可以 Ctrl+Z 撤销。

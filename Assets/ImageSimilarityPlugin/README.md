# Image Similarity Plugin

Unity Editor 图片相似度检测工具，基于 MobileNetV2 深度学习模型 + 余弦相似度算法，帮助快速找出并清理项目中的重复/相似图片资产。

## 功能

- 扫描任意文件夹中的相似图片（支持 JPG / PNG / BMP / GIF / TIFF / WebP）
- 图形化结果展示，按相似组排列，支持缩略图预览
- 缩略图水平滚动，保宽高比显示，支持任意组内图片数量
- 点击缩略图打开大图预览窗口，查看文件详细信息
- 批量勾选并删除重复资产（支持 Ctrl+Z 撤销）
- 自动选择重复项：按文件大小 + 文件名启发式识别副本
- **引用替换**：保留一张图，自动替换所有 Prefab 中 Image.sprite 的引用
- 扫描结果缓存，下次打开可直接加载无需重扫
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

### 2. 配置扫描参数

| 参数    | 说明                 | 默认值       |
| ----- | ------------------ | --------- |
| 文件夹   | 要扫描的目录             | `Assets/` |
| 相似度阈值 | 余弦相似度阈值（0~1），越高越严格 | 0.80      |
| 递归子目录 | 是否扫描子文件夹           | 是         |
| 线程数   | 并行处理线程数            | 4         |

### 3. 扫描与结果

点击 **"开始扫描"**，进度条会显示实时进度。完成后展示所有相似图片组：

- 每组显示缩略图行列（水平滚动）
- 勾选框选择要删除的图片
- **"自动选择重复项"** 根据文件大小和文件名自动勾选可能的副本
- 点击缩略图打开**预览窗口**

### 4. 删除重复

勾选要删除的图片 → 点击 **"删除 N 个选中资产"** → 确认后通过 `AssetDatabase.DeleteAsset` 删除（可 Ctrl+Z 撤销）。

### 5. 引用替换

在预览窗口中：

1. 选择要**保留**的图片
2. 点击 **"保留此图片并替换所有引用"**
3. 工具会自动查找所有引用了同组其他图片的 Prefab，将其中的 `Image.sprite` 替换为保留图
4. 完成后报告修改的 Prefab 数量和替换的组件数量

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
- 替换后 FR2 角标自动更新

没有 FR2 时，引用查找回退到 Unity 原生 `AssetDatabase.FindAssets` 全量扫描（项目 Prefab 较少时几乎无感知）。

## 文件结构

```
ImageSimilarityPlugin/
├── Editor/
│   ├── ScanResultData.cs          数据结构
│   ├── PluginUtils.cs             共享工具方法
│   ├── PythonLocator.cs           跨平台 Python 检测
│   ├── PythonRunner.cs            Python 子进程管理
│   ├── DependencyInstaller.cs     pip 依赖安装
│   ├── FR2Integration.cs          FR2 反射集成
│   ├── SimilarityWindow.cs        主窗口
│   └── ImagePreviewWindow.cs      预览窗口
├── Python/
│   ├── feature_extractor.py       特征提取引擎
│   ├── duplicate_detector_cli.py  无头 CLI
│   └── requirements.txt           pip 依赖清单
└── README.md
```

## 常见问题

**Q: 扫描很慢？**
A: 首次运行需要下载 MobileNetV2 预训练模型（约 14MB）。增加线程数可提高速度。

**Q: 缩略图变形？**
A: 工具从原始文件字节加载图片以保证宽高比，不受 Unity 导入的 2 次幂纹理影响。如果在 Project 窗口看到变形，那是 Unity 导入设置的问题。

**Q: 扫描后没找到相似图片？**
A: 尝试降低相似度阈值（如 0.75）。阈值越高越严格。

**Q: 删除的图片能恢复吗？**
A: 通过 AssetDatabase 删除的可以 Ctrl+Z 撤销。外部文件的删除会移至回收站。

# 

# Image Similarity Plugin

`ImageSimilarityPlugin` 是一个仅在 Unity Editor 中运行的图片相似度检查工具。它使用 ImageNet 预训练的 MobileNetV2 将图片转换为 1280 维特征向量，再以余弦相似度完成分组扫描和以图搜图。

该工具适合发现视觉内容相近的候选资源，不是像素级重复文件检测器。裁剪、透明边缘、配色和主体变化都可能影响结果，删除前必须人工确认引用和画面用途。

## 能力与边界

| 能力     | 行为                                                  | 边界                                                     |
| ------ | --------------------------------------------------- | ------------------------------------------------------ |
| 分组扫描   | 扫描指定目录，将达到阈值的图片按组展示                                 | 每组以一张未处理图片为锚点收集直接相似项，不保证组内任意两张都达到阈值                    |
| 名称筛选   | 按图片文件名关键词筛选分组，组内任意图片命中时展示整组                         | 仅检查文件名，不匹配目录路径；匹配不区分大小写                                |
| 以图搜图   | 返回达到阈值的候选图片，按相似度降序排列                                | 查询图片本身不出现在结果中                                          |
| 排除目录   | 项目级维护不参与搜索的目录列表，分组扫描和以图搜图统一剪枝                       | 只排除搜索候选；查询图片本身仍可位于排除目录中                                |
| 特征缓存   | 按“目录 + 是否递归 + 排除目录”缓存特征，后续只处理新增或修改项                 | 变更检测依赖文件 mtime，不计算内容哈希                                 |
| 批量删除   | 将选中的项目内资产移入系统回收站或废纸篓                                | 只支持 `Assets` 内文件；Unity 的 `Ctrl+Z` 不能撤销文件删除             |
| 引用替换   | 将 Prefab 中旧图片的 `UnityEngine.UI.Image.sprite` 替换为保留图 | 不处理 Scene、Material、ScriptableObject、RawImage 或代码中的路径引用 |
| FR2 集成 | 显示引用数，并加速 Prefab 候选查找                               | 可选；没有 FR2 时回退到 Unity 原生依赖扫描                            |
| 外部 API | 提供异步查询和候选选择器窗口                                      | 插件不会自动接管项目的图片导入流程，调用方需要主动接入                            |

扫描目标支持以下扩展名：

```text
.jpg .jpeg .png .bmp .gif .tiff .tif .webp
```

## 目录结构



```text
ImageSimilarityPlugin/
|-- Editor/
|   |-- ImageSimilarityPlugin.Editor.asmdef
|   |-- SimilarityWindow.cs
|   |-- ImageSimilarityQuery.cs
|   |-- PythonRunner.cs
|   |-- PythonSession.cs
|   `-- ...
|-- Python/
|   |-- feature_extractor.py
|   |-- duplicate_detector_cli.py
|   |-- image_query_cli.py
|   |-- query_server.py
|   `-- requirements.txt
`-- README.md
```

插件目录可以放在 `Assets` 下的其他位置，但必须保留 `Editor` 与 `Python` 为同级目录。C# 程序集只包含 Editor 平台，不进入玩家构建。

## 环境要求

- Unity 2022.3
- 64 位 Python 3.6 或更高版本
- 能够安装与当前 Python、操作系统匹配的 TensorFlow wheel
- 首次安装依赖和首次加载 MobileNetV2 权重时可访问对应的 Python 包源及模型下载地址

`Python/requirements.txt` 中的直接依赖为：

```text
tensorflow>=2.6.0
numpy>=1.19.5
Pillow>=8.4.0
```

TensorFlow 对 Python 和操作系统有自己的版本兼容范围。“Python 版本通过插件检测”不等于当前 TensorFlow 一定存在可安装的 wheel；遇到安装失败时，应先选择一个与目标 TensorFlow 兼容的 Python 解释器。

## 快速开始

### 1. 打开窗口

在 Unity 菜单中选择：

```text
Tools > 查找相似图片
```

窗口顶部显示 Python、依赖和 FR2 状态。未找到 Python 时可手动选择解释器；缺少依赖时可点击“安装依赖”，等价于：

```bash
python -m pip install -r Assets/Editor/ImageSimilarityPlugin/Python/requirements.txt
```

### 2. 分组扫描

1. 在“分组扫描”页选择目录。
2. 按需添加排除目录，并设置相似度阈值、是否递归和 worker 数。
3. 点击“开始扫描”。
4. 人工检查分组，必要时打开大图预览或查看引用数。
5. 只在确认安全后选择并删除候选资产。

相似度范围为 `0` 到 `1`，判断条件为“大于或等于阈值”。阈值越高，结果通常越接近；建议先以较高阈值检查，再逐步降低。

扫描文件夹和以图搜图的目标文件夹共享最近一次有效目录，并通过 `EditorPrefs` 按当前项目保存。重新打开窗口时会恢复该目录；如果目录已被删除，则回退到项目的 `Assets` 目录。

扫描完成后可在结果上方输入图片名称关键词并点击“搜索”。筛选只检查文件名且不区分大小写；组内任意一张图片的文件名包含关键词时展示整组。输入框内容不会即时触发筛选，只有点击“搜索”才会应用；点击“清除”恢复全部分组。未命中的分组不参与布局绘制，也不会加载缩略图或查询 FR2 引用数。

### 3. 以图搜图

在“以图搜图”页选择一个项目内 Sprite，设置目标目录、阈值和最大结果数，然后点击“开始搜索”。也可以在 Project 窗口中右键图片并选择：

```text
Assets > 查找相似图片
```

内置的相似图片选择器只保留 Project 窗口的图片右键入口，不再提供独立的顶部菜单入口。右键入口会先要求选择搜索目录，目录选择器默认定位到查询图片所在文件夹；取消目录选择不会启动查询。随后候选窗口会显示实际搜索目录以及路径、文件大小、缩略图、相似度和引用数，但不会自动修改任何资产。右键入口和外部查询 API 同样会自动应用当前项目的排除目录。

### 4. 排除目录

“分组扫描”和“以图搜图”设置区共享同一个排除目录列表。点击 `+` 选择目录，点击 `-` 移除；项目内路径显示为 `Assets/...`。列表通过 `EditorPrefs` 按当前项目保存，不会写入 Unity 资产或影响其他项目。

递归搜索会在目录枚举阶段直接跳过排除目录及其全部后代，不会读取其中图片、提取特征或返回结果。添加一个已有规则的父目录时，列表会自动移除被其覆盖的子目录规则。任务运行期间列表不可修改，确保本次搜索范围和缓存范围一致。

### 5. 替换 Prefab 引用

在分组结果中点击缩略图打开预览窗口。进入窗口时，点击进入的图片会作为初始替换目标；点击缩略图只切换大图预览，不会改变目标。每张图片需要分别选择“设为目标”或勾选“替换引用”，替换来源默认全部不选。

1. 只查找引用了用户勾选来源图片的 Prefab；FR2 可用时优先使用其缓存，否则扫描项目 Prefab 依赖。
2. 逐个加载 Prefab 内容。
3. 将匹配到的 `Image.sprite` 替换为用户指定的目标图。
4. 保存被修改的 Prefab。
5. FR2 可用时刷新其 UsedBy 索引；完成后详情页和分组列表会重新查询目标图及来源图的引用数角标。

目标图不能同时作为替换来源。确认对话框会列出本次目标和来源范围；未勾选图片不会参与引用扫描或修改。此操作不会自动删除旧图片，也不会覆盖 Prefab 之外的引用类型。执行后仍需检查版本差异和目标界面。

缩略图列表保持固定单元宽度，过长文件名不会撑开布局；鼠标悬停在分组扫描、路径列表或预览缩略图的文件名上可查看完整路径。预览页“文件信息”中的文件名会左对齐自动换行，并可选中复制，不会使用单行截断。

大图预览页会固定预留文件信息区，并根据窗口剩余宽度按原始比例缩放图片；预览宽高均不超过 `512px`。有效预览区使用图片缩放后的实际尺寸，小图不会被放大或保留额外空白；横向图片不会再撑宽外层滚动区域而移出可视范围。图片加载失败时会保留有限的预览占位，文件信息区不会贴到窗口左侧。

插件内部仍使用绝对路径访问文件和生成缓存键，但 UI 中的项目内目录、文件路径和 Tooltip 统一显示为 `Assets/...`。目录输入框接受 `Assets/...`，执行查询前会自动恢复为绝对路径；项目外扫描结果因没有对应的 Asset 路径，仍显示其原始绝对路径。

## 外部 API

### 数据查询

```csharp
PythonRunner runner = ImageSimilarityQuery.QueryAsync(
    queryImagePath: newImagePath,
    folderPath: Application.dataPath,
    threshold: 0.85f,
    topK: 30,
    recursive: true,
    workers: 4,
    onComplete: result =>
    {
        foreach (SimilarImage item in result.results)
            Debug.Log($"{item.image_path}: {item.similarity:P1}");
    },
    onError: error => Debug.LogError(error));

// 关闭调用方或不再需要结果时取消。
runner?.Cancel();
```

参数无效时 `QueryAsync` 会同步调用 `onError` 并返回 `null`。正常回调在 Unity Editor 主线程执行。`QueryResultData.failed_images` 包含读取或特征提取失败并被跳过的路径；分组结果中的对应字段为 `ScanResultData.failed_images`。

### 候选选择器

```csharp
SimilarImagePickerWindow window = ImageSimilarityQuery.ShowPicker(
    queryImagePath: newImagePath,
    folderPath: Application.dataPath,
    threshold: 0.85f,
    topK: 30,
    onPicked: selectedPath =>
    {
        if (selectedPath == null)
        {
            ImportOriginalImage();
            return;
        }

        string assetPath = PluginUtils.AbsoluteToAssetPath(selectedPath);
        Sprite sprite = AssetDatabase.LoadAssetAtPath<Sprite>(assetPath);
        UseExistingSprite(sprite);
    },
    onCancelled: () => Debug.Log("用户取消了相似图片选择"));
```

回调语义：

- 选择候选图：`onPicked` 收到该图片的绝对路径。
- 点击“不选择，使用原图导入”：`onPicked` 收到 `null`，不触发 `onCancelled`。
- 点击“取消”或关闭窗口：`onPicked` 收到 `null`，随后触发 `onCancelled`。
- 每个窗口最多回调一次。

## 缓存与进程

### 特征缓存

缓存根目录：

```text
{Application.temporaryCachePath}/ImageSimilarityPlugin/features/
|-- {scope_hash}.npy
`-- {scope_hash}.json
```

- `.npy` 保存 `(N, 1280)` 的 `float32` 特征数组。
- `.json` 保存图片路径、mtime、扫描目录、递归范围、排除目录和生成时间。
- 缓存键包含规范化目录路径、递归范围和排序后的排除目录；修改排除列表后不会复用旧搜索范围的特征缓存。
- 查询图位于目标目录内时仍保留在完整特征缓存中，只在查询结果阶段排除。
- 损坏或维度不匹配的缓存会被忽略并重新生成。

如果外部工具修改文件内容但保留原 mtime，插件无法自动发现。需要强制重建时，关闭正在运行的任务后删除 `features` 目录，再重新扫描或查询。

### 扫描结果缓存

分组扫描结果保存在：

```text
{Application.temporaryCachePath}/ImageSimilarityPlugin/scan_{scope_hash}.json
{Application.temporaryCachePath}/ImageSimilarityPlugin/scan_{scope_hash}_result.json
```

扫描结果缓存键同样包含目录路径、递归范围和排除目录，只用于恢复上一次分组 UI，不替代特征缓存。

分组结果默认保持完整展开，组头、缩略图行、路径列表和操作按钮的高度与交互不变。结果区按整组判断是否与当前垂直视口相交：不可见组只保留等高布局占位，不创建组内控件，也不加载缩略图或查询 FR2 引用；可见组的水平缩略图行同样只处理与横向视口相交的图片。这样缓存中存在大量分组或单组图片较多时，单帧 GUI 工作量主要由当前可见内容决定。

分组扫描的缩略图列表固定为单行高度，只提供横向滚动条；纵向位置固定为 0，不会出现无意义的竖向滚动或因滚动状态改变组高。

FR2 的程序集类型、反射成员、状态快照和缓存资产查找结果会在当前 Unity Domain 内复用。FR2 未就绪时不会请求引用数，也不会把临时的 0 引用写入引用数缓存；状态检查期间仅以低频率重新采样。分组扫描、以图搜图或扫描结果缓存加载完成时会清除插件保存的引用数，下一次绘制角标会重新查询 FR2；检测到 FR2 内部仍有待处理资产时会自动启动 UsedBy 索引刷新。同时监测 FR2 缓存 `_timeStamp`，即使一次异步重建在两次状态采样之间完成，也会使旧角标失效。该限制同时作用于分组扫描和以图搜图界面，避免每次 `OnGUI` 扫描程序集或查询 `AssetDatabase`。

### Python 进程

打开主窗口并确认依赖可用后，插件会尝试启动 `query_server.py`，通过逐行 JSON 的 stdin/stdout 协议复用已加载的 TensorFlow 模型。协议一次只处理一个命令：

- 服务空闲时使用常驻进程。
- 服务尚未就绪、正忙或异常退出时，查询任务回退到独立 CLI 子进程。
- 取消常驻任务会终止服务进程；下次访问时自动重启。
- 每个独立子进程使用唯一结果文件，多个调用方不会覆盖彼此的输出。

## 命令行调试

可以绕过 Unity 单独验证 Python 端。以下命令不会修改 Unity 资产：

```bash
python Assets/Editor/ImageSimilarityPlugin/Python/duplicate_detector_cli.py \
  --folder "Assets" \
  --threshold 0.90 \
  --recursive \
  --exclude "Assets/path/to/excluded-folder" \
  --workers 4 \
  --output "Temp/image-groups.json"

python Assets/Editor/ImageSimilarityPlugin/Python/image_query_cli.py \
  --query "Assets/path/to/query.png" \
  --folder "Assets" \
  --threshold 0.85 \
  --top-k 30 \
  --recursive \
  --exclude "Assets/path/to/excluded-folder" \
  --workers 4 \
  --output "Temp/image-query.json"
```

`--exclude` 可以重复传入多个目录。标准输出中的 `PROGRESS:<0-100>` 供 Unity 解析，诊断信息写入标准错误，最终结果写入 `--output` 指定的 JSON 文件。结果 JSON 的 `failed_images` 字段列出无法处理的输入路径。

## 常见问题

### 首次运行很慢或长时间停在模型加载

首次使用需要导入 TensorFlow 并获取 MobileNetV2 的 ImageNet 权重。先在同一 Python 环境中执行依赖安装，再查看 Unity Console 和 Python stderr 是否包含网络、证书、wheel 或动态库错误。

### 安装完成后仍显示缺少依赖

顶部配置的 Python 可能与执行 `pip` 的 Python 不一致。使用以下命令核对解释器：

```bash
python -c "import sys, tensorflow, numpy, PIL; print(sys.executable); print(tensorflow.__version__)"
```

重新选择 Python 路径后，插件会清除旧解释器的依赖检测状态并重新检查。

### 相似图片没有被分到同一组

MobileNetV2 更关注语义和整体视觉特征，不等同于像素哈希。可降低阈值复查；若目标是严格重复文件检测，应另行比较文件哈希或解码后的像素数据。

### 删除按钮不可用

删除只支持全部位于项目 `Assets` 目录内的分组。外部目录可以扫描和预览，但必须在文件管理器中处理。

### 引用数为空或替换很慢

FR2 未安装或缓存未就绪时，插件会使用 Unity 原生 API 扫描全部 Prefab。大型项目中这一步可能较慢；先更新 FR2 缓存可以减少查找时间。

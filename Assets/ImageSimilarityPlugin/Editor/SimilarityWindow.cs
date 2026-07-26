using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace ImageSimilarityPlugin
{
    /// <summary>
    /// 图片相似度检测工具的主窗口。
    /// 提供文件夹选择、参数配置、Python 扫描调度、结果分组展示、
    /// 缓存管理、依赖安装以及批量删除功能。
    /// 菜单入口: Tools > 查找相似图片
    /// </summary>
    public class SimilarityWindow : EditorWindow
    {
        // ===== 扫描参数 =====
        private string _folderPath = "";
        private float _threshold = 0.80f;
        private bool _recursive = true;
        private int _workers = 4;

        // ===== 运行状态 =====
        private PythonRunner _runner;
        private ScanResultData _results;
        private string _statusMessage = "";
        private bool _statusIsError = false;
        private string _pythonVersion = null;
        private bool _depsInstalled = false;
        private bool _checkingDeps = false;
        private bool _fr2Ready = false;

        // ===== 扫描结果缓存 =====
        private static string CacheDir => Path.Combine(Application.temporaryCachePath, "ImageSimilarityPlugin");
        private string _cachePath;         // 当前文件夹对应的缓存文件路径
        private string _lastCheckedFolder; // 上一次检查缓存的文件夹，用于检测变化
        private bool _hasCache;            // 是否有有效缓存可用
        private string _cacheInfo;         // 缓存摘要信息（用于 UI 显示）

        // ===== 结果 UI =====
        private Vector2 _scrollPos;
        private Dictionary<int, Vector2> _thumbScrolls = new Dictionary<int, Vector2>();
        private Dictionary<int, HashSet<int>> _selectedForDeletion = new Dictionary<int, HashSet<int>>();
        private Dictionary<string, Texture2D> _thumbnailCache = new Dictionary<string, Texture2D>();
        private const int THUMB_SIZE = 64;

        // ===== 依赖安装 =====
        private DependencyInstaller _installer;

        // ===== Tab 切换 =====
        private int _tabIndex = 0;
        private readonly string[] _tabNames = { "分组扫描", "以图搜图" };

        // ===== 以图搜图参数 =====
        private string _queryImagePath = "";      // 查询图片的绝对路径
        private int _topK = 50;                   // 最大返回结果数
        private int _queryPickerControlID;         // ObjectPicker 控件 ID

        // ===== 查询结果 =====
        private QueryResultData _queryResults;    // 查询结果

        // ==================================================================
        //  窗口生命周期
        // ==================================================================

        [MenuItem("Tools/查找相似图片")]
        public static void ShowWindow()
        {
            var window = GetWindow<SimilarityWindow>("相似图片检测");
            window.minSize = new Vector2(500, 400);
            window.Show();
        }

        private void OnEnable()
        {
            _runner = new PythonRunner();
            _runner.ProgressChanged += Repaint;
            _installer = new DependencyInstaller();
            _installer.OnCompleted += (success, msg) =>
            {
                if (success) _depsInstalled = true;
                _statusMessage = msg;
                _statusIsError = !success;
                Repaint();
            };

            // 默认扫描整个 Assets 目录
            if (string.IsNullOrEmpty(_folderPath))
                _folderPath = Application.dataPath;

            // 提前启动持久化 Python 会话，后台加载 TF 模型
            if (PythonLocator.GetPythonPath() != null)
                _ = PythonSession.Instance;

            CheckEnvironment();
        }

        private void OnDisable()
        {
            _runner?.Cancel();
            ClearThumbnailCache();
        }

        /// <summary>
        /// 检测运行环境：Python 是否可用、依赖是否安装、FR2 是否就绪、是否有缓存。
        /// Python 路径和依赖状态的结果会缓存在 EditorPrefs 中。
        /// </summary>
        private void CheckEnvironment()
        {
            string pyPath = PythonLocator.GetPythonPath();
            _pythonVersion = pyPath != null ? PythonLocator.GetPythonVersion() : null;

            // 首次检查依赖时为异步（耗时 1~15 秒），显示"检查中"状态
            if (pyPath != null && !PythonLocator.WereDependenciesChecked())
            {
                _checkingDeps = true;
                EditorApplication.delayCall += () =>
                {
                    _depsInstalled = PythonLocator.AreDependenciesInstalled();
                    _checkingDeps = false;
                    PythonLocator.MarkDependenciesChecked();
                    Repaint();
                };
            }
            else if (pyPath != null)
            {
                _depsInstalled = PythonLocator.AreDependenciesInstalled();
            }

            _fr2Ready = FR2Integration.IsReady;
            CheckCache();
        }

        // ==================================================================
        //  Tab 标签栏
        // ==================================================================

        /// <summary>
        /// 绘制顶部 Tab 标签栏，切换"分组扫描"和"以图搜图"。
        /// </summary>
        private void DrawTabBar()
        {
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);
            _tabIndex = GUILayout.Toolbar(_tabIndex, _tabNames, GUILayout.Height(28));
            GUILayout.FlexibleSpace();
            EditorGUILayout.EndHorizontal();
        }

        /// <summary>
        /// 处理 Unity 原生 ObjectPicker 的选择事件。
        /// 当用户在 Sprite 选择器中选定资源后，解析为绝对路径。
        /// </summary>
        private void HandleObjectPicker()
        {
            if (Event.current == null) return;

            string cmd = Event.current.commandName;
            if (cmd == "ObjectSelectorUpdated" || cmd == "ObjectSelectorClosed")
            {
                int pickedControlID = EditorGUIUtility.GetObjectPickerControlID();
                if (pickedControlID == _queryPickerControlID && _queryPickerControlID != 0)
                {
                    var picked = EditorGUIUtility.GetObjectPickerObject() as Sprite;
                    if (picked != null)
                    {
                        string assetPath = AssetDatabase.GetAssetPath(picked);
                        if (!string.IsNullOrEmpty(assetPath))
                            _queryImagePath = Path.GetFullPath(Path.Combine(
                                Application.dataPath, "..", assetPath));
                    }
                    if (cmd == "ObjectSelectorClosed")
                        _queryPickerControlID = 0;
                    Repaint();
                }
            }
        }

        // ==================================================================
        //  主 GUI 布局
        // ==================================================================

        private void OnGUI()
        {
            CheckCache();               // 检测文件夹是否改变，刷新缓存状态
            DrawEnvironmentBar();        // 顶部环境状态栏
            DrawInstallLog();            // 依赖安装日志（仅安装中可见）
            EditorGUILayout.Space(5);
            DrawTabBar();                // Tab 标签栏
            EditorGUILayout.Space(5);

            if (_tabIndex == 0)
                DrawScanTab();
            else
                DrawQueryTab();

            // 处理 ObjectPicker 回调
            HandleObjectPicker();
        }

        // ==================================================================
        //  顶部环境状态栏
        // ==================================================================

        /// <summary>
        /// 绘制三色状态栏：Python 版本、依赖状态、FR2 状态。
        /// 绿色 = 就绪，黄色 = 检查中/缓存为空，红色 = 未安装/缺失。
        /// 未安装时提供"配置 Python"或"安装依赖"按钮。
        /// </summary>
        private void DrawEnvironmentBar()
        {
            EditorGUILayout.BeginHorizontal(EditorStyles.helpBox);

            bool pythonOk = !string.IsNullOrEmpty(_pythonVersion);
            bool depsOk = _depsInstalled;

            // Python 状态
            GUI.color = pythonOk ? Color.green : Color.red;
            EditorGUILayout.LabelField(pythonOk ? $" Python {_pythonVersion}" : " 未找到 Python",
                GUILayout.Width(200));

            // 依赖状态
            GUI.color = depsOk ? Color.green : (_checkingDeps ? Color.yellow : Color.red);
            EditorGUILayout.LabelField(
                _checkingDeps ? " 正在检查依赖..." :
                depsOk ? " 依赖已就绪" : " 缺少依赖",
                GUILayout.Width(180));

            // FR2 状态
            bool fr2Installed = FR2Integration.HasFR2();
            GUI.color = _fr2Ready ? Color.green : (fr2Installed ? Color.yellow : Color.red);
            EditorGUILayout.LabelField(
                _fr2Ready ? " FR2 已就绪" :
                fr2Installed ? " FR2 缓存为空" : " FR2 未安装",
                GUILayout.Width(160));

            GUI.color = Color.white;
            GUILayout.FlexibleSpace();

            // 操作按钮
            if (!pythonOk)
            {
                if (GUILayout.Button("配置 Python...", GUILayout.Width(140)))
                {
                    string selected = EditorUtility.OpenFilePanel("选择 Python 可执行文件",
                        "", Application.platform == RuntimePlatform.WindowsEditor ? "exe" : "");
                    if (!string.IsNullOrEmpty(selected) && PythonLocator.SetCustomPath(selected))
                    {
                        CheckEnvironment();
                    }
                }
            }
            else if (!depsOk && !_checkingDeps)
            {
                GUI.enabled = !_installer.IsInstalling;
                if (GUILayout.Button(_installer.IsInstalling ? "安装中..." : "安装依赖", GUILayout.Width(140)))
                {
                    InstallDependencies();
                }
                GUI.enabled = true;
            }

            EditorGUILayout.EndHorizontal();
        }

        // ==================================================================
        //  依赖安装日志
        // ==================================================================

        /// <summary>
        /// 如果正在安装依赖，显示进度条和实时滚动日志。
        /// 安装完成或出错后出现"关闭"按钮。
        /// </summary>
        private void DrawInstallLog()
        {
            if (!_installer.IsInstalling) return;

            EditorGUILayout.Space(3);
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("安装日志", EditorStyles.boldLabel);
            GUILayout.FlexibleSpace();
            if (_installer.Progress >= 1f || string.IsNullOrEmpty(_installer.Log))
            {
                // 已完成或出错时可关闭
            }
            if (_installer.Progress >= 1f || _installer.Log.StartsWith("错误"))
            {
                if (GUILayout.Button("关闭", EditorStyles.miniButton, GUILayout.Width(40)))
                {
                    _installer.Close();
                    EditorGUILayout.EndHorizontal();
                    EditorGUILayout.EndVertical();
                    return;
                }
            }
            EditorGUILayout.EndHorizontal();

            Rect barRect = EditorGUILayout.GetControlRect(false, 22);
            EditorGUI.ProgressBar(barRect, _installer.Progress,
                _installer.Progress >= 1f ? "安装完成" :
                _installer.Progress >= 0.7f ? "正在安装包..." :
                _installer.Progress > 0f ? "正在下载..." : "准备中...");

            if (!string.IsNullOrEmpty(_installer.Log))
            {
                float logHeight = Mathf.Min(200, EditorGUIUtility.currentViewWidth * 0.4f);
                var scrollPos = _installer.LogScrollPos;
                _installer.LogScrollPos = EditorGUILayout.BeginScrollView(
                    scrollPos, GUILayout.Height(logHeight));
                EditorGUILayout.TextArea(_installer.Log, EditorStyles.label, GUILayout.ExpandHeight(true));
                EditorGUILayout.EndScrollView();
            }

            EditorGUILayout.EndVertical();
        }

        // ==================================================================
        //  分组扫描 Tab
        // ==================================================================

        /// <summary>
        /// 绘制"分组扫描"Tab 的全部内容。
        /// </summary>
        private void DrawScanTab()
        {
            DrawSettings();
            EditorGUILayout.Space(5);
            DrawScanControls();
            EditorGUILayout.Space(5);
            DrawResults();
        }

        // ==================================================================
        //  扫描设置
        // ==================================================================

        /// <summary>
        /// 绘制扫描参数区域：文件夹选择、相似度阈值、递归开关、线程数。
        /// </summary>
        private void DrawSettings()
        {
            EditorGUILayout.LabelField("扫描设置", EditorStyles.boldLabel);

            // 文件夹选择
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("文件夹:", GUILayout.Width(60));
            _folderPath = EditorGUILayout.TextField(_folderPath);
            if (GUILayout.Button("浏览", GUILayout.Width(70)))
            {
                string selected = EditorUtility.OpenFolderPanel("选择要扫描的文件夹", _folderPath, "");
                if (!string.IsNullOrEmpty(selected))
                    _folderPath = selected;
            }
            EditorGUILayout.EndHorizontal();

            // 相似度阈值滑块（0~1）
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("相似度阈值:", GUILayout.Width(80));
            _threshold = EditorGUILayout.Slider(_threshold, 0f, 1.00f);
            EditorGUILayout.LabelField(_threshold.ToString("F3"), GUILayout.Width(40));
            EditorGUILayout.EndHorizontal();

            // 递归 + 线程数
            EditorGUILayout.BeginHorizontal();
            _recursive = EditorGUILayout.ToggleLeft("递归子目录", _recursive, GUILayout.Width(100));
            EditorGUILayout.LabelField("线程数:", GUILayout.Width(60));
            _workers = EditorGUILayout.IntSlider(_workers, 1, 16);
            EditorGUILayout.EndHorizontal();
        }

        // ==================================================================
        //  扫描控制
        // ==================================================================

        /// <summary>
        /// 绘制扫描按钮、取消按钮、进度条、状态消息以及缓存加载提示。
        /// </summary>
        private void DrawScanControls()
        {
            EditorGUILayout.BeginHorizontal();

            bool canScan = !string.IsNullOrEmpty(_pythonVersion) && _depsInstalled && !_runner.IsRunning;

            GUI.enabled = canScan;
            if (GUILayout.Button("开始扫描", GUILayout.Height(30), GUILayout.Width(120)))
            {
                StartScan();
            }
            GUI.enabled = true;

            if (_runner.IsRunning)
            {
                if (GUILayout.Button("取消", GUILayout.Height(30), GUILayout.Width(80)))
                {
                    _runner.Cancel();
                    _statusMessage = "扫描已取消。";
                    _statusIsError = false;
                    Repaint();
                }
            }

            EditorGUILayout.EndHorizontal();

            // 扫描进度条
            if (_runner.IsRunning)
            {
                Rect r = EditorGUILayout.GetControlRect(false, 20);
                EditorGUI.ProgressBar(r, _runner.Progress, $"正在扫描... {(_runner.Progress * 100f):F0}%");
            }

            // 状态消息
            if (!string.IsNullOrEmpty(_statusMessage))
            {
                GUI.color = _statusIsError ? Color.red : Color.white;
                EditorGUILayout.LabelField(_statusMessage, EditorStyles.wordWrappedLabel);
                GUI.color = Color.white;
            }

            // 缓存可用提示（无缓存结果时显示加载按钮）
            if (_hasCache && _results == null && !_runner.IsRunning)
            {
                EditorGUILayout.BeginHorizontal(EditorStyles.helpBox);
                GUI.color = new Color(0.5f, 0.8f, 0.5f);
                EditorGUILayout.LabelField($" 上次扫描缓存可用 — {_cacheInfo}", GUILayout.ExpandWidth(true));
                GUI.color = Color.white;
                if (GUILayout.Button("加载缓存", GUILayout.Width(80), GUILayout.Height(20)))
                {
                    LoadCache();
                }
                EditorGUILayout.EndHorizontal();
            }

            // 缓存已加载提示
            if (_results != null && _hasCache && !_runner.IsRunning)
            {
                EditorGUILayout.BeginHorizontal();
                GUILayout.FlexibleSpace();
                GUI.color = Color.gray;
                bool fromCache = _statusMessage != null && _statusMessage.Contains("缓存");
                if (GUILayout.Button(fromCache ? "这些是缓存数据，点击重新扫描" : "清除缓存", EditorStyles.miniLabel))
                {
                    DeleteCache();
                    _results = null;
                    _statusMessage = "缓存已清除，可以重新扫描。";
                    Repaint();
                }
                GUI.color = Color.white;
                EditorGUILayout.EndHorizontal();
            }
        }

        // ==================================================================
        //  结果展示
        // ==================================================================

        /// <summary>
        /// 绘制所有结果分组。如果没有结果则什么都不显示。
        /// </summary>
        private void DrawResults()
        {
            if (_results == null || _results.groups == null || _results.groups.Count == 0)
                return;

            EditorGUILayout.LabelField("检测结果", EditorStyles.boldLabel);
            EditorGUILayout.LabelField(
                $"在 {_results.total_images} 张图片中找到 {_results.total_groups} 组相似图片 " +
                $"(耗时 {_results.elapsed_seconds:F1} 秒)。");

            EditorGUILayout.Space(5);

            _scrollPos = EditorGUILayout.BeginScrollView(_scrollPos);

            for (int gi = 0; gi < _results.groups.Count; gi++)
            {
                DrawGroupCard(_results.groups[gi]);
                EditorGUILayout.Space(8);
            }

            EditorGUILayout.EndScrollView();
        }

        /// <summary>
        /// 绘制单组相似图片卡片。
        /// 包含：组头、水平滚动缩略图行（支持选择 + FR2 角标）、
        /// 路径列表、"定位"按钮、自动选择重复项、删除选中资产。
        /// </summary>
        private void DrawGroupCard(DuplicateGroup group)
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);

            // 组头
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField($"第 {group.id} 组", EditorStyles.boldLabel, GUILayout.Width(80));
            EditorGUILayout.LabelField($"{group.images.Count} 张图片");
            GUILayout.FlexibleSpace();

            // 自动选择重复项（启发式：保留文件最大且文件名不含 Copy 的）
            if (GUILayout.Button("自动选择重复项", GUILayout.Width(150)))
            {
                AutoSelectDuplicates(group);
            }
            EditorGUILayout.EndHorizontal();

            // 缩略图行 — 水平滚动，支持任意数量图片
            float thumbSlotWidth = THUMB_SIZE + 12;
            float rowWidth = group.images.Count * thumbSlotWidth + 4;

            if (!_thumbScrolls.ContainsKey(group.id))
                _thumbScrolls[group.id] = Vector2.zero;
            var scroll = _thumbScrolls[group.id];
            _thumbScrolls[group.id] = EditorGUILayout.BeginScrollView(
                scroll, false, true, GUILayout.Height(THUMB_SIZE + 40));
            EditorGUILayout.BeginHorizontal(GUILayout.Width(rowWidth));

            for (int i = 0; i < group.images.Count; i++)
            {
                bool isSelected = IsSelected(group.id, i);

                EditorGUILayout.BeginVertical(GUILayout.Width(THUMB_SIZE + 8));

                // 选择框 + 文件名
                EditorGUILayout.BeginHorizontal();
                bool newSelected = EditorGUILayout.Toggle(isSelected, GUILayout.Width(16));
                if (newSelected != isSelected)
                    ToggleSelection(group.id, i);
                EditorGUILayout.LabelField(Path.GetFileName(group.images[i]), GUILayout.Width(THUMB_SIZE - 8));
                EditorGUILayout.EndHorizontal();

                // 缩略图（保宽高比）
                Texture2D thumb = GetThumbnail(group.images[i]);
                Rect thumbRect = GUILayoutUtility.GetRect(THUMB_SIZE, THUMB_SIZE,
                    GUILayout.Width(THUMB_SIZE), GUILayout.Height(THUMB_SIZE));
                EditorGUI.DrawRect(thumbRect, new Color(0.2f, 0.2f, 0.2f, 0.5f));

                if (thumb != null)
                {
                    float texAspect = (float)thumb.width / Mathf.Max(1, thumb.height);
                    float drawW, drawH;
                    if (texAspect >= 1f) { drawW = THUMB_SIZE; drawH = THUMB_SIZE / texAspect; }
                    else { drawH = THUMB_SIZE; drawW = THUMB_SIZE * texAspect; }
                    Rect drawRect = new Rect(
                        thumbRect.x + (THUMB_SIZE - drawW) / 2f,
                        thumbRect.y + (THUMB_SIZE - drawH) / 2f,
                        drawW, drawH);
                    GUI.DrawTexture(drawRect, thumb, ScaleMode.StretchToFill);
                }
                else
                {
                    GUI.Label(thumbRect, "?", EditorStyles.centeredGreyMiniLabel);
                }

                // 点击缩略图 → 打开大图预览窗口
                if (GUI.Button(thumbRect, GUIContent.none, GUIStyle.none))
                {
                    ImagePreviewWindow.Open(group, i, onRefreshParent: () => Repaint());
                }

                // FR2 引用数角标
                FR2Integration.DrawRefCountBadge(thumbRect, group.images[i]);

                EditorGUILayout.EndVertical();
                GUILayout.Space(4);
            }
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.EndScrollView();

            // 路径列表（每张图可定位）
            EditorGUILayout.LabelField("路径:", EditorStyles.miniLabel);
            foreach (var img in group.images)
            {
                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.LabelField("  " + img, EditorStyles.miniLabel);
                if (GUILayout.Button("定位", EditorStyles.miniButton, GUILayout.Width(40)))
                {
                    PluginUtils.PingAsset(img);
                }
                EditorGUILayout.EndHorizontal();
            }

            // 删除选中资产按钮
            EditorGUILayout.BeginHorizontal();
            int selectedCount = GetSelectedCount(group.id);
            GUI.enabled = selectedCount > 0 && IsInProjectAssets(group);
            if (GUILayout.Button($"删除 {selectedCount} 个选中资产", GUILayout.Height(25)))
            {
                DeleteSelected(group);
            }
            GUI.enabled = true;
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.EndVertical();
        }

        // ==================================================================
        //  扫描操作
        // ==================================================================

        /// <summary>
        /// 启动扫描。先验证文件夹存在，然后通过 PythonRunner 异步启动 Python 子进程。
        /// 完成后自动保存结果到缓存。
        /// </summary>
        private void StartScan()
        {
            if (!Directory.Exists(_folderPath))
            {
                _statusMessage = $"文件夹不存在: {_folderPath}";
                _statusIsError = true;
                return;
            }

            _results = null;
            _selectedForDeletion.Clear();
            ClearThumbnailCache();
            _statusMessage = "";
            _statusIsError = false;

            _runner.StartScan(
                folderPath: _folderPath,
                threshold: _threshold,
                recursive: _recursive,
                workers: _workers,
                cacheFeaturesDir: Path.Combine(CacheDir, "features"),
                onComplete: result =>
                {
                    _results = result;
                    _statusMessage = $"扫描完成：找到 {result.total_groups} 组相似图片。";
                    _statusIsError = false;
                    SaveCache(result);
                    Repaint();
                },
                onError: error =>
                {
                    _statusMessage = error;
                    _statusIsError = true;
                    Repaint();
                }
            );
        }

        /// <summary>
        /// 启发式自动选择重复项。
        /// 规则：文件体积最大的优先保留；文件名含 "Copy" 或 "_old" 的降权。
        /// 选中组内除"最佳"图片外的所有图片。
        /// </summary>
        private void AutoSelectDuplicates(DuplicateGroup group)
        {
            int bestIndex = 0;
            long bestScore = 0;

            for (int i = 0; i < group.images.Count; i++)
            {
                long score = 0;
                try
                {
                    var fi = new FileInfo(group.images[i]);
                    score = fi.Exists ? fi.Length : 0;

                    // 文件名含 Copy 或 _old 的减半（可能是副本）
                    string name = Path.GetFileNameWithoutExtension(group.images[i]);
                    if (name.Contains("Copy") || name.Contains("copy")
                        || name.EndsWith("_old") || name.EndsWith("_Old"))
                        score /= 2;
                }
                catch { }

                if (score > bestScore)
                {
                    bestScore = score;
                    bestIndex = i;
                }
            }

            // 选中除最佳外的所有图片
            for (int i = 0; i < group.images.Count; i++)
            {
                SetSelection(group.id, i, i != bestIndex);
            }

            Repaint();
        }

        /// <summary>
        /// 删除组内选中的图片资产。
        /// 仅支持项目 Assets 目录内的文件（通过 AssetDatabase.DeleteAsset），
        /// 外部文件需手动删除。操作可撤销（Ctrl+Z）。
        /// </summary>
        private void DeleteSelected(DuplicateGroup group)
        {
            if (!IsInProjectAssets(group))
            {
                EditorUtility.DisplayDialog("无法删除",
                    "部分图片不在项目 Assets 文件夹内，请通过文件管理器手动删除。", "确定");
                return;
            }

            var toDelete = new List<string>();
            for (int i = 0; i < group.images.Count; i++)
            {
                if (IsSelected(group.id, i))
                    toDelete.Add(group.images[i]);
            }

            if (toDelete.Count == 0) return;

            string msg = $"确认删除 {toDelete.Count} 张图片？\n\n文件将移至回收站，可通过 Ctrl+Z 撤销此操作。";
            if (!EditorUtility.DisplayDialog("确认删除", msg, "删除", "取消"))
                return;

            // 批量删除以减少资源刷新次数
            AssetDatabase.StartAssetEditing();
            try
            {
                foreach (var path in toDelete)
                {
                    string assetPath = PluginUtils.AbsoluteToAssetPath(path);
                    if (!string.IsNullOrEmpty(assetPath))
                    {
                        AssetDatabase.DeleteAsset(assetPath);
                    }
                }
            }
            finally
            {
                AssetDatabase.StopAssetEditing();
                AssetDatabase.Refresh();
            }

            group.images.RemoveAll(img => toDelete.Contains(img));
            ClearSelection(group.id);
            _statusMessage = $"已删除 {toDelete.Count} 个资产。";
            _statusIsError = false;
            Repaint();
        }

        // ==================================================================
        //  依赖安装
        // ==================================================================

        /// <summary>
        /// 启动 pip install 安装缺失的 Python 依赖。
        /// 委托给 DependencyInstaller，通过 OnCompleted 回调接收结果。
        /// </summary>
        private void InstallDependencies()
        {
            _statusMessage = "";
            _statusIsError = false;
            string pythonPath = PythonLocator.GetPythonPath();
            string reqPath = Path.Combine(PythonRunner.GetPythonScriptsDir(), "requirements.txt");
            _installer.StartInstall(pythonPath, reqPath);
            Repaint();
        }

        /// <summary>
        /// 关闭安装日志面板。
        /// </summary>
        private void CloseInstallLog()
        {
            _installer.Close();
        }

        // ==================================================================
        //  缓存
        // ==================================================================

        /// <summary>
        /// 检测当前文件夹是否有对应的扫描缓存。
        /// 每种文件夹对应一个独立的缓存文件（文件名基于路径哈希）。
        /// 如果文件夹未变则跳过检测。
        /// </summary>
        private void CheckCache()
        {
            if (_folderPath == _lastCheckedFolder) return;
            _lastCheckedFolder = _folderPath;

            _cachePath = GetCacheFilePath();
            if (File.Exists(_cachePath))
            {
                try
                {
                    string json = File.ReadAllText(_cachePath, Encoding.UTF8);
                    var meta = JsonUtility.FromJson<CacheMeta>(json);
                    if (meta != null && meta.folder == _folderPath)
                    {
                        _hasCache = true;
                        _cacheInfo = $"{meta.total_groups} 组 | 阈值: {meta.threshold:F2} | {meta.date}";
                        return;
                    }
                }
                catch { }
            }
            _hasCache = false;
            _cacheInfo = "";
        }

        /// <summary>
        /// 保存扫描结果到缓存。
        /// 同时写入元数据（CacheMeta）和完整结果（_result.json）两个文件。
        /// </summary>
        private void SaveCache(ScanResultData result)
        {
            try
            {
                Directory.CreateDirectory(CacheDir);
                var meta = JsonUtility.ToJson(new CacheMeta
                {
                    folder = _folderPath,
                    threshold = _threshold,
                    total_images = result.total_images,
                    total_groups = result.total_groups,
                    date = DateTime.Now.ToString("yyyy-MM-dd HH:mm"),
                }, true);
                File.WriteAllText(_cachePath, meta, Encoding.UTF8);

                string resultPath = _cachePath.Replace(".json", "_result.json");
                string resultJson = JsonUtility.ToJson(result, true);
                File.WriteAllText(resultPath, resultJson, Encoding.UTF8);

                _hasCache = true;
                _cacheInfo = $"{result.total_groups} 组 | 阈值: {_threshold:F2} | {DateTime.Now:yyyy-MM-dd HH:mm}";
            }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogWarning($"[ImageSimilarityPlugin] 缓存保存失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 从缓存加载扫描结果，恢复上次的完整 UI 状态。
        /// 加载前清除选择状态和缩略图缓存。
        /// </summary>
        private void LoadCache()
        {
            string resultPath = _cachePath.Replace(".json", "_result.json");
            if (!File.Exists(resultPath)) return;

            try
            {
                string json = File.ReadAllText(resultPath, Encoding.UTF8);
                _results = JsonUtility.FromJson<ScanResultData>(json);
                if (_results != null)
                {
                    _selectedForDeletion.Clear();
                    ClearThumbnailCache();
                    _statusMessage = $"已从缓存加载 — {_cacheInfo}";
                    _statusIsError = false;
                    Repaint();
                }
            }
            catch (Exception ex)
            {
                _statusMessage = $"缓存加载失败: {ex.Message}";
                _statusIsError = true;
            }
        }

        /// <summary>
        /// 删除当前文件夹对应的所有缓存文件。
        /// </summary>
        private void DeleteCache()
        {
            try
            {
                if (File.Exists(_cachePath)) File.Delete(_cachePath);
                string resultPath = _cachePath?.Replace(".json", "_result.json");
                if (resultPath != null && File.Exists(resultPath)) File.Delete(resultPath);
            }
            catch { }
            _hasCache = false;
            _cacheInfo = "";
        }

        // ==================================================================
        //  以图搜图 Tab
        // ==================================================================

        /// <summary>
        /// 绘制"以图搜图"Tab 的全部内容。
        /// </summary>
        private void DrawQueryTab()
        {
            DrawQuerySettings();
            EditorGUILayout.Space(5);
            DrawQueryControls();
            EditorGUILayout.Space(5);
            DrawQueryResults();
        }

        /// <summary>
        /// 绘制查询参数区域：查询图片选择（文件浏览器 + 拖入区）、
        /// 目标文件夹、相似度阈值、最大结果数。
        /// </summary>
        private void DrawQuerySettings()
        {
            EditorGUILayout.LabelField("查询设置", EditorStyles.boldLabel);

            // 查询图片 — Unity 原生 ObjectPicker
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("查询图片:", GUILayout.Width(60));

            string displayName = string.IsNullOrEmpty(_queryImagePath)
                ? "未选择"
                : Path.GetFileName(_queryImagePath);
            EditorGUILayout.LabelField(displayName, EditorStyles.boldLabel);

            if (GUILayout.Button("从项目中选择...", GUILayout.Width(120)))
            {
                _queryPickerControlID = GUIUtility.GetControlID(FocusType.Passive);
                EditorGUIUtility.ShowObjectPicker<Sprite>(null, false, "", _queryPickerControlID);
            }

            if (!string.IsNullOrEmpty(_queryImagePath) && GUILayout.Button("×", GUILayout.Width(25)))
            {
                _queryImagePath = "";
                Repaint();
            }

            EditorGUILayout.EndHorizontal();

            // 目标文件夹
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("目标文件夹:", GUILayout.Width(80));
            _folderPath = EditorGUILayout.TextField(_folderPath);
            if (GUILayout.Button("浏览", GUILayout.Width(70)))
            {
                string selected = EditorUtility.OpenFolderPanel("选择目标文件夹", _folderPath, "");
                if (!string.IsNullOrEmpty(selected))
                    _folderPath = selected;
            }
            EditorGUILayout.EndHorizontal();

            // 相似度阈值 + Top-K
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("相似度阈值:", GUILayout.Width(80));
            _threshold = EditorGUILayout.Slider(_threshold, 0f, 1.00f);
            EditorGUILayout.LabelField(_threshold.ToString("F3"), GUILayout.Width(40));
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("最大结果数:", GUILayout.Width(80));
            _topK = EditorGUILayout.IntSlider(_topK, 1, 200);
            _recursive = EditorGUILayout.ToggleLeft("递归子目录", _recursive, GUILayout.Width(100));
            EditorGUILayout.LabelField("线程数:", GUILayout.Width(60));
            _workers = EditorGUILayout.IntSlider(_workers, 1, 16);
            EditorGUILayout.EndHorizontal();
        }

        /// <summary>
        /// 绘制查询按钮、取消按钮、进度条和状态消息。
        /// </summary>
        private void DrawQueryControls()
        {
            EditorGUILayout.BeginHorizontal();

            bool canQuery = !string.IsNullOrEmpty(_pythonVersion) && _depsInstalled
                && !_runner.IsRunning
                && !string.IsNullOrEmpty(_queryImagePath)
                && File.Exists(_queryImagePath);

            GUI.enabled = canQuery;
            if (GUILayout.Button("开始搜索", GUILayout.Height(30), GUILayout.Width(120)))
            {
                StartQuery();
            }
            GUI.enabled = true;

            if (_runner.IsRunning)
            {
                if (GUILayout.Button("取消", GUILayout.Height(30), GUILayout.Width(80)))
                {
                    _runner.Cancel();
                    _statusMessage = "查询已取消。";
                    _statusIsError = false;
                    Repaint();
                }
            }

            EditorGUILayout.EndHorizontal();

            // 进度条
            if (_runner.IsRunning)
            {
                Rect r = EditorGUILayout.GetControlRect(false, 20);
                EditorGUI.ProgressBar(r, _runner.Progress, $"正在搜索... {(_runner.Progress * 100f):F0}%");
            }

            // 状态消息
            if (!string.IsNullOrEmpty(_statusMessage))
            {
                GUI.color = _statusIsError ? Color.red : Color.white;
                EditorGUILayout.LabelField(_statusMessage, EditorStyles.wordWrappedLabel);
                GUI.color = Color.white;
            }
        }

        /// <summary>
        /// 启动以图搜图查询。
        /// 验证查询图片和目标文件夹存在后，通过 PythonRunner 异步执行。
        /// </summary>
        private void StartQuery()
        {
            if (!File.Exists(_queryImagePath))
            {
                _statusMessage = $"查询图片不存在: {_queryImagePath}";
                _statusIsError = true;
                return;
            }
            if (!Directory.Exists(_folderPath))
            {
                _statusMessage = $"目标文件夹不存在: {_folderPath}";
                _statusIsError = true;
                return;
            }

            _results = null;
            _queryResults = null;
            ClearThumbnailCache();
            _statusMessage = "";
            _statusIsError = false;

            _runner.StartQuery(
                queryImagePath: _queryImagePath,
                folderPath: _folderPath,
                threshold: _threshold,
                topK: _topK,
                recursive: _recursive,
                workers: _workers,
                useCache: true,
                onComplete: result =>
                {
                    _queryResults = result;
                    float topScore = result.results.Count > 0 ? result.results[0].similarity : 0f;
                    _statusMessage = $"搜索完成：在 {result.total_images} 张图片中找到 {result.results.Count} 张相似图片" +
                                     (result.results.Count > 0 ? $" (最高相似度: {topScore:P1})" : "");
                    _statusIsError = false;
                    Repaint();
                },
                onError: error =>
                {
                    _statusMessage = error;
                    _statusIsError = true;
                    Repaint();
                }
            );
        }

        /// <summary>
        /// 绘制查询结果区域。展示查询摘要和按相似度降序的结果列表。
        /// </summary>
        private void DrawQueryResults()
        {
            if (_queryResults == null || _queryResults.results == null || _queryResults.results.Count == 0)
                return;

            EditorGUILayout.LabelField("查询结果", EditorStyles.boldLabel);

            // 摘要行
            string summary = $"查询图片: {Path.GetFileName(_queryResults.query_image)}  |  " +
                             $"在 {_queryResults.total_images} 张目标图片中命中 {_queryResults.results.Count} 张  |  " +
                             $"耗时 {_queryResults.elapsed_seconds:F1} 秒  |  阈值: {_queryResults.threshold:F2}";
            EditorGUILayout.LabelField(summary, EditorStyles.miniLabel);

            EditorGUILayout.Space(5);

            _scrollPos = EditorGUILayout.BeginScrollView(_scrollPos);

            for (int i = 0; i < _queryResults.results.Count; i++)
            {
                DrawQueryResultRow(_queryResults.results[i]);
                EditorGUILayout.Space(4);
            }

            EditorGUILayout.EndScrollView();
        }

        /// <summary>
        /// 绘制单条查询结果行。
        /// 横向卡片：排名号 → 缩略图 → 文件信息 → 相似度分数条 → 定位按钮。
        /// </summary>
        private void DrawQueryResultRow(SimilarImage img)
        {
            EditorGUILayout.BeginHorizontal(EditorStyles.helpBox);

            // 排名标签
            var rankStyle = new GUIStyle(EditorStyles.boldLabel)
            {
                alignment = TextAnchor.MiddleCenter,
                fontSize = 14,
            };
            EditorGUILayout.LabelField($"#{img.rank}", rankStyle, GUILayout.Width(40));

            // 缩略图
            Texture2D thumb = GetThumbnail(img.image_path);
            Rect thumbRect = GUILayoutUtility.GetRect(THUMB_SIZE, THUMB_SIZE,
                GUILayout.Width(THUMB_SIZE), GUILayout.Height(THUMB_SIZE));
            EditorGUI.DrawRect(thumbRect, new Color(0.2f, 0.2f, 0.2f, 0.5f));
            if (thumb != null)
            {
                float texAspect = (float)thumb.width / Mathf.Max(1, thumb.height);
                float drawW, drawH;
                if (texAspect >= 1f) { drawW = THUMB_SIZE; drawH = THUMB_SIZE / texAspect; }
                else { drawH = THUMB_SIZE; drawW = THUMB_SIZE * texAspect; }
                Rect drawRect = new Rect(
                    thumbRect.x + (THUMB_SIZE - drawW) / 2f,
                    thumbRect.y + (THUMB_SIZE - drawH) / 2f,
                    drawW, drawH);
                GUI.DrawTexture(drawRect, thumb, ScaleMode.StretchToFill);
            }
            else
            {
                GUI.Label(thumbRect, "?", EditorStyles.centeredGreyMiniLabel);
            }

            // 点击缩略图定位
            if (GUI.Button(thumbRect, GUIContent.none, GUIStyle.none))
            {
                PluginUtils.PingAsset(img.image_path);
            }
            FR2Integration.DrawRefCountBadge(thumbRect, img.image_path);

            GUILayout.Space(8);

            // 中间：文件信息
            EditorGUILayout.BeginVertical();
            EditorGUILayout.LabelField(Path.GetFileName(img.image_path), EditorStyles.boldLabel);
            string assetPath = PluginUtils.AbsoluteToAssetPath(img.image_path);
            if (!string.IsNullOrEmpty(assetPath))
                EditorGUILayout.LabelField(assetPath, EditorStyles.miniLabel);
            else
                EditorGUILayout.LabelField(img.image_path, EditorStyles.miniLabel);
            try
            {
                var fi = new FileInfo(img.image_path);
                if (fi.Exists)
                    EditorGUILayout.LabelField(PluginUtils.FormatFileSize(fi.Length), EditorStyles.miniLabel);
            }
            catch { }
            EditorGUILayout.EndVertical();

            GUILayout.FlexibleSpace();

            // 右侧：相似度分数条
            EditorGUILayout.BeginVertical(GUILayout.Width(160));
            EditorGUILayout.LabelField($"相似度: {img.similarity:P2}", GUILayout.Width(120));

            Rect barRect = GUILayoutUtility.GetRect(120, 16);
            EditorGUI.DrawRect(new Rect(barRect.x, barRect.y, barRect.width, barRect.height),
                new Color(0.3f, 0.3f, 0.3f));
            Color barColor = img.similarity > 0.90f ? Color.green :
                             img.similarity > 0.80f ? new Color(1f, 0.8f, 0f) : Color.red;
            EditorGUI.DrawRect(new Rect(barRect.x, barRect.y, barRect.width * img.similarity, barRect.height),
                barColor);

            // 定位按钮
            if (GUILayout.Button("定位", GUILayout.Width(50)))
                PluginUtils.PingAsset(img.image_path);
            EditorGUILayout.EndVertical();

            EditorGUILayout.EndHorizontal();
        }

        /// <summary>
        /// 基于文件夹路径的稳定哈希生成缓存文件名。
        /// 不同文件夹对应不同的缓存，互不干扰。
        /// </summary>
        private string GetCacheFilePath()
        {
            string hash = _folderPath.GetHashCode().ToString("X8");
            return Path.Combine(CacheDir, $"scan_{hash}.json");
        }

        /// <summary>缓存的元数据结构（不包含完整分组信息）</summary>
        [Serializable]
        private class CacheMeta
        {
            public string folder;
            public float threshold;
            public int total_images;
            public int total_groups;
            public string date;
        }

        // ==================================================================
        //  工具方法
        // ==================================================================

        private bool IsSelected(int groupId, int imageIndex)
        {
            return _selectedForDeletion.TryGetValue(groupId, out var set) && set.Contains(imageIndex);
        }

        private void ToggleSelection(int groupId, int imageIndex)
        {
            if (!_selectedForDeletion.ContainsKey(groupId))
                _selectedForDeletion[groupId] = new HashSet<int>();

            if (_selectedForDeletion[groupId].Contains(imageIndex))
                _selectedForDeletion[groupId].Remove(imageIndex);
            else
                _selectedForDeletion[groupId].Add(imageIndex);
        }

        private void SetSelection(int groupId, int imageIndex, bool selected)
        {
            if (!_selectedForDeletion.ContainsKey(groupId))
                _selectedForDeletion[groupId] = new HashSet<int>();

            if (selected)
                _selectedForDeletion[groupId].Add(imageIndex);
            else
                _selectedForDeletion[groupId].Remove(imageIndex);
        }

        private int GetSelectedCount(int groupId)
        {
            return _selectedForDeletion.TryGetValue(groupId, out var set) ? set.Count : 0;
        }

        private void ClearSelection(int groupId)
        {
            _selectedForDeletion.Remove(groupId);
        }

        /// <summary>
        /// 判断组内所有图片是否都在项目 Assets 目录下。
        /// 如果是外部文件，不能使用 AssetDatabase 删除。
        /// </summary>
        private bool IsInProjectAssets(DuplicateGroup group)
        {
            string assetsRoot = Application.dataPath.Replace('/', Path.DirectorySeparatorChar);
            foreach (var img in group.images)
            {
                if (!Path.GetFullPath(img).StartsWith(assetsRoot, StringComparison.OrdinalIgnoreCase))
                    return false;
            }
            return true;
        }

        /// <summary>
        /// 获取缩略图纹理。
        /// 始终从原始文件字节加载，以保留原始尺寸，
        /// 避免 Unity 导入的 2 次幂纹理导致宽高比失真。
        /// 结果会缓存以加速重复绘制。
        /// </summary>
        private Texture2D GetThumbnail(string path)
        {
            if (_thumbnailCache.TryGetValue(path, out var cached))
                return cached;

            Texture2D tex = null;
            if (File.Exists(path))
            {
                try
                {
                    byte[] data = File.ReadAllBytes(path);
                    tex = new Texture2D(2, 2);
                    tex.LoadImage(data);
                }
                catch { }
            }

            _thumbnailCache[path] = tex;
            return tex;
        }

        /// <summary>清除缩略图缓存，释放所有内存中的纹理对象</summary>
        private void ClearThumbnailCache()
        {
            foreach (var tex in _thumbnailCache.Values)
            {
                if (tex != null)
                    DestroyImmediate(tex);
            }
            _thumbnailCache.Clear();
        }

    }
}

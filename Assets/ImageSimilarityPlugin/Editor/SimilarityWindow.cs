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
        private double _nextFr2RepaintTime = 0d;
        private bool CanStartRunner => !string.IsNullOrEmpty(_pythonVersion)
            && _depsInstalled
            && !_runner.IsRunning;

        // ===== 扫描结果缓存 =====
        private static string CacheDir => Path.Combine(Application.temporaryCachePath, "ImageSimilarityPlugin");
        private string _cachePath;         // 当前文件夹对应的缓存文件路径
        private string _lastCheckedFolder; // 上一次检查缓存的文件夹，用于检测变化
        private bool _lastCheckedRecursive;
        private string _lastCheckedExclusionScope;
        private string _lastFeatureCheckFolder; // 上一次检查特征缓存过期的文件夹
        private bool _lastFeatureCheckRecursive;
        private string _lastFeatureCheckExclusionScope;
        private bool _pendingFeatureCheck;  // 等待 session ready 后重试
        private bool _hasCache;            // 是否有有效缓存可用
        private string _cacheInfo;         // 缓存摘要信息（用于 UI 显示）
        private CacheInfo _featureCacheStaleness; // 特征缓存过期检测结果（null=未检查/无缓存）

        // ===== 结果 UI =====
        private Vector2 _scanScrollPos;
        private Vector2 _queryScrollPos;
        private GUIStyle _queryRankStyle;
        private Vector2 _pendingScrollPos;
        private bool _hasPendingScrollPos;
        private float _resultsViewportHeight;
        private float _pendingResultsViewportHeight;
        private readonly Dictionary<int, Vector2> _thumbScrolls = new Dictionary<int, Vector2>();
        private readonly Dictionary<int, Vector2> _pendingThumbScrolls = new Dictionary<int, Vector2>();
        private readonly Dictionary<int, float> _groupHeights = new Dictionary<int, float>();
        private readonly Dictionary<int, float> _pendingGroupHeights = new Dictionary<int, float>();
        private readonly Dictionary<int, HashSet<int>> _selectedForDeletion = new Dictionary<int, HashSet<int>>();
        private readonly Dictionary<string, Texture2D> _thumbnailCache = new Dictionary<string, Texture2D>();
        private string _groupKeywordInput = string.Empty;
        private string _appliedGroupKeyword = string.Empty;
        private List<DuplicateGroup> _filteredGroups;
        private const int ThumbnailSize = 64;
        private const float ThumbnailListHeight = ThumbnailSize + 40f;
        private const float GroupSpacing = 8f;

        // ===== 依赖安装 =====
        private DependencyInstaller _installer;

        // ===== Tab 切换 =====
        private int _tabIndex = 0;
        private readonly string[] _tabNames = { "分组扫描", "以图搜图" };

        // ===== 以图搜图参数 =====
        private string _queryImagePath = "";      // 查询图片的绝对路径
        private int _topK = 50;                   // 最大返回结果数
        private int _queryPickerControlID;         // ObjectPicker 控件 ID
        private bool _showExcludedDirectories = true;

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
                if (success)
                {
                    // pip 成功后用当前 Python 快速复检一次，避免窗口状态和实际解释器环境不一致。
                    _depsInstalled = PythonLocator.AreDependenciesInstalled();
                    PythonLocator.MarkDependenciesChecked();
                    if (!_depsInstalled)
                        msg = "依赖安装命令已完成，但当前 Python 仍检测不到所需模块，请检查顶部配置的 Python 路径。";
                }
                else
                {
                    _depsInstalled = false;
                }

                _statusMessage = msg;
                _statusIsError = !success || !_depsInstalled;
                if (_depsInstalled)
                    EnsurePythonSessionStarted();
                Repaint();
            };

            // 新建窗口恢复当前项目上次使用的目录；已序列化的有效窗口状态优先保留。
            if (string.IsNullOrEmpty(_folderPath) || !Directory.Exists(_folderPath))
                _folderPath = SearchDirectorySettings.GetDirectory();

            EditorApplication.update += RepaintWhileFR2Pending;
            CheckEnvironment();
        }

        private void OnDisable()
        {
            EditorApplication.update -= RepaintWhileFR2Pending;
            _runner?.Cancel();
            _installer?.Close();
            SearchDirectorySettings.Save(_folderPath);
            ClearThumbnailCache();
        }

        private void RepaintWhileFR2Pending()
        {
            if (EditorApplication.timeSinceStartup < _nextFr2RepaintTime) return;
            _nextFr2RepaintTime = EditorApplication.timeSinceStartup + 0.5d;

            var status = FR2Integration.GetStatus();
            if (status.Installed && !status.Ready)
                Repaint();
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
                    if (_depsInstalled)
                        EnsurePythonSessionStarted();
                    Repaint();
                };
            }
            else if (pyPath != null)
            {
                _depsInstalled = PythonLocator.AreDependenciesInstalled();
            }

            CheckCache();
            if (_depsInstalled)
                EnsurePythonSessionStarted();
        }

        /// <summary>
        /// 仅在依赖确认可用后启动 TensorFlow 服务，避免缺包时反复拉起失败进程。
        /// </summary>
        private void EnsurePythonSessionStarted()
        {
            if (!_depsInstalled || PythonLocator.GetPythonPath() == null) return;

            _ = PythonSession.Instance;
            _lastFeatureCheckFolder = null;
            TriggerFeatureCacheCheck();
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
            RetryPendingFeatureCheck(); // session 就绪后补发缓存过期检查
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
            var fr2Status = FR2Integration.GetStatus();
            GUI.color = fr2Status.Ready ? Color.green : (fr2Status.Installed ? Color.yellow : Color.red);
            EditorGUILayout.LabelField(new GUIContent(fr2Status.Label, fr2Status.Tooltip),
                GUILayout.Width(180));

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
            if (!_installer.IsPanelVisible) return;

            EditorGUILayout.Space(3);
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("安装日志", EditorStyles.boldLabel);
            GUILayout.FlexibleSpace();
            if (!_installer.IsInstalling)
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
            SetFolderPath(DrawEditablePathField(_folderPath));
            if (GUILayout.Button("浏览", GUILayout.Width(70)))
            {
                string selected = EditorUtility.OpenFolderPanel("选择要扫描的文件夹", _folderPath, "");
                if (!string.IsNullOrEmpty(selected))
                    SetFolderPath(selected);
            }
            EditorGUILayout.EndHorizontal();

            DrawExcludedDirectories();

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

            bool previousEnabled = GUI.enabled;
            GUI.enabled = previousEnabled && CanStartRunner;
            if (GUILayout.Button("开始扫描", GUILayout.Height(30), GUILayout.Width(120)))
            {
                StartScan();
            }
            GUI.enabled = previousEnabled;

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

            // 特征缓存过期警告
            DrawFeatureCacheWarning();

            // 扫描进度条
            if (_runner.IsRunning)
            {
                Rect r = EditorGUILayout.GetControlRect(false, 20);
                EditorGUI.ProgressBar(r, _runner.Progress, $"正在扫描... {(_runner.Progress * 100f):F0}%");
            }

            DrawStatusMessage();

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
                    ClearScanResults();
                    ClearThumbnailCache();
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

            // 缓存更新信息
            DrawCacheInfo(_results.cache_info);
            DrawGroupKeywordFilter();

            EditorGUILayout.Space(5);

            List<DuplicateGroup> displayedGroups = _filteredGroups ?? _results.groups;
            if (displayedGroups.Count == 0)
            {
                EditorGUILayout.LabelField(
                    $"没有图片名称包含“{_appliedGroupKeyword}”的分组。",
                    EditorStyles.wordWrappedLabel);
                return;
            }

            ApplyPendingVirtualizationState();

            Vector2 currentScroll = _scanScrollPos;
            Vector2 updatedScroll = EditorGUILayout.BeginScrollView(currentScroll);

            if (Event.current.type == EventType.Repaint)
            {
                Rect viewportRect = GUILayoutUtility.GetLastRect();
                if (viewportRect.height > 1f)
                    _pendingResultsViewportHeight = viewportRect.height;
            }

            float viewportHeight = _resultsViewportHeight > 1f
                ? _resultsViewportHeight
                : Mathf.Max(1f, position.height);
            float visibleTop = Mathf.Max(0f, currentScroll.y);
            float visibleBottom = visibleTop + viewportHeight;
            float groupTop = 0f;

            for (int gi = 0; gi < displayedGroups.Count; gi++)
            {
                DuplicateGroup group = displayedGroups[gi];
                float reservedHeight = GetGroupHeight(group);
                bool shouldDraw = groupTop + reservedHeight > visibleTop
                    && groupTop < visibleBottom;

                if (shouldDraw)
                {
                    float actualHeight = DrawGroupCard(group);
                    if (Event.current.type == EventType.Repaint && actualHeight > 0f)
                        _pendingGroupHeights[group.id] = actualHeight;
                }
                else
                {
                    // 只保留布局高度，不执行组内缩略图、路径控件和 FR2 查询。
                    GUILayout.Space(reservedHeight);
                }

                EditorGUILayout.Space(GroupSpacing);
                groupTop += reservedHeight + GroupSpacing;
            }

            EditorGUILayout.EndScrollView();

            if (updatedScroll != currentScroll)
            {
                // 滚动位置延迟到下一次 Layout 应用，确保同一事件的控件集合保持一致。
                _pendingScrollPos = updatedScroll;
                _hasPendingScrollPos = true;
                Repaint();
            }
        }

        /// <summary>绘制显式应用的图片名称关键词筛选工具栏。</summary>
        private void DrawGroupKeywordFilter()
        {
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);
            EditorGUILayout.LabelField("图片名称:", GUILayout.Width(64));
            _groupKeywordInput = EditorGUILayout.TextField(
                _groupKeywordInput,
                EditorStyles.toolbarTextField);

            if (GUILayout.Button("搜索", EditorStyles.toolbarButton, GUILayout.Width(52)))
                ApplyGroupKeywordFilter(_groupKeywordInput);

            bool previousEnabled = GUI.enabled;
            GUI.enabled = previousEnabled && !string.IsNullOrEmpty(_appliedGroupKeyword);
            if (GUILayout.Button("清除", EditorStyles.toolbarButton, GUILayout.Width(52)))
            {
                _groupKeywordInput = string.Empty;
                ApplyGroupKeywordFilter(string.Empty);
            }
            GUI.enabled = previousEnabled;
            EditorGUILayout.EndHorizontal();

            if (!string.IsNullOrEmpty(_appliedGroupKeyword))
            {
                int displayedCount = _filteredGroups?.Count ?? 0;
                EditorGUILayout.LabelField(
                    $"关键词“{_appliedGroupKeyword}”：显示 {displayedCount} / {_results.groups.Count} 组",
                    EditorStyles.miniLabel);
            }
        }

        private void ApplyGroupKeywordFilter(string keyword)
        {
            _appliedGroupKeyword = (keyword ?? string.Empty).Trim();
            RebuildGroupKeywordFilter();
            ResetScanResultViewState();
            Repaint();
        }

        /// <summary>仅在筛选提交或结果变化时重建，避免 OnGUI 每帧扫描所有文件名。</summary>
        private void RebuildGroupKeywordFilter()
        {
            if (_results?.groups == null || string.IsNullOrEmpty(_appliedGroupKeyword))
            {
                _filteredGroups = null;
                return;
            }

            var matches = new List<DuplicateGroup>();
            foreach (DuplicateGroup group in _results.groups)
            {
                if (group?.images == null) continue;
                foreach (string imagePath in group.images)
                {
                    if (string.IsNullOrEmpty(imagePath)) continue;
                    string fileName = Path.GetFileName(imagePath);
                    if (fileName.IndexOf(_appliedGroupKeyword, StringComparison.OrdinalIgnoreCase) < 0)
                        continue;
                    matches.Add(group);
                    break;
                }
            }
            _filteredGroups = matches;
        }

        /// <summary>
        /// 绘制单组相似图片卡片。
        /// 包含：组头、水平滚动缩略图行（支持选择 + FR2 角标）、
        /// 路径列表、"定位"按钮、自动选择重复项、删除选中资产。
        /// </summary>
        private float DrawGroupCard(DuplicateGroup group)
        {
            Rect groupRect = EditorGUILayout.BeginVertical(EditorStyles.helpBox);

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
            float thumbSlotWidth = ThumbnailSize + 12;
            float rowWidth = group.images.Count * thumbSlotWidth + 4;

            if (!_thumbScrolls.ContainsKey(group.id))
                _thumbScrolls[group.id] = Vector2.zero;
            var scroll = _thumbScrolls[group.id];
            scroll.y = 0f;
            Vector2 updatedScroll = GUILayout.BeginScrollView(
                scroll,
                true,
                false,
                GUI.skin.horizontalScrollbar,
                GUIStyle.none,
                GUILayout.Height(ThumbnailListHeight));
            updatedScroll.y = 0f;
            EditorGUILayout.BeginHorizontal(GUILayout.Width(rowWidth));

            float thumbnailViewportWidth = Mathf.Max(ThumbnailSize, position.width - 40f);
            int firstVisibleIndex = Mathf.Max(0, Mathf.FloorToInt(scroll.x / thumbSlotWidth));
            int lastVisibleIndex = Mathf.Clamp(
                Mathf.CeilToInt((scroll.x + thumbnailViewportWidth) / thumbSlotWidth),
                firstVisibleIndex,
                group.images.Count);

            if (firstVisibleIndex > 0)
                GUILayout.Space(firstVisibleIndex * thumbSlotWidth);

            for (int i = firstVisibleIndex; i < lastVisibleIndex; i++)
            {
                bool isSelected = IsSelected(group.id, i);

                EditorGUILayout.BeginVertical(GUILayout.Width(ThumbnailSize + 8));

                // 选择框 + 文件名
                EditorGUILayout.BeginHorizontal();
                bool newSelected = EditorGUILayout.Toggle(isSelected, GUILayout.Width(16));
                if (newSelected != isSelected)
                    ToggleSelection(group.id, i);
                string fileName = Path.GetFileName(group.images[i]);
                string displayPath = PluginUtils.ToDisplayPath(group.images[i]);
                EditorGUILayout.LabelField(
                    new GUIContent(fileName, displayPath),
                    GUILayout.Width(ThumbnailSize - 8));
                EditorGUILayout.EndHorizontal();

                // 缩略图（保宽高比）
                Texture2D thumb = GetThumbnail(group.images[i]);
                Rect thumbRect = GUILayoutUtility.GetRect(ThumbnailSize, ThumbnailSize,
                    GUILayout.Width(ThumbnailSize), GUILayout.Height(ThumbnailSize));
                DrawThumbnail(thumbRect, thumb);

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

            int trailingCount = group.images.Count - lastVisibleIndex;
            if (trailingCount > 0)
                GUILayout.Space(trailingCount * thumbSlotWidth);

            EditorGUILayout.EndHorizontal();
            GUILayout.EndScrollView();

            if (updatedScroll != scroll)
                _pendingThumbScrolls[group.id] = updatedScroll;

            // 路径列表（每张图可定位）
            EditorGUILayout.LabelField("路径:", EditorStyles.miniLabel);
            foreach (var img in group.images)
            {
                string displayPath = PluginUtils.ToDisplayPath(img);
                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.LabelField(
                    new GUIContent("  " + displayPath, displayPath),
                    EditorStyles.miniLabel);
                if (GUILayout.Button("定位", EditorStyles.miniButton, GUILayout.Width(40)))
                {
                    PluginUtils.PingAsset(img);
                }
                EditorGUILayout.EndHorizontal();
            }

            // 删除选中资产按钮
            EditorGUILayout.BeginHorizontal();
            int selectedCount = GetSelectedCount(group.id);
            bool previousEnabled = GUI.enabled;
            GUI.enabled = previousEnabled && selectedCount > 0 && IsInProjectAssets(group);
            if (GUILayout.Button($"删除 {selectedCount} 个选中资产", GUILayout.Height(25)))
            {
                DeleteSelected(group);
            }
            GUI.enabled = previousEnabled;
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.EndVertical();
            return groupRect.height;
        }

        /// <summary>
        /// 在 Layout 事件统一应用上一帧测得的高度和滚动位置，保证 Layout/Repaint 使用同一批控件。
        /// </summary>
        private void ApplyPendingVirtualizationState()
        {
            if (Event.current.type != EventType.Layout) return;

            if (_hasPendingScrollPos)
            {
                _scanScrollPos = _pendingScrollPos;
                _hasPendingScrollPos = false;
            }

            if (_pendingResultsViewportHeight > 1f)
            {
                _resultsViewportHeight = _pendingResultsViewportHeight;
                _pendingResultsViewportHeight = 0f;
            }

            foreach (var pair in _pendingGroupHeights)
                _groupHeights[pair.Key] = pair.Value;
            _pendingGroupHeights.Clear();

            foreach (var pair in _pendingThumbScrolls)
                _thumbScrolls[pair.Key] = pair.Value;
            _pendingThumbScrolls.Clear();
        }

        /// <summary>
        /// 未实际绘制过的组按当前控件结构估算高度；进入可见区后会用真实高度替换。
        /// </summary>
        private float GetGroupHeight(DuplicateGroup group)
        {
            if (_groupHeights.TryGetValue(group.id, out float measuredHeight))
                return measuredHeight;

            int imageCount = group.images?.Count ?? 0;
            float lineHeight = EditorGUIUtility.singleLineHeight;
            float spacing = EditorGUIUtility.standardVerticalSpacing;
            float padding = EditorStyles.helpBox.padding.vertical;

            // 组头 + 缩略图滚动区 + 路径标题 + 路径行 + 删除按钮。
            return lineHeight * 2f
                + ThumbnailListHeight
                + 25f
                + padding
                + spacing * (imageCount + 3)
                + lineHeight * imageCount;
        }

        /// <summary>
        /// 切换结果集时清除只对旧分组有效的测量值和滚动位置。
        /// 分组本身始终保持完整展开，虚拟化只决定是否创建整组控件。
        /// </summary>
        private void ResetScanResultViewState()
        {
            _scanScrollPos = Vector2.zero;
            _pendingScrollPos = Vector2.zero;
            _hasPendingScrollPos = false;
            _resultsViewportHeight = 0f;
            _pendingResultsViewportHeight = 0f;
            _thumbScrolls.Clear();
            _pendingThumbScrolls.Clear();
            _groupHeights.Clear();
            _pendingGroupHeights.Clear();
        }

        /// <summary>清除分组结果及只对当前结果有效的 UI 状态。</summary>
        private void ClearScanResults()
        {
            _results = null;
            _filteredGroups = null;
            _selectedForDeletion.Clear();
            ResetScanResultViewState();
        }

        /// <summary>清除分组扫描和以图搜图两种模式的结果状态。</summary>
        private void ClearAllResults()
        {
            ClearScanResults();
            _queryResults = null;
            _queryScrollPos = Vector2.zero;
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
                _statusMessage = $"文件夹不存在: {PluginUtils.ToDisplayPath(_folderPath)}";
                _statusIsError = true;
                return;
            }

            ClearScanResults();
            ClearThumbnailCache();
            _statusMessage = "";
            _statusIsError = false;
            _featureCacheStaleness = null; // 扫描期间隐藏过期警告

            _runner.StartScan(
                folderPath: _folderPath,
                threshold: _threshold,
                recursive: _recursive,
                workers: _workers,
                cacheFeaturesDir: Path.Combine(CacheDir, "features"),
                excludedDirectories: ExcludedDirectorySettings.GetDirectories(),
                onComplete: result =>
                {
                    RefreshReferenceCounts(result);
                    _results = result;
                    RebuildGroupKeywordFilter();
                    int failedCount = result.failed_images?.Count ?? 0;
                    _statusMessage = $"扫描完成：找到 {result.total_groups} 组相似图片。" +
                                     (failedCount > 0 ? $" 跳过 {failedCount} 张处理失败的图片。" : string.Empty);
                    _statusIsError = false;
                    SaveCache(result);
                    _featureCacheStaleness = null; // 扫描后缓存已最新，清除警告
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
        /// 仅支持项目 Assets 目录内的文件，成功项会移入系统回收站/废纸篓。
        /// Unity 的 Ctrl+Z 不能撤销文件删除，外部文件需手动处理。
        /// </summary>
        private void DeleteSelected(DuplicateGroup group)
        {
            var toDelete = new List<string>();
            for (int i = 0; i < group.images.Count; i++)
            {
                if (IsSelected(group.id, i))
                    toDelete.Add(group.images[i]);
            }

            if (toDelete.Count == 0) return;
            if (toDelete.Exists(path => string.IsNullOrEmpty(PluginUtils.AbsoluteToAssetPath(path))))
            {
                EditorUtility.DisplayDialog("无法删除",
                    "选中项包含 Assets 目录外的文件，请通过文件管理器手动处理。", "确定");
                return;
            }

            string msg = $"确认删除 {toDelete.Count} 张图片？\n\n" +
                         "文件将移至系统回收站/废纸篓；Unity 不支持 Ctrl+Z 撤销此操作。";
            if (!EditorUtility.DisplayDialog("确认删除", msg, "删除", "取消"))
                return;

            var deleted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var path in toDelete)
            {
                string assetPath = PluginUtils.AbsoluteToAssetPath(path);
                if (!string.IsNullOrEmpty(assetPath) && AssetDatabase.MoveAssetToTrash(assetPath))
                {
                    deleted.Add(path);
                }
                else
                {
                    Debug.LogError($"[ImageSimilarityPlugin] 删除资产失败: {assetPath}");
                }
            }
            AssetDatabase.Refresh();

            group.images.RemoveAll(img => deleted.Contains(img));
            // 删除命中图片后立即重建筛选，避免空组继续占用旧的虚拟化布局。
            RebuildGroupKeywordFilter();
            ClearSelection(group.id);
            ResetScanResultViewState();
            _statusMessage = deleted.Count == toDelete.Count
                ? $"已将 {deleted.Count} 个资产移至系统回收站/废纸篓。"
                : $"删除完成：成功 {deleted.Count} 个，失败 {toDelete.Count - deleted.Count} 个。";
            _statusIsError = deleted.Count != toDelete.Count;
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
            string scriptsDir = PythonRunner.GetPythonScriptsDir();
            if (string.IsNullOrEmpty(scriptsDir))
            {
                _statusMessage = "无法定位插件 Python 目录，请保持 ImageSimilarityPlugin 内部结构为 Editor/Python 同级。";
                _statusIsError = true;
                return;
            }

            string reqPath = Path.Combine(scriptsDir, "requirements.txt");
            _installer.StartInstall(pythonPath, reqPath);
            Repaint();
        }

        // ==================================================================
        //  缓存
        // ==================================================================

        /// <summary>
        /// 检测当前文件夹是否有对应的扫描缓存 + 特征缓存是否过期。
        /// 每种文件夹对应一个独立的缓存文件（文件名基于路径哈希）。
        /// 如果文件夹未变则跳过检测。
        /// </summary>
        private void CheckCache()
        {
            string exclusionScope = ExcludedDirectorySettings.GetScopeKey();
            if (_folderPath == _lastCheckedFolder
                && _recursive == _lastCheckedRecursive
                && exclusionScope == _lastCheckedExclusionScope) return;
            _lastCheckedFolder = _folderPath;
            _lastCheckedRecursive = _recursive;
            _lastCheckedExclusionScope = exclusionScope;

            // --- 扫描结果缓存 ---
            _cachePath = GetCacheFilePath();
            if (File.Exists(_cachePath))
            {
                try
                {
                    string json = File.ReadAllText(_cachePath, Encoding.UTF8);
                    var meta = JsonUtility.FromJson<CacheMeta>(json);
                    if (meta != null && meta.recursive == _recursive
                        && PluginUtils.PathsEqual(meta.folder, _folderPath)
                        && meta.exclusion_scope == exclusionScope
                        && File.Exists(GetResultCacheFilePath()))
                    {
                        _hasCache = true;
                        _cacheInfo = $"{meta.total_groups} 组 | 阈值: {meta.threshold:F2} | {meta.date}";
                    }
                }
                catch { _hasCache = false; _cacheInfo = ""; }
            }
            else { _hasCache = false; _cacheInfo = ""; }

            // --- 特征缓存过期检测（异步，通过持久化 Python 会话） ---
            _featureCacheStaleness = null;
            _pendingFeatureCheck = false;
            TriggerFeatureCacheCheck();
        }

        /// <summary>
        /// 通过持久化 Python 会话异步检查特征缓存是否过期。
        /// 结果存入 _featureCacheStaleness。
        /// </summary>
        private void RetryPendingFeatureCheck()
        {
            if (!_pendingFeatureCheck) return;
            if (!PythonSession.Instance.IsReady) return;
            // Session is now ready — retry the check
            _lastFeatureCheckFolder = null; // allow Trigger to proceed
            _lastFeatureCheckExclusionScope = null;
            TriggerFeatureCacheCheck();
        }

        private void TriggerFeatureCacheCheck()
        {
            if (!_depsInstalled || string.IsNullOrEmpty(_folderPath)) return;
            string exclusionScope = ExcludedDirectorySettings.GetScopeKey();
            if (_folderPath == _lastFeatureCheckFolder
                && _recursive == _lastFeatureCheckRecursive
                && exclusionScope == _lastFeatureCheckExclusionScope) return;
            _lastFeatureCheckFolder = _folderPath;
            _lastFeatureCheckRecursive = _recursive;
            _lastFeatureCheckExclusionScope = exclusionScope;

            if (!PythonSession.Instance.IsReady)
            {
                _pendingFeatureCheck = true;
                return;
            }

            _pendingFeatureCheck = false;
            string featuresDir = Path.Combine(CacheDir, "features");
            PythonSession.Instance.CheckCache(
                _folderPath, featuresDir, _recursive,
                ExcludedDirectorySettings.GetDirectories(),
                onResult: info =>
                {
                    _featureCacheStaleness = info;
                    Repaint();
                },
                onError: err =>
                {
                    UnityEngine.Debug.LogWarning($"[SimilarityWindow] Feature cache check failed: {err}");
                });
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
                    recursive = _recursive,
                    exclusion_scope = ExcludedDirectorySettings.GetScopeKey(),
                    total_images = result.total_images,
                    total_groups = result.total_groups,
                    date = DateTime.Now.ToString("yyyy-MM-dd HH:mm"),
                }, true);
                File.WriteAllText(_cachePath, meta, Encoding.UTF8);

                string resultPath = GetResultCacheFilePath();
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
            string resultPath = GetResultCacheFilePath();
            if (!File.Exists(resultPath)) return;

            try
            {
                string json = File.ReadAllText(resultPath, Encoding.UTF8);
                _results = JsonUtility.FromJson<ScanResultData>(json);
                if (_results != null)
                {
                    RefreshReferenceCounts(_results);
                    RebuildGroupKeywordFilter();
                    _selectedForDeletion.Clear();
                    ResetScanResultViewState();
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
                string resultPath = GetResultCacheFilePath();
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
            SetFolderPath(DrawEditablePathField(_folderPath));
            if (GUILayout.Button("浏览", GUILayout.Width(70)))
            {
                string selected = EditorUtility.OpenFolderPanel("选择目标文件夹", _folderPath, "");
                if (!string.IsNullOrEmpty(selected))
                    SetFolderPath(selected);
            }
            EditorGUILayout.EndHorizontal();

            DrawExcludedDirectories();

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

            bool canQuery = CanStartRunner
                && !string.IsNullOrEmpty(_queryImagePath)
                && File.Exists(_queryImagePath);

            bool previousEnabled = GUI.enabled;
            GUI.enabled = previousEnabled && canQuery;
            if (GUILayout.Button("开始搜索", GUILayout.Height(30), GUILayout.Width(120)))
            {
                StartQuery();
            }
            GUI.enabled = previousEnabled;

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

            // 特征缓存过期警告
            DrawFeatureCacheWarning();

            // 进度条
            if (_runner.IsRunning)
            {
                Rect r = EditorGUILayout.GetControlRect(false, 20);
                EditorGUI.ProgressBar(r, _runner.Progress, $"正在搜索... {(_runner.Progress * 100f):F0}%");
            }

            DrawStatusMessage();
        }

        /// <summary>
        /// 启动以图搜图查询。
        /// 验证查询图片和目标文件夹存在后，通过 PythonRunner 异步执行。
        /// </summary>
        private void StartQuery()
        {
            if (!File.Exists(_queryImagePath))
            {
                _statusMessage = $"查询图片不存在: {PluginUtils.ToDisplayPath(_queryImagePath)}";
                _statusIsError = true;
                return;
            }
            if (!Directory.Exists(_folderPath))
            {
                _statusMessage = $"目标文件夹不存在: {PluginUtils.ToDisplayPath(_folderPath)}";
                _statusIsError = true;
                return;
            }

            ClearAllResults();
            ClearThumbnailCache();
            _statusMessage = "";
            _statusIsError = false;
            _featureCacheStaleness = null; // 查询期间隐藏过期警告

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
                    RefreshReferenceCounts(result);
                    _queryResults = result;
                    _featureCacheStaleness = null; // 查询后缓存已更新，清除警告
                    int resultCount = result.results?.Count ?? 0;
                    int failedCount = result.failed_images?.Count ?? 0;
                    float topScore = resultCount > 0 ? result.results[0].similarity : 0f;
                    _statusMessage = $"搜索完成：在 {result.total_images} 张图片中找到 {resultCount} 张相似图片" +
                                     (resultCount > 0 ? $" (最高相似度: {topScore:P1})" : "") +
                                     (failedCount > 0 ? $"，跳过 {failedCount} 张处理失败的图片" : string.Empty);
                    _statusIsError = false;
                    Repaint();
                },
                onError: error =>
                {
                    _statusMessage = error;
                    _statusIsError = true;
                    Repaint();
                },
                excludedDirectories: ExcludedDirectorySettings.GetDirectories()
            );
        }

        private void DrawStatusMessage()
        {
            if (string.IsNullOrEmpty(_statusMessage))
                return;

            Color previousColor = GUI.color;
            GUI.color = _statusIsError ? Color.red : previousColor;
            EditorGUILayout.LabelField(_statusMessage, EditorStyles.wordWrappedLabel);
            GUI.color = previousColor;
        }

        /// <summary>
        /// 绘制特征缓存过期警告条（扫描前预检结果）。
        /// 当有图片修改/新增/删除时显示黄色警告 + "重新扫描"按钮。
        /// </summary>
        private void DrawFeatureCacheWarning()
        {
            if (_featureCacheStaleness == null) return;
            if (!_featureCacheStaleness.HasChanges) return;

            EditorGUILayout.BeginHorizontal(EditorStyles.helpBox);
            GUI.color = new Color(1f, 0.85f, 0.3f);
            var sb = new StringBuilder();
            sb.Append("⚠️ 特征缓存可能过期：");
            if (_featureCacheStaleness.fresh_count > 0)
                sb.Append($"{_featureCacheStaleness.fresh_count} 张未变  ");
            if (_featureCacheStaleness.stale_count > 0)
                sb.Append($"{_featureCacheStaleness.stale_count} 张已修改  ");
            if (_featureCacheStaleness.new_since_cache > 0)
                sb.Append($"{_featureCacheStaleness.new_since_cache} 张新增  ");
            if (_featureCacheStaleness.missing_count > 0)
                sb.Append($"{_featureCacheStaleness.missing_count} 张已删除  ");
            EditorGUILayout.LabelField(sb.ToString(), EditorStyles.miniLabel);
            GUI.color = Color.white;

            bool previousEnabled = GUI.enabled;
            GUI.enabled = previousEnabled && CanStartRunner;
            if (GUILayout.Button("重新扫描", GUILayout.Width(100), GUILayout.Height(22)))
            {
                if (_tabIndex == 0)
                    StartScan();
                else
                    StartQuery();
            }
            GUI.enabled = previousEnabled;
            EditorGUILayout.EndHorizontal();
        }

        /// <summary>
        /// 绘制缓存更新信息条。仅当有增量更新发生（重新提取或新增）时显示。
        /// </summary>
        private void DrawCacheInfo(CacheInfo info)
        {
            if (info == null) return;

            bool hasChanges = info.re_extracted > 0 || info.new_added > 0
                           || info.missing_removed > 0;
            if (!hasChanges) return;

            EditorGUILayout.BeginHorizontal(EditorStyles.helpBox);
            GUI.color = new Color(1f, 0.85f, 0.3f); // yellow tint
            var sb = new StringBuilder();
            sb.Append("📦 缓存已增量更新：");
            if (info.fresh_used > 0)
                sb.Append($"{info.fresh_used} 张复用缓存  ");
            if (info.re_extracted > 0)
                sb.Append($"{info.re_extracted} 张已修改  ");
            if (info.new_added > 0)
                sb.Append($"{info.new_added} 张新增  ");
            if (info.missing_removed > 0)
                sb.Append($"{info.missing_removed} 张已删除  ");
            EditorGUILayout.LabelField(sb.ToString(), EditorStyles.miniLabel);
            GUI.color = Color.white;
            EditorGUILayout.EndHorizontal();
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

            // 缓存更新信息
            DrawCacheInfo(_queryResults.cache_info);

            EditorGUILayout.Space(5);

            _queryScrollPos = EditorGUILayout.BeginScrollView(_queryScrollPos);

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
            EditorGUILayout.LabelField($"#{img.rank}", GetQueryRankStyle(), GUILayout.Width(40));

            // 缩略图
            Texture2D thumb = GetThumbnail(img.image_path);
            Rect thumbRect = GUILayoutUtility.GetRect(ThumbnailSize, ThumbnailSize,
                GUILayout.Width(ThumbnailSize), GUILayout.Height(ThumbnailSize));
            DrawThumbnail(thumbRect, thumb);

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
            string displayPath = PluginUtils.ToDisplayPath(img.image_path);
            EditorGUILayout.LabelField(new GUIContent(displayPath, displayPath), EditorStyles.miniLabel);
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

        /// <summary>目录输入框显示 Assets/...，内部始终保存绝对路径。</summary>
        private static string DrawEditablePathField(string absolutePath)
        {
            string displayPath = PluginUtils.ToDisplayPath(absolutePath);
            string editedPath = EditorGUILayout.TextField(displayPath);
            return editedPath == displayPath
                ? absolutePath
                : PluginUtils.ToAbsolutePath(editedPath);
        }

        private void SetFolderPath(string path)
        {
            if (string.Equals(_folderPath, path, StringComparison.Ordinal))
                return;

            _folderPath = path;
            SearchDirectorySettings.Save(path);
        }

        /// <summary>扫描结果展示前使角标失效，并在 FR2 存在待处理资产时自动刷新索引。</summary>
        private void RefreshReferenceCounts(ScanResultData result)
        {
            var imagePaths = new List<string>();
            if (result?.groups != null)
            {
                foreach (DuplicateGroup group in result.groups)
                    if (group?.images != null)
                        imagePaths.AddRange(group.images);
            }

            RefreshReferenceCounts(imagePaths);
        }

        /// <summary>查询结果使用与分组扫描相同的 FR2 刷新边界。</summary>
        private void RefreshReferenceCounts(QueryResultData result)
        {
            var imagePaths = new List<string>();
            if (result?.results != null)
            {
                foreach (SimilarImage image in result.results)
                    if (!string.IsNullOrEmpty(image?.image_path))
                        imagePaths.Add(image.image_path);
            }

            RefreshReferenceCounts(imagePaths);
        }

        private void RefreshReferenceCounts(IEnumerable<string> imagePaths)
        {
            FR2Integration.RefreshReferenceCountsIfPending(imagePaths, OnReferenceCountsRefreshed);
        }

        private void OnReferenceCountsRefreshed(bool _)
        {
            if (this != null)
                Repaint();
        }

        /// <summary>绘制分组扫描和以图搜图共用的项目级排除目录列表。</summary>
        private void DrawExcludedDirectories()
        {
            string[] directories = ExcludedDirectorySettings.GetDirectories();
            string removePath = null;

            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.BeginHorizontal();
            _showExcludedDirectories = EditorGUILayout.Foldout(
                _showExcludedDirectories,
                $"排除目录 ({directories.Length})",
                true);
            GUILayout.FlexibleSpace();

            bool previousEnabled = GUI.enabled;
            GUI.enabled = previousEnabled && !_runner.IsRunning;
            if (GUILayout.Button(new GUIContent("+", "添加排除目录"), EditorStyles.miniButton, GUILayout.Width(24)))
            {
                string initialFolder = Directory.Exists(_folderPath) ? _folderPath : Application.dataPath;
                string selected = EditorUtility.OpenFolderPanel("选择需要排除的目录", initialFolder, string.Empty);
                if (!string.IsNullOrEmpty(selected))
                {
                    bool added = ExcludedDirectorySettings.TryAdd(selected, out string message);
                    _statusMessage = message;
                    _statusIsError = !added;
                    if (added)
                        OnExcludedDirectoriesChanged();
                }
            }
            EditorGUILayout.EndHorizontal();

            if (_showExcludedDirectories)
            {
                foreach (string directory in directories)
                {
                    EditorGUILayout.BeginHorizontal();
                    string displayPath = PluginUtils.ToDisplayPath(directory);
                    EditorGUILayout.LabelField(new GUIContent(displayPath, displayPath), EditorStyles.miniLabel);
                    if (GUILayout.Button(new GUIContent("-", "移除排除目录"), EditorStyles.miniButton, GUILayout.Width(24)))
                        removePath = directory;
                    EditorGUILayout.EndHorizontal();
                }
            }

            GUI.enabled = previousEnabled;
            EditorGUILayout.EndVertical();

            if (!string.IsNullOrEmpty(removePath) && ExcludedDirectorySettings.Remove(removePath))
            {
                _statusMessage = $"已移除排除目录: {PluginUtils.ToDisplayPath(removePath)}";
                _statusIsError = false;
                OnExcludedDirectoriesChanged();
            }
        }

        /// <summary>排除范围变化后清除当前展示状态，并让两类缓存按新范围重新检查。</summary>
        private void OnExcludedDirectoriesChanged()
        {
            _lastCheckedFolder = null;
            _lastCheckedExclusionScope = null;
            _lastFeatureCheckFolder = null;
            _lastFeatureCheckExclusionScope = null;
            _hasCache = false;
            _cacheInfo = string.Empty;
            _featureCacheStaleness = null;
            ClearAllResults();
            ClearThumbnailCache();
            Repaint();
        }

        /// <summary>
        /// 基于文件夹路径、递归范围和排除目录生成稳定缓存键。
        /// 任一搜索范围变化后都不会复用其他范围的扫描结果。
        /// </summary>
        private string GetCacheFilePath()
        {
            // string.GetHashCode 不保证跨进程稳定，使用 Hash128 让重启后的缓存键保持一致。
            string normalizedPath = Path.GetFullPath(_folderPath)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (Application.platform == RuntimePlatform.WindowsEditor)
                normalizedPath = normalizedPath.ToUpperInvariant();
            string scopeKey = normalizedPath
                + "|recursive=" + _recursive
                + "|excluded=" + ExcludedDirectorySettings.GetScopeKey();
            string hash = Hash128.Compute(scopeKey).ToString();
            return Path.Combine(CacheDir, $"scan_{hash}.json");
        }

        private string GetResultCacheFilePath()
        {
            if (string.IsNullOrEmpty(_cachePath)) return null;
            string directory = Path.GetDirectoryName(_cachePath);
            string fileName = Path.GetFileNameWithoutExtension(_cachePath) + "_result.json";
            return Path.Combine(directory ?? string.Empty, fileName);
        }

        /// <summary>缓存的元数据结构（不包含完整分组信息）</summary>
        [Serializable]
        private class CacheMeta
        {
            public string folder;
            public float threshold;
            public bool recursive;
            public string exclusion_scope;
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
            HashSet<int> selection = GetOrCreateSelection(groupId);
            if (!selection.Add(imageIndex))
            {
                selection.Remove(imageIndex);
                if (selection.Count == 0)
                    _selectedForDeletion.Remove(groupId);
            }
        }

        private void SetSelection(int groupId, int imageIndex, bool selected)
        {
            if (selected)
            {
                GetOrCreateSelection(groupId).Add(imageIndex);
                return;
            }

            if (!_selectedForDeletion.TryGetValue(groupId, out HashSet<int> selection))
                return;

            selection.Remove(imageIndex);
            if (selection.Count == 0)
                _selectedForDeletion.Remove(groupId);
        }

        private HashSet<int> GetOrCreateSelection(int groupId)
        {
            if (_selectedForDeletion.TryGetValue(groupId, out HashSet<int> selection))
                return selection;

            selection = new HashSet<int>();
            _selectedForDeletion[groupId] = selection;
            return selection;
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
            foreach (var img in group.images)
            {
                if (string.IsNullOrEmpty(PluginUtils.AbsoluteToAssetPath(img)))
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

        private GUIStyle GetQueryRankStyle()
        {
            if (_queryRankStyle != null)
                return _queryRankStyle;

            _queryRankStyle = new GUIStyle(EditorStyles.boldLabel)
            {
                alignment = TextAnchor.MiddleCenter,
                fontSize = 14,
            };
            return _queryRankStyle;
        }

        /// <summary>在固定区域内按原始宽高比绘制缩略图。</summary>
        private static void DrawThumbnail(Rect rect, Texture2D texture)
        {
            EditorGUI.DrawRect(rect, new Color(0.2f, 0.2f, 0.2f, 0.5f));
            if (texture == null)
            {
                GUI.Label(rect, "?", EditorStyles.centeredGreyMiniLabel);
                return;
            }

            float textureAspect = (float)texture.width / Mathf.Max(1, texture.height);
            float drawWidth = rect.width;
            float drawHeight = rect.height;
            if (textureAspect >= 1f)
                drawHeight /= textureAspect;
            else
                drawWidth *= textureAspect;

            Rect drawRect = new Rect(
                rect.x + (rect.width - drawWidth) / 2f,
                rect.y + (rect.height - drawHeight) / 2f,
                drawWidth,
                drawHeight);
            GUI.DrawTexture(drawRect, texture, ScaleMode.StretchToFill);
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

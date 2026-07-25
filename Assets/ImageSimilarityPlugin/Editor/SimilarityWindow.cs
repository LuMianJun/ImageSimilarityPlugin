using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace ImageSimilarityPlugin
{
    /// <summary>
    /// Unity Editor window for finding similar/duplicate images using a Python backend.
    /// Open via Tools > Find Similar Images.
    /// </summary>
    public class SimilarityWindow : EditorWindow
    {
        // --- Settings ---
        private string _folderPath = "";
        private float _threshold = 0.80f;
        private bool _recursive = true;
        private int _workers = 4;

        // --- State ---
        private PythonRunner _runner;
        private ScanResultData _results;
        private string _statusMessage = "";
        private bool _statusIsError = false;
        private string _pythonVersion = null;
        private bool _depsInstalled = false;
        private bool _checkingDeps = false;
        private bool _fr2Ready = false;

        // --- Results UI ---
        private Vector2 _scrollPos;
        private Dictionary<int, HashSet<int>> _selectedForDeletion = new Dictionary<int, HashSet<int>>(); // groupId -> set of image indices
        private Dictionary<string, Texture2D> _thumbnailCache = new Dictionary<string, Texture2D>();
        private const int THUMB_SIZE = 64;

        // --- Dependency install ---
        private Process _installProcess;
        private bool _isInstalling;
        private float _installProgress;
        private string _installLog = "";
        private Vector2 _installLogScrollPos;

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
            // Default to the project's Assets folder
            if (string.IsNullOrEmpty(_folderPath))
                _folderPath = Application.dataPath;

            CheckEnvironment();
        }

        private void OnDisable()
        {
            _runner?.Cancel();
            ClearThumbnailCache();
        }

        private void CheckEnvironment()
        {
            string pyPath = PythonLocator.GetPythonPath();
            _pythonVersion = pyPath != null ? PythonLocator.GetPythonVersion() : null;

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

            _fr2Ready = ImagePreviewWindow.IsFR2Ready;
        }

        private void OnGUI()
        {
            // --- Environment status bar ---
            DrawEnvironmentBar();

            // --- Install log ---
            if (_isInstalling)
            {
                EditorGUILayout.Space(3);
                EditorGUILayout.BeginVertical(EditorStyles.helpBox);

                // Title bar with close button
                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.LabelField("安装日志", EditorStyles.boldLabel);
                GUILayout.FlexibleSpace();
                bool isRunning = _installProcess != null && !_installProcess.HasExited;
                if (!isRunning)
                {
                    if (GUILayout.Button("关闭", EditorStyles.miniButton, GUILayout.Width(40)))
                    {
                        CloseInstallLog();
                        EditorGUILayout.EndHorizontal();
                        EditorGUILayout.EndVertical();
                        return;
                    }
                }
                EditorGUILayout.EndHorizontal();

                // Progress bar
                Rect barRect = EditorGUILayout.GetControlRect(false, 22);
                EditorGUI.ProgressBar(barRect, _installProgress,
                    _installProgress >= 1f ? "安装完成" :
                    _installProgress >= 0.7f ? "正在安装包..." :
                    _installProgress > 0f ? "正在下载..." : "准备中...");

                // Scrollable log
                if (!string.IsNullOrEmpty(_installLog))
                {
                    float logHeight = Mathf.Min(200, EditorGUIUtility.currentViewWidth * 0.4f);
                    _installLogScrollPos = EditorGUILayout.BeginScrollView(
                        _installLogScrollPos, GUILayout.Height(logHeight));
                    EditorGUILayout.TextArea(_installLog, EditorStyles.label,
                        GUILayout.ExpandHeight(true));
                    EditorGUILayout.EndScrollView();
                }

                EditorGUILayout.EndVertical();
            }

            EditorGUILayout.Space(5);

            // --- Settings ---
            DrawSettings();

            EditorGUILayout.Space(5);

            // --- Scan button + progress ---
            DrawScanControls();

            EditorGUILayout.Space(5);

            // --- Results ---
            DrawResults();
        }

        // ==================================================================
        //  Environment
        // ==================================================================

        private void DrawEnvironmentBar()
        {
            EditorGUILayout.BeginHorizontal(EditorStyles.helpBox);

            bool pythonOk = !string.IsNullOrEmpty(_pythonVersion);
            bool depsOk = _depsInstalled;

            GUI.color = pythonOk ? Color.green : Color.red;
            EditorGUILayout.LabelField(pythonOk ? $" Python {_pythonVersion}" : " 未找到 Python",
                GUILayout.Width(200));

            GUI.color = depsOk ? Color.green : (_checkingDeps ? Color.yellow : Color.red);
            EditorGUILayout.LabelField(
                _checkingDeps ? " 正在检查依赖..." :
                depsOk ? " 依赖已就绪" : " 缺少依赖",
                GUILayout.Width(180));

            bool fr2Installed = ImagePreviewWindow.HasFR2();
            GUI.color = _fr2Ready ? Color.green : (fr2Installed ? Color.yellow : Color.red);
            EditorGUILayout.LabelField(
                _fr2Ready ? " FR2 已就绪" :
                fr2Installed ? " FR2 缓存为空" : " FR2 未安装",
                GUILayout.Width(160));

            GUI.color = Color.white;
            GUILayout.FlexibleSpace();

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
                GUI.enabled = !_isInstalling;
                if (GUILayout.Button(_isInstalling ? "安装中..." : "安装依赖", GUILayout.Width(140)))
                {
                    InstallDependencies();
                }
                GUI.enabled = true;
            }

            EditorGUILayout.EndHorizontal();
        }

        // ==================================================================
        //  Settings
        // ==================================================================

        private void DrawSettings()
        {
            EditorGUILayout.LabelField("扫描设置", EditorStyles.boldLabel);

            // Folder
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

            // Threshold
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("相似度阈值:", GUILayout.Width(80));
            _threshold = EditorGUILayout.Slider(_threshold, 0f, 1.00f);
            EditorGUILayout.LabelField(_threshold.ToString("F3"), GUILayout.Width(40));
            EditorGUILayout.EndHorizontal();

            // Recursive + Workers
            EditorGUILayout.BeginHorizontal();
            _recursive = EditorGUILayout.ToggleLeft("递归子目录", _recursive, GUILayout.Width(100));
            EditorGUILayout.LabelField("线程数:", GUILayout.Width(60));
            _workers = EditorGUILayout.IntSlider(_workers, 1, 16);
            EditorGUILayout.EndHorizontal();
        }

        // ==================================================================
        //  Scan Controls
        // ==================================================================

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

            // Progress bar
            if (_runner.IsRunning)
            {
                Rect r = EditorGUILayout.GetControlRect(false, 20);
                EditorGUI.ProgressBar(r, _runner.Progress, $"正在扫描... {(_runner.Progress * 100f):F0}%");
            }

            // Status
            if (!string.IsNullOrEmpty(_statusMessage))
            {
                GUI.color = _statusIsError ? Color.red : Color.white;
                EditorGUILayout.LabelField(_statusMessage, EditorStyles.wordWrappedLabel);
                GUI.color = Color.white;
            }
        }

        // ==================================================================
        //  Results
        // ==================================================================

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

        private void DrawGroupCard(DuplicateGroup group)
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);

            // Header
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField($"第 {group.id} 组", EditorStyles.boldLabel, GUILayout.Width(80));
            EditorGUILayout.LabelField($"{group.images.Count} 张图片");
            GUILayout.FlexibleSpace();

            // Select duplicates button (auto-select based on heuristics)
            if (GUILayout.Button("自动选择重复项", GUILayout.Width(150)))
            {
                AutoSelectDuplicates(group);
            }
            EditorGUILayout.EndHorizontal();

            // Thumbnails
            EditorGUILayout.BeginHorizontal();
            int maxThumbs = Mathf.Min(group.images.Count, 6);
            for (int i = 0; i < maxThumbs; i++)
            {
                bool isSelected = IsSelected(group.id, i);

                EditorGUILayout.BeginVertical(GUILayout.Width(THUMB_SIZE + 8));

                // Selection toggle
                EditorGUILayout.BeginHorizontal();
                bool newSelected = EditorGUILayout.Toggle(isSelected, GUILayout.Width(16));
                if (newSelected != isSelected)
                    ToggleSelection(group.id, i);

                // Filename label
                string shortName = Path.GetFileName(group.images[i]);
                EditorGUILayout.LabelField(shortName, GUILayout.Width(THUMB_SIZE - 8));
                EditorGUILayout.EndHorizontal();

                // Thumbnail (aspect-ratio preserved)
                Texture2D thumb = GetThumbnail(group.images[i]);
                Rect thumbRect = GUILayoutUtility.GetRect(THUMB_SIZE, THUMB_SIZE, GUILayout.Width(THUMB_SIZE), GUILayout.Height(THUMB_SIZE));

                // Draw background
                EditorGUI.DrawRect(thumbRect, new Color(0.2f, 0.2f, 0.2f, 0.5f));

                if (thumb != null)
                {
                    // Calculate aspect-preserving draw rect
                    float texAspect = (float)thumb.width / Mathf.Max(1, thumb.height);
                    float drawW, drawH;
                    if (texAspect >= 1f)
                    {
                        drawW = THUMB_SIZE;
                        drawH = THUMB_SIZE / texAspect;
                    }
                    else
                    {
                        drawH = THUMB_SIZE;
                        drawW = THUMB_SIZE * texAspect;
                    }
                    float offsetX = thumbRect.x + (THUMB_SIZE - drawW) / 2f;
                    float offsetY = thumbRect.y + (THUMB_SIZE - drawH) / 2f;
                    Rect drawRect = new Rect(offsetX, offsetY, drawW, drawH);

                    GUI.DrawTexture(drawRect, thumb, ScaleMode.StretchToFill);
                }
                else
                {
                    GUI.Label(thumbRect, "?", EditorStyles.centeredGreyMiniLabel);
                }

                // Click thumbnail → open preview window
                if (GUI.Button(thumbRect, GUIContent.none, GUIStyle.none))
                {
                    ImagePreviewWindow.Open(group, i,
                        onRefreshParent: () => Repaint());
                }

                // FR2 reference count badge (drawn after button to be on top)
                ImagePreviewWindow.DrawRefCountBadge(thumbRect, group.images[i]);

                EditorGUILayout.EndVertical();

                if (i < maxThumbs - 1)
                    GUILayout.Space(4);
            }
            EditorGUILayout.EndHorizontal();

            // Path list
            EditorGUILayout.LabelField("路径:", EditorStyles.miniLabel);
            foreach (var img in group.images)
            {
                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.LabelField("  " + img, EditorStyles.miniLabel);
                if (GUILayout.Button("定位", EditorStyles.miniButton, GUILayout.Width(40)))
                {
                    PingAsset(img);
                }
                EditorGUILayout.EndHorizontal();
            }

            // Delete button
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
        //  Actions
        // ==================================================================

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
                onComplete: result =>
                {
                    _results = result;
                    _statusMessage = $"扫描完成：找到 {result.total_groups} 组相似图片。";
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

        private void AutoSelectDuplicates(DuplicateGroup group)
        {
            // Heuristic: keep the "best" image, mark others as duplicates.
            // "Best" = largest file that doesn't look like a copy.
            // Simple approach: keep the first (by path length / file size), select rest.

            int bestIndex = 0;
            long bestScore = 0;

            for (int i = 0; i < group.images.Count; i++)
            {
                long score = 0;
                try
                {
                    var fi = new FileInfo(group.images[i]);
                    score = fi.Exists ? fi.Length : 0;

                    // Penalize filenames that look like copies
                    string name = Path.GetFileNameWithoutExtension(group.images[i]);
                    if (name.Contains("Copy") || name.Contains("copy") || name.EndsWith("_old") || name.EndsWith("_Old"))
                        score /= 2;
                }
                catch { }

                if (score > bestScore)
                {
                    bestScore = score;
                    bestIndex = i;
                }
            }

            // Select all except the best
            for (int i = 0; i < group.images.Count; i++)
            {
                if (i != bestIndex)
                    SetSelection(group.id, i, true);
                else
                    SetSelection(group.id, i, false);
            }

            Repaint();
        }

        private void DeleteSelected(DuplicateGroup group)
        {
            if (!IsInProjectAssets(group))
            {
                EditorUtility.DisplayDialog("无法删除",
                    "部分图片不在项目 Assets 文件夹内，" +
                    "请通过文件管理器手动删除。", "确定");
                return;
            }

            // Collect selected images
            var toDelete = new List<string>();
            for (int i = 0; i < group.images.Count; i++)
            {
                if (IsSelected(group.id, i))
                    toDelete.Add(group.images[i]);
            }

            if (toDelete.Count == 0) return;

            string msg = $"确认删除 {toDelete.Count} 张图片？\n\n文件将移至回收站，" +
                         "可通过 Ctrl+Z 撤销此操作。";
            if (!EditorUtility.DisplayDialog("确认删除", msg, "删除", "取消"))
                return;

            AssetDatabase.StartAssetEditing();
            try
            {
                foreach (var path in toDelete)
                {
                    // Convert absolute path to relative asset path
                    string assetPath = AbsoluteToAssetPath(path);
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

            // Remove deleted items from the group
            group.images.RemoveAll(img => toDelete.Contains(img));
            ClearSelection(group.id);
            _statusMessage = $"已删除 {toDelete.Count} 个资产。";
            _statusIsError = false;
            Repaint();
        }

        private void InstallDependencies()
        {
            _isInstalling = true;
            _installProgress = 0f;
            _installLog = "";
            _installLogScrollPos = Vector2.zero;
            _statusMessage = "";
            _statusIsError = false;

            string pythonPath = PythonLocator.GetPythonPath();
            if (string.IsNullOrEmpty(pythonPath))
            {
                _installLog = "错误: 未找到 Python，无法安装依赖。\n请先在顶部点击配置 Python指定路径。";
                _installProgress = 0f;
                _isInstalling = true; // keep log visible
                Repaint();
                return;
            }

            string reqPath = Path.Combine(PythonRunner.GetPythonScriptsDir(), "requirements.txt");
            if (!File.Exists(reqPath))
            {
                _installLog = $"错误: 未找到 requirements.txt\n路径: {reqPath}";
                _installProgress = 0f;
                _statusMessage = $"未找到 requirements.txt: {reqPath}";
                _statusIsError = true;
                Repaint();
                return;
            }

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = pythonPath,
                    Arguments = $"-m pip install -r \"{reqPath}\"",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                };

                _installProcess = new Process { StartInfo = psi, EnableRaisingEvents = true };

                _installProcess.OutputDataReceived += (sender, e) =>
                {
                    if (e.Data != null)
                    {
                        EditorApplication.delayCall += () =>
                        {
                            _installLog += e.Data + "\n";
                            // Advance progress based on pip output phases
                            if (e.Data.Contains("Downloading")) _installProgress += 0.03f;
                            else if (e.Data.Contains("Installing collected packages")) _installProgress = 0.7f;
                            else _installProgress += 0.01f;
                            _installProgress = Mathf.Min(_installProgress, 0.9f);
                            _installLogScrollPos.y = float.MaxValue; // auto-scroll to bottom
                            Repaint();
                        };
                    }
                };

                _installProcess.ErrorDataReceived += (sender, e) =>
                {
                    if (e.Data != null)
                    {
                        EditorApplication.delayCall += () =>
                        {
                            _installLog += e.Data + "\n";
                            _installLogScrollPos.y = float.MaxValue;
                            Repaint();
                        };
                    }
                };

                _installProcess.Exited += (sender, e) =>
                {
                    EditorApplication.delayCall += () =>
                    {
                        _installProgress = 1f;
                        if (_installProcess.ExitCode == 0)
                        {
                            _depsInstalled = true;
                            _statusMessage = "依赖包安装成功。";
                            _statusIsError = false;
                        }
                        else
                        {
                            _statusMessage = "依赖包安装失败，请手动运行:\n" +
                                             $"pip install -r \"{reqPath}\"";
                            _statusIsError = true;
                        }
                        _installProcess.Dispose();
                        _installProcess = null;
                        Repaint();
                    };
                };

                _installProcess.Start();
                _installProcess.BeginOutputReadLine();
                _installProcess.BeginErrorReadLine();
            }
            catch (Exception ex)
            {
                _installLog += $"\n错误: {ex.Message}";
                _installProgress = 0f;
                _statusMessage = $"运行 pip 失败: {ex.Message}";
                _statusIsError = true;
                // keep _isInstalling = true so user can see the log
                Repaint();
            }
        }

        /// <summary>
        /// Close the install log panel.
        /// </summary>
        private void CloseInstallLog()
        {
            _isInstalling = false;
            _installLog = "";
            try { if (_installProcess != null && !_installProcess.HasExited) _installProcess.Kill(); } catch { }
        }

        // ==================================================================
        //  Helpers
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

        private bool IsInProjectAssets(DuplicateGroup group)
        {
            string assetsRoot = Application.dataPath.Replace('/', Path.DirectorySeparatorChar);
            foreach (var img in group.images)
            {
                string full = Path.GetFullPath(img);
                if (!full.StartsWith(assetsRoot, StringComparison.OrdinalIgnoreCase))
                    return false;
            }
            return true;
        }

        private string AbsoluteToAssetPath(string absolutePath)
        {
            string assetsRoot = Application.dataPath.Replace('/', Path.DirectorySeparatorChar);
            string full = Path.GetFullPath(absolutePath);
            if (!full.StartsWith(assetsRoot, StringComparison.OrdinalIgnoreCase))
                return null;

            string relative = full.Substring(assetsRoot.Length).TrimStart(Path.DirectorySeparatorChar);
            return "Assets/" + relative.Replace('\\', '/');
        }

        private Texture2D GetThumbnail(string path)
        {
            if (_thumbnailCache.TryGetValue(path, out var cached))
                return cached;

            Texture2D tex = null;

            // Always load from raw file bytes to preserve original image dimensions.
            // AssetDatabase.LoadAssetAtPath returns Unity-imported textures which may
            // have power-of-two dimensions, causing aspect ratio distortion.
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

        private void ClearThumbnailCache()
        {
            foreach (var tex in _thumbnailCache.Values)
            {
                if (tex != null)
                    DestroyImmediate(tex);
            }
            _thumbnailCache.Clear();
        }

        private void PingAsset(string path)
        {
            string assetPath = AbsoluteToAssetPath(path);
            if (!string.IsNullOrEmpty(assetPath))
            {
                var obj = AssetDatabase.LoadMainAssetAtPath(assetPath);
                if (obj != null)
                {
                    EditorGUIUtility.PingObject(obj);
                    Selection.activeObject = obj;
                }
            }
            else
            {
                // Outside the project — reveal in file explorer
                EditorUtility.RevealInFinder(path);
            }
        }
    }
}

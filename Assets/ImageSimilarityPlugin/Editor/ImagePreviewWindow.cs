using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

namespace ImageSimilarityPlugin
{
    /// <summary>
    /// 大图预览窗口。
    /// 从主窗口点击缩略图时以模态窗口打开。
    /// 支持大图预览、同组缩略图切换、文件信息、FR2 引用角标，
    /// 以及显式选择替换目标和替换来源后修改 Prefab 中 Image.sprite 引用的工作流。
    /// </summary>
    public class ImagePreviewWindow : EditorWindow
    {
        private DuplicateGroup _group;
        private int _selectedIndex;
        private int _replacementTargetIndex;
        private Action _onRefreshParent;
        private readonly HashSet<string> _replaceSourcePaths = new HashSet<string>();

        private Dictionary<string, Texture2D> _thumbCache = new Dictionary<string, Texture2D>();
        private Texture2D _largePreview;
        private string _largePreviewPath;

        private Vector2 _mainScroll;
        private Vector2 _thumbScroll;
        private string _statusMsg = "";
        private bool _statusIsError;
        private bool _isReplacing;
        private GUIStyle _fullFileNameStyle;

        private const int THUMB_HEIGHT = 80;
        private const int MAX_PREVIEW_SIZE = 512;
        private const float FILE_INFO_WIDTH = 220f;
        private const float PREVIEW_SPACING = 10f;
        private const float PREVIEW_LAYOUT_RESERVE = 28f;
        private const float PREVIEW_PLACEHOLDER_HEIGHT = 120f;

        // ==================================================================
        //  打开 / 关闭
        // ==================================================================

        /// <summary>
        /// 打开图片预览窗口。
        /// </summary>
        /// <param name="group">图片组数据</param>
        /// <param name="selectedIndex">初始预览图片及替换目标在组内的索引</param>
        /// <param name="onRefreshParent">操作完成后刷新父窗口的回调</param>
        public static void Open(DuplicateGroup group, int selectedIndex, Action onRefreshParent = null)
        {
            var win = GetWindow<ImagePreviewWindow>(true, "图片预览");
            win._group = group;
            win._selectedIndex = Mathf.Clamp(selectedIndex, 0, group.images.Count - 1);
            win._replacementTargetIndex = win._selectedIndex;
            win._onRefreshParent = onRefreshParent;
            win._largePreview = null;
            win._largePreviewPath = null;
            win._thumbCache.Clear();
            win._replaceSourcePaths.Clear();
            win._mainScroll = Vector2.zero;
            win._thumbScroll = Vector2.zero;
            win._statusMsg = "";
            win._statusIsError = false;
            win._isReplacing = false;
            win.minSize = new Vector2(520, 400);
            win.Show();
        }

        private void OnDisable() => ClearCache();

        // ==================================================================
        //  主 GUI 布局
        // ==================================================================

        private void OnGUI()
        {
            if (_group == null) { EditorGUILayout.LabelField("无数据。"); return; }

            _mainScroll = EditorGUILayout.BeginScrollView(_mainScroll);
            DrawLargePreview();
            EditorGUILayout.Space(4);
            DrawThumbnailRow();
            EditorGUILayout.Space(4);
            DrawStatus();
            EditorGUILayout.Space(4);
            DrawActions();
            EditorGUILayout.EndScrollView();
        }

        private void DrawStatus()
        {
            if (!string.IsNullOrEmpty(_statusMsg))
            {
                GUI.color = _statusIsError ? Color.red : Color.white;
                EditorGUILayout.LabelField(_statusMsg, EditorStyles.wordWrappedLabel);
                GUI.color = Color.white;
            }
        }

        // ==================================================================
        //  大图预览
        // ==================================================================

        /// <summary>
        /// 绘制左侧大图预览和右侧文件信息。
        /// 图片按原始宽高比缩放，同时受最大预览尺寸和窗口剩余宽度约束。
        /// </summary>
        private void DrawLargePreview()
        {
            string currentPath = _group.images[_selectedIndex];

            if (_largePreviewPath != currentPath)
            {
                if (_largePreview != null && !_thumbCache.ContainsValue(_largePreview))
                    DestroyImmediate(_largePreview);
                _largePreview = LoadTexture(currentPath);
                _largePreviewPath = currentPath;
            }

            EditorGUILayout.BeginHorizontal();

            // 固定预留文件信息区，避免横图撑宽外层滚动视图后把预览图移出可视区域。
            float availablePreviewWidth = Mathf.Clamp(
                position.width - FILE_INFO_WIDTH - PREVIEW_SPACING - PREVIEW_LAYOUT_RESERVE,
                64f,
                MAX_PREVIEW_SIZE);
            float previewWidth = Mathf.Min(200f, availablePreviewWidth);
            float previewHeight = PREVIEW_PLACEHOLDER_HEIGHT;
            if (_largePreview != null)
            {
                float scale = Mathf.Min(
                    1f,
                    availablePreviewWidth / _largePreview.width,
                    (float)MAX_PREVIEW_SIZE / _largePreview.height);
                previewWidth = _largePreview.width * scale;
                previewHeight = _largePreview.height * scale;
            }

            Rect previewRect = GUILayoutUtility.GetRect(
                previewWidth,
                previewHeight,
                GUILayout.Width(previewWidth),
                GUILayout.Height(previewHeight));
            if (_largePreview != null)
                GUI.DrawTexture(previewRect, _largePreview, ScaleMode.ScaleToFit);
            else
                GUI.Box(previewRect, "无法加载图片", EditorStyles.helpBox);

            GUILayout.Space(PREVIEW_SPACING);
            DrawFileInfo(currentPath);
            EditorGUILayout.EndHorizontal();
        }

        /// <summary>
        /// 绘制右侧文件信息：文件名、尺寸、文件大小、修改时间、组内序号、"在 Project 中定位"按钮。
        /// </summary>
        private void DrawFileInfo(string currentPath)
        {
            EditorGUILayout.BeginVertical(GUILayout.Width(FILE_INFO_WIDTH));
            EditorGUILayout.LabelField("文件信息", EditorStyles.boldLabel);
            EditorGUILayout.LabelField("文件名:", EditorStyles.miniLabel);

            string fileName = Path.GetFileName(currentPath);
            GUIContent fileNameContent = new GUIContent(
                fileName, PluginUtils.ToDisplayPath(currentPath));
            float fileNameHeight = Mathf.Max(
                EditorGUIUtility.singleLineHeight,
                FullFileNameStyle.CalcHeight(fileNameContent, FILE_INFO_WIDTH));
            EditorGUILayout.SelectableLabel(
                fileName,
                FullFileNameStyle,
                GUILayout.Width(FILE_INFO_WIDTH),
                GUILayout.Height(fileNameHeight));

            if (_largePreview != null)
                EditorGUILayout.LabelField($"尺寸: {_largePreview.width} × {_largePreview.height}");

            try
            {
                var fi = new FileInfo(currentPath);
                if (fi.Exists)
                {
                    EditorGUILayout.LabelField($"文件大小: {PluginUtils.FormatFileSize(fi.Length)}");
                    EditorGUILayout.LabelField($"修改时间: {fi.LastWriteTime:yyyy-MM-dd HH:mm}");
                }
            }
            catch { }

            EditorGUILayout.LabelField($"组内序号: {_selectedIndex + 1} / {_group.images.Count}");

            string usage = _selectedIndex == _replacementTargetIndex
                ? "替换目标"
                : _replaceSourcePaths.Contains(currentPath) ? "替换来源" : "未选择";
            EditorGUILayout.LabelField($"引用替换: {usage}");

            if (GUILayout.Button("在 Project 中定位"))
                PluginUtils.PingAsset(currentPath);

            EditorGUILayout.EndVertical();
        }

        // ==================================================================
        //  缩略图行
        // ==================================================================

        /// <summary>
        /// 绘制底部水平滚动缩略图行。
        /// 点击缩略图只切换大图预览；替换目标和替换来源分别显式选择。
        /// </summary>
        private void DrawThumbnailRow()
        {
            EditorGUILayout.LabelField("同组图片", EditorStyles.boldLabel);

            float totalWidth = _group.images.Count * (THUMB_HEIGHT + 14) + 4;

            _thumbScroll = EditorGUILayout.BeginScrollView(_thumbScroll, false, true,
                GUILayout.Height(THUMB_HEIGHT + 78));
            EditorGUILayout.BeginHorizontal(GUILayout.Width(totalWidth));

            for (int i = 0; i < _group.images.Count; i++)
            {
                string path = _group.images[i];
                bool isPreviewed = i == _selectedIndex;
                bool isTarget = i == _replacementTargetIndex;
                bool isReplaceSource = _replaceSourcePaths.Contains(path);

                Color bg = isTarget
                    ? new Color(0.3f, 0.75f, 0.4f, 0.35f)
                    : isReplaceSource
                        ? new Color(1f, 0.65f, 0.2f, 0.3f)
                        : isPreviewed ? new Color(0.3f, 0.6f, 1f, 0.25f) : Color.clear;
                Rect rowRect = EditorGUILayout.BeginVertical(GUILayout.Width(THUMB_HEIGHT + 12));
                if (bg != Color.clear) EditorGUI.DrawRect(rowRect, bg);

                EditorGUI.BeginDisabledGroup(_isReplacing || isTarget);
                if (isTarget)
                {
                    GUILayout.Button("替换目标", EditorStyles.miniButton, GUILayout.Width(THUMB_HEIGHT));
                }
                else if (GUILayout.Button("设为目标", EditorStyles.miniButton, GUILayout.Width(THUMB_HEIGHT)))
                {
                    SetReplacementTarget(i);
                }
                EditorGUI.EndDisabledGroup();

                Texture2D thumb = GetOrLoadThumb(path);
                Rect thumbR = GUILayoutUtility.GetRect(THUMB_HEIGHT, THUMB_HEIGHT,
                    GUILayout.Width(THUMB_HEIGHT), GUILayout.Height(THUMB_HEIGHT));

                if (thumb != null)
                {
                    float a = (float)thumb.width / Mathf.Max(1, thumb.height);
                    float dw = a >= 1 ? THUMB_HEIGHT : THUMB_HEIGHT * a;
                    float dh = a >= 1 ? THUMB_HEIGHT / a : THUMB_HEIGHT;
                    GUI.DrawTexture(new Rect(thumbR.x + (THUMB_HEIGHT - dw) / 2,
                        thumbR.y + (THUMB_HEIGHT - dh) / 2, dw, dh), thumb, ScaleMode.StretchToFill);
                }

                if (!_isReplacing && GUI.Button(thumbR, GUIContent.none, GUIStyle.none))
                {
                    _selectedIndex = i;
                    _largePreviewPath = null;
                    _statusMsg = "";
                    Repaint();
                }

                FR2Integration.DrawRefCountBadge(thumbR, path);
                EditorGUILayout.LabelField(
                    new GUIContent(Path.GetFileName(path), PluginUtils.ToDisplayPath(path)),
                    EditorStyles.centeredGreyMiniLabel,
                    GUILayout.Width(THUMB_HEIGHT + 8));

                EditorGUI.BeginDisabledGroup(_isReplacing || isTarget);
                bool shouldReplace = EditorGUILayout.ToggleLeft(
                    new GUIContent("替换引用", "将引用此图片的 Image.sprite 替换为目标图片"),
                    isReplaceSource,
                    GUILayout.Width(THUMB_HEIGHT + 8));
                if (!isTarget && shouldReplace != isReplaceSource)
                    SetReplacementSource(path, shouldReplace);
                EditorGUI.EndDisabledGroup();

                EditorGUILayout.EndVertical();
                GUILayout.Space(2);
            }

            EditorGUILayout.EndHorizontal();
            EditorGUILayout.EndScrollView();
        }

        private GUIStyle FullFileNameStyle
        {
            get
            {
                if (_fullFileNameStyle == null)
                {
                    _fullFileNameStyle = new GUIStyle(EditorStyles.label)
                    {
                        alignment = TextAnchor.UpperLeft,
                        wordWrap = true,
                    };
                }
                return _fullFileNameStyle;
            }
        }

        // ==================================================================
        //  操作按钮
        // ==================================================================

        /// <summary>
        /// 仅对用户显式勾选的来源图片执行引用替换。
        /// </summary>
        private void DrawActions()
        {
            List<string> oldPaths = GetSelectedReplacementPaths();

            EditorGUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            GUI.enabled = !_isReplacing && oldPaths.Count > 0;

            if (GUILayout.Button(
                $"替换 {oldPaths.Count} 张图片的引用",
                GUILayout.Height(30), GUILayout.Width(220)))
            {
                string keepPath = _group.images[_replacementTargetIndex];
                if (EditorUtility.DisplayDialog("确认操作",
                    $"替换目标:\n  {Path.GetFileName(keepPath)}\n\n" +
                    $"需要替换引用的图片 ({oldPaths.Count}):\n{BuildPathSummary(oldPaths)}\n\n" +
                    "只会修改 Prefab 中匹配这些图片的 Image.sprite 引用。\n\n继续？",
                    "确认", "取消"))
                {
                    ReplaceReferences(keepPath, oldPaths);
                }
            }

            GUI.enabled = true;
            EditorGUILayout.EndHorizontal();
        }

        private void SetReplacementTarget(int index)
        {
            _replacementTargetIndex = Mathf.Clamp(index, 0, _group.images.Count - 1);
            // 同一图片不能同时作为替换目标和来源。
            _replaceSourcePaths.Remove(_group.images[_replacementTargetIndex]);
            _statusMsg = "";
            Repaint();
        }

        private void SetReplacementSource(string path, bool selected)
        {
            if (selected)
                _replaceSourcePaths.Add(path);
            else
                _replaceSourcePaths.Remove(path);
            _statusMsg = "";
            Repaint();
        }

        private List<string> GetSelectedReplacementPaths()
        {
            var result = new List<string>();
            string keepPath = _group.images[_replacementTargetIndex];
            foreach (string path in _group.images)
            {
                if (path != keepPath && _replaceSourcePaths.Contains(path))
                    result.Add(path);
            }
            return result;
        }

        private static string BuildPathSummary(List<string> paths)
        {
            const int maxDisplayed = 10;
            var names = new List<string>();
            int count = Mathf.Min(paths.Count, maxDisplayed);
            for (int i = 0; i < count; i++)
                names.Add("  " + Path.GetFileName(paths[i]));
            if (paths.Count > maxDisplayed)
                names.Add($"  ... 另有 {paths.Count - maxDisplayed} 张");
            return string.Join("\n", names);
        }

        // ==================================================================
        //  引用替换
        // ==================================================================

        /// <summary>
        /// 执行用户确认的引用替换：
        /// 加载目标图 Sprite → 查找所有引用选中来源图片的 Prefab →
        /// 逐个替换 Image.sprite → 保存 Prefab 并报告结果。
        /// 引用查找优先使用 FR2，回退到原生扫描。
        /// </summary>
        private void ReplaceReferences(string keepPath, List<string> selectedOldPaths)
        {
            if (selectedOldPaths == null || selectedOldPaths.Count == 0) return;

            // delayCall 执行前冻结本次操作范围，后续 UI 状态变化不会扩大修改集合。
            var oldPaths = new List<string>(selectedOldPaths);
            _isReplacing = true;
            _statusMsg = "正在查找引用...";
            _statusIsError = false;
            Repaint();

            EditorApplication.delayCall += () =>
            {
                try
                {
                    string keepAssetPath = PluginUtils.AbsoluteToAssetPath(keepPath);
                    Sprite keepSprite = string.IsNullOrEmpty(keepAssetPath) ? null
                        : AssetDatabase.LoadAssetAtPath<Sprite>(keepAssetPath);

                    if (keepSprite == null)
                    {
                        _statusMsg = "无法加载目标图片的 Sprite。请确认文件格式被 Unity 识别为 Sprite。";
                        _statusIsError = true;
                        _isReplacing = false;
                        Repaint();
                        return;
                    }

                    var prefabsToFix = FR2Integration.FindPrefabsReferencing(oldPaths,
                        (i, total) => { _statusMsg = $"查找引用中... ({i}/{total})"; Repaint(); });
                    if (prefabsToFix.Count == 0)
                    {
                        _statusMsg = "未找到任何 Prefab 引用选中的来源图片，无需替换。";
                        _statusIsError = false;
                        _isReplacing = false;
                        Repaint();
                        return;
                    }

                    int replacedCount = 0;
                    int totalComponents = 0;

                    for (int pi = 0; pi < prefabsToFix.Count; pi++)
                    {
                        string prefabPath = prefabsToFix[pi];
                        _statusMsg = $"正在处理 Prefab ({pi + 1}/{prefabsToFix.Count}): {Path.GetFileName(prefabPath)}";
                        Repaint();

                        GameObject root = null;
                        try
                        {
                            root = PrefabUtility.LoadPrefabContents(prefabPath);
                            bool modified = false;

                            foreach (var img in root.GetComponentsInChildren<Image>(true))
                            {
                                if (img.sprite == null) continue;
                                string texPath = AssetDatabase.GetAssetPath(img.sprite);
                                if (string.IsNullOrEmpty(texPath)) continue;
                                string fullPath = Path.GetFullPath(Path.Combine(Application.dataPath, "..", texPath));

                                foreach (var oldPath in oldPaths)
                                    if (PluginUtils.PathsEqual(fullPath, oldPath))
                                        { img.sprite = keepSprite; EditorUtility.SetDirty(img); modified = true; totalComponents++; break; }
                            }

                            if (modified) { PrefabUtility.SaveAsPrefabAsset(root, prefabPath); replacedCount++; }
                        }
                        catch (Exception ex)
                        {
                            Debug.LogError($"处理 Prefab 失败 ({prefabPath}): {ex}");
                        }
                        finally
                        {
                            // 单个 Prefab 失败也必须卸载临时内容，避免残留预览场景和对象。
                            if (root != null)
                            {
                                try { PrefabUtility.UnloadPrefabContents(root); }
                                catch (Exception ex) { Debug.LogError($"卸载 Prefab 内容失败 ({prefabPath}): {ex}"); }
                            }
                        }
                    }

                    AssetDatabase.SaveAssets();
                    AssetDatabase.Refresh();
                    string completionMessage =
                        $"完成! 修改了 {replacedCount} 个 Prefab，共替换 {totalComponents} 个 Image.sprite 引用。";
                    _statusMsg = completionMessage;
                    _statusIsError = false;

                    if (replacedCount > 0)
                    {
                        var affectedImagePaths = new List<string>(oldPaths) { keepPath };
                        Action refreshParent = _onRefreshParent;
                        bool refreshStarted = FR2Integration.RefreshReferenceCounts(
                            affectedImagePaths,
                            success =>
                            {
                                refreshParent?.Invoke();
                                if (this == null) return;
                                _statusMsg = success
                                    ? completionMessage + " FR2 引用数已更新。"
                                    : completionMessage + " FR2 引用数刷新超时，请检查 FR2 状态。";
                                _statusIsError = false;
                                Repaint();
                            });
                        if (refreshStarted)
                            _statusMsg = completionMessage + " 正在更新 FR2 引用数...";
                    }

                    _onRefreshParent?.Invoke();
                }
                catch (Exception ex)
                {
                    _statusMsg = $"操作失败: {ex.Message}";
                    _statusIsError = true;
                }
                finally
                {
                    _isReplacing = false;
                    Repaint();
                }
            };
        }

        // ==================================================================
        //  纹理加载
        // ==================================================================

        /// <summary>从缓存获取缩略图，缓存未命中则加载</summary>
        private Texture2D GetOrLoadThumb(string path)
        {
            if (_thumbCache.TryGetValue(path, out var cached) && cached != null) return cached;
            var tex = LoadTexture(path);
            _thumbCache[path] = tex;
            return tex;
        }

        /// <summary>
        /// 从原始文件字节加载纹理（而非 Unity 导入版本），确保宽高比正确。
        /// </summary>
        private Texture2D LoadTexture(string path)
        {
            if (!File.Exists(path)) return null;
            Texture2D tex = null;
            try
            {
                byte[] data = File.ReadAllBytes(path);
                tex = new Texture2D(2, 2);
                if (!tex.LoadImage(data))
                {
                    DestroyImmediate(tex);
                    return null;
                }
                return tex;
            }
            catch
            {
                if (tex != null)
                    DestroyImmediate(tex);
                return null;
            }
        }

        /// <summary>释放所有缓存的纹理对象</summary>
        private void ClearCache()
        {
            foreach (var tex in _thumbCache.Values)
                if (tex != null) DestroyImmediate(tex);
            _thumbCache.Clear();

            if (_largePreview != null && !_thumbCache.ContainsValue(_largePreview))
            {
                DestroyImmediate(_largePreview);
                _largePreview = null;
                _largePreviewPath = null;
            }
        }
    }
}

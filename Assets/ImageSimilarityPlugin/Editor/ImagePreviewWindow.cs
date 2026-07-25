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
    /// 以及"保留此图片并替换所有 Prefab 中 Image.sprite 引用"的核心工作流。
    /// </summary>
    public class ImagePreviewWindow : EditorWindow
    {
        private DuplicateGroup _group;
        private int _selectedIndex;
        private Action _onRefreshParent;

        private Dictionary<string, Texture2D> _thumbCache = new Dictionary<string, Texture2D>();
        private Texture2D _largePreview;
        private string _largePreviewPath;

        private Vector2 _thumbScroll;
        private string _statusMsg = "";
        private bool _statusIsError;
        private bool _isReplacing;

        private const int THUMB_HEIGHT = 80;
        private const int MAX_PREVIEW_SIZE = 512;

        // ==================================================================
        //  打开 / 关闭
        // ==================================================================

        /// <summary>
        /// 打开图片预览窗口。
        /// </summary>
        /// <param name="group">图片组数据</param>
        /// <param name="selectedIndex">初始选中图片在组内的索引</param>
        /// <param name="onRefreshParent">操作完成后刷新父窗口的回调</param>
        public static void Open(DuplicateGroup group, int selectedIndex, Action onRefreshParent = null)
        {
            var win = GetWindow<ImagePreviewWindow>(true, "图片预览");
            win._group = group;
            win._selectedIndex = Mathf.Clamp(selectedIndex, 0, group.images.Count - 1);
            win._onRefreshParent = onRefreshParent;
            win._largePreview = null;
            win._largePreviewPath = null;
            win._thumbCache.Clear();
            win._statusMsg = "";
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

            DrawLargePreview();
            EditorGUILayout.Space(4);
            DrawThumbnailRow();
            EditorGUILayout.Space(4);
            DrawStatus();
            EditorGUILayout.Space(4);
            DrawActions();
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
        /// 图片按原始宽高比缩放，最大不超过 MAX_PREVIEW_SIZE。
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

            if (_largePreview != null)
            {
                float scale = Mathf.Min(1f, MAX_PREVIEW_SIZE / Mathf.Max(_largePreview.width, _largePreview.height));
                Rect r = GUILayoutUtility.GetRect(_largePreview.width * scale, _largePreview.height * scale,
                    GUILayout.Width(_largePreview.width * scale), GUILayout.Height(_largePreview.height * scale));
                GUI.DrawTexture(r, _largePreview, ScaleMode.ScaleToFit);
            }
            else
            {
                GUILayout.Label("无法加载图片", GUILayout.Width(200), GUILayout.Height(200));
            }

            GUILayout.Space(10);
            DrawFileInfo(currentPath);
            EditorGUILayout.EndHorizontal();
        }

        /// <summary>
        /// 绘制右侧文件信息：文件名、尺寸、文件大小、修改时间、组内序号、"在 Project 中定位"按钮。
        /// </summary>
        private void DrawFileInfo(string currentPath)
        {
            EditorGUILayout.BeginVertical(GUILayout.Width(220));
            EditorGUILayout.LabelField("文件信息", EditorStyles.boldLabel);
            EditorGUILayout.LabelField("文件名:", Path.GetFileName(currentPath));

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

            if (GUILayout.Button("在 Project 中定位"))
                PluginUtils.PingAsset(currentPath);

            EditorGUILayout.EndVertical();
        }

        // ==================================================================
        //  缩略图行
        // ==================================================================

        /// <summary>
        /// 绘制底部水平滚动缩略图行。
        /// 当前选中图片有蓝色高亮背景，每张缩略图显示 FR2 角标。
        /// 点击缩略图切换大图预览。
        /// </summary>
        private void DrawThumbnailRow()
        {
            EditorGUILayout.LabelField("同组图片 — 点击切换预览", EditorStyles.boldLabel);

            float totalWidth = _group.images.Count * (THUMB_HEIGHT + 14) + 4;

            _thumbScroll = EditorGUILayout.BeginScrollView(_thumbScroll, false, true,
                GUILayout.Height(THUMB_HEIGHT + 38));
            EditorGUILayout.BeginHorizontal(GUILayout.Width(totalWidth));

            for (int i = 0; i < _group.images.Count; i++)
            {
                string path = _group.images[i];
                bool isActive = (i == _selectedIndex);

                Color bg = isActive ? new Color(0.3f, 0.6f, 1f, 0.4f) : Color.clear;
                Rect rowRect = EditorGUILayout.BeginVertical(GUILayout.Width(THUMB_HEIGHT + 12));
                if (isActive) EditorGUI.DrawRect(rowRect, bg);

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

                if (GUI.Button(thumbR, GUIContent.none, GUIStyle.none))
                {
                    _selectedIndex = i;
                    _largePreviewPath = null;
                    _statusMsg = "";
                    Repaint();
                }

                FR2Integration.DrawRefCountBadge(thumbR, path);
                EditorGUILayout.LabelField(Path.GetFileName(path), EditorStyles.centeredGreyMiniLabel,
                    GUILayout.Width(THUMB_HEIGHT + 8));

                EditorGUILayout.EndVertical();
                GUILayout.Space(2);
            }

            EditorGUILayout.EndHorizontal();
            EditorGUILayout.EndScrollView();
        }

        // ==================================================================
        //  操作按钮
        // ==================================================================

        /// <summary>
        /// 绘制底部操作按钮："保留此图片并替换所有引用"。
        /// 先弹出确认对话框，用户确认后启动替换流程。
        /// </summary>
        private void DrawActions()
        {
            EditorGUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            GUI.enabled = !_isReplacing;

            if (GUILayout.Button("保留此图片并替换所有引用", GUILayout.Height(30), GUILayout.Width(220)))
            {
                if (EditorUtility.DisplayDialog("确认操作",
                    $"将保留:\n  {Path.GetFileName(_group.images[_selectedIndex])}\n\n" +
                    "会找到所有 Prefab 中引用同组其他图片的 Image.sprite，替换为这张保留的图片。\n\n继续？",
                    "确认", "取消"))
                {
                    ReplaceReferences();
                }
            }

            GUI.enabled = true;
            EditorGUILayout.EndHorizontal();
        }

        // ==================================================================
        //  引用替换
        // ==================================================================

        /// <summary>
        /// 执行"保留此图并替换所有引用"的核心逻辑：
        /// 加载保留图 Sprite → 查找所有引用同组其他图片的 Prefab →
        /// 逐个替换 Image.sprite → 保存 Prefab 并报告结果。
        /// 引用查找优先使用 FR2，回退到原生扫描。
        /// </summary>
        private void ReplaceReferences()
        {
            _isReplacing = true;
            _statusMsg = "正在查找引用...";
            _statusIsError = false;
            Repaint();

            EditorApplication.delayCall += () =>
            {
                try
                {
                    string keepPath = _group.images[_selectedIndex];
                    var oldPaths = new List<string>();
                    for (int i = 0; i < _group.images.Count; i++)
                        if (i != _selectedIndex) oldPaths.Add(_group.images[i]);

                    string keepAssetPath = PluginUtils.AbsoluteToAssetPath(keepPath);
                    Sprite keepSprite = string.IsNullOrEmpty(keepAssetPath) ? null
                        : AssetDatabase.LoadAssetAtPath<Sprite>(keepAssetPath);

                    if (keepSprite == null)
                    {
                        _statusMsg = "无法加载保留图片的 Sprite。请确认文件格式被 Unity 识别为 Sprite。";
                        _statusIsError = true;
                        _isReplacing = false;
                        Repaint();
                        return;
                    }

                    var prefabsToFix = FR2Integration.FindPrefabsReferencing(oldPaths,
                        (i, total) => { _statusMsg = $"查找引用中... ({i}/{total})"; Repaint(); });
                    if (prefabsToFix.Count == 0)
                    {
                        _statusMsg = "未找到任何 Prefab 引用同组的其他图片，无需替换。";
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

                        try
                        {
                            GameObject root = PrefabUtility.LoadPrefabContents(prefabPath);
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
                            PrefabUtility.UnloadPrefabContents(root);
                        }
                        catch (Exception ex) { Debug.LogError($"处理 Prefab 失败 ({prefabPath}): {ex.Message}"); }
                    }

                    AssetDatabase.SaveAssets();
                    AssetDatabase.Refresh();
                    _statusMsg = $"完成! 修改了 {replacedCount} 个 Prefab，共替换 {totalComponents} 个 Image.sprite 引用。";
                    _statusIsError = false;
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
            try
            {
                byte[] data = File.ReadAllBytes(path);
                var tex = new Texture2D(2, 2);
                tex.LoadImage(data);
                return tex;
            }
            catch { return null; }
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

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

namespace ImageSimilarityPlugin
{
    /// <summary>
    /// Large preview window opened when clicking a thumbnail in the results.
    /// Supports "keep this image and replace all references in prefabs" workflow.
    /// </summary>
    public class ImagePreviewWindow : EditorWindow
    {
        // --- Passed-in data ---
        private DuplicateGroup _group;
        private int _selectedIndex;
        private Action _onRefreshParent; // callback to refresh parent window after delete

        // --- Cache ---
        private Dictionary<string, Texture2D> _thumbCache = new Dictionary<string, Texture2D>();
        private Texture2D _largePreview;
        private string _largePreviewPath;

        // --- UI state ---
        private Vector2 _scrollInfo;
        private string _statusMsg = "";
        private bool _statusIsError;
        private bool _isReplacing;

        // --- Reference finder cache ---
        private static bool? _fr2Available;
        private static MethodInfo _fr2FindMethod;

        private const int THUMB_HEIGHT = 80;
        private const int MAX_PREVIEW_SIZE = 512;

        // ==================================================================
        //  Open
        // ==================================================================

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

        private void OnDisable()
        {
            ClearCache();
        }

        // ==================================================================
        //  GUI
        // ==================================================================

        private void OnGUI()
        {
            if (_group == null)
            {
                EditorGUILayout.LabelField("无数据。");
                return;
            }

            // --- Top: large preview + info ---
            DrawLargePreview();

            EditorGUILayout.Space(4);

            // --- Bottom: thumbnails row ---
            DrawThumbnailRow();

            EditorGUILayout.Space(4);

            // --- Status ---
            if (!string.IsNullOrEmpty(_statusMsg))
            {
                GUI.color = _statusIsError ? Color.red : Color.white;
                EditorGUILayout.LabelField(_statusMsg, EditorStyles.wordWrappedLabel);
                GUI.color = Color.white;
            }

            EditorGUILayout.Space(4);

            // --- Actions ---
            DrawActions();
        }

        // ==================================================================
        //  Large Preview
        // ==================================================================

        private void DrawLargePreview()
        {
            string currentPath = _group.images[_selectedIndex];

            // Load full-size texture
            if (_largePreviewPath != currentPath)
            {
                if (_largePreview != null && !_thumbCache.ContainsValue(_largePreview))
                    DestroyImmediate(_largePreview);

                _largePreview = LoadTexture(currentPath);
                _largePreviewPath = currentPath;
            }

            EditorGUILayout.BeginHorizontal();

            // Preview area
            if (_largePreview != null)
            {
                float texW = _largePreview.width;
                float texH = _largePreview.height;
                float maxDim = Mathf.Max(texW, texH);
                float scale = Mathf.Min(1f, MAX_PREVIEW_SIZE / maxDim);
                float drawW = texW * scale;
                float drawH = texH * scale;

                Rect previewRect = GUILayoutUtility.GetRect(drawW, drawH, GUILayout.Width(drawW), GUILayout.Height(drawH));
                GUI.DrawTexture(previewRect, _largePreview, ScaleMode.ScaleToFit);
            }
            else
            {
                GUILayout.Label("无法加载图片", GUILayout.Width(200), GUILayout.Height(200));
            }

            GUILayout.Space(10);

            // File info
            EditorGUILayout.BeginVertical(GUILayout.Width(220));
            EditorGUILayout.LabelField("文件信息", EditorStyles.boldLabel);

            string shortName = Path.GetFileName(currentPath);
            EditorGUILayout.LabelField("文件名:", shortName);

            if (_largePreview != null)
            {
                EditorGUILayout.LabelField($"尺寸: {_largePreview.width} × {_largePreview.height}");
            }

            try
            {
                var fi = new FileInfo(currentPath);
                if (fi.Exists)
                {
                    EditorGUILayout.LabelField($"文件大小: {FormatFileSize(fi.Length)}");
                    EditorGUILayout.LabelField($"修改时间: {fi.LastWriteTime:yyyy-MM-dd HH:mm}");
                }
            }
            catch { }

            EditorGUILayout.LabelField($"组内序号: {_selectedIndex + 1} / {_group.images.Count}");

            if (GUILayout.Button("在 Project 中定位"))
            {
                PingAsset(currentPath);
            }

            EditorGUILayout.EndVertical();

            EditorGUILayout.EndHorizontal();
        }

        // ==================================================================
        //  Thumbnail Row
        // ==================================================================

        private void DrawThumbnailRow()
        {
            EditorGUILayout.LabelField("同组图片 — 点击切换预览", EditorStyles.boldLabel);
            EditorGUILayout.BeginHorizontal();

            for (int i = 0; i < _group.images.Count; i++)
            {
                string path = _group.images[i];
                bool isActive = (i == _selectedIndex);

                // Highlight active
                Color bg = isActive ? new Color(0.3f, 0.6f, 1f, 0.4f) : Color.clear;
                Rect rowRect = EditorGUILayout.BeginVertical(GUILayout.Width(THUMB_HEIGHT + 12));
                if (isActive)
                    EditorGUI.DrawRect(rowRect, bg);

                // Thumbnail
                Texture2D thumb = GetOrLoadThumb(path);
                Rect thumbR = GUILayoutUtility.GetRect(THUMB_HEIGHT, THUMB_HEIGHT,
                    GUILayout.Width(THUMB_HEIGHT), GUILayout.Height(THUMB_HEIGHT));
                if (thumb != null)
                {
                    float a = (float)thumb.width / Mathf.Max(1, thumb.height);
                    float dw, dh;
                    if (a >= 1) { dw = THUMB_HEIGHT; dh = THUMB_HEIGHT / a; }
                    else { dh = THUMB_HEIGHT; dw = THUMB_HEIGHT * a; }
                    Rect dr = new Rect(thumbR.x + (THUMB_HEIGHT - dw) / 2,
                                       thumbR.y + (THUMB_HEIGHT - dh) / 2, dw, dh);
                    GUI.DrawTexture(dr, thumb, ScaleMode.StretchToFill);
                }

                // Click → switch preview
                if (GUI.Button(thumbR, GUIContent.none, GUIStyle.none))
                {
                    _selectedIndex = i;
                    _largePreviewPath = null; // force reload
                    _statusMsg = "";
                    Repaint();
                }

                // Filename
                EditorGUILayout.LabelField(Path.GetFileName(path), EditorStyles.centeredGreyMiniLabel,
                    GUILayout.Width(THUMB_HEIGHT + 8));

                EditorGUILayout.EndVertical();
                GUILayout.Space(2);
            }

            EditorGUILayout.EndHorizontal();
        }

        // ==================================================================
        //  Actions
        // ==================================================================

        private void DrawActions()
        {
            EditorGUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();

            GUI.enabled = !_isReplacing;

            // Keep this image & replace references to others
            if (GUILayout.Button("保留此图片并替换所有引用", GUILayout.Height(30), GUILayout.Width(220)))
            {
                if (EditorUtility.DisplayDialog("确认操作",
                    $"将保留:\n  {Path.GetFileName(_group.images[_selectedIndex])}\n\n" +
                    "会找到所有 Prefab 中引用同组其他图片的 Image.sprite，\n" +
                    "替换为这张保留的图片。\n\n继续？",
                    "确认", "取消"))
                {
                    ReplaceReferences();
                }
            }

            GUI.enabled = true;
            EditorGUILayout.EndHorizontal();
        }

        // ==================================================================
        //  Reference Replacement
        // ==================================================================

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
                    {
                        if (i != _selectedIndex)
                            oldPaths.Add(_group.images[i]);
                    }

                    // Load the keep sprite
                    string keepAssetPath = AbsoluteToAssetPath(keepPath);
                    Sprite keepSprite = null;
                    if (!string.IsNullOrEmpty(keepAssetPath))
                        keepSprite = AssetDatabase.LoadAssetAtPath<Sprite>(keepAssetPath);

                    if (keepSprite == null)
                    {
                        _statusMsg = "无法加载保留图片的 Sprite。请确认文件格式被 Unity 识别为 Sprite。";
                        _statusIsError = true;
                        _isReplacing = false;
                        Repaint();
                        return;
                    }

                    // Find prefabs referencing the OLD images
                    var prefabsToFix = FindPrefabsReferencing(oldPaths);
                    if (prefabsToFix.Count == 0)
                    {
                        _statusMsg = "未找到任何 Prefab 引用同组的其他图片，无需替换。";
                        _statusIsError = false;
                        _isReplacing = false;
                        Repaint();
                        return;
                    }

                    // Replace in each prefab
                    int replacedCount = 0;
                    int totalComponents = 0;

                    for (int pi = 0; pi < prefabsToFix.Count; pi++)
                    {
                        string prefabPath = prefabsToFix[pi];
                        _statusMsg = $"正在处理 Prefab ({pi + 1}/{prefabsToFix.Count}): {Path.GetFileName(prefabPath)}";
                        Repaint();

                        try
                        {
                            // Load prefab contents for editing
                            GameObject prefabRoot = PrefabUtility.LoadPrefabContents(prefabPath);
                            bool modified = false;

                            // Find all Image components
                            Image[] images = prefabRoot.GetComponentsInChildren<Image>(true);
                            foreach (var img in images)
                            {
                                if (img.sprite == null) continue;

                                string spriteTexPath = AssetDatabase.GetAssetPath(img.sprite);
                                if (string.IsNullOrEmpty(spriteTexPath)) continue;

                                string spriteFullPath = Path.GetFullPath(
                                    Path.Combine(Application.dataPath, "..", spriteTexPath));

                                // Check if this sprite comes from an old image
                                foreach (var oldPath in oldPaths)
                                {
                                    if (PathsEqual(spriteFullPath, oldPath))
                                    {
                                        img.sprite = keepSprite;
                                        EditorUtility.SetDirty(img);
                                        modified = true;
                                        totalComponents++;
                                        break;
                                    }
                                }
                            }

                            if (modified)
                            {
                                PrefabUtility.SaveAsPrefabAsset(prefabRoot, prefabPath);
                                replacedCount++;
                            }

                            PrefabUtility.UnloadPrefabContents(prefabRoot);
                        }
                        catch (Exception ex)
                        {
                            UnityEngine.Debug.LogError($"处理 Prefab 失败 ({prefabPath}): {ex.Message}");
                        }
                    }

                    AssetDatabase.SaveAssets();
                    AssetDatabase.Refresh();

                    _statusMsg = $"完成! 修改了 {replacedCount} 个 Prefab，共替换 {totalComponents} 个 Image.sprite 引用。";
                    _statusIsError = false;

                    // Refresh parent if any
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

        /// <summary>
        /// Find all prefab asset paths that reference any of the given image paths.
        /// Uses FR2 if available, otherwise falls back to native Unity API.
        /// </summary>
        private List<string> FindPrefabsReferencing(List<string> oldImagePaths)
        {
            // Convert absolute paths to asset paths
            var oldAssetPaths = new HashSet<string>();
            foreach (var p in oldImagePaths)
            {
                string ap = AbsoluteToAssetPath(p);
                if (!string.IsNullOrEmpty(ap))
                    oldAssetPaths.Add(ap);
            }

            if (oldAssetPaths.Count == 0)
                return new List<string>();

            // Try FR2 first
            if (HasFR2())
                return FindWithFR2(oldAssetPaths);

            // Fallback: native scan
            return FindWithNativeAPI(oldAssetPaths);
        }

        // ==================================================================
        //  FR2 Integration
        // ==================================================================

        private static bool HasFR2()
        {
            if (_fr2Available.HasValue) return _fr2Available.Value;

            // Look for FR2's main cache type by common assembly-qualified names
            string[] fr2TypeNames = {
                "FR2_Cache, Assembly-CSharp-Editor, Version=0.0.0.0, Culture=neutral, PublicKeyToken=null",
                "FR2_Cache, Assembly-CSharp-firstpass, Version=0.0.0.0, Culture=neutral, PublicKeyToken=null",
            };

            foreach (var name in fr2TypeNames)
            {
                var t = Type.GetType(name);
                if (t != null)
                {
                    // Try to find a "FindReferences" or "FindUsage" method
                    _fr2FindMethod = t.GetMethod("FindReferences", BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance)
                                  ?? t.GetMethod("FindUsage", BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance)
                                  ?? t.GetMethod("FindAssets", BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance);

                    // Also check nested Dependency class
                    var depType = t.GetNestedType("Dependency", BindingFlags.Public);
                    if (depType != null)
                    {
                        _fr2FindMethod = depType.GetMethod("Find", BindingFlags.Public | BindingFlags.Static)
                                      ?? depType.GetMethod("FindAsset", BindingFlags.Public | BindingFlags.Static)
                                      ?? _fr2FindMethod;
                    }

                    _fr2Available = true;
                    return true;
                }
            }

            _fr2Available = false;
            return false;
        }

        private List<string> FindWithFR2(HashSet<string> oldAssetPaths)
        {
            var result = new HashSet<string>();

            // Try the FR2_Cache singleton pattern
            Type fr2Type = Type.GetType("FR2_Cache, Assembly-CSharp-Editor, Version=0.0.0.0, Culture=neutral, PublicKeyToken=null")
                        ?? Type.GetType("FR2_Cache, Assembly-CSharp-firstpass, Version=0.0.0.0, Culture=neutral, PublicKeyToken=null");

            if (fr2Type == null) return FindWithNativeAPI(oldAssetPaths);

            try
            {
                // Get the Cache instance (often a singleton property)
                var cacheProp = fr2Type.GetProperty("Cache", BindingFlags.Public | BindingFlags.Static)
                             ?? fr2Type.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static);
                object cacheInstance = null;

                if (cacheProp != null)
                {
                    cacheInstance = cacheProp.GetValue(null);
                }

                // Try static Dependency property
                var depProp = fr2Type.GetProperty("Dependency", BindingFlags.Public | BindingFlags.Static);
                object depInstance = null;
                if (depProp != null)
                    depInstance = depProp.GetValue(cacheInstance);

                if (depInstance == null) return FindWithNativeAPI(oldAssetPaths);

                // Try to find references for each old asset
                var findMethod = depInstance.GetType().GetMethod("Find",
                    new[] { typeof(string), typeof(bool) });
                if (findMethod == null)
                    findMethod = depInstance.GetType().GetMethod("FindAsset",
                        new[] { typeof(string) });

                if (findMethod == null) return FindWithNativeAPI(oldAssetPaths);

                foreach (var assetPath in oldAssetPaths)
                {
                    try
                    {
                        object references;
                        if (findMethod.GetParameters().Length == 2)
                            references = findMethod.Invoke(depInstance, new object[] { assetPath, false });
                        else
                            references = findMethod.Invoke(depInstance, new object[] { assetPath });

                        if (references == null) continue;

                        // References is typically a list or dictionary - try to iterate
                        if (references is System.Collections.IEnumerable enumerable)
                        {
                            foreach (var item in enumerable)
                            {
                                // Each reference item typically has an "assetPath" or similar property
                                var pathProp = item.GetType().GetProperty("assetPath",
                                    BindingFlags.Public | BindingFlags.Instance)
                                    ?? item.GetType().GetProperty("AssetPath",
                                    BindingFlags.Public | BindingFlags.Instance)
                                    ?? item.GetType().GetProperty("path",
                                    BindingFlags.Public | BindingFlags.Instance);

                                if (pathProp != null)
                                {
                                    string refPath = pathProp.GetValue(item) as string;
                                    if (!string.IsNullOrEmpty(refPath) && refPath.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
                                        result.Add(refPath);
                                }
                            }
                        }
                    }
                    catch { }
                }
            }
            catch { }

            if (result.Count > 0)
            {
                UnityEngine.Debug.Log($"[ImageSimilarityPlugin] FR2 找到 {result.Count} 个引用 Prefab。");
                return new List<string>(result);
            }

            return FindWithNativeAPI(oldAssetPaths);
        }

        // ==================================================================
        //  Native Reference Finding
        // ==================================================================

        private List<string> FindWithNativeAPI(HashSet<string> oldAssetPaths)
        {
            var result = new HashSet<string>();

            // Get all prefab GUIDs
            string[] prefabGuids = AssetDatabase.FindAssets("t:Prefab");
            UnityEngine.Debug.Log($"[ImageSimilarityPlugin] 扫描 {prefabGuids.Length} 个 Prefab 查找引用...");

            for (int i = 0; i < prefabGuids.Length; i++)
            {
                string prefabPath = AssetDatabase.GUIDToAssetPath(prefabGuids[i]);
                if (string.IsNullOrEmpty(prefabPath)) continue;

                // Show progress every 500 prefabs
                if (i % 500 == 0)
                {
                    _statusMsg = $"查找引用中... ({i}/{prefabGuids.Length})";
                    Repaint();
                }

                string[] deps = AssetDatabase.GetDependencies(prefabPath, false);
                foreach (var dep in deps)
                {
                    if (oldAssetPaths.Contains(dep))
                    {
                        result.Add(prefabPath);
                        break;
                    }
                }
            }

            UnityEngine.Debug.Log($"[ImageSimilarityPlugin] 原生扫描完成，找到 {result.Count} 个引用 Prefab。");
            return new List<string>(result);
        }

        // ==================================================================
        //  Helpers
        // ==================================================================

        private Texture2D GetOrLoadThumb(string path)
        {
            if (_thumbCache.TryGetValue(path, out var cached) && cached != null)
                return cached;

            var tex = LoadTexture(path);
            _thumbCache[path] = tex;
            return tex;
        }

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
            catch
            {
                return null;
            }
        }

        private void ClearCache()
        {
            foreach (var tex in _thumbCache.Values)
            {
                if (tex != null) DestroyImmediate(tex);
            }
            _thumbCache.Clear();

            if (_largePreview != null && !_thumbCache.ContainsValue(_largePreview))
            {
                DestroyImmediate(_largePreview);
                _largePreview = null;
                _largePreviewPath = null;
            }
        }

        private void PingAsset(string path)
        {
            string ap = AbsoluteToAssetPath(path);
            if (!string.IsNullOrEmpty(ap))
            {
                var obj = AssetDatabase.LoadMainAssetAtPath(ap);
                if (obj != null)
                {
                    EditorGUIUtility.PingObject(obj);
                    Selection.activeObject = obj;
                }
            }
            else
            {
                EditorUtility.RevealInFinder(path);
            }
        }

        private static string AbsoluteToAssetPath(string absolutePath)
        {
            string assetsRoot = Application.dataPath.Replace('/', Path.DirectorySeparatorChar);
            string full = Path.GetFullPath(absolutePath);
            if (!full.StartsWith(assetsRoot, StringComparison.OrdinalIgnoreCase))
                return null;
            string relative = full.Substring(assetsRoot.Length).TrimStart(Path.DirectorySeparatorChar);
            return "Assets/" + relative.Replace('\\', '/');
        }

        private static bool PathsEqual(string a, string b)
        {
            return string.Equals(
                Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar, '/'),
                Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar, '/'),
                StringComparison.OrdinalIgnoreCase);
        }

        private static string FormatFileSize(long bytes)
        {
            if (bytes < 1024) return $"{bytes} B";
            if (bytes < 1024 * 1024) return $"{bytes / 1024f:F1} KB";
            if (bytes < 1024 * 1024 * 1024) return $"{bytes / (1024f * 1024f):F1} MB";
            return $"{bytes / (1024f * 1024f * 1024f):F2} GB";
        }
    }
}

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

        /// <summary>
        /// Whether FR2 is installed and its cache is populated (ready for queries).
        /// </summary>
        public static bool IsFR2Ready
        {
            get
            {
                if (!HasFR2()) return false;
                try
                {
                    Type fr2CacheType = FindTypeInAllAssemblies("FR2_Cache");
                    var apiProp = fr2CacheType.GetProperty("Api",
                        BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Public);
                    var cache = apiProp?.GetValue(null);
                    var assetMapField = cache?.GetType().GetField("AssetMap",
                        BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public);
                    var assetMap = assetMapField?.GetValue(cache);
                    var countProp = assetMap?.GetType().GetProperty("Count");
                    return (int)(countProp?.GetValue(assetMap) ?? 0) > 0;
                }
                catch { return false; }
            }
        }
        private static Dictionary<string, int> _refCountCache = new Dictionary<string, int>();

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

                // FR2 reference count badge (drawn after button to be on top)
                DrawRefCountBadge(thumbR, path);

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

        /// <summary>
        /// Find a type by name across all loaded assemblies. Searches both full name
        /// (namespace.TypeName) and simple name (TypeName) to handle namespace variations.
        /// </summary>
        private static Type FindTypeInAllAssemblies(string typeName)
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                // Try full name first (e.g. "FR2_Cache")
                var t = asm.GetType(typeName);
                if (t != null) return t;

                // Try with namespace prefix (e.g. "FR2.FR2_Cache" or "FindReference2.FR2_Cache")
                t = asm.GetType("FR2." + typeName);
                if (t != null) return t;
                t = asm.GetType("FindReference2." + typeName);
                if (t != null) return t;

                // Fallback: iterate all types and match by simple name
                try
                {
                    foreach (var exportedType in asm.GetExportedTypes())
                    {
                        if (exportedType.Name == typeName)
                            return exportedType;
                    }
                }
                catch { }
            }
            return null;
        }

        /// <summary>
        /// Diagnostic: dump all loaded assemblies and any types matching a filter to Console.
        /// Call from Unity's Console: ImageSimilarityPlugin.ImagePreviewWindow.DiagnoseFR2();
        /// </summary>
        [MenuItem("Tools/诊断 FR2 检测")]
        public static void DiagnoseFR2()
        {
            UnityEngine.Debug.Log("=== FR2 诊断 ===");
            var assemblies = AppDomain.CurrentDomain.GetAssemblies();
            UnityEngine.Debug.Log($"已加载程序集总数: {assemblies.Length}");

            foreach (var asm in assemblies)
            {
                string name = asm.GetName().Name;
                // Look for FR2 or FindReference related assemblies
                if (name.IndexOf("FR2", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    name.IndexOf("FindReference", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    name.IndexOf("Find Ref", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    UnityEngine.Debug.Log($"  ▶ 程序集: {name}   ({asm.GetTypes().Length} types)");
                    foreach (var t in asm.GetTypes())
                    {
                        if (t.Name.Contains("FR2") || t.Name.Contains("Cache"))
                            UnityEngine.Debug.Log($"      - {t.FullName}");
                    }
                }
            }

            // Also search for FR2_Cache in all assemblies
            foreach (var asm in assemblies)
            {
                var t = asm.GetType("FR2_Cache");
                if (t != null)
                {
                    UnityEngine.Debug.Log($"  ★ 找到 FR2_Cache 在: {asm.GetName().Name}");
                    UnityEngine.Debug.Log($"      FullName: {t.FullName}");
                    UnityEngine.Debug.Log($"      Namespace: {t.Namespace}");
                }
            }

            // Check for FR2 namespace variations
            string[] typeNames = { "FR2_Cache", "FR2Cache", "FR2.FR2_Cache", "FindReference2.FR2_Cache" };
            foreach (var tn in typeNames)
            {
                var t = FindTypeInAllAssemblies(tn);
                if (t != null)
                    UnityEngine.Debug.Log($"  ✓ 找到: {tn} in assembly {t.Assembly.GetName().Name}");
                else
                    UnityEngine.Debug.Log($"  ✗ 未找到: {tn}");
            }

            UnityEngine.Debug.Log("=== 诊断完毕 ===");
        }

        /// <summary>
        /// True if FR2 (Find Reference 2) is installed in this project.
        /// Key API:
        ///   FR2_Cache.Api.AssetMap[guid]  → FR2_Asset
        ///   FR2_Asset.FindUsedBy(asset)   → List&lt;FR2_Asset&gt; (who references this)
        ///   FR2_Asset.assetPath           → string
        /// </summary>
        public static bool HasFR2()
        {
            if (_fr2Available.HasValue) return _fr2Available.Value;

            // Search all loaded assemblies for FR2_Cache
            Type fr2CacheType = FindTypeInAllAssemblies("FR2_Cache");
            if (fr2CacheType == null)
            {
                UnityEngine.Debug.Log("[ImageSimilarityPlugin] FR2 未检测到。");
                _fr2Available = false;
                return false;
            }

            // Verify we can access Api . AssetMap
            try
            {
                var apiProp = fr2CacheType.GetProperty("Api",
                    BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Public);
                if (apiProp == null) { _fr2Available = false; return false; }

                var cache = apiProp.GetValue(null);
                if (cache == null) { _fr2Available = false; return false; }

                var assetMapField = cache.GetType().GetField("AssetMap",
                    BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public);
                if (assetMapField == null) { _fr2Available = false; return false; }

                UnityEngine.Debug.Log("[ImageSimilarityPlugin] FR2 已检测到并可用。");
                _fr2Available = true;
                return true;
            }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogWarning($"[ImageSimilarityPlugin] FR2 检测异常: {ex.Message}");
                _fr2Available = false;
                return false;
            }
        }

        private List<string> FindWithFR2(HashSet<string> oldAssetPaths)
        {
            var result = new HashSet<string>();

            try
            {
                Type fr2CacheType = FindTypeInAllAssemblies("FR2_Cache");
                if (fr2CacheType == null) return FindWithNativeAPI(oldAssetPaths);

                // FR2_Cache.Api → cache instance
                var apiProp = fr2CacheType.GetProperty("Api",
                    BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Public);
                var cache = apiProp?.GetValue(null);
                if (cache == null) return FindWithNativeAPI(oldAssetPaths);

                // cache.AssetMap → Dictionary<string, FR2_Asset>
                var assetMapField = cache.GetType().GetField("AssetMap",
                    BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public);
                var assetMap = assetMapField?.GetValue(cache);
                if (assetMap == null) return FindWithNativeAPI(oldAssetPaths);

                // Find the indexer or TryGetValue on the dictionary
                var tryGetMethod = assetMap.GetType().GetMethod("TryGetValue");
                var indexerProp = assetMap.GetType().GetProperty("Item");

                // FR2_Asset.FindUsedBy(FR2_Asset) → List<FR2_Asset>
                Type fr2AssetType = null;
                System.Reflection.MethodInfo findUsedByMethod = null;

                foreach (var assetPath in oldAssetPaths)
                {
                    try
                    {
                        string guid = AssetDatabase.AssetPathToGUID(assetPath);
                        if (string.IsNullOrEmpty(guid)) continue;

                        // assetMap[guid] → FR2_Asset
                        object fr2Asset = null;
                        if (indexerProp != null)
                        {
                            fr2Asset = indexerProp.GetValue(assetMap, new object[] { guid });
                        }
                        else if (tryGetMethod != null)
                        {
                            var args = new object[] { guid, null };
                            if ((bool)tryGetMethod.Invoke(assetMap, args))
                                fr2Asset = args[1];
                        }
                        if (fr2Asset == null) continue;

                        // Cache the type and method reference
                        if (fr2AssetType == null)
                        {
                            fr2AssetType = fr2Asset.GetType();
                            findUsedByMethod = fr2AssetType.GetMethod("FindUsedBy",
                                BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Public);
                        }

                        if (findUsedByMethod == null) continue;

                        // FR2_Asset.FindUsedBy(asset) → List<FR2_Asset>
                        var usedByList = findUsedByMethod.Invoke(null, new[] { fr2Asset })
                            as System.Collections.IList;
                        if (usedByList == null || usedByList.Count == 0) continue;

                        // Extract assetPath from each FR2_Asset
                        var pathField = fr2AssetType.GetField("assetPath",
                            BindingFlags.Public | BindingFlags.Instance);

                        foreach (var item in usedByList)
                        {
                            string refPath = pathField?.GetValue(item) as string;
                            if (!string.IsNullOrEmpty(refPath) &&
                                refPath.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
                            {
                                result.Add(refPath);
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

            // If FR2 found nothing (cache may be stale), fall back to native
            return FindWithNativeAPI(oldAssetPaths);
        }

        /// <summary>
        /// Get the number of assets (prefabs, scenes, etc.) that reference this image.
        /// Returns 0 if FR2 is not available or the asset is unused.
        /// Results are cached for the session.
        /// </summary>
        public static int GetReferenceCount(string absolutePath)
        {
            if (!HasFR2()) return 0;

            string assetPath = AbsoluteToAssetPath(absolutePath);
            if (string.IsNullOrEmpty(assetPath)) return 0;

            if (_refCountCache.TryGetValue(assetPath, out int cached))
                return cached;

            int count = 0;
            try
            {
                Type fr2CacheType = FindTypeInAllAssemblies("FR2_Cache");
                if (fr2CacheType == null) { _refCountCache[assetPath] = 0; return 0; }

                var apiProp = fr2CacheType.GetProperty("Api",
                    BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Public);
                var cache = apiProp?.GetValue(null);
                if (cache == null) { _refCountCache[assetPath] = 0; return 0; }

                var assetMapField = cache.GetType().GetField("AssetMap",
                    BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public);
                var assetMap = assetMapField?.GetValue(cache);
                if (assetMap == null) { _refCountCache[assetPath] = 0; return 0; }

                string guid = AssetDatabase.AssetPathToGUID(assetPath);
                if (string.IsNullOrEmpty(guid)) { _refCountCache[assetPath] = 0; return 0; }

                var indexerProp = assetMap.GetType().GetProperty("Item");
                object fr2Asset = indexerProp?.GetValue(assetMap, new object[] { guid });

                if (fr2Asset == null) { _refCountCache[assetPath] = 0; return 0; }

                var findUsedByMethod = fr2Asset.GetType().GetMethod("FindUsedBy",
                    BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Public);
                if (findUsedByMethod != null)
                {
                    var usedByList = findUsedByMethod.Invoke(null, new[] { fr2Asset })
                        as System.Collections.IList;
                    count = usedByList?.Count ?? 0;
                }
                else
                {
                    // Fallback: check UsedByMap field directly
                    var usedByMapField = fr2Asset.GetType().GetField("UsedByMap",
                        BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public);
                    if (usedByMapField != null)
                    {
                        var usedByMap = usedByMapField.GetValue(fr2Asset);
                        var countProp = usedByMap?.GetType().GetProperty("Count");
                        count = (int)(countProp?.GetValue(usedByMap) ?? 0);
                    }
                }
            }
            catch { }

            _refCountCache[assetPath] = count;
            return count;
        }

        /// <summary>
        /// Clear the reference count cache (call after FR2 cache rebuild).
        /// </summary>
        public static void ClearRefCountCache()
        {
            _refCountCache.Clear();
        }

        /// <summary>
        /// Draw an FR2 reference-count badge in the top-right corner of a thumbnail rect.
        /// </summary>
        public static void DrawRefCountBadge(Rect thumbRect, string imagePath)
        {
            if (!HasFR2()) return;

            int count = GetReferenceCount(imagePath);
            if (count <= 0) return;

            // Badge background (small circle/square in top-right)
            float badgeSize = 20f;
            Rect badgeRect = new Rect(
                thumbRect.xMax - badgeSize - 2,
                thumbRect.y + 2,
                badgeSize,
                badgeSize);

            // Draw circle background
            Color oldColor = GUI.color;
            GUI.color = new Color(0.2f, 0.5f, 0.9f, 0.85f);
            GUI.Box(badgeRect, "", EditorStyles.helpBox);

            // Draw count text
            var labelStyle = new GUIStyle(EditorStyles.miniLabel)
            {
                alignment = TextAnchor.MiddleCenter,
                fontStyle = FontStyle.Bold,
                fontSize = 10,
                normal = { textColor = Color.white }
            };
            GUI.color = Color.white;
            GUI.Label(badgeRect, count > 99 ? "99+" : count.ToString(), labelStyle);
            GUI.color = oldColor;
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

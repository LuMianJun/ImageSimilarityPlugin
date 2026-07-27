using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace ImageSimilarityPlugin
{
    /// <summary>
    /// 相似图片选择器窗口。
    /// 外部模块调用 ImageSimilarityQuery.ShowPicker() 时弹出。
    /// 展示与查询图片相似的已有图片列表，用户可选择一张替代导入，
    /// 也可以关闭窗口表示"不选用，继续导入原图"。
    /// </summary>
    public class SimilarImagePickerWindow : EditorWindow
    {
        // ===== 输入参数 =====
        private string _queryImagePath;
        private string _queryImageName;
        private string _folderPath;
        private QueryResultData _results;
        private Action<string> _onPicked;       // 选中回调，参数为选中图片的绝对路径；null 表示未选
        private Action _onCancelled;            // 取消/关闭回调

        // ===== 内部状态 =====
        private PythonRunner _runner;
        private string _statusMessage = "";
        private bool _statusIsError;
        private bool _isSearching;
        private bool _hasSearched;
        private bool _callbackInvoked;

        // ===== UI 缓存 =====
        private Vector2 _scrollPos;
        private Dictionary<string, Texture2D> _thumbnailCache = new Dictionary<string, Texture2D>();
        private const int THUMB_SIZE = 80;
        private const int MAX_PREVIEW_SIZE = 256;

        private Texture2D _queryPreview;

        // ==================================================================
        //  打开 / 关闭
        // ==================================================================

        /// <summary>
        /// 创建并打开选择器窗口。
        /// 不要直接调用，使用 ImageSimilarityQuery.ShowPicker()。
        /// </summary>
        public static SimilarImagePickerWindow Open(
            string queryImagePath,
            string folderPath,
            float threshold,
            int topK,
            Action<string> onPicked,
            Action onCancelled)
        {
            // 每次调用独立建窗，避免并发导入流程复用窗口并覆盖彼此的回调。
            var win = CreateInstance<SimilarImagePickerWindow>();
            win.titleContent = new GUIContent("相似图片选择器");
            win._queryImagePath = queryImagePath;
            win._queryImageName = Path.GetFileName(queryImagePath);
            win._folderPath = folderPath;
            win._results = null;
            win._onPicked = onPicked;
            win._onCancelled = onCancelled;
            win._statusMessage = "正在搜索相似图片...";
            win._statusIsError = false;
            win._isSearching = true;
            win._hasSearched = false;
            win._callbackInvoked = false;
            win.minSize = new Vector2(520, 400);
            win.maxSize = new Vector2(800, 900);

            // 启动查询
            win._runner = ImageSimilarityQuery.QueryAsync(
                queryImagePath: queryImagePath,
                folderPath: folderPath,
                threshold: threshold,
                topK: topK,
                onComplete: result => win.OnQueryComplete(result),
                onError: err => win.OnQueryError(err)
            );

            win.ShowUtility();
            return win;
        }

        private void OnDisable()
        {
            _runner?.Cancel();
            ClearThumbnailCache();
            if (!_callbackInvoked)
                CompleteSelection(null, true);
        }

        // ==================================================================
        //  查询回调
        // ==================================================================

        private void OnQueryComplete(QueryResultData result)
        {
            var imagePaths = new List<string>();
            if (result?.results != null)
            {
                foreach (SimilarImage image in result.results)
                    if (!string.IsNullOrEmpty(image?.image_path))
                        imagePaths.Add(image.image_path);
            }
            FR2Integration.RefreshReferenceCountsIfPending(
                imagePaths,
                _ => { if (this != null) Repaint(); });
            _results = result;
            _isSearching = false;
            _hasSearched = true;

            if (result?.results == null || result.results.Count == 0)
            {
                int failedCount = result?.failed_images?.Count ?? 0;
                _statusMessage = $"未找到与 \"{_queryImageName}\" 相似的图片。" +
                                 (failedCount > 0 ? $" 跳过 {failedCount} 张处理失败的图片。" : string.Empty) +
                                 "\n你可以关闭窗口，继续使用原图。";
                _statusIsError = false;
            }
            else
            {
                int failedCount = result.failed_images?.Count ?? 0;
                _statusMessage = $"找到 {result.results.Count} 张相似图片（共扫描 {result.total_images} 张，耗时 {result.elapsed_seconds:F1} 秒）" +
                                 (failedCount > 0 ? $"，跳过 {failedCount} 张处理失败的图片" : string.Empty);
                _statusIsError = false;
            }
            Repaint();
        }

        private void OnQueryError(string error)
        {
            _isSearching = false;
            _hasSearched = true;
            _statusMessage = $"搜索失败: {error}";
            _statusIsError = true;
            Repaint();
        }

        // ==================================================================
        //  主 GUI
        // ==================================================================

        private void OnGUI()
        {
            DrawHeader();
            EditorGUILayout.Space(5);

            if (_isSearching)
            {
                DrawSearchingState();
            }
            else if (_hasSearched)
            {
                DrawResults();
                EditorGUILayout.Space(8);
                DrawFooter();
            }
        }

        /// <summary>
        /// 顶部：查询图片预览 + 说明文字
        /// </summary>
        private void DrawHeader()
        {
            EditorGUILayout.BeginHorizontal(EditorStyles.helpBox);

            // 查询图片缩略图
            if (_queryPreview == null)
                _queryPreview = LoadTexture(_queryImagePath);

            if (_queryPreview != null)
            {
                float scale = Mathf.Min(1f, (float)MAX_PREVIEW_SIZE / Mathf.Max(_queryPreview.width, _queryPreview.height));
                float w = _queryPreview.width * scale;
                float h = _queryPreview.height * scale;
                Rect r = GUILayoutUtility.GetRect(w, h, GUILayout.Width(w), GUILayout.Height(h));
                GUI.DrawTexture(r, _queryPreview, ScaleMode.ScaleToFit);
            }
            else
            {
                GUILayout.Label("?", GUILayout.Width(64), GUILayout.Height(64));
            }

            GUILayout.Space(10);

            // 说明 + 原图详细属性
            EditorGUILayout.BeginVertical();
            EditorGUILayout.LabelField("查询图片 — 原图属性", EditorStyles.boldLabel);
            EditorGUILayout.LabelField(_queryImageName, EditorStyles.wordWrappedLabel);

            string displayFolder = PluginUtils.ToDisplayPath(_folderPath);
            EditorGUILayout.LabelField(
                $"搜索目录: {displayFolder}",
                EditorStyles.wordWrappedMiniLabel);

            // 原图文件大小
            try
            {
                var fi = new FileInfo(_queryImagePath);
                if (fi.Exists)
                    EditorGUILayout.LabelField($"文件大小: {PluginUtils.FormatFileSize(fi.Length)}", EditorStyles.miniLabel);
            }
            catch { }

            // 原图像素尺寸
            string dims = _queryPreview != null
                ? $"{_queryPreview.width} × {_queryPreview.height}"
                : "? × ?";
            EditorGUILayout.LabelField($"图片尺寸: {dims}", EditorStyles.miniLabel);

            EditorGUILayout.Space(5);
            EditorGUILayout.LabelField(
                "系统检测到项目中存在与上图相似的图片。\n你可以选择一张已有的图片来替代导入，\n避免项目中产生重复资源。",
                EditorStyles.wordWrappedMiniLabel);
            EditorGUILayout.EndVertical();

            EditorGUILayout.EndHorizontal();
        }

        /// <summary>
        /// 搜索中状态：进度条 + 提示
        /// </summary>
        private void DrawSearchingState()
        {
            if (_runner != null)
            {
                Rect r = EditorGUILayout.GetControlRect(false, 24);
                EditorGUI.ProgressBar(r, _runner.Progress, $"正在搜索相似图片... {(_runner.Progress * 100f):F0}%");
            }

            if (!string.IsNullOrEmpty(_statusMessage))
            {
                GUI.color = _statusIsError ? Color.red : Color.white;
                EditorGUILayout.LabelField(_statusMessage, EditorStyles.wordWrappedLabel);
                GUI.color = Color.white;
            }

            // 搜索中也可以取消
            EditorGUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("取消", GUILayout.Width(80), GUILayout.Height(28)))
            {
                _runner?.Cancel();
                CompleteSelection(null, true);
                Close();
            }
            EditorGUILayout.EndHorizontal();
        }

        /// <summary>
        /// 结果列表区域
        /// </summary>
        private void DrawResults()
        {
            // 状态消息
            if (!string.IsNullOrEmpty(_statusMessage))
            {
                GUI.color = _statusIsError ? Color.red : Color.white;
                EditorGUILayout.LabelField(_statusMessage, EditorStyles.wordWrappedLabel);
                GUI.color = Color.white;
            }

            if (_results?.results == null || _results.results.Count == 0)
                return;

            EditorGUILayout.LabelField("相似图片 — 点击选择一个:", EditorStyles.boldLabel);

            _scrollPos = EditorGUILayout.BeginScrollView(_scrollPos);

            for (int i = 0; i < _results.results.Count; i++)
            {
                DrawResultRow(_results.results[i]);
                EditorGUILayout.Space(3);
            }

            EditorGUILayout.EndScrollView();
        }

        /// <summary>
        /// 单条结果行
        /// </summary>
        private void DrawResultRow(SimilarImage img)
        {
            EditorGUILayout.BeginHorizontal(EditorStyles.helpBox);

            // 排名
            EditorGUILayout.LabelField($"#{img.rank}", GUILayout.Width(30));

            // 缩略图
            Texture2D thumb = GetThumbnail(img.image_path);
            Rect thumbRect = GUILayoutUtility.GetRect(THUMB_SIZE, THUMB_SIZE,
                GUILayout.Width(THUMB_SIZE), GUILayout.Height(THUMB_SIZE));
            EditorGUI.DrawRect(thumbRect, new Color(0.2f, 0.2f, 0.2f, 0.5f));
            if (thumb != null)
            {
                float a = (float)thumb.width / Mathf.Max(1, thumb.height);
                float dw = a >= 1 ? THUMB_SIZE : THUMB_SIZE * a;
                float dh = a >= 1 ? THUMB_SIZE / a : THUMB_SIZE;
                GUI.DrawTexture(new Rect(thumbRect.x + (THUMB_SIZE - dw) / 2,
                    thumbRect.y + (THUMB_SIZE - dh) / 2, dw, dh), thumb, ScaleMode.StretchToFill);
            }
            FR2Integration.DrawRefCountBadge(thumbRect, img.image_path);

            GUILayout.Space(8);

            // 文件信息
            EditorGUILayout.BeginVertical();
            EditorGUILayout.LabelField(Path.GetFileName(img.image_path), EditorStyles.boldLabel);
            string displayPath = PluginUtils.ToDisplayPath(img.image_path);
            EditorGUILayout.LabelField(
                new GUIContent(displayPath, displayPath),
                EditorStyles.miniLabel);
            try
            {
                var fi = new FileInfo(img.image_path);
                if (fi.Exists)
                {
                    string dims = thumb != null ? $"{thumb.width} × {thumb.height}" : "? × ?";
                    EditorGUILayout.LabelField($"{PluginUtils.FormatFileSize(fi.Length)}  |  {dims}", EditorStyles.miniLabel);
                }
            }
            catch { }
            EditorGUILayout.EndVertical();

            GUILayout.FlexibleSpace();

            // 右侧：相似度 + 选择按钮
            EditorGUILayout.BeginVertical(GUILayout.Width(140));

            // 相似度分数条
            EditorGUILayout.LabelField($"相似度: {img.similarity:P1}", GUILayout.Width(120));
            Rect barRect = GUILayoutUtility.GetRect(120, 14);
            EditorGUI.DrawRect(new Rect(barRect.x, barRect.y, barRect.width, barRect.height),
                new Color(0.3f, 0.3f, 0.3f));
            Color barColor = img.similarity > 0.90f ? Color.green :
                             img.similarity > 0.80f ? new Color(1f, 0.8f, 0f) : Color.red;
            EditorGUI.DrawRect(new Rect(barRect.x, barRect.y, barRect.width * img.similarity, barRect.height), barColor);

            EditorGUILayout.Space(3);

            // 选择按钮
            GUI.backgroundColor = new Color(0.4f, 0.8f, 0.4f);
            if (GUILayout.Button("✔ 选择此图片替代导入", GUILayout.Height(28)))
            {
                CompleteSelection(img.image_path, false);
                Close();
            }
            GUI.backgroundColor = Color.white;

            EditorGUILayout.EndVertical();

            EditorGUILayout.EndHorizontal();
        }

        /// <summary>
        /// 底部：不选择按钮
        /// </summary>
        private void DrawFooter()
        {
            EditorGUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();

            if (GUILayout.Button("不选择，使用原图导入", GUILayout.Height(30), GUILayout.Width(200)))
            {
                CompleteSelection(null, false);
                Close();
            }

            GUILayout.FlexibleSpace();
            EditorGUILayout.EndHorizontal();
        }

        /// <summary>
        /// 确保窗口无论通过按钮、取消还是右上角关闭，都只回调一次。
        /// </summary>
        private void CompleteSelection(string selectedPath, bool cancelled)
        {
            if (_callbackInvoked) return;
            _callbackInvoked = true;

            Action<string> picked = _onPicked;
            Action onCancelled = _onCancelled;
            _onPicked = null;
            _onCancelled = null;

            try
            {
                picked?.Invoke(selectedPath);
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
            }

            if (!cancelled) return;
            try
            {
                onCancelled?.Invoke();
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
            }
        }

        // ==================================================================
        //  纹理加载
        // ==================================================================

        private Texture2D GetThumbnail(string path)
        {
            if (_thumbnailCache.TryGetValue(path, out var cached) && cached != null)
                return cached;
            var tex = LoadTexture(path);
            _thumbnailCache[path] = tex;
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
            catch { return null; }
        }

        private void ClearThumbnailCache()
        {
            foreach (var tex in _thumbnailCache.Values)
                if (tex != null) DestroyImmediate(tex);
            _thumbnailCache.Clear();

            if (_queryPreview != null)
            {
                DestroyImmediate(_queryPreview);
                _queryPreview = null;
            }
        }
    }
}

using System;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace ImageSimilarityPlugin
{
    /// <summary>
    /// 以图搜图的公开静态 API。
    /// 其他模块（如导入流程、详情页等）可直接调用 QueryAsync() 异步查询相似图片，
    /// 或调用 ShowPicker() 弹出选择器窗口让用户交互选择。
    ///
    /// 调用示例（纯数据查询）：
    /// <code>
    /// var runner = ImageSimilarityQuery.QueryAsync(
    ///     importedImagePath,
    ///     Application.dataPath,
    ///     threshold: 0.85f,
    ///     topK: 10,
    ///     onComplete: result => {
    ///         foreach (var img in result.results)
    ///             Debug.Log($"相似图片: {img.image_path} ({img.similarity:F2%})");
    ///     },
    ///     onError: err => Debug.LogWarning(err)
    /// );
    /// </code>
    ///
    /// 调用示例（弹出选择器——推荐用于导入流程）：
    /// <code>
    /// ImageSimilarityQuery.ShowPicker(
    ///     newImagePath,
    ///     Application.dataPath,
    ///     threshold: 0.85f,
    ///     onPicked: selectedPath => {
    ///         if (selectedPath != null)
    ///         {
    ///             // 用户选了已有图片，用它替代导入
    ///             var sprite = AssetDatabase.LoadAssetAtPath&lt;Sprite&gt;(
    ///                 PluginUtils.AbsoluteToAssetPath(selectedPath));
    ///             prefabImage.sprite = sprite;
    ///         }
    ///         else
    ///         {
    ///             // 用户没选，继续正常导入流程
    ///             ImportOriginalImage();
    ///         }
    ///     }
    /// );
    /// </code>
    /// </summary>
    public static class ImageSimilarityQuery
    {
        /// <summary>
        /// 特征缓存目录。扫描时自动在此保存 .npy + manifest，查询时优先加载缓存。
        /// </summary>
        public static string CacheDir =>
            Path.Combine(Application.temporaryCachePath, "ImageSimilarityPlugin", "features");

        /// <summary>
        /// 异步查询与指定图片视觉上相似的图片。
        /// 搜索候选会自动排除 ExcludedDirectorySettings 中配置的目录。
        /// </summary>
        /// <param name="queryImagePath">查询图片的绝对路径</param>
        /// <param name="folderPath">搜索目标文件夹的绝对路径（通常为 Application.dataPath）</param>
        /// <param name="threshold">余弦相似度阈值 (0~1)，默认 0.80。越高越严格</param>
        /// <param name="topK">最大返回结果数，默认 50</param>
        /// <param name="recursive">是否递归子目录，默认 true</param>
        /// <param name="workers">特征提取并行线程数，默认 4</param>
        /// <param name="onComplete">查询完成回调（主线程），参数为查询结果</param>
        /// <param name="onError">查询失败回调（主线程），参数为错误描述</param>
        /// <returns>
        /// 可用于取消查询的 PythonRunner 实例，或 null（验证失败时立即返回）。
        /// </returns>
        public static PythonRunner QueryAsync(
            string queryImagePath,
            string folderPath,
            float threshold = 0.80f,
            int topK = 50,
            bool recursive = true,
            int workers = 4,
            Action<QueryResultData> onComplete = null,
            Action<string> onError = null)
        {
            // 验证
            if (string.IsNullOrEmpty(queryImagePath) || !File.Exists(queryImagePath))
            {
                onError?.Invoke($"查询图片不存在: {PluginUtils.ToDisplayPath(queryImagePath)}");
                return null;
            }

            if (string.IsNullOrEmpty(folderPath) || !Directory.Exists(folderPath))
            {
                onError?.Invoke($"目标文件夹不存在: {PluginUtils.ToDisplayPath(folderPath)}");
                return null;
            }

            if (threshold < 0f || threshold > 1f)
            {
                onError?.Invoke("相似度阈值必须在 0 到 1 之间。");
                return null;
            }

            if (topK < 1 || workers < 1)
            {
                onError?.Invoke("最大结果数和线程数必须大于或等于 1。");
                return null;
            }

            var runner = new PythonRunner();
            runner.StartQuery(
                queryImagePath: queryImagePath,
                folderPath: folderPath,
                threshold: threshold,
                topK: topK,
                recursive: recursive,
                workers: workers,
                useCache: true,
                onComplete: onComplete,
                onError: onError
            );
            return runner;
        }

        /// <summary>
        /// 弹出相似图片选择器窗口。
        /// 自动搜索项目中与 queryImagePath 相似的图片，
        /// 并应用当前项目的排除目录设置。
        /// 用户可选择一张已有图片来替代导入，也可关闭窗口表示不选用。
        /// </summary>
        /// <param name="queryImagePath">查询图片（即将导入的新图片）的绝对路径</param>
        /// <param name="folderPath">搜索目标文件夹（通常为 Application.dataPath）</param>
        /// <param name="threshold">相似度阈值，默认 0.80</param>
        /// <param name="topK">最大候选数，默认 30</param>
        /// <param name="onPicked">
        ///   用户选择回调：参数为选中图片的绝对路径；
        ///   若用户关闭窗口/选择"不选用"，参数为 null。
        /// </param>
        /// <param name="onCancelled">用户取消/关闭窗口回调（可选）</param>
        /// <returns>选择器窗口实例</returns>
        public static SimilarImagePickerWindow ShowPicker(
            string queryImagePath,
            string folderPath,
            float threshold = 0.80f,
            int topK = 30,
            Action<string> onPicked = null,
            Action onCancelled = null)
        {
            return SimilarImagePickerWindow.Open(
                queryImagePath, folderPath, threshold, topK, onPicked, onCancelled);
        }

        // ==================================================================
        //  Project 图片右键入口
        // ==================================================================

        /// <summary>
        /// Project 窗口右键菜单：对选中的图片查找项目中相似图片。
        /// </summary>
        [MenuItem("Assets/查找相似图片", false, 30)]
        private static void ShowPickerFromAsset()
        {
            var obj = Selection.activeObject;
            if (obj == null) return;
            string assetPath = AssetDatabase.GetAssetPath(obj);
            if (string.IsNullOrEmpty(assetPath)) return;
            string fullPath = Path.GetFullPath(Path.Combine(Application.dataPath, "..", assetPath));
            if (!File.Exists(fullPath)) return;

            // 右键查询必须先确认范围，默认使用图片所在目录，避免无意间扫描整个 Assets。
            string defaultFolder = Path.GetDirectoryName(fullPath);
            string searchFolder = EditorUtility.OpenFolderPanel(
                "选择相似图片搜索目录", defaultFolder ?? Application.dataPath, string.Empty);
            if (string.IsNullOrEmpty(searchFolder)) return;

            ShowPicker(fullPath, searchFolder,
                onPicked: selectedPath =>
                {
                    if (selectedPath != null)
                        Debug.Log($"[ImageSimilarityQuery] 用户选择了: {PluginUtils.ToDisplayPath(selectedPath)}");
                    else
                        Debug.Log("[ImageSimilarityQuery] 用户未选择，继续使用原图。");
                });
        }

        /// <summary>
        /// 验证 Assets 右键菜单项：只有选中 Texture2D/Sprite 等图片资源时才显示。
        /// </summary>
        [MenuItem("Assets/查找相似图片", true)]
        private static bool ShowPickerFromAssetValidate()
        {
            var obj = Selection.activeObject;
            if (obj == null) return false;
            string path = AssetDatabase.GetAssetPath(obj);
            if (string.IsNullOrEmpty(path)) return false;
            string ext = Path.GetExtension(path).ToLower();
            return ext == ".png" || ext == ".jpg" || ext == ".jpeg"
                || ext == ".bmp" || ext == ".gif" || ext == ".tiff"
                || ext == ".tif" || ext == ".webp"
                || ext == ".psd" || ext == ".tga";
        }

    }
}

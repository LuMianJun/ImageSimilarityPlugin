using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace ImageSimilarityPlugin
{
    /// <summary>
    /// 插件内部共享的静态工具方法。
    /// </summary>
    public static class PluginUtils
    {
        private static string _assetsRoot;

        private static StringComparison PathComparison =>
            Application.platform == RuntimePlatform.WindowsEditor
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;

        private static string AssetsRoot
        {
            get
            {
                if (string.IsNullOrEmpty(_assetsRoot))
                {
                    _assetsRoot = Path.GetFullPath(Application.dataPath)
                        .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                }
                return _assetsRoot;
            }
        }

        /// <summary>
        /// 将绝对路径转换为 "Assets/..." 格式的 Unity 资产相对路径。
        /// 如果文件不在项目 Assets 目录下，返回 null。
        /// </summary>
        public static string AbsoluteToAssetPath(string absolutePath)
        {
            if (string.IsNullOrWhiteSpace(absolutePath)) return null;

            try
            {
                string root = AssetsRoot;
                string full = Path.GetFullPath(absolutePath)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

                if (string.Equals(full, root, PathComparison)) return "Assets";

                // 必须校验目录分隔符边界，避免把 AssetsBackup 等同前缀目录误判为项目资产。
                string rootPrefix = root + Path.DirectorySeparatorChar;
                if (!full.StartsWith(rootPrefix, PathComparison)) return null;

                return "Assets/" + full.Substring(rootPrefix.Length).Replace('\\', '/');
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// 将项目内路径统一格式化为 Assets/... 供 UI 显示；项目外路径保留原值。
        /// </summary>
        public static string ToDisplayPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return path;

            string normalized = path.Replace('\\', '/');
            if (string.Equals(normalized, "Assets", StringComparison.OrdinalIgnoreCase))
                return "Assets";
            if (normalized.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase))
                return "Assets/" + normalized.Substring("Assets/".Length);

            return AbsoluteToAssetPath(path) ?? path;
        }

        /// <summary>
        /// 将 UI 中输入的 Assets/... 路径恢复为绝对路径，供文件系统和 Python 使用。
        /// 无法规范化的输入保持原值，便于用户继续编辑。
        /// </summary>
        public static string ToAbsolutePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return path;

            try
            {
                string normalized = path.Replace('\\', '/');
                if (string.Equals(normalized, "Assets", StringComparison.OrdinalIgnoreCase)
                    || normalized.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase))
                {
                    string projectRoot = Path.GetDirectoryName(AssetsRoot);
                    string assetPath = string.Equals(normalized, "Assets", StringComparison.OrdinalIgnoreCase)
                        ? "Assets"
                        : "Assets/" + normalized.Substring("Assets/".Length);
                    return Path.GetFullPath(Path.Combine(projectRoot ?? string.Empty, assetPath));
                }

                return Path.GetFullPath(path);
            }
            catch (Exception)
            {
                return path;
            }
        }

        /// <summary>
        /// 比较两个文件路径是否指向同一文件。Windows 忽略大小写，其他平台区分大小写。
        /// </summary>
        public static bool PathsEqual(string a, string b)
        {
            if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return false;

            try
            {
                return string.Equals(
                    Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                    Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                    PathComparison);
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// 将字节数格式化为人类可读的文件大小字符串（B / KB / MB / GB）。
        /// </summary>
        public static string FormatFileSize(long bytes)
        {
            if (bytes < 1024) return $"{bytes} B";
            if (bytes < 1024 * 1024) return $"{bytes / 1024f:F1} KB";
            if (bytes < 1024 * 1024 * 1024) return $"{bytes / (1024f * 1024f):F1} MB";
            return $"{bytes / (1024f * 1024f * 1024f):F2} GB";
        }

        /// <summary>
        /// 在 Project 窗口中定位指定路径的资产。
        /// 项目内的文件高亮选中；项目外的文件在系统文件管理器中打开。
        /// </summary>
        public static void PingAsset(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return;

            string ap = AbsoluteToAssetPath(path);
            if (!string.IsNullOrEmpty(ap))
            {
                var obj = AssetDatabase.LoadMainAssetAtPath(ap);
                if (obj != null) { EditorGUIUtility.PingObject(obj); Selection.activeObject = obj; }
            }
            else
            {
                EditorUtility.RevealInFinder(path);
            }
        }
    }

    /// <summary>管理当前项目最近一次使用的图片搜索目录。</summary>
    public static class SearchDirectorySettings
    {
        private const string PreferencesKeyPrefix = "ImageSimilarityPlugin.SearchDirectory.";

        /// <summary>读取有效的上次搜索目录；目录不存在时回退到 Assets。</summary>
        public static string GetDirectory()
        {
            string fallback = Path.GetFullPath(Application.dataPath);
            string saved = EditorPrefs.GetString(GetPreferencesKey(), string.Empty);
            if (string.IsNullOrWhiteSpace(saved))
                return fallback;

            try
            {
                string directory = Path.GetFullPath(PluginUtils.ToAbsolutePath(saved));
                return Directory.Exists(directory) ? directory : fallback;
            }
            catch
            {
                return fallback;
            }
        }

        /// <summary>仅保存有效目录，避免错误输入覆盖最后一次可用设置。</summary>
        public static void Save(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return;

            try
            {
                string directory = Path.GetFullPath(PluginUtils.ToAbsolutePath(path));
                if (Directory.Exists(directory))
                    EditorPrefs.SetString(GetPreferencesKey(), directory);
            }
            catch
            {
                // 输入尚未形成有效路径时保留之前的设置。
            }
        }

        private static string GetPreferencesKey()
        {
            string projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            if (Application.platform == RuntimePlatform.WindowsEditor)
                projectRoot = projectRoot.ToUpperInvariant();
            return PreferencesKeyPrefix + Hash128.Compute(projectRoot);
        }
    }

    /// <summary>
    /// 管理当前项目的图片搜索排除目录。
    /// 设置存放在 EditorPrefs 中，不写入项目资产，也不会影响其他 Unity 项目。
    /// </summary>
    public static class ExcludedDirectorySettings
    {
        private const string PreferencesKeyPrefix = "ImageSimilarityPlugin.ExcludedDirectories.";
        private static List<string> _directories;

        private static StringComparison PathComparison =>
            Application.platform == RuntimePlatform.WindowsEditor
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;

        private static StringComparer PathComparer =>
            Application.platform == RuntimePlatform.WindowsEditor
                ? StringComparer.OrdinalIgnoreCase
                : StringComparer.Ordinal;

        /// <summary>返回规范化后的排除目录快照，调用方可安全地跨异步任务使用。</summary>
        public static string[] GetDirectories()
        {
            EnsureLoaded();
            return _directories.ToArray();
        }

        /// <summary>添加排除目录；已有父目录规则时不会添加重复的子目录。</summary>
        public static bool TryAdd(string path, out string message)
        {
            string normalized = NormalizeDirectory(path);
            if (string.IsNullOrEmpty(normalized) || !Directory.Exists(normalized))
            {
                message = $"排除目录不存在: {PluginUtils.ToDisplayPath(path)}";
                return false;
            }

            EnsureLoaded();
            foreach (string existing in _directories)
            {
                if (!IsSameOrChild(normalized, existing)) continue;
                message = $"该目录已被排除规则覆盖: {PluginUtils.ToDisplayPath(existing)}";
                return false;
            }

            // 新增父目录时移除其下已有规则，保持列表最小且语义清晰。
            _directories.RemoveAll(existing => IsSameOrChild(existing, normalized));
            _directories.Add(normalized);
            _directories.Sort(PathComparer);
            Save();
            message = $"已添加排除目录: {PluginUtils.ToDisplayPath(normalized)}";
            return true;
        }

        /// <summary>移除指定排除目录。</summary>
        public static bool Remove(string path)
        {
            string normalized = NormalizeDirectory(path);
            if (string.IsNullOrEmpty(normalized)) return false;

            EnsureLoaded();
            int removed = _directories.RemoveAll(existing =>
                string.Equals(existing, normalized, PathComparison));
            if (removed == 0) return false;

            Save();
            return true;
        }

        /// <summary>生成与顺序无关的排除范围文本，用于组成扫描和特征缓存键。</summary>
        public static string GetScopeKey()
        {
            string[] directories = GetDirectories();
            Array.Sort(directories, PathComparer);
            return string.Join("\n", directories);
        }

        private static void EnsureLoaded()
        {
            if (_directories != null) return;

            _directories = new List<string>();
            try
            {
                string json = EditorPrefs.GetString(GetPreferencesKey(), string.Empty);
                if (string.IsNullOrEmpty(json)) return;

                var data = JsonUtility.FromJson<DirectoryListData>(json);
                if (data?.directories == null) return;

                foreach (string path in data.directories)
                {
                    string normalized = NormalizeDirectory(path);
                    if (string.IsNullOrEmpty(normalized)) continue;
                    if (_directories.Exists(existing => IsSameOrChild(normalized, existing))) continue;
                    _directories.RemoveAll(existing => IsSameOrChild(existing, normalized));
                    _directories.Add(normalized);
                }
                _directories.Sort(PathComparer);
            }
            catch
            {
                _directories.Clear();
            }
        }

        private static void Save()
        {
            var data = new DirectoryListData { directories = _directories.ToArray() };
            EditorPrefs.SetString(GetPreferencesKey(), JsonUtility.ToJson(data));
        }

        private static string GetPreferencesKey()
        {
            string projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            if (Application.platform == RuntimePlatform.WindowsEditor)
                projectRoot = projectRoot.ToUpperInvariant();
            return PreferencesKeyPrefix + Hash128.Compute(projectRoot);
        }

        private static string NormalizeDirectory(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            try
            {
                string fullPath = Path.GetFullPath(PluginUtils.ToAbsolutePath(path));
                string root = Path.GetPathRoot(fullPath);
                return string.Equals(fullPath, root, PathComparison)
                    ? fullPath
                    : fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }
            catch
            {
                return null;
            }
        }

        private static bool IsSameOrChild(string path, string parent)
        {
            if (string.Equals(path, parent, PathComparison)) return true;
            string prefix = parent.EndsWith(Path.DirectorySeparatorChar.ToString(), PathComparison)
                || parent.EndsWith(Path.AltDirectorySeparatorChar.ToString(), PathComparison)
                ? parent
                : parent + Path.DirectorySeparatorChar;
            return path.StartsWith(prefix, PathComparison);
        }

        [Serializable]
        private sealed class DirectoryListData
        {
            public string[] directories;
        }
    }
}

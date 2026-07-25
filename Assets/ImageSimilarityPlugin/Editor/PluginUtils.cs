using System;
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
        /// <summary>
        /// 将绝对路径转换为 "Assets/..." 格式的 Unity 资产相对路径。
        /// 如果文件不在项目 Assets 目录下，返回 null。
        /// </summary>
        public static string AbsoluteToAssetPath(string absolutePath)
        {
            string root = Application.dataPath.Replace('/', Path.DirectorySeparatorChar);
            string full = Path.GetFullPath(absolutePath);
            if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return null;
            return "Assets/" + full.Substring(root.Length).TrimStart(Path.DirectorySeparatorChar).Replace('\\', '/');
        }

        /// <summary>
        /// 比较两个文件路径是否指向同一文件（跨平台，忽略大小写）。
        /// </summary>
        public static bool PathsEqual(string a, string b)
        {
            return string.Equals(
                Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar, '/'),
                Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar, '/'),
                StringComparison.OrdinalIgnoreCase);
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
}

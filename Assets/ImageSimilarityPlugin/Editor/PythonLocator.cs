using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace ImageSimilarityPlugin
{
    /// <summary>
    /// 跨平台 Python 解释器检测器。
    /// 按优先级搜索 Windows / macOS 上的 Python，验证版本 >= 3.6，
    /// 结果缓存在 EditorPrefs 中以加速后续启动。
    /// </summary>
    public static class PythonLocator
    {
        // EditorPrefs 键名，用于持久化 Python 路径和依赖检测状态
        private const string PYTHON_PATH_KEY = "ImageSimilarityPlugin.PythonPath";
        private const string DEPENDENCIES_CHECKED_KEY = "ImageSimilarityPlugin.DepsChecked";

        // 内存缓存，避免重复检测
        private static string _cachedPath;
        private static bool _cachedPathResolved;

        /// <summary>
        /// 获取可用 Python 解释器的完整路径。
        /// 检测顺序：EditorPrefs 手动配置 > PATH 中的 python/py/py3 > 常见安装目录。
        /// 返回 null 表示未找到可用的 Python。
        /// </summary>
        public static string GetPythonPath()
        {
            if (_cachedPathResolved)
                return _cachedPath;

            // 1. 优先使用用户手动配置的路径
            string saved = EditorPrefs.GetString(PYTHON_PATH_KEY, "");
            if (!string.IsNullOrEmpty(saved) && ValidatePython(saved))
            {
                _cachedPath = saved;
                _cachedPathResolved = true;
                return _cachedPath;
            }

            // 2. 按平台搜索候选路径
            var candidates = new List<string>();

            if (Application.platform == RuntimePlatform.WindowsEditor)
            {
                // Windows: 先试 PATH 中的命令
                candidates.Add("python");
                candidates.Add("py");
                // 再试常见的用户安装目录
                string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                candidates.Add(Path.Combine(localAppData, "Programs", "Python", "Python312", "python.exe"));
                candidates.Add(Path.Combine(localAppData, "Programs", "Python", "Python311", "python.exe"));
                candidates.Add(Path.Combine(localAppData, "Programs", "Python", "Python310", "python.exe"));
                candidates.Add(@"C:\Python312\python.exe");
                candidates.Add(@"C:\Python311\python.exe");
                candidates.Add(@"C:\Python310\python.exe");
            }
            else // macOS
            {
                candidates.Add("python3");
                candidates.Add("python");
                candidates.Add("/usr/local/bin/python3");
                candidates.Add("/opt/homebrew/bin/python3");
                candidates.Add("/usr/bin/python3");
            }

            foreach (var candidate in candidates)
            {
                if (ValidatePython(candidate))
                {
                    _cachedPath = candidate;
                    _cachedPathResolved = true;
                    EditorPrefs.SetString(PYTHON_PATH_KEY, candidate);
                    return _cachedPath;
                }
            }

            _cachedPath = null;
            _cachedPathResolved = true;
            return null;
        }

        /// <summary>
        /// 设置用户自定义 Python 路径。
        /// 会先验证该路径是否可用，通过后写入 EditorPrefs 持久化。
        /// 返回 true 表示路径有效并已保存。
        /// </summary>
        public static bool SetCustomPath(string path)
        {
            if (!string.IsNullOrEmpty(path) && ValidatePython(path))
            {
                _cachedPath = path;
                _cachedPathResolved = true;
                EditorPrefs.SetString(PYTHON_PATH_KEY, path);
                return true;
            }
            return false;
        }

        /// <summary>
        /// 获取已安装 Python 的版本字符串。
        /// 如 "3.12.6"，失败返回 null。
        /// </summary>
        public static string GetPythonVersion()
        {
            string pythonPath = GetPythonPath();
            if (pythonPath == null) return null;

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = pythonPath,
                    Arguments = "--version",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                using (var proc = Process.Start(psi))
                {
                    // --version 会输出到 stderr 而非 stdout
                    string output = proc.StandardOutput.ReadToEnd() + proc.StandardError.ReadToEnd();
                    proc.WaitForExit(5000);
                    return output.Trim();
                }
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// 检查 pip 依赖（tensorflow, numpy, PIL, sklearn, tqdm）是否已安装。
        /// 通过尝试 import 所有所需模块来判断，超时 15 秒。
        /// </summary>
        public static bool AreDependenciesInstalled()
        {
            string pythonPath = GetPythonPath();
            if (pythonPath == null) return false;

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = pythonPath,
                    // 一次性尝试导入所有依赖模块
                    Arguments = "-c \"import tensorflow; import numpy; import PIL; import sklearn; import tqdm\"",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                using (var proc = Process.Start(psi))
                {
                    proc.WaitForExit(15000);
                    return proc.ExitCode == 0;
                }
            }
            catch
            {
                return false;
            }
        }

        /// <summary>之前是否已检查过依赖状态</summary>
        public static bool WereDependenciesChecked()
        {
            return EditorPrefs.GetBool(DEPENDENCIES_CHECKED_KEY, false);
        }

        /// <summary>标记依赖检测已完成，下次打开窗口不再自动检查</summary>
        public static void MarkDependenciesChecked()
        {
            EditorPrefs.SetBool(DEPENDENCIES_CHECKED_KEY, true);
        }

        /// <summary>
        /// 验证指定的 Python 路径是否可用，且版本 >= 3.6。
        /// 通过执行 python -c 读取 sys.version_info 来判断。
        /// </summary>
        private static bool ValidatePython(string path)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = path,
                    Arguments = "-c \"import sys; v=sys.version_info; print(f'{v.major}.{v.minor}.{v.micro}')\"",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                using (var proc = Process.Start(psi))
                {
                    string output = proc.StandardOutput.ReadToEnd().Trim();
                    proc.WaitForExit(5000);

                    if (proc.ExitCode == 0 && !string.IsNullOrEmpty(output))
                    {
                        // 解析版本号，要求 >= 3.6
                        var parts = output.Split('.');
                        if (parts.Length >= 2 && int.TryParse(parts[0], out int major))
                        {
                            return major >= 3 || (major == 3 && parts.Length >= 2 && int.TryParse(parts[1], out int minor) && minor >= 6);
                        }
                    }
                }
            }
            catch
            {
                // 命令未找到或执行失败
            }
            return false;
        }
    }
}

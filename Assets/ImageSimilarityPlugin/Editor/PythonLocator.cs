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
                    if (!string.Equals(saved, candidate, StringComparison.OrdinalIgnoreCase))
                        EditorPrefs.SetBool(DEPENDENCIES_CHECKED_KEY, false);
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
                // 解释器切换后必须重新检查依赖，不能沿用上一个 Python 的检测结果。
                EditorPrefs.SetBool(DEPENDENCIES_CHECKED_KEY, false);
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

            if (!TryRunPython(pythonPath, "--version", 5000, out string output, out int exitCode)
                || exitCode != 0)
                return null;

            string version = output.Trim();
            const string prefix = "Python ";
            return version.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                ? version.Substring(prefix.Length)
                : version;
        }

        /// <summary>
        /// 检查 pip 依赖是否已安装。
        /// 这里只检查模块规格是否存在，避免实际 import TensorFlow 导致冷启动超时并误判为缺少依赖。
        /// </summary>
        public static bool AreDependenciesInstalled()
        {
            string pythonPath = GetPythonPath();
            if (pythonPath == null) return false;

            try
            {
                const string args = "-c \"import importlib.util, sys; mods=('tensorflow','numpy','PIL'); missing=[m for m in mods if importlib.util.find_spec(m) is None]; print(','.join(missing)); sys.exit(1 if missing else 0)\"";
                return TryRunPython(pythonPath, args, 15000, out _, out int exitCode)
                    && exitCode == 0;
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
            const string args = "-c \"import sys; v=sys.version_info; print(f'{v.major}.{v.minor}.{v.micro}')\"";
            if (!TryRunPython(path, args, 5000, out string output, out int exitCode) || exitCode != 0)
                return false;

            var parts = output.Trim().Split('.');
            if (parts.Length < 2 || !int.TryParse(parts[0], out int major)
                || !int.TryParse(parts[1], out int minor))
                return false;

            return major > 3 || (major == 3 && minor >= 6);
        }

        /// <summary>
        /// 执行输出量很小的 Python 探测命令，并在超时后回收进程。
        /// </summary>
        private static bool TryRunPython(
            string pythonPath,
            string arguments,
            int timeoutMilliseconds,
            out string output,
            out int exitCode)
        {
            output = null;
            exitCode = -1;

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = pythonPath,
                    Arguments = arguments,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                using (var proc = Process.Start(psi))
                {
                    if (proc == null) return false;
                    if (!proc.WaitForExit(timeoutMilliseconds))
                    {
                        try { proc.Kill(); } catch { }
                        return false;
                    }

                    output = proc.StandardOutput.ReadToEnd() + proc.StandardError.ReadToEnd();
                    exitCode = proc.ExitCode;
                    return true;
                }
            }
            catch
            {
                return false;
            }
        }
    }
}

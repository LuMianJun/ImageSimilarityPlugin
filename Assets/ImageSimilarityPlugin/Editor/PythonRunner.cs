using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace ImageSimilarityPlugin
{
    /// <summary>
    /// Python 子进程管理器。
    /// 负责启动 headless CLI、异步读取 stdout/stderr、
    /// 解析 PROGRESS: 进度行，以及读取结果 JSON 文件。
    /// </summary>
    public class PythonRunner
    {
        private Process _process;
        private bool _isRunning;
        private bool _cancelled;
        private float _progress;
        private string _outputJsonPath;

        /// <summary>当前是否有任务正在运行</summary>
        public bool IsRunning => _isRunning;

        /// <summary>任务进度（0~1），从 Python stdout 的 PROGRESS: 行解析</summary>
        public float Progress => _progress;

        /// <summary>
        /// 获取插件捆绑的 Python 脚本所在目录的绝对路径。
        /// </summary>
        public static string GetPythonScriptsDir()
        {
            return Path.GetFullPath(Path.Combine(
                Application.dataPath,
                "ImageSimilarityPlugin", "Python"));
        }

        /// <summary>
        /// 取消正在运行的任务。可安全地多次调用。
        /// </summary>
        public void Cancel()
        {
            _cancelled = true;
            try
            {
                if (_process != null && !_process.HasExited)
                {
                    _process.Kill();
                }
            }
            catch { }
            _isRunning = false;
            CleanupTempFile();
        }

        // ==================================================================
        //  公开 API
        // ==================================================================

        /// <summary>
        /// 启动一次图片相似度扫描（分组模式）。
        /// </summary>
        public void StartScan(
            string folderPath,
            float threshold,
            bool recursive,
            int workers,
            string cacheFeaturesDir = null,
            Action<ScanResultData> onComplete = null,
            Action<string> onError = null)
        {
            if (_isRunning) { onError?.Invoke("已有任务正在运行。"); return; }

            if (!ValidateEnvironment("duplicate_detector_cli.py", onError)) return;
            if (!Directory.Exists(folderPath)) { onError?.Invoke($"文件夹不存在: {folderPath}"); return; }

            _outputJsonPath = Path.Combine(Application.temporaryCachePath, "similarity_result.json");

            var sb = new StringBuilder();
            sb.Append("\"").Append(Path.Combine(GetPythonScriptsDir(), "duplicate_detector_cli.py")).Append("\"");
            sb.Append(" --folder \"").Append(folderPath).Append("\"");
            sb.Append(" --threshold ").Append(threshold.ToString("F4"));
            sb.Append(" --output \"").Append(_outputJsonPath).Append("\"");
            sb.Append(" --workers ").Append(workers);
            if (recursive) sb.Append(" --recursive");
            if (!string.IsNullOrEmpty(cacheFeaturesDir))
                sb.Append(" --cache-features \"").Append(cacheFeaturesDir).Append("\"");

            RunAsync(sb.ToString(),
                json => JsonUtility.FromJson<ScanResultData>(json),
                onComplete, onError);
        }

        /// <summary>
        /// 启动一次以图搜图查询（靶向模式）。
        /// </summary>
        public void StartQuery(
            string queryImagePath,
            string folderPath,
            float threshold,
            int topK,
            bool recursive,
            int workers,
            bool useCache,
            Action<QueryResultData> onComplete,
            Action<string> onError)
        {
            if (_isRunning) { onError?.Invoke("已有任务正在运行。"); return; }

            if (!ValidateEnvironment("image_query_cli.py", onError)) return;
            if (!File.Exists(queryImagePath)) { onError?.Invoke($"查询图片不存在: {queryImagePath}"); return; }
            if (!Directory.Exists(folderPath)) { onError?.Invoke($"文件夹不存在: {folderPath}"); return; }

            _outputJsonPath = Path.Combine(Application.temporaryCachePath, "query_result.json");

            string cacheDir = null;
            if (useCache)
                cacheDir = Path.Combine(Application.temporaryCachePath, "ImageSimilarityPlugin", "features");

            var sb = new StringBuilder();
            sb.Append("\"").Append(Path.Combine(GetPythonScriptsDir(), "image_query_cli.py")).Append("\"");
            sb.Append(" --query \"").Append(queryImagePath).Append("\"");
            sb.Append(" --folder \"").Append(folderPath).Append("\"");
            sb.Append(" --threshold ").Append(threshold.ToString("F4"));
            sb.Append(" --top-k ").Append(topK);
            sb.Append(" --output \"").Append(_outputJsonPath).Append("\"");
            sb.Append(" --workers ").Append(workers);
            if (recursive) sb.Append(" --recursive");
            if (cacheDir != null)
                sb.Append(" --cache \"").Append(cacheDir).Append("\"");

            RunAsync(sb.ToString(),
                json => JsonUtility.FromJson<QueryResultData>(json),
                onComplete, onError);
        }

        // ==================================================================
        //  内部实现
        // ==================================================================

        /// <summary>验证 Python 和 CLI 脚本可用</summary>
        private bool ValidateEnvironment(string cliScriptName, Action<string> onError)
        {
            string pythonPath = PythonLocator.GetPythonPath();
            if (string.IsNullOrEmpty(pythonPath))
            {
                onError?.Invoke("未找到 Python，请在插件窗口中配置 Python 路径。");
                return false;
            }

            string scriptsDir = GetPythonScriptsDir();
            string cliPath = Path.Combine(scriptsDir, cliScriptName);
            if (!File.Exists(cliPath))
            {
                onError?.Invoke($"未找到 {cliScriptName}:\n{cliPath}");
                return false;
            }

            string enginePath = Path.Combine(scriptsDir, "feature_extractor.py");
            if (!File.Exists(enginePath))
            {
                onError?.Invoke($"未找到 feature_extractor.py:\n{enginePath}");
                return false;
            }

            return true;
        }

        /// <summary>
        /// 启动 Python 子进程的通用方法。
        /// 处理进程生命周期、stderr 过滤、进度解析、JSON 反序列化。
        /// </summary>
        private void RunAsync<T>(
            string args,
            Func<string, T> deserializer,
            Action<T> onComplete,
            Action<string> onError)
        {
            string pythonPath = PythonLocator.GetPythonPath();
            string scriptsDir = GetPythonScriptsDir();

            _cancelled = false;
            _progress = 0f;

            try
            {
                _process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = pythonPath,
                        Arguments = args,
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true,
                        StandardOutputEncoding = Encoding.UTF8,
                        StandardErrorEncoding = Encoding.UTF8,
                        WorkingDirectory = scriptsDir,
                    },
                    EnableRaisingEvents = true,
                };

                _process.ErrorDataReceived += (sender, e) =>
                {
                    if (!string.IsNullOrEmpty(e.Data))
                    {
                        if (!e.Data.StartsWith("WARNING:") &&
                            !e.Data.StartsWith("I0000") &&
                            !e.Data.Contains("oneDNN"))
                        {
                            UnityEngine.Debug.LogWarning($"[Python stderr] {e.Data}");
                        }
                    }
                };

                _process.OutputDataReceived += (sender, e) =>
                {
                    if (string.IsNullOrEmpty(e.Data) || _cancelled) return;
                    if (e.Data.StartsWith("PROGRESS:"))
                    {
                        string numStr = e.Data.Substring("PROGRESS:".Length).Trim();
                        if (int.TryParse(numStr, out int pct))
                            _progress = pct / 100f;
                    }
                };

                _process.Exited += (sender, e) =>
                {
                    _isRunning = false;
                    try { _process.WaitForExit(); } catch { }

                    if (_cancelled) { CleanupTempFile(); return; }

                    if (_process.ExitCode != 0)
                    {
                        string errMsg = "Python 进程异常退出，错误码: " + _process.ExitCode;
                        UnityEngine.Debug.LogError(errMsg);
                        EditorApplication.delayCall += () => onError?.Invoke(errMsg);
                        return;
                    }

                    EditorApplication.delayCall += () =>
                    {
                        try
                        {
                            if (!File.Exists(_outputJsonPath))
                            {
                                onError?.Invoke("未找到结果文件，任务可能失败。");
                                return;
                            }

                            string json = File.ReadAllText(_outputJsonPath, Encoding.UTF8);
                            T result = deserializer(json);

                            if (result == null)
                            {
                                onError?.Invoke("解析结果失败。");
                                return;
                            }

                            onComplete?.Invoke(result);
                        }
                        catch (Exception ex)
                        {
                            onError?.Invoke($"读取结果出错: {ex.Message}");
                        }
                        finally
                        {
                            CleanupTempFile();
                        }
                    };
                };

                _isRunning = true;
                _progress = 0f;
                _process.Start();
                _process.BeginOutputReadLine();
                _process.BeginErrorReadLine();
            }
            catch (Exception ex)
            {
                _isRunning = false;
                onError?.Invoke($"启动 Python 失败: {ex.Message}");
            }
        }

        private void CleanupTempFile()
        {
            try
            {
                if (File.Exists(_outputJsonPath))
                    File.Delete(_outputJsonPath);
            }
            catch { }
        }
    }
}

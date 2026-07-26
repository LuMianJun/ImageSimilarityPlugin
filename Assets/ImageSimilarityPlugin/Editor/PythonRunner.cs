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
    /// 优先使用持久化 Python 会话（PythonSession），
    /// 不可用时自动回退到独立子进程。
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

        /// <summary>进度更新时触发（主线程），订阅方应调用 Repaint()。</summary>
        public event Action ProgressChanged;

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

            // Build session command for persistent server (fast path)
            string sessionCmd = null;
            if (!string.IsNullOrEmpty(cacheFeaturesDir))
            {
                sessionCmd = BuildSessionCommand("scan",
                    new[] {
                        ("folder", folderPath),
                        ("threshold", threshold.ToString("F4")),
                        ("workers", workers.ToString()),
                        ("recursive", recursive ? "true" : "false"),
                        ("cache_dir", cacheFeaturesDir),
                    });
            }

            RunAsync(sb.ToString(),
                json => JsonUtility.FromJson<ScanResultData>(json),
                onComplete, onError, sessionCmd);
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

            // Build session command for persistent server (fast path)
            var cmdParams = new (string key, string value)[]
            {
                ("query", queryImagePath),
                ("folder", folderPath),
                ("threshold", threshold.ToString("F4")),
                ("top_k", topK.ToString()),
                ("workers", workers.ToString()),
                ("recursive", recursive ? "true" : "false"),
            };
            string sessionCmd = BuildSessionCommand("query", cmdParams,
                cacheDir != null
                    ? new[] { ("cache_dir", cacheDir) }
                    : null);

            RunAsync(sb.ToString(),
                json => JsonUtility.FromJson<QueryResultData>(json),
                onComplete, onError, sessionCmd);
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
        /// 启动任务的通用方法。
        /// 优先尝试持久化会话（省去 TF 加载时间），不可用时回退到子进程。
        /// </summary>
        private void RunAsync<T>(
            string args,
            Func<string, T> deserializer,
            Action<T> onComplete,
            Action<string> onError,
            string sessionCmd = null)
        {
            // Try persistent session first (fast path)
            if (!string.IsNullOrEmpty(sessionCmd) && PythonSession.Instance.IsReady)
            {
                _cancelled = false;
                _progress = 0f;
                _isRunning = true;

                PythonSession.Instance.SendCommand(
                    sessionCmd,
                    onProgress: pct =>
                    {
                        _progress = pct;
                        ProgressChanged?.Invoke();
                    },
                    onResult: resultJson =>
                    {
                        _isRunning = false;
                        if (_cancelled) return;
                        try
                        {
                            T result = deserializer(resultJson);
                            if (result != null)
                                onComplete?.Invoke(result);
                            else
                                onError?.Invoke("解析结果失败（持久会话）。");
                        }
                        catch (Exception ex)
                        {
                            onError?.Invoke($"解析结果出错: {ex.Message}");
                        }
                    },
                    onError: err =>
                    {
                        UnityEngine.Debug.LogWarning($"[PythonRunner] Session unavailable ({err}), falling back to subprocess.");
                        _isRunning = false;
                        RunSubprocess(args, deserializer, onComplete, onError);
                    }
                );
                return;
            }

            RunSubprocess(args, deserializer, onComplete, onError);
        }

        /// <summary>回退路径：启动独立 Python 子进程。</summary>
        private void RunSubprocess<T>(
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

        /// <summary>用 StringBuilder 拼出带 action 字段的 JSON 命令，保证格式正确。</summary>
        private static string BuildSessionCommand(string action,
            (string key, string value)[] required,
            (string key, string value)[] optional = null)
        {
            var sb = new StringBuilder();
            sb.Append("{\"action\":\"").Append(action).Append("\"");
            foreach (var (k, v) in required)
                AppendJsonField(sb, k, v);
            if (optional != null)
                foreach (var (k, v) in optional)
                    AppendJsonField(sb, k, v);
            sb.Append("}");
            return sb.ToString();
        }

        private static void AppendJsonField(StringBuilder sb, string key, string value)
        {
            sb.Append(",\"").Append(key).Append("\":");
            // Heuristic: if value looks like a number or bool literal, emit as-is;
            // otherwise emit as a JSON string.
            bool isLiteral = value == "true" || value == "false"
                || (value.Length > 0 && (char.IsDigit(value[0]) || value[0] == '-'));
            if (isLiteral)
                sb.Append(value);
            else
                sb.Append("\"").Append(value.Replace("\\", "\\\\").Replace("\"", "\\\"")).Append("\"");
        }
    }
}

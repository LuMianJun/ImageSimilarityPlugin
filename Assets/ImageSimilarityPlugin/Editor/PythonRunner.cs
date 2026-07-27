using System;
using System.Diagnostics;
using System.Globalization;
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
        private bool _usingSession;
        private float _progress;
        private string _outputJsonPath;

        /// <summary>当前是否有任务正在运行</summary>
        public bool IsRunning => _isRunning;

        /// <summary>任务进度（0~1），从 Python stdout 的 PROGRESS: 行解析</summary>
        public float Progress => _progress;

        /// <summary>进度更新时触发（主线程），订阅方应调用 Repaint()。</summary>
        public event Action ProgressChanged;

        /// <summary>
        /// 获取插件捆绑的 Python 脚本所在目录的绝对路径。目录只能从当前脚本位置推导，避免插件移动后误用旧路径。
        /// </summary>
        public static string GetPythonScriptsDir()
        {
            string scriptRelativePath = GetScriptRelativePythonScriptsDir();
            if (!string.IsNullOrEmpty(scriptRelativePath) && Directory.Exists(scriptRelativePath))
                return scriptRelativePath;

            return null;
        }

        private static string GetScriptRelativePythonScriptsDir()
        {
            string scriptPath = GetCurrentScriptAssetPath();
            if (string.IsNullOrEmpty(scriptPath)) return null;

            string editorDir = Path.GetDirectoryName(scriptPath);
            string pluginRoot = string.IsNullOrEmpty(editorDir) ? null : Path.GetDirectoryName(editorDir);
            if (string.IsNullOrEmpty(pluginRoot)) return null;

            // 插件内部结构固定为 Editor/Python 同级，依赖解析应跟随当前脚本实际位置。
            string pythonAssetPath = Path.Combine(pluginRoot, "Python").Replace('\\', '/');
            return Path.GetFullPath(Path.Combine(Application.dataPath, "..", pythonAssetPath));
        }

        private static string GetCurrentScriptAssetPath()
        {
            string[] guids = AssetDatabase.FindAssets($"{nameof(PythonRunner)} t:MonoScript");
            foreach (string guid in guids)
            {
                string assetPath = AssetDatabase.GUIDToAssetPath(guid);
                if (string.IsNullOrEmpty(assetPath)) continue;
                if (!assetPath.EndsWith("/" + nameof(PythonRunner) + ".cs", StringComparison.OrdinalIgnoreCase))
                    continue;

                try
                {
                    var script = AssetDatabase.LoadAssetAtPath<MonoScript>(assetPath);
                    if (script != null && script.GetClass() == typeof(PythonRunner))
                        return assetPath;
                }
                catch { }
            }

            return null;
        }

        /// <summary>
        /// 取消正在运行的任务。可安全地多次调用。
        /// </summary>
        public void Cancel()
        {
            _cancelled = true;
            bool waitForSubprocessExit = false;
            if (_usingSession)
            {
                // 常驻服务一次只处理一个命令；取消时重启服务，避免旧任务继续占用会话。
                PythonSession.Instance.CancelCurrentCommand();
                _usingSession = false;
            }
            try
            {
                if (_process != null)
                {
                    waitForSubprocessExit = true;
                    if (!_process.HasExited)
                        _process.Kill();
                }
            }
            catch { }
            // 子进程退出回调负责收尾；常驻会话没有对应回调，需要立即复位。
            if (!waitForSubprocessExit)
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
            string[] excludedDirectories = null,
            Action<ScanResultData> onComplete = null,
            Action<string> onError = null)
        {
            if (_isRunning) { onError?.Invoke("已有任务正在运行。"); return; }

            if (!ValidateEnvironment("duplicate_detector_cli.py", out string scriptsDir, onError)) return;
            if (!Directory.Exists(folderPath))
            {
                onError?.Invoke($"文件夹不存在: {PluginUtils.ToDisplayPath(folderPath)}");
                return;
            }
            if (threshold < 0f || threshold > 1f) { onError?.Invoke("相似度阈值必须在 0 到 1 之间。"); return; }
            if (workers < 1) { onError?.Invoke("线程数必须大于或等于 1。"); return; }

            _outputJsonPath = CreateOutputPath("similarity_result");
            string[] excludedDirectorySnapshot = GetExcludedDirectorySnapshot(excludedDirectories);

            var sb = new StringBuilder();
            sb.Append("\"").Append(Path.Combine(scriptsDir, "duplicate_detector_cli.py")).Append("\"");
            sb.Append(" --folder \"").Append(folderPath).Append("\"");
            sb.Append(" --threshold ").Append(threshold.ToString("F4", CultureInfo.InvariantCulture));
            sb.Append(" --output \"").Append(_outputJsonPath).Append("\"");
            sb.Append(" --workers ").Append(workers);
            if (recursive) sb.Append(" --recursive");
            if (!string.IsNullOrEmpty(cacheFeaturesDir))
                sb.Append(" --cache-features \"").Append(cacheFeaturesDir).Append("\"");
            AppendExcludedDirectoryArguments(sb, excludedDirectorySnapshot);

            // Build session command for persistent server (fast path)
            string sessionCmd = null;
            if (!string.IsNullOrEmpty(cacheFeaturesDir))
            {
                sessionCmd = JsonUtility.ToJson(new SessionCommand
                {
                    action = "scan",
                    folder = folderPath,
                    threshold = threshold,
                    workers = workers,
                    recursive = recursive,
                    cache_dir = cacheFeaturesDir,
                    exclude_dirs = excludedDirectorySnapshot,
                });
            }

            RunAsync(sb.ToString(),
                json => JsonUtility.FromJson<ScanResultData>(json),
                onComplete, onError, sessionCmd, scriptsDir);
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
            Action<string> onError,
            string[] excludedDirectories = null)
        {
            if (_isRunning) { onError?.Invoke("已有任务正在运行。"); return; }

            if (!ValidateEnvironment("image_query_cli.py", out string scriptsDir, onError)) return;
            if (!File.Exists(queryImagePath)) { onError?.Invoke($"查询图片不存在: {queryImagePath}"); return; }
            if (!Directory.Exists(folderPath))
            {
                onError?.Invoke($"文件夹不存在: {PluginUtils.ToDisplayPath(folderPath)}");
                return;
            }
            if (threshold < 0f || threshold > 1f) { onError?.Invoke("相似度阈值必须在 0 到 1 之间。"); return; }
            if (topK < 1) { onError?.Invoke("最大结果数必须大于或等于 1。"); return; }
            if (workers < 1) { onError?.Invoke("线程数必须大于或等于 1。"); return; }

            _outputJsonPath = CreateOutputPath("query_result");

            string cacheDir = null;
            if (useCache)
                cacheDir = Path.Combine(Application.temporaryCachePath, "ImageSimilarityPlugin", "features");
            string[] excludedDirectorySnapshot = GetExcludedDirectorySnapshot(excludedDirectories);

            var sb = new StringBuilder();
            sb.Append("\"").Append(Path.Combine(scriptsDir, "image_query_cli.py")).Append("\"");
            sb.Append(" --query \"").Append(queryImagePath).Append("\"");
            sb.Append(" --folder \"").Append(folderPath).Append("\"");
            sb.Append(" --threshold ").Append(threshold.ToString("F4", CultureInfo.InvariantCulture));
            sb.Append(" --top-k ").Append(topK);
            sb.Append(" --output \"").Append(_outputJsonPath).Append("\"");
            sb.Append(" --workers ").Append(workers);
            if (recursive) sb.Append(" --recursive");
            if (cacheDir != null)
                sb.Append(" --cache \"").Append(cacheDir).Append("\"");
            AppendExcludedDirectoryArguments(sb, excludedDirectorySnapshot);

            // Build session command for persistent server (fast path)
            string sessionCmd = JsonUtility.ToJson(new SessionCommand
            {
                action = "query",
                query = queryImagePath,
                folder = folderPath,
                threshold = threshold,
                top_k = topK,
                workers = workers,
                recursive = recursive,
                cache_dir = cacheDir,
                exclude_dirs = excludedDirectorySnapshot,
            });

            RunAsync(sb.ToString(),
                json => JsonUtility.FromJson<QueryResultData>(json),
                onComplete, onError, sessionCmd, scriptsDir);
        }

        // ==================================================================
        //  内部实现
        // ==================================================================

        /// <summary>验证 Python 和 CLI 脚本可用</summary>
        private bool ValidateEnvironment(string cliScriptName, out string scriptsDir, Action<string> onError)
        {
            scriptsDir = null;
            string pythonPath = PythonLocator.GetPythonPath();
            if (string.IsNullOrEmpty(pythonPath))
            {
                onError?.Invoke("未找到 Python，请在插件窗口中配置 Python 路径。");
                return false;
            }

            scriptsDir = GetPythonScriptsDir();
            if (string.IsNullOrEmpty(scriptsDir))
            {
                onError?.Invoke("无法定位插件 Python 目录。请保持 ImageSimilarityPlugin 内部结构为 Editor/Python 同级。");
                return false;
            }

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
            string sessionCmd = null,
            string scriptsDir = null)
        {
            // Try persistent session first (fast path)
            if (!string.IsNullOrEmpty(sessionCmd) && PythonSession.Instance.IsReady)
            {
                _cancelled = false;
                _progress = 0f;
                _isRunning = true;
                _usingSession = true;

                PythonSession.Instance.SendCommand(
                    sessionCmd,
                    onProgress: pct =>
                    {
                        _progress = pct;
                        ProgressChanged?.Invoke();
                    },
                    onResult: resultJson =>
                    {
                        _usingSession = false;
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
                        _usingSession = false;
                        if (_cancelled) return;
                        UnityEngine.Debug.LogWarning($"[PythonRunner] Session unavailable ({err}), falling back to subprocess.");
                        _isRunning = false;
                        RunSubprocess(args, deserializer, onComplete, onError, scriptsDir);
                    }
                );
                return;
            }

            RunSubprocess(args, deserializer, onComplete, onError, scriptsDir);
        }

        /// <summary>回退路径：启动独立 Python 子进程。</summary>
        private void RunSubprocess<T>(
            string args,
            Func<string, T> deserializer,
            Action<T> onComplete,
            Action<string> onError,
            string scriptsDir)
        {
            string pythonPath = PythonLocator.GetPythonPath();
            if (string.IsNullOrEmpty(scriptsDir))
            {
                onError?.Invoke("无法定位插件 Python 目录。请保持 ImageSimilarityPlugin 内部结构为 Editor/Python 同级。");
                return;
            }

            _cancelled = false;
            _progress = 0f;
            _usingSession = false;
            string outputJsonPath = _outputJsonPath;

            Process process = null;
            try
            {
                var stderr = new StringBuilder();
                process = new Process
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

                process.ErrorDataReceived += (sender, e) =>
                {
                    if (!string.IsNullOrEmpty(e.Data))
                    {
                        lock (stderr) stderr.AppendLine(e.Data);
                        if (!e.Data.StartsWith("WARNING:") &&
                            !e.Data.StartsWith("I0000") &&
                            !e.Data.Contains("oneDNN"))
                        {
                            UnityEngine.Debug.LogWarning($"[Python stderr] {e.Data}");
                        }
                    }
                };

                process.OutputDataReceived += (sender, e) =>
                {
                    if (string.IsNullOrEmpty(e.Data) || _cancelled) return;
                    if (e.Data.StartsWith("PROGRESS:"))
                    {
                        string numStr = e.Data.Substring("PROGRESS:".Length).Trim();
                        if (int.TryParse(numStr, out int pct))
                        {
                            _progress = Mathf.Clamp01(pct / 100f);
                            EditorApplication.delayCall += () => ProgressChanged?.Invoke();
                        }
                    }
                };

                process.Exited += (sender, e) =>
                {
                    try { process.WaitForExit(); } catch { }
                    int exitCode = -1;
                    try { exitCode = process.ExitCode; } catch { }
                    string errorOutput;
                    lock (stderr) errorOutput = stderr.ToString().Trim();

                    EditorApplication.delayCall += () =>
                    {
                        try
                        {
                            if (ReferenceEquals(_process, process))
                                _process = null;
                            _isRunning = false;

                            if (_cancelled) return;

                            if (exitCode != 0)
                            {
                                string detail = string.IsNullOrEmpty(errorOutput)
                                    ? string.Empty
                                    : $"\n{errorOutput}";
                                onError?.Invoke($"Python 进程异常退出，错误码: {exitCode}{detail}");
                                return;
                            }

                            if (!File.Exists(outputJsonPath))
                            {
                                onError?.Invoke("未找到结果文件，任务可能失败。");
                                return;
                            }

                            string json = File.ReadAllText(outputJsonPath, Encoding.UTF8);
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
                            CleanupTempFile(outputJsonPath);
                            try { process.Dispose(); } catch { }
                        }
                    };
                };

                _isRunning = true;
                _progress = 0f;
                _process = process;
                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
            }
            catch (Exception ex)
            {
                _isRunning = false;
                if (ReferenceEquals(_process, process))
                    _process = null;
                try { process?.Dispose(); } catch { }
                CleanupTempFile();
                onError?.Invoke($"启动 Python 失败: {ex.Message}");
            }
        }

        private static string CreateOutputPath(string prefix)
        {
            string directory = Path.Combine(Application.temporaryCachePath, "ImageSimilarityPlugin", "results");
            Directory.CreateDirectory(directory);
            return Path.Combine(directory, $"{prefix}_{Guid.NewGuid():N}.json");
        }

        /// <summary>任务启动时复制排除目录，避免异步执行期间设置变化。</summary>
        private static string[] GetExcludedDirectorySnapshot(string[] excludedDirectories)
        {
            string[] source = excludedDirectories ?? ExcludedDirectorySettings.GetDirectories();
            return source == null ? Array.Empty<string>() : (string[])source.Clone();
        }

        private static void AppendExcludedDirectoryArguments(StringBuilder builder, string[] excludedDirectories)
        {
            foreach (string directory in excludedDirectories)
            {
                if (!string.IsNullOrWhiteSpace(directory))
                    builder.Append(" --exclude \"").Append(directory).Append("\"");
            }
        }

        private void CleanupTempFile()
        {
            CleanupTempFile(_outputJsonPath);
        }

        private static void CleanupTempFile(string outputJsonPath)
        {
            try
            {
                if (!string.IsNullOrEmpty(outputJsonPath) && File.Exists(outputJsonPath))
                    File.Delete(outputJsonPath);
            }
            catch { }
        }

        [Serializable]
        private class SessionCommand
        {
            public string action;
            public string query;
            public string folder;
            public float threshold;
            public int top_k;
            public int workers;
            public bool recursive;
            public string cache_dir;
            public string[] exclude_dirs;
        }
    }
}

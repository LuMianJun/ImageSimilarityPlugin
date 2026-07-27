using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace ImageSimilarityPlugin
{
    /// <summary>
    /// 持久化 Python 会话管理器（单例）。
    /// 启动一个常驻 Python 进程，通过 stdin/stdout JSON 通信，
    /// 避免每次查询都重新加载 TensorFlow（约 2 秒开销）。
    ///
    /// 用法：
    ///   PythonSession.Instance.SendCommand(json, onProgress, onResult, onError);
    /// </summary>
    public class PythonSession : IDisposable
    {
        private static PythonSession _instance;
        private static readonly object _lock = new object();
        private static bool _quitRegistered;

        static PythonSession()
        {
            if (!_quitRegistered)
            {
                _quitRegistered = true;
                EditorApplication.quitting += () => _instance?.Dispose();
                AssemblyReloadEvents.beforeAssemblyReload += () => _instance?.Dispose();
            }
        }

        private Process _process;
        private StreamWriter _stdin;
        private volatile bool _ready;
        private Action<float> _pendingProgress;
        private Action<string> _pendingResult;
        private Action<string> _pendingError;
        private bool _disposed;

        /// <summary>全局单例</summary>
        public static PythonSession Instance
        {
            get
            {
                lock (_lock)
                {
                    if (_instance == null || _instance._disposed)
                    {
                        _instance = new PythonSession();
                        _instance.Start();
                    }
                    else if (_instance._process == null)
                    {
                        // 服务异常退出或被取消后，在下一次访问时自动恢复。
                        _instance.Start();
                    }
                    return _instance;
                }
            }
        }

        /// <summary>服务器是否已就绪（模型已加载）</summary>
        public bool IsReady => _ready;

        // ==================================================================
        //  生命周期
        // ==================================================================

        private void Start()
        {
            if (_disposed || _process != null) return;

            string pythonPath = PythonLocator.GetPythonPath();
            if (string.IsNullOrEmpty(pythonPath))
            {
                UnityEngine.Debug.LogWarning("[PythonSession] Python not found — persistent session disabled.");
                return;
            }

            string scriptsDir = PythonRunner.GetPythonScriptsDir();
            if (string.IsNullOrEmpty(scriptsDir))
            {
                UnityEngine.Debug.LogWarning("[PythonSession] 无法定位插件 Python 目录，请保持 ImageSimilarityPlugin 内部结构为 Editor/Python 同级。");
                return;
            }

            string serverPath = Path.Combine(scriptsDir, "query_server.py");
            if (!File.Exists(serverPath))
            {
                UnityEngine.Debug.LogWarning($"[PythonSession] query_server.py not found at: {serverPath}");
                return;
            }

            try
            {
                var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = pythonPath,
                        Arguments = "\"" + serverPath + "\"",
                        UseShellExecute = false,
                        RedirectStandardInput = true,
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
                    if (string.IsNullOrEmpty(e.Data)) return;
                    if (e.Data.Contains("[server]"))
                    {
                        // Status messages from server — only log unexpected ones
                        if (!e.Data.Contains("Ready") && !e.Data.Contains("Loading"))
                            UnityEngine.Debug.LogWarning($"[PythonServer] {e.Data}");
                        return;
                    }
                    // Suppress known TF/oneDNN noise
                    if (!e.Data.StartsWith("WARNING:") &&
                        !e.Data.StartsWith("I0000") &&
                        !e.Data.Contains("oneDNN"))
                    {
                        UnityEngine.Debug.LogWarning($"[Python stderr] {e.Data}");
                    }
                };

                process.OutputDataReceived += OnOutputLine;

                process.Exited += (sender, e) =>
                {
                    int code = -1;
                    try { code = process.ExitCode; } catch { }

                    Action<string> pendingError = null;
                    lock (_lock)
                    {
                        if (ReferenceEquals(_process, process))
                        {
                            _ready = false;
                            _process = null;
                            _stdin = null;
                            if (!_disposed)
                            {
                                pendingError = _pendingError;
                                ClearPendingCallbacks();
                            }
                        }
                    }

                    try { process.Dispose(); } catch { }
                    if (_disposed) return;

                    UnityEngine.Debug.LogWarning($"[PythonSession] Server process exited (code={code}). Will restart on next use.");
                    if (pendingError != null)
                    {
                        EditorApplication.delayCall += () =>
                            pendingError($"Python server exited unexpectedly (code={code}).");
                    }
                };

                _process = process;
                process.Start();

                // Use new UTF8Encoding(false) to avoid emitting a BOM (0xEFBBBF).
                // The BOM would prepend the first JSON line written to stdin, causing
                // the Python server's json.loads() to fail with "Bad JSON" and hang
                // the first query indefinitely.
                _stdin = new StreamWriter(process.StandardInput.BaseStream, new UTF8Encoding(false))
                {
                    AutoFlush = true
                };

                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
            }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogWarning($"[PythonSession] Failed to start: {ex.Message}");
                _ready = false;
                try { _process?.Dispose(); } catch { }
                _process = null;
                _stdin = null;
            }
        }

        public void Dispose()
        {
            lock (_lock)
            {
                _disposed = true;
                ClearPendingCallbacks();
                try
                {
                    if (_process != null && !_process.HasExited)
                    {
                        _stdin?.WriteLine("{\"action\":\"exit\"}");
                        _stdin?.Flush();
                        _process.WaitForExit(2000);
                        if (!_process.HasExited)
                            _process.Kill();
                    }
                }
                catch { }
                try { _stdin?.Dispose(); } catch { }
                try { _process?.Dispose(); } catch { }
                _process = null;
                _stdin = null;
                _ready = false;
                _instance = null;
            }
        }

        /// <summary>
        /// 取消当前常驻命令。Python 推理本身不可中断，因此终止服务进程，
        /// 下次访问 Instance 时会启动全新的服务。
        /// </summary>
        public void CancelCurrentCommand()
        {
            Process process;
            lock (_lock)
            {
                if (_pendingResult == null && _pendingError == null) return;

                ClearPendingCallbacks();
                _ready = false;
                process = _process;
            }

            try
            {
                if (process != null && !process.HasExited)
                    process.Kill();
            }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogWarning($"[PythonSession] 取消任务失败: {ex.Message}");
            }
        }

        // ==================================================================
        //  命令发送
        // ==================================================================

        /// <summary>
        /// 轻量检查缓存状态（不触发 TF 推理，毫秒级）。
        /// 仅读取缓存 manifest 并对比文件系统 mtime。
        /// </summary>
        public void CheckCache(string folderPath, string cacheDir, bool recursive,
            string[] excludedDirectories,
            Action<CacheInfo> onResult, Action<string> onError)
        {
            string commandJson = JsonUtility.ToJson(new CheckCacheCommand
            {
                action = "check_cache",
                folder = folderPath,
                cache_dir = cacheDir,
                recursive = recursive,
                exclude_dirs = excludedDirectories ?? Array.Empty<string>(),
            });

            SendCommand(commandJson,
                onProgress: null,
                onResult: json =>
                {
                    try
                    {
                        // Parse only cache_info from the result wrapper
                        var wrapper = JsonUtility.FromJson<CheckCacheResult>(json);
                        onResult?.Invoke(wrapper?.cache_info);
                    }
                    catch (Exception ex)
                    {
                        onError?.Invoke($"解析缓存状态失败: {ex.Message}");
                    }
                },
                onError: onError);
        }

        [Serializable]
        private class CheckCacheResult
        {
            public CacheInfo cache_info;
        }

        [Serializable]
        private class CheckCacheCommand
        {
            public string action;
            public string folder;
            public string cache_dir;
            public bool recursive;
            public string[] exclude_dirs;
        }

        /// <summary>
        /// 向持久化 Python 进程发送命令。
        /// </summary>
        /// <param name="commandJson">符合 query_server.py 命令格式的 JSON 字符串</param>
        /// <param name="onProgress">进度回调 (0~1)</param>
        /// <param name="onResult">结果回调，参数为结果 JSON 字符串（"type":"result" 的完整 JSON）</param>
        /// <param name="onError">错误回调</param>
        public void SendCommand(
            string commandJson,
            Action<float> onProgress,
            Action<string> onResult,
            Action<string> onError)
        {
            lock (_lock)
            {
                if (!_ready || _process == null || _process.HasExited || _stdin == null)
                {
                    onError?.Invoke("Python server not ready — falling back to subprocess mode.");
                    return;
                }

                if (_pendingResult != null || _pendingError != null)
                {
                    // 协议没有 request id，只允许串行命令，防止后发请求覆盖先发请求的回调。
                    onError?.Invoke("Python server is busy — falling back to subprocess mode.");
                    return;
                }

                _pendingProgress = onProgress;
                _pendingResult = onResult;
                _pendingError = onError;

                try
                {
                    _stdin.WriteLine(commandJson);
                }
                catch (Exception ex)
                {
                    _pendingError?.Invoke($"Failed to send command: {ex.Message}");
                    ClearPendingCallbacks();
                }
            }
        }

        private void ClearPendingCallbacks()
        {
            _pendingProgress = null;
            _pendingResult = null;
            _pendingError = null;
        }

        // ==================================================================
        //  响应解析
        // ==================================================================

        private void OnOutputLine(object sender, DataReceivedEventArgs e)
        {
            if (string.IsNullOrEmpty(e.Data)) return;

            string line = e.Data.Trim();
            if (string.IsNullOrEmpty(line)) return;

            try
            {
                var response = JsonUtility.FromJson<ResponseEnvelope>(line);
                string type = response?.type;

                if (type == "ready")
                {
                    _ready = true;
                    UnityEngine.Debug.Log("[PythonSession] Handshake received — server ready.");
                }
                else if (type == "progress")
                {
                    Action<float> callback;
                    lock (_lock) callback = _pendingProgress;
                    float pct = Mathf.Clamp01(response.value / 100f);
                    if (callback != null)
                        EditorApplication.delayCall += () => callback(pct);
                }
                else if (type == "result")
                {
                    string json = line;
                    EditorApplication.delayCall += () =>
                    {
                        Action<string> cb;
                        lock (_lock)
                        {
                            cb = _pendingResult;
                            ClearPendingCallbacks();
                        }
                        cb?.Invoke(json);
                    };
                }
                else if (type == "error")
                {
                    string msg = string.IsNullOrEmpty(response.message) ? "Unknown error" : response.message;
                    UnityEngine.Debug.LogError($"[PythonSession] Server error: {msg}");
                    EditorApplication.delayCall += () =>
                    {
                        Action<string> cb;
                        lock (_lock)
                        {
                            cb = _pendingError;
                            ClearPendingCallbacks();
                        }
                        cb?.Invoke(msg);
                    };
                }
            }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogError($"[PythonSession] Parse error: {ex.Message}");
            }
        }

        [Serializable]
        private class ResponseEnvelope
        {
            public string type;
            public int value;
            public string message;
        }
    }
}

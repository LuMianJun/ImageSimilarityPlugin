using System;
using System.Collections.Generic;
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
            }
        }

        private Process _process;
        private StreamWriter _stdin;
        private bool _ready;
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
            string pythonPath = PythonLocator.GetPythonPath();
            if (string.IsNullOrEmpty(pythonPath))
            {
                UnityEngine.Debug.LogWarning("[PythonSession] Python not found — persistent session disabled.");
                return;
            }

            string scriptsDir = PythonRunner.GetPythonScriptsDir();
            string serverPath = Path.Combine(scriptsDir, "query_server.py");
            if (!File.Exists(serverPath))
            {
                UnityEngine.Debug.LogWarning($"[PythonSession] query_server.py not found at: {serverPath}");
                return;
            }

            try
            {
                _process = new Process
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

                _process.ErrorDataReceived += (sender, e) =>
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

                _process.OutputDataReceived += OnOutputLine;

                _process.Exited += (sender, e) =>
                {
                    int code = -1;
                    try { code = _process.ExitCode; } catch { }
                    UnityEngine.Debug.LogWarning($"[PythonSession] Server process exited (code={code}). Will restart on next use.");
                    _ready = false;
                    try { _process?.Dispose(); } catch { }
                    _process = null;
                    _stdin = null;
                };

                _process.Start();

                // Use new UTF8Encoding(false) to avoid emitting a BOM (0xEFBBBF).
                // The BOM would prepend the first JSON line written to stdin, causing
                // the Python server's json.loads() to fail with "Bad JSON" and hang
                // the first query indefinitely.
                _stdin = new StreamWriter(_process.StandardInput.BaseStream, new UTF8Encoding(false))
                {
                    AutoFlush = true
                };

                _process.BeginOutputReadLine();
                _process.BeginErrorReadLine();
            }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogWarning($"[PythonSession] Failed to start: {ex.Message}");
                _ready = false;
            }
        }

        public void Dispose()
        {
            lock (_lock)
            {
                _disposed = true;
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

        // ==================================================================
        //  命令发送
        // ==================================================================

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
                    _pendingProgress = null;
                    _pendingResult = null;
                    _pendingError = null;
                }
            }
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
                var obj = MiniJsonParse(line);
                string type = GetStringField(obj, "type");

                if (type == "ready")
                {
                    _ready = true;
                    UnityEngine.Debug.Log("[PythonSession] Handshake received — server ready.");
                }
                else if (type == "progress")
                {
                    int val = GetIntField(obj, "value", 0);
                    float pct = val / 100f;
                    EditorApplication.delayCall += () => _pendingProgress?.Invoke(pct);
                }
                else if (type == "result")
                {
                    string json = line;
                    EditorApplication.delayCall += () =>
                    {
                        var cb = _pendingResult;
                        _pendingResult = null;
                        _pendingProgress = null;
                        _pendingError = null;
                        cb?.Invoke(json);
                    };
                }
                else if (type == "error")
                {
                    string msg = GetStringField(obj, "message") ?? "Unknown error";
                    UnityEngine.Debug.LogError($"[PythonSession] Server error: {msg}");
                    EditorApplication.delayCall += () =>
                    {
                        var cb = _pendingError;
                        _pendingResult = null;
                        _pendingProgress = null;
                        _pendingError = null;
                        cb?.Invoke(msg);
                    };
                }
            }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogError($"[PythonSession] Parse error: {ex.Message}");
            }
        }

        // ==================================================================
        //  轻量 JSON 解析（避免依赖 Newtonsoft.Json）
        // ==================================================================

        private static Dictionary<string, object> MiniJsonParse(string json)
        {
            var dict = new Dictionary<string, object>();
            json = json.Trim();
            if (!json.StartsWith("{") || !json.EndsWith("}")) return dict;

            string inner = json.Substring(1, json.Length - 2);
            int i = 0;
            while (i < inner.Length)
            {
                while (i < inner.Length && char.IsWhiteSpace(inner[i])) i++;
                if (i >= inner.Length) break;

                if (inner[i] != '"') break;
                i++;
                int keyStart = i;
                while (i < inner.Length && inner[i] != '"')
                {
                    if (inner[i] == '\\') i++;
                    i++;
                }
                string key = inner.Substring(keyStart, i - keyStart);
                i++;

                while (i < inner.Length && (char.IsWhiteSpace(inner[i]) || inner[i] == ':')) i++;

                if (i >= inner.Length) break;
                object value;
                if (inner[i] == '"')
                {
                    i++;
                    int valStart = i;
                    while (i < inner.Length && inner[i] != '"')
                    {
                        if (inner[i] == '\\') i++;
                        i++;
                    }
                    value = inner.Substring(valStart, i - valStart);
                    i++;
                }
                else if (inner[i] == '-' || char.IsDigit(inner[i]))
                {
                    int valStart = i;
                    while (i < inner.Length && (char.IsDigit(inner[i]) || inner[i] == '.' || inner[i] == '-')) i++;
                    string numStr = inner.Substring(valStart, i - valStart);
                    if (int.TryParse(numStr, out int intVal))
                        value = intVal;
                    else if (float.TryParse(numStr,
                        System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out float floatVal))
                        value = floatVal;
                    else
                        value = numStr;
                }
                else
                {
                    int depth = 0;
                    int valStart = i;
                    while (i < inner.Length)
                    {
                        char c = inner[i];
                        if (c == '{' || c == '[') depth++;
                        else if (c == '}' || c == ']') depth--;
                        else if (c == ',' && depth == 0) break;
                        i++;
                        if (depth == 0 && (c == '}' || c == ']')) break;
                    }
                    value = inner.Substring(valStart, i - valStart).Trim();
                }
                dict[key] = value;

                while (i < inner.Length && (char.IsWhiteSpace(inner[i]) || inner[i] == ',')) i++;
            }
            return dict;
        }

        private static string GetStringField(Dictionary<string, object> dict, string key)
        {
            return dict.TryGetValue(key, out var val) ? val as string : null;
        }

        private static int GetIntField(Dictionary<string, object> dict, string key, int defaultValue)
        {
            if (dict.TryGetValue(key, out var val) && val is int intVal)
                return intVal;
            return defaultValue;
        }
    }
}

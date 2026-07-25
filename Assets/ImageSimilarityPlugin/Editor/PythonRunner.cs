using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using UnityEditor;
using UnityEngine;

namespace ImageSimilarityPlugin
{
    /// <summary>
    /// Manages the Python subprocess lifecycle for image similarity scanning.
    /// Parses PROGRESS: lines from stdout and reads the JSON result file.
    /// </summary>
    public class PythonRunner
    {
        private Process _process;
        private bool _isRunning;
        private bool _cancelled;
        private float _progress;
        private string _outputJsonPath;
        private Thread _readThread;

        public bool IsRunning => _isRunning;
        public float Progress => _progress;

        /// <summary>
        /// Returns the path to the Python scripts bundled with the plugin.
        /// </summary>
        public static string GetPythonScriptsDir()
        {
            // Application.dataPath = <project>/Assets
            return Path.GetFullPath(Path.Combine(
                Application.dataPath,
                "ImageSimilarityPlugin", "Python"));
        }

        /// <summary>
        /// Start a scan. Events are fired on background thread; poll Progress in OnGUI/Update.
        /// </summary>
        public void StartScan(
            string folderPath,
            float threshold,
            bool recursive,
            int workers,
            Action<ScanResultData> onComplete,
            Action<string> onError)
        {
            if (_isRunning)
            {
                onError?.Invoke("已有扫描正在运行。");
                return;
            }

            string pythonPath = PythonLocator.GetPythonPath();
            if (string.IsNullOrEmpty(pythonPath))
            {
                onError?.Invoke("未找到 Python，请在插件窗口中配置 Python 路径。");
                return;
            }

            string scriptsDir = GetPythonScriptsDir();
            string cliPath = Path.Combine(scriptsDir, "duplicate_detector_cli.py");

            if (!File.Exists(cliPath))
            {
                onError?.Invoke($"未找到 Python CLI 脚本:\n{cliPath}");
                return;
            }

            // Ensure the feature_extractor module is importable
            string enginePath = Path.Combine(scriptsDir, "feature_extractor.py");
            if (!File.Exists(enginePath))
            {
                onError?.Invoke($"未找到 feature_extractor.py:\n{enginePath}");
                return;
            }

            // Output temp file
            _outputJsonPath = Path.Combine(Application.temporaryCachePath, "similarity_result.json");

            // Build arguments
            var sb = new StringBuilder();
            sb.Append("\"").Append(cliPath).Append("\"");
            sb.Append(" --folder \"").Append(folderPath).Append("\"");
            sb.Append(" --threshold ").Append(threshold.ToString("F4"));
            sb.Append(" --output \"").Append(_outputJsonPath).Append("\"");
            sb.Append(" --workers ").Append(workers);
            if (recursive) sb.Append(" --recursive");

            _cancelled = false;
            _progress = 0f;

            try
            {
                _process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = pythonPath,
                        Arguments = sb.ToString(),
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true,
                        StandardOutputEncoding = Encoding.UTF8,
                        StandardErrorEncoding = Encoding.UTF8,
                        // Set working directory to scripts dir so relative imports work
                        WorkingDirectory = scriptsDir,
                    },
                    EnableRaisingEvents = true,
                };

                _process.ErrorDataReceived += (sender, e) =>
                {
                    if (!string.IsNullOrEmpty(e.Data))
                    {
                        // Log TF warnings to Unity console, but filter noise
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

                    // Parse PROGRESS:<int>
                    if (e.Data.StartsWith("PROGRESS:"))
                    {
                        string numStr = e.Data.Substring("PROGRESS:".Length).Trim();
                        if (int.TryParse(numStr, out int pct))
                        {
                            _progress = pct / 100f;
                        }
                    }
                };

                _process.Exited += (sender, e) =>
                {
                    _isRunning = false;

                    // Delay to let streams flush
                    try { _process.WaitForExit(); } catch { }

                    if (_cancelled)
                    {
                        CleanupTempFile();
                        return;
                    }

                    if (_process.ExitCode != 0)
                    {
                        string errMsg = "Python 进程异常退出，错误码: " + _process.ExitCode;
                        UnityEngine.Debug.LogError(errMsg);
                        EditorApplication.delayCall += () => onError?.Invoke(errMsg);
                        return;
                    }

                    // Read JSON result
                    EditorApplication.delayCall += () =>
                    {
                        try
                        {
                            if (!File.Exists(_outputJsonPath))
                            {
                                onError?.Invoke("未找到结果文件，扫描可能失败。");
                                return;
                            }

                            string json = File.ReadAllText(_outputJsonPath, Encoding.UTF8);
                            var result = JsonUtility.FromJson<ScanResultData>(json);

                            if (result == null)
                            {
                                onError?.Invoke("解析扫描结果失败。");
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

        /// <summary>
        /// Cancel the running scan. Safe to call from any thread.
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

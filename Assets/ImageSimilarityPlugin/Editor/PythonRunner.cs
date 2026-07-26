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

        /// <summary>当前是否有扫描正在运行</summary>
        public bool IsRunning => _isRunning;

        /// <summary>扫描进度（0~1），从 Python stdout 的 PROGRESS: 行解析</summary>
        public float Progress => _progress;

        /// <summary>
        /// 获取插件捆绑的 Python 脚本所在目录的绝对路径。
        /// 目录位于 Assets/ImageSimilarityPlugin/Python/ 下。
        /// </summary>
        public static string GetPythonScriptsDir()
        {
            return Path.GetFullPath(Path.Combine(
                Application.dataPath,
                "ImageSimilarityPlugin", "Python"));
        }

        /// <summary>
        /// 启动一次图片相似度扫描。
        /// </summary>
        /// <param name="folderPath">要扫描的文件夹绝对路径</param>
        /// <param name="threshold">余弦相似度阈值（0~1）</param>
        /// <param name="recursive">是否递归子目录</param>
        /// <param name="workers">并行线程数</param>
        /// <param name="cacheFeaturesDir">特征缓存目录。非 null 时传递 --cache-features 给 CLI，扫描同时保存特征索引</param>
        /// <param name="onComplete">扫描完成回调（主线程），参数为解析后的结果</param>
        /// <param name="onError">扫描出错回调（主线程），参数为错误描述</param>
        public void StartScan(
            string folderPath,
            float threshold,
            bool recursive,
            int workers,
            string cacheFeaturesDir = null,
            Action<ScanResultData> onComplete = null,
            Action<string> onError = null)
        {
            if (_isRunning)
            {
                onError?.Invoke("已有扫描正在运行。");
                return;
            }

            // 确保 Python 可用
            string pythonPath = PythonLocator.GetPythonPath();
            if (string.IsNullOrEmpty(pythonPath))
            {
                onError?.Invoke("未找到 Python，请在插件窗口中配置 Python 路径。");
                return;
            }

            // 确保 CLI 脚本存在
            string scriptsDir = GetPythonScriptsDir();
            string cliPath = Path.Combine(scriptsDir, "duplicate_detector_cli.py");
            if (!File.Exists(cliPath))
            {
                onError?.Invoke($"未找到 Python CLI 脚本:\n{cliPath}");
                return;
            }

            string enginePath = Path.Combine(scriptsDir, "feature_extractor.py");
            if (!File.Exists(enginePath))
            {
                onError?.Invoke($"未找到 feature_extractor.py:\n{enginePath}");
                return;
            }

            // 结果 JSON 写入临时目录
            _outputJsonPath = Path.Combine(Application.temporaryCachePath, "similarity_result.json");

            // 构建命令行参数
            var sb = new StringBuilder();
            sb.Append("\"").Append(cliPath).Append("\"");
            sb.Append(" --folder \"").Append(folderPath).Append("\"");
            sb.Append(" --threshold ").Append(threshold.ToString("F4"));
            sb.Append(" --output \"").Append(_outputJsonPath).Append("\"");
            sb.Append(" --workers ").Append(workers);
            if (recursive) sb.Append(" --recursive");
            if (!string.IsNullOrEmpty(cacheFeaturesDir))
                sb.Append(" --cache-features \"").Append(cacheFeaturesDir).Append("\"");

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
                        UseShellExecute = false,           // 必须为 false 才能重定向流
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true,
                        StandardOutputEncoding = Encoding.UTF8,  // 中文路径兼容
                        StandardErrorEncoding = Encoding.UTF8,
                        WorkingDirectory = scriptsDir,          // 确保相对导入可用
                    },
                    EnableRaisingEvents = true,
                };

                // stderr 异步读取，过滤 TensorFlow 噪声日志
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

                // stdout 异步读取，解析 PROGRESS: 行
                _process.OutputDataReceived += (sender, e) =>
                {
                    if (string.IsNullOrEmpty(e.Data) || _cancelled) return;

                    if (e.Data.StartsWith("PROGRESS:"))
                    {
                        string numStr = e.Data.Substring("PROGRESS:".Length).Trim();
                        if (int.TryParse(numStr, out int pct))
                        {
                            _progress = pct / 100f;
                        }
                    }
                };

                // 进程退出回调
                _process.Exited += (sender, e) =>
                {
                    _isRunning = false;
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

                    // 回到主线程读取 JSON 结果
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
        /// 取消正在运行的扫描。
        /// 会强制终止子进程并清理临时 JSON 文件。
        /// 可以安全地多次调用。
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

        /// <summary>清理临时的 JSON 结果文件</summary>
        private void CleanupTempFile()
        {
            try
            {
                if (File.Exists(_outputJsonPath))
                    File.Delete(_outputJsonPath);
            }
            catch { }
        }

        /// <summary>
        /// 启动一次以图搜图查询。
        /// </summary>
        /// <param name="queryImagePath">查询图片的绝对路径</param>
        /// <param name="folderPath">目标文件夹绝对路径</param>
        /// <param name="threshold">余弦相似度阈值（0~1）</param>
        /// <param name="topK">最大返回结果数</param>
        /// <param name="recursive">是否递归子目录</param>
        /// <param name="workers">并行线程数</param>
        /// <param name="useCache">是否启用特征缓存加速</param>
        /// <param name="onComplete">查询完成回调（主线程），参数为解析后的结果</param>
        /// <param name="onError">查询出错回调（主线程），参数为错误描述</param>
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
            if (_isRunning)
            {
                onError?.Invoke("已有任务正在运行。");
                return;
            }

            // 确保 Python 可用
            string pythonPath = PythonLocator.GetPythonPath();
            if (string.IsNullOrEmpty(pythonPath))
            {
                onError?.Invoke("未找到 Python，请在插件窗口中配置 Python 路径。");
                return;
            }

            // 确保脚本存在
            string scriptsDir = GetPythonScriptsDir();
            string cliPath = Path.Combine(scriptsDir, "image_query_cli.py");
            if (!File.Exists(cliPath))
            {
                onError?.Invoke($"未找到 image_query_cli.py:\n{cliPath}");
                return;
            }

            string enginePath = Path.Combine(scriptsDir, "feature_extractor.py");
            if (!File.Exists(enginePath))
            {
                onError?.Invoke($"未找到 feature_extractor.py:\n{enginePath}");
                return;
            }

            // 验证查询图片存在
            if (!File.Exists(queryImagePath))
            {
                onError?.Invoke($"查询图片不存在:\n{queryImagePath}");
                return;
            }

            // 结果 JSON 写入临时目录
            _outputJsonPath = Path.Combine(Application.temporaryCachePath, "query_result.json");

            // 特征缓存目录
            string cacheDir = null;
            if (useCache)
            {
                cacheDir = Path.Combine(Application.temporaryCachePath, "ImageSimilarityPlugin", "features");
            }

            // 构建命令行参数
            var sb = new StringBuilder();
            sb.Append("\"").Append(cliPath).Append("\"");
            sb.Append(" --query \"").Append(queryImagePath).Append("\"");
            sb.Append(" --folder \"").Append(folderPath).Append("\"");
            sb.Append(" --threshold ").Append(threshold.ToString("F4"));
            sb.Append(" --top-k ").Append(topK);
            sb.Append(" --output \"").Append(_outputJsonPath).Append("\"");
            sb.Append(" --workers ").Append(workers);
            if (recursive) sb.Append(" --recursive");
            if (cacheDir != null)
                sb.Append(" --cache \"").Append(cacheDir).Append("\"");

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
                        {
                            _progress = pct / 100f;
                        }
                    }
                };

                _process.Exited += (sender, e) =>
                {
                    _isRunning = false;
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

                    EditorApplication.delayCall += () =>
                    {
                        try
                        {
                            if (!File.Exists(_outputJsonPath))
                            {
                                onError?.Invoke("未找到结果文件，查询可能失败。");
                                return;
                            }

                            string json = File.ReadAllText(_outputJsonPath, Encoding.UTF8);
                            var result = JsonUtility.FromJson<QueryResultData>(json);

                            if (result == null)
                            {
                                onError?.Invoke("解析查询结果失败。");
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
    }
}

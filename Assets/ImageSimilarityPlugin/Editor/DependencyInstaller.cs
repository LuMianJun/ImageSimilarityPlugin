using System;
using System.Diagnostics;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace ImageSimilarityPlugin
{
    /// <summary>
    /// Python pip 依赖安装器。
    /// 将 install 逻辑从主窗口抽离，管理子进程生命周期、实时日志收集和进度更新。
    /// 通过回调通知主窗口状态变化。
    /// </summary>
    public class DependencyInstaller
    {
        private Process _installProcess;
        private bool _isInstalling;
        private bool _isPanelVisible;
        private float _installProgress;
        private string _installLog = "";
        private Vector2 _installLogScrollPos;
        private string _reqPath;

        /// <summary>是否有安装正在进行中</summary>
        public bool IsInstalling => _isInstalling;

        /// <summary>安装日志面板是否可见；安装结束后保留，直到用户关闭。</summary>
        public bool IsPanelVisible => _isPanelVisible;

        /// <summary>安装进度（0~1）</summary>
        public float Progress => _installProgress;

        /// <summary>实时日志文本</summary>
        public string Log => _installLog;

        /// <summary>日志滚动位置</summary>
        public Vector2 LogScrollPos
        {
            get => _installLogScrollPos;
            set => _installLogScrollPos = value;
        }

        /// <summary>安装完成回调。参数：是否成功，状态消息。</summary>
        public event Action<bool, string> OnCompleted;

        /// <summary>开始安装。</summary>
        /// <param name="pythonPath">Python 可执行文件路径</param>
        /// <param name="requirementsPath">requirements.txt 完整路径</param>
        public void StartInstall(string pythonPath, string requirementsPath)
        {
            // 验证参数
            if (string.IsNullOrEmpty(pythonPath))
            {
                _installLog = "错误: 未找到 Python，无法安装依赖。\n请先在顶部点击配置 Python 指定路径。";
                _isPanelVisible = true;
                _isInstalling = false;
                _installProgress = 0f;
                return;
            }

            _reqPath = requirementsPath;
            if (!File.Exists(_reqPath))
            {
                _installLog = $"错误: 未找到 requirements.txt\n路径: {_reqPath}";
                _isPanelVisible = true;
                _isInstalling = false;
                _installProgress = 0f;
                return;
            }

            // 重置状态
            _isPanelVisible = true;
            _isInstalling = true;
            _installProgress = 0f;
            _installLog = "";
            _installLogScrollPos = Vector2.zero;

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = pythonPath,
                    Arguments = $"-m pip install -r \"{_reqPath}\"",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                };

                var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
                _installProcess = process;

                // stdout → 实时日志 + 进度推进
                process.OutputDataReceived += (sender, e) =>
                {
                    if (e.Data == null) return;
                    EditorApplication.delayCall += () =>
                    {
                        _installLog += e.Data + "\n";
                        // 根据 pip 输出关键词推进进度
                        if (e.Data.Contains("Downloading")) _installProgress += 0.03f;
                        else if (e.Data.Contains("Installing collected packages")) _installProgress = 0.7f;
                        else _installProgress += 0.01f;
                        _installProgress = Mathf.Min(_installProgress, 0.9f);
                        _installLogScrollPos.y = float.MaxValue;    // 自动滚到底部
                    };
                };

                // stderr → 也追加到日志
                process.ErrorDataReceived += (sender, e) =>
                {
                    if (e.Data == null) return;
                    EditorApplication.delayCall += () =>
                    {
                        _installLog += e.Data + "\n";
                        _installLogScrollPos.y = float.MaxValue;
                    };
                };

                // 进程退出处理
                process.Exited += (sender, e) =>
                {
                    EditorApplication.delayCall += () =>
                    {
                        if (!ReferenceEquals(_installProcess, process))
                        {
                            try { process.Dispose(); } catch { }
                            return;
                        }

                        _installProgress = 1f;
                        _isInstalling = false;
                        bool success = process.ExitCode == 0;
                        string msg = success ? "依赖包安装成功。" :
                            $"依赖包安装失败，请手动运行:\npip install -r \"{_reqPath}\"";
                        process.Dispose();
                        _installProcess = null;
                        OnCompleted?.Invoke(success, msg);
                    };
                };

                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
            }
            catch (Exception ex)
            {
                _installLog = $"错误: {ex.Message}";
                _isInstalling = false;
                _installProgress = 0f;
                try { _installProcess?.Dispose(); } catch { }
                _installProcess = null;
                OnCompleted?.Invoke(false, $"运行 pip 失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 关闭安装并清理。
        /// 如果安装进程仍在运行则强制终止。
        /// </summary>
        public void Close()
        {
            _isPanelVisible = false;
            _isInstalling = false;
            _installLog = "";
            try
            {
                if (_installProcess != null && !_installProcess.HasExited)
                    _installProcess.Kill();
            }
            catch { }
            try { _installProcess?.Dispose(); } catch { }
            _installProcess = null;
        }
    }
}

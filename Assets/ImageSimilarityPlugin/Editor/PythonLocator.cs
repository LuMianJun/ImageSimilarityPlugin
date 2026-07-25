using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace ImageSimilarityPlugin
{
    /// <summary>
    /// Locates a working Python interpreter on Windows and macOS.
    /// Results are cached in EditorPrefs for subsequent sessions.
    /// </summary>
    public static class PythonLocator
    {
        private const string PYTHON_PATH_KEY = "ImageSimilarityPlugin.PythonPath";
        private const string DEPENDENCIES_CHECKED_KEY = "ImageSimilarityPlugin.DepsChecked";

        private static string _cachedPath;
        private static bool _cachedPathResolved;

        /// <summary>
        /// Returns the full path to a working Python interpreter, or null if not found.
        /// </summary>
        public static string GetPythonPath()
        {
            if (_cachedPathResolved)
                return _cachedPath;

            // 1. User-override from EditorPrefs
            string saved = EditorPrefs.GetString(PYTHON_PATH_KEY, "");
            if (!string.IsNullOrEmpty(saved) && ValidatePython(saved))
            {
                _cachedPath = saved;
                _cachedPathResolved = true;
                return _cachedPath;
            }

            // 2. Search candidates in priority order
            var candidates = new List<string>();

            if (Application.platform == RuntimePlatform.WindowsEditor)
            {
                candidates.Add("python");
                candidates.Add("py");
                // Common install locations
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
        /// Prompts the user to configure a custom Python path.
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
        /// Returns the version string (e.g. "3.12.6"), or null.
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
        /// Returns true if pip dependencies for this plugin are installed.
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

        public static bool WereDependenciesChecked()
        {
            return EditorPrefs.GetBool(DEPENDENCIES_CHECKED_KEY, false);
        }

        public static void MarkDependenciesChecked()
        {
            EditorPrefs.SetBool(DEPENDENCIES_CHECKED_KEY, true);
        }

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
                        // Parse version: must be >= 3.6
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
                // Command not found or execution failed
            }
            return false;
        }
    }
}

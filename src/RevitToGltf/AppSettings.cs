using System;
using System.IO;
using Newtonsoft.Json;

namespace RevitToGltf
{
    /// <summary>
    /// 插件级持久化设置：记住上一次选择的导出输出目录，跨 Revit 会话保存到
    /// %APPDATA%\revitToGltf\settings.json。两个导出命令共用同一个"上次目录"。
    /// </summary>
    public static class AppSettings
    {
        private static readonly string SettingsFile = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "revitToGltf", "settings.json");

        private static string _lastOutputDirectory;

        /// <summary>读取上次的输出目录；无记录或目录已不存在时返回 null。</summary>
        public static string LoadLastOutputDirectory()
        {
            if (_lastOutputDirectory != null)
                return string.IsNullOrWhiteSpace(_lastOutputDirectory) ? null : _lastOutputDirectory;

            _lastOutputDirectory = string.Empty;
            try
            {
                if (File.Exists(SettingsFile))
                {
                    var data = JsonConvert.DeserializeObject<SettingsData>(File.ReadAllText(SettingsFile));
                    if (data != null)
                        _lastOutputDirectory = data.LastOutputDirectory ?? string.Empty;
                }
            }
            catch
            {
                _lastOutputDirectory = string.Empty;
            }

            if (string.IsNullOrWhiteSpace(_lastOutputDirectory) || !Directory.Exists(_lastOutputDirectory))
                return null;
            return _lastOutputDirectory;
        }

        /// <summary>保存上次的输出目录（失败不影响导出主流程）。</summary>
        public static void SaveLastOutputDirectory(string directory)
        {
            if (string.IsNullOrWhiteSpace(directory))
                return;

            _lastOutputDirectory = directory;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(SettingsFile));
                File.WriteAllText(SettingsFile,
                    JsonConvert.SerializeObject(new SettingsData { LastOutputDirectory = directory }, Formatting.Indented));
            }
            catch
            {
                // 忽略：设置文件写入失败不应阻断导出。
            }
        }

        private class SettingsData
        {
            public string LastOutputDirectory { get; set; }
        }
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Teka
{
    public sealed class ItemData
    {
        public string Path { get; set; }
        public string Name { get; set; }

        /// <summary>Шлях, з якого елемент додали (щоб не додати двічі).</summary>
        public string Source { get; set; }

        /// <summary>Звідки ярлик був перенесений (якщо його прибрали з робочого столу).</summary>
        public string MovedFrom { get; set; }
    }

    public sealed class FolderData
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string Name { get; set; } = "Нова папка";
        public double X { get; set; }
        public double Y { get; set; }
        public List<ItemData> Items { get; set; } = new List<ItemData>();
    }

    public sealed class AppConfig
    {
        public List<FolderData> Folders { get; set; } = new List<FolderData>();
        public bool MoveDesktopShortcuts { get; set; }
        public bool TilesHidden { get; set; }
    }

    internal static class Storage
    {
        public static readonly string Dir =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Teka");

        public static readonly string ShortcutsDir = Path.Combine(Dir, "Shortcuts");

        private static string ConfigPath => Path.Combine(Dir, "config.json");
        private static string LogPath => Path.Combine(Dir, "teka.log");

        private static readonly JsonSerializerOptions json = new JsonSerializerOptions
        {
            WriteIndented = true
        };

        public static bool ConfigExists => File.Exists(ConfigPath);

        public static AppConfig Load()
        {
            try
            {
                Directory.CreateDirectory(Dir);
                Directory.CreateDirectory(ShortcutsDir);
                if (File.Exists(ConfigPath))
                {
                    var cfg = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(ConfigPath), json);
                    if (cfg != null)
                    {
                        cfg.Folders ??= new List<FolderData>();
                        foreach (var f in cfg.Folders) f.Items ??= new List<ItemData>();
                        return cfg;
                    }
                }
            }
            catch (Exception ex)
            {
                Log(ex);
                try { File.Copy(ConfigPath, ConfigPath + ".broken", true); } catch { }
            }
            return new AppConfig();
        }

        public static void Save(AppConfig cfg)
        {
            try
            {
                Directory.CreateDirectory(Dir);
                string tmp = ConfigPath + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(cfg, json));
                File.Move(tmp, ConfigPath, true);
            }
            catch (Exception ex)
            {
                Log(ex);
            }
        }

        public static bool IsInShortcutsDir(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            try
            {
                string full = System.IO.Path.GetFullPath(path);
                return full.StartsWith(ShortcutsDir + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        public static string UniquePath(string dir, string fileName)
        {
            string name = System.IO.Path.GetFileNameWithoutExtension(fileName);
            string ext = System.IO.Path.GetExtension(fileName);
            string candidate = System.IO.Path.Combine(dir, fileName);
            int n = 2;
            while (File.Exists(candidate) || Directory.Exists(candidate))
            {
                candidate = System.IO.Path.Combine(dir, $"{name} ({n}){ext}");
                n++;
            }
            return candidate;
        }

        public static void Log(Exception ex) => Log(ex?.ToString());

        public static void Log(string text)
        {
            try
            {
                Directory.CreateDirectory(Dir);
                var fi = new FileInfo(LogPath);
                if (fi.Exists && fi.Length > 512 * 1024) fi.Delete();
                File.AppendAllText(LogPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {text}{Environment.NewLine}");
            }
            catch
            {
            }
        }
    }
}

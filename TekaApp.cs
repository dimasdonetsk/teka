using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;

namespace Teka
{
    public static class Program
    {
        [STAThread]
        public static void Main()
        {
            using var mutex = new Mutex(true, "Teka.SingleInstance.v1", out bool created);
            if (!created) return;

            var app = new TekaApp();
            app.Run();
            GC.KeepAlive(mutex);
        }
    }

    /// <summary>Дані, які несемо під час перетягування іконки з папки.</summary>
    public sealed class TekaDrag
    {
        public const string Format = "Teka.Item";
        public FolderData From { get; init; }
        public ItemData Item { get; init; }
    }

    public sealed class TekaApp : Application
    {
        public AppConfig Config { get; private set; }

        private readonly Dictionary<string, FolderTile> tiles = new Dictionary<string, FolderTile>();
        private FolderPopup popup;
        private TrayIcon tray;
        private bool exiting;

        public bool TilesHidden => Config.TilesHidden;

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            ShutdownMode = ShutdownMode.OnExplicitShutdown;

            DispatcherUnhandledException += (s, a) =>
            {
                Storage.Log(a.Exception);
                a.Handled = true;
            };
            AppDomain.CurrentDomain.UnhandledException += (s, a) => Storage.Log(a.ExceptionObject as Exception);

            bool firstRun = !Storage.ConfigExists;
            Config = Storage.Load();

            if (Config.Folders.Count == 0)
            {
                var wa = SystemParameters.WorkArea;
                Config.Folders.Add(new FolderData
                {
                    Name = "Програми",
                    X = wa.Right - 140,
                    Y = wa.Top + 40
                });
                Save();
            }

            foreach (var f in Config.Folders) CreateTile(f);

            tray = new TrayIcon(this);
            if (firstRun)
                tray.ShowTip("Teka працює", "Перетягни ярлики на папку. Права кнопка на папці — більше дій.");
        }

        // ---------------- плитки ----------------

        private void CreateTile(FolderData f)
        {
            var tile = new FolderTile(this, f);
            tile.Closed += OnTileClosed;
            tiles[f.Id] = tile;
            tile.Show();
            if (Config.TilesHidden) tile.Hide();
        }

        private void OnTileClosed(object sender, EventArgs e)
        {
            var tile = (FolderTile)sender;
            if (tiles.TryGetValue(tile.Folder.Id, out var current) && current == tile)
                tiles.Remove(tile.Folder.Id);

            if (exiting || tile.Removed) return;
            if (!Config.Folders.Contains(tile.Folder)) return;

            // Вікно закрилося саме (наприклад, перезапустили Провідник) — відновлюємо.
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            timer.Tick += (s, a) =>
            {
                timer.Stop();
                if (exiting || tiles.ContainsKey(tile.Folder.Id)) return;
                if (Config.Folders.Contains(tile.Folder)) CreateTile(tile.Folder);
            };
            timer.Start();
        }

        public void SetTilesHidden(bool hidden)
        {
            Config.TilesHidden = hidden;
            popup?.CloseAnimated();
            foreach (var t in tiles.Values)
            {
                if (hidden) t.Hide();
                else t.Show();
            }
            Save();
        }

        public void CreateFolder(FolderTile near)
        {
            var wa = SystemParameters.WorkArea;
            double x, y;
            if (near != null)
            {
                x = near.Left;
                y = near.Top + near.Height + 12;
                if (y + near.Height > wa.Bottom)
                {
                    y = wa.Top + 40;
                    x = near.Left - near.Width - 12;
                }
            }
            else
            {
                int n = Config.Folders.Count;
                x = wa.Right - 140 - (n / 6) * 110;
                y = wa.Top + 40 + (n % 6) * 124;
            }

            var f = new FolderData { Name = "Нова папка", X = x, Y = y };
            Config.Folders.Add(f);
            Save();

            if (Config.TilesHidden) SetTilesHidden(false);
            CreateTile(f);
            if (tiles.TryGetValue(f.Id, out var tile)) OpenFolder(tile, rename: true);
        }

        public void DeleteFolder(FolderData f)
        {
            if (f.Items.Count > 0)
            {
                var res = MessageBox.Show(
                    $"Видалити папку «{f.Name}»?\n\nПрограми не видаляться. Ярлики, які ти переніс з робочого столу, повернуться на робочий стіл.",
                    "Teka", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (res != MessageBoxResult.Yes) return;
            }

            if (popup != null && popup.Folder == f) popup.CloseAnimated();

            foreach (var item in f.Items.ToList()) ReleaseItem(item);
            f.Items.Clear();
            Config.Folders.Remove(f);
            Save();

            if (tiles.TryGetValue(f.Id, out var tile))
            {
                tile.Removed = true;
                tiles.Remove(f.Id);
                tile.Close();
            }
        }

        // ---------------- спливна папка ----------------

        private FolderData lastAutoClosed;
        private DateTime lastAutoClosedAt;

        public void NoteAutoClose(FolderData f)
        {
            lastAutoClosed = f;
            lastAutoClosedAt = DateTime.UtcNow;
        }

        public void ToggleFolder(FolderTile tile)
        {
            if (lastAutoClosed == tile.Folder && (DateTime.UtcNow - lastAutoClosedAt).TotalMilliseconds < 400)
            {
                lastAutoClosed = null;
                return;
            }
            if (popup != null && popup.Folder == tile.Folder && !popup.IsClosing)
            {
                popup.CloseAnimated();
                return;
            }
            OpenFolder(tile, rename: false);
        }

        public void OpenFolder(FolderTile tile, bool rename)
        {
            if (popup != null)
            {
                var old = popup;
                popup = null;
                old.CloseImmediately();
            }

            var p = new FolderPopup(this, tile.Folder, tile);
            p.Closed += (s, e) =>
            {
                if (popup == p) popup = null;
            };
            popup = p;
            p.ShowAnimated(rename);
        }

        // ---------------- елементи ----------------

        public void Save() => Storage.Save(Config);

        public void Refresh(FolderData f)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (tiles.TryGetValue(f.Id, out var tile)) tile.Refresh();
                if (popup != null && popup.Folder == f) popup.Rebuild();
            }), DispatcherPriority.Background);
        }

        public void Rename(FolderData f, string name)
        {
            name = (name ?? "").Trim();
            if (name.Length == 0 || name == f.Name) return;
            f.Name = name;
            Save();
            if (tiles.TryGetValue(f.Id, out var tile)) tile.Refresh();
        }

        private static readonly string[] ShortcutExt = { ".lnk", ".url", ".appref-ms" };

        private static bool IsShortcut(string path) =>
            ShortcutExt.Contains(Path.GetExtension(path).ToLowerInvariant());

        private static bool IsOnDesktop(string path)
        {
            try
            {
                string dir = Path.GetDirectoryName(Path.GetFullPath(path)) ?? "";
                string user = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                string common = Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory);
                return string.Equals(dir.TrimEnd('\\'), user.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)
                    || string.Equals(dir.TrimEnd('\\'), common.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        public static string DisplayName(string path)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    var di = new DirectoryInfo(path);
                    return di.Parent == null ? path : di.Name;
                }
                string ext = Path.GetExtension(path).ToLowerInvariant();
                if (ext == ".exe" || IsShortcut(path))
                    return Path.GetFileNameWithoutExtension(path);
                return Path.GetFileName(path);
            }
            catch
            {
                return path;
            }
        }

        /// <summary>Додає файли/ярлики в папку. Ярлики копіюються у сховище Teka.</summary>
        public void AddPaths(FolderData f, IEnumerable<string> paths, int index = -1)
        {
            int added = 0;
            foreach (var raw in paths)
            {
                if (string.IsNullOrWhiteSpace(raw)) continue;
                string path = raw;
                if (!File.Exists(path) && !Directory.Exists(path)) continue;

                // Не додаємо те саме двічі в одну папку.
                if (f.Items.Any(i => string.Equals(i.Path, path, StringComparison.OrdinalIgnoreCase)
                                  || string.Equals(i.Source, path, StringComparison.OrdinalIgnoreCase)
                                  || (Storage.IsInShortcutsDir(i.Path) && IsShortcut(path)
                                      && string.Equals(i.Name, DisplayName(path), StringComparison.OrdinalIgnoreCase))))
                    continue;

                var item = new ItemData { Name = DisplayName(path), Path = path, Source = path };

                if (IsShortcut(path) && !Storage.IsInShortcutsDir(path))
                {
                    try
                    {
                        Directory.CreateDirectory(Storage.ShortcutsDir);
                        string copy = Storage.UniquePath(Storage.ShortcutsDir, Path.GetFileName(path));
                        File.Copy(path, copy);
                        item.Path = copy;

                        if (Config.MoveDesktopShortcuts && IsOnDesktop(path))
                        {
                            try
                            {
                                File.Delete(path);
                                item.MovedFrom = path;
                            }
                            catch (Exception ex)
                            {
                                // Наприклад, ярлик «для всіх користувачів» без прав адміна — лишаємо на місці.
                                Storage.Log(ex);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Storage.Log(ex);
                        item.Path = path;
                    }
                }

                if (index >= 0 && index <= f.Items.Count)
                {
                    f.Items.Insert(index, item);
                    index++;
                }
                else
                {
                    f.Items.Add(item);
                }
                added++;
            }

            if (added > 0)
            {
                Save();
                Refresh(f);
            }
        }

        /// <summary>Прибирає ярлик з папки, не втрачаючи його.</summary>
        private void ReleaseItem(ItemData item)
        {
            try
            {
                if (!Storage.IsInShortcutsDir(item.Path) || !File.Exists(item.Path)) return;

                if (!string.IsNullOrEmpty(item.MovedFrom))
                {
                    // Повертаємо туди, звідки взяли (або на робочий стіл, якщо місце зайняте).
                    string target = item.MovedFrom;
                    string dir = Path.GetDirectoryName(target);
                    if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir) || IsCommonDesktop(dir))
                        dir = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                    target = Storage.UniquePath(dir, Path.GetFileName(target));
                    File.Move(item.Path, target);
                }
                else
                {
                    // Оригінал лишився на місці — це лише наша копія.
                    bool usedElsewhere = Config.Folders.Any(fo => fo.Items.Any(i => i != item &&
                        string.Equals(i.Path, item.Path, StringComparison.OrdinalIgnoreCase)));
                    if (!usedElsewhere) File.Delete(item.Path);
                }
                IconLoader.Forget(item.Path);
            }
            catch (Exception ex)
            {
                Storage.Log(ex);
            }
        }

        private static bool IsCommonDesktop(string dir)
        {
            string common = Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory);
            return string.Equals(dir.TrimEnd('\\'), common.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
        }

        public void RemoveItem(FolderData f, ItemData item)
        {
            if (!f.Items.Remove(item)) return;
            ReleaseItem(item);
            Save();
            Refresh(f);
        }

        public void RenameItem(FolderData f, ItemData item, string name)
        {
            name = (name ?? "").Trim();
            if (name.Length == 0 || name == item.Name) return;
            item.Name = name;
            Save();
            Refresh(f);
        }

        public void ReorderItem(FolderData f, ItemData item, int index)
        {
            int old = f.Items.IndexOf(item);
            if (old < 0) return;
            f.Items.RemoveAt(old);
            if (index > old) index--;
            if (index < 0 || index > f.Items.Count) index = f.Items.Count;
            f.Items.Insert(index, item);
            Save();
            Refresh(f);
        }

        public void MoveItem(TekaDrag drag, FolderData target)
        {
            if (drag?.From == null || drag.Item == null || drag.From == target) return;
            if (!drag.From.Items.Remove(drag.Item)) return;
            target.Items.Add(drag.Item);
            Save();
            Refresh(drag.From);
            Refresh(target);
        }

        // ---------------- автозапуск і вихід ----------------

        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

        public bool AutostartEnabled
        {
            get
            {
                try
                {
                    using var key = Registry.CurrentUser.OpenSubKey(RunKey);
                    return key?.GetValue("Teka") is string;
                }
                catch
                {
                    return false;
                }
            }
            set
            {
                try
                {
                    using var key = Registry.CurrentUser.CreateSubKey(RunKey);
                    if (value) key.SetValue("Teka", $"\"{Environment.ProcessPath}\"");
                    else key.DeleteValue("Teka", false);
                }
                catch (Exception ex)
                {
                    Storage.Log(ex);
                }
            }
        }

        public void ExitApp()
        {
            exiting = true;
            Save();
            popup?.CloseImmediately();
            foreach (var t in tiles.Values.ToList()) t.Close();
            tray?.Dispose();
            Shutdown();
        }
    }
}

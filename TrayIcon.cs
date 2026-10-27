using System;
using System.Diagnostics;
using System.Drawing;
using System.Reflection;
using System.Windows.Forms;

namespace Teka
{
    /// <summary>Іконка в треї з темним меню.</summary>
    internal sealed class TrayIcon : IDisposable
    {
        private readonly TekaApp app;
        private readonly NotifyIcon icon;
        private readonly ToolStripMenuItem hideItem;
        private readonly ToolStripMenuItem moveItem;
        private readonly ToolStripMenuItem autostartItem;

        public TrayIcon(TekaApp app)
        {
            this.app = app;

            var menu = new ContextMenuStrip
            {
                Renderer = new DarkRenderer(),
                ShowImageMargin = false,
                ShowCheckMargin = true,
                BackColor = Color.FromArgb(28, 27, 34),
                ForeColor = Color.FromArgb(235, 235, 240),
                Font = new Font("Segoe UI", 9.5f),
                Padding = new Padding(2, 4, 2, 4)
            };

            var newItem = new ToolStripMenuItem("Нова папка");
            newItem.Click += (s, e) => app.CreateFolder(null);

            hideItem = new ToolStripMenuItem("Сховати папки");
            hideItem.Click += (s, e) => app.SetTilesHidden(!app.TilesHidden);

            moveItem = new ToolStripMenuItem("Переносити ярлики з робочого столу")
            {
                ToolTipText = "Увімкнено: ярлик зникає з робочого столу і живе в папці.\nВимкнено: на робочому столі лишається копія."
            };
            moveItem.Click += (s, e) =>
            {
                app.Config.MoveDesktopShortcuts = !app.Config.MoveDesktopShortcuts;
                app.Save();
            };

            autostartItem = new ToolStripMenuItem("Запускати разом з Windows");
            autostartItem.Click += (s, e) => app.AutostartEnabled = !app.AutostartEnabled;

            var dataItem = new ToolStripMenuItem("Відкрити теку даних");
            dataItem.Click += (s, e) =>
            {
                try { Process.Start(new ProcessStartInfo("explorer.exe", $"\"{Storage.Dir}\"") { UseShellExecute = true }); }
                catch (Exception ex) { Storage.Log(ex); }
            };

            var updateItem = new ToolStripMenuItem("Перевірити оновлення");
            updateItem.Click += (s, e) => app.CheckUpdatesManual();

            var versionItem = new ToolStripMenuItem("Версія " + Updater.CurrentVersion) { Enabled = false };

            var exitItem = new ToolStripMenuItem("Вийти");
            exitItem.Click += (s, e) => app.ExitApp();

            menu.Items.Add(newItem);
            menu.Items.Add(hideItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(moveItem);
            menu.Items.Add(autostartItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(dataItem);
            menu.Items.Add(updateItem);
            menu.Items.Add(versionItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(exitItem);

            menu.Opening += (s, e) =>
            {
                hideItem.Text = app.TilesHidden ? "Показати папки" : "Сховати папки";
                moveItem.Checked = app.Config.MoveDesktopShortcuts;
                autostartItem.Checked = app.AutostartEnabled;
            };

            icon = new NotifyIcon
            {
                Text = "Teka — папки на робочому столі",
                Icon = LoadIcon(),
                ContextMenuStrip = menu,
                Visible = true
            };
            icon.MouseUp += (s, e) =>
            {
                if (e.Button != MouseButtons.Left) return;
                // Лівий клік теж відкриває меню.
                var mi = typeof(NotifyIcon).GetMethod("ShowContextMenu", BindingFlags.Instance | BindingFlags.NonPublic);
                mi?.Invoke(icon, null);
            };
        }

        private static Icon LoadIcon()
        {
            try
            {
                using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Teka.teka.ico");
                if (stream != null) return new Icon(stream, SystemInformation.SmallIconSize);
            }
            catch (Exception ex)
            {
                Storage.Log(ex);
            }
            return SystemIcons.Application;
        }

        public void ShowTip(string title, string text)
        {
            try { icon.ShowBalloonTip(5000, title, text, ToolTipIcon.None); }
            catch { }
        }

        public void Dispose()
        {
            icon.Visible = false;
            icon.Dispose();
        }

        private sealed class DarkRenderer : ToolStripProfessionalRenderer
        {
            public DarkRenderer() : base(new DarkColors())
            {
                RoundedEdges = false;
            }

            protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
            {
                e.TextColor = e.Item.Enabled ? Color.FromArgb(235, 235, 240) : Color.FromArgb(120, 120, 130);
                base.OnRenderItemText(e);
            }

            protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
            {
                var g = e.Graphics;
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                var r = e.ImageRectangle;
                int cx = r.Left + r.Width / 2, cy = r.Top + r.Height / 2;
                using var pen = new Pen(Color.FromArgb(150, 190, 255), 1.8f);
                g.DrawLines(pen, new[]
                {
                    new Point(cx - 4, cy),
                    new Point(cx - 1, cy + 3),
                    new Point(cx + 5, cy - 4)
                });
            }

            protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
            {
                int y = e.Item.Height / 2;
                using var pen = new Pen(Color.FromArgb(55, 54, 64));
                e.Graphics.DrawLine(pen, 8, y, e.Item.Width - 8, y);
            }
        }

        private sealed class DarkColors : ProfessionalColorTable
        {
            private static readonly Color Bg = Color.FromArgb(28, 27, 34);
            private static readonly Color Hover = Color.FromArgb(52, 50, 64);

            public override Color ToolStripDropDownBackground => Bg;
            public override Color ImageMarginGradientBegin => Bg;
            public override Color ImageMarginGradientMiddle => Bg;
            public override Color ImageMarginGradientEnd => Bg;
            public override Color MenuBorder => Color.FromArgb(60, 58, 72);
            public override Color MenuItemBorder => Hover;
            public override Color MenuItemSelected => Hover;
            public override Color MenuItemSelectedGradientBegin => Hover;
            public override Color MenuItemSelectedGradientEnd => Hover;
            public override Color CheckBackground => Bg;
            public override Color CheckSelectedBackground => Hover;
            public override Color CheckPressedBackground => Hover;
            public override Color SeparatorDark => Color.FromArgb(55, 54, 64);
            public override Color SeparatorLight => Bg;
        }
    }
}

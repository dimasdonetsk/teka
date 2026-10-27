using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace Teka
{
    /// <summary>Іконка папки на робочому столі: скляний квадрат із мініатюрами 3×3 і підписом.</summary>
    public sealed class FolderTile : Window
    {
        public const double TileW = 96;
        public const double TileH = 116;
        public const double SquareSize = 64;
        public const double SquareTop = 6;

        private readonly TekaApp app;
        public FolderData Folder { get; }
        public bool Removed { get; set; }

        private readonly Border host;
        private readonly Border square;
        private readonly UniformGrid mini;
        private readonly TextBlock label;
        private IntPtr hwnd;

        private bool mouseDown;
        private Point downPos;
        private bool dropHover;
        private bool hover;

        private static readonly Brush HitBrush = Frozen(new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)));
        private static readonly Brush HoverHostBrush = Frozen(new SolidColorBrush(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF)));
        private static readonly Brush DropHostBrush = Frozen(new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)));
        private static readonly Brush SquareBrush = Frozen(new LinearGradientBrush(
            Color.FromArgb(0x8C, 0x3A, 0x38, 0x46), Color.FromArgb(0x8C, 0x1C, 0x1B, 0x24), 90));
        private static readonly Brush SquareHoverBrush = Frozen(new LinearGradientBrush(
            Color.FromArgb(0xB0, 0x4A, 0x47, 0x5A), Color.FromArgb(0xB0, 0x26, 0x24, 0x30), 90));
        private static readonly Brush DropBorder = Frozen(new SolidColorBrush(Color.FromArgb(0xD0, 0xFF, 0xFF, 0xFF)));
        private static readonly Transform DropScale = Frozen(new ScaleTransform(1.08, 1.08));
        private static readonly Brush SquareBorder = Frozen(new SolidColorBrush(Color.FromArgb(0x59, 0xFF, 0xFF, 0xFF)));

        internal static T Frozen<T>(T f) where T : Freezable
        {
            f.Freeze();
            return f;
        }

        public FolderTile(TekaApp app, FolderData folder)
        {
            this.app = app;
            Folder = folder;

            Title = "Teka — " + folder.Name;
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            ShowActivated = false;
            Topmost = false;
            WindowStartupLocation = WindowStartupLocation.Manual;
            Width = TileW;
            Height = TileH;
            Left = folder.X;
            Top = folder.Y;
            AllowDrop = true;
            UseLayoutRounding = true;

            mini = new UniformGrid
            {
                Rows = 3,
                Columns = 3,
                Margin = new Thickness(8),
                VerticalAlignment = VerticalAlignment.Top
            };

            square = new Border
            {
                Width = SquareSize,
                Height = SquareSize,
                CornerRadius = new CornerRadius(17),
                Background = SquareBrush,
                BorderBrush = SquareBorder,
                BorderThickness = new Thickness(1),
                HorizontalAlignment = HorizontalAlignment.Center,
                RenderTransformOrigin = new Point(0.5, 0.5),
                Margin = new Thickness(0, SquareTop, 0, 0),
                Child = mini,
                Effect = new DropShadowEffect
                {
                    BlurRadius = 10,
                    ShadowDepth = 2,
                    Direction = 270,
                    Opacity = 0.35,
                    Color = Colors.Black
                }
            };

            label = new TextBlock
            {
                Foreground = Brushes.White,
                FontSize = 12,
                FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI"),
                TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxHeight = 34,
                Margin = new Thickness(4, 5, 4, 0),
                Effect = new DropShadowEffect
                {
                    BlurRadius = 5,
                    ShadowDepth = 1,
                    Direction = 270,
                    Opacity = 0.95,
                    Color = Colors.Black
                }
            };

            var stack = new StackPanel();
            stack.Children.Add(square);
            stack.Children.Add(label);

            host = new Border
            {
                Background = HitBrush,
                CornerRadius = new CornerRadius(10),
                Child = stack
            };
            Content = host;

            host.MouseEnter += (s, e) => { hover = true; UpdateLook(); };
            host.MouseLeave += (s, e) => { hover = false; UpdateLook(); };
            host.MouseLeftButtonDown += OnDown;
            host.MouseMove += OnMove;
            host.MouseLeftButtonUp += OnUp;
            host.ContextMenu = BuildMenu();

            DragEnter += OnDragOver;
            DragOver += OnDragOver;
            DragLeave += (s, e) => { dropHover = false; UpdateLook(); };
            Drop += OnDrop;

            SourceInitialized += OnSourceInitialized;
            Refresh();
        }

        private ContextMenu BuildMenu()
        {
            var menu = new ContextMenu();
            menu.Items.Add(MakeMenuItem("Відкрити", () => app.OpenFolder(this, rename: false)));
            menu.Items.Add(MakeMenuItem("Перейменувати", () => app.OpenFolder(this, rename: true)));
            menu.Items.Add(new Separator());
            menu.Items.Add(MakeMenuItem("Нова папка", () => app.CreateFolder(this)));
            menu.Items.Add(MakeMenuItem("Видалити папку", () => app.DeleteFolder(Folder)));
            return menu;
        }

        internal static MenuItem MakeMenuItem(string header, Action action)
        {
            var mi = new MenuItem { Header = header };
            mi.Click += (s, e) => action();
            return mi;
        }

        private void OnSourceInitialized(object sender, EventArgs e)
        {
            hwnd = new WindowInteropHelper(this).Handle;
            Native.MakeToolWindow(hwnd, noActivate: true);

            // Власник — робочий стіл: тоді Win+D не ховає папки.
            IntPtr progman = Native.FindWindow("Progman", null);
            if (progman != IntPtr.Zero)
                Native.SetWindowLongPtr(hwnd, Native.GWLP_HWNDPARENT, progman);

            HwndSource.FromHwnd(hwnd)?.AddHook(WndProc);
            SendToBottom();
        }

        private IntPtr WndProc(IntPtr h, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == Native.WM_WINDOWPOSCHANGING)
            {
                var wp = Marshal.PtrToStructure<Native.WINDOWPOS>(lParam);
                if ((wp.flags & Native.SWP_NOZORDER) == 0)
                {
                    wp.hwndInsertAfter = Native.HWND_BOTTOM;
                    Marshal.StructureToPtr(wp, lParam, false);
                }
            }
            return IntPtr.Zero;
        }

        private void SendToBottom()
        {
            if (hwnd == IntPtr.Zero) return;
            Native.SetWindowPos(hwnd, Native.HWND_BOTTOM, 0, 0, 0, 0,
                Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
        }

        public void Refresh()
        {
            Title = "Teka — " + Folder.Name;
            label.Text = Folder.Name;

            mini.Children.Clear();
            foreach (var item in Folder.Items.Take(9))
            {
                var img = new Image
                {
                    Source = IconLoader.Get(item.Path, 48),
                    Width = 13,
                    Height = 13,
                    Margin = new Thickness(1.5),
                    Stretch = Stretch.Uniform
                };
                RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
                mini.Children.Add(img);
            }
        }

        private void UpdateLook()
        {
            host.Background = HitBrush;
            square.BorderBrush = dropHover ? DropBorder : SquareBorder;
            square.RenderTransform = dropHover ? DropScale : null;
            square.Background = (hover || dropHover) ? SquareHoverBrush : SquareBrush;
        }

        // ---------------- миша ----------------

        private void OnDown(object sender, MouseButtonEventArgs e)
        {
            mouseDown = true;
            downPos = PointToScreen(e.GetPosition(this));
            host.CaptureMouse();
            e.Handled = true;
        }

        private void OnMove(object sender, MouseEventArgs e)
        {
            if (!mouseDown || e.LeftButton != MouseButtonState.Pressed) return;
            var p = PointToScreen(e.GetPosition(this));
            if (Math.Abs(p.X - downPos.X) < 6 && Math.Abs(p.Y - downPos.Y) < 6) return;

            mouseDown = false;
            host.ReleaseMouseCapture();
            try
            {
                DragMove();
            }
            catch (InvalidOperationException)
            {
            }
            Folder.X = Math.Round(Left);
            Folder.Y = Math.Round(Top);
            app.Save();
            SendToBottom();
        }

        private void OnUp(object sender, MouseButtonEventArgs e)
        {
            if (!mouseDown) return;
            mouseDown = false;
            host.ReleaseMouseCapture();
            e.Handled = true;
            app.ToggleFolder(this);
        }

        // ---------------- перетягування ----------------

        private void OnDragOver(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
                e.Effects = DragDropEffects.Copy;
            else if (e.Data.GetDataPresent(TekaDrag.Format) &&
                     e.Data.GetData(TekaDrag.Format) is TekaDrag d && d.From != Folder)
                e.Effects = DragDropEffects.Move;
            else
                e.Effects = DragDropEffects.None;

            dropHover = e.Effects != DragDropEffects.None;
            UpdateLook();
            e.Handled = true;
        }

        private void OnDrop(object sender, DragEventArgs e)
        {
            dropHover = false;
            UpdateLook();
            e.Handled = true;

            if (e.Data.GetDataPresent(DataFormats.FileDrop) && e.Data.GetData(DataFormats.FileDrop) is string[] files)
            {
                app.AddPaths(Folder, files);
            }
            else if (e.Data.GetDataPresent(TekaDrag.Format) && e.Data.GetData(TekaDrag.Format) is TekaDrag d)
            {
                app.MoveItem(d, Folder);
            }
        }

        /// <summary>Центр квадратика в екранних DIP — звідси «виростає» відкрита папка.</summary>
        public Point IconCenter => new Point(Left + Width / 2, Top + SquareTop + SquareSize / 2);

        public IntPtr Handle => hwnd;
    }
}

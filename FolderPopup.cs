using System;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace Teka
{
    /// <summary>Відкрита папка: скляна панель, що «виростає» з іконки.</summary>
    public sealed class FolderPopup : Window
    {
        private const int Cols = 4;
        private const int MaxRows = 4;
        private const double CellW = 90;
        private const double CellH = 100;
        private const double Pad = 18;
        private const double TitleH = 48;
        private const double HintH = 26;
        private const double Radius = 24;

        private readonly TekaApp app;
        private readonly FolderTile tile;
        public FolderData Folder { get; }

        private readonly Border root;
        private readonly ScaleTransform scale = new ScaleTransform(1, 1);
        private readonly TextBox title;
        private readonly WrapPanel grid;
        private readonly TextBlock hint;
        private IntPtr hwnd;

        private bool closing;
        private bool suppressClose;
        private bool positioned;

        // перетягування всередині
        private Border pressedCell;
        private Point pressPos;
        private bool dragging;

        private static readonly Brush CellHit = FolderTile.Frozen(new SolidColorBrush(Color.FromArgb(1, 255, 255, 255)));
        private static readonly Brush CellHover = FolderTile.Frozen(new SolidColorBrush(Color.FromArgb(0x26, 255, 255, 255)));
        private static readonly Brush CellDrop = FolderTile.Frozen(new SolidColorBrush(Color.FromArgb(0x45, 255, 255, 255)));
        private static readonly Brush Soft = FolderTile.Frozen(new SolidColorBrush(Color.FromArgb(0xA6, 255, 255, 255)));
        private static readonly FontFamily UiFont = new FontFamily("Segoe UI Variable Text, Segoe UI");
        private static readonly FontFamily TitleFont = new FontFamily("Segoe UI Variable Display, Segoe UI");

        public bool IsClosing => closing;

        public FolderPopup(TekaApp app, FolderData folder, FolderTile tile)
        {
            this.app = app;
            this.tile = tile;
            Folder = folder;

            Title = folder.Name;
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            Topmost = true;
            WindowStartupLocation = WindowStartupLocation.Manual;
            AllowDrop = true;
            UseLayoutRounding = true;
            Opacity = 0;

            title = new TextBox
            {
                Text = folder.Name,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Foreground = Brushes.White,
                CaretBrush = Brushes.White,
                SelectionBrush = new SolidColorBrush(Color.FromRgb(0x7A, 0xA8, 0xFF)),
                FontFamily = TitleFont,
                FontSize = 21,
                FontWeight = FontWeights.SemiBold,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Top,
                Padding = new Thickness(0),
                Margin = new Thickness(0, 0, 0, 0),
                MaxLength = 40,
                Cursor = Cursors.IBeam,
                FocusVisualStyle = null
            };
            title.KeyDown += (s, e) =>
            {
                if (e.Key == Key.Enter) { CommitTitle(); root.Focus(); e.Handled = true; }
                else if (e.Key == Key.Escape) { title.Text = Folder.Name; root.Focus(); e.Handled = true; }
            };
            title.LostKeyboardFocus += (s, e) => CommitTitle();

            grid = new WrapPanel { Width = Cols * CellW, HorizontalAlignment = HorizontalAlignment.Center };

            var scroller = new ScrollViewer
            {
                Content = grid,
                VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Focusable = false
            };

            hint = new TextBlock
            {
                Text = "Перетягни сюди ярлики, програми чи файли",
                Foreground = Soft,
                FontFamily = UiFont,
                FontSize = 12,
                TextAlignment = TextAlignment.Center,
                Margin = new Thickness(0, 6, 0, 0),
                Visibility = Visibility.Collapsed
            };

            var layout = new Grid();
            layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(TitleH) });
            layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.SetRow(title, 0);
            Grid.SetRow(scroller, 1);
            Grid.SetRow(hint, 2);
            layout.Children.Add(title);
            layout.Children.Add(scroller);
            layout.Children.Add(hint);

            root = new Border
            {
                CornerRadius = new CornerRadius(Radius),
                Background = new SolidColorBrush(Color.FromArgb(Native.IsWin11 ? (byte)0x18 : (byte)0x55, 0x14, 0x13, 0x1A)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(Pad),
                Child = layout,
                Focusable = true,
                FocusVisualStyle = null,
                RenderTransform = scale
            };
            Content = root;

            root.MouseLeftButtonDown += (s, e) =>
            {
                if (!title.IsKeyboardFocusWithin) return;
                root.Focus();
            };

            Deactivated += (s, e) =>
            {
                if (!suppressClose && !closing && !dragging) CloseAnimated();
            };
            PreviewKeyDown += (s, e) =>
            {
                if (e.Key == Key.Escape && !title.IsKeyboardFocusWithin)
                {
                    CloseAnimated();
                    e.Handled = true;
                }
            };

            DragOver += OnWindowDragOver;
            DragEnter += OnWindowDragOver;
            Drop += OnWindowDrop;

            SourceInitialized += (s, e) =>
            {
                hwnd = new WindowInteropHelper(this).Handle;
                Native.MakeToolWindow(hwnd, noActivate: false);
                Native.SetRoundCorners(hwnd, true);
                Native.EnableBlur(hwnd, Native.IsWin11 ? 0x701C1B22u : 0xB01C1B22u);
            };
        }

        // ---------------- показ і закриття ----------------

        public void ShowAnimated(bool rename)
        {
            Rebuild();
            Reposition();
            Show();
            ApplyRegion();
            Activate();

            var c = tile.IconCenter;
            double ox = (c.X - Left) / Width, oy = (c.Y - Top) / Height;
            root.RenderTransformOrigin = new Point(Math.Clamp(ox, 0, 1), Math.Clamp(oy, 0, 1));

            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            var grow = new DoubleAnimation(0.35, 1, TimeSpan.FromMilliseconds(230)) { EasingFunction = ease };
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
            BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160)));

            if (rename)
            {
                title.Focus();
                title.SelectAll();
            }
            else
            {
                root.Focus();
            }
        }

        public void CloseAnimated()
        {
            if (closing) return;
            closing = true;
            CommitTitle();

            var ease = new CubicEase { EasingMode = EasingMode.EaseIn };
            var shrink = new DoubleAnimation(1, 0.6, TimeSpan.FromMilliseconds(150)) { EasingFunction = ease };
            var fade = new DoubleAnimation(Opacity, 0, TimeSpan.FromMilliseconds(140));
            fade.Completed += (s, e) => Close();
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, shrink);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, shrink);
            BeginAnimation(OpacityProperty, fade);
        }

        public void CloseImmediately()
        {
            if (closing) return;
            closing = true;
            CommitTitle();
            Close();
        }

        private void CommitTitle() => app.Rename(Folder, title.Text);

        // ---------------- розмір і положення ----------------

        private void Reposition()
        {
            double w = Width, h = Height;
            var c = tile.IconCenter;

            Rect wa = WorkAreaDip();
            double left = c.X - w / 2;
            double top = c.Y - h / 2;
            left = Math.Max(wa.Left + 10, Math.Min(left, wa.Right - w - 10));
            top = Math.Max(wa.Top + 10, Math.Min(top, wa.Bottom - h - 10));
            Left = left;
            Top = top;
            positioned = true;
        }

        private Rect WorkAreaDip()
        {
            try
            {
                var screen = tile.Handle != IntPtr.Zero
                    ? System.Windows.Forms.Screen.FromHandle(tile.Handle)
                    : System.Windows.Forms.Screen.PrimaryScreen;
                var dpi = VisualTreeHelper.GetDpi(tile);
                var r = screen.WorkingArea;
                return new Rect(r.Left / dpi.DpiScaleX, r.Top / dpi.DpiScaleY, r.Width / dpi.DpiScaleX, r.Height / dpi.DpiScaleY);
            }
            catch
            {
                return SystemParameters.WorkArea;
            }
        }

        private void ApplyRegion()
        {
            if (hwnd == IntPtr.Zero || Native.IsWin11) return; // на Win11 кути заокруглює сама система
            try
            {
                var dpi = VisualTreeHelper.GetDpi(this);
                int w = (int)Math.Ceiling(Width * dpi.DpiScaleX);
                int h = (int)Math.Ceiling(Height * dpi.DpiScaleY);
                int r = (int)Math.Round(Radius * 2 * dpi.DpiScaleX);
                IntPtr rgn = Native.CreateRoundRectRgn(0, 0, w + 1, h + 1, r, r);
                Native.SetWindowRgn(hwnd, rgn, true); // вікно саме звільнить регіон
            }
            catch
            {
            }
        }

        // ---------------- вміст ----------------

        public void Rebuild()
        {
            grid.Children.Clear();
            for (int i = 0; i < Folder.Items.Count; i++)
                grid.Children.Add(MakeCell(Folder.Items[i], i));
            grid.Children.Add(MakeAddCell());

            bool empty = Folder.Items.Count == 0;
            hint.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;

            int rows = (int)Math.Ceiling((Folder.Items.Count + 1) / (double)Cols);
            int visible = Math.Min(Math.Max(rows, 1), MaxRows);

            Width = Cols * CellW + Pad * 2 + 2;
            Height = TitleH + visible * CellH + Pad * 2 + 2 + (empty ? HintH : 0);

            if (positioned)
            {
                // Не даємо панелі вилізти за екран після додавання іконок.
                Rect wa = WorkAreaDip();
                if (Top + Height > wa.Bottom - 10) Top = Math.Max(wa.Top + 10, wa.Bottom - Height - 10);
                ApplyRegion();
            }
        }

        private Border MakeCell(ItemData item, int index)
        {
            var img = new Image
            {
                Source = IconLoader.Get(item.Path, 96),
                Width = 48,
                Height = 48,
                Margin = new Thickness(0, 9, 0, 6),
                Stretch = Stretch.Uniform
            };
            RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);

            var name = new TextBlock
            {
                Text = item.Name,
                Foreground = Brushes.White,
                FontFamily = UiFont,
                FontSize = 12,
                TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxHeight = 32,
                Margin = new Thickness(4, 0, 4, 0)
            };

            var stack = new StackPanel();
            stack.Children.Add(img);
            stack.Children.Add(name);

            var cell = new Border
            {
                Width = CellW - 6,
                Height = CellH - 6,
                Margin = new Thickness(3),
                CornerRadius = new CornerRadius(14),
                Background = CellHit,
                Child = stack,
                Tag = item,
                AllowDrop = true,
                ToolTip = item.Path
            };
            ToolTipService.SetInitialShowDelay(cell, 900);

            if (!File.Exists(item.Path) && !Directory.Exists(item.Path))
                cell.Opacity = 0.45;

            cell.MouseEnter += (s, e) => { if (!dragging) cell.Background = CellHover; };
            cell.MouseLeave += (s, e) => cell.Background = CellHit;

            cell.MouseLeftButtonDown += (s, e) =>
            {
                pressedCell = cell;
                pressPos = e.GetPosition(this);
                e.Handled = true;
            };
            cell.MouseMove += (s, e) =>
            {
                if (pressedCell != cell || e.LeftButton != MouseButtonState.Pressed || dragging) return;
                var p = e.GetPosition(this);
                if (Math.Abs(p.X - pressPos.X) < 6 && Math.Abs(p.Y - pressPos.Y) < 6) return;

                dragging = true;
                cell.Opacity = 0.35;
                try
                {
                    var data = new DataObject(TekaDrag.Format, new TekaDrag { From = Folder, Item = item });
                    DragDrop.DoDragDrop(cell, data, DragDropEffects.Move);
                }
                finally
                {
                    dragging = false;
                    pressedCell = null;
                    cell.Opacity = 1;
                    if (!IsActive && !closing) Activate();
                }
            };
            cell.MouseLeftButtonUp += (s, e) =>
            {
                if (pressedCell != cell) return;
                pressedCell = null;
                e.Handled = true;
                Launch(item);
            };

            cell.DragEnter += (s, e) => OnCellDragOver(cell, e);
            cell.DragOver += (s, e) => OnCellDragOver(cell, e);
            cell.DragLeave += (s, e) => cell.Background = CellHit;
            cell.Drop += (s, e) =>
            {
                cell.Background = CellHit;
                e.Handled = true;
                if (e.Data.GetData(TekaDrag.Format) is TekaDrag d)
                {
                    if (d.From == Folder) app.ReorderItem(Folder, d.Item, index);
                    else app.MoveItem(d, Folder);
                }
                else if (e.Data.GetData(DataFormats.FileDrop) is string[] files)
                {
                    app.AddPaths(Folder, files, index);
                }
            };

            cell.ContextMenu = BuildItemMenu(item, name, stack);
            cell.ContextMenuOpening += (s, e) => suppressClose = true;
            cell.ContextMenuClosing += (s, e) => suppressClose = false;
            return cell;
        }

        private void OnCellDragOver(Border cell, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(TekaDrag.Format))
            {
                e.Effects = DragDropEffects.Move;
                if (!(e.Data.GetData(TekaDrag.Format) is TekaDrag d && cell.Tag == d.Item))
                    cell.Background = CellDrop;
            }
            else if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                e.Effects = DragDropEffects.Copy;
                cell.Background = CellDrop;
            }
            else
            {
                e.Effects = DragDropEffects.None;
            }
            e.Handled = true;
        }

        private ContextMenu BuildItemMenu(ItemData item, TextBlock nameBlock, StackPanel stack)
        {
            var menu = new ContextMenu();
            menu.Items.Add(FolderTile.MakeMenuItem("Відкрити", () => Launch(item)));
            menu.Items.Add(FolderTile.MakeMenuItem("Показати в Провіднику", () => Reveal(item)));
            menu.Items.Add(FolderTile.MakeMenuItem("Перейменувати", () => BeginItemRename(item, nameBlock, stack)));
            menu.Items.Add(new Separator());
            string removeText = string.IsNullOrEmpty(item.MovedFrom) ? "Прибрати з папки" : "Повернути на робочий стіл";
            menu.Items.Add(FolderTile.MakeMenuItem(removeText, () => app.RemoveItem(Folder, item)));
            return menu;
        }

        private void BeginItemRename(ItemData item, TextBlock nameBlock, StackPanel stack)
        {
            int idx = stack.Children.IndexOf(nameBlock);
            if (idx < 0) return;

            var box = new TextBox
            {
                Text = item.Name,
                Background = new SolidColorBrush(Color.FromArgb(0x33, 0, 0, 0)),
                BorderThickness = new Thickness(0),
                Foreground = Brushes.White,
                CaretBrush = Brushes.White,
                FontFamily = UiFont,
                FontSize = 12,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(4, 0, 4, 0),
                MaxLength = 60
            };
            bool done = false;
            void Finish(bool commit)
            {
                if (done) return;
                done = true;
                if (commit) app.RenameItem(Folder, item, box.Text);
                Dispatcher.BeginInvoke(new Action(Rebuild), System.Windows.Threading.DispatcherPriority.Background);
            }
            box.KeyDown += (s, e) =>
            {
                if (e.Key == Key.Enter) { Finish(true); e.Handled = true; }
                else if (e.Key == Key.Escape) { Finish(false); e.Handled = true; }
            };
            box.LostKeyboardFocus += (s, e) => Finish(true);

            stack.Children.RemoveAt(idx);
            stack.Children.Insert(idx, box);
            Dispatcher.BeginInvoke(new Action(() =>
            {
                box.Focus();
                box.SelectAll();
            }), System.Windows.Threading.DispatcherPriority.Input);
        }

        private Border MakeAddCell()
        {
            var dash = new Rectangle
            {
                Width = 46,
                Height = 46,
                RadiusX = 13,
                RadiusY = 13,
                Stroke = new SolidColorBrush(Color.FromArgb(0x80, 255, 255, 255)),
                StrokeThickness = 1.5,
                StrokeDashArray = new DoubleCollection { 3, 2.5 }
            };
            var plus = new TextBlock
            {
                Text = "+",
                Foreground = Soft,
                FontFamily = UiFont,
                FontSize = 26,
                FontWeight = FontWeights.Light,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, -3, 0, 0)
            };
            var iconArea = new Grid { Width = 48, Height = 48, Margin = new Thickness(0, 9, 0, 6) };
            iconArea.Children.Add(dash);
            iconArea.Children.Add(plus);

            var text = new TextBlock
            {
                Text = "Додати",
                Foreground = Soft,
                FontFamily = UiFont,
                FontSize = 12,
                TextAlignment = TextAlignment.Center
            };

            var stack = new StackPanel();
            stack.Children.Add(iconArea);
            stack.Children.Add(text);

            var cell = new Border
            {
                Width = CellW - 6,
                Height = CellH - 6,
                Margin = new Thickness(3),
                CornerRadius = new CornerRadius(14),
                Background = CellHit,
                Child = stack,
                Cursor = Cursors.Hand
            };
            cell.MouseEnter += (s, e) => cell.Background = CellHover;
            cell.MouseLeave += (s, e) => cell.Background = CellHit;
            cell.MouseLeftButtonUp += (s, e) => { e.Handled = true; AddViaDialog(); };
            return cell;
        }

        private void AddViaDialog()
        {
            suppressClose = true;
            try
            {
                string programs = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu), "Programs");
                var dlg = new Microsoft.Win32.OpenFileDialog
                {
                    Title = "Додати в «" + Folder.Name + "»",
                    Multiselect = true,
                    DereferenceLinks = false,
                    Filter = "Програми та ярлики|*.exe;*.lnk;*.url;*.appref-ms|Усі файли|*.*",
                    InitialDirectory = Directory.Exists(programs) ? programs : ""
                };
                if (dlg.ShowDialog(this) == true)
                    app.AddPaths(Folder, dlg.FileNames);
            }
            catch (Exception ex)
            {
                Storage.Log(ex);
            }
            finally
            {
                suppressClose = false;
                if (!closing) Activate();
            }
        }

        // ---------------- дії ----------------

        private void Launch(ItemData item)
        {
            try
            {
                var psi = new ProcessStartInfo(item.Path) { UseShellExecute = true };
                string ext = System.IO.Path.GetExtension(item.Path).ToLowerInvariant();
                if (ext == ".exe")
                {
                    string dir = System.IO.Path.GetDirectoryName(item.Path);
                    if (!string.IsNullOrEmpty(dir)) psi.WorkingDirectory = dir;
                }
                Process.Start(psi);
                CloseAnimated();
            }
            catch (Exception ex)
            {
                Storage.Log(ex);
                suppressClose = true;
                MessageBox.Show(this, $"Не вдалося відкрити «{item.Name}».\n\n{ex.Message}", "Teka",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                suppressClose = false;
            }
        }

        private void Reveal(ItemData item)
        {
            try
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{item.Path}\"") { UseShellExecute = true });
                CloseAnimated();
            }
            catch (Exception ex)
            {
                Storage.Log(ex);
            }
        }

        // ---------------- кидання на панель ----------------

        private void OnWindowDragOver(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop)) e.Effects = DragDropEffects.Copy;
            else if (e.Data.GetDataPresent(TekaDrag.Format)) e.Effects = DragDropEffects.Move;
            else e.Effects = DragDropEffects.None;
            e.Handled = true;
        }

        private void OnWindowDrop(object sender, DragEventArgs e)
        {
            e.Handled = true;
            if (e.Data.GetData(TekaDrag.Format) is TekaDrag d)
            {
                if (d.From == Folder) app.ReorderItem(Folder, d.Item, Folder.Items.Count);
                else app.MoveItem(d, Folder);
            }
            else if (e.Data.GetData(DataFormats.FileDrop) is string[] files)
            {
                app.AddPaths(Folder, files);
            }
        }
    }
}

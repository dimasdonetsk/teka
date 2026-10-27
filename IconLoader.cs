using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Teka
{
    /// <summary>Якісні іконки файлів, ярликів і папок через Shell (IShellItemImageFactory).</summary>
    internal static class IconLoader
    {
        private static readonly Dictionary<string, ImageSource> cache = new(StringComparer.OrdinalIgnoreCase);

        [ComImport]
        [Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellItemImageFactory
        {
            [PreserveSig]
            int GetImage(SIZE size, int flags, out IntPtr phbm);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SIZE
        {
            public int cx;
            public int cy;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct BITMAP
        {
            public int bmType;
            public int bmWidth;
            public int bmHeight;
            public int bmWidthBytes;
            public ushort bmPlanes;
            public ushort bmBitsPixel;
            public IntPtr bmBits;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct BITMAPINFOHEADER
        {
            public uint biSize;
            public int biWidth;
            public int biHeight;
            public ushort biPlanes;
            public ushort biBitCount;
            public uint biCompression;
            public uint biSizeImage;
            public int biXPelsPerMeter;
            public int biYPelsPerMeter;
            public uint biClrUsed;
            public uint biClrImportant;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DIBSECTION
        {
            public BITMAP dsBm;
            public BITMAPINFOHEADER dsBmih;
            public uint dsBitfields0;
            public uint dsBitfields1;
            public uint dsBitfields2;
            public IntPtr dshSection;
            public uint dsOffset;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
        private static extern int SHCreateItemFromParsingName(
            string pszPath,
            IntPtr pbc,
            [MarshalAs(UnmanagedType.LPStruct)] Guid riid,
            [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory ppv);

        [DllImport("gdi32.dll")]
        private static extern int GetObject(IntPtr hgdiobj, int cbBuffer, ref DIBSECTION lpvObject);

        private const int SIIGBF_BIGGERSIZEOK = 0x1;
        private const int SIIGBF_ICONONLY = 0x4;

        public static void Forget(string path)
        {
            if (path == null) return;
            foreach (var key in new List<string>(cache.Keys))
                if (key.StartsWith(path + "|", StringComparison.OrdinalIgnoreCase))
                    cache.Remove(key);
        }

        public static ImageSource Get(string path, int size = 96)
        {
            if (string.IsNullOrEmpty(path)) return null;
            string key = path + "|" + size;
            if (cache.TryGetValue(key, out var cached)) return cached;

            ImageSource result = null;
            try
            {
                result = FromShell(path, size);
            }
            catch
            {
            }

            if (result == null)
            {
                try
                {
                    if (File.Exists(path))
                    {
                        using var ico = System.Drawing.Icon.ExtractAssociatedIcon(path);
                        if (ico != null)
                        {
                            var bs = Imaging.CreateBitmapSourceFromHIcon(ico.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                            bs.Freeze();
                            result = bs;
                        }
                    }
                }
                catch
                {
                }
            }

            cache[key] = result;
            return result;
        }

        private static ImageSource FromShell(string path, int size)
        {
            Guid iid = typeof(IShellItemImageFactory).GUID;
            int hr = SHCreateItemFromParsingName(path, IntPtr.Zero, iid, out var factory);
            if (hr != 0 || factory == null) return null;

            IntPtr hbmp = IntPtr.Zero;
            try
            {
                hr = factory.GetImage(new SIZE { cx = size, cy = size }, SIIGBF_ICONONLY | SIIGBF_BIGGERSIZEOK, out hbmp);
                if (hr != 0 || hbmp == IntPtr.Zero) return null;
                return FromHBitmap(hbmp);
            }
            finally
            {
                if (hbmp != IntPtr.Zero) Native.DeleteObject(hbmp);
                Marshal.ReleaseComObject(factory);
            }
        }

        private static BitmapSource FromHBitmap(IntPtr hbmp)
        {
            var dib = new DIBSECTION();
            int got = GetObject(hbmp, Marshal.SizeOf<DIBSECTION>(), ref dib);

            if (got == 0 || dib.dsBm.bmBits == IntPtr.Zero || dib.dsBm.bmBitsPixel != 32)
            {
                var fallback = Imaging.CreateBitmapSourceFromHBitmap(hbmp, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                fallback.Freeze();
                return fallback;
            }

            int w = dib.dsBm.bmWidth;
            int h = Math.Abs(dib.dsBm.bmHeight);
            int stride = dib.dsBm.bmWidthBytes;
            var buffer = new byte[stride * h];
            Marshal.Copy(dib.dsBm.bmBits, buffer, 0, buffer.Length);

            // Додатна висота в заголовку = рядки знизу вгору → перевертаємо.
            if (dib.dsBmih.biHeight > 0)
            {
                var flipped = new byte[buffer.Length];
                for (int y = 0; y < h; y++)
                    Buffer.BlockCopy(buffer, y * stride, flipped, (h - 1 - y) * stride, stride);
                buffer = flipped;
            }

            // Якщо альфа-канал порожній — картинка непрозора.
            bool anyAlpha = false;
            for (int i = 3; i < buffer.Length; i += 4)
            {
                if (buffer[i] != 0) { anyAlpha = true; break; }
            }
            if (!anyAlpha)
            {
                for (int i = 3; i < buffer.Length; i += 4) buffer[i] = 255;
            }

            var bs = BitmapSource.Create(w, h, 96, 96, PixelFormats.Pbgra32, null, buffer, stride);
            bs.Freeze();
            return bs;
        }
    }
}

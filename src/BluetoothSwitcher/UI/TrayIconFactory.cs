using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace BluetoothSwitcher.UI;

/// <summary>Draws the tray icon at runtime (blue square with a keyboard glyph) to avoid shipping an .ico file.</summary>
internal static class TrayIconFactory
{
    public static Icon Create(bool busy = false)
    {
        const int size = 32;
        using var bmp = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.Clear(Color.Transparent);

            using var path = RoundedRect(new Rectangle(1, 1, size - 2, size - 2), 7);
            using var fill = new SolidBrush(busy ? Color.FromArgb(230, 140, 0) : Color.FromArgb(0, 103, 192));
            g.FillPath(fill, path);

            // Segoe Fluent Icons (Win11) / Segoe MDL2 Assets (Win10): U+E765 is the keyboard glyph.
            var fontName = FontFamily.Families.Any(f => f.Name == "Segoe Fluent Icons") ? "Segoe Fluent Icons" : "Segoe MDL2 Assets";
            using var font = new Font(fontName, 17f, FontStyle.Regular, GraphicsUnit.Pixel);
            using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.DrawString("", font, Brushes.White, new RectangleF(0, 1, size, size), format);
        }

        var hIcon = bmp.GetHicon();
        try
        {
            using var temp = Icon.FromHandle(hIcon);
            return (Icon)temp.Clone();
        }
        finally
        {
            DestroyIcon(hIcon);
        }
    }

    private static GraphicsPath RoundedRect(Rectangle r, int radius)
    {
        int d = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr hIcon);
}

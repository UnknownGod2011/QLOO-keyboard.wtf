namespace KeyboardWtf;

using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

// The bundled mark is never cropped to fill its row. A small proportional
// inset preserves its full edges at native and scaled Settings/popup sizes.
internal sealed class ConnectorProviderIcon : PictureBox
{
    internal ConnectorProviderIcon() => SetStyle(ControlStyles.ResizeRedraw
        | ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);

    internal static Rectangle ImageBounds(Size image, Size client)
    {
        if (image.Width <= 0 || image.Height <= 0 || client.Width <= 2 || client.Height <= 2)
            return Rectangle.Empty;
        var inset = Math.Max(1, (int)Math.Round(Math.Min(client.Width, client.Height) / 12d));
        var available = new Size(client.Width - inset * 2, client.Height - inset * 2);
        if (available.Width <= 0 || available.Height <= 0) return Rectangle.Empty;
        var scale = Math.Min((double)available.Width / image.Width, (double)available.Height / image.Height);
        var width = Math.Max(1, (int)Math.Round(image.Width * scale));
        var height = Math.Max(1, (int)Math.Round(image.Height * scale));
        return new Rectangle((client.Width - width) / 2, (client.Height - height) / 2, width, height);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        if (Image is not { } image) return;
        var bounds = ImageBounds(image.Size, ClientSize);
        if (bounds.IsEmpty) return;
        var state = e.Graphics.Save();
        try
        {
            e.Graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            e.Graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            using var attributes = new ImageAttributes();
            attributes.SetWrapMode(WrapMode.TileFlipXY);
            e.Graphics.DrawImage(image, bounds, 0, 0, image.Width, image.Height, GraphicsUnit.Pixel, attributes);
        }
        finally { e.Graphics.Restore(state); }
    }
}

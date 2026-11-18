using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using Valve.VR;
using VrcTranslator;

namespace VrcTranslator.Desktop;

internal sealed class SubtitleOverlay : IDisposable
{
    private const int Width = 1024;
    private const int MaxHeight = 480;
    private const int Padding = 22;
    private readonly CVROverlay vr;
    private ulong handle;
    private bool visible;
    private string? textureText;
    private int textureFont;
    private SubtitleSettings? layout;

    public SubtitleOverlay(CVROverlay vr, string key)
    {
        this.vr = vr;
        Check(vr.CreateOverlay(key + ".subtitles", "VRChat 语音翻译字幕", ref handle));
        try
        {
            Check(vr.SetOverlayInputMethod(handle, VROverlayInputMethod.None));
            Check(vr.SetOverlayFlag(handle, VROverlayFlags.NoBackside, true));
            Check(vr.HideOverlay(handle));
        }
        catch { Dispose(); throw; }
    }

    public void Show(string text, SubtitleSettings settings)
    {
        if (layout?.DistanceMeters != settings.DistanceMeters || layout?.DownAngleDegrees != settings.DownAngleDegrees)
        {
            double angle = settings.DownAngleDegrees * Math.PI / 180;
            float cosine = (float)Math.Cos(angle);
            float sine = (float)Math.Sin(angle);
            float distance = (float)settings.DistanceMeters;
            var transform = new HmdMatrix34_t
            {
                m0 = 1, m5 = cosine, m6 = sine, m7 = -distance * sine,
                m9 = -sine, m10 = cosine, m11 = -distance * cosine
            };
            Check(vr.SetOverlayWidthInMeters(handle, (float)(2 * distance * Math.Tan(20 * Math.PI / 180))));
            Check(vr.SetOverlayTransformTrackedDeviceRelative(handle, OpenVR.k_unTrackedDeviceIndex_Hmd, ref transform));
        }
        if (layout?.OpacityPercent != settings.OpacityPercent)
            Check(vr.SetOverlayAlpha(handle, settings.OpacityPercent / 100f));
        layout = settings;

        if (textureText != text || textureFont != settings.FontPercent)
        {
            using var bitmap = Render(text, settings.FontPercent);
            Upload(bitmap);
            textureText = text;
            textureFont = settings.FontPercent;
        }
        if (!visible)
        {
            Check(vr.ShowOverlay(handle));
            visible = true;
        }
    }

    private void Upload(Bitmap bitmap)
    {
        var data = bitmap.LockBits(new(0, 0, bitmap.Width, bitmap.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            byte[] rgba = new byte[bitmap.Width * bitmap.Height * 4];
            int stride = bitmap.Width * 4;
            for (int y = 0; y < bitmap.Height; y++) Marshal.Copy(data.Scan0 + y * data.Stride, rgba, y * stride, stride);
            for (int i = 0; i < rgba.Length; i += 4) (rgba[i], rgba[i + 2]) = (rgba[i + 2], rgba[i]);
            var pinned = GCHandle.Alloc(rgba, GCHandleType.Pinned);
            try { Check(vr.SetOverlayRaw(handle, pinned.AddrOfPinnedObject(), (uint)bitmap.Width, (uint)bitmap.Height, 4)); }
            finally { pinned.Free(); }
        }
        finally { bitmap.UnlockBits(data); }
    }

    private static Bitmap Render(string text, int fontPercent)
    {
        using var scratch = new Bitmap(1, 1);
        using var measure = Graphics.FromImage(scratch);
        using var format = new StringFormat { Alignment = StringAlignment.Center, Trimming = StringTrimming.None };
        float fontSize = 44 * fontPercent / 100f;
        var fontFamily = SystemFonts.MessageBoxFont!.FontFamily;
        var area = new SizeF(Width - 2 * Padding, MaxHeight - 2 * Padding);
        int height;
        while (true)
        {
            using var font = new Font(fontFamily, fontSize, FontStyle.Regular, GraphicsUnit.Pixel);
            height = (int)Math.Ceiling(measure.MeasureString(text, font, new SizeF(area.Width, 64000), format).Height) + 2 * Padding;
            if (height <= MaxHeight || fontSize <= 1) break;
            fontSize = Math.Max(1, fontSize * 0.85f);
        }
        var result = new Bitmap(Width, Math.Min(height, MaxHeight), PixelFormat.Format32bppArgb);
        using var drawing = Graphics.FromImage(result);
        drawing.SmoothingMode = SmoothingMode.AntiAlias;
        drawing.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        using var background = new SolidBrush(Color.FromArgb(182, Color.Black));
        using var finalFont = new Font(fontFamily, fontSize, FontStyle.Regular, GraphicsUnit.Pixel);
        using var shape = MakeBackground(result.Height);
        drawing.FillPath(background, shape);
        drawing.DrawString(text, finalFont, Brushes.White, new RectangleF(Padding, Padding, area.Width, result.Height - 2 * Padding), format);
        return result;
    }

    private static GraphicsPath MakeBackground(int height)
    {
        const int diameter = 36;
        var path = new GraphicsPath();
        path.AddArc(0, 0, diameter, diameter, 180, 90);
        path.AddArc(Width - diameter, 0, diameter, diameter, 270, 90);
        path.AddArc(Width - diameter, height - diameter, diameter, diameter, 0, 90);
        path.AddArc(0, height - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }

    public void Hide()
    {
        if (!visible) return;
        Check(vr.HideOverlay(handle));
        visible = false;
    }

    private static void Check(EVROverlayError error)
    {
        if (error != EVROverlayError.None) throw new ServiceException($"SteamVR 字幕操作失败（{error}）。");
    }

    public void Dispose()
    {
        if (handle == 0) return;
        vr.HideOverlay(handle);
        vr.DestroyOverlay(handle);
        handle = 0;
    }
}

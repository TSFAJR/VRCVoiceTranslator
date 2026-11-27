using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using Valve.VR;
using VrcTranslator;

namespace VrcTranslator.Desktop;

internal sealed class SubtitleOverlay(CVRSystem system, CVROverlay vr, string key) : IDisposable
{
    private const int Width = 1024;
    private const int PreferredMaxHeight = 480;
    private const int Padding = 22;
    private SubtitleTexture? texture;
    private ulong handle;
    private bool visible;
    private string? textureText;
    private int textureFont;
    private SubtitleSettings? layout;

    public void Show(string text, SubtitleSettings settings)
    {
        if (handle == 0)
        {
            ulong created = 0;
            Check(vr.CreateOverlay(key + ".subtitles", "VRChat 语音翻译字幕", ref created), nameof(vr.CreateOverlay));
            handle = created;
            Check(vr.SetOverlayInputMethod(handle, VROverlayInputMethod.None), nameof(vr.SetOverlayInputMethod));
            Check(vr.SetOverlayFlag(handle, VROverlayFlags.NoBackside, true), nameof(vr.SetOverlayFlag));
        }
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
            Check(vr.SetOverlayWidthInMeters(handle, (float)(2 * distance * Math.Tan(20 * Math.PI / 180))), nameof(vr.SetOverlayWidthInMeters));
            Check(vr.SetOverlayTransformTrackedDeviceRelative(handle, OpenVR.k_unTrackedDeviceIndex_Hmd, ref transform), nameof(vr.SetOverlayTransformTrackedDeviceRelative));
        }
        if (layout?.OpacityPercent != settings.OpacityPercent)
            Check(vr.SetOverlayAlpha(handle, settings.OpacityPercent / 100f), nameof(vr.SetOverlayAlpha));
        layout = settings;

        if (textureText != text || textureFont != settings.FontPercent)
        {
            using var bitmap = Render(text, settings.FontPercent);
            texture ??= new SubtitleTexture(system);
            texture.Upload(vr, handle, bitmap);
            textureText = text;
            textureFont = settings.FontPercent;
        }
        if (!visible)
        {
            Check(vr.ShowOverlay(handle), nameof(vr.ShowOverlay));
            visible = true;
        }
    }

    private static Bitmap Render(string text, int fontPercent)
    {
        using var scratch = new Bitmap(1, 1);
        using var measure = Graphics.FromImage(scratch);
        measure.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        using var format = new StringFormat { Alignment = StringAlignment.Center, Trimming = StringTrimming.None };
        float fontSize = 44 * fontPercent / 100f;
        using var messageFont = SystemFonts.MessageBoxFont!;
        using var fontFamily = messageFont.FontFamily;
        const int textWidth = Width - 2 * Padding;
        int height;
        while (true)
        {
            using var font = new Font(fontFamily, fontSize, FontStyle.Regular, GraphicsUnit.Pixel);
            int textHeight = (int)Math.Ceiling(measure.MeasureString(text, font, new SizeF(textWidth, 64000), format).Height);
            height = textHeight + 2 * Padding;
            if (height <= PreferredMaxHeight || fontSize <= 1) break;
            fontSize = Math.Max(1, fontSize * 0.85f);
        }
        var result = new Bitmap(Width, height, PixelFormat.Format32bppArgb);
        using var drawing = Graphics.FromImage(result);
        drawing.SmoothingMode = SmoothingMode.AntiAlias;
        drawing.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        using var background = new SolidBrush(Color.FromArgb(182, Color.Black));
        using var finalFont = new Font(fontFamily, fontSize, FontStyle.Regular, GraphicsUnit.Pixel);
        using var shape = MakeBackground(result.Height);
        drawing.FillPath(background, shape);
        drawing.DrawString(text, finalFont, Brushes.White, new RectangleF(Padding, Padding, textWidth, height - 2 * Padding), format);
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
        Check(vr.HideOverlay(handle), nameof(vr.HideOverlay));
        visible = false;
        textureText = null;
    }

    internal static void Check(EVROverlayError error, string operation)
    {
        if (error != EVROverlayError.None) throw new ServiceException($"SteamVR 字幕 {operation} 失败（{error}）。");
    }

    public void Destroy(bool disconnecting)
    {
        try
        {
            if (handle != 0)
            {
                var error = vr.DestroyOverlay(handle);
                if (!disconnecting && error is not (EVROverlayError.InvalidHandle or EVROverlayError.UnknownOverlay))
                    Check(error, nameof(vr.DestroyOverlay));
            }
        }
        finally
        {
            handle = 0;
            visible = false;
            textureText = null;
            textureFont = 0;
            layout = null;
            texture?.Dispose();
            texture = null;
        }
    }

    public void Dispose() => Destroy(disconnecting: true);
}

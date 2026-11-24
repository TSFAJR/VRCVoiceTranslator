using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Valve.VR;
using VrcTranslator;

namespace VrcTranslator.Desktop;

// Owned and used exclusively by the OpenVR worker. The texture stays alive between submissions.
internal sealed unsafe class SubtitleTexture : IDisposable
{
    private nint device, context, texture, spare;
    private Size textureSize, spareSize;

    public SubtitleTexture(CVRSystem system)
    {
        int adapterIndex = -1;
        system.GetDXGIOutputInfo(ref adapterIndex);
        if (adapterIndex < 0) throw new ServiceException("SteamVR 未提供头显的 DXGI 显卡。");

        Guid factoryId = new("770aae78-f26f-4dba-a829-253c83d1b387"); // IDXGIFactory1
        CheckHr(CreateDXGIFactory1(in factoryId, out var factory), "CreateDXGIFactory1");
        nint adapter = 0;
        try
        {
            // IDXGIFactory::EnumAdapters (dxgi.h)
            var enumAdapters = (delegate* unmanaged[Stdcall]<nint, uint, nint*, int>)(*(nint**)factory)[7];
            CheckHr(enumAdapters(factory, (uint)adapterIndex, &adapter), "EnumAdapters");
            const uint bgraSupport = 0x20, sdkVersion = 7;
            CheckHr(D3D11CreateDevice(adapter, 0, 0, bgraSupport, 0, 0, sdkVersion, out device, out _, out context),
                "D3D11CreateDevice");
        }
        catch { Dispose(); throw; }
        finally { Release(ref adapter); Release(ref factory); }
    }

    public void Upload(CVROverlay overlay, ulong handle, Bitmap bitmap)
    {
        nint replacement = 0;
        try
        {
            // SteamVR may still be sampling the front texture when the next caption arrives.
            if (spare == 0 || bitmap.Size != spareSize)
            {
                var desc = new TextureDescription((uint)bitmap.Width, (uint)bitmap.Height);
                // ID3D11Device::CreateTexture2D (d3d11.h)
                var create = (delegate* unmanaged[Stdcall]<nint, TextureDescription*, nint, nint*, int>)(*(nint**)device)[5];
                CheckHr(create(device, &desc, 0, &replacement), "CreateTexture2D");
            }
            nint target = replacement != 0 ? replacement : spare;
            var data = bitmap.LockBits(new(Point.Empty, bitmap.Size), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                // GDI+ Format32bppArgb is BGRA in memory on Windows x64.
                if (data.Stride < bitmap.Width * 4) throw new ServiceException("字幕位图行跨度无效。");
                var update = (delegate* unmanaged[Stdcall]<nint, nint, uint, nint, nint, uint, uint, void>)(*(nint**)context)[48];
                update(context, target, 0, 0, data.Scan0, (uint)data.Stride, 0); // UpdateSubresource
            }
            finally { bitmap.UnlockBits(data); }
            var submission = new Texture_t { handle = target, eType = ETextureType.DirectX, eColorSpace = EColorSpace.Gamma };
            SubtitleOverlay.Check(overlay.SetOverlayTexture(handle, ref submission), nameof(overlay.SetOverlayTexture));
            // OpenVR queues its texture copy on our D3D11 context during SetOverlayTexture.
            // A static caption has no Present loop to dispatch that copy before the next utterance.
            var flush = (delegate* unmanaged[Stdcall]<nint, void>)(*(nint**)context)[111];
            flush(context);
            if (replacement != 0)
            {
                Release(ref spare);
                replacement = 0;
            }
            spare = texture;
            spareSize = textureSize;
            texture = target;
            textureSize = bitmap.Size;
        }
        finally { Release(ref replacement); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TextureDescription(uint width, uint height)
    {
        public uint Width = width, Height = height, MipLevels = 1, ArraySize = 1;
        public uint Format = 87; // DXGI_FORMAT_B8G8R8A8_UNORM
        public uint SampleCount = 1, SampleQuality = 0, Usage = 0;
        public uint BindFlags = 0x8 | 0x20; // SHADER_RESOURCE | RENDER_TARGET
        public uint CpuAccessFlags = 0, MiscFlags = 2; // D3D11_RESOURCE_MISC_SHARED
    }

    [DllImport("dxgi.dll", ExactSpelling = true)]
    private static extern int CreateDXGIFactory1(in Guid iid, out nint factory);

    [DllImport("d3d11.dll", ExactSpelling = true)]
    private static extern int D3D11CreateDevice(nint adapter, uint driverType, nint software, uint flags, nint levels,
        uint levelCount, uint sdkVersion, out nint device, out uint level, out nint context);

    private static void CheckHr(int hr, string operation)
    {
        if (hr < 0) throw new ServiceException($"字幕 {operation} 失败（0x{hr:X8}）。");
    }

    private static void Release(ref nint pointer)
    {
        if (pointer == 0) return;
        Marshal.Release(pointer);
        pointer = 0;
    }

    public void Dispose()
    {
        Release(ref texture);
        Release(ref spare);
        Release(ref context);
        Release(ref device);
    }
}

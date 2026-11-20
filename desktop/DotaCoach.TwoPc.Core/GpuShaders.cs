using System.Runtime.InteropServices;
using System.Text;
using static DotaCoach.TwoPc.NativeCom;

namespace DotaCoach.TwoPc;

/// <summary>HLSL compiled at start-up with the system's d3dcompiler_47 (part of Windows 10/11).</summary>
internal static unsafe class GpuShaders
{
    public static byte[] Compile(string source, string entry, string target)
    {
        byte[] text = Encoding.UTF8.GetBytes(source), name = Encoding.ASCII.GetBytes(entry + "\0"), profile = Encoding.ASCII.GetBytes(target + "\0");
        nint code = 0, errors = 0;
        int hr;
        fixed (byte* s = text, e = name, t = profile)
            hr = D3DCompile(s, text.Length, null, 0, 0, e, t, 1 << 15 /* optimization level 3 */, 0, &code, &errors);
        try {
            if (hr < 0) {
                string message = errors == 0 ? "" : Marshal.PtrToStringAnsi(Blob(errors, 3), (int)Blob(errors, 4)) ?? "";
                throw new InvalidOperationException($"Shader {entry} did not compile: {message}");
            }
            var bytes = new byte[(int)Blob(code, 4)];
            Marshal.Copy(Blob(code, 3), bytes, 0, bytes.Length);
            return bytes;
        } finally { Release(code); Release(errors); }
        static nint Blob(nint blob, int slot) => ((delegate* unmanaged[Stdcall]<nint, nint>)Slot(blob, slot))(blob);
    }
    [DllImport("d3dcompiler_47.dll")] private static extern int D3DCompile(byte* source, nint size, byte* name, nint defines, nint include,
        byte* entry, byte* target, uint flags, uint effectFlags, nint* code, nint* errors);
}

/// <summary>ID3D11Device / ID3D11DeviceContext calls shared by the scanner and the overlay (slots per d3d11.h).</summary>
internal static unsafe class D3D
{
    [StructLayout(LayoutKind.Sequential)] public struct BufferDesc { public uint ByteWidth; public int Usage; public uint BindFlags, CpuAccessFlags, MiscFlags, StructureByteStride; }
    [StructLayout(LayoutKind.Sequential)] public struct Mapped { public nint Data; public uint RowPitch, DepthPitch; }
    [StructLayout(LayoutKind.Sequential)] public struct ViewDesc { public int Format, Dimension; public uint A, B, C, D; }

    public static nint Buffer(nint device, BufferDesc desc, void* initial = null)
    {
        nint buffer = 0;
        var data = stackalloc nint[3]; data[0] = (nint)initial; data[1] = 0; data[2] = 0;
        Hr(((delegate* unmanaged[Stdcall]<nint, BufferDesc*, nint*, nint*, int>)Slot(device, 3))(device, &desc, initial == null ? null : data, &buffer));
        return buffer;
    }
    public static nint ShaderView(nint device, nint resource, ViewDesc* desc)
    {
        nint view = 0;
        Hr(((delegate* unmanaged[Stdcall]<nint, nint, ViewDesc*, nint*, int>)Slot(device, 7))(device, resource, desc, &view));
        return view;
    }
    public static nint AccessView(nint device, nint resource, ViewDesc desc)
    {
        nint view = 0;
        Hr(((delegate* unmanaged[Stdcall]<nint, nint, ViewDesc*, nint*, int>)Slot(device, 8))(device, resource, &desc, &view));
        return view;
    }
    /// <summary>CreateVertexShader 12, CreatePixelShader 15, CreateComputeShader 18.</summary>
    public static nint Shader(nint device, int slot, byte[] code)
    {
        nint shader = 0;
        fixed (byte* bytes = code)
            Hr(((delegate* unmanaged[Stdcall]<nint, byte*, nint, nint, nint*, int>)Slot(device, slot))(device, bytes, code.Length, 0, &shader));
        return shader;
    }
    /// <summary>Map with DO_NOT_WAIT, polled: a blocking Map can spin a core inside some drivers.</summary>
    public static Mapped MapRead(nint context, nint resource, PreciseTimer timer, int timeoutMs = 500)
    {
        Mapped mapped; int hr; long started = Environment.TickCount64;
        while ((hr = ((delegate* unmanaged[Stdcall]<nint, nint, uint, int, uint, Mapped*, int>)Slot(context, 14))(
            context, resource, 0, 1, 0x100000, &mapped)) == unchecked((int)0x887A000A)) {
            if (Environment.TickCount64 - started > timeoutMs) throw new TimeoutException("GPU work did not finish");
            timer.Wait(2_500); // 0.25 ms
        }
        Hr(hr);
        return mapped;
    }
    public static Mapped MapWrite(nint context, nint resource)
    {
        Mapped mapped;
        Hr(((delegate* unmanaged[Stdcall]<nint, nint, uint, int, uint, Mapped*, int>)Slot(context, 14))(context, resource, 0, 4 /* write discard */, 0, &mapped));
        return mapped;
    }
    public static void Unmap(nint context, nint resource) => ((delegate* unmanaged[Stdcall]<nint, nint, uint, void>)Slot(context, 15))(context, resource, 0);
    public static void Update(nint context, nint resource, void* data) =>
        ((delegate* unmanaged[Stdcall]<nint, nint, uint, nint, void*, uint, uint, void>)Slot(context, 48))(context, resource, 0, 0, data, 0, 0);
    public static void Copy(nint context, nint destination, nint source) =>
        ((delegate* unmanaged[Stdcall]<nint, nint, nint, void>)Slot(context, 47))(context, destination, source);
    /// <summary>ID3D11Texture2D::GetDesc (slot 10): width and height.</summary>
    public static (int Width, int Height) Size(nint texture)
    {
        var desc = stackalloc uint[11];
        ((delegate* unmanaged[Stdcall]<nint, uint*, void>)Slot(texture, 10))(texture, desc);
        return ((int)desc[0], (int)desc[1]);
    }
    public static void Flush(nint context) => ((delegate* unmanaged[Stdcall]<nint, void>)Slot(context, 111))(context);
}

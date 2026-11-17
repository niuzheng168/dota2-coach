using System.Runtime.InteropServices;

namespace DotaCoach.TwoPc;

/// <summary>
/// Raw COM/WinRT vtable access. Slot numbers follow the interface order in the Windows SDK headers
/// (d3d11.h, mfobjects.h, mftransform.h, windows.graphics.capture.h); IUnknown is 0-2, IInspectable 0-5.
/// </summary>
internal static unsafe class NativeCom
{
    public static nint Slot(nint p, int index) => ((nint*)(*(nint*)p))[index];
    public static void Release(nint p) { if (p != 0) ((delegate* unmanaged[Stdcall]<nint, uint>)Slot(p, 2))(p); }
    public static void Hr(int hr) { if (hr < 0) Marshal.ThrowExceptionForHR(hr); }
    public static nint Query(nint p, Guid iid)
    {
        nint result = 0;
        Hr(((delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)Slot(p, 0))(p, &iid, &result));
        return result;
    }
    public static nint TryQuery(nint p, Guid iid)
    {
        nint result = 0;
        return ((delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)Slot(p, 0))(p, &iid, &result) < 0 ? 0 : result;
    }
    /// <summary>WinRT activation factory for a runtime class, queried for a specific statics/interop interface.</summary>
    public static nint Factory(string runtimeClass, Guid iid)
    {
        nint name = 0, factory = 0;
        Hr(WindowsCreateString(runtimeClass, runtimeClass.Length, &name));
        try { Hr(RoGetActivationFactory(name, &iid, &factory)); return factory; }
        finally { WindowsDeleteString(name); }
    }
    [DllImport("combase.dll", CharSet = CharSet.Unicode)] private static extern int WindowsCreateString(string source, int length, nint* result);
    [DllImport("combase.dll")] private static extern int WindowsDeleteString(nint value);
    [DllImport("combase.dll")] private static extern int RoGetActivationFactory(nint classId, Guid* iid, nint* factory);
}

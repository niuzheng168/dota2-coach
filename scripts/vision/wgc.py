"""Windows.Graphics.Capture of one window from Python, through ctypes only (no extra packages).

Mirrors desktop/DotaCoach.TwoPc.Core/GpuCapture.cs: a free-threaded frame pool on a D3D11 device,
MinUpdateInterval lifted to 1 ms (Windows 11 24H2+), no capture border. Each frame is cropped on
the GPU to the requested client-area rectangle before the small CPU read-back, so reading the
bottom HUD band of a 1080p window copies about 1.4 MB instead of 8 MB. Works while the window is
covered; a minimised window produces no frames. The mouse cursor is left out of the image.
Vtable slots follow the Windows SDK headers (IUnknown 0-2, IInspectable 0-5).
"""
from __future__ import annotations
import ctypes
import ctypes.wintypes as wt
import time
import uuid
from ctypes import POINTER, byref, c_int, c_int64, c_uint, c_void_p

import numpy as np

user32, dwmapi, d3d11, combase = ctypes.windll.user32, ctypes.windll.dwmapi, ctypes.windll.d3d11, ctypes.windll.combase
HRESULT = ctypes.HRESULT  # raises OSError on failure
DXGI_FORMAT_B8G8R8A8_UNORM = 87
DXGI_ERROR_WAS_STILL_DRAWING = -2005270518  # 0x887A000A


class GUID(ctypes.Structure):
    _fields_ = [("bytes", ctypes.c_ubyte * 16)]


def iid(text: str) -> GUID:
    g = GUID()
    ctypes.memmove(byref(g), uuid.UUID(text).bytes_le, 16)
    return g


class SizeInt32(ctypes.Structure):
    _fields_ = [("width", c_int), ("height", c_int)]


class Box(ctypes.Structure):
    _fields_ = [("left", c_uint), ("top", c_uint), ("front", c_uint), ("right", c_uint), ("bottom", c_uint), ("back", c_uint)]


class TextureDesc(ctypes.Structure):
    _fields_ = [("width", c_uint), ("height", c_uint), ("mips", c_uint), ("array", c_uint), ("format", c_int),
                ("samples", c_uint), ("quality", c_uint), ("usage", c_int), ("bind", c_uint), ("cpu", c_uint), ("misc", c_uint)]


class Mapped(ctypes.Structure):
    _fields_ = [("data", c_void_p), ("row_pitch", c_uint), ("depth_pitch", c_uint)]


def call(p, slot: int, restype, *args):
    """Calls vtable entry `slot` of COM object `p` with (p, *args). Argument types come from the ctypes
    values; byref() results and None travel as pointers."""
    fn = ctypes.cast(p, POINTER(POINTER(c_void_p))).contents[slot]
    types = [type(a) if isinstance(a, (ctypes._SimpleCData, ctypes.Structure)) else c_void_p for a in args]
    return ctypes.WINFUNCTYPE(restype, c_void_p, *types)(fn)(p, *args)


def release(p) -> None:
    if p:
        call(p, 2, c_uint)


def query(p, text: str, required: bool = True):
    out = c_void_p()
    hr = call(p, 0, ctypes.c_long, byref(iid(text)), byref(out))
    if hr < 0 and required:
        raise OSError(hr, f"QueryInterface {text} failed")
    return out.value if hr >= 0 else None


def close(p) -> None:
    """IClosable.Close then Release: hands a frame/pool/session back to Windows immediately."""
    if not p:
        return
    closable = query(p, "30d5a829-7fa4-4026-83bb-d75bae4ea99e", required=False)
    if closable:
        call(closable, 6, HRESULT)
        release(closable)
    release(p)


def factory(runtime_class: str, interface: str):
    name, out = c_void_p(), c_void_p()
    combase.WindowsCreateString(ctypes.c_wchar_p(runtime_class), c_uint(len(runtime_class)), byref(name))
    try:
        combase.RoGetActivationFactory.restype = HRESULT
        combase.RoGetActivationFactory(name, byref(iid(interface)), byref(out))
        return out.value
    finally:
        combase.WindowsDeleteString(name)


def client_rect(window: int) -> tuple[int, int, int, int]:
    """(left, top, width, height) of the client area inside the captured image, which covers the
    DWM frame bounds (title bar and borders in windowed mode, nothing extra when borderless)."""
    rect, frame, origin = wt.RECT(), wt.RECT(), wt.POINT(0, 0)
    user32.GetClientRect(window, byref(rect))
    user32.ClientToScreen(window, byref(origin))
    if dwmapi.DwmGetWindowAttribute(window, 9, byref(frame), ctypes.sizeof(frame)):  # DWMWA_EXTENDED_FRAME_BOUNDS
        raise OSError("window bounds unavailable")
    return max(0, origin.x - frame.left), max(0, origin.y - frame.top), rect.right, rect.bottom


class WindowCapture:
    """Newest frames of one window, cropped to `region(width, height)` of its client area.

    Not thread-safe: create and use it on one thread."""

    def __init__(self, window: int, region=lambda w, h: (0, 0, w, h)):
        combase.RoInitialize(c_uint(1))  # multithreaded; an existing apartment is fine
        self.device = self.context = self.item = self.pool = self.session = self.staging = None
        try:
            self._open(window, region)
        except BaseException:
            self.close()
            raise

    def _open(self, window, region):
        user32.SetProcessDpiAwarenessContext(c_void_p(-4))  # physical pixels
        left, top, cw, ch = client_rect(window)
        x, y, w, h = region(cw, ch)
        if w <= 0 or h <= 0 or x < 0 or y < 0 or x + w > cw or y + h > ch:
            raise ValueError("crop outside the client area")
        self.crop = (left + x, top + y, w, h)
        self.client = (cw, ch)
        device, context, level = c_void_p(), c_void_p(), c_int()
        d3d11.D3D11CreateDevice.restype = HRESULT
        d3d11.D3D11CreateDevice(None, 1, None, c_uint(0x20), None, 0, 7, byref(device), byref(level), byref(context))
        self.device, self.context = device.value, context.value
        dxgi = query(self.device, "54ec77fa-1377-44e6-8c32-88fd5f44c84c")
        inspectable = c_void_p()
        try:
            d3d11.CreateDirect3D11DeviceFromDXGIDevice.restype = HRESULT
            d3d11.CreateDirect3D11DeviceFromDXGIDevice(c_void_p(dxgi), byref(inspectable))
        finally:
            release(dxgi)
        try:
            winrt_device = query(inspectable.value, "a37624ab-8d5f-4650-9d3e-9eae3d9bc670")
        finally:
            release(inspectable.value)
        try:
            interop = factory("Windows.Graphics.Capture.GraphicsCaptureItem", "3628e81b-3cac-4c60-b7f4-23ce0e0c3356")
            try:
                item = c_void_p()
                call(interop, 3, HRESULT, c_void_p(window), byref(iid("79c3f95b-31f7-4ec2-a464-632ef5d30760")), byref(item))
                self.item = item.value
            finally:
                release(interop)
            size = SizeInt32()
            call(self.item, 7, HRESULT, byref(size))
            self.size = (size.width, size.height)
            statics = factory("Windows.Graphics.Capture.Direct3D11CaptureFramePool", "589b103f-6bbc-5df5-a991-02e28b3b66d5")
            try:
                pool = c_void_p()
                call(statics, 6, HRESULT, c_void_p(winrt_device), c_int(DXGI_FORMAT_B8G8R8A8_UNORM), c_int(2), size, byref(pool))
                self.pool = pool.value
            finally:
                release(statics)
        finally:
            release(winrt_device)
        session = c_void_p()
        call(self.pool, 10, HRESULT, c_void_p(self.item), byref(session))
        self.session = session.value
        for interface, value in (("2c39ae40-7d2e-5044-804e-8b6799d4cf9e", ctypes.c_byte(0)),  # IsCursorCaptureEnabled
                                 ("f2cdd966-22ae-5ea1-9596-3a289344c3be", ctypes.c_byte(0)),  # IsBorderRequired
                                 ("67c0ea62-1f85-5061-925a-239be0ac09cb", c_int64(10_000))):  # MinUpdateInterval 1 ms
            p = query(self.session, interface, required=False)
            if p:
                call(p, 7, ctypes.c_long, value)  # best effort, as in GpuCapture.cs
                release(p)
        desc = TextureDesc(w, h, 1, 1, DXGI_FORMAT_B8G8R8A8_UNORM, 1, 0, 3, 0, 0x20000, 0)  # staging, CPU read
        staging = c_void_p()
        call(self.device, 5, HRESULT, byref(desc), None, byref(staging))
        self.staging = staging.value
        call(self.session, 6, HRESULT)  # StartCapture

    def latest(self) -> tuple[float, np.ndarray] | None:
        """(capture time on the time.perf_counter clock, BGRA crop) of the newest frame since the last
        call, or None. Older queued frames are dropped unread."""
        newest = None
        try:
            while True:
                frame = c_void_p()
                call(self.pool, 7, HRESULT, byref(frame))
                if not frame.value:
                    break
                close(newest)
                newest = frame.value
            if newest is None:
                return None
            content, stamp = SizeInt32(), c_int64()
            call(newest, 8, HRESULT, byref(content))
            if (content.width, content.height) != self.size:
                raise WindowChanged("window size changed")
            call(newest, 7, HRESULT, byref(stamp))  # SystemRelativeTime: QPC in 100 ns units
            surface, texture = c_void_p(), c_void_p()
            call(newest, 6, HRESULT, byref(surface))
            try:
                access = query(surface.value, "a9b3d012-3df2-4ee3-b8d1-8695f457d3c1")
                try:
                    call(access, 3, HRESULT, byref(iid("6f15aaf2-d208-4e89-9ab4-489535d34f9c")), byref(texture))
                finally:
                    release(access)
            finally:
                release(surface.value)
            try:
                x, y, w, h = self.crop
                box = Box(x, y, 0, x + w, y + h, 1)
                call(self.context, 46, None, c_void_p(self.staging), c_uint(0), c_uint(0), c_uint(0), c_uint(0),
                     c_void_p(texture.value), c_uint(0), byref(box))  # CopySubresourceRegion
            finally:
                release(texture.value)
            return stamp.value * 1e-7, self._read()
        finally:
            close(newest)

    def _read(self) -> np.ndarray:
        x, y, w, h = self.crop
        mapped = Mapped()
        started = time.perf_counter()
        while (hr := call(self.context, 14, ctypes.c_long, c_void_p(self.staging), c_uint(0), c_int(1), c_uint(0x100000),
                          byref(mapped))) == DXGI_ERROR_WAS_STILL_DRAWING:  # poll: a blocking Map can spin a core
            if time.perf_counter() - started > 0.5:
                raise TimeoutError("GPU copy did not finish")
            time.sleep(0.0002)
        if hr < 0:
            raise OSError(hr, "Map failed")
        try:
            rows = np.ctypeslib.as_array(ctypes.cast(mapped.data, POINTER(ctypes.c_ubyte)), (h, mapped.row_pitch))
            return rows[:, :w * 4].reshape(h, w, 4).copy()
        finally:
            call(self.context, 15, None, c_void_p(self.staging), c_uint(0))

    def close(self):
        close(self.session)
        close(self.pool)
        release(self.item)
        release(self.staging)
        release(self.context)
        release(self.device)
        self.session = self.pool = self.item = self.staging = self.context = self.device = None


class WindowChanged(RuntimeError):
    pass

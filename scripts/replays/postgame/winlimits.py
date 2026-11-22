"""Bound private worker commit and kill descendants if the Windows supervisor dies."""
import ctypes as c
from ctypes import wintypes as w


def enter_job():
    class Basic(c.Structure):
        _fields_ = [("processTime", c.c_int64), ("jobTime", c.c_int64), ("flags", w.DWORD),
                    ("minWorkingSet", c.c_size_t), ("maxWorkingSet", c.c_size_t),
                    ("activeProcesses", w.DWORD), ("affinity", c.c_size_t),
                    ("priorityClass", w.DWORD), ("schedulingClass", w.DWORD)]

    class Extended(c.Structure):
        _fields_ = [("basic", Basic), ("io", c.c_uint64 * 6), ("processMemory", c.c_size_t),
                    ("jobMemory", c.c_size_t), ("peakProcess", c.c_size_t), ("peakJob", c.c_size_t)]

    k = c.WinDLL("kernel32", use_last_error=True)
    k.CreateJobObjectW.argtypes = [c.c_void_p, w.LPCWSTR]
    k.CreateJobObjectW.restype = w.HANDLE
    k.SetInformationJobObject.argtypes = [w.HANDLE, c.c_int, c.c_void_p, w.DWORD]
    k.AssignProcessToJobObject.argtypes = [w.HANDLE, w.HANDLE]
    limits = Extended()
    limits.basic.flags = 0x2000 | 0x100  # KILL_ON_JOB_CLOSE | PROCESS_MEMORY
    limits.processMemory = 1536 * 1024**2
    handle = k.CreateJobObjectW(None, None)  # non-inheritable handle; children inherit membership only
    if (not handle or not k.SetInformationJobObject(handle, 9, c.byref(limits), c.sizeof(limits))
            or not k.AssignProcessToJobObject(handle, w.HANDLE(-1))):
        raise c.WinError(c.get_last_error())
    # Intentionally owned until process exit: closing it here would terminate the supervisor itself.

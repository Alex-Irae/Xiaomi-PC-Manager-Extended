"""Prefer Windows' installed C++ runtime before importing Qt or the model engine.

Dependencies: standard library. No installation, DLL replacement or PATH changes.
Imported by the desktop entry point and the isolated engine, not run directly.
"""
import ctypes
from ctypes import wintypes
from pathlib import Path
import sys

_runtime_handles = []


def prefer_system_runtime():
    """Keep the system MSVCP140 loaded so Qt's older bundled copy cannot win first."""
    if sys.platform != "win32" or _runtime_handles:
        return
    kernel = ctypes.WinDLL("kernel32", use_last_error=True)
    kernel.GetSystemDirectoryW.argtypes = [wintypes.LPWSTR, wintypes.UINT]
    kernel.GetSystemDirectoryW.restype = wintypes.UINT
    buffer = ctypes.create_unicode_buffer(32768)
    length = kernel.GetSystemDirectoryW(buffer, len(buffer))
    if not 0 < length < len(buffer):
        raise ctypes.WinError(ctypes.get_last_error())
    path = Path(buffer.value)/"msvcp140.dll"
    if path.is_file():
        # LOAD_LIBRARY_SEARCH_SYSTEM32 restricts dependent DLL lookup to Windows.
        _runtime_handles.append(ctypes.WinDLL(str(path), winmode=0x800))

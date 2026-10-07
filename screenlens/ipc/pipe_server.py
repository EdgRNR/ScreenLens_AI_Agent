# -*- coding: utf-8 -*-
"""命名管道服务端（纯 ctypes 实现，不引入 pywin32）。

安全：管道 DACL 只允许【当前用户】与 SYSTEM 完全访问，
其他同机用户无法连接；不监听任何 TCP 端口。

线程模型：
- serve_forever() 阻塞在 ConnectNamedPipe 上等待客户端；
- 每个连接由一个独立线程执行 handler(conn)；
- stop() 通过自连一次管道解除 accept 阻塞并退出循环。
"""
from __future__ import annotations

import ctypes
import ctypes.wintypes as wt
import logging
import threading
import time

from screenlens.ipc.protocol import ProtocolError

logger = logging.getLogger(__name__)

kern = ctypes.WinDLL("kernel32", use_last_error=True)
adv = ctypes.WinDLL("advapi32", use_last_error=True)

# 64 位 HANDLE 必须显式声明返回类型，否则 ctypes 默认 c_int 截断
kern.GetCurrentProcess.restype = ctypes.c_void_p
kern.GetCurrentProcess.argtypes = []

adv.OpenProcessToken.argtypes = [ctypes.c_void_p, ctypes.c_uint32,
                                 ctypes.POINTER(wt.HANDLE)]
adv.OpenProcessToken.restype = ctypes.c_int
adv.GetTokenInformation.argtypes = [wt.HANDLE, ctypes.c_uint32,
                                    ctypes.c_void_p, ctypes.c_uint32,
                                    ctypes.POINTER(wt.DWORD)]
adv.GetTokenInformation.restype = ctypes.c_int
adv.ConvertSidToStringSidW.argtypes = [ctypes.c_void_p,
                                       ctypes.POINTER(ctypes.c_wchar_p)]
adv.ConvertSidToStringSidW.restype = ctypes.c_int
adv.ConvertStringSecurityDescriptorToSecurityDescriptorW.argtypes = [
    ctypes.c_wchar_p, ctypes.c_uint32,
    ctypes.POINTER(ctypes.c_void_p), ctypes.POINTER(wt.DWORD)]
adv.ConvertStringSecurityDescriptorToSecurityDescriptorW.restype = ctypes.c_int

# CreateNamedPipeW 常量
PIPE_ACCESS_DUPLEX = 0x00000003
PIPE_TYPE_BYTE = 0x00000000
PIPE_READMODE_BYTE = 0x00000000
PIPE_WAIT = 0x00000000
PIPE_UNLIMITED_INSTANCES = 255
INVALID_HANDLE = ctypes.c_void_p(-1).value

# CreateFileW 常量（自连解除阻塞用）
GENERIC_READ = 0x80000000
GENERIC_WRITE = 0x40000000
OPEN_EXISTING = 3


class SECURITY_ATTRIBUTES(ctypes.Structure):
    _fields_ = [("nLength", wt.DWORD),
                ("lpSecurityDescriptor", ctypes.c_void_p),
                ("bInheritHandle", wt.BOOL)]


# Win32 API 默认 restype 是 c_int；对 HANDLE 必须显式使用指针宽度，
# 否则 x64 下 CreateNamedPipeW/CreateFileW 的返回值可能被截断。
kern.LocalFree.argtypes = [ctypes.c_void_p]
kern.LocalFree.restype = ctypes.c_void_p
kern.CloseHandle.argtypes = [wt.HANDLE]
kern.CloseHandle.restype = wt.BOOL
kern.CreateNamedPipeW.argtypes = [
    ctypes.c_wchar_p, wt.DWORD, wt.DWORD, wt.DWORD, wt.DWORD, wt.DWORD,
    wt.DWORD, ctypes.POINTER(SECURITY_ATTRIBUTES)]
kern.CreateNamedPipeW.restype = wt.HANDLE
kern.ConnectNamedPipe.argtypes = [wt.HANDLE, ctypes.c_void_p]
kern.ConnectNamedPipe.restype = wt.BOOL
kern.ReadFile.argtypes = [wt.HANDLE, ctypes.c_void_p, wt.DWORD,
                          ctypes.POINTER(wt.DWORD), ctypes.c_void_p]
kern.ReadFile.restype = wt.BOOL
kern.WriteFile.argtypes = [wt.HANDLE, ctypes.c_void_p, wt.DWORD,
                           ctypes.POINTER(wt.DWORD), ctypes.c_void_p]
kern.WriteFile.restype = wt.BOOL
kern.CreateFileW.argtypes = [
    ctypes.c_wchar_p, wt.DWORD, wt.DWORD, ctypes.c_void_p, wt.DWORD,
    wt.DWORD, wt.HANDLE]
kern.CreateFileW.restype = wt.HANDLE


def _current_user_sid() -> str:
    """取当前进程令牌的用户 SID 字符串（S-1-5-21-…）。"""
    token = wt.HANDLE()
    process = kern.GetCurrentProcess()
    if not adv.OpenProcessToken(process, 0x0008,  # TOKEN_QUERY
                                 ctypes.byref(token)):
        raise ctypes.WinError(ctypes.get_last_error())
    try:
        size = wt.DWORD(0)
        adv.GetTokenInformation(token, 1, None, 0, ctypes.byref(size))  # TokenUser
        buf = ctypes.create_string_buffer(size.value)
        if not adv.GetTokenInformation(token, 1, buf, size, ctypes.byref(size)):
            raise ctypes.WinError(ctypes.get_last_error())
        # TOKEN_USER = { SID* UserSid; DWORD Attributes; }（指针对齐）
        sid_ptr = ctypes.cast(buf, ctypes.POINTER(ctypes.c_void_p))[0]
        sid_str = ctypes.c_wchar_p()
        if not adv.ConvertSidToStringSidW(ctypes.c_void_p(sid_ptr),
                                          ctypes.byref(sid_str)):
            raise ctypes.WinError(ctypes.get_last_error())
        try:
            return sid_str.value
        finally:
            kern.LocalFree(ctypes.cast(sid_str, ctypes.c_void_p))
    finally:
        kern.CloseHandle(token)


def _make_security_attributes():
    """DACL：当前用户 + SYSTEM 完全访问，其余拒绝。

    返回 (SECURITY_ATTRIBUTES, sd)；调用方在 CreateNamedPipeW 之后
    需 LocalFree(sd)。
    """
    sddl = f"D:P(A;;GA;;;{_current_user_sid()})(A;;GA;;;SY)"
    sd = ctypes.c_void_p()
    if not adv.ConvertStringSecurityDescriptorToSecurityDescriptorW(
            sddl, 1, ctypes.byref(sd), None):  # SDDL_REVISION_1
        raise ctypes.WinError(ctypes.get_last_error())
    sa = SECURITY_ATTRIBUTES(ctypes.sizeof(SECURITY_ATTRIBUTES), sd, False)
    return sa, sd


class _PipeHandle:
    """单个管道连接实例（客户端↔服务端一对一）。"""

    def __init__(self, raw):
        self._raw = ctypes.c_void_p(raw)

    def read_exact(self, n: int):
        """阻塞读满 n 字节；对端关闭/出错返回 None。"""
        chunks = []
        remaining = n
        while remaining > 0:
            buf = ctypes.create_string_buffer(remaining)
            got = wt.DWORD(0)
            ok = kern.ReadFile(self._raw, ctypes.byref(buf), remaining,
                               ctypes.byref(got), None)
            if not ok or got.value == 0:
                return None
            chunks.append(buf.raw[:got.value])
            remaining -= got.value
        return b"".join(chunks)

    def write_all(self, data: bytes) -> None:
        got = wt.DWORD(0)
        off = 0
        while off < len(data):
            chunk = ctypes.create_string_buffer(data[off:])
            ok = kern.WriteFile(self._raw, ctypes.byref(chunk), len(data) - off,
                                ctypes.byref(got), None)
            if not ok or got.value == 0:
                raise ProtocolError("管道写入失败")
            off += got.value

    def close(self) -> None:
        if self._raw:
            kern.CloseHandle(self._raw)
            self._raw = None


def _close(raw) -> None:
    if raw not in (INVALID_HANDLE, None, 0):
        kern.CloseHandle(ctypes.c_void_p(raw))


class PipeServer:
    """单实例多连接的命名管道服务端。"""

    def __init__(self, name: str, handler, *, on_ready=None):
        self._name = name
        self._handler = handler  # handler(pipe: _PipeHandle)
        self._on_ready = on_ready
        self._stopping = False
        self._lock = threading.Lock()
        self._threads: list[threading.Thread] = []

    # ---------------------------------------------------------- 生命周期

    def serve_forever(self) -> None:
        logger.info("IPC 管道服务启动: %s", self._name)
        while not self._stopping:
            raw = INVALID_HANDLE
            sd = None
            try:
                sa, sd = _make_security_attributes()
                raw = kern.CreateNamedPipeW(
                    self._name,
                    PIPE_ACCESS_DUPLEX,
                    PIPE_TYPE_BYTE | PIPE_READMODE_BYTE | PIPE_WAIT,
                    PIPE_UNLIMITED_INSTANCES,
                    64 * 1024,   # 输出缓冲
                    64 * 1024,   # 输入缓冲
                    0,
                    ctypes.byref(sa))
                if raw in (INVALID_HANDLE, None, 0):
                    raise ctypes.WinError(ctypes.get_last_error())

                if self._on_ready is not None:
                    callback, self._on_ready = self._on_ready, None
                    try:
                        callback()
                    except Exception:
                        logger.exception("后台就绪后的启动操作失败")

                # stop() may run before this instance exists, so its wake-up
                # connection can miss the pipe. Recheck before blocking.
                if self._stopping:
                    _close(raw)
                    raw = INVALID_HANDLE
                    break
                connected = kern.ConnectNamedPipe(ctypes.c_void_p(raw), None)
                last_err = ctypes.get_last_error()
                # ERROR_PIPE_CONNECTED(535)：客户端在 Connect 前已接入
                if not connected and last_err not in (0, 535):
                    _close(raw)
                    raw = INVALID_HANDLE
                    continue
                if self._stopping:
                    _close(raw)
                    break

                pipe = _PipeHandle(raw)
                raw = INVALID_HANDLE
                t = threading.Thread(target=self._serve_connection,
                                     args=(pipe,), daemon=True,
                                     name="ipc-conn")
                with self._lock:
                    self._threads.append(t)
                t.start()
            except OSError as e:
                if self._stopping:
                    _close(raw)
                    break
                logger.error("管道接受连接失败: %s", e)
                _close(raw)
                time.sleep(0.2)  # 异常退避，避免空转
            finally:
                if sd is not None:
                    kern.LocalFree(sd)
        logger.info("IPC 管道服务已停止")

    def _serve_connection(self, pipe: _PipeHandle) -> None:
        try:
            self._handler(pipe)
        except Exception:
            logger.exception("IPC 连接处理异常")
        finally:
            try:
                pipe.close()
            except Exception:
                pass

    def stop(self) -> None:
        """解除 serve_forever 阻塞（自连一次管道）。"""
        self._stopping = True
        try:
            handle = kern.CreateFileW(
                self._name, GENERIC_READ | GENERIC_WRITE, 0, None,
                OPEN_EXISTING, 0, None)
            _close(handle)
        except Exception:
            pass

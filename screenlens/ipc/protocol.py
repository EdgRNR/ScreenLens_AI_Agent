# -*- coding: utf-8 -*-
"""ScreenLens IPC 协议 v1（Python 端实现，C# WinUI 端按同一规范实现）。

传输层
------
- Windows 命名管道（单机、命名空间 ``\\\\.\\pipe\\``），不监听任何 TCP 端口；
- 服务端创建管道时设置仅当前用户 + SYSTEM 可访问的 DACL；
- 长连接：一个连接上顺序执行多个请求-响应，支持并发连接。

帧格式（长度前缀二进制帧）
--------------------------
每个帧 = 4 字节无符号大端长度 N + N 字节 payload，N 上限 32 MiB。

- 控制帧：payload 为 UTF-8 JSON；
- 数据帧：payload 为原始二进制（目前仅 RecognizeImage 的 PNG 截图）。

请求（客户端 → 服务端）::

    {"v": 1, "id": <int>, "op": "<op>", "data": {...}}

    RecognizeImage 请求的控制帧之后必须紧跟一帧 PNG 二进制。

响应（服务端 → 客户端）::

    成功: {"v": 1, "id": <同请求>, "ok": true,  "data": {...}}
    失败: {"v": 1, "id": <同请求>, "ok": false,
           "error": {"code": "<错误码>", "message": "<可读信息>"}}

操作（op）
---------
- Ping            -> {}                       连通性探测
- GetStatus       -> {pid, hotkey, provider, worker_busy, frontend_pids}
- GetSettings     -> {config, path}            config 含 api_key（仅本用户管道可达）
- SaveSettings    -> {}                         校验失败回 config_invalid
- RegisterHotkey  -> {}                         冲突回 hotkey_conflict（保留原热键）
- RecognizeImage  -> {text, lines:[{text,score,box}], elapsed_ms}
- TranslateText   -> {text}
- CancelRequest   -> {}                         取消进行中的识别/翻译
- Shutdown        -> {}                         停止代理（托盘退出等）

错误码
------
bad_request / unsupported_version / unknown_op / busy / timeout / cancelled /
hotkey_conflict / config_invalid / image_too_large / ocr_failed /
translate_failed / provider_not_configured / worker_crashed / internal
"""
from __future__ import annotations

import json
import struct

PROTOCOL_VERSION = 1
PIPE_NAME = r"\\.\pipe\ScreenLensAgent"
MAX_FRAME = 32 * 1024 * 1024  # 32 MiB，覆盖 4K 全屏 PNG


class ProtocolError(Exception):
    """帧编解码错误（超限、截断、非法 JSON）。"""


# ---------------------------------------------------------------- 帧编解码

def encode_frame(payload: bytes) -> bytes:
    """payload（JSON 或二进制）→ 完整帧字节。"""
    if not isinstance(payload, (bytes, bytearray)):
        raise ProtocolError("帧 payload 必须是 bytes")
    if len(payload) > MAX_FRAME:
        raise ProtocolError(f"帧超过上限 {MAX_FRAME} 字节")
    return struct.pack(">I", len(payload)) + bytes(payload)


def read_frame(read_exact) -> bytes | None:
    """从 read_exact(n) 读一帧；对端关闭返回 None。

    read_exact 必须阻塞读满 n 字节或返回不足（EOF）。
    """
    header = read_exact(4)
    if header is None or len(header) == 0:
        return None
    if len(header) < 4:
        raise ProtocolError("帧头截断")
    (n,) = struct.unpack(">I", header)
    if n == 0:
        raise ProtocolError("空帧非法")
    if n > MAX_FRAME:
        raise ProtocolError(f"帧超过上限 {MAX_FRAME} 字节")
    payload = read_exact(n)
    if payload is None or len(payload) < n:
        raise ProtocolError("帧体截断")
    return payload


def read_json_frame(read_exact) -> dict | None:
    frame = read_frame(read_exact)
    if frame is None:
        return None
    try:
        obj = json.loads(frame.decode("utf-8"))
    except (UnicodeDecodeError, json.JSONDecodeError) as e:
        # 调试用：附带原始字节以便排查帧对齐问题
        import reprlib
        head = reprlib.repr(bytes(frame[:64]))
        raise ProtocolError(f"控制帧不是合法 JSON: {e} (head={head})") from e
    if not isinstance(obj, dict):
        raise ProtocolError("控制帧必须是 JSON 对象")
    return obj


# ------------------------------------------------------------ 消息构造

def make_request(req_id: int, op: str, data: dict | None = None) -> bytes:
    return encode_frame(json.dumps(
        {"v": PROTOCOL_VERSION, "id": req_id, "op": op, "data": data or {}},
        ensure_ascii=False).encode("utf-8"))


def make_response(req_id: int, *, data: dict | None = None,
                  error_code: str | None = None,
                  message: str | None = None) -> bytes:
    resp: dict = {"v": PROTOCOL_VERSION, "id": req_id, "ok": error_code is None}
    if error_code is not None:
        resp["error"] = {"code": error_code,
                         "message": message or error_code}
    else:
        resp["data"] = data or {}
    return encode_frame(json.dumps(resp, ensure_ascii=False).encode("utf-8"))


# 常量集合（供两端引用）
OPS = (
    "Ping", "GetStatus", "GetSettings", "SaveSettings", "RegisterHotkey",
    "RecognizeImage", "TranslateText", "CancelRequest", "Shutdown",
)

ERROR_CODES = (
    "bad_request", "unsupported_version", "unknown_op", "busy", "timeout",
    "cancelled", "hotkey_conflict", "config_invalid", "image_too_large",
    "ocr_failed", "translate_failed", "provider_not_configured",
    "worker_crashed", "internal",
)

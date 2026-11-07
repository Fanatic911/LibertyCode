"""Stores the API key encrypted with a key derived from the machine's ID, so the file is useless on any other
computer. Windows: BIOS UUID / board serial. macOS: IOPlatformUUID. Linux: /etc/machine-id.
Everything is silent: nothing is ever printed about it.

Same file format as the original C# version ("LC2" + salt + nonce + tag + ciphertext, AES-256-GCM, PBKDF2-SHA256),
so key files written by one version are read by the other. Old DPAPI files are migrated on the spot."""
from __future__ import annotations

import hashlib
import os
import re
import secrets
import shutil
import subprocess
import sys
from typing import Optional

from . import paths

_ENTROPY = b"LibertyCode-2024-Salt"
_MAGIC = b"LC2"


def clean_key(key) -> str:
    """Keeps only printable ASCII without spaces: anything else makes HTTP headers fail."""
    return "".join(c for c in (key or "") if "!" <= c <= "~")


def _key_file(provider: str) -> str:
    return os.path.join(paths.DATA_DIR, f".env.{provider}.enc")


def migrate_legacy(provider: str) -> None:
    """Old versions stored one key for all providers: it belongs to the provider configured then."""
    try:
        if not provider.strip() or not os.path.exists(paths.ENV_FILE) or os.path.exists(_key_file(provider)):
            return
        shutil.move(paths.ENV_FILE, _key_file(provider))
    except Exception:
        pass


# ── Hardware ID ───────────────────────────────────────────────────────────────
def _junk(v: Optional[str]) -> bool:
    l = (v or "").strip().lower()
    return (not l or not l.strip("0-f") or "o.e.m" in l or "default string" in l
            or l in ("none", "n/a", "unknown", "not applicable", "system serial number", "uninitialized"))


def _wmi(cls: str, prop: str) -> Optional[str]:
    try:
        out = subprocess.run(
            ["powershell.exe", "-NoProfile", "-NonInteractive", "-Command", f"(Get-CimInstance {cls}).{prop}"],
            capture_output=True, text=True, timeout=20, creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0),
            stdin=subprocess.DEVNULL)
        for l in out.stdout.splitlines():
            if not _junk(l):
                return l.strip()
    except Exception:
        pass
    return None


def _macos_uuid() -> Optional[str]:
    """The hardware UUID of the Mac (stable across reboots and OS reinstalls)."""
    for exe in ("/usr/sbin/ioreg", "ioreg"):
        try:
            out = subprocess.run([exe, "-rd1", "-c", "IOPlatformExpertDevice"], capture_output=True, text=True,
                                 timeout=10, stdin=subprocess.DEVNULL).stdout
            m = re.search(r'"IOPlatformUUID"\s*=\s*"([^"]+)"', out)
            if m and not _junk(m.group(1)):
                return m.group(1).strip()
        except Exception:
            continue
    return None


def _linux_machine_id() -> Optional[str]:
    for f in ("/etc/machine-id", "/var/lib/dbus/machine-id", "/sys/class/dmi/id/product_uuid"):
        try:
            with open(f, "r", encoding="utf-8") as fh:
                v = fh.read().strip()
            if not _junk(v):
                return v
        except Exception:
            continue
    return None


def _install_id() -> str:
    """Last resort (containers, minimal systems without a machine id): a random id created once and kept in the
    data folder (private to the user). It does not tie the key file to the machine, but the key stays encrypted."""
    path = os.path.join(paths.DATA_DIR, ".install-id")
    try:
        with open(path, "r", encoding="utf-8") as f:
            v = f.read().strip()
        if v:
            return v
    except Exception:
        pass
    try:
        v = secrets.token_hex(16)
        os.makedirs(paths.DATA_DIR, exist_ok=True)
        fd = os.open(path, os.O_WRONLY | os.O_CREAT | os.O_TRUNC, 0o600)
        with os.fdopen(fd, "w", encoding="utf-8") as f:
            f.write(v)
        return v
    except Exception:
        return ""


_hwid_cache: Optional[str] = None


def _hwid() -> str:
    global _hwid_cache
    if _hwid_cache is not None:
        return _hwid_cache
    ident: Optional[str] = None
    try:
        if os.name == "nt":
            ident = (_wmi("Win32_ComputerSystemProduct", "UUID") or _wmi("Win32_BaseBoard", "SerialNumber")
                     or _wmi("Win32_BIOS", "SerialNumber"))
            if not ident:
                import winreg
                with winreg.OpenKey(winreg.HKEY_LOCAL_MACHINE, r"SOFTWARE\Microsoft\Cryptography",
                                    0, winreg.KEY_READ | winreg.KEY_WOW64_64KEY) as k:
                    ident = str(winreg.QueryValueEx(k, "MachineGuid")[0])
        elif sys.platform == "darwin":
            ident = _macos_uuid()
        else:
            ident = _linux_machine_id()
    except Exception:
        pass
    if not ident and os.name != "nt":
        ident = _install_id()
    _hwid_cache = ident or ""
    return _hwid_cache


def _derive_key(salt: bytes) -> bytes:
    return hashlib.pbkdf2_hmac("sha256", _hwid().encode("utf-8"), salt + _ENTROPY, 100_000, 32)


def _encrypt(plain: bytes) -> bytes:
    from cryptography.hazmat.primitives.ciphers.aead import AESGCM
    salt, nonce = os.urandom(16), os.urandom(12)
    ct_tag = AESGCM(_derive_key(salt)).encrypt(nonce, plain, _MAGIC)   # ciphertext || tag(16)
    cipher, tag = ct_tag[:-16], ct_tag[-16:]
    return _MAGIC + salt + nonce + tag + cipher


def _decrypt(data: bytes) -> Optional[bytes]:
    """None if the data is not in the new format. Raises if it is but cannot be decrypted (other machine, damaged)."""
    if len(data) < 3 + 16 + 12 + 16 or data[:3] != _MAGIC:
        return None
    from cryptography.hazmat.primitives.ciphers.aead import AESGCM
    salt, nonce, tag, cipher = data[3:19], data[19:31], data[31:47], data[47:]
    return AESGCM(_derive_key(salt)).decrypt(nonce, cipher + tag, _MAGIC)


# ── Old format: Windows DPAPI through ctypes ──────────────────────────────────
def _dpapi_unprotect(data: bytes) -> bytes:
    import ctypes
    from ctypes import wintypes

    class DATA_BLOB(ctypes.Structure):
        _fields_ = [("cbData", wintypes.DWORD), ("pbData", ctypes.POINTER(ctypes.c_char))]

    def blob(b: bytes):
        buf = ctypes.create_string_buffer(b, len(b))
        return DATA_BLOB(len(b), ctypes.cast(buf, ctypes.POINTER(ctypes.c_char))), buf

    b_in, k1 = blob(data)
    b_ent, k2 = blob(_ENTROPY)
    b_out = DATA_BLOB()
    if not ctypes.windll.crypt32.CryptUnprotectData(ctypes.byref(b_in), None, ctypes.byref(b_ent), None, None, 0,
                                                    ctypes.byref(b_out)):
        raise OSError("DPAPI call failed")
    try:
        return ctypes.string_at(b_out.pbData, b_out.cbData)
    finally:
        ctypes.windll.kernel32.LocalFree(b_out.pbData)
        del k1, k2


def save_api_key(key: str, provider: str) -> None:
    os.makedirs(paths.DATA_DIR, exist_ok=True)
    path = _key_file(provider)
    data = _encrypt(clean_key(key).encode("utf-8"))
    if os.name == "nt":
        with open(path, "wb") as f:
            f.write(data)
        return
    # POSIX: created private from the start (0600), never readable by other users, even for a moment.
    fd = os.open(path, os.O_WRONLY | os.O_CREAT | os.O_TRUNC, 0o600)
    with os.fdopen(fd, "wb") as f:
        f.write(data)
    try:
        os.chmod(path, 0o600)   # also fixes a file that already existed with looser rights
    except Exception:
        pass


def load_api_key(provider: str) -> Optional[str]:
    """Decrypts silently. A file from another machine, a damaged file or an old-format file (migrated on the
    spot) never prints anything."""
    path = _key_file(provider)
    if not os.path.exists(path):
        return None
    try:
        with open(path, "rb") as f:
            data = f.read()
        plain = _decrypt(data)
        if plain is None:   # old format (DPAPI on Windows, plain text elsewhere): read it, then re-encrypt
            plain = _dpapi_unprotect(data) if os.name == "nt" else data
            try:
                with open(path, "wb") as f:
                    f.write(_encrypt(plain))
                if os.name != "nt":
                    os.chmod(path, 0o600)
            except Exception:
                pass
        key = clean_key(plain.decode("utf-8", errors="ignore"))
        return key or None
    except Exception:
        return None

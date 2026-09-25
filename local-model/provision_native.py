"""Install the pinned llama.cpp Windows CUDA runtime. Runtime never calls this."""

from __future__ import annotations

import hashlib
import json
import os
from pathlib import Path, PurePosixPath
import shutil
import subprocess
import urllib.request
import uuid
from zipfile import ZipFile


ROOT = Path(__file__).resolve().parent.parent
CACHE = ROOT / ".cache" / "native-llama-server"
TOOLS = ROOT / ".tools"
DESTINATION = TOOLS / "llama-server"
ATTRIBUTION = ROOT / "local-model" / "attribution"
RELEASE = "b11146"
COMMIT = "7fe450e19305b828c199d602c23a8337aaa1f03b"
EXPECTED_VERSION = ("11146", "7fe450e")
ASSETS = (
    {
        "name": "llama-b11146-bin-win-cuda-12.4-x64.zip",
        "url": "https://github.com/ggml-org/llama.cpp/releases/download/b11146/llama-b11146-bin-win-cuda-12.4-x64.zip",
        "bytes": 253_869_799,
        "sha256": "3c806a6ceccc3dae1c743ceb1a1fb2cce5b76f40bfbd4c6b7b8afb6ef45a5807",
    },
    {
        "name": "cudart-llama-bin-win-cuda-12.4-x64.zip",
        "url": "https://github.com/ggml-org/llama.cpp/releases/download/b11146/cudart-llama-bin-win-cuda-12.4-x64.zip",
        "bytes": 391_443_627,
        "sha256": "8c79a9b226de4b3cacfd1f83d24f962d0773be79f1e7b75c6af4ded7e32ae1d6",
    },
)
REQUIRED_FILES = {
    "llama-server.exe": 9_216,
    "llama-server-impl.dll": 8_916_992,
    "ggml-cuda.dll": 545_604_608,
    "cublas64_12.dll": 100_033_536,
    "cublasLt64_12.dll": 473_551_360,
    "cudart64_12.dll": 553_984,
}


def sha256(path: Path) -> str:
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def valid_asset(path: Path, asset: dict[str, object]) -> bool:
    return (
        path.is_file()
        and path.stat().st_size == asset["bytes"]
        and sha256(path) == asset["sha256"]
    )


def acquire(asset: dict[str, object]) -> Path:
    CACHE.mkdir(parents=True, exist_ok=True)
    path = CACHE / str(asset["name"])
    if valid_asset(path, asset):
        return path
    path.unlink(missing_ok=True)
    partial = path.with_suffix(path.suffix + ".part")
    partial.unlink(missing_ok=True)
    request = urllib.request.Request(str(asset["url"]), headers={"User-Agent": "Translumo-setup"})
    try:
        with urllib.request.urlopen(request) as response, partial.open("wb") as output:
            shutil.copyfileobj(response, output, 1024 * 1024)
        if not valid_asset(partial, asset):
            raise RuntimeError(f"Downloaded runtime asset failed size or SHA256 verification: {asset['name']}")
        os.replace(partial, path)
        return path
    finally:
        partial.unlink(missing_ok=True)


def selected_member(name: str) -> bool:
    lower = name.lower()
    return lower.endswith(".dll") or lower == "llama-server.exe" or lower == "license-llvm-openmp"


def extract_selected(archive: Path, staging: Path) -> None:
    with ZipFile(archive) as bundle:
        for item in bundle.infolist():
            member = PurePosixPath(item.filename.replace("\\", "/"))
            if item.is_dir() or not selected_member(member.name):
                continue
            if member.is_absolute() or ".." in member.parts or len(member.parts) != 1:
                raise RuntimeError(f"Unsafe runtime archive member: {item.filename}")
            target = staging / member.name
            with bundle.open(item) as source, target.open("wb") as output:
                shutil.copyfileobj(source, output, 1024 * 1024)


def verify_files(directory: Path) -> None:
    for name, size in REQUIRED_FILES.items():
        path = directory / name
        if not path.is_file() or path.stat().st_size != size:
            raise RuntimeError(f"Native runtime is missing its verified {name} component.")


def verify_version(directory: Path) -> str:
    environment = os.environ.copy()
    environment["PATH"] = str(directory) + os.pathsep + environment.get("PATH", "")
    result = subprocess.run(
        [directory / "llama-server.exe", "--version"],
        cwd=directory,
        env=environment,
        capture_output=True,
        text=True,
        timeout=20,
        check=False,
        creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0),
    )
    output = (result.stdout + "\n" + result.stderr).strip()
    if result.returncode != 0 or any(value not in output for value in EXPECTED_VERSION):
        raise RuntimeError(f"Unexpected llama-server version ({result.returncode}): {output[:1000]}")
    return next(line.strip() for line in output.splitlines() if "build 11146" in line)


def installed_server_is_running() -> bool:
    """Return whether this exact installed executable is an active process."""
    if os.name != "nt" or not (DESTINATION / "llama-server.exe").is_file():
        return False

    import ctypes
    from ctypes import wintypes

    kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
    psapi = ctypes.WinDLL("psapi", use_last_error=True)
    psapi.EnumProcesses.argtypes = (
        ctypes.POINTER(wintypes.DWORD),
        wintypes.DWORD,
        ctypes.POINTER(wintypes.DWORD),
    )
    psapi.EnumProcesses.restype = wintypes.BOOL
    kernel32.OpenProcess.argtypes = (wintypes.DWORD, wintypes.BOOL, wintypes.DWORD)
    kernel32.OpenProcess.restype = wintypes.HANDLE
    kernel32.QueryFullProcessImageNameW.argtypes = (
        wintypes.HANDLE,
        wintypes.DWORD,
        wintypes.LPWSTR,
        ctypes.POINTER(wintypes.DWORD),
    )
    kernel32.QueryFullProcessImageNameW.restype = wintypes.BOOL
    kernel32.CloseHandle.argtypes = (wintypes.HANDLE,)
    kernel32.CloseHandle.restype = wintypes.BOOL

    process_ids = (wintypes.DWORD * 4096)()
    bytes_returned = wintypes.DWORD()
    if not psapi.EnumProcesses(
        process_ids, ctypes.sizeof(process_ids), ctypes.byref(bytes_returned)
    ):
        raise OSError("Could not enumerate processes before replacing the native runtime.")

    expected = os.path.normcase(str((DESTINATION / "llama-server.exe").resolve()))
    process_count = bytes_returned.value // ctypes.sizeof(wintypes.DWORD)
    query_limited_information = 0x1000
    for process_id in process_ids[:process_count]:
        if process_id == 0:
            continue
        handle = kernel32.OpenProcess(query_limited_information, False, process_id)
        if not handle:
            continue
        try:
            capacity = wintypes.DWORD(32_768)
            executable = ctypes.create_unicode_buffer(capacity.value)
            if kernel32.QueryFullProcessImageNameW(
                handle, 0, executable, ctypes.byref(capacity)
            ) and os.path.normcase(executable.value) == expected:
                return True
        finally:
            kernel32.CloseHandle(handle)
    return False


def already_installed() -> bool:
    manifest = DESTINATION / "provisioned.json"
    if not manifest.is_file():
        return False
    try:
        data = json.loads(manifest.read_text(encoding="utf-8"))
        if data.get("release") != RELEASE or data.get("commit") != COMMIT:
            return False
        verify_files(DESTINATION)
        return True
    except (OSError, ValueError, RuntimeError):
        return False


def remove_staging(path: Path) -> None:
    if path.parent != TOOLS or not path.name.startswith(".llama-server-"):
        raise RuntimeError(f"Refusing to remove unexpected path: {path}")
    shutil.rmtree(path, ignore_errors=True)


def install(staging: Path) -> None:
    backup = TOOLS / f".llama-server-old-{uuid.uuid4().hex}"
    moved_existing = False
    try:
        if DESTINATION.exists():
            DESTINATION.rename(backup)
            moved_existing = True
        staging.rename(DESTINATION)
    except OSError as error:
        if moved_existing and backup.exists() and not DESTINATION.exists():
            backup.rename(DESTINATION)
        raise RuntimeError(
            "Could not replace the native runtime. Stop Translumo and any llama-server process, then rerun setup."
        ) from error
    if backup.exists():
        remove_staging(backup)


def main() -> None:
    if already_installed():
        version = verify_version(DESTINATION)
        print(f"Verified native translation runtime: {DESTINATION} ({version})")
        return

    if installed_server_is_running():
        raise RuntimeError(
            "Stop Translumo and its llama-server child before replacing the native runtime."
        )

    archives = [acquire(asset) for asset in ASSETS]
    TOOLS.mkdir(parents=True, exist_ok=True)
    staging = TOOLS / f".llama-server-stage-{uuid.uuid4().hex}"
    staging.mkdir()
    try:
        for archive in archives:
            extract_selected(archive, staging)
        shutil.copyfile(ATTRIBUTION / "LICENSE-LLAMA.CPP-MIT.txt", staging / "LICENSE-LLAMA.CPP-MIT.txt")
        shutil.copyfile(ATTRIBUTION / "NATIVE-RUNTIME.md", staging / "NATIVE-RUNTIME.md")
        verify_files(staging)
        version = verify_version(staging)
        (staging / "provisioned.json").write_text(
            json.dumps(
                {
                    "runtime": "llama.cpp",
                    "release": RELEASE,
                    "commit": COMMIT,
                    "version": version,
                    "platform": "windows-x64-cuda-12.4",
                    "assets": list(ASSETS),
                },
                indent=2,
            ),
            encoding="utf-8",
        )
        install(staging)
    finally:
        if staging.exists():
            remove_staging(staging)
    print(f"Installed native translation runtime: {DESTINATION} ({version})")


if __name__ == "__main__":
    main()

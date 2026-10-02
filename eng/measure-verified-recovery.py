"""Disposable real-CLI trials; sampled process memory and independent SHA-256."""
import argparse
import ctypes
from ctypes import wintypes
from datetime import datetime, timezone
import hashlib
import json
import os
from pathlib import Path
import random
import re
import shutil
import subprocess
import tempfile
import time
import uuid

class ProcessMemory(ctypes.Structure):
    _fields_ = [("cb", wintypes.DWORD), ("PageFaultCount", wintypes.DWORD)] + [(name, ctypes.c_size_t) for name in
        ("PeakWorkingSetSize", "WorkingSetSize", "QuotaPeakPagedPoolUsage", "QuotaPagedPoolUsage", "QuotaPeakNonPagedPoolUsage",
         "QuotaNonPagedPoolUsage", "PagefileUsage", "PeakPagefileUsage", "PrivateUsage")]

get_memory = ctypes.WinDLL("psapi", use_last_error=True).GetProcessMemoryInfo
get_memory.argtypes = [wintypes.HANDLE, ctypes.c_void_p, wintypes.DWORD]
get_memory.restype = wintypes.BOOL


def digest(path):
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def run(cli, args):
    started = time.monotonic()
    process = subprocess.Popen(["dotnet", str(cli), *args], stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                               creationflags=subprocess.CREATE_NO_WINDOW)
    private_peak = working_peak = samples = 0
    try:
        while process.poll() is None:
            memory = ProcessMemory()
            memory.cb = ctypes.sizeof(memory)
            if get_memory(int(process._handle), ctypes.byref(memory), memory.cb):
                private_peak = max(private_peak, memory.PrivateUsage)
                working_peak = max(working_peak, memory.PeakWorkingSetSize)
                samples += 1
            time.sleep(0.01)
        output, error = process.communicate()
    finally:
        if process.poll() is None:
            process.terminate()
            try:
                process.wait(timeout=5)
            except subprocess.TimeoutExpired:
                process.kill()
                process.wait(timeout=5)
        for pipe in (process.stdout, process.stderr):
            if pipe is not None:
                pipe.close()
    elapsed = time.monotonic() - started
    if process.returncode != 0:
        raise RuntimeError(f"CLI exit {process.returncode}: {error.decode(errors='replace')}")
    return dict(seconds=round(elapsed, 4), sampled_peak_private_bytes=private_peak,
                observed_peak_working_set_bytes=working_peak, samples=samples,
                output=output.decode().strip(), warnings=error.decode().strip())


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--cli", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--sizes", default="64,256,1024")
    args = parser.parse_args()
    cli = args.cli.resolve(strict=True)
    parent = Path(tempfile.gettempdir()) / "FluxVault.Integrity.Bench"
    root = parent / uuid.uuid4().hex
    root.mkdir(parents=True)
    binary_hashes = {name: digest(cli.parent / name) for name in
                     ("FluxVault.Cli.dll", "FluxVault.Core.dll", "FluxVault.Abstractions.dll")}
    report = dict(started_utc=datetime.now(timezone.utc).isoformat(), binary_sha256=binary_hashes,
                  runtime=subprocess.check_output(["dotnet", "--version"], text=True).strip(),
                  interface="child CLI, explicit legacy-file-manifest mode", sample_interval_ms=10,
                  notes="Fresh process per operation; Windows filesystem cache is warm after generation/capture. Random data uses a seeded PRNG with distinct blocks. Compressible data has one distinct header per MiB. No PostgreSQL or professional-app workload claim.", trials=[])
    try:
        for pattern, compression in (("random", "off"), ("compressible", "zstd")):
            for size in map(int, args.sizes.split(",")):
                case = root / f"{pattern}-{size}"
                case.mkdir()
                source = case / "source.bin"
                random_generator = random.Random(1001)
                source_hash = hashlib.sha256()
                with source.open("wb") as stream:
                    for index in range(size):
                        if pattern == "random":
                            block = random_generator.randbytes(1024 * 1024)
                        else:
                            header = f"FluxVault synthetic working file block {index:08d}\n".encode()
                            block = header + (b"Synthetic project content. " * 50000)[:1024 * 1024 - len(header)]
                        stream.write(block)
                        source_hash.update(block)
                repository = case / "repository"
                common = ["--repository", str(repository), "--legacy-file-manifest"]
                capture = run(cli, ["backup", "--source", str(source), "--compression", compression, *common])
                recapture = run(cli, ["backup", "--source", str(source), "--compression", compression, *common])
                if "New chunks: 0" not in recapture["output"]:
                    raise AssertionError("Unchanged recapture unexpectedly published new objects")
                version = re.search(r"Version: ([a-fA-F0-9]{32})", capture["output"]).group(1)
                restored = case / "restored.bin"
                restore = run(cli, ["restore", "--version", version, "--output", str(restored), *common])
                matches = digest(restored) == source_hash.hexdigest()
                if not matches or restored.stat().st_size != source.stat().st_size:
                    raise AssertionError("Independent restore hash/length mismatch")
                trial = dict(pattern=pattern, compression=compression, mib=size, bytes=source.stat().st_size,
                             capture=capture, unchanged_recapture=recapture, restore=restore, independent_sha256_match=matches)
                report["trials"].append(trial)
                args.output.parent.mkdir(parents=True, exist_ok=True)
                args.output.write_text(json.dumps(report, indent=2), encoding="utf-8")
                print(json.dumps(dict(pattern=pattern, mib=size, capture=capture["seconds"], recapture=recapture["seconds"],
                                      restore=restore["seconds"], restore_private_mib=round(restore["sampled_peak_private_bytes"] / 1024**2, 2), verified=matches)), flush=True)
                resolved = case.resolve()
                if resolved.parent != root.resolve() or not re.fullmatch(r"(random|compressible)-[0-9]+", resolved.name):
                    raise AssertionError("Refusing unexpected cleanup path")
                shutil.rmtree(resolved)
        report["completed_utc"] = datetime.now(timezone.utc).isoformat()
        args.output.write_text(json.dumps(report, indent=2), encoding="utf-8")
    finally:
        # Remove only the freshly generated, checked GUID directory when empty. Preserve failures.
        if root.resolve().parent == parent.resolve() and re.fullmatch(r"[0-9a-f]{32}", root.name) and not any(root.iterdir()):
            root.rmdir()

if __name__ == "__main__":
    main()

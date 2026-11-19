"""Start a temporary service, compress test files, restart, then verify SHA-256."""
import argparse
from contextlib import contextmanager
from hashlib import sha256
from http.client import HTTPConnection
import json
import os
from pathlib import Path
import socket
import subprocess
import tempfile
import time
import struct
import zlib
import io
import zipfile

BASE = Path(__file__).resolve().parents[1]


@contextmanager
def service(dll):
    with socket.socket() as sock:
        sock.bind(("127.0.0.1", 0))
        port = sock.getsockname()[1]
    process = subprocess.Popen(
        ["dotnet", str(dll), "--urls", f"http://127.0.0.1:{port}"],
        stdout=subprocess.DEVNULL,
    )
    try:
        for _ in range(100):
            try:
                status, body = request(port, "GET", "/v1/health")
                if status == 200 and json.loads(body) == {"status": "ok"}:
                    break
            except OSError:
                pass
            if process.poll() is not None:
                raise RuntimeError("API stopped before startup")
            time.sleep(.1)
        else:
            raise RuntimeError("API startup timed out")
        yield port
    finally:
        process.terminate()
        process.wait(timeout=10)


def request(port, method, path, body=None, chunked=False):
    conn = HTTPConnection("127.0.0.1", port, timeout=210)
    try:
        conn.request(method, path, body=body, headers={"Content-Type": "application/octet-stream"},
                     encode_chunked=chunked)
        response = conn.getresponse()
        return response.status, response.read()
    finally:
        conn.close()


def verify(dll, large):
    samples = {
        "empty": b"",
        "single": b"A",
        "zeros": bytes(500000),
        "text": ("Складка. Архив без потерь. " * 4096).encode(),
        "binary": bytes(range(256)) * 1400,
        "random": os.urandom(262144),
        "mixed": bytes(262144) + os.urandom(262144) + b"tail",
    }
    if large:
        samples["128MiB"] = os.urandom(128 * 1024 * 1024)
    with tempfile.TemporaryDirectory() as tmp:
        with service(dll) as port:
            for name, source in samples.items():
                start = time.monotonic()
                code, archive = request(port, "POST", "/v1/compress", source)
                assert code == 200 and len(archive) <= 2 * len(source) + 1024, name
                assert archive.startswith(b"SKLD\x03")
                (Path(tmp) / name).write_bytes(archive)
                print(f"{name}: {len(source)} -> {len(archive)} bytes, {time.monotonic()-start:.2f}s", flush=True)
            status, archive = request(port, "POST", "/v1/compress", iter([b"chunk", b"ed"]), True)
            assert status == 200
        with service(dll) as port:
            assert request(port, "POST", "/v1/decompress", archive) == (200, b"chunked")
            # Independent fixtures exercise legacy framing and v2's transformed streams.
            def fixture(version, raw, records):
                return b"SKLD" + bytes([version]) + struct.pack("<qH" if version >= 4 else "<IH", len(raw), 0) + records + sha256(raw).digest()

            legacy = fixture(1, b"x", struct.pack("<BI", 0, 1) + b"x")
            assert request(port, "POST", "/v1/decompress", legacy)[0] == 400
            record = struct.pack("<IBI",1,0,1) + b"x"
            assert request(port,"POST","/v1/decompress",fixture(2,b"x",record))[0] == 400
            assert request(port,"POST","/v1/decompress",fixture(4,b"x",record)) == (200,b"x")
            def entry(path, contents=None):
                encoded = path.encode()
                header = struct.pack("<BH", int(contents is not None), len(encoded)) + encoded
                return header if contents is None else header + struct.pack("<q",len(contents)) + contents
            def folder_request(data):
                stored = struct.pack("<IBI",len(data),0,len(data)) + data
                return request(port,"POST","/v1/decompress",fixture(5,data,stored))
            root = entry("Проект")
            status, result = folder_request(root + entry("Проект/Пусто") + entry("Проект/данные.txt",b"test") + entry("Проект/zero",b""))
            assert status == 200
            with zipfile.ZipFile(io.BytesIO(result)) as tree:
                assert tree.testzip() is None
                assert tree.getinfo("Проект/Пусто/").is_dir()
                assert tree.read("Проект/данные.txt") == b"test" and tree.read("Проект/zero") == b""
            for bad in (root+root, root+entry("Проект/../x",b"test"),root+entry("Другой/x"),root+entry("Проект/missing/x"),root+entry("Проект/CON")):
                assert folder_request(bad)[0] == 400
            for length in (-1, 2**32+5):
                broken = b"SKLD\x04" + struct.pack("<qH",length,0) + record
                assert request(port,"POST","/v1/decompress",broken)[0] == 400
            def deflation(data):
                encoder = zlib.compressobj(wbits=-15)
                return encoder.compress(data) + encoder.flush()

            # The corrected C must propagate through overlapping distance-two copies.
            raw = b"ABAB" + b"CB"*15
            tokens = bytes([4,65,66,65,2,1,2,2])
            wrapped = struct.pack("<I",len(tokens)) + deflation(tokens)
            for method,payload in [(70,tokens),(71,wrapped)]:
                record = struct.pack("<IBI",len(raw),method,len(payload)) + payload
                assert request(port,"POST","/v1/decompress",fixture(3,raw,record)) == (200,raw)
                assert request(port,"POST","/v1/decompress",fixture(2,raw,record))[0] == 400
            for bad in (bytes([0]), bytes([65,1,0]), tokens[:-1], tokens[:-2]+bytes([32,2]),
                        tokens+b"\x80", bytes([128,128,128,0]), tokens[:-1]+b"\0"):
                record = struct.pack("<IBI",len(raw),70,len(bad)) + bad
                assert request(port,"POST","/v1/decompress",fixture(3,raw,record))[0] == 400
            for claimed in (0, len(tokens)-1, len(raw)+17):
                bad = struct.pack("<I",claimed) + deflation(tokens)
                record = struct.pack("<IBI",len(raw),71,len(bad)) + bad
                assert request(port,"POST","/v1/decompress",fixture(3,raw,record))[0] == 400

            for step in (1, 2, 4, 8):
                raw = bytes(i // step for i in range(120))
                # Residuals start with step zeros, then 1 repeats through the end.
                repeat = len(raw) - step - 1
                tokens = bytes([step]) + bytes(step) + bytes([1, 128 + repeat - 3, 1, 0])
                record = struct.pack("<IBI", len(raw), 32+step, len(tokens)) + tokens
                assert request(port, "POST", "/v1/decompress", fixture(3,raw,record)) == (200,raw)
            raw = bytes(range(256)) * 3
            previous = {}
            errors = bytearray()
            a = b = 0
            for c in raw:
                errors.append((c - previous.get((a,b), 2*b-a)) % 256)
                previous[a,b] = c
                a,b = b,c
            payload = deflation(errors)
            record = struct.pack("<IBI",len(raw),66,len(payload)) + payload
            assert request(port, "POST", "/v1/decompress", fixture(3,raw,record)) == (200,raw)
            raw = bytes(range(71)) * 20 + b"xy"
            shuffled = deflation(b"".join(raw[k::4] for k in range(4)))
            record = struct.pack("<IBI",len(raw),65,len(shuffled)) + shuffled
            assert request(port, "POST", "/v1/decompress", fixture(3,raw,record)) == (200,raw)
            raw = b"A" * 32768 + b"A" * 130
            first = struct.pack("<IBI",32768,1,1) + b"A"
            second = struct.pack("<IBI",130,64,3) + bytes([255,1,0])
            assert request(port, "POST", "/v1/decompress", fixture(3,raw,first+second)) == (200,raw)
            for bad in (struct.pack("<IBI", 0, 0, 0), struct.pack("<IBI", 524289, 1, 1)+b"A",
                        struct.pack("<IBI", 130, 64, 3)+bytes([255,1,0])):
                assert request(port,"POST","/v1/decompress",fixture(3,b"A"*130,bad))[0] == 400
            for name, source in reversed(list(samples.items())):
                packed = (Path(tmp) / name).read_bytes()
                status, restored = request(port, "POST", "/v1/decompress", packed)
                assert status == 200 and sha256(restored).digest() == sha256(source).digest(), name
            for damaged in (b"bad format", packed[:-1], packed + b"!", packed[:-1] + bytes([packed[-1] ^ 1])):
                status, body = request(port, "POST", "/v1/decompress", damaged)
                assert 400 <= status < 500 and json.loads(body)["error"]["code"]
            assert request(port, "GET", "/v1/health")[0] == 200
    print("Fresh-process round trips, chunked input and error responses passed.")


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--large", action="store_true")
    args = parser.parse_args()
    subprocess.run(["dotnet", "build", str(BASE / "api"), "-c", "Release", "-p:NuGetAudit=false"], check=True)
    verify(BASE / "api/bin/Release/net10.0/Skladka.Api.dll", args.large)

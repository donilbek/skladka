from pathlib import Path
from hashlib import sha256
import base64
import struct
import zlib

root = Path(__file__).resolve().parent
demo = "день;категория;значение\n" + "".join(
    f"{i%31+1};{('книги','музыка','кино')[i%3]};{i%12+1}\n" for i in range(1536)
)
raw = demo.encode()
name = "пример.csv".encode()
compressor = zlib.compressobj(wbits=-15)
block = compressor.compress(raw) + compressor.flush()
sample = b"SKLD\x03" + struct.pack("<IH",len(raw),len(name)) + name
sample += struct.pack("<IBI",len(raw),3,len(block)) + block + sha256(raw).digest()
page = (root / "page.html").read_text(encoding="utf-8")
for tag, path in [("STYLE","style.css"),("CODEC","codec.js"),("APP","app.js"),("ART","art.js")]:
    source = (root/path).read_text(encoding="utf-8")
    if tag == "CODEC":
        source = "\n".join((root/f).read_text(encoding="utf-8") for f in ["sha256.js","folders.js"]) + "\n" + source
    if tag == "APP":
        source = (root/"folder-ui.js").read_text(encoding="utf-8") + "\n" + source
    page = page.replace("/*"+tag+"*/", source)
page = page.replace("/*SAMPLE*/",base64.b64encode(sample).decode())
(root/"dist").mkdir(exist_ok=True)
(root/"dist/index.html").write_text(page,encoding="utf-8")

"""
Builds Halcyon for macOS and Linux from any machine that has the .NET SDK (including Windows) and packs each build as a
.tar.gz that keeps the executable permissions those systems need.

    python package.py mac-arm64     Apple Silicon Macs, native
    python package.py mac-x64       Intel Macs (bundles its own VLC engine)
    python package.py linux-x64     Linux on Intel/AMD PCs
    python package.py linux-arm64   Linux on ARM (Raspberry Pi 4/5, ARM laptops)
    python package.py all

Output goes to ..\\dist. Add --skip-publish to repack an existing publish folder.

Playback uses VLC's engine. Both Mac builds carry their own copy: the Intel one from the VideoLAN.LibVLC.Mac NuGet package,
the Apple Silicon one from VideoLAN's arm64 disk image (fetched by fetch_vlc.py). The Linux builds use the system's VLC.
The Mac apps are not signed: on first launch, right-click Halcyon.app and choose Open (or run: xattr -cr Halcyon.app).
"""
import io, os, shutil, struct, subprocess, sys, tarfile, time, zlib

here = os.path.dirname(os.path.abspath(__file__))
root = os.path.dirname(here)
out = os.path.join(root, "dist")
version = "1.0.1"

TARGETS = {
    # Apple Silicon: VideoLAN's own arm64 engine is bundled from ..endor (fetch_vlc.py gets it)
    "mac-arm64": dict(rid="osx-arm64", props=["-p:Mac=true", "-p:BundleVlc=false"], vlc="arm64"),
    "mac-x64": dict(rid="osx-x64", props=["-p:Mac=true"]),
    "linux-x64": dict(rid="linux-x64", props=["-p:Linux=true"]),
    "linux-arm64": dict(rid="linux-arm64", props=["-p:Linux=true"]),
}

# files that only matter on another operating system
SKIP_EXT = (".pdb",)
SKIP_NAMES = {"Avalonia.Win32.dll", "Avalonia.Win32.Automation.dll", "av_libglesv2.dll"}
SKIP_MAC = {"Avalonia.X11.dll", "Avalonia.FreeDesktop.dll"}
SKIP_LINUX = {"Avalonia.Native.dll", "libAvaloniaNative.dylib", "Avalonia.Metal.dll"}


# ---------- icon: the pink Halcyon tile with its ring ----------

def png(size):
    def mix(a, b, t): return a + (b - a) * t
    rows = []
    cx = cy = size / 2
    r_out, r_in = size * 0.30, size * 0.23
    dot_x, dot_y, dot_r = cx + size * 0.21, cy - size * 0.21, size * 0.055
    for y in range(size):
        row = bytearray([0])
        for x in range(size):
            t = (x + y) / (2 * size)
            r, g, b = (int(mix(0xF0, 0xD5, t)), int(mix(0x60, 0x35, t)), int(mix(0x3A, 0x8F, t)))
            d = ((x + 0.5 - cx) ** 2 + (y + 0.5 - cy) ** 2) ** 0.5
            cover = max(0.0, min(1.0, min(d - r_in, r_out - d) + 0.5))
            dd = ((x + 0.5 - dot_x) ** 2 + (y + 0.5 - dot_y) ** 2) ** 0.5
            cover = max(cover, max(0.0, min(1.0, dot_r - dd + 0.5)))
            r, g, b = (int(mix(r, 255, cover)), int(mix(g, 255, cover)), int(mix(b, 255, cover)))
            row += bytes((r, g, b, 255))
        rows.append(bytes(row))
    raw = b"".join(rows)

    def chunk(kind, data):
        body = kind + data
        return struct.pack(">I", len(data)) + body + struct.pack(">I", zlib.crc32(body) & 0xFFFFFFFF)

    return (b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">IIBBBBB", size, size, 8, 6, 0, 0, 0))
            + chunk(b"IDAT", zlib.compress(raw, 9)) + chunk(b"IEND", b""))


def icns():
    # 'ic07' = 128 px and 'ic08' = 256 px PNG, which every macOS since 10.7 reads
    parts = b""
    for kind, size in ((b"ic07", 128), (b"ic08", 256)):
        data = png(size)
        parts += kind + struct.pack(">I", len(data) + 8) + data
    return b"icns" + struct.pack(">I", len(parts) + 8) + parts


# ---------- tar helpers ----------

class Packer:
    def __init__(self, path):
        self.tar = tarfile.open(path, "w:gz")
        self.count = 0

    def dir(self, name):
        info = tarfile.TarInfo(name)
        info.type = tarfile.DIRTYPE
        info.mode = 0o755
        info.mtime = time.time()
        self.tar.addfile(info)

    def data(self, name, data, mode=0o644):
        info = tarfile.TarInfo(name)
        info.size = len(data)
        info.mode = mode
        info.mtime = time.time()
        self.tar.addfile(info, io.BytesIO(data))

    def tree(self, stage, prefix, skip):
        self.dir(prefix)
        for folder, dirs, files in os.walk(stage):
            rel = os.path.relpath(folder, stage)
            arc = prefix + ("" if rel == "." else "/" + rel.replace("\\", "/"))
            if rel != ".":
                self.dir(arc)
            for f in files:
                if f.lower().endswith(SKIP_EXT) or f in skip:
                    continue
                path = os.path.join(folder, f)
                # native code and the main program must be executable; everything else is data
                executable = f == "Halcyon" or f.endswith((".dylib", ".so")) or ".so." in f or "/plugins" in arc or f == "createdump"
                info = self.tar.gettarinfo(path, arc + "/" + f)
                info.mode = 0o755 if executable else 0o644
                info.uid = info.gid = 0
                info.uname = info.gname = ""
                with open(path, "rb") as fh:
                    self.tar.addfile(info, fh)
                self.count += 1

    def close(self):
        self.tar.close()


def publish(name, spec):
    stage = os.path.join(root, "publish-mac", spec["rid"])
    if "--skip-publish" not in sys.argv:
        shutil.rmtree(stage, ignore_errors=True)
        subprocess.run(["dotnet", "publish", os.path.join(here, "Halcyon.Mac.csproj"), "-c", "Release", "-r", spec["rid"], "--self-contained", "true",
                        "-p:PublishSingleFile=false", *spec["props"], "-o", stage], check=True)
    return stage


def build_mac(name, spec):
    stage = publish(name, spec)
    plist = f"""<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleName</key><string>Halcyon</string>
  <key>CFBundleDisplayName</key><string>Halcyon</string>
  <key>CFBundleIdentifier</key><string>app.halcyon.player</string>
  <key>CFBundleExecutable</key><string>Halcyon</string>
  <key>CFBundleIconFile</key><string>Halcyon</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>CFBundleShortVersionString</key><string>{version}</string>
  <key>CFBundleVersion</key><string>{version}</string>
  <key>LSMinimumSystemVersion</key><string>12.0</string>
  <key>LSApplicationCategoryType</key><string>public.app-category.music</string>
  <key>NSHighResolutionCapable</key><true/>
  <key>NSSupportsAutomaticGraphicsSwitching</key><true/>
  <key>NSAppTransportSecurity</key><dict><key>NSAllowsArbitraryLoads</key><true/></dict>
</dict>
</plist>
"""
    os.makedirs(out, exist_ok=True)
    target = os.path.join(out, f"Halcyon-{name}.tar.gz")
    p = Packer(target)
    p.dir("Halcyon.app")
    p.dir("Halcyon.app/Contents")
    p.dir("Halcyon.app/Contents/Resources")
    p.data("Halcyon.app/Contents/Info.plist", plist.encode("utf-8"))
    p.data("Halcyon.app/Contents/PkgInfo", b"APPL????")
    p.data("Halcyon.app/Contents/Resources/Halcyon.icns", icns())
    p.tree(stage, "Halcyon.app/Contents/MacOS", SKIP_NAMES | SKIP_MAC)
    if spec.get("vlc") == "arm64":
        import fetch_vlc
        fetch_vlc.main()
        p.dir("Halcyon.app/Contents/MacOS/libvlc")
        p.tree(fetch_vlc.vendor, "Halcyon.app/Contents/MacOS/libvlc/osx-arm64", set())
    p.close()
    print(f"wrote {target}  ({p.count} files, {os.path.getsize(target) / 1e6:.0f} MB)")


def build_linux(name, spec):
    stage = publish(name, spec)
    desktop = """[Desktop Entry]
Type=Application
Name=Halcyon
Comment=Music, video, pictures, podcasts and books in one player
Exec=@EXEC@
Icon=@ICON@
Terminal=false
Categories=AudioVideo;Audio;Player;
"""
    install = """#!/bin/sh
# Installs Halcyon for the current user: files in ~/.local/opt/halcyon, a launcher in ~/.local/bin and a menu entry.
set -e
here="$(cd "$(dirname "$0")" && pwd)"
dest="$HOME/.local/opt/halcyon"
rm -rf "$dest"
mkdir -p "$dest" "$HOME/.local/bin" "$HOME/.local/share/applications" "$HOME/.local/share/icons"
cp -r "$here/app/." "$dest/"
chmod +x "$dest/Halcyon"
cp "$here/halcyon.png" "$HOME/.local/share/icons/halcyon.png"
ln -sf "$dest/Halcyon" "$HOME/.local/bin/halcyon"
sed -e "s|@EXEC@|$dest/Halcyon|" -e "s|@ICON@|$HOME/.local/share/icons/halcyon.png|" "$here/halcyon.desktop.in" > "$HOME/.local/share/applications/halcyon.desktop"
echo "Halcyon installed. Start it from your applications menu, or run: halcyon"
command -v vlc >/dev/null 2>&1 || echo "Note: Halcyon plays media with VLC's engine. Install VLC (for example: sudo apt install vlc) before starting it."
"""
    uninstall = """#!/bin/sh
rm -rf "$HOME/.local/opt/halcyon" "$HOME/.local/bin/halcyon" "$HOME/.local/share/applications/halcyon.desktop" "$HOME/.local/share/icons/halcyon.png"
echo "Halcyon removed. Your library and settings in ~/.local/share/halcyon were kept."
"""
    os.makedirs(out, exist_ok=True)
    target = os.path.join(out, f"Halcyon-{name}.tar.gz")
    p = Packer(target)
    p.dir("halcyon")
    p.data("halcyon/install.sh", install.encode("utf-8"), 0o755)
    p.data("halcyon/uninstall.sh", uninstall.encode("utf-8"), 0o755)
    p.data("halcyon/halcyon.desktop.in", desktop.encode("utf-8"))
    p.data("halcyon/halcyon.png", png(256))
    p.data("halcyon/README.txt", (
        "Halcyon for Linux\n\n"
        "1. Install VLC (Halcyon plays media with its engine):  sudo apt install vlc   (or dnf / pacman equivalents)\n"
        "2. Run ./install.sh\n"
        "3. Start Halcyon from your applications menu, or run: halcyon\n\n"
        "Your library and settings live in ~/.local/share/halcyon.\n").encode("utf-8"))
    p.tree(stage, "halcyon/app", SKIP_NAMES | SKIP_LINUX)
    p.close()
    print(f"wrote {target}  ({p.count} files, {os.path.getsize(target) / 1e6:.0f} MB)")


args = [a for a in sys.argv[1:] if not a.startswith("--")] or ["all"]
names = list(TARGETS) if "all" in args else args
for n in names:
    if n not in TARGETS:
        sys.exit(f"unknown target {n}; choose from {', '.join(TARGETS)} or all")
    (build_mac if n.startswith("mac") else build_linux)(n, TARGETS[n])

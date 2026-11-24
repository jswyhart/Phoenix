"""
Builds Phoenix (the Electron app) for macOS and Linux from any machine that has the .NET SDK, Node and Python
(including Windows), and packs each build as a .tar.gz that keeps the symbolic links and executable permissions those
systems need.

    python package.py mac-arm64     Apple Silicon Macs (bundles VideoLAN's arm64 VLC engine from ..\\vendor)
    python package.py mac-x64       Intel Macs (bundles the VLC engine from the VideoLAN.LibVLC.Mac package)
    python package.py linux-x64     Linux on Intel/AMD PCs (uses the system's VLC)
    python package.py linux-arm64   Linux on ARM (uses the system's VLC)
    python package.py all

Output goes to ..\\dist\\Phoenix-<target>.tar.gz. Add --skip-publish to reuse the last .NET publish of the helper.

Each build is three things put together: Electron's own prebuilt program for that system (downloaded once from the
Electron release page and checked against the checksums published there), the page and main-process code from
build.mjs, and the self-contained .NET helper (backend\\) for that system.

Electron's Mac download is a zip full of symbolic links, which Windows cannot unpack faithfully, so it is never unpacked:
its entries are copied straight from the zip into the tar.

The Mac apps are not signed with an Apple developer certificate and not notarised (that needs a Mac and an Apple
account). See README.txt inside the Mac archives for the two commands that make macOS accept them.
"""
import hashlib, io, json, os, plistlib, shutil, stat, struct, subprocess, sys, tarfile, time, urllib.request, zipfile

here = os.path.dirname(os.path.abspath(__file__))
root = os.path.dirname(here)
out = os.path.join(root, "dist")
cache = os.path.join(here, ".cache")
version = json.load(open(os.path.join(here, "package.json"), encoding="utf-8"))["version"]
electron_version = json.load(open(os.path.join(here, "node_modules", "electron", "package.json"), encoding="utf-8"))["version"]
icon_png = os.path.join(root, "installer", "phoenix-512.png")

TARGETS = {
    "mac-arm64": dict(rid="osx-arm64", electron="darwin-arm64", vlc="vendor"),
    "mac-x64": dict(rid="osx-x64", electron="darwin-x64"),
    "linux-x64": dict(rid="linux-x64", electron="linux-x64"),
    "linux-arm64": dict(rid="linux-arm64", electron="linux-arm64"),
}
SKIP_EXT = (".pdb",)
NOW = time.time()


# ---------- Electron's prebuilt program ----------

def electron_zip(platform):
    """The Electron release zip for a platform, downloaded once and checked against the published SHA-256."""
    name = f"electron-v{electron_version}-{platform}.zip"
    base = f"https://github.com/electron/electron/releases/download/v{electron_version}/"
    os.makedirs(cache, exist_ok=True)
    sums_path = os.path.join(cache, f"SHASUMS256-v{electron_version}.txt")
    if not os.path.exists(sums_path):
        urllib.request.urlretrieve(base + "SHASUMS256.txt", sums_path)
    expected = None
    for line in open(sums_path, encoding="utf-8"):
        parts = line.split()
        if len(parts) == 2 and parts[1].lstrip("*") == name:
            expected = parts[0].lower()
    if not expected:
        sys.exit(f"{name} is not listed in Electron's checksums for v{electron_version}")
    path = os.path.join(cache, name)

    def digest():
        h = hashlib.sha256()
        with open(path, "rb") as f:
            for block in iter(lambda: f.read(1 << 20), b""):
                h.update(block)
        return h.hexdigest()

    if not os.path.exists(path) or digest() != expected:
        print("downloading", base + name)
        urllib.request.urlretrieve(base + name, path)
        if digest() != expected:
            os.remove(path)
            sys.exit(f"checksum mismatch for {name}")
    return path


# ---------- tar helpers ----------

class Packer:
    def __init__(self, path):
        self.tar = tarfile.open(path, "w:gz", compresslevel=6)
        self.count = 0
        self.dirs = set()

    def dir(self, name):
        name = name.rstrip("/")
        if not name or name in self.dirs:
            return
        self.dir(os.path.dirname(name).replace("\\", "/"))
        self.dirs.add(name)
        info = tarfile.TarInfo(name)
        info.type = tarfile.DIRTYPE
        info.mode = 0o755
        info.mtime = NOW
        self.tar.addfile(info)

    def data(self, name, data, mode=0o644):
        self.dir(os.path.dirname(name))
        info = tarfile.TarInfo(name)
        info.size = len(data)
        info.mode = mode
        info.mtime = NOW
        self.tar.addfile(info, io.BytesIO(data))
        self.count += 1

    def link(self, name, target):
        self.dir(os.path.dirname(name))
        info = tarfile.TarInfo(name)
        info.type = tarfile.SYMTYPE
        info.linkname = target
        info.mode = 0o755
        info.mtime = NOW
        self.tar.addfile(info)
        self.count += 1

    def tree(self, folder, prefix, executable=lambda arc, name: False, skip=lambda arc, name: False):
        """A folder from this disk. Windows keeps no permission bits, so <executable> says which files are programs."""
        self.dir(prefix)
        for base, dirs, files in os.walk(folder):
            dirs.sort()
            rel = os.path.relpath(base, folder)
            arc = prefix + ("" if rel == "." else "/" + rel.replace("\\", "/"))
            self.dir(arc)
            for f in sorted(files):
                if f.lower().endswith(SKIP_EXT) or skip(arc, f):
                    continue
                with open(os.path.join(base, f), "rb") as fh:
                    self.data(arc + "/" + f, fh.read(), 0o755 if executable(arc, f) else 0o644)

    def from_zip(self, zip_path, rename, replace):
        """
        Every entry of a zip, straight into the tar: symbolic links stay links and permission bits are kept.
        <rename> maps an entry's path to its path in the tar (None drops it); <replace> may return new bytes for a file.
        """
        with zipfile.ZipFile(zip_path) as z:
            for item in z.infolist():
                name = rename(item.filename)
                if name is None:
                    continue
                mode = (item.external_attr >> 16) & 0xFFFF
                if item.is_dir():
                    self.dir(name)
                elif stat.S_ISLNK(mode):
                    self.link(name, z.read(item).decode("utf-8"))
                else:
                    data = z.read(item)
                    changed = replace(item.filename, data)
                    self.data(name, data if changed is None else changed, (mode & 0o777) or 0o644)

    def close(self):
        self.tar.close()


def helper_is_program(arc, name):
    """Which files of the .NET helper (and the VLC engine beside it) must be executable."""
    return name in ("Phoenix.Backend", "createdump") or name.endswith((".dylib", ".so")) or ".so." in name or "/plugins" in arc


# ---------- the pieces ----------

def build_page():
    subprocess.run(["node", os.path.join(here, "build.mjs")], check=True, cwd=here)


def publish_helper(spec):
    stage = os.path.join(here, "publish", spec["rid"])
    if "--skip-publish" not in sys.argv or not os.path.isdir(stage):
        shutil.rmtree(stage, ignore_errors=True)
        subprocess.run(["dotnet", "publish", os.path.join(here, "backend", "Phoenix.Backend.csproj"), "-c", "Release", "-r", spec["rid"],
                        "--self-contained", "true", "-p:PublishSingleFile=false", "-p:DebugType=none", "-o", stage], check=True)
    return stage


def add_app(p, resources, spec):
    """What goes into Electron's resources folder: the app (page + main process) and the helper."""
    manifest = {"name": "phoenix", "productName": "Phoenix", "version": version, "main": "build/main.js"}
    p.data(f"{resources}/app/package.json", json.dumps(manifest, indent=2).encode("utf-8"))
    p.tree(os.path.join(here, "build"), f"{resources}/app/build")
    p.tree(publish_helper(spec), f"{resources}/backend", helper_is_program)
    if spec.get("vlc") == "vendor":
        sys.path.insert(0, os.path.join(root, "mac"))
        import fetch_vlc   # the Avalonia build's fetcher: VideoLAN's arm64 engine, unchanged so its signatures stay valid
        fetch_vlc.main()
        p.tree(fetch_vlc.vendor, f"{resources}/backend/libvlc/osx-arm64", helper_is_program)


def icns():
    """An .icns holding the 512-pixel logo ('ic09'); macOS scales it for the smaller sizes."""
    data = open(icon_png, "rb").read()
    body = b"ic09" + struct.pack(">I", len(data) + 8) + data
    return b"icns" + struct.pack(">I", len(body) + 8) + body


MAC_README = """Phoenix for macOS

This build is not signed with an Apple developer certificate, so macOS needs to be told once that it is wanted.
After unpacking, in Terminal:

    xattr -cr Phoenix.app
    codesign --force --deep --sign - Phoenix.app

(The first removes the "downloaded from the internet" mark; the second gives the app a local signature, which Apple
Silicon Macs require of every program.) Then move Phoenix.app to Applications and open it.

Your library and settings live in ~/Library/Application Support/Phoenix. A library left by Halcyon is copied over the
first time Phoenix starts; the Halcyon folder itself is not changed.
"""


def build_mac(name, spec):
    zip_path = electron_zip(spec["electron"])
    resources = "Phoenix.app/Contents/Resources"

    def rename(entry):
        if not entry.startswith("Electron.app/"):
            return None   # licence texts beside the app are added again below
        rest = entry[len("Electron.app/"):]
        if rest in ("Contents/Resources/default_app.asar", "Contents/Resources/electron.icns"):
            return None
        if rest == "Contents/MacOS/Electron":
            rest = "Contents/MacOS/Phoenix"
        return "Phoenix.app/" + rest

    def replace(entry, data):
        if entry != "Electron.app/Contents/Info.plist":
            return None
        info = plistlib.loads(data)
        info.update(CFBundleName="Phoenix", CFBundleDisplayName="Phoenix", CFBundleExecutable="Phoenix", CFBundleIdentifier="app.phoenix.player",
                    CFBundleIconFile="Phoenix.icns", CFBundleShortVersionString=version, CFBundleVersion=version,
                    LSApplicationCategoryType="public.app-category.music", NSHumanReadableCopyright="",
                    # phone sync answers the phone app on the local network; macOS asks the user with this sentence
                    NSLocalNetworkUsageDescription="Phoenix lets the Phoenix app on your phone find this Mac and copy music from it when phone sync is turned on.")
        for key in ("ElectronAsarIntegrity", "NSCameraUsageDescription", "NSMicrophoneUsageDescription", "NSBluetoothAlwaysUsageDescription", "NSBluetoothPeripheralUsageDescription"):
            info.pop(key, None)
        return plistlib.dumps(info)

    target = os.path.join(out, f"Phoenix-{name}.tar.gz")
    p = Packer(target)
    p.from_zip(zip_path, rename, replace)
    p.data(f"{resources}/Phoenix.icns", icns())
    add_app(p, resources, spec)
    with zipfile.ZipFile(zip_path) as z:
        for licence in ("LICENSE", "LICENSES.chromium.html"):
            if licence in z.namelist():
                p.data(f"{resources}/licenses/Electron-{licence}", z.read(licence))
    p.data(f"{resources}/licenses/Selawik-LICENSE.txt", open(os.path.join(here, "static", "fonts", "Selawik-LICENSE.txt"), "rb").read())
    p.data("README.txt", MAC_README.encode("utf-8"))
    p.close()
    print(f"wrote {target}  ({p.count} files, {os.path.getsize(target) / 1e6:.0f} MB)")


LINUX_DESKTOP = """[Desktop Entry]
Type=Application
Name=Phoenix
Comment=Music, video, pictures, podcasts and books in one player
Exec=@EXEC@
Icon=@ICON@
Terminal=false
Categories=AudioVideo;Audio;Player;
StartupWMClass=phoenix
"""

LINUX_INSTALL = """#!/bin/sh
# Installs Phoenix for the current user: files in ~/.local/opt/phoenix, a launcher in ~/.local/bin and a menu entry.
set -e
here="$(cd "$(dirname "$0")" && pwd)"
dest="$HOME/.local/opt/phoenix"
rm -rf "$dest"
mkdir -p "$dest" "$HOME/.local/bin" "$HOME/.local/share/applications" "$HOME/.local/share/icons"
cp -a "$here/app/." "$dest/"
cp "$here/phoenix.png" "$HOME/.local/share/icons/phoenix.png"
ln -sf "$dest/phoenix" "$HOME/.local/bin/phoenix"
sed -e "s|@EXEC@|$dest/phoenix|" -e "s|@ICON@|$HOME/.local/share/icons/phoenix.png|" "$here/phoenix.desktop.in" > "$HOME/.local/share/applications/phoenix.desktop"
echo "Phoenix installed. Start it from your applications menu, or run: phoenix"
command -v vlc >/dev/null 2>&1 || echo "Note: Phoenix plays media with VLC's engine. Install VLC (for example: sudo apt install vlc) before starting it."
if [ "$(cat /proc/sys/kernel/apparmor_restrict_unprivileged_userns 2>/dev/null)" = "1" ] || [ "$(cat /proc/sys/kernel/unprivileged_userns_clone 2>/dev/null)" = "0" ]; then
  echo "Note: this system limits the sandbox the window's browser engine uses. If Phoenix does not start, run once:"
  echo "  sudo chown root:root \\"$dest/chrome-sandbox\\" && sudo chmod 4755 \\"$dest/chrome-sandbox\\""
fi
"""

LINUX_UNINSTALL = """#!/bin/sh
rm -rf "$HOME/.local/opt/phoenix" "$HOME/.local/bin/phoenix" "$HOME/.local/share/applications/phoenix.desktop" "$HOME/.local/share/icons/phoenix.png"
echo "Phoenix removed. Your library and settings in ~/.local/share/phoenix were kept."
"""

LINUX_README = """Phoenix for Linux

1. Install VLC (Phoenix plays media with its engine):  sudo apt install vlc   (or dnf / pacman equivalents)
2. Run ./install.sh
3. Start Phoenix from your applications menu, or run: phoenix

If the window does not appear on a system that restricts unprivileged user namespaces (Ubuntu 23.10 and later),
install.sh prints the one command that fixes it.

Your library and settings live in ~/.local/share/phoenix. A library left by Halcyon is copied over the first time
Phoenix starts; the Halcyon folder itself is not changed.
"""


def build_linux(name, spec):
    zip_path = electron_zip(spec["electron"])

    def rename(entry):
        if entry == "resources/default_app.asar":
            return None
        if entry == "electron":
            entry = "phoenix"
        return "phoenix/app/" + entry

    target = os.path.join(out, f"Phoenix-{name}.tar.gz")
    p = Packer(target)
    p.data("phoenix/install.sh", LINUX_INSTALL.encode("utf-8"), 0o755)
    p.data("phoenix/uninstall.sh", LINUX_UNINSTALL.encode("utf-8"), 0o755)
    p.data("phoenix/phoenix.desktop.in", LINUX_DESKTOP.encode("utf-8"))
    p.data("phoenix/phoenix.png", open(icon_png, "rb").read())
    p.data("phoenix/README.txt", LINUX_README.encode("utf-8"))
    p.from_zip(zip_path, rename, lambda entry, data: None)
    add_app(p, "phoenix/app/resources", spec)
    p.data("phoenix/app/resources/licenses/Selawik-LICENSE.txt", open(os.path.join(here, "static", "fonts", "Selawik-LICENSE.txt"), "rb").read())
    p.close()
    print(f"wrote {target}  ({p.count} files, {os.path.getsize(target) / 1e6:.0f} MB)")


if __name__ == "__main__":
    args = [a for a in sys.argv[1:] if not a.startswith("--")] or ["all"]
    names = list(TARGETS) if "all" in args else args
    for n in names:
        if n not in TARGETS:
            sys.exit(f"unknown target {n}; choose from {', '.join(TARGETS)} or all")
    os.makedirs(out, exist_ok=True)
    build_page()
    for n in names:
        (build_mac if n.startswith("mac") else build_linux)(n, TARGETS[n])

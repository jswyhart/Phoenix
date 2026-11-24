"""
Fetches VLC's engine for the Apple Silicon Mac build: downloads VideoLAN's official Apple Silicon disk image, checks it
against the checksum VideoLAN publishes, and unpacks only what Halcyon needs (libvlc, libvlccore and the plug-ins) into
..\\vendor\\vlc-<version>-arm64. Needs 7-Zip to read the disk image. package.py runs this when the folder is missing.

The files are copied unchanged so VideoLAN's code signatures on them stay valid (Apple Silicon refuses unsigned code).
"""
import hashlib, os, shutil, subprocess, sys, urllib.request

VERSION = "3.0.21"
here = os.path.dirname(os.path.abspath(__file__))
root = os.path.dirname(here)
vendor = os.path.join(root, "vendor", f"vlc-{VERSION}-arm64")
work = os.path.join(root, "_tools", "vlc-dl")
dmg_name = f"vlc-{VERSION}-arm64.dmg"
base = f"https://download.videolan.org/pub/videolan/vlc/{VERSION}/macosx/"
seven = next((p for p in (r"C:\Program Files\7-Zip\7z.exe", r"C:\Program Files (x86)\7-Zip\7z.exe", shutil.which("7z") or "", shutil.which("7zz") or "") if p and os.path.exists(p)), None)


def main():
    if os.path.exists(os.path.join(vendor, "lib", "libvlc.dylib")):
        print("already present:", vendor)
        return
    if not seven:
        sys.exit("7-Zip is needed to unpack the VLC disk image (https://www.7-zip.org).")
    os.makedirs(work, exist_ok=True)
    dmg = os.path.join(work, dmg_name)
    expected = urllib.request.urlopen(base + dmg_name + ".sha256").read().decode().split()[0].lower()
    if not os.path.exists(dmg) or hashlib.sha256(open(dmg, "rb").read()).hexdigest() != expected:
        print("downloading", base + dmg_name)
        urllib.request.urlretrieve(base + dmg_name, dmg)
    actual = hashlib.sha256(open(dmg, "rb").read()).hexdigest()
    if actual != expected:
        sys.exit(f"checksum mismatch for {dmg_name}: got {actual}, VideoLAN publishes {expected}")

    unpack = os.path.join(work, "unpacked")
    shutil.rmtree(unpack, ignore_errors=True)
    inside = "VLC media player/VLC.app/Contents/MacOS"
    subprocess.run([seven, "x", "-y", "-snl", dmg, f"{inside}/lib/*", f"{inside}/plugins/*", f"-o{unpack}"], check=True, stdout=subprocess.DEVNULL)
    src = os.path.join(unpack, *inside.split("/"))

    shutil.rmtree(vendor, ignore_errors=True)
    os.makedirs(os.path.join(vendor, "lib"))
    # real files under the plain names (the disk image has libvlc.dylib -> libvlc.5.dylib links)
    shutil.copy2(os.path.join(src, "lib", "libvlc.5.dylib"), os.path.join(vendor, "lib", "libvlc.dylib"))
    shutil.copy2(os.path.join(src, "lib", "libvlccore.9.dylib"), os.path.join(vendor, "lib", "libvlccore.dylib"))
    shutil.copytree(os.path.join(src, "plugins"), os.path.join(vendor, "lib", "vlc", "plugins"))
    shutil.rmtree(unpack, ignore_errors=True)
    print("unpacked VLC", VERSION, "for Apple Silicon into", vendor)


if __name__ == "__main__":
    main()

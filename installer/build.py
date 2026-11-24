"""
Builds the Windows installers:  python installer\build.py [x64] [arm64]
Publishes the app self-contained (so it needs nothing pre-installed except the optional WebView2 runtime) and wraps it in
a setup wizard with Inno Setup. Output: ..\dist\Phoenix-<version>-win-<arch>-setup.exe  and a portable ..\dist\Phoenix-<version>-win-<arch>.zip
"""
import glob, os, re, shutil, subprocess, sys, zipfile

here = os.path.dirname(os.path.abspath(__file__))
root = os.path.dirname(here)
dist = os.path.join(root, "dist")
csproj = open(os.path.join(root, "Phoenix.csproj"), encoding="utf-8").read()
version = re.search(r"<Version>([^<]+)</Version>", csproj).group(1)

iscc = glob.glob(os.path.expanduser(r"~\.nuget\packages\tools.innosetup\*\tools\ISCC.exe")) + glob.glob(r"C:\Program Files (x86)\Inno Setup 6\ISCC.exe") + glob.glob(r"C:\Program Files\Inno Setup 6\ISCC.exe")
if not iscc:
    sys.exit("Inno Setup compiler not found. Restore the Tools.InnoSetup NuGet package or install Inno Setup 6.")
iscc = sorted(iscc)[-1]

archs = [a for a in sys.argv[1:] if a in ("x64", "arm64")] or ["x64", "arm64"]
subprocess.run([sys.executable, os.path.join(here, "make_logo.py")], check=True) if not os.path.exists(os.path.join(here, "Phoenix.ico")) else None
os.makedirs(dist, exist_ok=True)
for arch in archs:
    stage = os.path.join(root, "publish-installer", f"win-{arch}")
    shutil.rmtree(stage, ignore_errors=True)
    subprocess.run(["dotnet", "publish", os.path.join(root, "Phoenix.csproj"), "-c", "Release", "-r", f"win-{arch}", "--self-contained", "true",
                    "-p:DebugType=None", "-p:DebugSymbols=false", "-o", stage], check=True)
    subprocess.run([iscc, f"/DVersion={version}", f"/DArch={arch}", f"/DSourceDir={stage}", os.path.join(here, "phoenix.iss")], check=True)
    # portable copy: unzip anywhere and run Phoenix.exe (settings still go to %LocalAppData%\Phoenix)
    zpath = os.path.join(dist, f"Phoenix-{version}-win-{arch}.zip")
    with zipfile.ZipFile(zpath, "w", zipfile.ZIP_DEFLATED, compresslevel=9) as z:
        for folder, _, files in os.walk(stage):
            for f in files:
                full = os.path.join(folder, f)
                z.write(full, os.path.join("Phoenix", os.path.relpath(full, stage)))
    print("wrote", zpath)

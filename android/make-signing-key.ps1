# Makes the key that signs Phoenix for Android, once. Every build after that is signed with it, so a new build can
# always be installed over the old one (Android refuses an update signed with a different key).
#
# The key and its password are kept in %LOCALAPPDATA%\Phoenix-signing, outside the project folder (and so outside
# Git and any cloud sync of the project). Only your Windows account can read that folder. Back up the folder
# somewhere safe: if the key is lost, the app on your phones has to be uninstalled before a new key's builds will
# install, just as happened once already.
#
# You type the password here; it is not shown, and it is not passed to anything but Java's keytool and the file
# the build reads it from.

$ErrorActionPreference = "Stop"
$dir = Join-Path $env:LOCALAPPDATA "Phoenix-signing"
$store = Join-Path $dir "phoenix.keystore"
$passFile = Join-Path $dir "password.txt"
$keytool = "D:\android-jdk\bin\keytool.exe"

if (Test-Path $store) { Write-Host "A Phoenix signing key already exists at $store. Nothing was changed."; return }
if (-not (Test-Path $keytool)) { throw "Java's keytool was not found at $keytool." }

$a = Read-Host "Choose a password for the signing key (at least 6 characters)" -AsSecureString
$b = Read-Host "Type it again" -AsSecureString
$pa = [Runtime.InteropServices.Marshal]::PtrToStringBSTR([Runtime.InteropServices.Marshal]::SecureStringToBSTR($a))
$pb = [Runtime.InteropServices.Marshal]::PtrToStringBSTR([Runtime.InteropServices.Marshal]::SecureStringToBSTR($b))
if ($pa -ne $pb) { throw "The two passwords are not the same. Nothing was changed." }
if ($pa.Length -lt 6) { throw "The password has to be at least 6 characters. Nothing was changed." }

New-Item -ItemType Directory -Force $dir | Out-Null
# only this Windows account (and the system) may read the folder
icacls $dir /inheritance:r /grant:r "$($env:USERNAME):(OI)(CI)F" "SYSTEM:(OI)(CI)F" | Out-Null

# the password goes to keytool through the environment of this process only, never on a command line
$env:PHOENIX_SIGNING_PASS = $pa
try
{
    & $keytool -genkeypair -keystore $store -storetype PKCS12 -alias phoenix -keyalg RSA -keysize 4096 -validity 10000 `
        -dname "CN=Phoenix, O=Phoenix" -storepass:env PHOENIX_SIGNING_PASS -keypass:env PHOENIX_SIGNING_PASS -noprompt
    if ($LASTEXITCODE -ne 0) { throw "keytool could not make the key." }
}
finally { Remove-Item Env:PHOENIX_SIGNING_PASS -ErrorAction SilentlyContinue }

# the Android build reads the password from this file (see android\Directory.Build.props)
[IO.File]::WriteAllText($passFile, $pa)
# and again for the key itself: apksigner reads the two from separate files (one file would run out after the first)
[IO.File]::WriteAllText((Join-Path $dir "key-password.txt"), $pa)
$pa = $pb = $null

Write-Host ""
Write-Host "Done. The key is in $dir."
Write-Host "Back up that folder somewhere safe (a USB drive or a password manager): it can't be made again."

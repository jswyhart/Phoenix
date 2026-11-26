using Android.OS;
using Android.Runtime;
using Android.Security.Keystore;
using Halcyon.Core;
using Java.Security;
using Javax.Crypto;
using Javax.Crypto.Spec;

namespace Halcyon.Droid;

[Application(Label = "Phoenix", UsesCleartextTraffic = true, AllowBackup = false)]
public class HalcyonApp : Application
{
    public static ExoEngine Engine = null!;
    public static readonly Handler Main = new(Looper.MainLooper!);

    public HalcyonApp(IntPtr handle, JniHandleOwnership transfer) : base(handle, transfer) { }

    public override void OnCreate()
    {
        base.OnCreate();
        App.Secrets = new KeystoreSecrets();
        App.DeviceName = $"{Build.Manufacturer} {Build.Model}";
        App.Ui = RunOnMain;
        App.ArtInvalidate = Art.Invalidate;
        // Music copied from the PC lives in the shared Music folder, so other players and file managers see it too.
        App.SyncDir = System.IO.Path.Combine(Android.OS.Environment.GetExternalStoragePublicDirectory(Android.OS.Environment.DirectoryMusic!)!.AbsolutePath, "Halcyon");
        Engine = new ExoEngine(this);
        App.Init(FilesDir!.AbsolutePath, Engine, DefaultSettings);
        Engine.ApplyEqualizer();
        Th.Apply(App.Settings.Theme, this);
        PlaybackService.Watch(this);
    }

    static Settings DefaultSettings()
    {
        static string Dir(string type) => Android.OS.Environment.GetExternalStoragePublicDirectory(type)!.AbsolutePath;
        var s = new Settings();
        s.MusicFolders.Add(Dir(Android.OS.Environment.DirectoryMusic!));
        s.VideoFolders.Add(Dir(Android.OS.Environment.DirectoryMovies!));
        s.PictureFolders.Add(Dir(Android.OS.Environment.DirectoryPictures!));
        s.PictureFolders.Add(Dir(Android.OS.Environment.DirectoryDcim!));
        return s;
    }

    /// <summary>Runs on the main thread and waits, like Dispatcher.Invoke on Windows.</summary>
    static void RunOnMain(Action a)
    {
        if (Looper.MyLooper() == Looper.MainLooper) { a(); return; }
        using var done = new ManualResetEventSlim();
        Main.Post(() => { try { a(); } finally { done.Set(); } });
        done.Wait();
    }
}

/// <summary>Encrypts tokens with an AES-256-GCM key that never leaves the Android Keystore.</summary>
public class KeystoreSecrets : ISecretProtector
{
    const string Alias = "halcyon.secrets.v1", Store = "AndroidKeyStore";

    static IKey Key()
    {
        var ks = KeyStore.GetInstance(Store)!;
        ks.Load(null);
        if (!ks.ContainsAlias(Alias))
        {
            var gen = KeyGenerator.GetInstance(KeyProperties.KeyAlgorithmAes, Store)!;
            gen.Init(new KeyGenParameterSpec.Builder(Alias, KeyStorePurpose.Encrypt | KeyStorePurpose.Decrypt)
                .SetBlockModes(KeyProperties.BlockModeGcm)!
                .SetEncryptionPaddings(KeyProperties.EncryptionPaddingNone)!
                .SetKeySize(256)!
                .Build());
            gen.GenerateKey();
        }
        return ks.GetKey(Alias, null)!;
    }

    public string Protect(string plain)
    {
        var c = Cipher.GetInstance("AES/GCM/NoPadding")!;
        c.Init(CipherMode.EncryptMode, Key());
        var iv = c.GetIV()!;
        var ct = c.DoFinal(System.Text.Encoding.UTF8.GetBytes(plain))!;
        var all = new byte[1 + iv.Length + ct.Length];
        all[0] = (byte)iv.Length;
        iv.CopyTo(all, 1);
        ct.CopyTo(all, 1 + iv.Length);
        return Convert.ToBase64String(all);
    }

    public string Unprotect(string stored)
    {
        var all = Convert.FromBase64String(stored);
        int ivLen = all[0];
        var c = Cipher.GetInstance("AES/GCM/NoPadding")!;
        c.Init(CipherMode.DecryptMode, Key(), new GCMParameterSpec(128, all, 1, ivLen));
        return System.Text.Encoding.UTF8.GetString(c.DoFinal(all, 1 + ivLen, all.Length - 1 - ivLen)!);
    }
}

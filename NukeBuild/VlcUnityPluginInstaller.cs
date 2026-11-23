using System;
using System.IO;
using System.Linq;
using Nuke.Common.IO;

namespace DefaultNamespace;

/**
 * Installs a self-built VLC for Unity plugin (https://code.videolan.org/videolan/vlc-unity) into the Unity project.
 * The plugin is licensed under LGPL 2.1 or later and is not committed to Git.
 *
 * Expects a vlc-unity checkout where the Linux build has been done as in its CI:
 * - linux-x86_64/linux-install/ (libvlc, see build-libvlc-linux.sh)
 * - build_linux_x86_64/install-root/ (libVLCUnityPlugin.so, see meson build)
 * - lvs-all/netstandard2.0/LibVLCSharp.dll (LibVLCSharp built with /p:UNITY=true)
 */
public class VlcUnityPluginInstaller(AbsolutePath unityProjectDir, AbsolutePath vlcUnityDir)
{
    // Enables the optional assemblies that use the plugin (see Vlc.asmdef)
    public const string VlcUnityInstalledDefine = "VLC_UNITY_INSTALLED";

    private AbsolutePath TargetDir => unityProjectDir / "Assets/VLCUnity";
    private AbsolutePath CscRspFile => unityProjectDir / "Assets/csc.rsp";

    public void Install()
    {
        if (vlcUnityDir == null || !Directory.Exists(vlcUnityDir))
        {
            throw new ArgumentException($"vlc-unity directory not found: '{vlcUnityDir}'. Pass it via --vlc-unity-dir.");
        }

        AbsolutePath sourceDir = vlcUnityDir / "Assets/VLCUnity";
        AbsolutePath libVlcInstallDir = vlcUnityDir / "linux-x86_64/linux-install/lib";
        AbsolutePath unityPluginFile = vlcUnityDir / "build_linux_x86_64/install-root/usr/local/lib/x86_64-linux-gnu/libVLCUnityPlugin.so";
        AbsolutePath libVlcSharpFile = vlcUnityDir / "lvs-all/netstandard2.0/LibVLCSharp.dll";
        AssertExists(sourceDir, libVlcInstallDir, unityPluginFile, libVlcSharpFile);

        Uninstall();
        Console.WriteLine($"Installing VLC for Unity plugin from '{vlcUnityDir}' to '{TargetDir}'");

        // C# scripts of the plugin. Demos and tests are not needed.
        // Timeline integration is skipped because the project does not use the Timeline package.
        CopyDirectory(sourceDir / "Internal", TargetDir / "Internal", "Timeline");
        CopyDirectory(sourceDir / "Editor", TargetDir / "Editor");
        CopyDirectory(sourceDir / "Textures", TargetDir / "Textures");
        foreach (string fileName in new[] { "VLCUnity.asmdef", "VLCUnity.asmdef.meta", "documentation.md", "notice.txt" })
        {
            File.Copy(sourceDir / fileName, TargetDir / fileName);
        }
        File.Copy(vlcUnityDir / "LICENSE", TargetDir / "LICENSE.txt");

        // Native libraries, same layout as package-unity-plugin.sh
        AbsolutePath linuxPluginDir = TargetDir / "Plugins/Linux/x86_64";
        Directory.CreateDirectory(linuxPluginDir);
        File.Copy(unityPluginFile, linuxPluginDir / unityPluginFile.Name);
        foreach (string file in Directory.GetFiles(libVlcInstallDir, "libvlc*.so*"))
        {
            CopyFileOrSymlink(file, linuxPluginDir / Path.GetFileName(file));
        }
        CopyDirectory(libVlcInstallDir / "vlc", linuxPluginDir / "vlc");

        File.Copy(libVlcSharpFile, TargetDir / "Plugins" / libVlcSharpFile.Name);

        File.WriteAllText(CscRspFile, $"-define:{VlcUnityInstalledDefine}\n");

        Console.WriteLine("Installed VLC for Unity plugin. Restart the Unity editor to load the native plugin.");
    }

    public void Uninstall()
    {
        DirectoryUtils.DeleteDirectory(TargetDir);
        FileUtils.DeleteFile(unityProjectDir / "Assets/VLCUnity.meta");
        FileUtils.DeleteFile(CscRspFile);
        FileUtils.DeleteFile(unityProjectDir / "Assets/csc.rsp.meta");
    }

    private static void AssertExists(params AbsolutePath[] paths)
    {
        AbsolutePath missingPath = paths.FirstOrDefault(path => !File.Exists(path) && !Directory.Exists(path));
        if (missingPath != null)
        {
            throw new FileNotFoundException($"Missing build output of vlc-unity: '{missingPath}'. Build vlc-unity for Linux first.");
        }
    }

    private static void CopyDirectory(AbsolutePath sourceDir, AbsolutePath targetDir, params string[] excludedDirectoryNames)
    {
        Directory.CreateDirectory(targetDir);
        foreach (string file in Directory.GetFiles(sourceDir))
        {
            string fileName = Path.GetFileName(file);
            if (excludedDirectoryNames.Any(excluded => fileName == $"{excluded}.meta"))
            {
                continue;
            }
            CopyFileOrSymlink(file, targetDir / fileName);
        }

        foreach (string directory in Directory.GetDirectories(sourceDir))
        {
            string directoryName = Path.GetFileName(directory);
            if (excludedDirectoryNames.Contains(directoryName))
            {
                continue;
            }
            CopyDirectory(sourceDir / directoryName, targetDir / directoryName);
        }
    }

    private static void CopyFileOrSymlink(string sourceFile, AbsolutePath targetFile)
    {
        // Keep SONAME symlinks (e.g. libvlc.so.12 -> libvlc.so), such that the Linux loader can resolve them.
        FileInfo fileInfo = new FileInfo(sourceFile);
        if (fileInfo.LinkTarget != null)
        {
            File.CreateSymbolicLink(targetFile, fileInfo.LinkTarget);
            return;
        }
        File.Copy(sourceFile, targetFile);
    }
}

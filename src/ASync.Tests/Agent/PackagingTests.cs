using ASync.Agent;

namespace ASync.Tests.Agent;

/// <summary>
/// What the installers rely on in the agent's service code: the program the service runs from an AppImage, the user unit
/// the .deb/.rpm ship (packaging/linux), and telling which program an installed service runs.
/// </summary>
public class PackagingTests
{
    private static string RepoFile(string relative)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "ASync.slnx"))) return Path.Combine(dir.FullName, relative);
        throw new FileNotFoundException("the repository root (ASync.slnx) was not found above the test output");
    }

    [Fact]
    public void CurrentProgram_FromAnAppImage_IsTheAppImageFile_NotItsMount()
    {
        Assert.Equal(["/home/a/Apps/ASync.AppImage"],
            ServiceInstaller.CurrentProgram("/tmp/.mount_ASyncX1/usr/bin/async-desktop", null, "/home/a/Apps/ASync.AppImage", "/tmp/.mount_ASyncX1"));
        // An AppImage's environment inherited by a program outside its mount (started from it) is not that program.
        Assert.Equal(["/opt/async-desktop/async-desktop"],
            ServiceInstaller.CurrentProgram("/opt/async-desktop/async-desktop", null, "/home/a/Apps/ASync.AppImage", "/tmp/.mount_ASyncX1"));
        Assert.Equal(["/usr/bin/dotnet", "/src/async-cli.dll"], ServiceInstaller.CurrentProgram("/usr/bin/dotnet", "/src/async-cli.dll", null, null));
        Assert.Equal(["/usr/bin/async-cli"], ServiceInstaller.CurrentProgram("/usr/bin/async-cli", "", null, null));
    }

    [Fact]
    public void ThePackagedUserUnit_IsUsed_WhenItWouldSayTheSame()
    {
        if (!OperatingSystem.IsLinux()) return; // systemd units, and these Linux paths, exist only on Linux
        var unit = File.ReadAllText(RepoFile("packaging/linux/async-agent.service")).ReplaceLineEndings("\n");
        const string home = "/home/alice/.config/async";
        ServiceInstallOptions Options(string program = "/opt/async-desktop/async-desktop", string config = home) =>
            new() { Program = [program], ConfigDirectory = config };

        Assert.True(ServiceInstaller.UsesPackagedUnit(Options(), unit, home));
        Assert.True(ServiceInstaller.UsesPackagedUnit(Options(config: home + "/"), unit, home));
        Assert.False(ServiceInstaller.UsesPackagedUnit(Options(config: "/tmp/other"), unit, home)); // another config: its own unit
        Assert.False(ServiceInstaller.UsesPackagedUnit(Options("/usr/bin/async-cli"), unit, home)); // the CLI: its own unit
        Assert.False(ServiceInstaller.UsesPackagedUnit(Options() with { Verbose = true }, unit, home));
        Assert.False(ServiceInstaller.UsesPackagedUnit(Options() with { System = true }, unit, home));
        Assert.False(ServiceInstaller.UsesPackagedUnit(Options(), null, home)); // no package installed

        // The packaged unit is the same service as the one the installer writes, minus --config.
        var written = SystemdUnit.Render(Options());
        foreach (var line in new[] { "Type=notify", "Restart=on-failure", "TimeoutStopSec=60", "WantedBy=default.target" })
        {
            Assert.Contains(line + "\n", unit);
            Assert.Contains(line + "\n", written);
        }
    }

    [Fact]
    public void Runs_FindsTheProgramInEachKindOfDefinition()
    {
        var o = new ServiceInstallOptions { Program = ["/home/a b/ASync.AppImage"], ConfigDirectory = "/home/a b/.config/async" };
        Assert.True(ServiceInstaller.Runs(SystemdUnit.Render(o), "/home/a b/ASync.AppImage"));
        Assert.True(ServiceInstaller.Runs(LaunchdPlist.Render(o with { Program = ["/Applications/A & B.app/Contents/MacOS/async-desktop"] }),
            "/Applications/A & B.app/Contents/MacOS/async-desktop"));
        Assert.True(ServiceInstaller.Runs(WindowsTask.Render(o with { Program = [@"C:\Users\a b\AppData\Local\ASync.Desktop\current\async-desktop.exe"] }, @"PC\a"),
            @"C:\Users\a b\AppData\Local\ASync.Desktop\current\async-desktop.exe"));
        Assert.False(ServiceInstaller.Runs(SystemdUnit.Render(o), "/usr/bin/async-cli"));
    }
}

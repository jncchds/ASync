using System.Text;
using System.Xml.Linq;
using ASync.Agent;
using ASync.Client.Core;
using ASync.Client.Core.Secrets;
using ASync.Client.Core.Sync;
using ASync.Contracts;
using ASync.Engine.Storage;
using ASync.Tests.Engine;

namespace ASync.Tests.Agent;

/// <summary>The unit, plist and task the installer writes (rendered on any OS), and the run arguments.</summary>
public class ServiceFilesTests
{
    private static ServiceInstallOptions Options(bool system = false, string program = "/opt/async/async-cli") => new()
    {
        Program = [program], ConfigDirectory = "/home/alice/.config/async", System = system, RunAs = system ? "alice" : null,
    };

    [Fact]
    public void RunArguments_StartTheServiceModeOfTheSameConfig()
    {
        Assert.Equal(["/opt/async/async-cli", "--config", Path.GetFullPath("/home/alice/.config/async"), "service", "run"],
            ServiceInstaller.RunArguments(Options()));
        Assert.Equal(["dotnet", "/x/async-cli.dll", "--config", Path.GetFullPath("/c"), "service", "run", "--system", "--verbose", "--log-file", "/l/a.log"],
            ServiceInstaller.RunArguments(new ServiceInstallOptions
            {
                Program = ["dotnet", "/x/async-cli.dll"], ConfigDirectory = "/c", System = true, Verbose = true, LogFile = "/l/a.log",
            }));
    }

    [Fact]
    public void UserUnit_IsTypeNotify_AndWantedByTheUsersSession()
    {
        var unit = SystemdUnit.Render(Options());
        Assert.Contains("Type=notify\n", unit);
        Assert.Contains("ExecStart=/opt/async/async-cli --config ", unit);
        Assert.Contains("Restart=on-failure\n", unit);
        Assert.Contains("WantedBy=default.target\n", unit);
        Assert.DoesNotContain("User=", unit);
        Assert.DoesNotContain("network-online", unit); // not in a user manager
    }

    [Fact]
    public void SystemUnit_RunsAsTheUser_WithItsSocketInARuntimeDirectory()
    {
        var unit = SystemdUnit.Render(Options(system: true));
        Assert.Contains("User=alice\n", unit);
        Assert.Contains("RuntimeDirectory=async-agent\n", unit);
        Assert.Contains("Environment=ASYNC_CONTROL_SOCKET=/run/async-agent/agent.sock\n", unit);
        Assert.Contains("WantedBy=multi-user.target\n", unit);
    }

    [Theory]
    [InlineData("/opt/async-cli", "/opt/async-cli")]
    [InlineData("/home/a b/async-cli", "\"/home/a b/async-cli\"")]
    [InlineData("/x/100%/y", "\"/x/100%%/y\"")]
    [InlineData("/x/$HOME", "\"/x/$$HOME\"")]
    public void ExecStartWords_AreQuotedForSystemd(string word, string expected) => Assert.Equal(expected, SystemdUnit.Quote(word));

    [Fact]
    public void Plist_IsValidXml_WithEscapedArguments()
    {
        var plist = LaunchdPlist.Render(Options(program: "/Applications/A & B/async-cli") with { LogFile = "/Users/a/Library/Logs/ASync/agent.log" });
        var doc = XDocument.Parse(plist, LoadOptions.None);
        var strings = doc.Descendants("array").Single().Elements("string").Select(e => e.Value).ToList();
        Assert.Equal("/Applications/A & B/async-cli", strings[0]);
        Assert.Contains("--log-file", strings);
        Assert.Contains("<key>Label</key>\n  <string>ua.pp.chds.async.agent</string>", plist);
        Assert.Contains("<key>RunAtLoad</key>\n  <true/>", plist);
    }

    [Fact]
    public void WindowsTask_IsValidXml_StartingHeadlessAtLogon()
    {
        var xml = WindowsTask.Render(Options(program: @"C:\Program Files\ASync\async-cli.exe"), @"PC\alice");
        var doc = XDocument.Parse(xml);
        XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
        Assert.Equal(@"PC\alice", doc.Descendants(ns + "LogonTrigger").Single().Element(ns + "UserId")!.Value);
        Assert.Equal("conhost.exe", doc.Descendants(ns + "Command").Single().Value);
        Assert.StartsWith(@"--headless ""C:\Program Files\ASync\async-cli.exe"" --config", doc.Descendants(ns + "Arguments").Single().Value);
    }

    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("with space", "\"with space\"")]
    [InlineData(@"C:\dir\", @"C:\dir\")]
    [InlineData(@"C:\my dir\", @"""C:\my dir\\""")]
    [InlineData("say \"hi\"", "\"say \\\"hi\\\"\"")]
    [InlineData("", "\"\"")]
    public void WindowsArguments_SurviveCommandLineToArgv(string arg, string expected) => Assert.Equal(expected, WindowsTask.QuoteArgument(arg));
}

/// <summary>The rolling log file of the services without a system log.</summary>
public class RollingFileLoggerTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    [Fact]
    public void RollsOver_AndKeepsTheConfiguredNumberOfFiles()
    {
        var path = Path.Combine(_dir.Path, "logs", "agent.log");
        using (var provider = new RollingFileLoggerProvider(path, maxBytes: 2_000, keep: 2))
        {
            var logger = provider.CreateLogger("agent");
            for (var i = 0; i < 200; i++)
                Microsoft.Extensions.Logging.LoggerExtensions.LogInformation(logger, "line {Number} with some padding to fill the file", i);
        }
        var files = Directory.GetFiles(Path.GetDirectoryName(path)!).Select(Path.GetFileName).Order().ToList();
        Assert.Equal(["agent.log", "agent.log.1", "agent.log.2"], files);
        Assert.Contains("line 199", File.ReadAllText(path));
        Assert.All(files, f => Assert.True(new FileInfo(Path.Combine(_dir.Path, "logs", f!)).Length < 3_000));
    }
}

/// <summary>Secrets per platform behind <see cref="IClientStore"/>, and identities written by older versions.</summary>
public class SecretStoreTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private static DeviceIdentity Identity() =>
        new("https://sync.example.com", Guid.NewGuid(), 1, "alice", "laptop", Convert.ToBase64String(new byte[] { 1, 2, 3, 4 }));

    [Fact]
    public void AnIdentityWrittenByAnOlderVersion_IsReadAsItIs()
    {
        var identity = Identity();
        // Every version before the agent service: the private key as plain base64 in the 0600 file.
        File.WriteAllText(Path.Combine(_dir.Path, "identity.json"), System.Text.Json.JsonSerializer.Serialize(identity, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)));
        Directory.CreateDirectory(Path.Combine(_dir.Path, "keys"));
        File.WriteAllText(Path.Combine(_dir.Path, "keys", "5.key"), Convert.ToBase64String(new byte[] { 9, 8, 7 }) + "\n");

        var store = new FileClientStore(_dir.Path, PlainSecretProtector.Instance);
        Assert.Equal(identity, store.LoadIdentity());
        Assert.Equal(new byte[] { 9, 8, 7 }, store.LoadRepositoryKeys(5));
        Assert.True(store.HasRepositoryKeys(5));
        Assert.False(store.HasRepositoryKeys(6));
    }

    [Fact]
    public void WithAnotherProtector_SecretsAreMigratedOnRead()
    {
        new FileClientStore(_dir.Path, PlainSecretProtector.Instance).SaveIdentity(Identity());
        var marking = new MarkingProtector();
        var store = new FileClientStore(_dir.Path, marking);
        var loaded = store.LoadIdentity();
        Assert.NotNull(loaded);
        Assert.Equal(["device-key"], marking.Protected); // rewritten in the new format once
    }

    [Fact]
    public void OnLinux_TheStoreKeepsSecretsInPrivateFiles()
    {
        if (!OperatingSystem.IsLinux()) return;
        var store = new FileClientStore(_dir.Path);
        Assert.Equal("file", store.Protector.Kind);
        store.SaveIdentity(Identity());
        store.SaveRepositoryKeys(3, [1, 2]);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(Path.Combine(_dir.Path, "identity.json")));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(Path.Combine(_dir.Path, "keys", "3.key")));
    }

    [Fact]
    public void OnWindows_SecretsAreDpapiProtected()
    {
        if (!OperatingSystem.IsWindows()) return;
        var store = new FileClientStore(_dir.Path);
        Assert.Equal("dpapi", store.Protector.Kind);
        var identity = Identity();
        store.SaveIdentity(identity);
        Assert.Contains("\"dpapi:", File.ReadAllText(Path.Combine(_dir.Path, "identity.json")));
        Assert.Equal(identity, new FileClientStore(_dir.Path).LoadIdentity());
    }

    [Fact]
    public void SecretsOfAnotherPlatform_AreRefusedWithAReason()
    {
        if (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()) return;
        Assert.Contains("DPAPI", Assert.Throws<System.Security.Cryptography.CryptographicException>(() => SecretProtectors.Unprotect("dpapi:AAAA", _dir.Path)).Message);
        Assert.Contains("Keychain", Assert.Throws<System.Security.Cryptography.CryptographicException>(() => SecretProtectors.Unprotect("keychain:x/y", _dir.Path)).Message);
    }

    /// <summary>Writes plain base64 too, but says it is another kind, so the store sees a file in a foreign format.</summary>
    private sealed class MarkingProtector : ISecretProtector
    {
        public List<string> Protected { get; } = [];
        public string Kind => "marking";
        public string Protect(string id, byte[] secret)
        {
            Protected.Add(id);
            return Convert.ToBase64String(secret);
        }
        public void Forget(string id) { }
    }
}

/// <summary>What a run tells the agent's status while it goes.</summary>
public class RunProgressTests : IDisposable
{
    private readonly TempDir _storage = new(), _local = new(), _state = new();

    public void Dispose()
    {
        foreach (var d in new[] { _storage, _local, _state }) d.Dispose();
    }

    [Fact]
    public async Task ASync_CountsItsPlan_AndEveryFileAndByte()
    {
        _local.File("a.txt", Encoding.UTF8.GetBytes("hello"));
        _local.File("dir/b.bin", new byte[3000]);
        var progress = new RunProgress();
        var result = await new FolderSyncer(new LocalBackend(_storage.Path), _local.Path, _state.Path,
            new SyncOptions { Mode = SyncMode.TwoWay, DeviceName = "A" }) { Progress = progress }.RunAsync();
        Assert.Equal(2, result.Uploaded);
        var s = progress.Snapshot();
        Assert.Equal((2, 2, 3005L, 3005L), (s.Files, s.TotalFiles, s.Bytes, s.TotalBytes));
    }

    [Fact]
    public void Snapshot_AddsTheCurrentFilesBytes()
    {
        var p = new RunProgress();
        Assert.Equal(new ProgressSnapshot(0, null, 0, null, null), p.Snapshot());
        p.SetTotals(2, 300);
        p.StartFile("x");
        p.FileBytes(50);
        Assert.Equal(new ProgressSnapshot(0, 2, 50, 300, "x"), p.Snapshot());
        p.FinishFile(100);
        Assert.Equal(new ProgressSnapshot(1, 2, 100, 300, "x"), p.Snapshot());
    }
}

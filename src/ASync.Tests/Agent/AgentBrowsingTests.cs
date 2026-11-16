using ASync.Agent;
using ASync.Agent.Control;
using ASync.Tests.Engine;
using Microsoft.Extensions.Logging.Abstractions;

namespace ASync.Tests.Agent;

/// <summary>
/// The per-device setting "Allow browsing folders from the web": on by default, kept in <c>agent-settings.json</c>,
/// changed over the control channel (the desktop app, <c>async-cli browsing</c>), and forced off by <c>--no-browse</c>
/// or <c>ASYNC_ALLOW_BROWSE=0</c>.
/// </summary>
public class AgentBrowsingTests : IDisposable
{
    private readonly TempDir _config = new();

    public void Dispose() => _config.Dispose();

    private AgentRuntime Runtime(bool forcedOff = false) => new(new AgentOptions
    {
        ConfigDirectory = _config.Path, Mode = AgentMode.Service, Version = "1.2.3", WaitForIdentity = true,
        IdentityPollInterval = TimeSpan.FromMilliseconds(50), BrowsingForcedOff = forcedOff,
    }, NullLoggerFactory.Instance);

    [Fact]
    public async Task OnByDefault_TurnedOffOverTheChannel_AndKept()
    {
        await using (var runtime = Runtime())
        {
            runtime.Start();
            await using var client = (await ControlClient.TryConnectAsync(_config.Path))!;
            Assert.Equal(new BrowsingStatusDto(true), (await client.StatusAsync()).Browsing);
            Assert.Contains("no longer", await client.SetBrowsingAsync(false));
            Assert.Equal(new BrowsingStatusDto(false), (await client.StatusAsync()).Browsing);
            Assert.False(runtime.BrowsingAllowed);
        }
        Assert.False(AgentSettings.Load(_config.Path).AllowBrowsing);
        await using var restarted = Runtime();
        Assert.False(restarted.BrowsingAllowed);
        await restarted.SetBrowsingAsync(true, CancellationToken.None);
        Assert.True(restarted.BrowsingAllowed);
        Assert.True(AgentSettings.Load(_config.Path).AllowBrowsing);
    }

    [Fact]
    public async Task ForcedOff_CannotBeTurnedOn_AndSaysWhy()
    {
        await using var runtime = Runtime(forcedOff: true);
        Assert.False(runtime.BrowsingAllowed);
        Assert.Equal(new BrowsingStatusDto(false, Locked: true), (await runtime.StatusAsync(CancellationToken.None)).Browsing);
        var e = await Assert.ThrowsAsync<ControlException>(() => runtime.SetBrowsingAsync(true, CancellationToken.None));
        Assert.Contains("--no-browse", e.Message);
        await runtime.SetBrowsingAsync(false, CancellationToken.None); // turning it off is always fine
    }

    [Theory]
    [InlineData("0", true)]
    [InlineData("false", true)]
    [InlineData(" OFF ", true)]
    [InlineData("no", true)]
    [InlineData("1", false)]
    [InlineData("", false)]
    public void TheEnvironmentVariable(string value, bool off) => Assert.Equal(off, AgentSettings.BrowsingOffByEnvironment(value));

    [Fact]
    public void ABrokenSettingsFile_MeansTheDefaults()
    {
        File.WriteAllText(Path.Combine(_config.Path, AgentSettings.FileName), "{ not json");
        Assert.True(AgentSettings.Load(_config.Path).AllowBrowsing);
    }
}

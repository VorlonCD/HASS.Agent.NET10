using System.Text.Json;
using HASS.Agent.Companion.Media;
using HASS.Agent.Companion.Runtime;
using HASS.Agent.Companion.Configuration;
using HASS.Agent.Companion.Mqtt;
using HASS.Agent.Companion.SystemStatus;

namespace HASS.Agent.Companion.Tests;

public sealed class ManualBuildAndMuteTests
{
    [Theory]
    [InlineData("true", true)]
    [InlineData("false", false)]
    [InlineData("\"true\"", true)]
    [InlineData("\"false\"", false)]
    public void ExplicitMuteIsIdempotent(string json, bool expected)
    {
        using var document = JsonDocument.Parse(json);
        for (var i = 0; i < 2; i++)
        {
            Assert.True(MediaSessionService.TryParseMuteRequest(document.RootElement, out var muted));
            Assert.Equal(expected, muted);
        }
    }

    [Theory]
    [InlineData("1")]
    [InlineData("\"toggle\"")]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("null")]
    public void MalformedSuppliedMuteCannotToggle(string json)
    {
        using var document = JsonDocument.Parse(json);
        Assert.False(MediaSessionService.TryParseMuteRequest(document.RootElement, out _));
    }

    [Fact]
    public void AbsentMuteCannotToggle()
    {
        Assert.False(MediaSessionService.TryParseMuteRequest(null, out _));
    }

    [Fact]
    public void DeploymentCannotUseReleaseInstallerUpdates()
    {
        Assert.False(AppIdentity.AutomaticUpdatesEnabled);
        Assert.Equal("VorlonCD/HASS.Agent.NET10", AppIdentity.GitHubRepository);
    }

    [Fact]
    public void ApplicationMuteRemainsAvailableWithEveryPowerCommandDisabled()
    {
        var settings = new CompanionSettings
        {
            TrayAppCommands = [], ServiceCommands = [],
            BuiltInSensors = [new BuiltInSensorSetting { Key = "audio_sessions", TrayApp = true }]
        };
        settings.Normalize();
        Assert.False(settings.MqttButtonsEnabled);
        Assert.Empty(settings.TrayAppCommands);
        Assert.Empty(settings.ServiceCommands);
        Assert.True(MqttCompanionService.RequiresTrayCommandSubscription(settings));
    }

    [Fact]
    public async Task ManualBuildUpdateCheckOffersNoInstaller()
    {
        var state = await AppUpdateService.CheckAsync("10.9.3-vorlon.1", cancellationToken: TestContext.Current.CancellationToken);
        Assert.False(state.UpdateAvailable);
        Assert.Null(state.DownloadUrl);
    }
}

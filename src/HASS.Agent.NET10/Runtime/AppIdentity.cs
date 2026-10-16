namespace HASS.Agent.Companion.Runtime;

internal static class AppIdentity
{
    public const string DisplayName = "HASS.Agent .NET10";
    public const string ExecutableName = "HASS.Agent.NET10";
    public const string ConfigurationDirectoryName = "HASS.Agent.NET10";
    public const string LegacyConfigurationDirectoryName = "HASS.Agent.Companion";
    public const string MqttClientIdPrefix = "hass-agent-net10";
    public const string ServiceName = "HASS.Agent.NET10.Service";
    public const string ServiceDisplayName = "HASS.Agent .NET10 Service";
    public const string ServiceDescription = "Background system service for HASS.Agent .NET10 commands that must work without a logged-in user.";
    public const string GitHubRepository = "VorlonCD/HASS.Agent.NET10";
    public const string GitHubRepositoryUrl = "https://github.com/VorlonCD/HASS.Agent.NET10";
    // Source-managed K4 builds must never be replaced by a release installer.
    public static bool AutomaticUpdatesEnabled => false;
    public const string SupportUrl = "https://ko-fi.com/v1k70rk4";
}

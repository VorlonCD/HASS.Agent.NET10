using System.Text.Json;
using HASS.Agent.Companion.Announcements;

namespace HASS.Agent.Companion.Tests;

public sealed class AnnouncementProtocolTests
{
    private static readonly DateTimeOffset Now = new(2032, 5, 6, 7, 8, 9, TimeSpan.Zero);

    [Fact]
    public void Parses_valid_request_from_json_and_dictionary_data()
    {
        var id = Guid.NewGuid().ToString("D");
        var json = JsonDocument.Parse(Payload(id, "critical", Now.AddMinutes(2).ToString("O")));
        Assert.True(AnnouncementProtocol.TryParse(json.RootElement, Now, out var fromJson, out var jsonReason));
        Assert.Equal(string.Empty, jsonReason);
        Assert.Equal(id, fromJson!.RequestId);
        Assert.Equal(AnnouncementPriority.Critical, fromJson.Priority);
        Assert.Null(fromJson.ChimeUrl);

        var dictionary = new Dictionary<string, object?>
        {
            ["request_id"] = id,
            ["speech_url"] = "https://ha.local:8123/api/tts.mp3?token=secret",
            ["chime_url"] = "https://ha.local:8123/local/chime.wav",
            ["priority"] = "important",
            ["expires_at"] = Now.AddSeconds(30).ToString("O")
        };
        Assert.True(AnnouncementProtocol.TryParse(dictionary, Now, out var fromDictionary, out _));
        Assert.Equal("important", fromDictionary!.Priority.ToString().ToLowerInvariant());
        Assert.Equal("https://ha.local:8123/local/chime.wav", fromDictionary.ChimeUrl);
    }

    [Theory]
    [InlineData("not-a-uuid", "normal", "2032-05-06T07:09:00Z", "invalid_request_id")]
    [InlineData("00000000-0000-0000-0000-000000000000", "urgent", "2032-05-06T07:09:00Z", "invalid_priority")]
    [InlineData("00000000-0000-0000-0000-000000000000", "normal", "2032-05-06T07:08:08Z", "invalid_expiry")]
    [InlineData("00000000-0000-0000-0000-000000000000", "normal", "2032-05-06T07:10:15Z", "invalid_expiry")]
    [InlineData("00000000-0000-0000-0000-000000000000", "normal", "2032-05-06T09:09:00+02:00", "invalid_expiry")]
    public void Rejects_invalid_identifier_priority_or_expiry_with_fixed_reason(
        string id, string priority, string expiresAt, string expectedReason)
    {
        var payload = Payload(id, priority, expiresAt);

        Assert.False(AnnouncementProtocol.TryParse(payload, Now, out var request, out var reason));
        Assert.Null(request);
        Assert.Equal(expectedReason, reason);
        Assert.DoesNotContain("ha.local", reason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("relative/path.mp3", null, "invalid_speech_url")]
    [InlineData("ftp://ha.local:8123/speech.mp3", null, "invalid_speech_url")]
    [InlineData("https://user:secret@ha.local:8123/speech.mp3", null, "invalid_speech_url")]
    [InlineData("https://ha.local:8123/speech.mp3", "file:///tmp/chime.wav", "invalid_chime_url")]
    public void Rejects_non_http_absolute_urls_and_user_information(string speechUrl, string? chimeUrl, string expectedReason)
    {
        var json = Payload(Guid.NewGuid().ToString("D"), "normal", Now.AddSeconds(30).ToString("O"), speechUrl, chimeUrl);

        Assert.False(AnnouncementProtocol.TryParse(json, Now, out _, out var reason));
        Assert.Equal(expectedReason, reason);
        Assert.DoesNotContain("secret", reason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0.5)]
    [InlineData(5.0)]
    public void Allows_bounded_sender_clock_skew_without_extending_two_minute_lifetime(double skewSeconds)
    {
        var payload = Payload(Guid.NewGuid().ToString("D"), "normal",
            Now.AddMinutes(2).AddSeconds(skewSeconds).ToString("O"));
        Assert.True(AnnouncementProtocol.TryParse(payload, Now, out var request, out _));
        Assert.Equal(Now.AddMinutes(2), request!.ExpiresAt);
    }

    [Fact]
    public void Extracts_only_a_valid_request_id_for_correlated_protocol_failure()
    {
        var id = Guid.NewGuid().ToString("D");
        var json = Payload(id, "normal", "not-a-date");

        Assert.True(AnnouncementProtocol.TryGetRequestId(json, out var extracted));
        Assert.Equal(id, extracted);
        Assert.False(AnnouncementProtocol.TryGetRequestId(Payload("bad-id", "normal", "not-a-date"), out _));
        Assert.False(AnnouncementProtocol.TryGetRequestId("{\"request_id\":\"" + id + "\",\"request_id\":\"" + id + "\"}", out _));
    }

    [Fact]
    public void Rejects_duplicate_known_properties_and_malformed_json_without_echoing_payload()
    {
        var duplicate = "{\"request_id\":\"" + Guid.NewGuid().ToString("D") + "\",\"request_id\":\"" + Guid.NewGuid().ToString("D") + "\"}";
        Assert.False(AnnouncementProtocol.TryParse(duplicate, Now, out _, out var duplicateReason));
        Assert.Equal("invalid_payload", duplicateReason);

        const string malformed = "{\"speech_url\":\"https://ha.local/private-token";
        Assert.False(AnnouncementProtocol.TryParse(malformed, Now, out _, out var malformedReason));
        Assert.Equal("invalid_payload", malformedReason);
        Assert.DoesNotContain("private-token", malformedReason, StringComparison.Ordinal);
    }

    private static string Payload(
        string requestId,
        string priority,
        string expiresAt,
        string speechUrl = "https://ha.local:8123/api/tts.mp3?token=secret",
        string? chimeUrl = null)
    {
        var chimeField = chimeUrl is null ? string.Empty : ",\"chime_url\":\"" + chimeUrl + "\"";
        return "{\"request_id\":\"" + requestId + "\",\"speech_url\":\"" + speechUrl + "\"" + chimeField +
            ",\"priority\":\"" + priority + "\",\"expires_at\":\"" + expiresAt + "\"}";
    }
}

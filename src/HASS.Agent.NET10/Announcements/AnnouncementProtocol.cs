using System.Globalization;
using System.Text.Json;

namespace HASS.Agent.Companion.Announcements;

/// <summary>Validates the data object carried by an announcement command.</summary>
internal static class AnnouncementProtocol
{
    private const int MaximumJsonLength = 32 * 1024;
    private static readonly TimeSpan MaximumClockSkew = TimeSpan.FromSeconds(5);

    public static bool TryGetRequestId(object? data, out string requestId)
    {
        requestId = string.Empty;
        JsonDocument? ownedDocument = null;
        try
        {
            JsonElement root;
            switch (data)
            {
                case JsonElement element:
                    root = element;
                    break;
                case JsonDocument document:
                    root = document.RootElement;
                    break;
                case string json when json.Length <= MaximumJsonLength:
                    ownedDocument = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 16 });
                    root = ownedDocument.RootElement;
                    break;
                case null:
                    return false;
                default:
                    root = JsonSerializer.SerializeToElement(data, new JsonSerializerOptions { MaxDepth = 16 });
                    break;
            }

            if (root.ValueKind != JsonValueKind.Object)
                return false;

            var found = false;
            foreach (var property in root.EnumerateObject())
            {
                if (!string.Equals(property.Name, "request_id", StringComparison.Ordinal))
                    continue;
                if (found || property.Value.ValueKind != JsonValueKind.String)
                    return false;

                var candidate = property.Value.GetString();
                if (candidate is null || !Guid.TryParseExact(candidate, "D", out _))
                    return false;

                requestId = candidate;
                found = true;
            }

            return found;
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException or ArgumentException or ObjectDisposedException)
        {
            return false;
        }
        finally
        {
            ownedDocument?.Dispose();
        }
    }

    public static bool TryParse(object? data, DateTimeOffset now, out AnnouncementRequest? request, out string reason)
    {
        request = null;
        reason = "invalid_payload";

        JsonDocument? ownedDocument = null;
        try
        {
            JsonElement root;
            switch (data)
            {
                case JsonElement element:
                    root = element;
                    break;
                case JsonDocument document:
                    root = document.RootElement;
                    break;
                case string json when json.Length <= MaximumJsonLength:
                    ownedDocument = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 16 });
                    root = ownedDocument.RootElement;
                    break;
                case null:
                    return false;
                default:
                    root = JsonSerializer.SerializeToElement(data, new JsonSerializerOptions { MaxDepth = 16 });
                    break;
            }

            if (root.ValueKind != JsonValueKind.Object || HasDuplicateKnownProperties(root))
                return false;

            if (!TryGetRequiredString(root, "request_id", out var requestId) ||
                !Guid.TryParseExact(requestId, "D", out _))
            {
                reason = "invalid_request_id";
                return false;
            }

            if (!TryGetRequiredString(root, "speech_url", out var speechText) ||
                !TryGetHttpUri(speechText, out var speechUri))
            {
                reason = "invalid_speech_url";
                return false;
            }

            string? chimeText = null;
            if (root.TryGetProperty("chime_url", out var chimeElement) && chimeElement.ValueKind != JsonValueKind.Null)
            {
                if (chimeElement.ValueKind != JsonValueKind.String ||
                    !TryGetHttpUri(chimeElement.GetString(), out _))
                {
                    reason = "invalid_chime_url";
                    return false;
                }

                chimeText = chimeElement.GetString();
            }

            if (!TryGetRequiredString(root, "priority", out var priorityText) ||
                !TryParsePriority(priorityText, out var priority))
            {
                reason = "invalid_priority";
                return false;
            }

            if (!TryGetRequiredString(root, "expires_at", out var expiresText) ||
                !HasUtcDesignator(expiresText) ||
                !DateTimeOffset.TryParse(expiresText, CultureInfo.InvariantCulture, DateTimeStyles.None, out var expiresAt) ||
                expiresAt.Offset != TimeSpan.Zero)
            {
                reason = "invalid_expiry";
                return false;
            }

            var nowUtc = now.ToUniversalTime();
            var maximumExpiry = nowUtc.AddMinutes(2);
            if (expiresAt <= nowUtc || expiresAt > maximumExpiry + MaximumClockSkew)
            {
                reason = "invalid_expiry";
                return false;
            }

            // HA and Windows need not agree to the millisecond. Permit bounded
            // clock skew on receipt, without extending the local request lifetime.
            if (expiresAt > maximumExpiry) expiresAt = maximumExpiry;

            request = new AnnouncementRequest(requestId, speechUri.AbsoluteUri, chimeText, priority, expiresAt);
            reason = string.Empty;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
        catch (NotSupportedException)
        {
            return false;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
        finally
        {
            ownedDocument?.Dispose();
        }
    }

    private static bool HasDuplicateKnownProperties(JsonElement root)
    {
        var known = new HashSet<string>(StringComparer.Ordinal)
        {
            "request_id", "speech_url", "chime_url", "priority", "expires_at"
        };
        var found = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in root.EnumerateObject())
        {
            if (known.Contains(property.Name) && !found.Add(property.Name))
                return true;
        }

        return false;
    }

    private static bool TryGetRequiredString(JsonElement root, string name, out string value)
    {
        value = string.Empty;
        if (!root.TryGetProperty(name, out var element) || element.ValueKind != JsonValueKind.String)
            return false;

        value = element.GetString() ?? string.Empty;
        return value.Length > 0;
    }

    private static bool TryGetHttpUri(string? text, out Uri uri)
    {
        uri = null!;
        if (string.IsNullOrWhiteSpace(text) || text.Length > 4096 ||
            !Uri.TryCreate(text, UriKind.Absolute, out var parsed) ||
            (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps) ||
            parsed.Host.Length == 0 || parsed.UserInfo.Length != 0)
        {
            return false;
        }

        uri = parsed;
        return true;
    }

    private static bool TryParsePriority(string text, out AnnouncementPriority priority)
    {
        switch (text)
        {
            case "normal":
                priority = AnnouncementPriority.Normal;
                return true;
            case "important":
                priority = AnnouncementPriority.Important;
                return true;
            case "critical":
                priority = AnnouncementPriority.Critical;
                return true;
            default:
                priority = default;
                return false;
        }
    }

    private static bool HasUtcDesignator(string value) =>
        value.EndsWith('Z') || value.EndsWith("+00:00", StringComparison.Ordinal);
}

# Priority announcements (VorlonCD build)

This repository contains the Windows client and its matching Home Assistant integration under `custom_components/hass_agent`. The integration is based on v1k70rk4/HASS.Agent.NET10-Integration v10.9.1; the Windows baseline is bcd0a58acb954ba31e9c9d8f63d0cb311e6fc41e (10.9.3-beta.1). Build both from the same reviewed source revision.

## Home Assistant action

```yaml
action: hass_agent.announce
data:
  device_name: K4
  message: Motion at the back door
  priority: normal
  tts_entity_id: tts.google_translate_en_com
  # Optional existing media item; resolves through HA's media source API:
  # chime_media_id: media-source://media_source/local/announcement.wav
```

`normal` respects the underlying output mute state. `important` and `critical` may temporarily override mute. Waiting critical requests precede important requests, then normal requests; each class remains FIFO. There are at most ten waiting requests. Requests expire after two minutes and are not replayed after expiry. Normal requests received while the underlying state is muted are skipped, including during another request's temporary override.

HA accepting the action only means that the request was submitted. Observe the correlated `hass_agent_announcement` event for `completed`, `skipped_muted`, `cancelled`, or `failed`. Match `request_id`; do not infer playback success from a service-call response. `media_player.media_stop` cancels the active announcement.

## Windows configuration and recovery

Set `announcementMediaOrigins` in the protected Windows settings to the exact trusted HA origins, for example `["https://ha.example.internal:8123"]`. Empty configuration permits no announcement downloads. URLs, redirects, transfer size and download duration are bounded. Keep MQTT authenticated and restrict command publishing to trusted HA clients. The initial announcement transport is MQTT; the local HTTP API defaults to loopback and the installer opens no inbound rule.

Speech is prepared before changing audio. A missing or failed optional chime is a warning and speech continues. Playback uses a dedicated session on the captured default output. Chime finishes before speech begins. Announcement session and endpoint volume are set to 100%. Priority overrides temporarily mute other application sessions and restore their recorded states afterward, preserving observed external changes. They do not alter microphones or the communications device.

A private journal and a separate hidden recovery process protect priority overrides. If that protection cannot start, the override must fail before unmuting. Normal completion, stop, failure, timeout, device loss and owner-process death must restore audio. When restoring a muted endpoint, mute it before restoring other applications. WinMute's saved schedule remains owned by WinMute; schedule boundary tests must not rewrite it.

## Updates and deployment

This fork disables automatic release-installer updates and HA update-install controls. Signature verification remains in the original download implementation. Build reviewed source manually, record the source revision, version and installer SHA-256, and identify unsigned local builds accurately. Never treat an unsigned local artifact as an upstream signed release.

Before replacing the classic client and integration, retain a full HA backup, private client/service settings, startup configuration and every consumer of the old PC entities. Pause only the PC notification route while preserving phone alerts. The classic and new integrations share `hass_agent` and cannot coexist. Deploy the matching integration through HACS, restart HA, reconnect clients, install the Windows build and replacement service, then map actual entity IDs. Keep power/script commands disabled unless separately requested.

## Acceptance gates

Run the existing .NET and integration suites plus focused queue, input-validation, URL-validation, mute, restoration, cancellation, duplicate, expiry and failure tests. Before enabling the PC route, verify a popup, listener-confirmed speech at 100%, and independent Windows endpoint/application-state readback. Include priority announcements from muted and unmuted starts, optional WAV ordering, new application sessions, cancellation, output loss and forced owner termination. Distinguish simulated WinMute 22:00/08:00 crossings from actual overnight observation.

On failed acceptance, restore the recorded classic client/service/startup and the narrow affected HA configuration. A full HA backup restore is a separate operation requiring user confirmation; it reverts unrelated changes since the backup.

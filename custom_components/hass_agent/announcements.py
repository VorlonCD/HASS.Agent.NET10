"""Service and result handling for HASS.Agent announcements."""

from __future__ import annotations

import json
import logging
from collections import OrderedDict
from datetime import UTC, datetime, timedelta
from typing import Any, Callable
from urllib.parse import urlsplit
from uuid import UUID, uuid4

import voluptuous as vol
from homeassistant.components import media_source, mqtt, tts
from homeassistant.components.media_player.browse_media import async_process_play_media_url
from homeassistant.components.mqtt.models import ReceiveMessage
from homeassistant.config_entries import ConfigEntry
from homeassistant.const import CONF_URL
from homeassistant.core import HomeAssistant, SupportsResponse, callback
from homeassistant.exceptions import HomeAssistantError
from homeassistant.helpers import config_validation as cv
from homeassistant.helpers.event import async_call_later

from .const import CONF_DEVICE_NAME, CONF_HA_API, DOMAIN

SERVICE_ANNOUNCE = "announce"
EVENT_ANNOUNCEMENT = "hass_agent_announcement"
ANNOUNCEMENT_STATE_STORAGE_KEY = "_announcement_terminal_results"
ANNOUNCEMENT_PENDING_STORAGE_KEY = "_announcement_pending"

DEFAULT_TTS_ENTITY_ID = "tts.google_translate_en_com"
ANNOUNCEMENT_TIMEOUT_SECONDS = 120
MAX_PENDING_ANNOUNCEMENTS = 128
MAX_TERMINAL_ANNOUNCEMENTS = 128
MAX_STATE_PAYLOAD_BYTES = 1024

ANNOUNCEMENT_PRIORITIES = ("normal", "important", "critical")
ANNOUNCEMENT_STATUSES = frozenset({"completed", "skipped_muted", "cancelled", "failed"})
_REASONS_BY_STATUS = {
    "completed": frozenset({"chime_unavailable"}),
    "skipped_muted": frozenset({"muted"}),
    "cancelled": frozenset({"cancelled", "disposed"}),
    "failed": frozenset(
        {
            "expired",
            "queue_full",
            "invalid_request",
            "invalid_request_id",
            "invalid_payload",
            "invalid_speech_url",
            "invalid_chime_url",
            "invalid_priority",
            "invalid_expiry",
            "mute_state_unknown",
            "media_prepare_failed",
            "audio_acquire_failed",
            "playback_failed",
        }
    ),
}
_LOGGER = logging.getLogger(__name__)
_DEFAULT_REASON_BY_STATUS = {
    "completed": "completed",
    "skipped_muted": "muted",
    "cancelled": "cancelled",
    "failed": "playback_failed",
}

ANNOUNCE_SCHEMA = vol.Schema(
    {
        vol.Required(CONF_DEVICE_NAME): cv.string,
        vol.Required("message"): vol.All(cv.string, vol.Length(min=1, max=4096)),
        vol.Optional("priority", default="normal"): vol.In(ANNOUNCEMENT_PRIORITIES),
        vol.Optional("tts_entity_id", default=DEFAULT_TTS_ENTITY_ID): cv.entity_id,
        vol.Optional("chime_media_id"): cv.string,
    }
)


def state_topic(topic_id: str) -> str:
    """Return the per-device announcement result topic."""
    return f"hass.agent/media_player/{topic_id}/announcement/state"


def state_subscription_topics(
    hass: HomeAssistant,
    entry: ConfigEntry,
) -> dict[str, Any]:
    """Return definitions to combine with every other shared MQTT subscription."""
    return {
        f"{entry.unique_id}-announcement-state": {
            "topic": state_topic(entry.unique_id),
            "msg_callback": state_message_callback(hass, entry),
            "qos": 0,
        }
    }


def state_message_callback(hass: HomeAssistant, entry: ConfigEntry) -> Callable[[ReceiveMessage], None]:
    """Create a callback that accepts only small, valid terminal result messages."""

    @callback
    def handle_state(message: ReceiveMessage) -> None:
        if getattr(message, "retain", False) is True:
            return
        raw_payload = message.payload
        if not isinstance(raw_payload, str | bytes | bytearray):
            return
        if len(raw_payload) > MAX_STATE_PAYLOAD_BYTES:
            return

        try:
            payload = json.loads(raw_payload)
        except (ValueError, UnicodeDecodeError):
            return
        if not isinstance(payload, dict):
            return

        raw_request_id = payload.get("request_id")
        if not isinstance(raw_request_id, str):
            return
        try:
            request_id = str(UUID(raw_request_id))
        except (ValueError, AttributeError, TypeError):
            return

        status = payload.get("status")
        if not isinstance(status, str) or status not in ANNOUNCEMENT_STATUSES:
            return

        entry_data = hass.data.get(DOMAIN, {}).get(entry.entry_id)
        if not isinstance(entry_data, dict):
            return
        pending = entry_data.get(ANNOUNCEMENT_PENDING_STORAGE_KEY)
        if not isinstance(pending, dict) or request_id not in pending:
            return

        reason = _normalized_reason(status, payload.get("reason"))
        _emit_terminal_result(hass, entry, entry_data, request_id, status, reason)

    return handle_state


def _normalized_reason(status: str, value: Any) -> str:
    """Return a fixed, status-appropriate reason without trusting client text."""
    if isinstance(value, str) and value in _REASONS_BY_STATUS[status]:
        return value
    return _DEFAULT_REASON_BY_STATUS[status]


def _device_name(entry: ConfigEntry) -> str:
    """Return the configured device name used in result events."""
    device = entry.data.get("device")
    name = device.get("name") if isinstance(device, dict) else None
    return name if isinstance(name, str) and name else entry.title


def _emit_terminal_result(
    hass: HomeAssistant,
    entry: ConfigEntry,
    entry_data: dict[str, Any],
    request_id: str,
    status: str,
    reason: str,
) -> None:
    """Cancel a pending timeout and fire one bounded, URL-free terminal event."""
    pending = entry_data.setdefault(ANNOUNCEMENT_PENDING_STORAGE_KEY, {})
    cancel_timeout = pending.pop(request_id, None)
    if cancel_timeout is not None:
        cancel_timeout()

    terminal_results = entry_data.get(ANNOUNCEMENT_STATE_STORAGE_KEY)
    if not isinstance(terminal_results, OrderedDict):
        terminal_results = OrderedDict()
        entry_data[ANNOUNCEMENT_STATE_STORAGE_KEY] = terminal_results
    if request_id in terminal_results:
        return

    terminal_results[request_id] = None
    while len(terminal_results) > MAX_TERMINAL_ANNOUNCEMENTS:
        terminal_results.popitem(last=False)

    hass.bus.async_fire(
        EVENT_ANNOUNCEMENT,
        {
            "device_name": _device_name(entry),
            "request_id": request_id,
            "status": status,
            "reason": reason,
        },
    )


def _schedule_pending_timeout(
    hass: HomeAssistant,
    entry: ConfigEntry,
    request_id: str,
) -> None:
    """Track an accepted request and publish `failed/expired` if it never finishes."""
    entry_data = hass.data[DOMAIN][entry.entry_id]
    pending: dict[str, Callable[[], None]] = entry_data.setdefault(ANNOUNCEMENT_PENDING_STORAGE_KEY, {})
    if len(pending) >= MAX_PENDING_ANNOUNCEMENTS:
        raise HomeAssistantError("too many announcements are still pending for this device")

    @callback
    def expired(_now: datetime) -> None:
        current_entry_data = hass.data.get(DOMAIN, {}).get(entry.entry_id)
        if not isinstance(current_entry_data, dict):
            return
        _emit_terminal_result(hass, entry, current_entry_data, request_id, "failed", "expired")

    pending[request_id] = async_call_later(hass, ANNOUNCEMENT_TIMEOUT_SECONDS, expired)


def async_cancel_pending(hass: HomeAssistant, entry: ConfigEntry) -> None:
    """Cancel outstanding announcement timers when an entry is unloaded."""
    entry_data = hass.data.get(DOMAIN, {}).get(entry.entry_id)
    if not isinstance(entry_data, dict):
        return

    pending = entry_data.pop(ANNOUNCEMENT_PENDING_STORAGE_KEY, {})
    for cancel_timeout in pending.values():
        cancel_timeout()


def _absolute_media_url(hass: HomeAssistant, url: str) -> str:
    """Make a resolved media-source URL usable by the Windows client."""
    absolute_url = async_process_play_media_url(hass, url)
    parsed = urlsplit(absolute_url)
    if parsed.scheme not in {"http", "https"} or not parsed.netloc:
        raise HomeAssistantError("announcement audio did not resolve to an HTTP URL")
    return absolute_url


async def _resolve_speech_url(
    hass: HomeAssistant,
    message: str,
    tts_entity_id: str,
) -> str:
    media_id = tts.generate_media_source_id(hass, message, engine=tts_entity_id)
    play_item = await media_source.async_resolve_media(hass, media_id, None)
    return _absolute_media_url(hass, play_item.url)


async def _resolve_chime_url(hass: HomeAssistant, chime_media_id: str) -> str:
    if not media_source.is_media_source_id(chime_media_id):
        raise HomeAssistantError("chime_media_id must be a Home Assistant media source ID")
    play_item = await media_source.async_resolve_media(hass, chime_media_id, None)
    return _absolute_media_url(hass, play_item.url)


async def async_announce(
    hass: HomeAssistant,
    call: Any,
    entry_for_device_name: Callable[[str], ConfigEntry | None],
) -> dict[str, Any] | None:
    """Resolve audio and submit one non-retained announcement command over MQTT."""
    device_name = call.data[CONF_DEVICE_NAME]
    entry = entry_for_device_name(device_name)
    if entry is None:
        raise HomeAssistantError(f"device not found: {device_name}")

    if entry.data.get(CONF_URL) is not None or entry.data.get(CONF_HA_API, False):
        raise HomeAssistantError("hass_agent.announce requires a device connected over MQTT")

    entry_data = hass.data.get(DOMAIN, {}).get(entry.entry_id)
    if not isinstance(entry_data, dict):
        raise HomeAssistantError(f"device is not connected: {device_name}")

    apis = entry_data.get("apis")
    if not isinstance(apis, dict) or apis.get("announcements") is not True:
        raise HomeAssistantError(f"{device_name} does not support announcements")

    if not mqtt.is_connected(hass):
        raise HomeAssistantError("MQTT is not connected")
    if entry_data.get("mqtt_availability_known") is not True or entry_data.get("app_online") is not True:
        raise HomeAssistantError(f"{device_name} is offline over MQTT")

    try:
        speech_url = await _resolve_speech_url(
            hass,
            call.data["message"],
            call.data["tts_entity_id"],
        )
    except HomeAssistantError:
        raise HomeAssistantError("could not prepare announcement audio") from None
    except Exception:  # noqa: BLE001 - do not expose TTS text or generated media URLs
        raise HomeAssistantError("could not prepare announcement audio") from None

    chime_url = None
    chime_media_id = call.data.get("chime_media_id")
    if chime_media_id:
        try:
            chime_url = await _resolve_chime_url(hass, chime_media_id)
        except Exception:  # noqa: BLE001 - an optional chime must not suppress the speech
            _LOGGER.warning("HASS.Agent chime media could not be resolved; continuing without a chime")

    request_id = str(uuid4())
    expires_at = datetime.now(UTC) + timedelta(seconds=ANNOUNCEMENT_TIMEOUT_SECONDS)
    data: dict[str, Any] = {
        "request_id": request_id,
        "speech_url": speech_url,
        "priority": call.data["priority"],
        "expires_at": expires_at.isoformat().replace("+00:00", "Z"),
    }
    if chime_url is not None:
        data["chime_url"] = chime_url

    payload = {"command": "announce", "data": data}
    _schedule_pending_timeout(hass, entry, request_id)
    try:
        await mqtt.async_publish(
            hass,
            f"hass.agent/media_player/{entry.unique_id}/cmd",
            json.dumps(payload),
            qos=0,
            retain=False,
        )
    except Exception:  # noqa: BLE001 - report an actionable error without audio details
        pending = hass.data.get(DOMAIN, {}).get(entry.entry_id, {}).get(ANNOUNCEMENT_PENDING_STORAGE_KEY, {})
        cancel_timeout = pending.pop(request_id, None)
        if cancel_timeout is not None:
            cancel_timeout()
        raise HomeAssistantError("could not publish announcement over MQTT") from None

    if call.return_response:
        return {"accepted": True, "request_id": request_id}
    return None


def async_register_service(
    hass: HomeAssistant,
    entry_for_device_name: Callable[[str], ConfigEntry | None],
) -> None:
    """Register the public Home Assistant announcement service."""

    async def handle_announce(call: Any) -> dict[str, Any] | None:
        return await async_announce(hass, call, entry_for_device_name)

    hass.services.async_register(
        DOMAIN,
        SERVICE_ANNOUNCE,
        handle_announce,
        schema=ANNOUNCE_SCHEMA,
        supports_response=SupportsResponse.OPTIONAL,
    )

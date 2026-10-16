"""The announcement service and its MQTT terminal results."""

from __future__ import annotations

import json
from datetime import UTC, datetime
from types import SimpleNamespace
from uuid import uuid4

import pytest
import voluptuous as vol
from homeassistant.core import HomeAssistant
from homeassistant.exceptions import HomeAssistantError, ServiceValidationError
from homeassistant.setup import async_setup_component
from pytest_homeassistant_custom_component.common import MockConfigEntry, async_capture_events

from custom_components.hass_agent import announcements
from custom_components.hass_agent.const import DOMAIN

SERIAL = "0123456789abcdef0123456789abcdef"
DEVICE_NAME = "MY-PC"
CHIME_MEDIA_ID = "media-source://media_source/local/sounds/doorbell.mp3"


@pytest.fixture
def expected_lingering_timers() -> bool:
    """The mocked MQTT client keeps a periodic timer of its own running after a test."""
    return True


@pytest.fixture
async def integration(hass: HomeAssistant, mqtt_mock):
    """Set up the services with a mocked MQTT client."""
    assert await async_setup_component(hass, "http", {})
    assert await async_setup_component(hass, DOMAIN, {})
    await hass.async_block_till_done()


def add_announcement_device(hass: HomeAssistant) -> MockConfigEntry:
    """Add an MQTT device with the announcement capability and confirmed availability."""
    entry = MockConfigEntry(
        domain=DOMAIN,
        unique_id=SERIAL,
        title=DEVICE_NAME,
        data={"device": {"name": DEVICE_NAME}},
    )
    entry.add_to_hass(hass)
    hass.data.setdefault(DOMAIN, {})[entry.entry_id] = {
        "apis": {"announcements": True},
        "mqtt_availability_known": True,
        "app_online": True,
        announcements.ANNOUNCEMENT_PENDING_STORAGE_KEY: {},
        announcements.ANNOUNCEMENT_STATE_STORAGE_KEY: {},
    }
    return entry


def mock_audio_resolution(
    monkeypatch: pytest.MonkeyPatch,
) -> tuple[list[tuple[str, str | None]], list[str]]:
    """Mock Home Assistant TTS/media-source helpers and return generated sources."""
    generated_sources: list[tuple[str, str | None]] = []

    def generate_media_source_id(hass, message: str, engine: str) -> str:
        generated_sources.append((message, engine))
        return "media-source://tts/generated"

    async def resolve_media(hass, media_id: str, entity_id: str | None):
        assert entity_id is None
        if media_id == CHIME_MEDIA_ID:
            return SimpleNamespace(url="/api/media_source_proxy/chime.mp3")
        return SimpleNamespace(url="/api/tts_proxy/speech.mp3")

    processed_urls: list[str] = []

    def process_media_url(hass, url: str) -> str:
        processed_urls.append(url)
        return f"https://ha.example{url}"

    monkeypatch.setattr(announcements.tts, "generate_media_source_id", generate_media_source_id)
    monkeypatch.setattr(announcements.media_source, "async_resolve_media", resolve_media)
    monkeypatch.setattr(announcements, "async_process_play_media_url", process_media_url)
    return generated_sources, processed_urls


async def test_announce_publishes_resolved_audio_and_returns_acceptance(
    hass: HomeAssistant,
    integration,
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    entry = add_announcement_device(hass)
    generated_sources, processed_urls = mock_audio_resolution(monkeypatch)
    monkeypatch.setattr(announcements.mqtt, "is_connected", lambda hass: True)
    published: list[tuple[str, str, dict[str, object]]] = []

    async def publish(hass, topic: str, payload: str, **kwargs) -> None:
        published.append((topic, payload, kwargs))

    monkeypatch.setattr(announcements.mqtt, "async_publish", publish)
    monkeypatch.setattr(announcements, "async_call_later", lambda hass, delay, callback: lambda: None)
    events = async_capture_events(hass, announcements.EVENT_ANNOUNCEMENT)

    response = await hass.services.async_call(
        DOMAIN,
        "announce",
        {
            "device_name": DEVICE_NAME,
            "message": "The front door was opened.",
            "priority": "important",
            "tts_entity_id": "tts.google_translate_en_com",
            "chime_media_id": CHIME_MEDIA_ID,
        },
        blocking=True,
        return_response=True,
    )

    assert response["accepted"] is True
    assert len(response["request_id"]) == 36
    assert "status" not in response  # service acceptance is not playback completion
    assert generated_sources == [("The front door was opened.", "tts.google_translate_en_com")]
    assert processed_urls == ["/api/tts_proxy/speech.mp3", "/api/media_source_proxy/chime.mp3"]
    assert events == []

    topic, raw_payload, kwargs = published[0]
    payload = json.loads(raw_payload)
    assert topic == f"hass.agent/media_player/{SERIAL}/cmd"
    assert payload["command"] == "announce"
    assert payload["data"]["request_id"] == response["request_id"]
    assert payload["data"]["speech_url"] == "https://ha.example/api/tts_proxy/speech.mp3"
    assert payload["data"]["chime_url"] == "https://ha.example/api/media_source_proxy/chime.mp3"
    assert payload["data"]["priority"] == "important"
    assert payload["data"]["expires_at"].endswith("Z")
    assert kwargs == {"qos": 0, "retain": False}


@pytest.mark.parametrize(
    ("runtime_update", "connected", "error_fragment"),
    [
        ({"apis": {"announcements": False}}, True, "does not support announcements"),
        ({"mqtt_availability_known": True, "app_online": False}, True, "offline over MQTT"),
        ({}, False, "MQTT is not connected"),
    ],
)
async def test_announce_rejects_unsupported_or_offline_device(
    hass: HomeAssistant,
    integration,
    monkeypatch: pytest.MonkeyPatch,
    runtime_update: dict[str, object],
    connected: bool,
    error_fragment: str,
) -> None:
    entry = add_announcement_device(hass)
    hass.data[DOMAIN][entry.entry_id].update(runtime_update)
    monkeypatch.setattr(announcements.mqtt, "is_connected", lambda hass: connected)
    published: list[str] = []

    async def publish(hass, topic: str, payload: str, **kwargs) -> None:
        published.append(topic)

    monkeypatch.setattr(announcements.mqtt, "async_publish", publish)

    with pytest.raises((HomeAssistantError, ServiceValidationError), match=error_fragment):
        await hass.services.async_call(
            DOMAIN,
            "announce",
            {"device_name": DEVICE_NAME, "message": "Hello"},
            blocking=True,
        )

    assert published == []


async def test_announce_continues_without_unresolvable_optional_chime(
    hass: HomeAssistant,
    integration,
    monkeypatch: pytest.MonkeyPatch,
    caplog: pytest.LogCaptureFixture,
) -> None:
    add_announcement_device(hass)
    mock_audio_resolution(monkeypatch)
    monkeypatch.setattr(announcements.mqtt, "is_connected", lambda hass: True)
    published: list[dict[str, object]] = []

    async def publish(hass, topic: str, payload: str, **kwargs) -> None:
        published.append(json.loads(payload)["data"])

    monkeypatch.setattr(announcements.mqtt, "async_publish", publish)
    monkeypatch.setattr(announcements, "async_call_later", lambda hass, delay, callback: lambda: None)
    await hass.services.async_call(
        DOMAIN,
        "announce",
        {
            "device_name": DEVICE_NAME,
            "message": "Hello",
            "chime_media_id": "https://example.invalid/chime.mp3",
        },
        blocking=True,
    )

    assert len(published) == 1
    assert published[0]["speech_url"] == "https://ha.example/api/tts_proxy/speech.mp3"
    assert "chime_url" not in published[0]
    assert "HASS.Agent chime media could not be resolved" in caplog.text
    assert "example.invalid" not in caplog.text


async def test_announcement_result_is_validated_sanitized_and_deduplicated(hass: HomeAssistant) -> None:
    entry = add_announcement_device(hass)
    events = async_capture_events(hass, announcements.EVENT_ANNOUNCEMENT)
    callback = announcements.state_message_callback(hass, entry)
    request_id = str(uuid4())
    hass.data[DOMAIN][entry.entry_id][announcements.ANNOUNCEMENT_PENDING_STORAGE_KEY][request_id] = lambda: None
    message = SimpleNamespace(
        payload=json.dumps({"request_id": request_id, "status": "failed", "reason": "https://private.invalid"})
    )

    callback(message)
    callback(message)
    callback(SimpleNamespace(payload=json.dumps({"request_id": "not-a-uuid", "status": "completed"})))
    callback(SimpleNamespace(payload=json.dumps({"request_id": str(uuid4()), "status": "unknown"})))
    retained_id = str(uuid4())
    hass.data[DOMAIN][entry.entry_id][announcements.ANNOUNCEMENT_PENDING_STORAGE_KEY][retained_id] = lambda: None
    callback(SimpleNamespace(payload=json.dumps({"request_id": retained_id, "status": "completed"}), retain=True))
    callback(SimpleNamespace(payload=json.dumps({"request_id": str(uuid4()), "status": "completed"})))
    await hass.async_block_till_done()

    assert len(events) == 1
    assert events[0].data == {
        "device_name": DEVICE_NAME,
        "request_id": request_id,
        "status": "failed",
        "reason": "playback_failed",
    }


async def test_terminal_result_deduplication_cache_is_bounded(hass: HomeAssistant) -> None:
    entry = add_announcement_device(hass)
    events = async_capture_events(hass, announcements.EVENT_ANNOUNCEMENT)
    entry_data = hass.data[DOMAIN][entry.entry_id]

    for _ in range(announcements.MAX_TERMINAL_ANNOUNCEMENTS + 1):
        request_id = str(uuid4())
        entry_data[announcements.ANNOUNCEMENT_PENDING_STORAGE_KEY][request_id] = lambda: None
        announcements._emit_terminal_result(hass, entry, entry_data, request_id, "completed", "completed")

    await hass.async_block_till_done()

    assert len(events) == announcements.MAX_TERMINAL_ANNOUNCEMENTS + 1
    assert len(entry_data[announcements.ANNOUNCEMENT_STATE_STORAGE_KEY]) == announcements.MAX_TERMINAL_ANNOUNCEMENTS


async def test_announcement_timeout_fires_failed_expired_event(
    hass: HomeAssistant,
    integration,
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    entry = add_announcement_device(hass)
    mock_audio_resolution(monkeypatch)
    monkeypatch.setattr(announcements.mqtt, "is_connected", lambda hass: True)

    async def publish(hass, topic: str, payload: str, **kwargs) -> None:
        return None

    monkeypatch.setattr(announcements.mqtt, "async_publish", publish)
    timeouts: list[object] = []

    def schedule(hass, delay: float, callback):
        timeouts.append(callback)
        return lambda: None

    monkeypatch.setattr(announcements, "async_call_later", schedule)
    events = async_capture_events(hass, announcements.EVENT_ANNOUNCEMENT)
    response = await hass.services.async_call(
        DOMAIN,
        "announce",
        {"device_name": DEVICE_NAME, "message": "Hello"},
        blocking=True,
        return_response=True,
    )

    assert response["accepted"] is True
    assert len(timeouts) == 1
    timeouts[0](datetime.now(UTC))
    await hass.async_block_till_done()

    assert [event.data["status"] for event in events] == ["failed"]
    assert events[0].data["reason"] == "expired"
    assert events[0].data["request_id"] == response["request_id"]
    assert events[0].data["device_name"] == DEVICE_NAME


async def test_state_subscription_uses_same_media_player_topic(
    hass: HomeAssistant,
) -> None:
    entry = add_announcement_device(hass)
    captured = announcements.state_subscription_topics(hass, entry)
    subscription = captured[f"{SERIAL}-announcement-state"]
    assert subscription["topic"] == announcements.state_topic(SERIAL)
    assert announcements.state_topic(SERIAL) == f"hass.agent/media_player/{SERIAL}/announcement/state"


async def test_announce_rejects_missing_required_fields(hass: HomeAssistant, integration) -> None:
    with pytest.raises((vol.Invalid, ServiceValidationError)):
        await hass.services.async_call(
            DOMAIN,
            "announce",
            {"device_name": DEVICE_NAME},
            blocking=True,
        )

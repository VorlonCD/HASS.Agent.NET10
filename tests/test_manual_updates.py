"""Manual-source builds cannot be replaced through stale update entities."""

from types import SimpleNamespace
from unittest.mock import AsyncMock

import pytest
from homeassistant.components.update import UpdateEntityFeature
from homeassistant.exceptions import HomeAssistantError
from pytest_homeassistant_custom_component.common import MockConfigEntry, async_capture_events

from custom_components.hass_agent import _apply_update_state
from custom_components.hass_agent import update
from custom_components.hass_agent.const import DOMAIN


async def test_disabled_updates_cannot_publish_or_fire_install(hass, monkeypatch):
    entry_id = "manual-build"
    hass.data[DOMAIN] = {entry_id: {"apis": {"update": False}}}
    device = SimpleNamespace(identifiers={(DOMAIN, "serial")}, name="PC", manufacturer="VorlonCD", model="Agent", sw_version="10.9.3-vorlon.1")
    entity = update.HassAgentUpdate(entry_id, "serial", device)
    entity.hass = hass
    publish = AsyncMock()
    monkeypatch.setattr(update.mqtt, "async_publish", publish)
    events = async_capture_events(hass, "hass_agent_command")
    assert not entity.supported_features & UpdateEntityFeature.INSTALL
    with pytest.raises(HomeAssistantError, match="manual source builds"):
        await entity.async_install(None, False)
    publish.assert_not_called()
    await hass.async_block_till_done()
    assert events == []


async def test_update_state_cannot_create_entity_when_disabled(hass):
    entry = MockConfigEntry(domain=DOMAIN, unique_id="manual-build", title="PC")
    entry.add_to_hass(hass)
    entry_data = {"apis": {"update": False}, "loaded": {"update": False}}
    hass.data[DOMAIN] = {entry.entry_id: entry_data}
    _apply_update_state(hass, entry, {"update_available": True, "latest_version": "99.0.0"})
    await hass.async_block_till_done()
    assert "update_state" not in entry_data
    assert entry_data["loaded"]["update"] is False

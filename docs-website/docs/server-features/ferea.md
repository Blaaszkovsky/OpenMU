---
title: Ferea on the extended client
sidebar_label: Ferea
description: Local Ferea map and monster configuration for the MuMain extended client.
---

# Ferea on the extended client

The Season 6 configuration includes Ferea as network map 112. It uses the
extended MuMain client's World113 assets. A character at level 400 can enter
through the Ferea warp entry. The entry gate places the character near
coordinates 236, 152. The map also has a provisional Baski spawn near
215, 165 (monster number 900). Ferea Knight, Archer, and Fighter also spawn in
six hunting locations each. These three monster types currently have no item
drops; their combat values are adjusted for this Season 6 configuration.

This configuration needs a client package containing World113 terrain, objects,
textures, the Baski model and textures, and `Monster286` through `Monster291`
with their matching textures. These monster models are native v14 BMD files;
the extended MuMain loader reads them directly. The Ferea General, Lord of
Ferea, and Crystal Orb have definitions for visual and configuration checks,
but do not spawn automatically. The crystal and boss fight, item drops, and
event timing have not yet been implemented. Baski remains an integration
prototype.

On an existing installation, back up the configuration database, publish the
server and client from compatible revisions, then use the admin panel's data
updates to apply **Add Ferea map** and **Add Ferea monsters**. If the earlier
**Add Baski prototype** update was installed, apply its new version. Restart the game
server after applying the updates so it reloads the new map and monster
configuration. Confirm the entry gate, terrain collision, monster appearance,
and a return warp with a real client. Merely passing initializer tests does not
establish that the map renders correctly in a client.

The original Season 6 client does not contain these assets; this map is for the
MuMain extended compatibility gate.

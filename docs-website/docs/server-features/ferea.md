---
title: Ferea on the extended client
sidebar_label: Ferea
description: Local Ferea map and provisional Baski configuration for the MuMain extended client.
---

# Ferea on the extended client

The Season 6 configuration includes Ferea as network map 112. It uses the
extended MuMain client's World113 assets. A character at level 400 can enter
through the Ferea warp entry. The entry gate places the character near
coordinates 236, 152. The map also has a provisional Baski spawn near
215, 165 (monster number 900).

This configuration needs a client package containing World113 terrain, objects,
textures, and the Baski model and textures. The Baski model is a native v14 BMD;
the extended MuMain loader reads it directly. The Baski identity and spawn are
only an integration prototype, not a finished boss encounter or loot design.

On an existing installation, back up the configuration database, publish the
server and client from compatible revisions, then use the admin panel's data
updates to apply **Add Ferea map** and **Add Baski prototype**. Restart the game
server after applying the updates so it reloads the new map and monster
configuration. Confirm the entry gate, terrain collision, monster appearance,
and a return warp with a real client. Merely passing initializer tests does not
establish that the map renders correctly in a client.

The original Season 6 client does not contain these assets; this map is for the
MuMain extended compatibility gate.

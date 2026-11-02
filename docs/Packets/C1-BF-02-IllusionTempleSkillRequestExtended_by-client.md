# C1 BF 02 - IllusionTempleSkillRequestExtended (by client)

## Is sent when

The open source client requests an illusion temple special skill with a full sixteen-bit target object index.

## Causes the following actions on the server side

The server checks if the player is inside the event etc. and performs the skills accordingly.

## Structure

| Index | Length | Data Type | Value | Description |
|-------|--------|-----------|-------|-------------|
| 0 | 1 |   Byte   | 0xC1  | [Packet type](PacketTypes.md) |
| 1 | 1 |    Byte   |   9   | Packet header - length of the packet |
| 2 | 1 |    Byte   | 0xBF  | Packet header - packet type identifier |
| 3 | 1 |    Byte   | 0x02  | Packet header - sub packet type identifier |
| 4 | 2 | ShortBigEndian |  | SkillNumber |
| 6 | 2 | ShortLittleEndian |  | TargetObjectIndex |
| 8 | 1 | Byte |  | Distance |
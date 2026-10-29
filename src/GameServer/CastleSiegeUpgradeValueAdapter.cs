// <copyright file="CastleSiegeUpgradeValueAdapter.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameServer;

using MUnique.OpenMU.DataModel.Configuration;
using MUnique.OpenMU.GameLogic.CastleSiege.NPC;

/// <summary>
/// Translates Season 6 structure-upgrade values without changing the level-based game action.
/// </summary>
internal static class CastleSiegeUpgradeValueAdapter
{
    /// <summary>
    /// Gets the requested level from the wire value: maximum health for life, level otherwise.
    /// </summary>
    /// <param name="configuration">The active siege configuration.</param>
    /// <param name="npcNumber">The structure monster number.</param>
    /// <param name="upgradeType">The upgrade type.</param>
    /// <param name="value">The client-supplied value.</param>
    /// <returns>The configured level, or zero for an invalid value; zero can never be the next upgrade level.</returns>
    internal static byte GetRequestedLevel(CastleSiegeConfiguration? configuration, uint npcNumber, CastleSiegeUpgradeType upgradeType, uint value)
    {
        if (upgradeType != CastleSiegeUpgradeType.Life)
        {
            return value <= byte.MaxValue ? (byte)value : (byte)0;
        }

        var definitions = GetLifeUpgrades(configuration, npcNumber);
        if (definitions is null)
        {
            return 0;
        }

        var matches = definitions.Where(definition => definition.Value > 0 && (uint)definition.Value == value).Take(2).ToArray();
        return matches is { Length: 1 } && definitions.Count(definition => definition.Level == matches[0].Level) == 1
            ? matches[0].Level
            : (byte)0;
    }

    /// <summary>
    /// Gets the wire response value for the requested upgrade level.
    /// </summary>
    /// <param name="configuration">The active siege configuration.</param>
    /// <param name="npcNumber">The structure monster number.</param>
    /// <param name="upgradeType">The upgrade type.</param>
    /// <param name="level">The requested upgrade level.</param>
    /// <returns>The configured maximum health for life, the level otherwise, or zero for missing or ambiguous life configuration.</returns>
    internal static uint GetResponseValue(CastleSiegeConfiguration? configuration, uint npcNumber, CastleSiegeUpgradeType upgradeType, byte level)
    {
        if (upgradeType != CastleSiegeUpgradeType.Life)
        {
            return level;
        }

        var definitions = GetLifeUpgrades(configuration, npcNumber);
        if (definitions is null)
        {
            return 0;
        }

        var matches = definitions.Where(definition => definition.Level == level && definition.Value > 0).Take(2).ToArray();
        return matches is { Length: 1 } && definitions.Count(definition => definition.Value == matches[0].Value) == 1
            ? (uint)matches[0].Value
            : 0;
    }

    private static ICollection<CastleSiegeUpgradeDefinition>? GetLifeUpgrades(CastleSiegeConfiguration? configuration, uint npcNumber)
    {
        // Season 6 sends physical maximum HP for life upgrades; defense and regeneration send levels.
        return npcNumber == (uint)CastleSiegeGate.MonsterNumber
            ? configuration?.GateLifeUpgrades
            : npcNumber == (uint)CastleSiegeStatue.MonsterNumber
                ? configuration?.StatueLifeUpgrades
                : null;
    }
}

// <copyright file="INpcHitFilter.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameLogic;

/// <summary>
/// An optional intelligence capability for encounter-specific damage rules.
/// </summary>
public interface INpcHitFilter
{
    /// <summary>
    /// Filters damage before health changes. Called synchronously under the NPC hit lock.
    /// </summary>
    /// <param name="hit">The incoming hit.</param>
    /// <param name="attacker">The source of damage.</param>
    /// <returns>The effective hit.</returns>
    HitInfo FilterHit(HitInfo hit, IAttacker attacker);
}

// <copyright file="IPlayerEnteringWorldPlugIn.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameLogic.PlugIns;

using System.Runtime.InteropServices;
using MUnique.OpenMU.PlugIns;

/// <summary>
/// Prepares initial admission after character selection, before the first character information packet.
/// Ordinary map changes and respawns do not call this plugin point.
/// </summary>
[Guid("663D8C2A-0F58-4D5B-99EC-6EA114DE811A")]
[PlugInPoint("Player entering world", "Prepares a selected character before initial world admission.")]
public interface IPlayerEnteringWorldPlugIn
{
    /// <summary>
    /// Prepares the selected character before its initial position is sent to the client.
    /// </summary>
    /// <param name="player">The player selecting a character.</param>
    ValueTask PlayerEnteringWorldAsync(Player player);
}

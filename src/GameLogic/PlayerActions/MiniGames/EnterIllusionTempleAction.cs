// <copyright file="EnterIllusionTempleAction.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameLogic.PlayerActions.MiniGames;

using MUnique.OpenMU.GameLogic.MiniGames;
using MUnique.OpenMU.GameLogic.MiniGames.IllusionTemple;

/// <summary>Admits a ticketed participant through a nearby Mirage.</summary>
public sealed class EnterIllusionTempleAction
{
    private readonly EnterMiniGameAction _enterAction = new();

    /// <summary>Checks the open NPC dialog, then uses the shared atomic admission rules.</summary>
    /// <param name="player">The participant.</param>
    /// <param name="level">The event level, between one and six.</param>
    /// <param name="slot">The ticket slot, or 0xFF to search the inventory.</param>
    public async ValueTask TryEnterAsync(Player player, byte level, byte slot)
    {
        if (level is < 1 or > 6 || player.SelectedCharacter?.CharacterClass is null || player.Attributes is null
            || !player.IsAlive || player.PlayerState.CurrentState != PlayerState.NpcDialogOpened
            || player.OpenedNpc is not { Definition.Number: 385 } npc
            || npc.CurrentMap != player.CurrentMap || player.GetDistanceTo(npc) > 3)
        {
            return;
        }

        await this._enterAction.TryEnterMiniGameAsync(player, MiniGameType.IllusionTemple, level, slot).ConfigureAwait(false);
        if (player.CurrentMiniGame is IllusionTempleContext)
        {
            player.OpenedNpc = null;
            await player.InvokeViewPlugInAsync<IShowMiniGameEnterResultPlugIn>(view => view.ShowResultAsync(MiniGameType.IllusionTemple, EnterResult.Success)).ConfigureAwait(false);
        }
    }
}

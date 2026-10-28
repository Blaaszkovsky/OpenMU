// <copyright file="IllusionTemplePlugIn.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameLogic.MiniGames.IllusionTemple;

using System.Runtime.InteropServices;
using MUnique.OpenMU.GameLogic.NPC;
using MUnique.OpenMU.GameLogic.PlugIns;
using MUnique.OpenMU.GameLogic.Views.NPC;
using MUnique.OpenMU.PlugIns;

/// <summary>Connects normal NPC interaction, deaths, and team respawn to the match.</summary>
[PlugIn]
[Guid("A650E9C2-236C-4932-82D2-E513AF16AB06")]
[Display(Name = nameof(IllusionTempleResources.FeatureName), Description = nameof(IllusionTempleResources.FeatureDescription), ResourceType = typeof(IllusionTempleResources))]
public sealed class IllusionTemplePlugIn : IPlayerTalkToNpcPlugIn, IPlayerSpawnGateSelectionPlugIn, IAttackableGotKilledPlugIn, IPlayerStateChangedPlugIn
{
    /// <inheritdoc />
    public async ValueTask PlayerTalksToNpcAsync(Player player, NonPlayerCharacter npc, NpcTalkEventArgs eventArgs)
    {
        if (npc.CurrentMap != player.CurrentMap || player.GetDistanceTo(npc) > 3 || !player.IsAlive)
        {
            return;
        }

        if (npc.Definition.Number == 385)
        {
            var counts = Enumerable.Range(1, 6).Select(level => (byte)Math.Clamp(
                player.GameContext.MiniGames.GetRunningMiniGames(MiniGameType.IllusionTemple).FirstOrDefault(game => game.Definition.GameLevel == level)?.PlayerCount ?? 0,
                0,
                10)).ToArray();
            await player.InvokeViewPlugInAsync<IOpenNpcWindowPlugIn>(view => view.OpenNpcWindowAsync(NpcWindow.IllusionTemple)).ConfigureAwait(false);
            await player.InvokeViewPlugInAsync<IIllusionTempleViewPlugIn>(view => view.ShowPlayerCountsAsync(counts)).ConfigureAwait(false);
            eventArgs.HasBeenHandled = true;
            eventArgs.LeavesDialogOpen = true;
        }
        else if (npc.Definition.Number is 380 or 383 or 384 && player.CurrentMiniGame is IllusionTempleContext game)
        {
            eventArgs.HasBeenHandled = true;
            await game.TalkToEventNpcAsync(player, npc).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public ValueTask SelectSpawnGateAsync(Player player, SpawnGateSelectionArgs args)
    {
        if (args.Gate is null && !player.IsAlive && player.CurrentMiniGame is IllusionTempleContext game)
        {
            args.Gate = game.GetTeamSpawnGate(player);
        }

        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask AttackableGotKilledAsync(IAttackable killed, IAttacker? killer) => killed is Player { CurrentMiniGame: IllusionTempleContext game } player
        ? game.PlayerKilledAsync(player, killer) : ValueTask.CompletedTask;

    /// <inheritdoc />
    public async ValueTask PlayerStateChangedAsync(Player player, State previousState, State currentState)
    {
        if (currentState != PlayerState.EnteredWorld || player.CurrentMiniGame is IllusionTempleContext)
        {
            return;
        }

        foreach (var item in player.Inventory?.Items.Where(item => item.Definition is { Group: 14, Number: 64 }).ToArray() ?? [])
        {
            await player.DestroyInventoryItemAsync(item).ConfigureAwait(false);
        }
    }
}

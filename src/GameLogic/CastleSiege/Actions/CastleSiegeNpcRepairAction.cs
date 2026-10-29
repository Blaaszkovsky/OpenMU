// <copyright file="CastleSiegeNpcRepairAction.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameLogic.CastleSiege.Actions;

using MUnique.OpenMU.GameLogic.CastleSiege.NPC;
using MUnique.OpenMU.GameLogic.Views.CastleSiege;
using MUnique.OpenMU.Persistence;

/// <summary>
/// Repairs Castle Siege gates and Guardian Statues.
/// </summary>
public static class CastleSiegeNpcRepairAction
{
    /// <summary>
    /// Tries to restore a defense structure to full health.
    /// </summary>
    /// <param name="player">The requesting player.</param>
    /// <param name="context">The Castle Siege context.</param>
    /// <param name="npcNumber">The target NPC number.</param>
    /// <param name="npcIndex">The target NPC instance identifier.</param>
    /// <returns>The operation result.</returns>
    public static async ValueTask<CastleSiegeNpcOperationResult> RepairAsync(
        Player player,
        CastleSiegeContext? context,
        uint npcNumber,
        uint npcIndex)
    {
        var (result, currentHealth, maximumHealth) = await RepairCoreAsync(
                player,
                context,
                npcNumber,
                npcIndex)
            .ConfigureAwait(false);
        await player.InvokeViewPlugInAsync<ICastleSiegeNpcOperationResultPlugIn>(
                view => view.ShowRepairResultAsync(
                    result,
                    npcNumber,
                    npcIndex,
                    currentHealth,
                    maximumHealth))
            .ConfigureAwait(false);
        return result;
    }

    private static async ValueTask<(
        CastleSiegeNpcOperationResult Result,
        int CurrentHealth,
        int MaximumHealth)> RepairCoreAsync(
        Player player,
        CastleSiegeContext? context,
        uint npcNumber,
        uint npcIndex)
    {
        if (context is not { Configuration.Enabled: true })
        {
            return (CastleSiegeNpcOperationResult.Failed, 0, 0);
        }

        await context.ExecutionLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (context.CurrentState == CastleSiegeState.Start)
            {
                return (CastleSiegeNpcOperationResult.Failed, 0, 0);
            }

            if (!await CastleSiegeNpcAuthorization.IsOwnerOfficerAsync(player, context).ConfigureAwait(false))
            {
                return (CastleSiegeNpcOperationResult.NotAuthorized, 0, 0);
            }

            if (context.NpcController.FindDefenseStructure(npcNumber, npcIndex) is not { IsAlive: true } runtime
                || runtime.PersistedState is not { } state)
            {
                return (CastleSiegeNpcOperationResult.Failed, 0, 0);
            }

            context.NpcController.SynchronizeNpcStates();
            var structure = runtime.SpawnedInstance as CastleSiegeAttackableNpc;
            var maximumHealth = structure?.MaximumHealth ?? context.NpcController.GetMaximumHealth(runtime);
            var currentHealth = structure?.Health ?? state.CurrentHp;
            if (maximumHealth <= 0)
            {
                return (CastleSiegeNpcOperationResult.Failed, currentHealth, maximumHealth);
            }

            var missingHealth = Math.Max(0L, (long)maximumHealth - currentHealth);
            var cost = runtime.Definition.MonsterDefinition?.Number switch
            {
                var number when number == CastleSiegeGate.MonsterNumber =>
                    (missingHealth * context.Configuration.GateRepairCostPerHealthPoint)
                    + ((long)state.DefenseLevel * context.Configuration.RepairCostPerUpgradeLevel),
                var number when number == CastleSiegeStatue.MonsterNumber =>
                    (missingHealth * context.Configuration.StatueRepairCostPerHealthPoint)
                    + ((long)(state.DefenseLevel + state.RegenLevel) * context.Configuration.RepairCostPerUpgradeLevel),
                _ => -1,
            };
            if (cost is < 0 or > int.MaxValue)
            {
                return (CastleSiegeNpcOperationResult.Failed, currentHealth, maximumHealth);
            }

            var previousMoney = player.Money;
            if (!player.TryRemoveMoney((int)cost))
            {
                return (CastleSiegeNpcOperationResult.InsufficientMoney, currentHealth, maximumHealth);
            }

            var previousAlive = runtime.IsAlive;
            state.CurrentHp = maximumHealth;
            try
            {
                await context.SaveNpcStateMutationAsync(runtime).ConfigureAwait(false);
            }
            catch (PersistenceConflictException)
            {
                // Only a known noncommitting rejection permits refund; an uncertain save must be reconciled.
                state.CurrentHp = currentHealth;
                runtime.IsAlive = previousAlive;
                player.Money = previousMoney;
                throw;
            }

            runtime.IsAlive = true;
            structure?.RestoreFullHealth();

            return (CastleSiegeNpcOperationResult.Success, state.CurrentHp, maximumHealth);
        }
        finally
        {
            context.ExecutionLock.Release();
        }
    }
}

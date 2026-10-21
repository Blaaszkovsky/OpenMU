// <copyright file="FereaEncounterPlugIn.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameLogic.Ferea;

using System.Collections.Concurrent;
using System.ComponentModel;
using System.Runtime.InteropServices;
using MUnique.OpenMU.GameLogic.PlugIns;
using MUnique.OpenMU.PlugIns;

/// <summary>
/// Runs a separate, configurable Ferea encounter for each game context hosting its map.
/// </summary>
[PlugIn]
[Display(Name = nameof(FereaResources.Name), Description = nameof(FereaResources.Description), ResourceType = typeof(FereaResources))]
[Guid("8D7152E4-65FB-4F53-A9F3-B38D42C2D37A")]
public sealed class FereaEncounterPlugIn : IFeaturePlugIn, IPeriodicTaskPlugIn, IWarpGateEnteringPlugIn,
    IAttackableMovedPlugIn, IAttackableGotKilledPlugIn, IObjectRemovedFromMapPlugIn,
    ISupportCustomConfiguration<FereaEncounterConfiguration>, ISupportDefaultCustomConfiguration
{
    private readonly ConcurrentDictionary<GameContext, FereaEncounter> _encounters = new();

    /// <inheritdoc />
    public FereaEncounterConfiguration? Configuration { get; set; }

    /// <inheritdoc />
    public object CreateDefaultConfig() => new FereaEncounterConfiguration();

    /// <summary>
    /// Gets the live encounter for diagnostics, without changing persisted configuration.
    /// </summary>
    /// <param name="gameContext">The game server.</param>
    /// <returns>The encounter, if initialized.</returns>
    public FereaEncounter? GetEncounter(GameContext gameContext) => this._encounters.GetValueOrDefault(gameContext);

    /// <inheritdoc />
    public async ValueTask ExecuteTaskAsync(GameContext gameContext)
    {
        var configuration = this.Configuration ??= new FereaEncounterConfiguration();
        if (!gameContext.Configuration.Maps.Any(m => m.Number == configuration.MapNumber))
        {
            return;
        }

        try
        {
            var encounter = this._encounters.GetOrAdd(gameContext, context => new FereaEncounter(context, configuration));
            await encounter.TickAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            gameContext.LoggerFactory.CreateLogger<FereaEncounterPlugIn>().LogError(exception, "Ferea encounter update failed.");
        }
    }

    /// <inheritdoc />
    public void ForceStart()
    {
        // Encounter activation is deliberately owned by the Crystal Orb, not the periodic-task command.
    }

    /// <inheritdoc />
    public ValueTask WarpGateEnteringAsync(Player player, ExitGate targetGate, CancelEventArgs eventArgs)
    {
        if (player.GameContext is GameContext context && this._encounters.TryGetValue(context, out var encounter))
        {
            eventArgs.Cancel |= !encounter.CanEnter(player, targetGate);
        }

        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public void AttackableMoved(IAttackable attackable)
    {
        if (attackable is Player player && player.GameContext is GameContext context
            && this._encounters.TryGetValue(context, out var encounter)
            && (player.CurrentMap?.Definition.Number != encounter.Configuration.MapNumber || !encounter.Configuration.Contains(player.Position)))
        {
            encounter.Revoke(player);
        }
    }

    /// <inheritdoc />
    public ValueTask AttackableGotKilledAsync(IAttackable killed, IAttacker? killer)
    {
        if (killed is Player player)
        {
            this.Revoke(player);
        }

        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask ObjectRemovedFromMapAsync(GameMap map, ILocateable removedObject)
    {
        if (removedObject is Player player)
        {
            this.Revoke(player);
        }

        return ValueTask.CompletedTask;
    }

    private void Revoke(Player player)
    {
        if (player.GameContext is GameContext context && this._encounters.TryGetValue(context, out var encounter))
        {
            encounter.Revoke(player);
        }
    }
}

// <copyright file="InvasionLifecycleTests.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Tests;

using System.Reflection;
using MUnique.OpenMU.DataModel.Configuration;
using MUnique.OpenMU.GameLogic;
using MUnique.OpenMU.GameLogic.Attributes;
using MUnique.OpenMU.GameLogic.PlugIns.InvasionEvents;
using MUnique.OpenMU.Pathfinding;
using MUnique.OpenMU.PlugIns;
using BasicModel = MUnique.OpenMU.Persistence.BasicModel;

/// <summary>
/// Runs built-in invasion spawning and cleanup with real maps and monsters.
/// </summary>
[TestFixture]
public class InvasionLifecycleTests
{
    /// <summary>
    /// Every configured monster must spawn on a permitted map and be removed when the invasion ends.
    /// </summary>
    /// <param name="eventName">The invasion.</param>
    [TestCase("golden")]
    [TestCase("dragon")]
    [TestCase("wizard")]
    public async ValueTask DefaultInvasionSpawnsAndCleansUpAsync(string eventName)
    {
        BaseInvasionPlugIn<PeriodicInvasionConfiguration> invasion = eventName switch
        {
            "golden" => new GoldenInvasionPlugIn(),
            "dragon" => new RedDragonInvasionPlugIn(),
            _ => new WhiteWizardInvasionPlugIn(),
        };
        var configuration = (PeriodicInvasionConfiguration)((ISupportDefaultCustomConfiguration)invasion).CreateDefaultConfig();
        invasion.Configuration = configuration;
        await using var context = (GameContext)GameContextTestHelper.CreateGameContext();
        context.StopPeriodicTasks();
        foreach (var mapNumber in configuration.Mobs.SelectMany(mob => mob.MapIds).Distinct())
        {
            if (context.Configuration.Maps.All(map => map.Number != mapNumber))
            {
                context.Configuration.Maps.Add(new BasicModel.GameMapDefinition
                {
                    Number = checked((short)mapNumber),
                    Name = $"Map {mapNumber}",
                    TerrainData = new byte[ushort.MaxValue + 3],
                });
            }
        }

        foreach (var spawn in configuration.Mobs)
        {
            var monster = new BasicModel.MonsterDefinition
            {
                Id = Guid.NewGuid(),
                Number = checked((short)spawn.MonsterId),
                Designation = $"Monster {spawn.MonsterId}",
                ObjectKind = NpcObjectKind.Monster,
            };
            monster.Attributes.Add(new BasicModel.MonsterAttribute { AttributeDefinition = Stats.MaximumHealth, Value = 100 });
            context.Configuration.Monsters.Add(monster);
        }

        var getState = invasion.GetType().GetMethod("GetStateByGameContext", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var state = (InvasionGameServerState)getState.Invoke(invasion, new object[] { context })!;
        await InvokeLifecycleAsync(invasion, "OnPrepareEventAsync", state).ConfigureAwait(false);
        await InvokeLifecycleAsync(invasion, "OnPreparedAsync", state).ConfigureAwait(false);
        await InvokeLifecycleAsync(invasion, "OnStartedAsync", state).ConfigureAwait(false);

        var maps = new List<GameMap>();
        foreach (var mapNumber in state.MapIds)
        {
            maps.Add((await context.GetMapAsync(mapNumber).ConfigureAwait(false))!);
        }

        var monsters = maps.SelectMany(map => map.GetNpcsInRange(new Point(128, 128), 256)).ToArray();
        Assert.That(monsters.Length, Is.EqualTo(configuration.Mobs.Sum(spawn => spawn.Count)));
        foreach (var spawn in configuration.Mobs)
        {
            var matching = monsters.Where(monster => monster.Definition.Number == spawn.MonsterId).ToArray();
            Assert.That(matching, Has.Length.EqualTo(spawn.Count));
            Assert.That(matching.Select(monster => monster.CurrentMap!.Definition.Number).Distinct().Count(), Is.EqualTo(1));
            Assert.That(matching.All(monster => spawn.MapIds.Contains(checked((ushort)monster.CurrentMap!.Definition.Number))), Is.True);
        }

        if (configuration.ForceSingleMap)
        {
            Assert.That(maps, Has.Count.EqualTo(1), "The wizard and its helpers must share the chosen map.");
        }

        await InvokeLifecycleAsync(invasion, "OnFinishedAsync", state).ConfigureAwait(false);

        Assert.Multiple(() =>
        {
            Assert.That(monsters.All(monster => monster.IsDisposed), Is.True);
            Assert.That(maps.SelectMany(map => map.GetNpcsInRange(new Point(128, 128), 256)), Is.Empty);
            Assert.That(state.MapIds, Is.Empty);
            Assert.That(state.SelectedMaps, Is.Empty);
            Assert.That(state.AnnouncedMapIds, Is.Empty);
        });
    }

    private static async ValueTask InvokeLifecycleAsync(BaseInvasionPlugIn<PeriodicInvasionConfiguration> invasion, string method, InvasionGameServerState state)
    {
        var callback = typeof(BaseInvasionPlugIn<PeriodicInvasionConfiguration>).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!;
        await ((ValueTask)callback.Invoke(invasion, new object[] { state })!).ConfigureAwait(false);
    }
}

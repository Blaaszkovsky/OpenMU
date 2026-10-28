// <copyright file="IllusionTempleTests.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Tests.MiniGames;

using MUnique.OpenMU.DataModel.Configuration;
using MUnique.OpenMU.GameLogic;
using MUnique.OpenMU.GameLogic.MiniGames;
using MUnique.OpenMU.GameLogic.MiniGames.IllusionTemple;
using Microsoft.Extensions.Logging.Abstractions;
using ExitGate = MUnique.OpenMU.Persistence.BasicModel.ExitGate;
using MiniGameDefinition = MUnique.OpenMU.Persistence.BasicModel.MiniGameDefinition;

/// <summary>Exercises the real Illusion Temple factory and match rules.</summary>
[TestFixture]
public class IllusionTempleTests
{
    /// <summary>Players in the waiting room must not hurt each other.</summary>
    [Test]
    public async Task LobbyRejectsPlayerDamageAsync()
    {
        var player = await PlayerTestHelper.CreatePlayerAsync().ConfigureAwait(false);
        var other = await PlayerTestHelper.CreatePlayerAsync(player.GameContext).ConfigureAwait(false);
        var definition = new MiniGameDefinition { Entrance = new ExitGate { Map = player.CurrentMap!.Definition }, MaximumPlayerCount = 10 };
        var initializer = new MapInitializer(player.GameContext.Configuration, NullLogger<MapInitializer>.Instance, player.GameContext.DropGenerator, null);
        await using var game = new IllusionTempleContext(new MiniGameMapKey(0, 1, string.Empty), definition, player.GameContext, initializer);
        Assert.That(game.IsAttackAllowed(player, other), Is.False);
    }

    /// <summary>The separate BF event-skill handler is the only skill route in this match.</summary>
    [Test]
    public async Task NormalSkillRouteCannotBypassEventPointsAsync()
    {
        var player = await PlayerTestHelper.CreatePlayerAsync().ConfigureAwait(false);
        var definition = new MiniGameDefinition { Entrance = new ExitGate { Map = player.CurrentMap!.Definition }, MaximumPlayerCount = 10 };
        var initializer = new MapInitializer(player.GameContext.Configuration, NullLogger<MapInitializer>.Instance, player.GameContext.DropGenerator, null);
        await using var game = new IllusionTempleContext(new MiniGameMapKey(0, 1, string.Empty), definition, player.GameContext, initializer);
        foreach (var number in new short[] { 9, 210, 211, 212, 213 })
        {
            Assert.That(game.IsSkillAllowed(new Skill { Number = number }, player, player), Is.False);
        }
    }

    /// <summary>The configured event must use its match implementation, including consequence-free PvP.</summary>
    [Test]
    public async Task FactoryCreatesPlayableIllusionTempleAsync()
    {
        var player = await PlayerTestHelper.CreatePlayerAsync().ConfigureAwait(false);
        ((GameContext)player.GameContext).StopPeriodicTasks();
        var definition = new MiniGameDefinition
        {
            Type = MiniGameType.IllusionTemple,
            GameLevel = 1,
            MaximumPlayerCount = 10,
            MapCreationPolicy = MiniGameMapCreationPolicy.Shared,
            Entrance = new ExitGate { Map = player.CurrentMap!.Definition },
            EnterDuration = TimeSpan.FromMinutes(5),
            GameDuration = TimeSpan.FromMinutes(15),
            ExitDuration = TimeSpan.FromMinutes(1),
        };
        await using var game = await player.GameContext.MiniGames.GetOrCreateAsync(definition, player).ConfigureAwait(false);
        Assert.Multiple(() =>
        {
            Assert.That(game.GetType().Name, Is.EqualTo("IllusionTempleContext"));
            Assert.That(game.AllowPlayerKilling, Is.True);
        });
    }
}

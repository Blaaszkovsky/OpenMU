// <copyright file="BotMiniGameNavigationTest.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Tests;

using System.Reflection;
using System.Threading;
using Moq;
using MUnique.OpenMU.DataModel.Configuration;
using MUnique.OpenMU.GameLogic;
using MUnique.OpenMU.GameLogic.Attributes;
using MUnique.OpenMU.GameLogic.Bots;
using MUnique.OpenMU.GameLogic.NPC;
using MUnique.OpenMU.Pathfinding;

/// <summary>
/// Tests movement from an event entrance with an opponent already in combat range.
/// </summary>
[TestFixture]
public class BotMiniGameNavigationTest
{
    /// <summary>
    /// Nearby opposition must not strand a bot inside the entrance safezone, where attacks are refused.
    /// </summary>
    /// <param name="botInSafezone">Whether the bot starts on a safe tile.</param>
    /// <param name="targetInSafezone">Whether the opponent is on a safe tile.</param>
    /// <param name="shouldMove">Whether the event navigation must leave its current tile.</param>
    [TestCase(true, false, true)]
    [TestCase(false, false, false)]
    [TestCase(true, true, false)]
    public async Task NearbyOpponentDoesNotStrandBotInEntranceSafezoneAsync(bool botInSafezone, bool targetInSafezone, bool shouldMove)
    {
        var gameContext = GameContextTestHelper.CreateGameContext();
        var bot = await PlayerTestHelper.CreateOfflineLevelingPlayerAsync(gameContext).ConfigureAwait(false);
        await using var navigator = new BotNavigator(bot);
        var definition = new MUnique.OpenMU.Persistence.BasicModel.GameMapDefinition
        {
            Id = Guid.NewGuid(),
            Number = 52,
            TerrainData = new byte[ushort.MaxValue + 3],
        };
        var map = new GameMap(definition, TimeSpan.FromMinutes(1), 8);
        map.Terrain.ApplyTerrainAttribute(13, 13, TerrainAttributeType.Safezone, botInSafezone);
        bot.SetCurrentMapSilently(map);
        bot.Position = new Point(13, 13);
        await map.AddAsync(bot).ConfigureAwait(false);

        var monsterDefinitionMock = new Mock<MonsterDefinition>();
        monsterDefinitionMock.SetupGet(entry => entry.Attributes).Returns(
            [new MonsterAttribute { AttributeDefinition = Stats.MaximumHealth, Value = 100 }]);
        var monsterDefinition = monsterDefinitionMock.Object;
        monsterDefinition.ObjectKind = NpcObjectKind.Monster;
        var spawn = new MonsterSpawnArea
        {
            GameMap = definition,
            MonsterDefinition = monsterDefinition,
            X1 = 13,
            X2 = 13,
            Y1 = 17,
            Y2 = 17,
            Quantity = 1,
        };
        await using var monster = new Monster(spawn, monsterDefinition, map, NullDropGenerator.Instance,
            new Mock<INpcIntelligence>().Object, gameContext.PlugInManager, gameContext.PathFinderPool);
        monster.Initialize();
        monster.Attributes[Stats.CurrentHealth] = 100;
        await map.AddAsync(monster).ConfigureAwait(false);
        map.Terrain.ApplyTerrainAttribute(13, 17, TerrainAttributeType.Safezone, targetInSafezone);
        Assert.That(map.GetAttackablesInRange(bot.Position, 6), Does.Contain(monster));

        var evaluation = typeof(BotNavigator).GetMethod("EvaluateInsideMiniGameAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        await ((ValueTask)evaluation.Invoke(navigator, [map, CancellationToken.None])!).ConfigureAwait(false);

        Assert.That(bot.IsWalking || bot.Position != new Point(13, 13), Is.EqualTo(shouldMove));
        await bot.StopWalkingAsync().ConfigureAwait(false);
    }
}

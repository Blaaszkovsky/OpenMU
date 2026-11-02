// <copyright file="IllusionTempleDataTest.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Persistence.Initialization.Tests;

using Microsoft.Extensions.Logging.Abstractions;
using MUnique.OpenMU.DataModel.Configuration;
using MUnique.OpenMU.GameLogic.PlugIns.PeriodicTasks;
using MUnique.OpenMU.PlugIns;
using MUnique.OpenMU.Persistence.Initialization.Updates;
using MUnique.OpenMU.Persistence.InMemory;

/// <summary>
/// Tests the data of the illusion temple event.
/// </summary>
/// <remarks>
/// The event configuration reaches a server through two independent paths: the data initialization of a
/// fresh database, and the update plug-in for a database which was created before the event existed. A
/// freshly initialized database marks every known update as installed, so the update plug-in never runs
/// there - which means the two have to be asserted to agree, or one of them silently rots.
/// </remarks>
[TestFixture]
internal class IllusionTempleDataTest
{
    private const short MirageNumber = 385;
    private const short StoneStatueNumber = 380;
    private const short AllianceGuardianNumber = 381;
    private const short IllusionGuardianNumber = 382;
    private const short AllianceItemStorageNumber = 383;
    private const short IllusionItemStorageNumber = 384;
    private const short DeviasNumber = 2;
    private const short ProtectionEffectNumber = 210;
    private const short RestraintEffectNumber = 211;

    private static readonly short[] TempleMapNumbers = [45, 46, 47, 48, 49, 50];

    /// <summary>
    /// Tests that a newly initialized season 6 database contains the illusion temple event.
    /// </summary>
    /// <returns>The task.</returns>
    [Test]
    public async Task NewDatabaseContainsEventAsync()
    {
        var gameConfiguration = await CreateSeason6ConfigurationAsync().ConfigureAwait(false);

        AssertEventData(gameConfiguration);
        Assert.That(GetTempleDefinitions(gameConfiguration).Select(definition => definition.MinimumPlayerCount), Has.All.EqualTo(4));
    }

    /// <summary>
    /// Tests that the update adds the illusion temple event to a database which was created before it
    /// existed, and that applying it twice doesn't duplicate anything.
    /// </summary>
    /// <returns>The task.</returns>
    [Test]
    public async Task UpdateAddsEventToExistingDatabaseAsync()
    {
        var contextProvider = new InMemoryPersistenceContextProvider();
        var dataInitialization = new VersionSeasonSix.DataInitialization(contextProvider, new NullLoggerFactory());
        await dataInitialization.CreateInitialDataAsync(1, true).ConfigureAwait(false);

        using var context = contextProvider.CreateNewContext();
        var gameConfiguration = (await context.GetAsync<GameConfiguration>().ConfigureAwait(false)).First();

        RevertToStateBeforeTheEvent(gameConfiguration);

        var update = new AddIllusionTempleDataUpdatePlugIn();
        await update.ApplyUpdateAsync(context, gameConfiguration).ConfigureAwait(false);
        await update.ApplyUpdateAsync(context, gameConfiguration).ConfigureAwait(false);

        AssertEventData(gameConfiguration);
    }

    /// <summary>
    /// Tests that the update brings the minimum player count of an already configured event up to a
    /// usable value. The field was added to <see cref="MiniGameDefinition"/> by this event, so it
    /// reads as 0 on every database which got the definitions from an earlier version of the update.
    /// </summary>
    /// <returns>The task.</returns>
    [Test]
    public async Task UpdateFixesMissingMinimumPlayerCountAsync()
    {
        var contextProvider = new InMemoryPersistenceContextProvider();
        var dataInitialization = new VersionSeasonSix.DataInitialization(contextProvider, new NullLoggerFactory());
        await dataInitialization.CreateInitialDataAsync(1, true).ConfigureAwait(false);

        using var context = contextProvider.CreateNewContext();
        var gameConfiguration = (await context.GetAsync<GameConfiguration>().ConfigureAwait(false)).First();
        foreach (var definition in GetTempleDefinitions(gameConfiguration))
        {
            definition.MinimumPlayerCount = 0;
        }

        await new AddIllusionTempleDataUpdatePlugIn().ApplyUpdateAsync(context, gameConfiguration).ConfigureAwait(false);

        Assert.That(GetTempleDefinitions(gameConfiguration), Has.All.Matches<MiniGameDefinition>(d => d.MinimumPlayerCount > 0));
        Assert.That(GetTempleDefinitions(gameConfiguration), Has.Count.EqualTo(TempleMapNumbers.Length), "The update must not duplicate the existing definitions.");
    }

    /// <summary>An upgrade inherits the owner's existing minimum and a second application preserves all values.</summary>
    [Test]
    public async Task UpdatePreservesSerializedOwnerMinimumAndExplicitDefinitionAsync()
    {
        var provider = new InMemoryPersistenceContextProvider();
        await new VersionSeasonSix.DataInitialization(provider, new NullLoggerFactory()).CreateInitialDataAsync(1, true).ConfigureAwait(false);
        using var context = provider.CreateNewContext();
        var configuration = (await context.GetAsync<GameConfiguration>().ConfigureAwait(false)).Single();
        var plugin = configuration.PlugInConfigurations.Single(entry => entry.TypeId == typeof(IllusionTempleStartPlugIn).GUID);
        var settings = IllusionTempleStartConfiguration.Default;
        settings.MinimumPlayers = 6;
        plugin.SetConfiguration(settings, null);
        var originalJson = plugin.CustomConfiguration;
        var definitions = GetTempleDefinitions(configuration);
        foreach (var definition in definitions)
        {
            definition.MinimumPlayerCount = 0;
        }

        definitions[0].MinimumPlayerCount = 7;
        var update = new AddIllusionTempleDataUpdatePlugIn();
        await update.ApplyUpdateAsync(context, configuration).ConfigureAwait(false);
        await update.ApplyUpdateAsync(context, configuration).ConfigureAwait(false);
        Assert.Multiple(() =>
        {
            Assert.That(definitions[0].MinimumPlayerCount, Is.EqualTo(7));
            Assert.That(definitions.Skip(1).Select(definition => definition.MinimumPlayerCount), Has.All.EqualTo(6));
            Assert.That(plugin.CustomConfiguration, Is.EqualTo(originalJson));
            Assert.That(update.Version, Is.EqualTo(2));
        });
    }

    private static async Task<GameConfiguration> CreateSeason6ConfigurationAsync()
    {
        var contextProvider = new InMemoryPersistenceContextProvider();
        var dataInitialization = new VersionSeasonSix.DataInitialization(contextProvider, new NullLoggerFactory());
        await dataInitialization.CreateInitialDataAsync(1, true).ConfigureAwait(false);

        using var context = contextProvider.CreateNewContext();
        return (await context.GetAsync<GameConfiguration>().ConfigureAwait(false)).First();
    }

    private static List<MiniGameDefinition> GetTempleDefinitions(GameConfiguration gameConfiguration)
    {
        return gameConfiguration.MiniGameDefinitions.Where(d => d.Type == MiniGameType.IllusionTemple).ToList();
    }

    /// <summary>
    /// Removes everything the update plug-in is responsible for, so that the configuration looks like
    /// one of a server which was installed before the event existed.
    /// </summary>
    /// <param name="gameConfiguration">The game configuration to revert.</param>
    private static void RevertToStateBeforeTheEvent(GameConfiguration gameConfiguration)
    {
        foreach (var definition in GetTempleDefinitions(gameConfiguration))
        {
            gameConfiguration.MiniGameDefinitions.Remove(definition);
        }

        gameConfiguration.Items.Remove(gameConfiguration.Items.Single(item => item is { Group: 14, Number: 64 }));

        foreach (var monster in gameConfiguration.Monsters.Where(monster => monster.Number is >= 386 and <= 399).ToList())
        {
            gameConfiguration.Monsters.Remove(monster);
        }

        gameConfiguration.Monsters.Single(monster => monster.Number == MirageNumber).NpcWindow = NpcWindow.Undefined;

        foreach (var effect in gameConfiguration.MagicEffects
                     .Where(effect => effect.Number is ProtectionEffectNumber or RestraintEffectNumber).ToList())
        {
            gameConfiguration.MagicEffects.Remove(effect);
        }

        foreach (var map in gameConfiguration.Maps.Where(map => TempleMapNumbers.Contains(map.Number)))
        {
            // The maps used to point at themselves as their own safezone.
            map.SafezoneMap = map;

            foreach (var spawn in map.MonsterSpawns.ToList())
            {
                map.MonsterSpawns.Remove(spawn);
            }

            // The arena used to be littered with "Captured Stone Statue" spawns, which were based on
            // the wrong NPC numbers and had no function.
            (short Number, byte X, byte Y)[] legacySpawns =
            [
                (658, 169, 85), (659, 136, 101), (660, 151, 119), (661, 150, 88),
                (662, 165, 102), (663, 173, 67), (664, 187, 81), (665, 187, 51),
                (666, 203, 67), (667, 133, 121), (668, 206, 48),
            ];
            foreach (var legacy in legacySpawns)
            {
                var area = new MUnique.OpenMU.Persistence.BasicModel.MonsterSpawnArea
                {
                    GameMap = map,
                    MonsterDefinition = gameConfiguration.Monsters.Single(monster => monster.Number == legacy.Number),
                    Quantity = 1,
                    SpawnTrigger = SpawnTrigger.AutomaticDuringEvent,
                    X1 = legacy.X, X2 = legacy.X,
                    Y1 = legacy.Y, Y2 = legacy.Y,
                };
                map.MonsterSpawns.Add(area);
            }
        }
    }

    private static void AssertEventData(GameConfiguration gameConfiguration)
    {
        AssertItems(gameConfiguration);
        AssertDefinitions(gameConfiguration);
        AssertNpcsAndEffects(gameConfiguration);
        AssertMaps(gameConfiguration);
    }

    private static void AssertItems(GameConfiguration gameConfiguration)
    {
        // Preserve the fork's persisted ticket definition IDs and names; no owned item is renumbered.
        var ticket = gameConfiguration.Items.Single(item => item is { Group: 13, Number: 51 });
        Assert.That(ticket.Name.ValueInNeutralLanguage, Is.EqualTo("Scroll of Blood"));
        var scrollOfBlood = gameConfiguration.Items.Single(item => item is { Group: 13, Number: 50 });
        Assert.That(scrollOfBlood.Name.ValueInNeutralLanguage, Is.EqualTo("Illusion Sorcerer Covenant"));

        // The sacred relic, which the game logic looks up by group and number.
        Assert.That(gameConfiguration.Items.Count(item => item is { Group: 14, Number: 64 }), Is.EqualTo(1), "sacred relic");
    }

    private static void AssertDefinitions(GameConfiguration gameConfiguration)
    {
        var definitions = GetTempleDefinitions(gameConfiguration);
        var ticket = gameConfiguration.Items.Single(item => item is { Group: 13, Number: 51 });

        Assert.That(definitions, Has.Count.EqualTo(TempleMapNumbers.Length));
        Assert.That(definitions.Select(d => (short)d.GameLevel), Is.EquivalentTo(new short[] { 1, 2, 3, 4, 5, 6 }));
        Assert.That(definitions.Select(d => d.Entrance?.Map?.Number), Is.EquivalentTo(TempleMapNumbers.Select(n => (short?)n)));
        Assert.That(definitions, Has.All.Matches<MiniGameDefinition>(d => d.TicketItem == ticket));
        Assert.That(definitions, Has.All.Matches<MiniGameDefinition>(d => d.MapCreationPolicy == MiniGameMapCreationPolicy.Shared));

        // The event is team based and scores per team, so parties are not allowed, and it needs at
        // least two players to have two teams at all.
        Assert.That(definitions, Has.All.Matches<MiniGameDefinition>(d => !d.AllowParty));
        Assert.That(definitions, Has.All.Matches<MiniGameDefinition>(d => d.MinimumPlayerCount >= 2));
        Assert.That(definitions, Has.All.Matches<MiniGameDefinition>(d => d.MaximumPlayerCount >= d.MinimumPlayerCount));

        // Each temple covers its own level range, and the sixth one is the master class temple.
        Assert.That(definitions.Where(d => d.GameLevel < 6), Has.All.Matches<MiniGameDefinition>(d => d.MaximumCharacterLevel > d.MinimumCharacterLevel));
        Assert.That(definitions.Single(d => d.GameLevel == 6).RequiresMasterClass, Is.True);

        // Every temple rewards the winning team with experience.
        Assert.That(
            definitions,
            Has.All.Matches<MiniGameDefinition>(d => d.Rewards.Any(r => r.RewardType == MiniGameRewardType.Experience)));
    }

    private static void AssertNpcsAndEffects(GameConfiguration gameConfiguration)
    {
        Assert.That(
            gameConfiguration.Monsters.Single(monster => monster.Number == MirageNumber).NpcWindow,
            Is.EqualTo(NpcWindow.Undefined), "The canonical NPC plugin opens the dialog and reports six player counts.");

        Assert.That(
            gameConfiguration.Monsters.Where(monster => monster.Number is >= 386 and <= 399).Select(monster => monster.Number),
            Is.EquivalentTo(Enumerable.Range(386, 14).Select(n => (short)n)),
            "the roaming arena monsters of all temples");

        // The two special skills which apply a lasting effect need their magic effect definitions;
        // Tracking and Weaken act instantly and have none.
        foreach (var effectNumber in new[] { ProtectionEffectNumber, RestraintEffectNumber })
        {
            Assert.That(gameConfiguration.MagicEffects.Count(effect => effect.Number == effectNumber), Is.EqualTo(1), $"magic effect {effectNumber}");
        }
    }

    private static void AssertMaps(GameConfiguration gameConfiguration)
    {
        var temples = gameConfiguration.Maps.Where(map => TempleMapNumbers.Contains(map.Number)).ToList();
        Assert.That(temples, Has.Count.EqualTo(TempleMapNumbers.Length));

        // A player who leaves the arena has to end up outside of it, not back inside.
        Assert.That(temples.Select(map => map.SafezoneMap?.Number), Has.All.EqualTo(DeviasNumber));

        foreach (var map in temples)
        {
            Assert.That(
                map.MonsterSpawns.Any(spawn => spawn.MonsterDefinition?.Number is >= 658 and <= 668),
                Is.False,
                $"map {map.Number} still carries the obsolete statue spawns");

            // The canonical channel engine starts with both configured statues and restores them after each score.
            var statueSpawns = map.MonsterSpawns.Where(spawn => spawn.MonsterDefinition?.Number == StoneStatueNumber).ToList();
            Assert.That(statueSpawns, Has.Count.EqualTo(2), $"map {map.Number}: stone statues");
            Assert.That(statueSpawns, Has.All.Matches<MonsterSpawnArea>(spawn => spawn.SpawnTrigger == SpawnTrigger.AutomaticDuringEvent));

            foreach (var npcNumber in new[] { AllianceGuardianNumber, IllusionGuardianNumber, AllianceItemStorageNumber, IllusionItemStorageNumber })
            {
                Assert.That(
                    map.MonsterSpawns.Count(spawn => spawn.MonsterDefinition?.Number == npcNumber),
                    Is.EqualTo(1),
                    $"map {map.Number}: NPC {npcNumber}");
            }
        }

        Assert.That(temples.SelectMany(map => map.MonsterSpawns)
            .Any(spawn => spawn.MonsterDefinition?.Number is >= 386 and <= 399), Is.False,
            "The canonical fork maps retain their configured PvP event NPCs; public roaming PvE spawns are not installed.");
    }
}

// <copyright file="CastleSiegeSignOfLordQualificationTests.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Tests;

using System.Collections.Immutable;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using MUnique.OpenMU.DataModel.Configuration;
using MUnique.OpenMU.DataModel.Entities;
using MUnique.OpenMU.GameLogic;
using MUnique.OpenMU.GameLogic.Attributes;
using MUnique.OpenMU.GameLogic.CastleSiege;
using MUnique.OpenMU.GameLogic.CastleSiege.Actions;
using MUnique.OpenMU.GameServer;
using MUnique.OpenMU.Interfaces;
using MUnique.OpenMU.Persistence.InMemory;
using MUnique.OpenMU.PlugIns;
using BasicModel = MUnique.OpenMU.Persistence.BasicModel;
using RuntimeGuild = MUnique.OpenMU.Interfaces.Guild;

/// <summary>
/// Verifies that a default-generated Sign of Lord permits normal donation and guild selection.
/// </summary>
[TestFixture]
public class CastleSiegeSignOfLordQualificationTests
{
    /// <summary>
    /// A twenty-member guild must submit the generated item before it can receive an attacking side.
    /// </summary>
    [Test]
    public async Task DefaultGeneratedSignQualifiesRegisteredTwentyMemberGuildAsync()
    {
        const uint runtimeGuildId = 42;
        const string guildName = "Qualify";
        const string masterName = "LordMaster";
        var persistence = new InMemoryPersistenceContextProvider();
        await new MUnique.OpenMU.Persistence.Initialization.VersionSeasonSix.DataInitialization(persistence, NullLoggerFactory.Instance)
            .CreateInitialDataAsync(1, true).ConfigureAwait(false);
        using var initializationContext = persistence.CreateNewContext();
        var defaults = (await initializationContext.GetAsync<GameConfiguration>().ConfigureAwait(false)).Single();
        var siege = defaults.CastleSiegeConfiguration!;
        Assert.Multiple(() =>
        {
            Assert.That(siege.RegisterMinMembers, Is.EqualTo(20));
            Assert.That(siege.MaxAttackingGuilds, Is.EqualTo(3));
        });
        var source = defaults.DropItemGroups.SingleOrDefault(group => group.ItemLevel == siege.SignOfLordItemLevel
            && group.PossibleItems.Contains(siege.SignOfLordItemDefinition!));
        Assert.That(source, Is.Not.Null, "The default registration must have a reachable drop source.");
        var generated = new DefaultDropGenerator(defaults, new Mock<IRandomizer>().Object).GenerateItemDrop(source!);
        Assert.That(generated, Is.Not.Null);
        var guild = initializationContext.CreateNew<MUnique.OpenMU.DataModel.Entities.Guild>();
        guild.Name = guildName;
        await initializationContext.SaveChangesAsync().ConfigureAwait(false);
        var members = Enumerable.Range(0, 20).Select(index => new GuildListEntry
        {
            PlayerName = index == 0 ? masterName : $"Member{index}",
            PlayerPosition = index == 0 ? GuildPosition.GuildMaster : GuildPosition.NormalMember,
        }).ToImmutableList();
        var guildServer = new Mock<IGuildServer>();
        guildServer.Setup(server => server.GetGuildAsync(runtimeGuildId)).ReturnsAsync(new RuntimeGuild { Name = guildName });
        guildServer.Setup(server => server.GetGuildListAsync(runtimeGuildId)).ReturnsAsync(members);
        guildServer.Setup(server => server.GetPersistentGuildIdAsync(runtimeGuildId)).ReturnsAsync(guild.Id);
        guildServer.Setup(server => server.GetGuildIdAsync(guild.Id)).ReturnsAsync(runtimeGuildId);
        guildServer.Setup(server => server.GetAllianceGuildsAsync(runtimeGuildId)).ReturnsAsync(ImmutableList<AllianceGuildEntry>.Empty);

        // Reuse the ordinary player helper's small walkable map; this test covers registration, not event combat.
        var configuration = PlayerTestHelper.CreateGameConfiguration();
        configuration.CastleSiegeConfiguration = siege;
        var mapInitializer = new MapInitializer(configuration, NullLogger<MapInitializer>.Instance, NullDropGenerator.Instance, null);
        await using var server = new GameServerContext(
            new BasicModel.GameServerDefinition { GameConfiguration = configuration, ServerConfiguration = new BasicModel.GameServerConfiguration() },
            guildServer.Object,
            new Mock<IEventPublisher>().Object,
            new Mock<ILoginServer>().Object,
            new Mock<IFriendServer>().Object,
            persistence,
            mapInitializer,
            NullLoggerFactory.Instance,
            new PlugInManager([], NullLoggerFactory.Instance, null, null),
            NullDropGenerator.Instance,
            new ConfigurationChangeMediator());
        mapInitializer.PlugInManager = server.PlugInManager;
        mapInitializer.PathFinderPool = server.PathFinderPool;
        await using var player = await PlayerTestHelper.CreatePlayerAsync(server).ConfigureAwait(false);
        player.SelectedCharacter!.Name = masterName;
        player.GuildStatus = new GuildMemberStatus(runtimeGuildId, GuildPosition.GuildMaster);
        player.Attributes![Stats.Level] = 400;
        await server.AddPlayerAsync(player).ConfigureAwait(false);
        var context = new CastleSiegeContext(server, siege);
        await context.InitializeAsync(new DateTime(2026, 8, 3, 12, 0, 0, DateTimeKind.Utc)).ConfigureAwait(false);
        await new CastleSiegeRegisterGuildAction().RegisterAsync(player, context).ConfigureAwait(false);
        Assert.That(context.RegisteredGuilds[guild.Id].Marks, Is.Zero);
        await CastleSiegeGuildSelector.SelectGuildsAsync(context).ConfigureAwait(false);
        Assert.That(context.FinalGuildList, Is.Empty, "Registration without a mark is not qualification.");

        context.SetPeriod(context.Schedule.GetCurrentPeriod(new DateTime(2026, 8, 5, 12, 0, 0, DateTimeKind.Utc)));
        var item = player.PersistenceContext.CreateNew<Item>();
        item.Definition = generated!.Definition;
        item.Level = generated.Level;
        item.Durability = generated.Durability;
        Assert.That(await player.Inventory!.AddItemAsync(20, item).ConfigureAwait(false), Is.True);
        await new CastleSiegeRegisterMarkAction().RegisterMarkAsync(player, context, 20).ConfigureAwait(false);
        Assert.Multiple(() =>
        {
            Assert.That(player.Inventory.GetItem(20), Is.Null);
            Assert.That(context.RegisteredGuilds[guild.Id].Marks, Is.EqualTo(1));
        });
        await CastleSiegeGuildSelector.SelectGuildsAsync(context).ConfigureAwait(false);
        Assert.Multiple(() =>
        {
            Assert.That(context.FinalGuildList, Has.Count.EqualTo(1));
            Assert.That(context.FinalGuildList[runtimeGuildId].Side, Is.EqualTo(CastleSiegeJoinSide.Attack1));
        });
        using var reloaded = persistence.CreateNewTypedContext(typeof(CastleSiegeGuildRegistration), false, configuration);
        Assert.That((await reloaded.GetAsync<CastleSiegeGuildRegistration>().ConfigureAwait(false)).Single().Marks, Is.EqualTo(1));
    }
}

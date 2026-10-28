// <copyright file="SignOfLordDropConfigurationTests.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Persistence.Initialization.Tests;

using System.Globalization;
using Moq;
using MUnique.OpenMU.DataModel.Configuration;
using MUnique.OpenMU.GameLogic;
using MUnique.OpenMU.Persistence.Initialization.Updates;

/// <summary>
/// Verifies a reachable Sign of Lord source without replacing Rena or owner-defined drops.
/// </summary>
[TestFixture]
internal class SignOfLordDropConfigurationTests
{
    private static readonly Guid SignGroupId = GuidHelper.CreateGuid<DropItemGroup>(14, 21, 3);

    /// <summary>
    /// Fresh data must generate the registration item's exact level on the intended event maps.
    /// </summary>
    [Test]
    public async Task FreshSignOfLordDropHasExactLevelAndEventMapsAsync()
    {
        var (context, configuration) = await TestGameConfiguration.CreateSeason6Async().ConfigureAwait(false);
        using var _ = context;
        var group = GetSignGroup(configuration);
        Assert.That(group, Is.Not.Null, "Registration requires level3, whereas the legacy Rena group only creates level0.");
        var rena = configuration.DropItemGroups.Single(candidate => candidate.GetId() == GuidHelper.CreateGuid<DropItemGroup>(14, 21));
        var maps = configuration.Maps.Where(map => map.DropItemGroups.Contains(group!)).Select(map => (map.Number, map.Discriminator)).ToArray();
        var expectedMaps = Enumerable.Range(11, 7).Select(number => ((short)number, (byte)0))
            .Append(((short)52, (byte)0)).Concat(Enumerable.Range(5, 3).Select(level => ((short)32, (byte)level)));
        var generator = new DefaultDropGenerator(configuration, new Mock<IRandomizer>().Object);
        var sign = generator.GenerateItemDrop(group!);
        var originalRena = generator.GenerateItemDrop(rena);
        Assert.Multiple(() =>
        {
            Assert.That(group!.Chance, Is.EqualTo(0.01), "Explicit fork weight; not a guaranteed percentage after combined group normalization.");
            Assert.That(group.MinimumMonsterLevel, Is.EqualTo(30));
            Assert.That(group.MaximumMonsterLevel, Is.EqualTo(255));
            Assert.That(group.ItemLevel, Is.EqualTo(3));
            Assert.That(group.PossibleItems, Has.Count.EqualTo(1));
            Assert.That(group.Description.SourceKey, Is.EqualTo("DropGroupDescriptions/SignOfLord"));
            Assert.That(group.Description.GetOwnTranslation(CultureInfo.GetCultureInfo("zh-CN")), Is.Not.Null.And.Not.Empty);
            Assert.That(maps, Is.EquivalentTo(expectedMaps));
            Assert.That(sign?.Definition, Is.SameAs(configuration.CastleSiegeConfiguration!.SignOfLordItemDefinition));
            Assert.That(sign?.Level, Is.EqualTo(configuration.CastleSiegeConfiguration.SignOfLordItemLevel));
            Assert.That(sign?.Durability, Is.GreaterThan(0));
            Assert.That(originalRena?.Level, Is.Zero);
            Assert.That(rena.Chance, Is.EqualTo(0.01));
            Assert.That(rena.ItemLevel, Is.Null);
            Assert.That(configuration.Maps.Where(map => map.DropItemGroups.Contains(rena)),
                Is.EquivalentTo(configuration.Maps.Where(map => map.DropItemGroups.Contains(group))));
        });
    }

    /// <summary>
    /// An installed version2 gets only the missing drop delta, preserving state and legacy owner edits.
    /// </summary>
    [Test]
    public async Task VersionTwoUpgradeAddsMissingSourceOnceWithoutReapplyingLegacySettingsAsync()
    {
        var (context, configuration) = await TestGameConfiguration.CreateSeason6Async().ConfigureAwait(false);
        using var _ = context;
        await RemoveSignGroupAsync(context, configuration).ConfigureAwait(false);
        var update = new AddCastleSiegeDataUpdatePlugIn();
        var installed = await InstallVersionTwoAsync(context, update).ConfigureAwait(false);
        var senior = configuration.Monsters.Single(monster => monster.Number == 223);
        senior.NpcWindow = NpcWindow.Undefined;
        configuration.CastleSiegeConfiguration!.CrownHoldTimeSeconds = 47;
        var data = (await context.GetAsync<MUnique.OpenMU.DataModel.Entities.CastleSiegeData>().ConfigureAwait(false)).Single();
        var owner = Guid.NewGuid();
        data.OwnerGuildId = owner;
        data.IsOccupied = true;
        data.TributeMoney = 1234;
        var npcStates = data.NpcStates.ToArray();
        await update.ApplyUpdateAsync(context, configuration).ConfigureAwait(false);
        Assert.That(GetSignGroup(configuration), Is.Not.Null);
        var groups = configuration.DropItemGroups.ToArray();
        var bindings = configuration.Maps.Select(map => (Map: map, Groups: map.DropItemGroups.ToArray())).ToArray();
        await update.ApplyUpdateAsync(context, configuration).ConfigureAwait(false);
        Assert.Multiple(() =>
        {
            Assert.That(update.Version, Is.EqualTo(4));
            Assert.That(installed.Version, Is.EqualTo(4));
            Assert.That(senior.NpcWindow, Is.EqualTo(NpcWindow.Undefined), "Version2 must not rerun the earlier Senior window rewrite.");
            Assert.That(configuration.CastleSiegeConfiguration.CrownHoldTimeSeconds, Is.EqualTo(47));
            Assert.That(data.OwnerGuildId, Is.EqualTo(owner));
            Assert.That(data.IsOccupied, Is.True);
            Assert.That(data.TributeMoney, Is.EqualTo(1234));
            Assert.That(data.NpcStates, Is.EquivalentTo(npcStates));
            Assert.That(configuration.DropItemGroups, Is.EquivalentTo(groups));
            foreach (var binding in bindings)
            {
                Assert.That(binding.Map.DropItemGroups, Is.EquivalentTo(binding.Groups));
            }
        });
    }

    /// <summary>
    /// Existing dedicated/custom sources keep their chance, level and map membership.
    /// </summary>
    /// <param name="useSeededId">Whether the owner customized the seeded group or supplied another source.</param>
    [TestCase(true)]
    [TestCase(false)]
    public async Task UpgradePreservesExistingDropAndCustomMappingAsync(bool useSeededId)
    {
        var (context, configuration) = await TestGameConfiguration.CreateSeason6Async().ConfigureAwait(false);
        using var _ = context;
        await RemoveSignGroupAsync(context, configuration).ConfigureAwait(false);
        var group = context.CreateNew<DropItemGroup>();
        group.SetGuid(useSeededId ? SignGroupId : Guid.NewGuid());
        group.PossibleItems.Add(configuration.CastleSiegeConfiguration!.SignOfLordItemDefinition!);
        group.ItemLevel = useSeededId ? (byte)2 : (byte)3;
        group.Chance = 0.07;
        group.MinimumMonsterLevel = 91;
        group.MaximumMonsterLevel = 101;
        configuration.DropItemGroups.Add(group);
        var customMap = configuration.Maps.Single(map => map.Number == 0 && map.Discriminator == 0);
        customMap.DropItemGroups.Add(group);
        var groups = configuration.DropItemGroups.ToArray();
        var update = new AddCastleSiegeDataUpdatePlugIn();
        await InstallVersionTwoAsync(context, update).ConfigureAwait(false);
        await update.ApplyUpdateAsync(context, configuration).ConfigureAwait(false);
        await update.ApplyUpdateAsync(context, configuration).ConfigureAwait(false);
        Assert.Multiple(() =>
        {
            Assert.That(configuration.DropItemGroups, Is.EquivalentTo(groups));
            Assert.That(group.Chance, Is.EqualTo(0.07));
            Assert.That(group.ItemLevel, Is.EqualTo(useSeededId ? 2 : 3));
            Assert.That(group.MinimumMonsterLevel, Is.EqualTo(91));
            Assert.That(group.MaximumMonsterLevel, Is.EqualTo(101));
            Assert.That(configuration.Maps.Where(map => map.DropItemGroups.Contains(group)), Is.EquivalentTo(new[] { customMap }));
        });
    }

    /// <summary>
    /// An owner-defined registration item or level must not acquire unrelated default sources.
    /// </summary>
    /// <param name="customItem">Whether the owner changed the item instead of its level.</param>
    [TestCase(true)]
    [TestCase(false)]
    public async Task UpgradePreservesCustomRegistrationRequirementsAsync(bool customItem)
    {
        var (context, configuration) = await TestGameConfiguration.CreateSeason6Async().ConfigureAwait(false);
        using var _ = context;
        await RemoveSignGroupAsync(context, configuration).ConfigureAwait(false);
        var registration = configuration.CastleSiegeConfiguration!;
        if (customItem)
        {
            registration.SignOfLordItemDefinition = configuration.Items.Single(item => item.Group == 14 && item.Number == 13);
        }
        else
        {
            registration.SignOfLordItemLevel = 2;
        }

        var definition = registration.SignOfLordItemDefinition;
        var level = registration.SignOfLordItemLevel;
        var groups = configuration.DropItemGroups.ToArray();
        var update = new AddCastleSiegeDataUpdatePlugIn();
        await InstallVersionTwoAsync(context, update).ConfigureAwait(false);
        await update.ApplyUpdateAsync(context, configuration).ConfigureAwait(false);
        Assert.Multiple(() =>
        {
            Assert.That(registration.SignOfLordItemDefinition, Is.SameAs(definition));
            Assert.That(registration.SignOfLordItemLevel, Is.EqualTo(level));
            Assert.That(configuration.DropItemGroups, Is.EquivalentTo(groups));
        });
    }

    /// <summary>
    /// The older Rena update must never mistake a dedicated level3 source for plain Rena.
    /// </summary>
    /// <param name="customPlainId">Whether the owner supplied the plain source with another identifier.</param>
    [TestCase(false)]
    [TestCase(true)]
    public async Task LegacyRenaUpdateSelectsPlainSourceIndependentOfOrderAsync(bool customPlainId)
    {
        var (context, configuration) = await TestGameConfiguration.CreateSeason6Async().ConfigureAwait(false);
        using var _ = context;
        await RemoveSignGroupAsync(context, configuration).ConfigureAwait(false);
        var plain = configuration.DropItemGroups.Single(group => group.GetId() == GuidHelper.CreateGuid<DropItemGroup>(14, 21));
        if (customPlainId)
        {
            plain.SetGuid(Guid.NewGuid());
        }

        plain.Chance = 0.04;
        var sign = context.CreateNew<DropItemGroup>();
        sign.SetGuid(SignGroupId);
        sign.PossibleItems.Add(configuration.CastleSiegeConfiguration!.SignOfLordItemDefinition!);
        sign.ItemLevel = 3;
        sign.Chance = 0.07;
        var existing = configuration.DropItemGroups.ToArray();
        configuration.DropItemGroups.Clear();
        configuration.DropItemGroups.Add(sign);
        foreach (var group in existing)
        {
            configuration.DropItemGroups.Add(group);
        }

        var customMap = configuration.Maps.Single(map => map.Number == 0 && map.Discriminator == 0);
        customMap.DropItemGroups.Add(sign);
        var groups = configuration.DropItemGroups.ToArray();
        await new AddRenaItemUpdatePlugIn().ApplyUpdateAsync(context, configuration).ConfigureAwait(false);
        Assert.Multiple(() =>
        {
            Assert.That(configuration.DropItemGroups, Is.EquivalentTo(groups), "A custom plain source must not acquire a duplicate seeded source.");
            Assert.That(plain.Chance, Is.EqualTo(0.04));
            Assert.That(sign.Chance, Is.EqualTo(0.07));
            Assert.That(configuration.Maps.Where(map => map.DropItemGroups.Contains(sign)), Is.EquivalentTo(new[] { customMap }));
        });
    }

    /// <summary>
    /// An owner-converted seeded group must not be overwritten or duplicated under the same identifier.
    /// </summary>
    [Test]
    public async Task LegacyRenaUpdatePreservesSeededIdentifierConvertedToCustomSignSourceAsync()
    {
        var (context, configuration) = await TestGameConfiguration.CreateSeason6Async().ConfigureAwait(false);
        using var _ = context;
        await RemoveSignGroupAsync(context, configuration).ConfigureAwait(false);
        var source = configuration.DropItemGroups.Single(group => group.GetId() == GuidHelper.CreateGuid<DropItemGroup>(14, 21));
        foreach (var map in configuration.Maps)
        {
            map.DropItemGroups.Remove(source);
        }

        source.ItemLevel = 3;
        source.Chance = 0.07;
        var customMap = configuration.Maps.Single(map => map.Number == 0 && map.Discriminator == 0);
        customMap.DropItemGroups.Add(source);
        var groups = configuration.DropItemGroups.ToArray();
        await new AddRenaItemUpdatePlugIn().ApplyUpdateAsync(context, configuration).ConfigureAwait(false);
        Assert.Multiple(() =>
        {
            Assert.That(configuration.DropItemGroups, Is.EquivalentTo(groups));
            Assert.That(configuration.DropItemGroups.Select(group => group.GetId()), Is.Unique);
            Assert.That(source.ItemLevel, Is.EqualTo(3));
            Assert.That(source.Chance, Is.EqualTo(0.07));
            Assert.That(configuration.Maps.Where(map => map.DropItemGroups.Contains(source)), Is.EquivalentTo(new[] { customMap }));
        });
    }

    private static DropItemGroup? GetSignGroup(GameConfiguration configuration)
    {
        return configuration.DropItemGroups.SingleOrDefault(group => group.GetId() == SignGroupId);
    }

    private static async Task RemoveSignGroupAsync(IContext context, GameConfiguration configuration)
    {
        if (GetSignGroup(configuration) is not { } group)
        {
            return;
        }

        foreach (var map in configuration.Maps)
        {
            map.DropItemGroups.Remove(group);
        }

        configuration.DropItemGroups.Remove(group);
        await context.DeleteAsync(group).ConfigureAwait(false);
    }

    private static async Task<ConfigurationUpdate> InstallVersionTwoAsync(IContext context, AddCastleSiegeDataUpdatePlugIn update)
    {
        var installed = (await context.GetAsync<ConfigurationUpdate>().ConfigureAwait(false)).SingleOrDefault(entry => entry.Key == update.Key)
            ?? context.CreateNew<ConfigurationUpdate>();
        installed.Key = update.Key;
        installed.Version = 2;
        await context.SaveChangesAsync().ConfigureAwait(false);
        return installed;
    }
}

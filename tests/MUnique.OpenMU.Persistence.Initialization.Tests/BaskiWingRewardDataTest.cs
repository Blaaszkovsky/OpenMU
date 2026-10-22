// <copyright file="BaskiWingRewardDataTest.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Persistence.Initialization.Tests;

using Microsoft.Extensions.Logging.Abstractions;
using MUnique.OpenMU.DataModel.Configuration;
using MUnique.OpenMU.Persistence.Initialization.Updates;
using MUnique.OpenMU.Persistence.InMemory;

/// <summary>
/// Verifies the isolated experimental Baski reward and operator-safe configuration update.
/// </summary>
[TestFixture]
internal class BaskiWingRewardDataTest
{
    /// <summary>Verifies fresh configurations contain the wing only in Baski's explicit reward.</summary>
    [Test]
    public async Task FreshConfigurationContainsLocalWingRewardAsync()
    {
        var (context, configuration) = await CreateConfigurationAsync().ConfigureAwait(false);
        using var _ = context;
        AssertReward(configuration);
    }

    /// <summary>Verifies an installation predating this update is repaired without duplicate groups.</summary>
    [Test]
    public async Task UpdateRepairsMissingRewardOnceAsync()
    {
        var (context, configuration) = await CreateConfigurationAsync().ConfigureAwait(false);
        using var _ = context;
        var baski = configuration.Monsters.Single(monster => monster.Number == 900);
        var previousReward = baski.DropItemGroups.Single();
        baski.DropItemGroups.Clear();
        baski.NumberOfMaximumItemDrops = 0;
        configuration.DropItemGroups.Remove(previousReward);
        await context.DeleteAsync(previousReward).ConfigureAwait(false);
        var update = new AddBaskiWingRewardUpdatePlugIn();
        var installed = (await context.GetAsync<ConfigurationUpdate>().ConfigureAwait(false)).Single(entry => entry.Key == update.Key);
        await context.DeleteAsync(installed).ConfigureAwait(false);

        await update.ApplyUpdateAsync(context, configuration).ConfigureAwait(false);
        AssertReward(configuration);
        var reward = baski.DropItemGroups.Single();
        var groupCount = configuration.DropItemGroups.Count;
        await update.ApplyUpdateAsync(context, configuration).ConfigureAwait(false);

        AssertReward(configuration);
        Assert.Multiple(() =>
        {
            Assert.That(baski.DropItemGroups.Single(), Is.SameAs(reward));
            Assert.That(configuration.DropItemGroups.Count, Is.EqualTo(groupCount));
        });
    }

    /// <summary>Verifies a detached reward is reused instead of duplicated.</summary>
    [Test]
    public async Task UpdateReusesExistingRewardDefinitionAsync()
    {
        var (context, configuration) = await CreateConfigurationAsync().ConfigureAwait(false);
        using var _ = context;
        var baski = configuration.Monsters.Single(monster => monster.Number == 900);
        var reward = baski.DropItemGroups.Single();
        baski.DropItemGroups.Clear();
        baski.NumberOfMaximumItemDrops = 0;
        var groupCount = configuration.DropItemGroups.Count;

        await new AddBaskiWingRewardUpdatePlugIn().ApplyUpdateAsync(context, configuration).ConfigureAwait(false);

        AssertReward(configuration);
        Assert.That(baski.DropItemGroups.Single(), Is.SameAs(reward));
        Assert.That(configuration.DropItemGroups.Count, Is.EqualTo(groupCount));
    }

    /// <summary>Verifies an installed v1 prototype is migrated without changing its identity.</summary>
    [Test]
    public async Task VersionOnePrototypeIsBalancedAsync()
    {
        var (context, configuration) = await CreateConfigurationAsync().ConfigureAwait(false);
        using var _ = context;
        var baski = configuration.Monsters.Single(monster => monster.Number == 900);
        var reward = baski.DropItemGroups.Single();
        reward.Chance = 1;
        baski.RespawnDelay = TimeSpan.FromSeconds(10);
        var update = new AddBaskiWingRewardUpdatePlugIn();
        var installed = (await context.GetAsync<ConfigurationUpdate>().ConfigureAwait(false)).Single(entry => entry.Key == update.Key);
        installed.Version = 1;

        await update.ApplyUpdateAsync(context, configuration).ConfigureAwait(false);

        AssertReward(configuration);
        Assert.Multiple(() =>
        {
            Assert.That(baski.DropItemGroups.Single(), Is.SameAs(reward));
            Assert.That(installed.Version, Is.EqualTo(2));
        });
    }

    /// <summary>Verifies a custom chance and respawn time survive the v1 to v2 update.</summary>
    [Test]
    public async Task VersionOneOperatorBalanceIsPreservedAsync()
    {
        var (context, configuration) = await CreateConfigurationAsync().ConfigureAwait(false);
        using var _ = context;
        var baski = configuration.Monsters.Single(monster => monster.Number == 900);
        var reward = baski.DropItemGroups.Single();
        reward.Chance = 0.25;
        baski.RespawnDelay = TimeSpan.FromMinutes(2);
        var update = new AddBaskiWingRewardUpdatePlugIn();
        var installed = (await context.GetAsync<ConfigurationUpdate>().ConfigureAwait(false)).Single(entry => entry.Key == update.Key);
        installed.Version = 1;

        await update.ApplyUpdateAsync(context, configuration).ConfigureAwait(false);

        Assert.Multiple(() =>
        {
            Assert.That(reward.Chance, Is.EqualTo(0.25));
            Assert.That(baski.RespawnDelay, Is.EqualTo(TimeSpan.FromMinutes(2)));
            Assert.That(baski.DropItemGroups.Single(), Is.SameAs(reward));
            Assert.That(installed.Version, Is.EqualTo(2));
        });
    }

    /// <summary>Verifies operator reward settings, including intentional disabling, survive reapplication.</summary>
    [TestCase(0, 0)]
    [TestCase(0.25, 3)]
    public async Task UpdatePreservesOperatorRewardAsync(double chance, int cap)
    {
        var (context, configuration) = await CreateConfigurationAsync().ConfigureAwait(false);
        using var _ = context;
        var baski = configuration.Monsters.Single(monster => monster.Number == 900);
        baski.DropItemGroups.Clear();
        var custom = context.CreateNew<DropItemGroup>();
        custom.Chance = chance;
        var customItem = configuration.Items.Single(item => item is { Group: 14, Number: 13 });
        custom.PossibleItems.Add(customItem);
        configuration.DropItemGroups.Add(custom);
        baski.DropItemGroups.Add(custom);
        baski.NumberOfMaximumItemDrops = cap;
        var groupCount = configuration.DropItemGroups.Count;
        var update = new AddBaskiWingRewardUpdatePlugIn();

        await update.ApplyUpdateAsync(context, configuration).ConfigureAwait(false);
        await update.ApplyUpdateAsync(context, configuration).ConfigureAwait(false);

        Assert.Multiple(() =>
        {
            Assert.That(baski.DropItemGroups.Single(), Is.SameAs(custom));
            Assert.That(custom.Chance, Is.EqualTo(chance));
            Assert.That(custom.PossibleItems.Single(), Is.SameAs(customItem));
            Assert.That(baski.NumberOfMaximumItemDrops, Is.EqualTo(cap));
            Assert.That(configuration.DropItemGroups.Count, Is.EqualTo(groupCount));
        });
    }

    /// <summary>Verifies the optional update requires both the Baski and wing definitions.</summary>
    [Test]
    public void UpdateRequiresBothContentDefinitions()
    {
        var update = new AddBaskiWingRewardUpdatePlugIn();
        Assert.Multiple(() =>
        {
            Assert.That(update.IsMandatory, Is.False);
            Assert.That(update.Version, Is.EqualTo(2));
            Assert.That(update.DependsOn.Single(dependency => dependency.Key == typeof(AddBaskiPrototypeUpdatePlugIn).GUID).MinVersion, Is.EqualTo(4));
            Assert.That(update.DependsOn.Single(dependency => dependency.Key == typeof(AddExperimentalFearlessWingUpdatePlugIn).GUID).MinVersion, Is.EqualTo(2));
            Assert.That(update.Name, Is.Not.Empty);
            Assert.That(update.Description, Does.Contain("5%"));
        });
    }

    private static void AssertReward(GameConfiguration configuration)
    {
        var baski = configuration.Monsters.Single(monster => monster.Number == 900);
        var reward = baski.DropItemGroups.Single();
        var wing = configuration.Items.Single(item => item is { Group: 12, Number: 245 });
        Assert.Multiple(() =>
        {
            Assert.That(baski.NumberOfMaximumItemDrops, Is.EqualTo(1));
            Assert.That(baski.RespawnDelay, Is.EqualTo(TimeSpan.FromMinutes(5)));
            Assert.That(reward.Chance, Is.EqualTo(0.05));
            Assert.That(reward.ItemLevel, Is.EqualTo(0));
            Assert.That(reward.ItemType, Is.EqualTo(SpecialItemType.None));
            Assert.That(reward.PossibleItems.Single(), Is.SameAs(wing));
            Assert.That(wing.DropsFromMonsters, Is.False);
            Assert.That(configuration.DropItemGroups.Count(group => group.PossibleItems.Contains(wing)), Is.EqualTo(1));
            Assert.That(configuration.Maps.SelectMany(map => map.DropItemGroups), Does.Not.Contain(reward));
            Assert.That(configuration.Monsters.Where(monster => monster != baski).SelectMany(monster => monster.DropItemGroups), Does.Not.Contain(reward));
        });
    }

    private static async Task<(IContext Context, GameConfiguration Configuration)> CreateConfigurationAsync()
    {
        var provider = new InMemoryPersistenceContextProvider();
        await new VersionSeasonSix.DataInitialization(provider, new NullLoggerFactory()).CreateInitialDataAsync(1, true).ConfigureAwait(false);
        var context = provider.CreateNewContext();
        var configuration = (await context.GetAsync<GameConfiguration>().ConfigureAwait(false)).Single();
        return (context, configuration);
    }
}

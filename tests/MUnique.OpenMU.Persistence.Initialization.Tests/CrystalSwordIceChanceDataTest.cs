// <copyright file="CrystalSwordIceChanceDataTest.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Persistence.Initialization.Tests;

using MUnique.OpenMU.AttributeSystem;
using MUnique.OpenMU.DataModel.Configuration;
using MUnique.OpenMU.DataModel.Configuration.Items;
using MUnique.OpenMU.GameLogic.Attributes;
using MUnique.OpenMU.Persistence.Initialization.Updates;

/// <summary>
/// Verifies Crystal Sword data for new and existing configurations, including update reapplication.
/// </summary>
[TestFixture]
internal class CrystalSwordIceChanceDataTest
{
    /// <summary>
    /// Verifies that the initializer installs the ice chance and records its configuration update.
    /// </summary>
    /// <param name="version">The initialized game version.</param>
    [TestCase("075")]
    [TestCase("095d")]
    [TestCase("Season6")]
    public async Task FreshConfigurationHasCrystalSwordIceChanceAsync(string version)
    {
        var (context, configuration) = await CreateAsync(version).ConfigureAwait(false);
        using var _ = context;
        AssertCrystalSword(configuration);
        var update = CreateUpdate(version);
        var installed = (await context.GetAsync<ConfigurationUpdate>().ConfigureAwait(false)).Single(entry => entry.Key == update.Key);

        Assert.That(installed.Version, Is.EqualTo(1));
    }

    /// <summary>
    /// Verifies that an old configuration missing the stat is repaired once without duplicating identities or power-ups.
    /// </summary>
    /// <param name="version">The initialized game version.</param>
    [TestCase("075")]
    [TestCase("095d")]
    [TestCase("Season6")]
    public async Task MissingCrystalSwordDataIsRepairedIdempotentlyAsync(string version)
    {
        var (context, configuration) = await CreateAsync(version).ConfigureAwait(false);
        using var _ = context;
        var sword = GetCrystalSword(configuration);
        var attribute = configuration.Attributes.Single(value => value == Stats.CrystalSwordIceChance);
        var oldPowerUp = sword.BasePowerUpAttributes.Single(value => value.TargetAttribute == attribute);
        var otherPowerUps = sword.BasePowerUpAttributes.Where(value => value != oldPowerUp).ToList();
        var update = CreateUpdate(version);
        var installed = (await context.GetAsync<ConfigurationUpdate>().ConfigureAwait(false)).Single(entry => entry.Key == update.Key);
        sword.BasePowerUpAttributes.Remove(oldPowerUp);
        configuration.Attributes.Remove(attribute);
        Assert.That(await context.DeleteAsync(oldPowerUp).ConfigureAwait(false), Is.True);
        Assert.That(await context.DeleteAsync(attribute).ConfigureAwait(false), Is.True);
        Assert.That(await context.DeleteAsync(installed).ConfigureAwait(false), Is.True);
        await context.SaveChangesAsync().ConfigureAwait(false);
        Assert.That(configuration.Attributes.Any(value => value == Stats.CrystalSwordIceChance), Is.False);

        await update.ApplyUpdateAsync(context, configuration).ConfigureAwait(false);
        await context.SaveChangesAsync().ConfigureAwait(false);
        AssertCrystalSword(configuration);
        var repairedAttribute = configuration.Attributes.Single(value => value == Stats.CrystalSwordIceChance);
        var repairedPowerUp = sword.BasePowerUpAttributes.Single(value => value.TargetAttribute == repairedAttribute);
        var attributeCount = (await context.GetAsync<AttributeDefinition>().ConfigureAwait(false)).Count();
        var powerUpCount = (await context.GetAsync<ItemBasePowerUpDefinition>().ConfigureAwait(false)).Count();
        var repairedUpdate = (await context.GetAsync<ConfigurationUpdate>().ConfigureAwait(false)).Single(entry => entry.Key == update.Key);

        await update.ApplyUpdateAsync(context, configuration).ConfigureAwait(false);
        await context.SaveChangesAsync().ConfigureAwait(false);

        AssertCrystalSword(configuration);
        Assert.Multiple(() =>
        {
            Assert.That(configuration.Attributes.Single(value => value == Stats.CrystalSwordIceChance), Is.SameAs(repairedAttribute));
            Assert.That(sword.BasePowerUpAttributes.Single(value => value.TargetAttribute == repairedAttribute), Is.SameAs(repairedPowerUp));
            Assert.That(sword.BasePowerUpAttributes.Where(value => value != repairedPowerUp), Is.EqualTo(otherPowerUps));
            Assert.That(repairedUpdate.Version, Is.EqualTo(1));
        });
        Assert.That((await context.GetAsync<AttributeDefinition>().ConfigureAwait(false)).Count(), Is.EqualTo(attributeCount));
        Assert.That((await context.GetAsync<ItemBasePowerUpDefinition>().ConfigureAwait(false)).Count(), Is.EqualTo(powerUpCount));
        Assert.That((await context.GetAsync<ConfigurationUpdate>().ConfigureAwait(false)).Single(entry => entry.Key == update.Key), Is.SameAs(repairedUpdate));
    }

    private static void AssertCrystalSword(GameConfiguration configuration)
    {
        var attribute = configuration.Attributes.Single(value => value == Stats.CrystalSwordIceChance);
        var sword = GetCrystalSword(configuration);
        var powerUp = sword.BasePowerUpAttributes.Single(value => value.TargetAttribute == attribute);
        Assert.Multiple(() =>
        {
            Assert.That(powerUp.BaseValue, Is.EqualTo(0.05f));
            Assert.That(powerUp.AggregateType, Is.EqualTo(AggregateType.AddRaw));
            Assert.That(configuration.Items.Where(item => item != sword).SelectMany(item => item.BasePowerUpAttributes).Any(value => value.TargetAttribute == attribute), Is.False);
        });
    }

    private static ItemDefinition GetCrystalSword(GameConfiguration configuration)
    {
        var sword = configuration.Items.Single(item => item is { Group: 2, Number: 5 });
        Assert.That(sword.Name.ValueInNeutralLanguage, Is.EqualTo("Crystal Sword"));
        return sword;
    }

    private static AddCrystalSwordIceChanceUpdatePlugInBase CreateUpdate(string version) => version switch
    {
        "075" => new AddCrystalSwordIceChanceUpdatePlugIn075(),
        "095d" => new AddCrystalSwordIceChanceUpdatePlugIn095D(),
        "Season6" => new AddCrystalSwordIceChanceUpdatePlugInSeasonSix(),
        _ => throw new ArgumentOutOfRangeException(nameof(version)),
    };

    private static Task<(IContext Context, GameConfiguration GameConfiguration)> CreateAsync(string version) => version switch
    {
        "075" => TestGameConfiguration.Create075Async(),
        "095d" => TestGameConfiguration.Create095dAsync(),
        "Season6" => TestGameConfiguration.CreateSeason6Async(),
        _ => throw new ArgumentOutOfRangeException(nameof(version)),
    };
}

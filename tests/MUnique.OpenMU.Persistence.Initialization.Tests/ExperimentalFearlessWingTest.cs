// <copyright file="ExperimentalFearlessWingTest.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Persistence.Initialization.Tests;

using Microsoft.Extensions.Logging.Abstractions;
using MUnique.OpenMU.DataModel.Configuration;
using MUnique.OpenMU.DataModel.Configuration.Items;
using MUnique.OpenMU.GameLogic.Attributes;
using MUnique.OpenMU.Persistence.Initialization.CharacterClasses;
using MUnique.OpenMU.Persistence.Initialization.Updates;
using MUnique.OpenMU.Persistence.InMemory;

/// <summary>
/// Verifies the experimental paired client wing in fresh and existing Season 6 configurations.
/// </summary>
[TestFixture]
internal class ExperimentalFearlessWingTest
{
    [Test]
    public async Task FreshConfigurationContainsFearlessWingAsync()
    {
        var (context, configuration) = await CreateConfigurationAsync().ConfigureAwait(false);
        using var _ = context;

        AssertFearlessWing(configuration);
        AssertAdditionalWings(configuration);
    }

    [Test]
    public async Task UpdateAddsFearlessWingOnlyOnceAsync()
    {
        var (context, configuration) = await CreateConfigurationAsync().ConfigureAwait(false);
        using var _ = context;
        var wing = configuration.Items.Single(item => item is { Group: 12, Number: 248 });
        configuration.Items.Remove(wing);
        foreach (var additionalWing in configuration.Items.Where(item => item is { Group: 12, Number: 245 or 249 }).ToList())
        {
            configuration.Items.Remove(additionalWing);
        }

        var update = new AddExperimentalFearlessWingUpdatePlugIn();
        await update.ApplyUpdateAsync(context, configuration).ConfigureAwait(false);
        await update.ApplyUpdateAsync(context, configuration).ConfigureAwait(false);

        AssertFearlessWing(configuration);
        AssertAdditionalWings(configuration);
    }

    [Test]
    public async Task UpdateRepairsExistingFearlessWingOptionsAsync()
    {
        var (context, configuration) = await CreateConfigurationAsync().ConfigureAwait(false);
        using var _ = context;
        var wing = configuration.Items.Single(item => item is { Group: 12, Number: 248 });
        var specialOptions = wing.PossibleItemOptions.Single(option => option.PossibleOptions.Any(value => value.OptionType == ItemOptionTypes.Wing));
        wing.PossibleItemOptions.Remove(specialOptions);

        var update = new AddExperimentalFearlessWingUpdatePlugIn();
        await update.ApplyUpdateAsync(context, configuration).ConfigureAwait(false);
        await update.ApplyUpdateAsync(context, configuration).ConfigureAwait(false);

        AssertFearlessWing(configuration);
        AssertAdditionalWings(configuration);
    }

    private static void AssertFearlessWing(GameConfiguration configuration)
    {
        var wing = configuration.Items.Single(item => item is { Group: 12, Number: 248 });
        var storm = configuration.Items.Single(item => item is { Group: 12, Number: 36 });

        Assert.Multiple(() =>
        {
            Assert.That(wing.Name.ValueInNeutralLanguage, Is.EqualTo("Fearless Wings (Knight)"));
            Assert.That((wing.Width, wing.Height), Is.EqualTo((4, 3)));
            Assert.That(wing.DropsFromMonsters, Is.False);
            Assert.That(wing.ItemSlot, Is.SameAs(storm.ItemSlot));
            Assert.That(wing.PossibleItemOptions.SelectMany(option => option.PossibleOptions).Count(option => option.OptionType == ItemOptionTypes.Wing), Is.EqualTo(4));
            Assert.That(wing.QualifiedCharacters.Select(characterClass => (CharacterClassNumber)characterClass.Number), Does.Contain(CharacterClassNumber.BladeMaster));
            Assert.That(wing.BasePowerUpAttributes.Single(powerUp => powerUp.TargetAttribute == Stats.CanFly).BaseValue, Is.EqualTo(1));
            Assert.That(wing.BasePowerUpAttributes.Single(powerUp => powerUp.TargetAttribute == Stats.DefenseBase).BonusPerLevelTable,
                Is.SameAs(storm.BasePowerUpAttributes.Single(powerUp => powerUp.TargetAttribute == Stats.DefenseBase).BonusPerLevelTable));
        });
    }

    private static void AssertAdditionalWings(GameConfiguration configuration)
    {
        var universal = configuration.Items.Single(item => item is { Group: 12, Number: 245 });
        var wizard = configuration.Items.Single(item => item is { Group: 12, Number: 249 });
        Assert.Multiple(() =>
        {
            Assert.That(universal.QualifiedCharacters.Select(characterClass => (CharacterClassNumber)characterClass.Number), Does.Contain(CharacterClassNumber.DarkKnight));
            Assert.That(universal.QualifiedCharacters.Select(characterClass => (CharacterClassNumber)characterClass.Number), Does.Contain(CharacterClassNumber.GrandMaster));
            Assert.That(universal.QualifiedCharacters.Select(characterClass => (CharacterClassNumber)characterClass.Number), Does.Contain(CharacterClassNumber.FairyElf));
            Assert.That(wizard.QualifiedCharacters.Select(characterClass => (CharacterClassNumber)characterClass.Number), Is.EquivalentTo(new[] { CharacterClassNumber.GrandMaster }));
            Assert.That(universal.PossibleItemOptions.SelectMany(option => option.PossibleOptions).Count(option => option.OptionType == ItemOptionTypes.Wing), Is.EqualTo(4));
        });
    }

    private static async Task<(IContext Context, GameConfiguration Configuration)> CreateConfigurationAsync()
    {
        var contextProvider = new InMemoryPersistenceContextProvider();
        var initialization = new VersionSeasonSix.DataInitialization(contextProvider, new NullLoggerFactory());
        await initialization.CreateInitialDataAsync(1, true).ConfigureAwait(false);

        var context = contextProvider.CreateNewContext();
        var configuration = (await context.GetAsync<GameConfiguration>().ConfigureAwait(false)).First();
        return (context, configuration);
    }
}

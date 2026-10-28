// <copyright file="InvasionSpawnConfigurationTests.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Tests;

using System.ComponentModel.DataAnnotations;
using MUnique.OpenMU.GameLogic.PlugIns.InvasionEvents;

/// <summary>
/// Built-in invasion defaults must satisfy the validation used by configuration editors.
/// </summary>
[TestFixture]
public class InvasionSpawnConfigurationTests
{
    /// <summary>
    /// Validates every default spawn, including the White Wizard's helper monsters.
    /// </summary>
    /// <param name="eventName">The invasion.</param>
    [TestCase("golden")]
    [TestCase("dragon")]
    [TestCase("wizard")]
    public void DefaultSpawnsAreValid(string eventName)
    {
        var configuration = (PeriodicInvasionConfiguration)(eventName switch
        {
            "golden" => new GoldenInvasionPlugIn().CreateDefaultConfig(),
            "dragon" => new RedDragonInvasionPlugIn().CreateDefaultConfig(),
            _ => new WhiteWizardInvasionPlugIn().CreateDefaultConfig(),
        });

        Assert.That(configuration.Mobs, Is.Not.Empty);
        foreach (var spawn in configuration.Mobs)
        {
            var errors = new List<ValidationResult>();
            Assert.That(Validator.TryValidateObject(spawn, new ValidationContext(spawn), errors, true), Is.True,
                $"Monster {spawn.MonsterId}: {string.Join("; ", errors.Select(error => error.ErrorMessage))}");
        }
    }
}

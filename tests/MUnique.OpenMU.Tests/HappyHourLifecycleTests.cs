// <copyright file="HappyHourLifecycleTests.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Tests;

using Moq;
using MUnique.OpenMU.AttributeSystem;
using MUnique.OpenMU.GameLogic;
using MUnique.OpenMU.GameLogic.Attributes;
using MUnique.OpenMU.GameLogic.PlugIns.PeriodicTasks;
using MUnique.OpenMU.GameLogic.Views;
using MUnique.OpenMU.Interfaces;

/// <summary>
/// Exercises Happy Hour's real periodic lifecycle and its effect on an existing player and a late entrant.
/// </summary>
[TestFixture]
public class HappyHourLifecycleTests
{
    /// <summary>
    /// Starts the event, applies both experience multipliers, admits a late player, and naturally restores both rates.
    /// </summary>
    [Test]
    public async Task NaturalFinishRestoresExperienceForExistingAndLatePlayersAsync()
    {
        var first = await PlayerTestHelper.CreatePlayerAsync().ConfigureAwait(false);
        await using var context = (GameContext)first.GameContext;
        context.StopPeriodicTasks();
        var plugIn = new HappyHourPlugIn
        {
            Configuration = new HappyHourConfiguration
            {
                Timetable = [],
                TaskDuration = TimeSpan.FromMilliseconds(50),
                PreStartMessageDelay = TimeSpan.Zero,
                ExperienceMultiplier = 2,
                StartMessage = new LocalizedString("Happy Hour test"),
            },
        };
        await PreparePlayerAsync(first, context).ConfigureAwait(false);
        var firstRate = first.Attributes![Stats.ExperienceRate];
        var firstMasterRate = first.Attributes[Stats.MasterExperienceRate];
        await plugIn.PlayerStateChangedAsync(first, PlayerState.CharacterSelection, PlayerState.EnteredWorld).ConfigureAwait(false);
        plugIn.ForceStart();
        await plugIn.ExecuteTaskAsync(context).ConfigureAwait(false);
        await plugIn.ExecuteTaskAsync(context).ConfigureAwait(false);
        Assert.That(first.Attributes[Stats.ExperienceRate], Is.EqualTo(firstRate * 2));
        Assert.That(first.Attributes[Stats.MasterExperienceRate], Is.EqualTo(firstMasterRate * 2));

        var late = await PlayerTestHelper.CreatePlayerAsync(context).ConfigureAwait(false);
        await PreparePlayerAsync(late, context).ConfigureAwait(false);
        var lateRate = late.Attributes![Stats.ExperienceRate];
        var lateMasterRate = late.Attributes[Stats.MasterExperienceRate];
        await plugIn.PlayerStateChangedAsync(late, PlayerState.CharacterSelection, PlayerState.EnteredWorld).ConfigureAwait(false);
        Assert.That(late.Attributes[Stats.ExperienceRate], Is.EqualTo(lateRate * 2));
        Assert.That(late.Attributes[Stats.MasterExperienceRate], Is.EqualTo(lateMasterRate * 2));
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (plugIn.GetMultiplierElement(context).Value != 1 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10).ConfigureAwait(false);
            await plugIn.ExecuteTaskAsync(context).ConfigureAwait(false);
        }

        Assert.That(plugIn.GetMultiplierElement(context).Value, Is.EqualTo(1));
        await plugIn.ExecuteTaskAsync(context).ConfigureAwait(false);
        Assert.That(first.Attributes[Stats.ExperienceRate], Is.EqualTo(firstRate));
        Assert.That(first.Attributes[Stats.MasterExperienceRate], Is.EqualTo(firstMasterRate));
        Assert.That(late.Attributes[Stats.ExperienceRate], Is.EqualTo(lateRate));
        Assert.That(late.Attributes[Stats.MasterExperienceRate], Is.EqualTo(lateMasterRate));
        foreach (var player in new[] { first, late })
        {
            Mock.Get(player.ViewPlugIns.GetPlugIn<IShowMessagePlugIn>()!)
                .Verify(view => view.ShowMessageAsync("Happy Hour test", MessageType.GoldenCenter), Times.Once);
        }
    }

    private static async Task PreparePlayerAsync(Player player, GameContext context)
    {
        player.Attributes!.AddElement(new SimpleElement(1, AggregateType.AddRaw), Stats.ExperienceRate);
        player.Attributes.AddElement(new SimpleElement(1, AggregateType.AddRaw), Stats.MasterExperienceRate);
        await player.PlayerState.TryAdvanceToAsync(PlayerState.EnteredWorld).ConfigureAwait(false);
        await context.AddPlayerAsync(player).ConfigureAwait(false);
    }
}

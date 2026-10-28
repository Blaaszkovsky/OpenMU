// <copyright file="MiniGameOpeningStateRequestActionTests.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Tests;

using System.Globalization;
using Moq;
using MUnique.OpenMU.DataModel.Configuration;
using MUnique.OpenMU.GameLogic;
using MUnique.OpenMU.GameLogic.MiniGames;
using MUnique.OpenMU.GameLogic.PlayerActions.MiniGames;
using MUnique.OpenMU.GameLogic.PlugIns.PeriodicTasks;
using MUnique.OpenMU.GameLogic.Properties;
using MUnique.OpenMU.GameLogic.Views;
using MUnique.OpenMU.Interfaces;
using BasicModel = MUnique.OpenMU.Persistence.BasicModel;

/// <summary>
/// Tests localized opening-state responses when an event has no periodic start strategy.
/// </summary>
[TestFixture]
public class MiniGameOpeningStateRequestActionTests
{
    /// <summary>
    /// Verifies the correct fallback response reaches only the requesting world player.
    /// </summary>
    /// <param name="miniGameType">The requested event.</param>
    /// <param name="messageKey">The expected localized response resource.</param>
    [TestCase(MiniGameType.BloodCastle, nameof(PlayerMessage.EventMapCreatedOnEntranceWithoutTimetable))]
    [TestCase(MiniGameType.DevilSquare, nameof(PlayerMessage.EventMapCreatedOnEntranceWithoutTimetable))]
    [TestCase(MiniGameType.Doppelganger, nameof(PlayerMessage.EventMapCreatedOnEntranceWithoutTimetable))]
    [TestCase(MiniGameType.IllusionTemple, nameof(PlayerMessage.EventMapCreatedOnEntranceWithoutTimetable))]
    public async ValueTask FallbackOpeningStateRepliesOnlyToRequesterAsync(MiniGameType miniGameType, string messageKey)
    {
        var requester = await PlayerTestHelper.CreatePlayerAsync().ConfigureAwait(false);
        await using var context = (GameContext)requester.GameContext;
        context.StopPeriodicTasks();
        Mock.Get(context.Configuration).SetupGet(configuration => configuration.MiniGameDefinitions)
            .Returns(Enumerable.Range(1, 4).Select(level => (MiniGameDefinition)new BasicModel.MiniGameDefinition
            {
                Id = Guid.NewGuid(),
                Type = MiniGameType.Doppelganger,
                GameLevel = (byte)level,
                MapCreationPolicy = MiniGameMapCreationPolicy.OnePerParty,
            }).ToList());

        var observer = await PlayerTestHelper.CreatePlayerAsync(context).ConfigureAwait(false);
        await context.AddPlayerAsync(requester).ConfigureAwait(false);
        await context.AddPlayerAsync(observer).ConfigureAwait(false);
        await requester.PlayerState.TryAdvanceToAsync(PlayerState.EnteredWorld).ConfigureAwait(false);
        await observer.PlayerState.TryAdvanceToAsync(PlayerState.EnteredWorld).ConfigureAwait(false);
        requester.Culture = CultureInfo.GetCultureInfo("de-DE");
        Assert.That(context.PlugInManager.GetStrategy<MiniGameType, IPeriodicMiniGameStartPlugIn>(miniGameType), Is.Null);
        Assert.That(requester.PlayerState.CurrentState, Is.SameAs(PlayerState.EnteredWorld));

        var requesterView = Mock.Get(requester.ViewPlugIns.GetPlugIn<IShowMessagePlugIn>()!);
        var observerView = Mock.Get(observer.ViewPlugIns.GetPlugIn<IShowMessagePlugIn>()!);
        requesterView.Invocations.Clear();
        observerView.Invocations.Clear();

        await new MiniGameOpeningStateRequestAction().HandleRequestAsync(requester, miniGameType, 1).ConfigureAwait(false);

        var expectedMessage = requester.GetLocalizedMessage(messageKey);
        requesterView.Verify(view => view.ShowMessageAsync(expectedMessage, MessageType.BlueNormal), Times.Once);
        requesterView.Verify(view => view.ShowMessageAsync(It.IsAny<string>(), It.IsAny<MessageType>()), Times.Once);
        observerView.Verify(view => view.ShowMessageAsync(It.IsAny<string>(), It.IsAny<MessageType>()), Times.Never);
        Mock.Get(requester.ViewPlugIns.GetPlugIn<IShowMiniGameOpeningStatePlugIn>()!)
            .Verify(view => view.ShowOpeningStateAsync(It.IsAny<MiniGameType>(), It.IsAny<TimeSpan?>(), It.IsAny<int>()), Times.Never);
    }
}

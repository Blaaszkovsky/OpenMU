// <copyright file="WorldNotificationTests.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Tests;

using System.Reflection;
using Moq;
using MUnique.OpenMU.GameLogic;
using MUnique.OpenMU.GameLogic.Crywolf;
using MUnique.OpenMU.GameLogic.PlugIns;
using MUnique.OpenMU.GameLogic.PlugIns.InvasionEvents;
using MUnique.OpenMU.GameLogic.PlugIns.PeriodicTasks;
using MUnique.OpenMU.GameLogic.Properties;
using MUnique.OpenMU.GameLogic.Raklion;
using MUnique.OpenMU.GameLogic.Views;
using MUnique.OpenMU.Interfaces;

/// <summary>
/// World announcements must not reach the login or character selection screen.
/// </summary>
[TestFixture]
public class WorldNotificationTests
{
    private static IEnumerable<TestCaseData> NotificationCases
    {
        get
        {
            foreach (var scene in new[] { "selection", "world", "dead", "logout", "disconnected" })
            {
                foreach (var route in new[] { "global", "localized", "notification", "translated", "rotation", "invasion-start", "invasion-end", "crywolf", "crywolf-map", "raklion", "raklion-result", "happy-hour" })
                {
                    yield return new TestCaseData(scene, route);
                }
            }
        }
    }

    /// <summary>
    /// Checks the real broadcast paths, including a selected character whose world entry is not ready yet.
    /// </summary>
    /// <param name="scene">The session phase.</param>
    /// <param name="route">The source of the announcement.</param>
    [TestCaseSource(nameof(NotificationCases))]
    public async ValueTask AnnouncementsOnlyReachCharactersInTheWorldAsync(string scene, string route)
    {
        var player = await PlayerTestHelper.CreatePlayerAsync().ConfigureAwait(false);
        await using var context = (GameContext)player.GameContext;
        context.StopPeriodicTasks();
        await context.AddPlayerAsync(player).ConfigureAwait(false);
        if (scene == "selection")
        {
            await player.PlayerState.TryAdvanceToAsync(PlayerState.CharacterSelection).ConfigureAwait(false);
            Assert.That(player.PlayerState.CurrentState, Is.SameAs(PlayerState.CharacterSelection));
        }
        else
        {
            await player.PlayerState.TryAdvanceToAsync(PlayerState.EnteredWorld).ConfigureAwait(false);
        }

        if (scene == "dead")
        {
            await player.PlayerState.TryAdvanceToAsync(PlayerState.Dead).ConfigureAwait(false);
        }
        else if (scene == "disconnected")
        {
            await player.PlayerState.TryAdvanceToAsync(PlayerState.Disconnected).ConfigureAwait(false);
        }
        else if (scene == "logout")
        {
            await player.PlayerState.TryAdvanceToAsync(PlayerState.CharacterSelection).ConfigureAwait(false);
            await player.SetSelectedCharacterAsync(null).ConfigureAwait(false);
        }

        var view = Mock.Get(player.ViewPlugIns.GetPlugIn<IShowMessagePlugIn>()!);
        view.Invocations.Clear();
        await BroadcastAsync(context, route).ConfigureAwait(false);

        view.Verify(p => p.ShowMessageAsync(It.IsAny<string>(), It.IsAny<MessageType>()),
            scene is "world" or "dead" ? Times.Exactly(route == "raklion-result" ? 2 : 1) : Times.Never());
    }

    private static async ValueTask BroadcastAsync(GameContext context, string route)
    {
        switch (route)
        {
            case "global":
                await context.SendGlobalMessageAsync("Event", MessageType.GoldenCenter).ConfigureAwait(false);
                break;
            case "localized":
                await context.ShowGlobalLocalizedMessageAsync(MessageType.BlueNormal, nameof(PlayerMessage.ItemCannotBeDropped)).ConfigureAwait(false);
                break;
            case "notification":
                await context.SendGlobalNotificationAsync("!Event").ConfigureAwait(false);
                break;
            case "translated":
                await context.SendGlobalNotificationAsync(new LocalizedString("Event {0}"), 2).ConfigureAwait(false);
                break;
            case "rotation":
                var rotation = new ServerAnnouncementsPlugIn
                {
                    Configuration = new ServerAnnouncementsConfiguration { Messages = { new() { Text = "Event" } } },
                };
                await rotation.ExecuteTaskAsync(context).ConfigureAwait(false);
                break;
            case "crywolf":
                using (var crywolf = new CrywolfContext(context, new CrywolfEventDefinition()))
                {
                    var send = typeof(CrywolfContext).GetMethod("ShowMessageToAllPlayersAsync", BindingFlags.Instance | BindingFlags.NonPublic, null, [typeof(Func<Player, string>)], null)!;
                    await ((ValueTask)send.Invoke(crywolf, new object[] { (Func<Player, string>)(_ => "Event") })!).ConfigureAwait(false);
                }

                break;
            case "crywolf-map":
                using (var crywolf = new CrywolfContext(context, new CrywolfEventDefinition { MapNumber = 0, MonsterSkills = [] }))
                {
                    Mock.Get(context.Configuration.Maps.Single()).SetupGet(map => map.TerrainVariants)
                        .Returns(new List<DataModel.Configuration.GameMapTerrainVariant>());
                    await crywolf.InitializeAsync().ConfigureAwait(false);
                    var send = typeof(CrywolfContext).GetMethod("ShowMessageToMapPlayersAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
                    await ((ValueTask)send.Invoke(crywolf, new object[] { nameof(PlayerMessage.ItemCannotBeDropped), Array.Empty<object>() })!).ConfigureAwait(false);
                }

                break;
            case "raklion":
            case "raklion-result":
                using (var raklion = new RaklionContext(context, new RaklionEventDefinition()))
                {
                    var send = typeof(RaklionContext).GetMethod("ShowMessageToAllPlayersAsync", BindingFlags.Instance | BindingFlags.NonPublic, null, [typeof(Func<Player, string>), typeof(bool)], null)!;
                    await ((ValueTask)send.Invoke(raklion, new object[] { (Func<Player, string>)(_ => "Event"), route == "raklion-result" })!).ConfigureAwait(false);
                }

                break;
            case "happy-hour":
                var happyHour = new TestHappyHour { Configuration = new HappyHourConfiguration { StartMessage = new LocalizedString("Event") } };
                await happyHour.AnnounceAsync((await context.GetPlayersAsync().ConfigureAwait(false)).Single()).ConfigureAwait(false);
                break;
            default:
                var invasion = new TestInvasion { Configuration = new PeriodicInvasionConfiguration() };
                var state = new InvasionGameServerState(context);
                state.RegisterMap(0, 43);
                state.SetAnnouncedMaps(new ushort[] { 0 });
                await invasion.AnnounceAsync(state, route == "invasion-start").ConfigureAwait(false);
                break;
        }
    }

    private sealed class TestInvasion : BaseInvasionPlugIn<PeriodicInvasionConfiguration>
    {
        public ValueTask AnnounceAsync(InvasionGameServerState state, bool start)
            => start ? this.OnPreparedAsync(state) : this.OnFinishedAsync(state);
    }

    private sealed class TestHappyHour : HappyHourPlugIn
    {
        public Task AnnounceAsync(Player player) => this.TrySendStartMessageAsync(player);
    }
}

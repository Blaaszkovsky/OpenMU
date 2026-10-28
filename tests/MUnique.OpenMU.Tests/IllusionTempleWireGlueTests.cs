// <copyright file="IllusionTempleWireGlueTests.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Tests;

using MUnique.OpenMU.GameLogic;
using MUnique.OpenMU.GameLogic.MiniGames;
using MUnique.OpenMU.GameLogic.MiniGames.IllusionTemple;
using MUnique.OpenMU.GameLogic.Views;
using MUnique.OpenMU.DataModel.Configuration;
using MUnique.OpenMU.GameServer.MessageHandler;
using MUnique.OpenMU.GameServer.MessageHandler.MuHelper;
using MUnique.OpenMU.GameServer.MessageHandler.MiniGames;
using MUnique.OpenMU.GameServer.RemoteView.MiniGames;
using MUnique.OpenMU.Network.Packets.ServerToClient;
using MUnique.OpenMU.Pathfinding;

/// <summary>Checks the Illusion Temple glue against the legacy C1/BF wire contracts.</summary>
[TestFixture]
public class IllusionTempleWireGlueTests
{
    /// <summary>All event phases and skill views use their BF-specific records.</summary>
    [Test]
    public async Task PhaseAndSkillViewsUseIllusionTemplePacketsAsync()
    {
        var (player, output) = CastleSiegeRemoteViewTestHelper.CreatePlayer();
        var view = new IllusionTempleViewPlugIn(player);

        await view.ShowPhaseAsync(3, 2).ConfigureAwait(false);
        await view.ShowSkillPointsAsync(6).ConfigureAwait(false);
        await view.ShowSkillResultAsync(0x1234, player, player, true).ConfigureAwait(false);
        await view.ShowSkillEndAsync(0x2345, player).ConfigureAwait(false);

        var data = output.ToArray().AsMemory();
        var offset = 0;
        var phase = (IllusionTempleSkillEnd)Next(IllusionTempleSkillEnd.Length);
        Assert.That(phase.TempleNumber, Is.EqualTo(3));
        Assert.That(phase.State, Is.EqualTo(2));

        var points = (IllusionTempleSkillPointUpdate)Next(IllusionTempleSkillPointUpdate.Length);
        Assert.That(points.SkillPoints, Is.EqualTo(6));

        var skill = (IllusionTempleSkillUsageResult)Next(IllusionTempleSkillUsageResult.Length);
        Assert.That(skill.Result, Is.EqualTo(1));
        Assert.That(skill.SkillNumber, Is.EqualTo(0x1234));
        Assert.That(skill.SourceObjectId, Is.EqualTo(ViewExtensions.ConstantPlayerId));
        Assert.That(skill.TargetObjectId, Is.EqualTo(ViewExtensions.ConstantPlayerId));

        var skillEnd = (IllusionTempleSkillEnded)Next(IllusionTempleSkillEnded.Length);
        Assert.That(skillEnd.SkillNumber, Is.EqualTo(0x2345));
        Assert.That(skillEnd.ObjectIndex, Is.EqualTo(ViewExtensions.ConstantPlayerId));

        Memory<byte> Next(int length)
        {
            var packet = data.Slice(offset, length);
            offset += length;
            return packet;
        }
    }

    /// <summary>The state packet carries the authoritative holder, relic position, scores and team member positions.</summary>
    [Test]
    public async Task StateViewSerializesHolderScoresAndMembersAsync()
    {
        var (player, output) = CastleSiegeRemoteViewTestHelper.CreatePlayer();
        await new IllusionTempleViewPlugIn(player).ShowStateAsync(
            TimeSpan.FromSeconds(42), player, new Point(17, 29), 4, 2, 1, [player]).ConfigureAwait(false);

        var data = output.ToArray().AsMemory();
        var state = (IllusionTempleState)data;
        Assert.That(data.Length, Is.EqualTo(IllusionTempleStateRef.GetRequiredSize(1)));
        Assert.That(state.RemainingSeconds, Is.EqualTo(42));
        Assert.That(state.PlayerIndex, Is.EqualTo(ViewExtensions.ConstantPlayerId));
        Assert.That(state.PositionX, Is.EqualTo(17));
        Assert.That(state.PositionY, Is.EqualTo(29));
        Assert.That(state.Team1Points, Is.EqualTo(4));
        Assert.That(state.Team2Points, Is.EqualTo(2));
        Assert.That(state.MyTeam, Is.EqualTo(1));
        Assert.That(state.PartyCount, Is.EqualTo(1));
        Assert.That(state[0].PlayerId, Is.EqualTo(ViewExtensions.ConstantPlayerId));
    }

    /// <summary>Result rows retain both team scores and the signed experience bit pattern expected by the client.</summary>
    [Test]
    public async Task ResultViewSerializesScoresAndRewardRowsAsync()
    {
        var (player, output) = CastleSiegeRemoteViewTestHelper.CreatePlayer();
        await new IllusionTempleViewPlugIn(player).ShowResultAsync(7, 3, [(player, (byte)1, 123456)]).ConfigureAwait(false);

        var data = output.ToArray().AsMemory();
        var result = (IllusionTempleResult)data;
        Assert.That(data.Length, Is.EqualTo(IllusionTempleResultRef.GetRequiredSize(1)));
        Assert.That(result.Team1Points, Is.EqualTo(7));
        Assert.That(result.Team2Points, Is.EqualTo(3));
        Assert.That(result.PlayerCount, Is.EqualTo(1));
        Assert.That(result[0].Team, Is.EqualTo(1));
        Assert.That(result[0].AddedExperience, Is.EqualTo(123456u));
    }

    /// <summary>Entry count updates always serialize exactly six level counters.</summary>
    [Test]
    public async Task PlayerCountsArePaddedToSixLevelsAsync()
    {
        var (player, output) = CastleSiegeRemoteViewTestHelper.CreatePlayer();
        await new IllusionTempleViewPlugIn(player).ShowPlayerCountsAsync([1, 2, 3]).ConfigureAwait(false);

        var packet = (IllusionTempleUserCount)output.ToArray().AsMemory();
        Assert.That(packet.UserCount1, Is.EqualTo(1));
        Assert.That(packet.UserCount2, Is.EqualTo(2));
        Assert.That(packet.UserCount3, Is.EqualTo(3));
        Assert.That(packet.UserCount4, Is.Zero);
        Assert.That(packet.UserCount5, Is.Zero);
        Assert.That(packet.UserCount6, Is.Zero);
    }

    /// <summary>Mini-game state notifications are translated to the IT-specific phase packet.</summary>
    [Test]
    public async Task StateNotificationsUseIllusionTemplePhasePacketAsync()
    {
        var (player, output) = CastleSiegeRemoteViewTestHelper.CreatePlayer();
        player.GameContext.PlugInManager.DiscoverAndRegisterPlugIns(typeof(IllusionTempleViewPlugIn).Assembly);
        var view = new UpdateMiniGameStateViewViewPlugIn(player);

        await view.UpdateStateAsync(MiniGameType.IllusionTemple, MiniGameState.Open).ConfigureAwait(false);
        await view.UpdateStateAsync(MiniGameType.IllusionTemple, MiniGameState.Closed).ConfigureAwait(false);
        await view.UpdateStateAsync(MiniGameType.IllusionTemple, MiniGameState.Playing).ConfigureAwait(false);
        await view.UpdateStateAsync(MiniGameType.IllusionTemple, MiniGameState.Ended).ConfigureAwait(false);

        var data = output.ToArray();
        Assert.That(data.Length, Is.EqualTo(4 * IllusionTempleSkillEnd.Length));
        var phases = Enumerable.Range(0, 4)
            .Select(index => (IllusionTempleSkillEnd)data.AsMemory(index * IllusionTempleSkillEnd.Length, IllusionTempleSkillEnd.Length))
            .Select(packet => packet.State)
            .ToArray();
        Assert.That(phases, Is.EqualTo(new byte[] { 0, 1, 2, 3 }));
    }

    /// <summary>BF subhandlers ignore truncated and out-of-event requests without sending arbitrary results.</summary>
    [Test]
    public async Task RequestHandlersRejectTruncatedAndUnmatchedRequestsAsync()
    {
        var (player, output) = CastleSiegeRemoteViewTestHelper.CreatePlayer();
        var enter = new IllusionTempleEnterRequestHandlerPlugIn();
        var skill = new IllusionTempleSkillRequestHandlerPlugIn();
        var reward = new IllusionTempleRewardRequestHandlerPlugIn();

        Assert.That(enter.Key, Is.EqualTo(0x00));
        Assert.That(skill.Key, Is.EqualTo(0x02));
        Assert.That(reward.Key, Is.EqualTo(0x05));
        Assert.That(enter.IsEncryptionExpected || skill.IsEncryptionExpected || reward.IsEncryptionExpected, Is.False);
        Assert.That(GetGroup(enter), Is.EqualTo(MuHelperGroupHandler.GroupKey));
        Assert.That(GetGroup(skill), Is.EqualTo(MuHelperGroupHandler.GroupKey));
        Assert.That(GetGroup(reward), Is.EqualTo(MuHelperGroupHandler.GroupKey));

        await enter.HandlePacketAsync(player, new byte[] { 0xC1, 0x04, 0xBF, 0x00 }).ConfigureAwait(false);
        await skill.HandlePacketAsync(player, new byte[] { 0xC1, 0x04, 0xBF, 0x02 }).ConfigureAwait(false);
        await reward.HandlePacketAsync(player, new byte[] { 0xC1, 0x04, 0xBF, 0x05 }).ConfigureAwait(false);

        Assert.That(player.CurrentMiniGame, Is.Null);
        Assert.That(output.Length, Is.Zero);

        static byte? GetGroup(object handler) => (Attribute.GetCustomAttribute(handler.GetType(), typeof(BelongsToGroupAttribute)) as BelongsToGroupAttribute)?.GroupKey;
    }
}

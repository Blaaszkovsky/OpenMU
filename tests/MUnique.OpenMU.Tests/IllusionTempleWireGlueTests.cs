// <copyright file="IllusionTempleWireGlueTests.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Tests;

using MUnique.OpenMU.GameLogic;
using MUnique.OpenMU.GameLogic.Attributes;
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
using Moq;
using ClientPackets = MUnique.OpenMU.Network.Packets.ClientToServer;
using World = MUnique.OpenMU.Tests.MiniGames.IllusionTempleLifecycleTests.TestWorld;

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
        var phase = (IllusionTempleEventState)Next(IllusionTempleEventState.Length);
        Assert.That(phase.TempleNumber, Is.EqualTo(3));
        Assert.That(phase.State, Is.EqualTo(IllusionTempleEventState.EventState.BattleStarted));

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
        Assert.That(state.RelicCarrierId, Is.EqualTo(ViewExtensions.ConstantPlayerId));
        Assert.That(state.PositionX, Is.EqualTo(17));
        Assert.That(state.PositionY, Is.EqualTo(29));
        Assert.That(state.AlliedForcesPoints, Is.EqualTo(4));
        Assert.That(state.IllusionForcesPoints, Is.EqualTo(2));
        Assert.That(state.MyTeam, Is.EqualTo(1));
        Assert.That(state.PartyCount, Is.EqualTo(1));
        Assert.That(state[0].PlayerId, Is.EqualTo(ViewExtensions.ConstantPlayerId));
    }

    /// <summary>Result rows retain both team scores and the signed experience bit pattern expected by the client.</summary>
    [Test]
    public async Task ResultViewSerializesScoresAndRewardRowsAsync()
    {
        var (player, output) = CastleSiegeRemoteViewTestHelper.CreatePlayer();
        var connection = player.Connection ?? throw new AssertionException("The remote fixture must have its configured connection.");
        connection.Output.GetSpan(47)[..47].Fill(0xCC);
        await new IllusionTempleViewPlugIn(player).ShowResultAsync(7, 3,
            [(player, (byte)1, 123456), (player, (byte)0, 654321)]).ConfigureAwait(false);

        var data = output.ToArray().AsMemory();
        var result = (IllusionTempleResult)data;
        Assert.That(data.Length, Is.EqualTo(IllusionTempleResultRef.GetRequiredSize(2)));
        Assert.That(result.Team1Points, Is.EqualTo(7));
        Assert.That(result.Team2Points, Is.EqualTo(3));
        Assert.That(result.PlayerCount, Is.EqualTo(2));
        Assert.That(data.Length, Is.EqualTo(47));
        Assert.That(data.Span.Slice(20, 3).ToArray(), Is.EqualTo(new byte[3]));
        Assert.That(data.Span.Slice(40, 3).ToArray(), Is.EqualTo(new byte[3]));
        Assert.That(result[1].Team, Is.Zero);
        Assert.That(result[1].AddedExperience, Is.EqualTo(654321u));
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
        Assert.That(data.Length, Is.EqualTo(4 * IllusionTempleEventState.Length));
        var phases = Enumerable.Range(0, 4)
            .Select(index => (IllusionTempleEventState)data.AsMemory(index * IllusionTempleEventState.Length, IllusionTempleEventState.Length))
            .Select(packet => (byte)packet.State)
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

    /// <summary>The Extended handler preserves a live target above the legacy byte range.</summary>
    [Test]
    public async Task ExtendedSkillHandlerKeepsFullTargetIdAsync()
    {
        await using var world = await World.CreateAsync(4).ConfigureAwait(false);
        await world.StartAsync().ConfigureAwait(false);
        var (caster, target) = await EarnPointsAsync(world).ConfigureAwait(false);
        Assert.That(target.Id, Is.GreaterThan(byte.MaxValue));
        var bytes = new byte[ClientPackets.IllusionTempleSkillRequestExtended.Length];
        _ = new ClientPackets.IllusionTempleSkillRequestExtended(bytes)
        {
            SkillNumber = 213, TargetObjectIndex = target.Id, Distance = byte.MaxValue,
        };
        target.Attributes![Stats.CurrentShield] = 100;
        await new IllusionTempleSkillRequestHandlerPlugIn().HandlePacketAsync(caster, bytes).ConfigureAwait(false);
        Assert.That(target.Attributes[Stats.CurrentShield], Is.EqualTo(50));
    }

    /// <summary>The original eight-byte request still accepts the original self-protection action.</summary>
    [Test]
    public async Task LegacySkillHandlerAcceptsOriginalRequestAsync()
    {
        await using var world = await World.CreateAsync(4).ConfigureAwait(false);
        await world.StartAsync().ConfigureAwait(false);
        var (caster, _) = await EarnPointsAsync(world).ConfigureAwait(false);
        var bytes = new byte[ClientPackets.IllusionTempleSkillRequest.Length];
        _ = new ClientPackets.IllusionTempleSkillRequest(bytes) { SkillNumber = 210, TargetObjectIndex = 7, Distance = 0 };
        await new IllusionTempleSkillRequestHandlerPlugIn().HandlePacketAsync(caster, bytes).ConfigureAwait(false);
        Assert.That(caster.MagicEffectList.ContainsEffect(202), Is.True);
    }

    /// <summary>A funded participant cannot make malformed request lengths mutate a target or produce a skill result.</summary>
    [TestCase(7, 7)]
    [TestCase(10, 10)]
    [TestCase(8, 9)]
    [TestCase(9, 8)]
    public async Task SkillHandlerRejectsNoncanonicalLengthsAsync(int size, byte declaredSize)
    {
        await using var world = await World.CreateAsync(4).ConfigureAwait(false);
        await world.StartAsync().ConfigureAwait(false);
        var (caster, target) = await EarnPointsAsync(world).ConfigureAwait(false);
        var bytes = new byte[size];
        bytes[0] = 0xC1;
        bytes[1] = declaredSize;
        bytes[2] = 0xBF;
        bytes[3] = 2;
        bytes[5] = 213;
        bytes[6] = (byte)target.Id;
        if (size > 7)
        {
            bytes[7] = (byte)(target.Id >> 8);
        }

        target.Attributes![Stats.CurrentShield] = 100;
        var views = Mock.Get(caster.ViewPlugIns.GetPlugIn<IIllusionTempleViewPlugIn>()!);
        views.Invocations.Clear();
        await new IllusionTempleSkillRequestHandlerPlugIn().HandlePacketAsync(caster, bytes).ConfigureAwait(false);
        Assert.That(target.Attributes[Stats.CurrentShield], Is.EqualTo(100));
        views.Verify(view => view.ShowSkillResultAsync(It.IsAny<ushort>(), It.IsAny<Player>(), It.IsAny<Player>(), It.IsAny<bool>()), Times.Never);
    }

    private static async ValueTask<(Player Caster, Player Target)> EarnPointsAsync(World world)
    {
        var caster = world.Players[0];
        var opponents = world.Players.Where(player => world.Game.GetTeam(player) != world.Game.GetTeam(caster)).ToArray();
        caster.Position = new Point(100, 100);
        opponents[0].Position = caster.Position;
        var victimAttributes = opponents[0].Attributes ?? throw new AssertionException("The combat fixture must initialize the opposing player's attributes.");
        victimAttributes[Stats.CurrentShield] = 0;
        victimAttributes[Stats.CurrentHealth] = 1;
        await opponents[0].AttackByAsync(caster, null, false).ConfigureAwait(false);
        Assert.That(opponents[0].IsAlive, Is.False, "The caster must earn its points through normal opposing-player combat.");
        opponents[1].Position = new Point(101, 100);
        return (caster, opponents[1]);
    }

}

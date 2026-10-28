// <copyright file="IllusionTemplePacketContractTests.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Network.Packets.Tests;

using System.Buffers.Binary;
using MUnique.OpenMU.Network.Packets.ServerToClient;
using ClientPackets = MUnique.OpenMU.Network.Packets.ClientToServer;

/// <summary>
/// Verifies the packed Season Six Illusion Temple wire contract without native structure padding.
/// </summary>
[TestFixture]
public class IllusionTemplePacketContractTests
{
    /// <summary>
    /// The remaining time and relic holder occupy different words, followed by five-byte team entries.
    /// </summary>
    [Test]
    public void StateKeepsRemainingTimeAndCarrierSeparate()
    {
        var data = new byte[19];
        var packet = new IllusionTempleState(data)
        {
            RemainingSeconds = 513,
            PlayerIndex = 0x1234,
            PositionX = 80,
            PositionY = 90,
            Team1Points = 2,
            Team2Points = 3,
            MyTeam = 1,
            PartyCount = 1,
        };
        var member = packet[0];
        member.PlayerId = 0x5678;
        member.MapNumber = 45;
        member.PositionX = 70;
        member.PositionY = 75;

        Assert.Multiple(() =>
        {
            Assert.That(IllusionTempleStateRef.GetRequiredSize(1), Is.EqualTo(19));
            Assert.That(data[4..14], Is.EqualTo(new byte[] { 1, 2, 0x34, 0x12, 80, 90, 2, 3, 1, 1 }));
            Assert.That(data[14..19], Is.EqualTo(new byte[] { 0x78, 0x56, 45, 70, 75 }));
        });
    }

    /// <summary>
    /// A one-byte map number must not overwrite the next coordinate.
    /// </summary>
    [Test]
    public void TeamMapNumberDoesNotOverwritePosition()
    {
        var data = new byte[19];
        var packet = new IllusionTempleState(data) { PartyCount = 1 };
        var member = packet[0];
        member.PositionX = 123;
        member.PositionY = 124;
        member.MapNumber = 45;

        Assert.That(member.PositionX, Is.EqualTo(123));
    }

    /// <summary>
    /// Results have a seven-byte prefix and seventeen-byte player entries with unaligned signed experience.
    /// </summary>
    [Test]
    public void ResultUsesPackedPlayerRows()
    {
        var data = new byte[27];
        var packet = new IllusionTempleResult(data) { Team1Points = 7, Team2Points = 2, PlayerCount = 1 };
        var result = packet[0];
        result.Name = "TestPlayer";
        result.MapNumber = 45;
        result.Team = 1;
        result.Class = 2;
        result.AddedExperience = unchecked((uint)-1234);

        Assert.Multiple(() =>
        {
            Assert.That(IllusionTempleResultRef.GetRequiredSize(1), Is.EqualTo(24));
            Assert.That(data[7..17], Is.EqualTo("TestPlayer"u8.ToArray()));
            Assert.That(data[17..20], Is.EqualTo(new byte[] { 45, 1, 2 }));
            Assert.That(BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(20)), Is.EqualTo(-1234));
        });
    }

    /// <summary>
    /// Skill targets retain all sixteen object-id bits and the distance follows the complete id.
    /// </summary>
    [Test]
    public void SkillRequestPreservesFullTargetIndex()
    {
        var data = new byte[] { 0xC1, 9, 0xBF, 2, 0, 210, 0x34, 0x12, 5 };
        var packet = new ClientPackets.IllusionTempleSkillRequest(data);

        Assert.Multiple(() =>
        {
            Assert.That(ClientPackets.IllusionTempleSkillRequest.Length, Is.EqualTo(9));
            Assert.That(packet.TargetObjectIndex, Is.EqualTo(0x1234));
            Assert.That(packet.Distance, Is.EqualTo(5));
        });
    }

    /// <summary>
    /// Phase updates use BF:09, leaving BF:07 exclusively for ended skills.
    /// </summary>
    [Test]
    public void PhaseUpdateDoesNotCollideWithSkillEnded()
    {
        var data = new byte[6];
        _ = new IllusionTempleSkillEnd(data) { TempleNumber = 1, State = 2 };

        Assert.That(data[..4], Is.EqualTo(new byte[] { 0xC1, 6, 0xBF, 9 }));
    }
}

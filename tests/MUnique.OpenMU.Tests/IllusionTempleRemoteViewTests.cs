// <copyright file="IllusionTempleRemoteViewTests.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Tests;

using MUnique.OpenMU.DataModel.Configuration;
using MUnique.OpenMU.GameLogic.PlayerActions.MiniGames;
using MUnique.OpenMU.GameServer.RemoteView.MiniGames;

/// <summary>Checks the actual bytes emitted for Illusion Temple entry.</summary>
[TestFixture]
public class IllusionTempleRemoteViewTests
{
    /// <summary>Entry results must select the client's matching Illusion Temple message.</summary>
    /// <param name="result">The admission outcome.</param>
    /// <param name="code">The client's message code.</param>
    [TestCase(EnterResult.Success, 0)]
    [TestCase(EnterResult.Failed, 3)]
    [TestCase(EnterResult.NotOpen, 1)]
    [TestCase(EnterResult.CharacterLevelTooLow, 3)]
    [TestCase(EnterResult.CharacterLevelTooHigh, 3)]
    [TestCase(EnterResult.Full, 4)]
    [TestCase(EnterResult.NotEnoughMoney, 3)]
    [TestCase(EnterResult.PlayerKillerCantEnter, 7)]
    public async Task EntryResultUsesIllusionTemplePacketAsync(EnterResult result, byte code)
    {
        var (player, output) = CastleSiegeRemoteViewTestHelper.CreatePlayer();
        await new ShowMiniGameEnterResultViewPlugIn(player).ShowResultAsync(MiniGameType.IllusionTemple, result).ConfigureAwait(false);
        Assert.That(output.ToArray(), Is.EqualTo(new byte[] { 0xC1, 5, 0xBF, 0, code }));
    }
}

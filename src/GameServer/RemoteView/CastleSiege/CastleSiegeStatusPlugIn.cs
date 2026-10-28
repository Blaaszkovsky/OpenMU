// <copyright file="CastleSiegeStatusPlugIn.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameServer.RemoteView.CastleSiege;

using System.Runtime.InteropServices;
using MUnique.OpenMU.GameLogic.Views.CastleSiege;
using MUnique.OpenMU.Network;
using MUnique.OpenMU.Network.Packets.ServerToClient;
using MUnique.OpenMU.PlugIns;

/// <summary>
/// The default implementation of the <see cref="ICastleSiegeStatusPlugIn"/>
/// which forwards the Castle Siege status to the game client.
/// </summary>
[PlugIn]
[Display(Name = nameof(PlugInResources.CastleSiegeStatusPlugIn_Name), Description = nameof(PlugInResources.CastleSiegeStatusPlugIn_Description), ResourceType = typeof(PlugInResources))]
[Guid("32D4E4C6-9CA0-4238-ADD8-14649E11A021")]
public class CastleSiegeStatusPlugIn : ICastleSiegeStatusPlugIn
{
    /// <summary>
    /// The result which tells the client that the status could not be determined.
    /// </summary>
    private const byte FailedResult = 0;

    /// <summary>
    /// The result which tells the client to show the status.
    /// </summary>
    private const byte SuccessResult = 1;

    private readonly RemotePlayer _player;

    /// <summary>
    /// Initializes a new instance of the <see cref="CastleSiegeStatusPlugIn"/> class.
    /// </summary>
    /// <param name="player">The player.</param>
    public CastleSiegeStatusPlugIn(RemotePlayer player) => this._player = player;

    /// <inheritdoc />
    public ValueTask ShowRemainingTimeAsync(TimeSpan remainingTime)
    {
        // This packet carries whole minutes; ceiling keeps a running battle from showing zero prematurely.
        var totalMinutes = (int)Math.Clamp(Math.Ceiling(remainingTime.TotalMinutes), 0, (byte.MaxValue * 60) + 59);
        return this._player.Connection.SendCastleSiegeRemainingTimeAsync(
            (byte)(totalMinutes / 60),
            (byte)(totalMinutes % 60));
    }

    /// <inheritdoc />
    public async ValueTask ShowBattleStateAsync(bool isStarted)
    {
        if (this._player.Connection is not { } connection)
        {
            return;
        }

        int WritePacket()
        {
            var length = CastleSiegeBattleStartEndRef.Length;
            var span = connection.Output.GetSpan(length)[..length];
            // The generated boolean setter changes one bit; the client reads the entire byte.
            span.Clear();
            var packet = new CastleSiegeBattleStartEndRef(span);
            packet.IsStarted = isStarted;
            return length;
        }

        await connection.SendAsync(WritePacket).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public ValueTask ShowStatusAsync(CastleSiegeStatus? status)
    {
        if (status is null)
        {
            return this._player.Connection.SendCastleSiegeStatusResponseAsync(
                FailedResult, default, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, string.Empty, string.Empty, 0);
        }

        var start = ClampToWireYear(status.StateStart);
        var end = ClampToWireYear(status.StateEnd);
        var siegeStart = ClampToWireYear(status.SiegeStart);
        var remainingSeconds = (uint)Math.Clamp(Math.Ceiling(status.RemainingTime.TotalSeconds), 0, uint.MaxValue);
        return this._player.Connection.SendCastleSiegeStatusResponseAsync(
            SuccessResult,
            (CastleSiegeState)status.State,
            (ushort)start.Year,
            (byte)start.Month,
            (byte)start.Day,
            (byte)start.Hour,
            (byte)start.Minute,
            (ushort)end.Year,
            (byte)end.Month,
            (byte)end.Day,
            (byte)end.Hour,
            (byte)end.Minute,
            (ushort)siegeStart.Year,
            (byte)siegeStart.Month,
            (byte)siegeStart.Day,
            (byte)siegeStart.Hour,
            (byte)siegeStart.Minute,
            status.OwnerGuildName,
            status.OwnerGuildMasterName,
            remainingSeconds);
    }

    /// <summary>
    /// Replaces the unset sentinel values with the default date, so the client never shows a year like 9999.
    /// </summary>
    private static DateTime ClampToWireYear(DateTime value)
        => value == DateTime.MaxValue ? default : value;
}

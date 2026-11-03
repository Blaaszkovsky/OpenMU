// <copyright file="CastleSiegeRemoteViewTestHelper.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Tests;

using System.Diagnostics.Metrics;
using System.IO;
using System.IO.Pipelines;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using MUnique.OpenMU.DataModel.Configuration;
using MUnique.OpenMU.GameLogic;
using MUnique.OpenMU.GameServer;
using MUnique.OpenMU.GameServer.RemoteView;
using MUnique.OpenMU.Network;
using MUnique.OpenMU.Network.PlugIns;
using MUnique.OpenMU.Persistence;
using MUnique.OpenMU.PlugIns;
using Nito.AsyncEx;

/// <summary>
/// Creates remote players with an in-memory packet output for Castle Siege view tests.
/// </summary>
internal static class CastleSiegeRemoteViewTestHelper
{
    private static readonly Meter OutputMeter = new(typeof(CastleSiegeRemoteViewTestHelper).FullName ?? nameof(CastleSiegeRemoteViewTestHelper));
    private static readonly Counter<long> OutputBytesCounter = OutputMeter.CreateCounter<long>("OutgoingBytes", "bytes");

    /// <summary>
    /// Creates a remote player and its packet output stream.
    /// </summary>
    /// <param name="clientVersion">The client version of the player.</param>
    /// <param name="productionOutput">Whether to use the production output wrapper instead of the raw writer.</param>
    /// <returns>The remote player and output stream.</returns>
    internal static (RemotePlayer Player, MemoryStream Output) CreatePlayer(ClientVersion clientVersion = default, bool productionOutput = false)
    {
        var manager = new PlugInManager(null, new NullLoggerFactory(), null, null);
        var gameContext = new Mock<IGameServerContext>();
        gameContext.Setup(c => c.PersistenceContextProvider)
            .Returns(new Mock<IPersistenceContextProvider>().Object);
        gameContext.Setup(c => c.Configuration).Returns(new GameConfiguration());
        gameContext.Setup(c => c.PlugInManager).Returns(manager);
        gameContext.Setup(c => c.FeaturePlugIns).Returns(new FeaturePlugInContainer(manager));
        gameContext.Setup(c => c.LoggerFactory).Returns(new NullLoggerFactory());
        return CreatePlayer(gameContext.Object, clientVersion, productionOutput);
    }

    /// <summary>
    /// Creates a remote player for an existing game-server context and an in-memory packet output stream.
    /// </summary>
    /// <param name="gameContext">The game-server context.</param>
    /// <param name="clientVersion">The client version of the player.</param>
    /// <param name="productionOutput">Whether to use the production output wrapper instead of the raw writer.</param>
    /// <returns>The remote player and output stream.</returns>
    internal static (RemotePlayer Player, MemoryStream Output) CreatePlayer(IGameServerContext gameContext, ClientVersion clientVersion = default, bool productionOutput = false)
    {
        var output = new MemoryStream();
        PipeWriter writer = PipeWriter.Create(output, new StreamPipeWriterOptions(leaveOpen: true));
        if (productionOutput)
        {
            writer = new ExtendedPipeWriter(writer, OutputBytesCounter);
        }

        var connection = new Mock<IConnection>();
        connection.SetupGet(c => c.Connected).Returns(true);
        connection.SetupGet(c => c.Output).Returns(writer);
        connection.SetupGet(c => c.OutputLock).Returns(new AsyncLock());
        return (new RemotePlayer(gameContext, connection.Object, clientVersion), output);
    }
}

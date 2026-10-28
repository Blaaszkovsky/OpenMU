// <copyright file="SocketConnectionTest.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Network.Tests;

using System.Buffers;
using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using MUnique.OpenMU.Network.Packets.ClientToServer;
using MUnique.OpenMU.Network.SimpleModulus;
using MUnique.OpenMU.Network.Xor;
using Pipelines.Sockets.Unofficial;

/// <summary>
/// Tests the connection implementation over isolated, ephemeral loopback sockets.
/// </summary>
[TestFixture]
public class SocketConnectionTest
{
    private static readonly TimeSpan OperationTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Tests framing and ordered payload delivery without encryption.
    /// </summary>
    /// <returns>The asynchronous test.</returns>
    [Test]
    public ValueTask TestReceivePipelinedAsync() => VerifyReceivePipelinedAsync(false);

    /// <summary>
    /// Tests ordered C3 payload delivery through the real client and server encryption pipelines.
    /// </summary>
    /// <returns>The asynchronous test.</returns>
    [Test]
    public ValueTask TestReceivePipelinedWithEncryptionAsync() => VerifyReceivePipelinedAsync(true);

    /// <summary>
    /// Tests that malformed outgoing data closes the socket within the original one-second limit.
    /// </summary>
    /// <returns>The asynchronous test.</returns>
    [Test]
    public async ValueTask TestDisconnectOnInvalidHeaderSentAsync()
    {
        await WithConnectionAsync(true, async (client, connection, _) =>
        {
            var packet = new byte[22222];
            packet[0] = 0xDE;
            packet[1] = 0xAD;
            packet[2] = 0xBE;
            packet[3] = 0xAF;
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            await connection.Output.WriteAsync(packet, deadline.Token).ConfigureAwait(false);

            // SocketConnection propagates the failed encryptor's output completion to the peer.
            var received = await client.GetStream().ReadAsync(new byte[1], deadline.Token).ConfigureAwait(false);
            Assert.That(received, Is.Zero, "Malformed outgoing data must not reach the peer.");
            Assert.That(connection.Connected, Is.False);
        }).ConfigureAwait(false);
    }

    /// <summary>
    /// Tests that malformed incoming data disconnects within the original 100-millisecond limit.
    /// </summary>
    /// <returns>The asynchronous test.</returns>
    [Test]
    public async ValueTask TestDisconnectOnInvalidHeaderReceivedAsync()
    {
        await WithConnectionAsync(true, async (client, connection, _) =>
        {
            var disconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            connection.Disconnected += () =>
            {
                disconnected.TrySetResult();
                return ValueTask.CompletedTask;
            };

            using var deadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
            var packet = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF, 0, 0, 0, 0, 0, 0 };
            await client.GetStream().WriteAsync(packet, deadline.Token).ConfigureAwait(false);
            await disconnected.Task.WaitAsync(deadline.Token).ConfigureAwait(false);
            Assert.That(connection.Connected, Is.False);
        }).ConfigureAwait(false);
    }

    private static async ValueTask VerifyReceivePipelinedAsync(bool withEncryption)
    {
        const int maximumPacketCount = 1000;
        var expectedPackets = Enumerable.Range(0, maximumPacketCount).Select(index =>
        {
            var data = new byte[Ping.Length];
            var ping = new Ping(data) { TickCount = (uint)index, AttackSpeed = 42 };
            return data;
        }).ToArray();

        await WithConnectionAsync(withEncryption, async (client, connection, receiving) =>
        {
            var packets = new List<byte[]>();
            var receivedAll = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            connection.PacketReceived += packet =>
            {
                packets.Add(packet.ToArray());
                if (packets.Count == maximumPacketCount)
                {
                    receivedAll.TrySetResult();
                }

                return ValueTask.CompletedTask;
            };

            var socketWriter = PipeWriter.Create(client.GetStream(), new StreamPipeWriterOptions(leaveOpen: true));
            var writer = withEncryption
                ? new PipelinedXor32Encryptor(new PipelinedSimpleModulusEncryptor(socketWriter, PipelinedSimpleModulusEncryptor.DefaultClientKey).Writer).Writer
                : socketWriter;
            try
            {
                using var deadline = new CancellationTokenSource(OperationTimeout);
                foreach (var packet in expectedPackets)
                {
                    writer.Write(packet);
                }

                await writer.FlushAsync(deadline.Token).ConfigureAwait(false);
                await receivedAll.Task.WaitAsync(deadline.Token).ConfigureAwait(false);
                client.Client.Shutdown(SocketShutdown.Send);
                await receiving.WaitAsync(deadline.Token).ConfigureAwait(false);

                Assert.That(packets, Has.Count.EqualTo(maximumPacketCount));
                for (var index = 0; index < maximumPacketCount; index++)
                {
                    Assert.That(packets[index], Is.EqualTo(expectedPackets[index]), $"Packet {index} must retain its payload and order.");
                }
            }
            finally
            {
                await writer.CompleteAsync().AsTask().WaitAsync(OperationTimeout).ConfigureAwait(false);
            }
        }).ConfigureAwait(false);
    }

    private static async ValueTask WithConnectionAsync(bool withEncryption, Func<TcpClient, Connection, Task, ValueTask> test)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        using var client = new TcpClient(AddressFamily.InterNetwork);
        listener.Start();
        try
        {
            using var deadline = new CancellationTokenSource(OperationTimeout);
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            await client.ConnectAsync(IPAddress.Loopback, port, deadline.Token).ConfigureAwait(false);
            using var accepted = await listener.AcceptSocketAsync(deadline.Token).ConfigureAwait(false);
            var socketConnection = SocketConnection.Create(accepted);
            var connection = new Connection(
                socketConnection,
                withEncryption ? new PipelinedDecryptor(socketConnection.Input) : null,
                withEncryption ? new PipelinedEncryptor(socketConnection.Output) : null,
                new NullLogger<Connection>());
            var receiving = connection.BeginReceiveAsync();
            var testCompleted = false;
            try
            {
                await test(client, connection, receiving).ConfigureAwait(false);
                testCompleted = true;
            }
            finally
            {
                try
                {
                    try
                    {
                        // Closing the peer wakes pending reads even when a test assertion or deadline fails.
                        client.Dispose();
                        await receiving.WaitAsync(OperationTimeout).ConfigureAwait(false);
                    }
                    finally
                    {
                        try
                        {
                            await connection.DisconnectAsync().AsTask().WaitAsync(OperationTimeout).ConfigureAwait(false);
                        }
                        finally
                        {
                            connection.Dispose();
                        }
                    }
                }
                catch (Exception cleanupException) when (!testCompleted)
                {
                    TestContext.Error.WriteLine($"Additional socket cleanup failure: {cleanupException}");
                }
            }
        }
        finally
        {
            listener.Stop();
        }
    }
}

using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using NUnit.Framework;
using Poker.Transport;

namespace Poker.Foundation.Tests
{
    public sealed class HoldemTcpConnectionTimeoutTests
    {
        [Test]
        public void TimedOutConnectReportsANetworkFailureRatherThanAnInternalCallbackError()
        {
            using (var endpoint = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp))
            {
                endpoint.Bind(new IPEndPoint(IPAddress.Loopback, 0));
                var connect = HoldemTcpClient.ConnectAsync(IPAddress.Loopback,
                    ((IPEndPoint)endpoint.LocalEndPoint).Port, new HoldemClientIdentity("친구"), 100);
                Assert.That(SpinWait.SpinUntil(() => connect.IsCompleted, TimeSpan.FromSeconds(5)), Is.True);
                // OSes may reject an unlistened port immediately or reach the explicit deadline.
                var error = Assert.Catch(() => connect.GetAwaiter().GetResult());
                Assert.That(error, Is.InstanceOf<IOException>().Or.InstanceOf<SocketException>());
            }
        }
    }
}

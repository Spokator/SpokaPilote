using System.Net;
using System.Net.Sockets;
using System.Text;
using SpokaPilote.Core.Printers;

namespace SpokaPilote.Core.Tests;

/// <summary>SNMP : types numériques, adresse MAC, parcours de colonnes (GETNEXT) contre un agent simulé sur 127.0.0.1.</summary>
public class SnmpWalkTests
{
    private static byte[] Tlv(byte tag, byte[] v)
    {
        byte[] len = v.Length < 0x80 ? new[] { (byte)v.Length } : new byte[] { 0x82, (byte)(v.Length >> 8), (byte)v.Length };
        return new[] { tag }.Concat(len).Concat(v).ToArray();
    }

    private static byte[] Int(int v) => Tlv(0x02, v is >= 0 and < 0x80 ? new[] { (byte)v } : BitConverter.GetBytes(v).Reverse().ToArray());

    private static byte[] Response(int requestId, IEnumerable<(string Oid, byte Type, byte[] Raw)> varbinds, int errorStatus = 0)
    {
        var list = varbinds.SelectMany(vb => Tlv(0x30, Tlv(0x06, SnmpClient.EncodeOid(vb.Oid)).Concat(Tlv(vb.Type, vb.Raw)).ToArray())).ToArray();
        var pdu = Tlv(0xA2, Int(requestId).Concat(Int(errorStatus)).Concat(Int(0)).Concat(Tlv(0x30, list)).ToArray());
        return Tlv(0x30, Int(1).Concat(Tlv(0x04, Encoding.ASCII.GetBytes("public"))).Concat(pdu).ToArray());
    }

    [Fact]
    public void Counters_gauges_and_mac_addresses_are_decoded()
    {
        var message = SnmpClient.ParseMessage(Response(77, new[]
        {
            (SnmpClient.PrtMarkerLifeCount, (byte)0x41, new byte[] { 0x00, 0xC3, 0x50 }),          // Counter32 50 000
            (SnmpClient.PrtMarkerSuppliesLevel + ".1.1", (byte)0x02, new byte[] { 0xFD }),          // -3 : « reste du consommable »
            (SnmpClient.IfPhysAddress + ".1", (byte)0x04, new byte[] { 0x00, 0x1B, 0xA9, 0x12, 0x34, 0x56 }),
            ("1.3.6.1.2.1.1.3.0", (byte)0x43, new byte[] { 0x01, 0x00 }),                         // TimeTicks 256
        }))!.Value;

        Assert.Equal(77, message.RequestId);
        Assert.Equal(0, message.ErrorStatus);
        Assert.Equal(50_000, message.VarBinds[0].Integer);
        Assert.Equal("50000", message.VarBinds[0].Text);
        Assert.Equal(-3, message.VarBinds[1].Integer);
        Assert.Equal("00:1B:A9:12:34:56", message.VarBinds[2].MacAddress);
        Assert.Equal(256, message.VarBinds[3].Integer);
        Assert.False(message.VarBinds[0].IsMissing);
    }

    [Fact]
    public void Get_next_request_uses_the_get_next_pdu()
    {
        var packet = SnmpClient.BuildGetRequest(5, "public", new[] { SnmpClient.PrtMarkerSuppliesLevel }, version: 1, pduType: 0xA1);
        Assert.Contains((byte)0xA1, packet);
        Assert.DoesNotContain((byte)0xA0, packet);
    }

    [Fact]
    public async Task Walk_reads_whole_columns_and_stops_at_the_end_of_each()
    {
        // Agent simulé : deux colonnes de prtMarkerSuppliesTable (3 consommables), puis d'autres objets.
        var table = new List<(string Oid, byte Type, byte[] Raw)>
        {
            (SnmpClient.PrtMarkerSuppliesDescription + ".1.1", 0x04, Encoding.ASCII.GetBytes("Black Toner")),
            (SnmpClient.PrtMarkerSuppliesDescription + ".1.2", 0x04, Encoding.ASCII.GetBytes("Cyan Toner")),
            (SnmpClient.PrtMarkerSuppliesDescription + ".1.3", 0x04, Encoding.ASCII.GetBytes("Waste Toner Box")),
            (SnmpClient.PrtMarkerSuppliesLevel + ".1.1", 0x02, new byte[] { 40 }),
            (SnmpClient.PrtMarkerSuppliesLevel + ".1.2", 0x02, new byte[] { 0x00, 0x96 }),
            (SnmpClient.PrtMarkerSuppliesLevel + ".1.3", 0x02, new byte[] { 0xFD }),
            ("1.3.6.1.2.1.43.12.1.1.4.1.1", 0x04, Encoding.ASCII.GetBytes("black")),
        };
        using var agent = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var endpoint = (IPEndPoint)agent.Client.LocalEndPoint!;
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var requests = 0;
        var server = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                UdpReceiveResult r;
                try { r = await agent.ReceiveAsync(stop.Token); } catch (OperationCanceledException) { return; }
                requests++;
                var request = SnmpClient.ParseMessage(r.Buffer)!.Value;
                var answer = request.VarBinds.Select(vb =>
                    table.Where(t => CompareOid(t.Oid, vb.Oid) > 0).OrderBy(t => t.Oid, Comparer<string>.Create(CompareOid))
                        .Select(t => (t.Oid, t.Type, t.Raw)).DefaultIfEmpty(("1.3.6.1.9", (byte)0x82, Array.Empty<byte>())).First());
                var bytes = Response(request.RequestId, answer);
                await agent.SendAsync(bytes, r.RemoteEndPoint, stop.Token);
            }
        });

        var rows = await SnmpClient.WalkAsync(endpoint, new[] { SnmpClient.PrtMarkerSuppliesDescription, SnmpClient.PrtMarkerSuppliesLevel },
            "public", maxRows: 32, timeoutMs: 2000, CancellationToken.None);
        stop.Cancel();
        await server;

        Assert.Equal(6, rows.Count);
        Assert.Equal(new[] { "Black Toner", "Cyan Toner", "Waste Toner Box" },
            rows.Where(r => r.Oid.StartsWith(SnmpClient.PrtMarkerSuppliesDescription + ".")).Select(r => r.Text));
        Assert.Equal(new long?[] { 40, 150, -3 },
            rows.Where(r => r.Oid.StartsWith(SnmpClient.PrtMarkerSuppliesLevel + ".")).Select(r => r.Integer));
        Assert.Equal(4, requests); // 3 lignes + 1 requête qui constate la fin des deux colonnes
    }

    [Fact]
    public async Task Walk_of_a_silent_host_returns_nothing()
    {
        using var silent = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)); // ne répond jamais
        var rows = await SnmpClient.WalkAsync((IPEndPoint)silent.Client.LocalEndPoint!, new[] { SnmpClient.PrtMarkerSuppliesLevel },
            "public", maxRows: 8, timeoutMs: 150, CancellationToken.None);
        Assert.Empty(rows);
    }

    private static int CompareOid(string a, string b)
    {
        var x = a.Split('.').Select(uint.Parse).ToArray();
        var y = b.Split('.').Select(uint.Parse).ToArray();
        for (var i = 0; i < Math.Min(x.Length, y.Length); i++)
            if (x[i] != y[i]) return x[i].CompareTo(y[i]);
        return x.Length.CompareTo(y.Length);
    }
}

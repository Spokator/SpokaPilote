using System.Net;
using System.Net.Sockets;
using System.Text;

namespace SpokaPilote.Core.Printers;

/// <summary>Client SNMP v1/v2c minimal (requête GET), suffisant pour identifier une imprimante réseau.</summary>
public static class SnmpClient
{
    public const string SysDescr = "1.3.6.1.2.1.1.1.0";
    public const string SysName = "1.3.6.1.2.1.1.5.0";
    public const string SysLocation = "1.3.6.1.2.1.1.6.0";
    public const string HrDeviceDescr = "1.3.6.1.2.1.25.3.2.1.3.1";
    public const string PrtSerialNumber = "1.3.6.1.2.1.43.5.1.1.17.1";
    /// <summary>ID IEEE 1284 (PWG Port Monitor MIB) : « MFG:HP;MDL:HP LaserJet M507;CMD:PCL,PJL,POSTSCRIPT; ».</summary>
    public const string Ieee1284DeviceId = "1.3.6.1.4.1.2699.1.2.1.2.1.1.3.1";

    public static async Task<Dictionary<string, string>> GetAsync(IPAddress host, IEnumerable<string> oids, string community = "public",
        int timeoutMs = 1200, CancellationToken ct = default)
    {
        var oidList = oids.ToList();
        var result = new Dictionary<string, string>();
        using var udp = new UdpClient(host.AddressFamily);
        var requestId = Random.Shared.Next(1, int.MaxValue);
        var packet = BuildGetRequest(requestId, community, oidList, version: 1);
        var endpoint = new IPEndPoint(host, 161);

        for (var attempt = 0; attempt < 2; attempt++)
        {
            await udp.SendAsync(packet, endpoint, ct).ConfigureAwait(false);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeoutMs);
            try
            {
                var response = await udp.ReceiveAsync(cts.Token).ConfigureAwait(false);
                foreach (var (oid, value) in ParseResponse(response.Buffer))
                    if (value is not null) result[oid] = value;
                return result;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // nouvel essai
            }
        }
        return result;
    }

    // ------------------------------------------------------------------ encodage BER

    internal static byte[] BuildGetRequest(int requestId, string community, IReadOnlyList<string> oids, int version)
    {
        var varbinds = new List<byte>();
        foreach (var oid in oids)
            varbinds.AddRange(Tlv(0x30, Concat(Tlv(0x06, EncodeOid(oid)), new byte[] { 0x05, 0x00 })));

        var pdu = Tlv(0xA0, Concat(
            Tlv(0x02, EncodeInt(requestId)),
            Tlv(0x02, EncodeInt(0)),
            Tlv(0x02, EncodeInt(0)),
            Tlv(0x30, varbinds.ToArray())));

        return Tlv(0x30, Concat(
            Tlv(0x02, EncodeInt(version)),
            Tlv(0x04, Encoding.ASCII.GetBytes(community)),
            pdu));
    }

    private static byte[] Concat(params byte[][] parts) => parts.SelectMany(p => p).ToArray();

    private static byte[] Tlv(byte tag, byte[] value)
    {
        var len = value.Length;
        byte[] lenBytes = len < 0x80 ? new[] { (byte)len }
            : len <= 0xFF ? new byte[] { 0x81, (byte)len }
            : new byte[] { 0x82, (byte)(len >> 8), (byte)len };
        return Concat(new[] { tag }, lenBytes, value);
    }

    private static byte[] EncodeInt(int value)
    {
        var bytes = BitConverter.GetBytes(value).Reverse().SkipWhile((b, i) => i < 3 && b == 0).ToList();
        if ((bytes[0] & 0x80) != 0) bytes.Insert(0, 0);
        return bytes.ToArray();
    }

    internal static byte[] EncodeOid(string oid)
    {
        var parts = oid.Split('.').Select(uint.Parse).ToArray();
        var bytes = new List<byte> { (byte)(parts[0] * 40 + parts[1]) };
        foreach (var part in parts.Skip(2))
        {
            var stack = new Stack<byte>();
            var v = part;
            stack.Push((byte)(v & 0x7F));
            while ((v >>= 7) > 0) stack.Push((byte)((v & 0x7F) | 0x80));
            bytes.AddRange(stack);
        }
        return bytes.ToArray();
    }

    internal static string DecodeOid(ReadOnlySpan<byte> data)
    {
        if (data.Length == 0) return "";
        var sb = new StringBuilder();
        sb.Append(data[0] / 40).Append('.').Append(data[0] % 40);
        ulong value = 0;
        for (var i = 1; i < data.Length; i++)
        {
            value = (value << 7) | (uint)(data[i] & 0x7F);
            if ((data[i] & 0x80) == 0)
            {
                sb.Append('.').Append(value);
                value = 0;
            }
        }
        return sb.ToString();
    }

    private static (byte Tag, int Length, int HeaderLength) ReadHeader(ReadOnlySpan<byte> data)
    {
        var tag = data[0];
        int len = data[1];
        var header = 2;
        if ((len & 0x80) != 0)
        {
            var n = len & 0x7F;
            len = 0;
            for (var i = 0; i < n; i++) len = (len << 8) | data[2 + i];
            header += n;
        }
        return (tag, len, header);
    }

    internal static List<(string Oid, string? Value)> ParseResponse(byte[] buffer)
    {
        var results = new List<(string, string?)>();
        try
        {
            ReadOnlySpan<byte> span = buffer;
            var (_, _, h) = ReadHeader(span);              // SEQUENCE message
            span = span[h..];
            span = Skip(span);                             // version
            span = Skip(span);                             // community
            var (_, _, ph) = ReadHeader(span);             // PDU (0xA2)
            span = span[ph..];
            span = Skip(span);                             // request-id
            span = Skip(span);                             // error-status
            span = Skip(span);                             // error-index
            var (_, vbLen, vbh) = ReadHeader(span);        // varbind list
            var list = span.Slice(vbh, vbLen);
            while (list.Length > 0)
            {
                var (_, itemLen, ih) = ReadHeader(list);
                var item = list.Slice(ih, itemLen);
                list = list[(ih + itemLen)..];

                var (oidTag, oidLen, oh) = ReadHeader(item);
                if (oidTag != 0x06) continue;
                var oid = DecodeOid(item.Slice(oh, oidLen));
                var valuePart = item[(oh + oidLen)..];
                var (vt, vl, vh) = ReadHeader(valuePart);
                var raw = valuePart.Slice(vh, vl);
                string? value = vt switch
                {
                    0x04 => DecodeText(raw),
                    0x02 => raw.Length > 0 ? DecodeInteger(raw).ToString() : "0",
                    0x06 => DecodeOid(raw),
                    _ => null, // NULL, noSuchObject (0x80), noSuchInstance (0x81)...
                };
                results.Add((oid, value));
            }
        }
        catch
        {
            // Réponse malformée : on ignore.
        }
        return results;

        static ReadOnlySpan<byte> Skip(ReadOnlySpan<byte> s)
        {
            var (_, l, hh) = ReadHeader(s);
            return s[(hh + l)..];
        }
    }

    private static long DecodeInteger(ReadOnlySpan<byte> raw)
    {
        long v = (raw[0] & 0x80) != 0 ? -1 : 0;
        foreach (var b in raw) v = (v << 8) | b;
        return v;
    }

    private static string DecodeText(ReadOnlySpan<byte> raw)
    {
        var s = Encoding.UTF8.GetString(raw);
        return new string(s.Where(c => !char.IsControl(c) || c == ' ').ToArray()).Trim();
    }
}

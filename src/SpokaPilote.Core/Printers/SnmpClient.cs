using System.Net;
using System.Net.Sockets;
using System.Text;

namespace SpokaPilote.Core.Printers;

/// <summary>
/// Valeur SNMP brute : type BER et octets, avec leurs conversions usuelles (texte, entier, adresse MAC).
/// </summary>
public sealed record SnmpVarBind(string Oid, byte Type, byte[] Raw)
{
    /// <summary>NULL, noSuchObject, noSuchInstance, endOfMibView : aucune valeur.</summary>
    public bool IsMissing => Type is 0x05 or 0x80 or 0x81 or 0x82;

    /// <summary>
    /// Texte : chaîne (caractères de contrôle retirés), entier, OID, compteur/jauge/durée (non signés), adresse IP ;
    /// null pour une valeur absente ou d'un type non pris en charge.
    /// </summary>
    public string? Text => Type switch
    {
        0x04 => SnmpClient.DecodeText(Raw),
        0x02 => Raw.Length > 0 ? SnmpClient.DecodeInteger(Raw).ToString(System.Globalization.CultureInfo.InvariantCulture) : "0",
        0x06 => SnmpClient.DecodeOid(Raw),
        0x41 or 0x42 or 0x43 or 0x46 => SnmpClient.DecodeUnsigned(Raw).ToString(System.Globalization.CultureInfo.InvariantCulture),
        0x40 when Raw.Length == 4 => new IPAddress(Raw).ToString(),
        _ => null,
    };

    /// <summary>Valeur numérique (INTEGER, Counter32, Gauge32, TimeTicks, Counter64), sinon null.</summary>
    public long? Integer => Type switch
    {
        0x02 => Raw.Length > 0 ? SnmpClient.DecodeInteger(Raw) : 0,
        0x41 or 0x42 or 0x43 or 0x46 => (long)Math.Min(SnmpClient.DecodeUnsigned(Raw), long.MaxValue),
        _ => null,
    };

    /// <summary>Adresse physique (ifPhysAddress : 6 octets) au format « 00:1B:A9:12:34:56 », sinon null.</summary>
    public string? MacAddress => Type == 0x04 && Raw.Length == 6 && Raw.Any(b => b != 0)
        ? string.Join(":", Raw.Select(b => b.ToString("X2", System.Globalization.CultureInfo.InvariantCulture)))
        : null;
}

/// <summary>
/// Client SNMP v2c minimal (GET et GETNEXT, lecture seule), suffisant pour identifier une imprimante réseau et lire
/// ses compteurs et consommables (Printer-MIB, RFC 3805).
/// </summary>
public static class SnmpClient
{
    public const string SysDescr = "1.3.6.1.2.1.1.1.0";
    public const string SysName = "1.3.6.1.2.1.1.5.0";
    public const string SysLocation = "1.3.6.1.2.1.1.6.0";
    public const string HrDeviceDescr = "1.3.6.1.2.1.25.3.2.1.3.1";
    public const string PrtSerialNumber = "1.3.6.1.2.1.43.5.1.1.17.1";
    /// <summary>ID IEEE 1284 (PWG Port Monitor MIB) : « MFG:HP;MDL:HP LaserJet M507;CMD:PCL,PJL,POSTSCRIPT; ».</summary>
    public const string Ieee1284DeviceId = "1.3.6.1.4.1.2699.1.2.1.2.1.1.3.1";

    /// <summary>prtMarkerLifeCount (compteur de pages total du moteur d'impression, hrDeviceIndex 1, marqueur 1).</summary>
    public const string PrtMarkerLifeCount = "1.3.6.1.2.1.43.10.2.1.4.1.1";
    /// <summary>hrPrinterStatus : 1 autre, 2 inconnu, 3 prête, 4 impression, 5 préchauffage.</summary>
    public const string HrPrinterStatus = "1.3.6.1.2.1.25.3.5.1.1.1";
    /// <summary>hrPrinterDetectedErrorState : bits d'alerte (papier, toner, bourrage, capot...).</summary>
    public const string HrPrinterDetectedErrorState = "1.3.6.1.2.1.25.3.5.1.2.1";
    /// <summary>Colonnes de prtMarkerSuppliesTable (consommables) : description, unité, capacité, niveau, type, classe.</summary>
    public const string PrtMarkerSuppliesDescription = "1.3.6.1.2.1.43.11.1.1.6";
    public const string PrtMarkerSuppliesSupplyUnit = "1.3.6.1.2.1.43.11.1.1.7";
    public const string PrtMarkerSuppliesMaxCapacity = "1.3.6.1.2.1.43.11.1.1.8";
    public const string PrtMarkerSuppliesLevel = "1.3.6.1.2.1.43.11.1.1.9";
    public const string PrtMarkerSuppliesType = "1.3.6.1.2.1.43.11.1.1.5";
    public const string PrtMarkerSuppliesClass = "1.3.6.1.2.1.43.11.1.1.4";
    /// <summary>ifPhysAddress (adresses MAC des interfaces).</summary>
    public const string IfPhysAddress = "1.3.6.1.2.1.2.2.1.6";

    private const byte GetRequest = 0xA0;
    private const byte GetNextRequest = 0xA1;

    public static async Task<Dictionary<string, string>> GetAsync(IPAddress host, IEnumerable<string> oids, string community = "public",
        int timeoutMs = 1200, CancellationToken ct = default)
    {
        var result = new Dictionary<string, string>();
        foreach (var vb in await GetRawAsync(host, oids, community, timeoutMs, ct).ConfigureAwait(false))
            if (vb.Text is { } value) result[vb.Oid] = value;
        return result;
    }

    /// <summary>GET : valeurs brutes, dans l'ordre de la réponse (liste vide si l'hôte ne répond pas).</summary>
    public static async Task<List<SnmpVarBind>> GetRawAsync(IPAddress host, IEnumerable<string> oids, string community = "public",
        int timeoutMs = 1200, CancellationToken ct = default)
    {
        using var udp = new UdpClient(host.AddressFamily);
        return await RequestAsync(udp, new IPEndPoint(host, 161), GetRequest, community, oids.ToList(), timeoutMs, ct).ConfigureAwait(false)
               ?? new List<SnmpVarBind>();
    }

    /// <summary>
    /// Parcourt une ou plusieurs colonnes de table (GETNEXT, en parallèle sur les colonnes) : toutes les lignes sous
    /// chaque racine, dans l'ordre. S'arrête en fin de colonne, à <paramref name="maxRows"/> lignes ou si l'hôte ne
    /// répond plus. Lecture seule.
    /// </summary>
    public static Task<List<SnmpVarBind>> WalkAsync(IPAddress host, IReadOnlyList<string> columns, string community = "public",
        int maxRows = 64, int timeoutMs = 1200, CancellationToken ct = default) =>
        WalkAsync(new IPEndPoint(host, 161), columns, community, maxRows, timeoutMs, ct);

    internal static async Task<List<SnmpVarBind>> WalkAsync(IPEndPoint endpoint, IReadOnlyList<string> columns, string community,
        int maxRows, int timeoutMs, CancellationToken ct)
    {
        var results = new List<SnmpVarBind>();
        var roots = columns.Select(c => c.Trim('.')).ToList();
        var cursors = new List<string>(roots);
        var active = Enumerable.Range(0, roots.Count).ToList();
        using var udp = new UdpClient(endpoint.AddressFamily);

        for (var row = 0; row < maxRows && active.Count > 0; row++)
        {
            var response = await RequestAsync(udp, endpoint, GetNextRequest, community, active.Select(i => cursors[i]).ToList(), timeoutMs, ct).ConfigureAwait(false);
            if (response is null || response.Count != active.Count) break;
            var stillActive = new List<int>();
            for (var k = 0; k < active.Count; k++)
            {
                var column = active[k];
                var vb = response[k];
                // Fin de la colonne : OID hors de la racine, valeur absente, ou agent qui n'avance plus (boucle).
                if (vb.IsMissing || !vb.Oid.StartsWith(roots[column] + ".", StringComparison.Ordinal) || vb.Oid == cursors[column]) continue;
                results.Add(vb);
                cursors[column] = vb.Oid;
                stillActive.Add(column);
            }
            active = stillActive;
        }
        return results;
    }

    /// <summary>Envoie une requête (2 essais) et renvoie les valeurs de la réponse correspondante, ou null.</summary>
    private static async Task<List<SnmpVarBind>?> RequestAsync(UdpClient udp, IPEndPoint endpoint, byte pduType, string community,
        IReadOnlyList<string> oids, int timeoutMs, CancellationToken ct)
    {
        var requestId = Random.Shared.Next(1, int.MaxValue);
        var packet = BuildGetRequest(requestId, community, oids, version: 1, pduType);
        for (var attempt = 0; attempt < 2; attempt++)
        {
            await udp.SendAsync(packet, endpoint, ct).ConfigureAwait(false);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeoutMs);
            try
            {
                while (true)
                {
                    var response = await udp.ReceiveAsync(cts.Token).ConfigureAwait(false);
                    var message = ParseMessage(response.Buffer);
                    // Réponse tardive à une requête précédente : ignorée.
                    if (message is null || message.Value.RequestId != requestId) continue;
                    // v2c : erreur globale (tooBig, genErr...) → pas de valeurs exploitables.
                    return message.Value.ErrorStatus == 0 ? message.Value.VarBinds : null;
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // nouvel essai
            }
            catch (SocketException)
            {
                return null; // port fermé (ICMP inaccessible) : inutile d'insister
            }
        }
        return null;
    }

    // ------------------------------------------------------------------ encodage BER

    internal static byte[] BuildGetRequest(int requestId, string community, IReadOnlyList<string> oids, int version, byte pduType = GetRequest)
    {
        var varbinds = new List<byte>();
        foreach (var oid in oids)
            varbinds.AddRange(Tlv(0x30, Concat(Tlv(0x06, EncodeOid(oid)), new byte[] { 0x05, 0x00 })));

        var pdu = Tlv(pduType, Concat(
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

    /// <summary>Valeurs d'une réponse : (OID, texte), le texte étant null pour une valeur absente (voir <see cref="SnmpVarBind.Text"/>).</summary>
    internal static List<(string Oid, string? Value)> ParseResponse(byte[] buffer) =>
        ParseMessage(buffer)?.VarBinds.Select(v => (v.Oid, v.Text)).ToList() ?? new List<(string, string?)>();

    /// <summary>Décode un message SNMP (réponse) : identifiant de requête, statut d'erreur et valeurs ; null si malformé.</summary>
    internal static (int RequestId, int ErrorStatus, List<SnmpVarBind> VarBinds)? ParseMessage(byte[] buffer)
    {
        var results = new List<SnmpVarBind>();
        int requestId, errorStatus;
        try
        {
            ReadOnlySpan<byte> span = buffer;
            var (_, _, h) = ReadHeader(span);              // SEQUENCE message
            span = span[h..];
            span = Skip(span);                             // version
            span = Skip(span);                             // community
            var (_, _, ph) = ReadHeader(span);             // PDU (0xA2)
            span = span[ph..];
            requestId = (int)ReadInteger(ref span);        // request-id
            errorStatus = (int)ReadInteger(ref span);      // error-status
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
                results.Add(new SnmpVarBind(oid, vt, valuePart.Slice(vh, vl).ToArray()));
            }
        }
        catch
        {
            // Réponse malformée : on ignore (mais les valeurs déjà lues restent valables).
            return results.Count == 0 ? null : (0, 0, results);
        }
        return (requestId, errorStatus, results);

        static ReadOnlySpan<byte> Skip(ReadOnlySpan<byte> s)
        {
            var (_, l, hh) = ReadHeader(s);
            return s[(hh + l)..];
        }

        static long ReadInteger(ref ReadOnlySpan<byte> s)
        {
            var (_, l, hh) = ReadHeader(s);
            var value = l == 0 ? 0 : DecodeInteger(s.Slice(hh, l));
            s = s[(hh + l)..];
            return value;
        }
    }

    internal static long DecodeInteger(ReadOnlySpan<byte> raw)
    {
        long v = (raw[0] & 0x80) != 0 ? -1 : 0;
        foreach (var b in raw) v = (v << 8) | b;
        return v;
    }

    /// <summary>Counter32, Gauge32, TimeTicks, Counter64 : entiers non signés (un octet 0x00 de tête est fréquent).</summary>
    internal static ulong DecodeUnsigned(ReadOnlySpan<byte> raw)
    {
        ulong v = 0;
        foreach (var b in raw.Length > 8 ? raw[^8..] : raw) v = (v << 8) | b;
        return v;
    }

    internal static string DecodeText(ReadOnlySpan<byte> raw)
    {
        var s = Encoding.UTF8.GetString(raw);
        return new string(s.Where(c => !char.IsControl(c) || c == ' ').ToArray()).Trim();
    }
}

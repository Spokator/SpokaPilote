using System.Buffers.Binary;
using System.Net.Http.Headers;
using System.Text;
using SpokaPilote.Core.Infrastructure;

namespace SpokaPilote.Core.Printers;

/// <summary>Client IPP minimal : opération Get-Printer-Attributes (RFC 8011) pour lire marque, modèle et ID 1284.</summary>
public static class IppClient
{
    private static readonly string[] Paths = { "/ipp/print", "/ipp", "/" };
    private static readonly string[] Requested =
    {
        "printer-make-and-model", "printer-device-id", "printer-info", "printer-location", "printer-name",
        "printer-uri-supported", "printer-more-info",
    };

    public sealed record IppAttributes(Uri PrinterUri, Dictionary<string, string> Values);

    public static async Task<IppAttributes?> GetPrinterAttributesAsync(string host, int port = 631, CancellationToken ct = default)
    {
        foreach (var path in Paths)
        {
            var uri = new Uri($"http://{host}:{port}{path}");
            try
            {
                var body = BuildRequest($"ipp://{host}:{port}{path}");
                using var content = new ByteArrayContent(body);
                content.Headers.ContentType = new MediaTypeHeaderValue("application/ipp");
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(4));
                using var response = await Http.Client.PostAsync(uri, content, cts.Token).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode) continue;
                var bytes = await response.Content.ReadAsByteArrayAsync(cts.Token).ConfigureAwait(false);
                var values = ParseResponse(bytes);
                if (values is not null && values.Count > 0) return new IppAttributes(uri, values);
            }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException)
            {
                if (ct.IsCancellationRequested) throw;
            }
        }
        return null;
    }

    internal static byte[] BuildRequest(string printerUri)
    {
        using var ms = new MemoryStream();
        void U8(byte b) => ms.WriteByte(b);
        void U16(int v) { Span<byte> b = stackalloc byte[2]; BinaryPrimitives.WriteUInt16BigEndian(b, (ushort)v); ms.Write(b); }
        void U32(int v) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteInt32BigEndian(b, v); ms.Write(b); }
        void Attr(byte tag, string name, string value)
        {
            U8(tag);
            var n = Encoding.UTF8.GetBytes(name);
            U16(n.Length); ms.Write(n);
            var v = Encoding.UTF8.GetBytes(value);
            U16(v.Length); ms.Write(v);
        }

        U8(0x02); U8(0x00);          // IPP 2.0
        U16(0x000B);                 // Get-Printer-Attributes
        U32(1);                      // request-id
        U8(0x01);                    // operation-attributes-tag
        Attr(0x47, "attributes-charset", "utf-8");
        Attr(0x48, "attributes-natural-language", "en");
        Attr(0x45, "printer-uri", printerUri);
        Attr(0x44, "requested-attributes", Requested[0]);
        foreach (var r in Requested.Skip(1)) Attr(0x44, "", r);
        U8(0x03);                    // end-of-attributes-tag
        return ms.ToArray();
    }

    internal static Dictionary<string, string>? ParseResponse(byte[] data)
    {
        if (data.Length < 8) return null;
        var status = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(2));
        if (status > 0x00FF) return null;

        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var pos = 8;
        string? lastName = null;
        while (pos < data.Length)
        {
            var tag = data[pos++];
            if (tag == 0x03) break;          // end-of-attributes
            if (tag < 0x10) continue;        // délimiteur de groupe
            if (pos + 2 > data.Length) break;
            var nameLen = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(pos)); pos += 2;
            if (pos + nameLen + 2 > data.Length) break;
            var name = Encoding.UTF8.GetString(data, pos, nameLen); pos += nameLen;
            var valueLen = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(pos)); pos += 2;
            if (pos + valueLen > data.Length) break;
            var raw = data.AsSpan(pos, valueLen); pos += valueLen;

            if (nameLen == 0) name = lastName ?? "";   // valeur supplémentaire d'un attribut multi-valué
            else lastName = name;
            if (values.ContainsKey(name)) continue;

            string? value = tag switch
            {
                0x41 or 0x42 or 0x44 or 0x45 or 0x46 or 0x47 or 0x48 or 0x49 => Encoding.UTF8.GetString(raw),
                0x35 or 0x36 => DecodeWithLanguage(raw),
                _ => null,
            };
            if (value is not null) values[name] = value;
        }
        return values;
    }

    private static string DecodeWithLanguage(ReadOnlySpan<byte> raw)
    {
        if (raw.Length < 4) return "";
        var langLen = BinaryPrimitives.ReadUInt16BigEndian(raw);
        var textLen = BinaryPrimitives.ReadUInt16BigEndian(raw[(2 + langLen)..]);
        return Encoding.UTF8.GetString(raw.Slice(4 + langLen, textLen));
    }
}

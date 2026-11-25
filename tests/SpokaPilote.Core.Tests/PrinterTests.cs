using System.Buffers.Binary;
using System.Net;
using System.Text;
using SpokaPilote.Core.Printers;

namespace SpokaPilote.Core.Tests;

public class PrinterTests
{
    [Theory]
    [InlineData("1.3.6.1.2.1.1.1.0")]
    [InlineData("1.3.6.1.4.1.2699.1.2.1.2.1.1.3.1")]
    [InlineData("1.3.6.1.2.1.25.3.2.1.3.1")]
    public void Snmp_oid_roundtrip(string oid)
        => Assert.Equal(oid, SnmpClient.DecodeOid(SnmpClient.EncodeOid(oid)));

    [Fact]
    public void Snmp_parses_get_response()
    {
        // Construit une réponse GetResponse avec 2 varbinds : sysDescr (OCTET STRING) et un noSuchObject.
        static byte[] Tlv(byte tag, byte[] v) => new[] { tag, (byte)v.Length }.Concat(v).ToArray();
        var text = Encoding.ASCII.GetBytes("KYOCERA Document Solutions Printing System");
        var vb1 = Tlv(0x30, Tlv(0x06, SnmpClient.EncodeOid(SnmpClient.SysDescr)).Concat(Tlv(0x04, text)).ToArray());
        var vb2 = Tlv(0x30, Tlv(0x06, SnmpClient.EncodeOid(SnmpClient.Ieee1284DeviceId)).Concat(new byte[] { 0x80, 0x00 }).ToArray());
        var pdu = Tlv(0xA2, Tlv(0x02, new byte[] { 0x01 }).Concat(Tlv(0x02, new byte[] { 0 })).Concat(Tlv(0x02, new byte[] { 0 }))
            .Concat(Tlv(0x30, vb1.Concat(vb2).ToArray())).ToArray());
        var msg = Tlv(0x30, Tlv(0x02, new byte[] { 1 }).Concat(Tlv(0x04, Encoding.ASCII.GetBytes("public"))).Concat(pdu).ToArray());

        var result = SnmpClient.ParseResponse(msg);
        Assert.Equal(2, result.Count);
        Assert.Equal((SnmpClient.SysDescr, "KYOCERA Document Solutions Printing System"), result[0]);
        Assert.Null(result[1].Value);
    }

    [Fact]
    public void Snmp_request_is_well_formed_ber()
    {
        var packet = SnmpClient.BuildGetRequest(42, "public", new[] { SnmpClient.SysDescr }, version: 1);
        Assert.Equal(0x30, packet[0]);
        Assert.Equal(packet.Length - 2, packet[1]);
    }

    [Fact]
    public void Ipp_parses_get_printer_attributes_response()
    {
        using var ms = new MemoryStream();
        void U16(int v) { var b = new byte[2]; BinaryPrimitives.WriteUInt16BigEndian(b, (ushort)v); ms.Write(b); }
        void Attr(byte tag, string name, string value)
        {
            ms.WriteByte(tag);
            var n = Encoding.UTF8.GetBytes(name); U16(n.Length); ms.Write(n);
            var v = Encoding.UTF8.GetBytes(value); U16(v.Length); ms.Write(v);
        }
        ms.Write(new byte[] { 2, 0, 0, 0, 0, 0, 0, 1 }); // version 2.0, status successful-ok, request-id 1
        ms.WriteByte(0x01);
        Attr(0x47, "attributes-charset", "utf-8");
        ms.WriteByte(0x04); // printer-attributes-tag
        Attr(0x41, "printer-make-and-model", "HP LaserJet M507");
        Attr(0x41, "printer-device-id", "MFG:HP;MDL:HP LaserJet M507;CMD:PJL,PCL,POSTSCRIPT;");
        Attr(0x44, "document-format-supported", "application/pdf");
        Attr(0x44, "", "image/pwg-raster"); // valeur supplémentaire
        ms.WriteByte(0x03);

        var values = IppClient.ParseResponse(ms.ToArray());
        Assert.NotNull(values);
        Assert.Equal("HP LaserJet M507", values!["printer-make-and-model"]);
        Assert.Equal("application/pdf", values["document-format-supported"]);
    }

    [Fact]
    public void Ipp_request_contains_printer_uri()
    {
        var req = IppClient.BuildRequest("ipp://10.0.0.5:631/ipp/print");
        Assert.Equal(0x02, req[0]);
        Assert.Equal(0x0B, req[3]);
        Assert.Contains("ipp://10.0.0.5:631/ipp/print", Encoding.UTF8.GetString(req));
    }

    [Fact]
    public void Ieee1284_device_id_fills_make_and_model()
    {
        var p = new DiscoveredPrinter { Address = "10.0.0.5" };
        PrinterDiscovery.ApplyDeviceId(p, "MFG:KYOCERA;MDL:TASKalfa 3253ci;CMD:PCLXL,PJL,KPDL;SN:W2X1234567;");
        Assert.Equal("KYOCERA", p.Manufacturer);
        Assert.Equal("TASKalfa 3253ci", p.Model);
        Assert.Equal("W2X1234567", p.SerialNumber);
        Assert.Equal("KYOCERA TASKalfa 3253ci", p.DisplayName);
    }

    [Fact]
    public void Parses_cidr_ranges_and_single_addresses()
    {
        Assert.Equal(254, PrinterDiscovery.ParseTargets("192.168.1.0/24").Count);
        var range = PrinterDiscovery.ParseTargets("10.0.0.10-10.0.0.20");
        Assert.Equal(11, range.Count);
        Assert.Equal(IPAddress.Parse("10.0.0.10"), range[0]);
        Assert.Single(PrinterDiscovery.ParseTargets("10.1.2.3"));
        Assert.Equal(3, PrinterDiscovery.ParseTargets("10.1.2.3, 10.1.2.4;10.1.2.5").Count);
    }

    [Theory]
    [InlineData("KYOCERA TASKalfa 3253ci", "Kyocera")]
    [InlineData("HP LaserJet M507", "HP")]
    [InlineData("Hewlett-Packard Color LaserJet", "HP")]
    [InlineData("KONICA MINOLTA bizhub C300i", "Konica Minolta")]
    [InlineData("RICOH IM C3000", "Ricoh")]
    [InlineData("Canon iR-ADV C5535", "Canon")]
    [InlineData("SHARP MX-3071", "Sharp")]
    [InlineData("Brother MFC-L8900CDW series", "Brother")]
    [InlineData("Shopping list", null)]
    public void Finds_vendor(string model, string? vendor)
        => Assert.Equal(vendor, PrinterVendorCatalog.FindVendor(model)?.Name);

    [Theory]
    [InlineData("Kyocera TASKalfa 3253ci KX", "KYOCERA TASKalfa 3253ci", 100)]
    [InlineData("HP LaserJet M507 PCL 6", "HP LaserJet M507", 100)]
    [InlineData("Brother MFC-L8900CDW series", "Brother MFC-L8900CDW", 100)]
    [InlineData("KONICA MINOLTA C300i Series PCL", "KONICA MINOLTA bizhub C300i", 70)]
    public void Model_scores_reward_exact_models(string driver, string model, int minimum)
        => Assert.True(PrinterVendorCatalog.ModelScore(driver, model) >= minimum,
            $"score={PrinterVendorCatalog.ModelScore(driver, model)}");

    [Fact]
    public void Model_score_rejects_other_models()
        => Assert.True(PrinterVendorCatalog.ModelScore("Kyocera TASKalfa 6053ci KX", "KYOCERA TASKalfa 3253ci") < 70);
}

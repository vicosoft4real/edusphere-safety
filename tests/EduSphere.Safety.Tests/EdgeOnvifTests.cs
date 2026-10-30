using System.Xml.Linq;
using EduSphere.Safety.Edge;
using Xunit;

public sealed class EdgeOnvifTests
{
    // Captured-style ProbeMatches sample: two entries, the first with
    // multiple space-separated XAddrs plus a scope list.
    private const string RichSample = """
        <?xml version="1.0" encoding="utf-8"?>
        <soap:Envelope xmlns:soap="http://www.w3.org/2003/05/soap-envelope"
                       xmlns:wsa="http://schemas.xmlsoap.org/ws/2004/08/addressing"
                       xmlns:wsdd="http://schemas.xmlsoap.org/ws/2005/04/discovery"
                       xmlns:tdn="http://www.onvif.org/ver10/network/wsdl">
          <soap:Header>
            <wsa:MessageID>uuid:response-1</wsa:MessageID>
            <wsa:RelatesTo>uuid:probe-1</wsa:RelatesTo>
            <wsa:To>http://schemas.xmlsoap.org/ws/2004/08/addressing/role/anonymous</wsa:To>
            <wsa:Action>http://schemas.xmlsoap.org/ws/2005/04/discovery/ProbeMatches</wsa:Action>
          </soap:Header>
          <soap:Body>
            <wsdd:ProbeMatches>
              <wsdd:ProbeMatch>
                <wsa:EndpointReference>
                  <wsa:Address>urn:uuid:84a845f6-7c0b-4b9a-9c1d-111111111111</wsa:Address>
                </wsa:EndpointReference>
                <wsdd:Types>tdn:NetworkVideoTransmitter</wsdd:Types>
                <wsdd:Scopes>onvif://www.onvif.org/type/video_encoder onvif://www.onvif.org/location/school-north</wsdd:Scopes>
                <wsdd:XAddrs>http://192.168.1.10:80/onvif/device_service http://192.168.1.10:8080/alt_service</wsdd:XAddrs>
                <wsdd:MetadataVersion>1</wsdd:MetadataVersion>
              </wsdd:ProbeMatch>
              <wsdd:ProbeMatch>
                <wsa:EndpointReference>
                  <wsa:Address>urn:uuid:84a845f6-7c0b-4b9a-9c1d-222222222222</wsa:Address>
                </wsa:EndpointReference>
                <wsdd:Types>tdn:NetworkVideoTransmitter tdn:Device</wsdd:Types>
                <wsdd:Scopes>onvif://www.onvif.org/type/video_encoder</wsdd:Scopes>
                <wsdd:XAddrs>http://10.0.0.44/onvif/device_service</wsdd:XAddrs>
                <wsdd:MetadataVersion>2</wsdd:MetadataVersion>
              </wsdd:ProbeMatch>
            </wsdd:ProbeMatches>
          </soap:Body>
        </soap:Envelope>
        """;

    // Minimal sample: only the endpoint address; every optional element absent.
    private const string MinimalSample = """
        <?xml version="1.0" encoding="utf-8"?>
        <Envelope xmlns="http://www.w3.org/2003/05/soap-envelope">
          <Body>
            <ProbeMatches xmlns="http://schemas.xmlsoap.org/ws/2005/04/discovery">
              <ProbeMatch>
                <EndpointReference xmlns="http://schemas.xmlsoap.org/ws/2004/08/addressing">
                  <Address>urn:uuid:minimal-device-1</Address>
                </EndpointReference>
              </ProbeMatch>
            </ProbeMatches>
          </Body>
        </Envelope>
        """;

    // ---- Probe builder ----

    [Fact]
    public void BuildProbeEnvelope_ContainsProbeActionAndTransmitterType()
    {
        var xml = OnvifDiscovery.BuildProbeEnvelope();

        Assert.Contains(OnvifDiscovery.ProbeAction, xml, StringComparison.Ordinal);
        Assert.Contains("tdn:NetworkVideoTransmitter", xml, StringComparison.Ordinal);
        Assert.Contains(
            "http://www.onvif.org/ver10/network/wsdl", xml, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildProbeEnvelope_FreshUuidPerCall()
    {
        var first = OnvifDiscovery.BuildProbeEnvelope();
        var second = OnvifDiscovery.BuildProbeEnvelope();

        Assert.NotEqual(first, second);
        Assert.True(TryGetMessageId(first, out var idA), "First envelope has a MessageID.");
        Assert.True(TryGetMessageId(second, out var idB), "Second envelope has a MessageID.");
        Assert.StartsWith("uuid:", idA, StringComparison.Ordinal);
        Assert.True(Guid.TryParse(idA["uuid:".Length..], out _), "MessageID carries a uuid.");
        Assert.NotEqual(idA, idB);

        static bool TryGetMessageId(string xml, out string messageId)
        {
            var doc = XDocument.Parse(xml, LoadOptions.None);
            messageId = doc.Descendants()
                .FirstOrDefault(e => e.Name.LocalName == "MessageID")?.Value.Trim()
                ?? string.Empty;
            return messageId.Length > 0;
        }
    }

    // ---- ProbeMatch parser ----

    [Fact]
    public void ParseProbeMatches_RichSample_TwoDevicesWithMultiXAddrsAndScopes()
    {
        var devices = OnvifDiscovery.ParseProbeMatches(RichSample);

        Assert.Equal(2, devices.Count);

        var first = devices[0];
        Assert.Equal("urn:uuid:84a845f6-7c0b-4b9a-9c1d-111111111111", first.EndpointAddress);
        Assert.Equal(
            ["http://192.168.1.10:80/onvif/device_service", "http://192.168.1.10:8080/alt_service"],
            first.XAddrs);
        Assert.Equal(["tdn:NetworkVideoTransmitter"], first.Types);
        Assert.Equal(
            ["onvif://www.onvif.org/type/video_encoder", "onvif://www.onvif.org/location/school-north"],
            first.Scopes);
        Assert.Equal(1, first.MetadataVersion);

        var second = devices[1];
        Assert.Equal("urn:uuid:84a845f6-7c0b-4b9a-9c1d-222222222222", second.EndpointAddress);
        Assert.Equal(["http://10.0.0.44/onvif/device_service"], second.XAddrs);
        Assert.Equal(2, second.MetadataVersion);
    }

    [Fact]
    public void ParseProbeMatches_MinimalSample_ToleratesMissingOptionals()
    {
        var devices = OnvifDiscovery.ParseProbeMatches(MinimalSample);

        var device = Assert.Single(devices);
        Assert.Equal("urn:uuid:minimal-device-1", device.EndpointAddress);
        Assert.Empty(device.XAddrs);
        Assert.Empty(device.Types);
        Assert.Empty(device.Scopes);
        Assert.Equal(0, device.MetadataVersion);
    }

    [Theory]
    [InlineData("<not xml at all")]
    [InlineData("<ProbeMatch><unclosed>")]
    [InlineData("")]
    [InlineData("   ")]
    public void ParseProbeMatches_MalformedXml_ThrowsInvalidData(string xml)
    {
        // Must be InvalidDataException with an actionable message — never a raw XmlException.
        var ex = Assert.Throws<InvalidDataException>(
            () => OnvifDiscovery.ParseProbeMatches(xml));
        Assert.False(string.IsNullOrWhiteSpace(ex.Message));
    }

    // ---- RTSP validator ----

    [Theory]
    [InlineData("rtsp://192.168.1.20:554/stream")]
    [InlineData("rtsp://10.0.0.5/cam")]
    [InlineData("rtsp://172.16.0.9:8554/x")]
    public void ValidateRtspUrl_PrivateLan_Accepts(string url)
    {
        var verdict = OnvifDiscovery.ValidateRtspUrl(url);

        Assert.True(verdict.Accepted, verdict.Reason);
        Assert.False(string.IsNullOrWhiteSpace(verdict.Reason));
    }

    [Theory]
    [InlineData("http://192.168.1.5/cam.mjpg")] // wrong scheme
    [InlineData("file:///tmp/cam.mp4")] // wrong scheme
    [InlineData("rtsp://169.254.169.254/latest/meta-data")] // cloud metadata endpoint
    [InlineData("rtsp://169.254.10.20/cam")] // link-local range
    [InlineData("rtsp://192.168.1.2/metadata/stream")] // metadata path abuse
    [InlineData("rtsp://127.0.0.1/cam")] // loopback abuse
    [InlineData("rtsp://localhost/cam")] // loopback abuse
    [InlineData("rtsp://[::1]/cam")] // loopback abuse (IPv6)
    [InlineData("rtsp://8.8.8.8/cam")] // public IP
    [InlineData("rtsp://example.com/cam")] // DNS hostname: gated, rejected
    [InlineData("rtsp://user:pass@192.168.1.2/cam")] // embedded credentials exposure
    [InlineData("rtsp://user@192.168.1.2/cam")] // bare userinfo still exposure-shaped
    [InlineData("")] // empty
    [InlineData(null)] // missing
    public void ValidateRtspUrl_PolicyViolations_RejectsWithReason(string? url)
    {
        var verdict = OnvifDiscovery.ValidateRtspUrl(url);

        Assert.False(verdict.Accepted, $"Expected rejection for {url ?? "<null>"}.");
        Assert.False(string.IsNullOrWhiteSpace(verdict.Reason));
    }
}

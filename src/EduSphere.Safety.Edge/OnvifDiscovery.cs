using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace EduSphere.Safety.Edge;

/// <summary>
/// One WS-Discovery <c>ProbeMatch</c>: a single discovered ONVIF device endpoint.
/// All collections are empty (never null) when the corresponding optional
/// element is absent from the response.
/// </summary>
/// <param name="EndpointAddress">
/// The <c>EndpointReference/Address</c> value (usually a <c>urn:uuid:…</c>);
/// empty when absent.
/// </param>
/// <param name="XAddrs">
/// Device service URLs from <c>XAddrs</c>, split on whitespace (a single
/// element may carry several space-separated addresses).
/// </param>
/// <param name="Types">Type QNames from <c>Types</c> (whitespace-separated).</param>
/// <param name="Scopes">Scope URIs from <c>Scopes</c> (whitespace-separated).</param>
/// <param name="MetadataVersion">
/// The <c>MetadataVersion</c> value; 0 when the optional element is absent.
/// </param>
public sealed record OnvifDevice(
    string EndpointAddress,
    IReadOnlyList<string> XAddrs,
    IReadOnlyList<string> Types,
    IReadOnlyList<string> Scopes,
    int MetadataVersion);

/// <summary>
/// Pure accept/reject verdict for a candidate RTSP stream URL.
/// <c>Accepted</c> true means the URL passed the local allowlist;
/// <c>Reason</c> always explains the verdict (accept or reject).
/// </summary>
public sealed record RtspValidationResult(bool Accepted, string Reason);

/// <summary>
/// ONVIF WS-Discovery probe builder + response parser, plus the RTSP URL
/// validator and the CI-gated live socket path. Pure functions
/// (<see cref="BuildProbeEnvelope"/>, <see cref="ParseProbeMatches"/>,
/// <see cref="ValidateRtspUrl"/>) never touch the network; only
/// <see cref="DiscoverAsync"/> opens a socket.
/// </summary>
public static class OnvifDiscovery
{
    /// <summary>Standard WS-Discovery Probe action URI.</summary>
    public const string ProbeAction = "http://schemas.xmlsoap.org/ws/2005/04/discovery/Probe";

    /// <summary>WS-Discovery namespace.</summary>
    public const string WsDiscoveryNamespace = "http://schemas.xmlsoap.org/ws/2005/04/discovery";

    /// <summary>WS-Addressing namespace (EndpointReference/Address, MessageID).</summary>
    public const string AddressingNamespace = "http://schemas.xmlsoap.org/ws/2004/08/addressing";

    /// <summary>ONVIF network WSDL namespace bound to the <c>tdn:</c> prefix.</summary>
    public const string OnvifNetworkNamespace = "http://www.onvif.org/ver10/network/wsdl";

    /// <summary>The device type this probe looks for.</summary>
    public const string VideoTransmitterType = "tdn:NetworkVideoTransmitter";

    /// <summary>WS-Discovery IPv4 multicast group.</summary>
    public const string MulticastAddress = "239.255.255.250";

    /// <summary>WS-Discovery UDP port.</summary>
    public const int MulticastPort = 3702;

    /// <summary>
    /// Builds a WS-Discovery Probe SOAP envelope asking for
    /// <c>tdn:NetworkVideoTransmitter</c> devices. A fresh <c>uuid:</c>
    /// MessageID is minted on every call so responses can be correlated
    /// and duplicate probes are distinguishable on the wire.
    /// </summary>
    /// <returns>The Probe envelope as an XML string.</returns>
    public static string BuildProbeEnvelope()
    {
        var messageId = $"uuid:{Guid.NewGuid():D}";
        var sb = new StringBuilder(512);
        sb.Append("<?xml version=\"1.0\" encoding=\"utf-8\"?>");
        sb.Append("<s:Envelope");
        sb.Append(" xmlns:s=\"http://www.w3.org/2003/05/soap-envelope\"");
        sb.Append($" xmlns:wsa=\"{AddressingNamespace}\"");
        sb.Append($" xmlns:wsdd=\"{WsDiscoveryNamespace}\"");
        sb.Append($" xmlns:tdn=\"{OnvifNetworkNamespace}\">");
        sb.Append("<s:Header>");
        sb.Append($"<wsa:MessageID>{messageId}</wsa:MessageID>");
        sb.Append("<wsa:To>urn:schemas-xmlsoap-org:ws:2005:04:discovery</wsa:To>");
        sb.Append($"<wsa:Action>{ProbeAction}</wsa:Action>");
        sb.Append("</s:Header>");
        sb.Append("<s:Body><wsdd:Probe>");
        sb.Append($"<wsdd:Types>{VideoTransmitterType}</wsdd:Types>");
        sb.Append("</wsdd:Probe></s:Body>");
        sb.Append("</s:Envelope>");
        return sb.ToString();
    }

    /// <summary>
    /// Parses a WS-Discovery ProbeMatches document into device records.
    /// Element lookup is by local name, so responses using different prefixes
    /// (or a default namespace) still parse. Missing optional elements
    /// (<c>XAddrs</c>, <c>Types</c>, <c>Scopes</c>, <c>MetadataVersion</c>)
    /// yield empty lists / version 0; multiple <c>ProbeMatch</c> entries each
    /// yield one record.
    /// </summary>
    /// <param name="xml">The ProbeMatches XML string.</param>
    /// <returns>One <see cref="OnvifDevice"/> per ProbeMatch entry.</returns>
    /// <exception cref="InvalidDataException">
    /// The input is empty or not well-formed XML. Raw <see cref="XmlException"/>
    /// is never leaked to callers; it is wrapped with an actionable message.
    /// </exception>
    public static IReadOnlyList<OnvifDevice> ParseProbeMatches(string? xml)
    {
        if (string.IsNullOrWhiteSpace(xml))
        {
            throw new InvalidDataException(
                "ONVIF ProbeMatches response is empty: expected a WS-Discovery " +
                "ProbeMatches SOAP envelope with at least one ProbeMatch entry.");
        }

        XDocument doc;
        try
        {
            doc = XDocument.Parse(xml, LoadOptions.None);
        }
        catch (XmlException ex)
        {
            throw new InvalidDataException(
                "ONVIF ProbeMatches response is not well-formed XML and was discarded: " +
                ex.Message,
                ex);
        }

        var devices = new List<OnvifDevice>();
        foreach (var match in doc.Descendants().Where(e => e.Name.LocalName == "ProbeMatch"))
        {
            var address = match.Descendants()
                .FirstOrDefault(e => e.Name.LocalName == "Address")?.Value.Trim() ?? string.Empty;
            var xAddrs = SplitList(match.Descendants()
                .FirstOrDefault(e => e.Name.LocalName == "XAddrs")?.Value);
            var types = SplitList(match.Descendants()
                .FirstOrDefault(e => e.Name.LocalName == "Types")?.Value);
            var scopes = SplitList(match.Descendants()
                .FirstOrDefault(e => e.Name.LocalName == "Scopes")?.Value);
            var versionText = match.Descendants()
                .FirstOrDefault(e => e.Name.LocalName == "MetadataVersion")?.Value.Trim();
            var version = int.TryParse(
                versionText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0;
            devices.Add(new OnvifDevice(address, xAddrs, types, scopes, version));
        }

        return devices;
    }

    /// <summary>
    /// Validates a candidate RTSP stream URL against the local allowlist, in the
    /// same spirit as <c>FfmpegFrameSource.ValidateSourceUrl</c> but stricter,
    /// because these URLs arrive from the network (discovery responses), not
    /// from the local operator. Deliberate differences from the FFmpeg file/RTSP
    /// source policy are part of the contract:
    /// <list type="bullet">
    /// <item>Scheme must be exactly <c>rtsp</c> (<c>http</c>/<c>file</c>/everything
    /// else rejected — same as the FFmpeg policy).</item>
    /// <item>Host must be an RFC 1918 private IPv4 literal
    /// (<c>10/8</c>, <c>172.16/12</c>, <c>192.168/16</c>). DNS hostnames are
    /// rejected (resolve-and-check is CI-only), public IPs are rejected, and
    /// link-local <c>169.254/16</c> is rejected — that range carries the cloud
    /// instance-metadata endpoint (<c>169.254.169.254</c>), which a hostile or
    /// misconfigured device must never steer the edge box toward.</item>
    /// <item>Loopback (<c>localhost</c>, <c>127/8</c>, <c>::1</c>) is REJECTED
    /// here even though <c>FfmpegFrameSource</c> accepts it for locally
    /// configured test playback: a REMOTE device directing playback at loopback
    /// is loopback abuse (SSRF-shaped), so discovery-provided URLs may not do
    /// it. Loopback testing stays on the manual-configuration path.</item>
    /// <item>Paths containing <c>metadata</c>/<c>meta-data</c> are rejected as
    /// metadata-endpoint abuse.</item>
    /// <item>Embedded userinfo (<c>rtsp://user:pass@…</c>) is rejected as
    /// unexpected credentials exposure: the operator supplies camera
    /// credentials out-of-band, and discovery-provided URLs must not carry
    /// secrets (which would also leak into argv/logs at spawn time).</item>
    /// </list>
    /// This function never throws on bad input: every rejection is a
    /// return value with an actionable <c>Reason</c>.
    /// </summary>
    public static RtspValidationResult ValidateRtspUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return new RtspValidationResult(false, "RTSP URL must not be empty.");
        }

        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri))
        {
            return new RtspValidationResult(false, "RTSP URL is not a valid absolute URI.");
        }

        if (!uri.Scheme.Equals("rtsp", StringComparison.OrdinalIgnoreCase))
        {
            return new RtspValidationResult(
                false,
                $"URL scheme \"{uri.Scheme}\" is not allowlisted (rtsp only).");
        }

        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            return new RtspValidationResult(
                false,
                "RTSP URLs with embedded credentials (userinfo) are rejected: supply camera " +
                "credentials out-of-band; discovery-provided URLs must not carry secrets.");
        }

        var path = uri.AbsolutePath ?? string.Empty;
        if (path.Contains("meta-data", StringComparison.OrdinalIgnoreCase) ||
            path.Contains("metadata", StringComparison.OrdinalIgnoreCase))
        {
            return new RtspValidationResult(
                false,
                "RTSP URL path looks like a cloud metadata endpoint; rejected.");
        }

        var host = uri.Host;
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
        {
            return new RtspValidationResult(
                false,
                "RTSP loopback hosts are rejected for discovery-provided stream URLs " +
                "(loopback-abuse guard); use manual local configuration for loopback testing.");
        }

        if (!IPAddress.TryParse(host, out var address))
        {
            return new RtspValidationResult(
                false,
                "RTSP hosts must be private IP literals (10/8, 172.16/12, 192.168/16); " +
                "DNS hostnames are rejected (resolve-and-check is CI-only).");
        }

        if (IPAddress.IsLoopback(address))
        {
            return new RtspValidationResult(
                false,
                "RTSP loopback addresses are rejected for discovery-provided stream URLs " +
                "(loopback-abuse guard); use manual local configuration for loopback testing.");
        }

        if (string.Equals(host, "169.254.169.254", StringComparison.Ordinal))
        {
            return new RtspValidationResult(
                false,
                "RTSP host 169.254.169.254 is the cloud instance-metadata endpoint; rejected.");
        }

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var bytes = address.GetAddressBytes();
            if (bytes[0] == 10 ||
                (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) ||
                (bytes[0] == 192 && bytes[1] == 168))
            {
                return new RtspValidationResult(true, "RTSP URL is an allowlisted private-LAN address.");
            }

            if (bytes[0] == 169 && bytes[1] == 254)
            {
                return new RtspValidationResult(
                    false,
                    "RTSP link-local hosts (169.254/16, the cloud metadata range) are rejected.");
            }

            return new RtspValidationResult(
                false,
                "RTSP hosts must be RFC 1918 private literals (10/8, 172.16/12, 192.168/16); " +
                "public IPs are rejected.");
        }

        return new RtspValidationResult(
            false,
            "RTSP hosts must be RFC 1918 private IPv4 literals; non-loopback IPv6 is rejected.");
    }

    // CI-gated (socket bind/send blocked in sandbox): the live multicast
    // send/receive path below compiles against BCL sockets only (no new
    // packages) and is verified on connected CI only. It fails closed — a
    // network fault surfaces as InvalidOperationException and no partial
    // device list is ever returned as if it were complete.

    /// <summary>
    /// CI-gated live discovery: multicasts a Probe to
    /// <c>239.255.255.250:3702</c>, collects ProbeMatches until
    /// <paramref name="probeTimeout"/> elapses, and returns every parsed
    /// device. Unparseable datagrams (non-Probe traffic on the group) are
    /// skipped. Socket bind/send is blocked in the sandbox; this method is
    /// verified on connected CI only.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The socket is unavailable (fail-closed; never a partial list).
    /// </exception>
    public static async Task<IReadOnlyList<OnvifDevice>> DiscoverAsync(
        TimeSpan probeTimeout, CancellationToken ct)
    {
        var payload = Encoding.UTF8.GetBytes(BuildProbeEnvelope());
        var group = new IPEndPoint(IPAddress.Parse(MulticastAddress), MulticastPort);
        using var client = new UdpClient(0);
        try
        {
            client.JoinMulticastGroup(IPAddress.Parse(MulticastAddress));
            await client.SendAsync(payload, group, ct).ConfigureAwait(false);
        }
        catch (SocketException ex)
        {
            throw new InvalidOperationException(
                $"ONVIF discovery socket failed (fail-closed, no devices returned): {ex.Message}",
                ex);
        }

        var devices = new List<OnvifDevice>();
        var deadline = DateTime.UtcNow + probeTimeout;
        while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            var remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero)
            {
                break;
            }

            using var waitCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            waitCts.CancelAfter(remaining);
            UdpReceiveResult result;
            try
            {
                result = await client.ReceiveAsync(waitCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break; // Timeout elapsed or caller cancelled: collection is over.
            }
            catch (SocketException)
            {
                break; // Interface/network went away mid-collection: keep fail-closed.
            }

            string text;
            try
            {
                text = Encoding.UTF8.GetString(result.Buffer);
            }
            catch (ArgumentException)
            {
                continue; // Not decodable: ignore non-Probe traffic on the group.
            }

            try
            {
                devices.AddRange(ParseProbeMatches(text));
            }
            catch (InvalidDataException)
            {
                // Not a ProbeMatches document: ignore non-Probe traffic on the group.
            }
        }

        return devices;
    }

    private static IReadOnlyList<string> SplitList(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return Array.Empty<string>();
        }

        return text.Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
    }
}

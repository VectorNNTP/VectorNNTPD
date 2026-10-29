using System.Net;
using VectorNNTPD.TransitService.Configuration;
using VectorNNTPD.TransitService.Tests.Fixtures;
using VectorNNTPD.Common.Configuration;

namespace VectorNNTPD.TransitService.Tests.TestDoubles;

/// <summary>
/// Deterministic local-address probe for configuration tests.
/// </summary>
internal sealed class FakeLocalIpAddressAssignee : ILocalIpAddressAssignee
{
    private readonly HashSet<IPAddress> _assigned;
    private readonly bool _assignAll;
    private readonly IReadOnlyList<IPAddress> _unicast;

    public FakeLocalIpAddressAssignee(bool assignAll)
    {
        _assignAll = assignAll;
        _assigned = new HashSet<IPAddress>();
        _unicast = assignAll
            ? [TestHostFactory.TestIpv4, TestHostFactory.TestIpv6]
            : Array.Empty<IPAddress>();
    }

    public FakeLocalIpAddressAssignee(params IPAddress[] assigned)
    {
        _assignAll = false;
        _assigned = new HashSet<IPAddress>(assigned);
        _unicast = assigned;
    }

    public bool IsLocallyAssigned(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        return _assignAll || _assigned.Contains(address);
    }

    public IReadOnlyList<IPAddress> GetAssignedUnicastAddresses() => _unicast;
}

namespace RhpV2.TestSupport;

/// <summary>
/// The XRouter image the integration tests run against.
///
/// <para>
/// Pinned to 505c, the last release these tests pass on. Never use the
/// floating <c>latest</c> tag here: it moved to 505o and then 505p, and
/// both break the AX.25-over-AXUDP tests (checked with a one-container
/// loopback probe, bisected over 505d to 505p):
/// </para>
/// <list type="bullet">
/// <item>An inet <c>bind</c> with local <c>127.0.0.1:0</c> (or any
/// <c>ip:0</c>) is answered with errCode 6 "Invalid local address" from
/// 505d onwards; 505c accepts it.</item>
/// <item>505o and 505p emit TRACE <c>recv</c> messages that are not valid
/// JSON (a comma is missing after <c>"port": 1</c>), so no trace frame
/// can be parsed. 505n and earlier are fine.</item>
/// <item>505p fixed the connectReply errCode quirk (it is 0 on success,
/// not the handle), so the test that pins the 505c quirk fails there.</item>
/// </list>
/// <para>
/// Move the pin only after an XRouter release passes this suite again.
/// </para>
/// </summary>
internal static class XRouterImage
{
    public const string Name = "ghcr.io/packethacking/xrouter:505c";
}

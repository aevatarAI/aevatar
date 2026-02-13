// ─── TimestampHelper tests ───

using Aevatar.Helpers;
using Shouldly;

namespace Aevatar.Abstractions.Tests;

public class TimestampHelperTests
{
    [Fact]
    public void Now_ReturnsRecentTimestamp()
    {
        var before = DateTime.UtcNow.AddSeconds(-1);
        var ts = TimestampHelper.Now();
        var after = DateTime.UtcNow.AddSeconds(1);

        var dt = ts.ToDateTime();
        dt.ShouldBeGreaterThan(before);
        dt.ShouldBeLessThan(after);
    }
}
using BlazorSqlite.EntityFrameworkCore;
using Xunit;

namespace BlazorSqlite.Interop.Tests;

/// <summary>
/// The version this stub reports is what EF Core gates its SQL translations on in the browser, so
/// it has to describe the engine that is actually there. The browser suite checks it against the
/// running engine; this checks the two forms of it against each other, which is the half that can
/// be got wrong on desktop.
/// </summary>
public sealed class BrowserSqlitePclProviderTests
{
    [Fact]
    public void TheReportedVersionAndVersionNumberAgree()
    {
        var provider = new BlazorSqlitePclProvider();
        var version = new Version(provider.sqlite3_libversion().utf8_to_string());

        var expected = (version.Major * 1_000_000)
            + (version.Minor * 1_000)
            + Math.Max(version.Build, 0);

        Assert.Equal(expected, provider.sqlite3_libversion_number());
    }

    /// <summary>
    /// Pinned so that bumping the vendored engine without updating the stub is a failing test here
    /// as well as in the browser, where the engine can actually be asked.
    /// </summary>
    [Fact]
    public void TheReportedVersionIsTheVendoredEngines()
    {
        Assert.Equal("3.53.0", BlazorSqlitePclProvider.EngineVersion);
        Assert.Equal(
            BlazorSqlitePclProvider.EngineVersion,
            new BlazorSqlitePclProvider().sqlite3_libversion().utf8_to_string());
    }
}

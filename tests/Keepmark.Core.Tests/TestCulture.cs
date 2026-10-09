using System.Globalization;
using System.Runtime.CompilerServices;

namespace Keepmark.Core.Tests;

// Existing tests assert English texts: pin the culture for every test thread.
internal static class TestCulture
{
    [ModuleInitializer]
    internal static void Init()
    {
        var en = CultureInfo.GetCultureInfo("en-US");
        CultureInfo.DefaultThreadCurrentCulture = en;
        CultureInfo.DefaultThreadCurrentUICulture = en;
        CultureInfo.CurrentCulture = en;
        CultureInfo.CurrentUICulture = en;
    }
}

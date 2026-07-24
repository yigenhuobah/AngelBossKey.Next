using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

[assembly: SuppressMessage(
    "Naming",
    "CA1716:Identifiers should not match keywords",
    Justification = "Next is part of the product namespace, not an API identifier.",
    Scope = "namespace",
    Target = "~N:AngelBossKey.Next.Win32")]
[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
[assembly: InternalsVisibleTo("AngelBossKey.Next.Tests")]

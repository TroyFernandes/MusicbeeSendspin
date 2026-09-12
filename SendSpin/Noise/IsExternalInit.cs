// net48 has no built-in `init` accessors. This polyfill (the standard approach) makes
// C# 9/10 `init` setters and positional records compile in the plugin.
namespace System.Runtime.CompilerServices
{
    internal static class IsExternalInit { }
}

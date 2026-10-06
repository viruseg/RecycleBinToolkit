using System.Runtime.Versioning;

// These tests drive the Windows Shell COM interop directly and only run on Windows; Program.cs
// exits early elsewhere. Declaring it for the whole assembly keeps the platform-compatibility
// analyzer accurate without repeating an attribute at every call site.
[assembly: SupportedOSPlatform("windows5.0")]
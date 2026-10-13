using System.Runtime.Versioning;

// Declared for the whole assembly so the platform-compatibility analyzer stays accurate: the lowest build
// this suite drives is the one that reads a version 2 record, and Program.cs exits early below it.
[assembly: SupportedOSPlatform("windows10.0.14393")]
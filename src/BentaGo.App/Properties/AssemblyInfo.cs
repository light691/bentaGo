using System.Runtime.CompilerServices;

// The AppShell test tier drives the real shell (ShellContent, generated
// x:Name members) and shares the app's internal test seams.
[assembly: InternalsVisibleTo("BentaGo.Tests")]

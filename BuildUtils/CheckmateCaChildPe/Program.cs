using OpenCrazyArcade.BuildUtils;

// CheckmateCaChildPe <exe> [<out>]      fix the image (in place when <out> is left out), then verify it
// CheckmateCaChildPe --check <exe>      only verify; exit code 2 lists what CA.exe would reject
const string Usage = """
    CheckmateCaChildPe: make an exe pass the checks CA.exe runs on its HackShield updater child process.
      CheckmateCaChildPe <exe> [<out>]   flatten the section layout, add the certificate table, verify
      CheckmateCaChildPe --check <exe>   verify only (exit 2 when CA.exe would reject the image)
    """;

try
{
    if (args.Length == 2 && args[0] == "--check")
        return Report(File.ReadAllBytes(args[1]), args[1]);

    if (args.Length is 1 or 2 && !args[0].StartsWith("--"))
    {
        string input = args[0], output = args.Length == 2 ? args[1] : args[0];
        byte[] fixedPe = CaChildPe.Prepare(File.ReadAllBytes(input));
        int rc = Report(fixedPe, output);
        if (rc == 0) File.WriteAllBytes(output, fixedPe);   // never leave a half-fixed file behind
        return rc;
    }

    Console.Error.WriteLine(Usage);
    return 1;
}
catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
{
    Console.Error.WriteLine($"CheckmateCaChildPe: {ex.Message}");
    return 1;
}

static int Report(byte[] pe, string name)
{
    var problems = CaChildPe.Check(pe);
    foreach (var p in problems) Console.Error.WriteLine($"CheckmateCaChildPe: {name}: {p}");
    if (problems.Count == 0) Console.WriteLine($"CheckmateCaChildPe: {name}: ok ({pe.Length} bytes)");
    return problems.Count == 0 ? 0 : 2;
}

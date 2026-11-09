using System.Runtime.CompilerServices;

namespace InMemoryBucket.Tests;

/// <summary>
/// Approved-file snapshots without a library: <c>Snapshots/{name}.verified.txt</c> holds the approved text. On a mismatch
/// the test fails and writes <c>{name}.received.txt</c> next to it; review the diff and copy it over the verified file to
/// accept it. Locally, <c>INMEMORYBUCKET_ACCEPT_SNAPSHOTS=1</c> writes the verified files instead of failing (never on CI).
/// The folder is found from <see cref="CallerFilePathAttribute"/>, so the test project must not map source paths
/// (<c>ContinuousIntegrationBuild</c> or <c>PathMap</c>). A final newline in a verified file is ignored.
/// </summary>
internal static class Snapshot
{
    /// <summary>The folder that holds the approved files.</summary>
    public static string Directory([CallerFilePath] string sourceFile = "") => Path.Combine(Path.GetDirectoryName(sourceFile)!, "Snapshots");

    public static void Match(string name, string actual, [CallerFilePath] string sourceFile = "")
    {
        var directory = Directory(sourceFile);
        var verified = Path.Combine(directory, name + ".verified.txt");
        var received = Path.Combine(directory, name + ".received.txt");
        actual = Normalize(actual);
        var expected = File.Exists(verified) ? Normalize(File.ReadAllText(verified)) : null;

        if (expected == actual)
        {
            File.Delete(received);
            return;
        }

        var accept = Environment.GetEnvironmentVariable("INMEMORYBUCKET_ACCEPT_SNAPSHOTS") == "1"
                     && Environment.GetEnvironmentVariable("CI") is null;
        System.IO.Directory.CreateDirectory(directory);
        if (accept)
        {
            File.WriteAllText(verified, actual);
            File.Delete(received);
            return;
        }

        File.WriteAllText(received, actual);
        Assert.Fail(expected is null
            ? $"No approved snapshot {name}.verified.txt. Review {received} and rename it to .verified.txt, or run with INMEMORYBUCKET_ACCEPT_SNAPSHOTS=1."
            : $"Snapshot {name} changed. Compare {received} with {verified}; if the change is intended, copy it over the verified file.\n\nExpected:\n{expected}\n\nActual:\n{actual}");
    }

    private static string Normalize(string text) => text.Replace("\r\n", "\n").TrimEnd('\n');
}

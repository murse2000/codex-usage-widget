var directory = AppContext.BaseDirectory;
if (args.Length == 0)
{
    File.WriteAllText(Path.Combine(directory, "restarted"), "ok");
    return;
}
File.AppendAllText(Path.Combine(directory, "calls"), string.Join(" ", args) + "\n");
if (args[0] == "api") Console.Write(File.ReadAllText(Path.Combine(directory, "release.json")));
else
{
    var destination = args[Array.IndexOf(args, "--dir") + 1];
    File.Copy(Path.Combine(directory, "payload.zip"), Path.Combine(destination, "CodexUsageWidget-win-x64.zip"));
}

namespace Chetah.CodeGen;

/// <summary>CLI entry point. Argument parsing and validation live in <see cref="CodeGenCli"/>.</summary>
public static class Program
{
    public static Task<int> Main(string[] args) => CodeGenCli.RunAsync(args, Console.Out, Console.Error);
}

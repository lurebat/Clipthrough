using System;
using System.IO;
using System.Runtime.InteropServices;
using Clipthrough.Models;
using Clipthrough.Services;
using Clipthrough.Services.Platform;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Clipthrough.Tests.Unit;

/// <summary>
/// The Start with Windows entry starts Clipthrough the way it was configured.
/// The environment variable and the portable marker are still there at sign-in,
/// so they need nothing; a folder chosen with --data-dir exists only in the
/// command line, so the entry has to carry it, or sign-in opens the default
/// folder. The decided trade-off: turning Start with Windows on from an
/// isolated --data-dir session points the sign-in entry at that folder.
/// </summary>
public sealed class StartWithWindowsCommandTests
{
    private const string Exe = @"C:\Users\me\AppData\Local\Clipthrough\current\Clipthrough.exe";
    private static readonly string s_base = Path.Combine(Path.GetTempPath(), "ct-startup-unused");

    private static string CommandFor(AppDataRoot root)
    {
        using var service = new SystemInteractionService(root);
        return service.BuildStartupCommand(Exe);
    }

    [Fact]
    public void ACommandLineFolder_IsPassedToTheSignInEntry()
    {
        var root = AppDataRoot.Chosen(Path.Combine(s_base, "Clipthrough data"), AppDataRootSource.CommandLine);

        Assert.Equal($"\"{Exe}\" --data-dir \"{root.Directory}\"", CommandFor(root));
    }

    [Fact]
    public void TheOtherSources_KeepTheBareExecutable()
    {
        Assert.Equal($"\"{Exe}\"", CommandFor(AppDataRoot.Default(s_base)));
        Assert.Equal($"\"{Exe}\"", CommandFor(AppDataRoot.Chosen(Path.Combine(s_base, "env"), AppDataRootSource.EnvironmentVariable)));
        Assert.Equal($"\"{Exe}\"", CommandFor(AppDataRoot.Chosen(Path.Combine(s_base, "ClipthroughData"), AppDataRootSource.Portable)));
    }

    // Parsed back by Windows' own command-line splitter, not by our tokenizer,
    // so the quoting is checked against what the started process will read.
    // A drive root ends in a backslash, which before a closing quote would
    // escape it ("D:\" reads back as D:") unless it is doubled.
    [Theory]
    [InlineData("Clipthrough data")]
    [InlineData("plain")]
    [InlineData("trailing space ")]
    public void TheEntry_ParsesBackToTheSameArguments(string folderName)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var root = AppDataRoot.Chosen(Path.Combine(s_base, folderName), AppDataRootSource.CommandLine);

        Assert.Equal([Exe, "--data-dir", root.Directory], SplitLikeWindows(CommandFor(root)));
    }

    [Fact]
    public void ADriveRootFolder_ParsesBackIntact()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var root = AppDataRoot.Chosen(@"D:\", AppDataRootSource.CommandLine);

        Assert.Equal([Exe, "--data-dir", @"D:\"], SplitLikeWindows(CommandFor(root)));
    }

    [Fact]
    public void TheContainer_GivesTheServiceTheRoot()
    {
        var root = AppDataRoot.Chosen(Path.Combine(s_base, "Clipthrough data"), AppDataRootSource.CommandLine);
        using var provider = App.CreateServiceCollection(root).BuildServiceProvider();

        var service = Assert.IsType<SystemInteractionService>(provider.GetRequiredService<ISystemInteractionService>());


        Assert.Contains("--data-dir", service.BuildStartupCommand(Exe), StringComparison.Ordinal);
    }

    private static string[] SplitLikeWindows(string commandLine)
    {
        var argv = CommandLineToArgvW(commandLine, out var count);
        if (argv == IntPtr.Zero)
        {
            throw new InvalidOperationException("CommandLineToArgvW failed.");
        }

        try
        {
            var result = new string[count];
            for (var i = 0; i < count; i++)
            {
                result[i] = Marshal.PtrToStringUni(Marshal.ReadIntPtr(argv, i * IntPtr.Size))!;
            }

            return result;
        }
        finally
        {
            LocalFree(argv);
        }
    }

    [DllImport("shell32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CommandLineToArgvW(string lpCmdLine, out int pNumArgs);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr hMem);
}

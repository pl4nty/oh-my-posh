using System;
using System.Collections;
using System.Globalization;
using System.IO;
using System.Management.Automation;
using System.Management.Automation.Host;
using System.Management.Automation.Runspaces;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.PowerShell.Commands;

namespace OhMyPosh.Native;

// The pwsh side of oh-my-posh's prompt (what its generated init script does in PowerShell),
// in C#: a compiled script is slow to load and JIT-compiles a DLR rule for every member
// access the first time it runs, which is most of a cold prompt.
//
//   using assembly C:\Users\<user>\AppData\Local\oh-my-posh\inproc\OhMyPosh.Native.dll
//   if ([OhMyPosh.Native.Prompt]::Start($ompExe, $config, $ExecutionContext, { <on idle> })) {
//     function global:prompt { $OhMyPoshPrompt }
//   }
//
// Start loads oh-my-posh and renders the first prompt on a background thread, so that work overlaps
// the rest of the profile; the first prompt uses that render if nothing it depends on has changed.
// The prompt is a variable whose value renders it, since reading a variable needs no DLR rule (each
// .NET method call from script compiles one the first time).
//
// Covers the primary and secondary prompts only; configs that use transient prompts,
// tooltips or line errors need oh-my-posh's own init script.
public static class Prompt
{
    /// <summary>
    /// Sets the console to UTF-8 (on the warm-up thread when installing), subscribes <paramref name="onIdle"/> to the first PowerShell.OnIdle event,
    /// then installs the in-process prompt as $OhMyPoshPrompt if oh-my-posh.dll is built for
    /// <paramref name="ompExe"/>'s version. Returns whether it was installed.
    /// </summary>
    public static bool Start(string ompExe, string config, EngineIntrinsics engine, ScriptBlock? onIdle)
    {
        if (onIdle is not null)
        {
            engine.Events.SubscribeEvent(null, null, "PowerShell.OnIdle", null, onIdle, false, false, 1);
        }

        // the native DLL is only used when built for the installed exe: its folder is named after the exe's
        // timestamp (reading its version resource is slow)
        string folder = Path.Combine(
            Path.GetDirectoryName(typeof(Prompt).Assembly.Location)!,
            File.GetLastWriteTimeUtc(ompExe).Ticks.ToString("x", CultureInfo.InvariantCulture));
        string dll = Path.Combine(folder, "oh-my-posh.dll");
        if (!File.Exists(dll))
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;
            return false;
        }

        Install(dll, config, engine);
        engine.SessionState.PSVariable.Set(new PromptVariable(engine));
        return true;
    }

    // $OhMyPoshPrompt: reading it renders the primary prompt
    private sealed class PromptVariable(EngineIntrinsics engine) : PSVariable("OhMyPoshPrompt", null, ScopedItemOptions.None)
    {
        public override object? Value
        {
            get => Primary(
                engine.SessionState.PSVariable.GetValue("?") is true,
                engine.SessionState.PSVariable.GetValue("LASTEXITCODE"),
                engine);
            set { }
        }
    }

    private static string shellVersion = string.Empty;
    private static long lastHistoryId = -1;
    private static int errorCode;
    private static int extraLines = -1;
    private static PropertyInfo? extraPromptLineCount;
    private static object? readLineOptions;
    private static Task<(string[] Args, string Output)>? warmup;
    private static object? history;
    private static MethodInfo? getEntries;
    private static string dllPath = string.Empty;

    public static void Install(string dllPath, string config, EngineIntrinsics engine)
    {
        Prompt.dllPath = dllPath;
        shellVersion = engine.Host.Version.ToString();
        if (engine.SessionState.PSVariable.GetValue("PSVersionTable") is Hashtable table && table["PSVersion"] is object version)
        {
            shellVersion = version.ToString()!;
        }

        Environment.SetEnvironmentVariable("VIRTUAL_ENV_DISABLE_PROMPT", "1");
        Environment.SetEnvironmentVariable("PYENV_VIRTUALENV_DISABLE_PROMPT", "1");
        Environment.SetEnvironmentVariable("POSH_SESSION_ID", Guid.NewGuid().ToString());
        Environment.SetEnvironmentVariable("POWERLINE_COMMAND", "oh-my-posh");
        Environment.SetEnvironmentVariable("POSH_SHELL", "pwsh");
        Environment.SetEnvironmentVariable("POSH_SHELL_VERSION", shellVersion);
        Environment.SetEnvironmentVariable("CONDA_PROMPT_MODIFIER", "False");
        Environment.SetEnvironmentVariable("POSH_THEME", config);

        // what the first prompt will most likely ask for: no history yet, nothing on the location stack
        string[] firstArgs = PrimaryArgs(engine, "primary", noExitCode: true, executionTime: 0);
        warmup = Task.Run(() =>
        {
            // Setting the console code page is slow; nothing is written before the first prompt, which waits for this.
            Console.OutputEncoding = System.Text.Encoding.UTF8;
            Omp.Load(dllPath);
            string first = Omp.Print(firstArgs);
            return (firstArgs, first);
        });

        // Then the secondary prompt, and PSReadLine's options (loaded before profiles in an interactive console,
        // and reached without referencing it; its type initializer may still be running on another thread).
        secondaryPrompt = warmup.ContinueWith(_ =>
        {
            EnsureReadLineOptions();
            string secondary = Omp.Print(["print", "secondary", "--shell=pwsh"]);
            readLineOptions?.GetType().GetProperty("ContinuationPrompt")?.SetValue(readLineOptions, secondary);
        });
    }

    private static Task? secondaryPrompt;

    private static void EnsureReadLineOptions()
    {
        if (readLineOptions is not null)
        {
            return;
        }

        Type? readLine = Type.GetType("Microsoft.PowerShell.PSConsoleReadLine, Microsoft.PowerShell.PSReadLine");
        object? options = readLine?.GetMethod("GetOptions", BindingFlags.Public | BindingFlags.Static)?.Invoke(null, null);
        extraPromptLineCount = options?.GetType().GetProperty("ExtraPromptLineCount");
        readLineOptions = options;
    }

    public static string Primary(bool lastStatus, object? lastExitCode, EngineIntrinsics engine)
    {
        // mirrors Set-PoshPromptType, for the prompt types this class supports
        string type = Runspace.DefaultRunspace?.Debugger?.InBreakpoint == true ? "debug" : "primary";

        // mirrors Update-PoshErrorCode
        HistoryInfo? lastHistory = LastHistory();
        bool noExitCode;
        double executionTime;
        if (lastHistory is null || lastHistory.Id == lastHistoryId)
        {
            noExitCode = true;
            executionTime = 0;
        }
        else
        {
            noExitCode = false;
            lastHistoryId = lastHistory.Id;
            executionTime = (lastHistory.EndExecutionTime - lastHistory.StartExecutionTime).TotalMilliseconds;
            if (lastStatus)
            {
                errorCode = 0;
            }
            else if (LastErrorHistoryId(engine) == lastHistory.Id)
            {
                errorCode = 1;
            }
            else if (lastExitCode is int code && code != 0)
            {
                errorCode = code;
            }
        }

        string[] args = PrimaryArgs(engine, type, noExitCode, executionTime);
        string? output = null;
        if (warmup is not null)
        {
            Task<(string[] Args, string Output)> pending = warmup;
            warmup = null;

            try
            {
                var (warmArgs, warmOutput) = pending.GetAwaiter().GetResult();
                if (args.AsSpan().SequenceEqual(warmArgs))
                {
                    output = warmOutput;
                }
            }
            catch (Exception)
            {
                // render here instead; if loading failed, this throws the real error from the prompt
                Omp.Load(dllPath);
            }
        }

        output ??= Omp.Print(args);

        // make sure PSReadLine knows if we have a multiline prompt
        int lines = 0;
        foreach (char c in output)
        {
            if (c == '\n')
            {
                lines++;
            }
        }

        if (lines != extraLines)
        {
            EnsureReadLineOptions();
            extraPromptLineCount?.SetValue(readLineOptions, lines);
            extraLines = lines;
        }

        return output;
    }

    private static string[] PrimaryArgs(EngineIntrinsics engine, string type, bool noExitCode, double executionTime)
    {
        PSHostRawUserInterface? rawUI = engine.Host.UI.RawUI;
        int width = 0;
        if (rawUI is not null)
        {
            Coordinates cursor = rawUI.CursorPosition;
            Environment.SetEnvironmentVariable("POSH_CURSOR_LINE", (cursor.Y + 1).ToString(CultureInfo.InvariantCulture));
            Environment.SetEnvironmentVariable("POSH_CURSOR_COLUMN", (cursor.X + 1).ToString(CultureInfo.InvariantCulture));
            width = rawUI.WindowSize.Width;
        }

        PathInfo pwd = engine.SessionState.Path.CurrentLocation;
        bool fileSystem = pwd.Provider.Name == "FileSystem";
        int stackCount = engine.SessionState.Path.LocationStack(null).Count;

        return [
            "print", type,
            "--save-cache",
            "--shell=pwsh",
            $"--shell-version={shellVersion}",
            $"--status={errorCode}",
            $"--no-status={noExitCode}",
            $"--execution-time={executionTime.ToString(CultureInfo.InvariantCulture)}",
            $"--pswd={(fileSystem ? string.Empty : pwd.ToString())}",
            $"--stack-count={stackCount}",
            $"--terminal-width={width}",
            "--job-count=0",
            .. fileSystem ? [$"--pwd={pwd.ProviderPath}"] : Array.Empty<string>(),
        ];
    }

    // What Get-History -Count 1 returns, without running a command: running Get-History from the prompt
    // function costs a command lookup and parameter binding.
    private static HistoryInfo? LastHistory()
    {
        try
        {
            if (history is null)
            {
                Runspace? runspace = Runspace.DefaultRunspace;
                history = runspace?.GetType().GetProperty("History", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(runspace);
                getEntries = history?.GetType().GetMethod(
                    "GetEntries",
                    BindingFlags.NonPublic | BindingFlags.Instance,
                    [typeof(long), typeof(long), typeof(SwitchParameter)]);
            }

            return getEntries?.Invoke(history, [0L, 1L, new SwitchParameter(true)]) is HistoryInfo[] { Length: > 0 } entries
                ? entries[0]
                : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    // HistoryId of the most recent ErrorRecord in $Error, or -1
    private static long LastErrorHistoryId(EngineIntrinsics engine)
    {
        if (engine.SessionState.PSVariable.GetValue("Error") is IList errors)
        {
            foreach (object? item in errors)
            {
                if (item is ErrorRecord record)
                {
                    return record.InvocationInfo?.HistoryId ?? -1;
                }
            }
        }

        return -1;
    }
}

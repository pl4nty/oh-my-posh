using System;
using System.Runtime.InteropServices;
using System.Text;

namespace OhMyPosh.Native;

// Calls the in-process oh-my-posh build (oh-my-posh.dll from src/cdll) from PowerShell.
public static unsafe class Omp
{
    private const string ErrPrefix = "OMPERR:";

    private static delegate* unmanaged<byte*, byte*> print;
    private static delegate* unmanaged<byte*, void> free;

    public static void Load(string path)
    {
        IntPtr lib = NativeLibrary.Load(path);
        print = (delegate* unmanaged<byte*, byte*>)NativeLibrary.GetExport(lib, "OmpPrint");
        free = (delegate* unmanaged<byte*, void>)NativeLibrary.GetExport(lib, "OmpFree");
    }

    // Returns what `oh-my-posh <args>` would write to stdout.
    public static string Print(string[] args)
    {
        byte[] input = Encoding.UTF8.GetBytes(string.Join('\x1f', args) + '\0');
        byte* result;
        fixed (byte* p = input)
        {
            result = print(p);
        }

        try
        {
            string output = Marshal.PtrToStringUTF8((IntPtr)result) ?? string.Empty;
            if (output.StartsWith(ErrPrefix, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(output.Substring(ErrPrefix.Length));
            }

            return output;
        }
        finally
        {
            free(result);
        }
    }
}

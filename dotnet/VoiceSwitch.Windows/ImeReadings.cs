using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace VoiceSwitch.Windows;

public static class ImeReadings
{
    // Hiragana reading from MS-IME, or null when MS-IME is missing or fails; a reading is never worth a crash.
    public static string? Of(string text)
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        try
        {
            return Phonetic(text);
        }
        catch
        {
            return null;
        }
    }

    [SupportedOSPlatform("windows")]
    private static string? Phonetic(string text)
    {
        if (Type.GetTypeFromProgID("MSIME.Japan") is not { } type || Activator.CreateInstance(type) is not IFELanguage ime)
        {
            return null;
        }

        ime.Open();
        try
        {
            return ime.GetPhonetic(text, 1, -1, out var result) == 0 ? result : null;
        }
        finally
        {
            ime.Close();
        }
    }

    // Declaration order is the COM vtable order; GetPhonetic is the only method called.
    [ComImport, Guid("019F7152-E6DB-11d0-83C3-00C04FDDB82E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFELanguage
    {
        int Open();
        int Close();
        int GetJMorphResult(uint dwRequest, uint dwCMode, int cwchInput, [MarshalAs(UnmanagedType.LPWStr)] string pwchInput, IntPtr pfCInfo, out object ppResult);
        int GetConversionModeCaps(ref uint pdwCaps);
        int GetPhonetic([MarshalAs(UnmanagedType.BStr)] string str, int start, int length, [MarshalAs(UnmanagedType.BStr)] out string result);
        int GetConversion([MarshalAs(UnmanagedType.BStr)] string str, int start, int length, [MarshalAs(UnmanagedType.BStr)] out string result);
    }
}

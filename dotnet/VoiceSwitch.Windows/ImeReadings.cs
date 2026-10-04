using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace VoiceSwitch.Windows;

public static class ImeReadings
{
    public static string? Of(string text) => Of([text])[0];

    // Hiragana readings from MS-IME, null where MS-IME is missing or fails; a reading is never worth a crash.
    // One call for all words: starting MS-IME costs about 200 ms each time.
    public static string?[] Of(IReadOnlyList<string> texts)
    {
        var readings = new string?[texts.Count];
        if (!OperatingSystem.IsWindows())
        {
            return readings;
        }

        // IFELanguage has no proxy, so from an MTA thread (the runtime's workers, any console host) the cast fails with
        // E_NOINTERFACE. A thread of its own in an STA is the only place the call works.
        var thread = new Thread(() =>
        {
            try
            {
                Phonetic(texts, readings);
            }
            catch
            {
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        return readings;
    }

    [SupportedOSPlatform("windows")]
    private static void Phonetic(IReadOnlyList<string> texts, string?[] readings)
    {
        if (Type.GetTypeFromProgID("MSIME.Japan") is not { } type || Activator.CreateInstance(type) is not IFELanguage ime)
        {
            return;
        }

        ime.Open();
        try
        {
            for (var i = 0; i < texts.Count; i++)
            {
                readings[i] = ime.GetPhonetic(texts[i], 1, -1, out var result) == 0 ? result : null;
            }
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

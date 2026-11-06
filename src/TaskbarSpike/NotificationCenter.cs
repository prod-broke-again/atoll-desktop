namespace TaskbarSpike;

/// <summary>
/// Opens the Windows notification center the same way a click on the shell clock does: Win+N.
/// The chord is one SendInput, so the Win key is not pressed alone and Start stays closed.
/// Shell_TrayWnd is not shown.
/// </summary>
internal static class NotificationCenter
{
    public static void Toggle()
    {
        var inputs = new NativeMethods.Input[4];
        inputs[0] = Key(NativeMethods.VkLwin, NativeMethods.KeyeventfExtendedKey);
        inputs[1] = Key(NativeMethods.VkN, 0);
        inputs[2] = Key(NativeMethods.VkN, NativeMethods.KeyeventfKeyup);
        inputs[3] = Key(NativeMethods.VkLwin, NativeMethods.KeyeventfExtendedKey | NativeMethods.KeyeventfKeyup);

        var sent = NativeMethods.SendInput((uint)inputs.Length, inputs, System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.Input>());
        if (sent != inputs.Length)
        {
            Console.WriteLine("[spike] clock hotkey failed: " + System.Runtime.InteropServices.Marshal.GetLastWin32Error());
        }
    }

    /// <summary>Task View (Win+Tab) — "Представление задач" в полосе Win10.</summary>
    public static void TaskView()
    {
        var inputs = new NativeMethods.Input[4];
        inputs[0] = Key(NativeMethods.VkLwin, NativeMethods.KeyeventfExtendedKey);
        inputs[1] = Key(NativeMethods.VkTab, 0);
        inputs[2] = Key(NativeMethods.VkTab, NativeMethods.KeyeventfKeyup);
        inputs[3] = Key(NativeMethods.VkLwin, NativeMethods.KeyeventfExtendedKey | NativeMethods.KeyeventfKeyup);
        NativeMethods.SendInput((uint)inputs.Length, inputs, System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.Input>());
    }

    /// <summary>Cast / wireless display (Win+K) — the "Трансляция" quick tile.</summary>
    public static void Cast()
    {
        var inputs = new NativeMethods.Input[4];
        inputs[0] = Key(NativeMethods.VkLwin, NativeMethods.KeyeventfExtendedKey);
        inputs[1] = Key(NativeMethods.VkK, 0);
        inputs[2] = Key(NativeMethods.VkK, NativeMethods.KeyeventfKeyup);
        inputs[3] = Key(NativeMethods.VkLwin, NativeMethods.KeyeventfExtendedKey | NativeMethods.KeyeventfKeyup);
        NativeMethods.SendInput((uint)inputs.Length, inputs, System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.Input>());
    }

    private static NativeMethods.Input Key(ushort virtualKey, uint flags)
    {
        return new NativeMethods.Input
        {
            Type = NativeMethods.InputKeyboard,
            Data = new NativeMethods.InputUnion
            {
                Keyboard = new NativeMethods.KeyboardInput
                {
                    VirtualKey = virtualKey,
                    Flags = flags,
                },
            },
        };
    }
}

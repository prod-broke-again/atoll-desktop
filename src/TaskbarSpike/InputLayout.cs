using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace TaskbarSpike;

/// <summary>
/// Keyboard layout of the active window. The label is the Windows taskbar
/// abbreviation: the first three letters of the language's own name (РУС, ENG).
/// Read and switch through the window thread only — never by showing Shell_TrayWnd.
/// </summary>
internal static class InputLayout
{
    private const int PendingMs = 900;
    private const int ClickGapMs = 350;

    private static readonly object Gate = new();
    private static IntPtr _focus;
    private static IntPtr _topLevel;
    private static IntPtr _pendingHkl;
    private static long _pendingUntil;
    private static long _ignoreUntil;
    private static string _label = "";
    private static string _logged = "";
    private static bool _faultLogged;

    public static string Label
    {
        get
        {
            lock (Gate)
            {
                return _label;
            }
        }
    }

    public static bool Observe()
    {
        try
        {
            return ObserveCore();
        }
        catch (Exception ex)
        {
            if (_faultLogged)
            {
                return false;
            }

            _faultLogged = true;
            Console.WriteLine($"[spike] input layout: {ex.Message}");
            return false;
        }
    }

    public static void Cycle()
    {
        lock (Gate)
        {
            var now = Environment.TickCount64;
            if (now < _ignoreUntil)
            {
                return;
            }

            // WebView can deliver the same click again while Activate pumps messages.
            _ignoreUntil = now + ClickGapMs;
        }

        try
        {
            CycleCore();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[spike] input layout switch: {ex.Message}");
        }
    }

    private static bool ObserveCore()
    {
        var foreground = NativeMethods.GetForegroundWindow();
        IntPtr focus;
        IntPtr top;
        if (foreground != IntPtr.Zero && !IsOwnProcess(foreground))
        {
            top = Root(foreground);
            focus = FocusOf(top);
        }
        else
        {
            lock (Gate)
            {
                focus = _focus;
                top = _topLevel;
            }

            if (focus == IntPtr.Zero || !NativeMethods.IsWindow(focus))
            {
                return false;
            }
        }

        var hkl = LayoutOf(focus);
        string? changed = null;
        var updated = false;
        lock (Gate)
        {
            _focus = focus;
            _topLevel = top;
            if (_pendingHkl != IntPtr.Zero && Environment.TickCount64 < _pendingUntil && hkl != _pendingHkl)
            {
                return false;
            }

            _pendingHkl = IntPtr.Zero;
            var label = ShortName(hkl);
            if (label.Length == 0 || label == _label)
            {
                return false;
            }

            _label = label;
            updated = true;
            if (label != _logged)
            {
                _logged = label;
                changed = label + " (" + Describe(hkl) + ")";
            }
        }

        if (changed != null)
        {
            Console.WriteLine("[spike] input layout: " + changed);
        }

        return updated;
    }

    private static void CycleCore()
    {
        IntPtr focus;
        IntPtr top;
        lock (Gate)
        {
            focus = _focus;
            top = _topLevel;
        }

        if (focus == IntPtr.Zero || !NativeMethods.IsWindow(focus))
        {
            var foreground = NativeMethods.GetForegroundWindow();
            if (foreground == IntPtr.Zero || IsOwnProcess(foreground))
            {
                return;
            }

            top = Root(foreground);
            focus = FocusOf(top);
        }

        var current = LayoutOf(focus);
        if (!TryNext(current, out var next))
        {
            return;
        }

        var posted = NativeMethods.PostMessage(
            focus,
            NativeMethods.WmInputLangChangeRequest,
            new IntPtr(NativeMethods.InputLangChangeSysCharset),
            next);
        if (!posted && top != focus)
        {
            NativeMethods.PostMessage(
                top,
                NativeMethods.WmInputLangChangeRequest,
                new IntPtr(NativeMethods.InputLangChangeSysCharset),
                next);
        }

        if (top != IntPtr.Zero && NativeMethods.IsWindow(top) && !NativeMethods.IsIconic(top) && !IsOwnProcess(top))
        {
            WindowCatalog.Activate(top);
        }

        var label = ShortName(next);
        lock (Gate)
        {
            _focus = focus;
            _topLevel = top;
            _pendingHkl = next;
            _pendingUntil = Environment.TickCount64 + PendingMs;
            if (label.Length > 0)
            {
                _label = label;
                _logged = label;
            }
        }

        if (label.Length > 0)
        {
            Console.WriteLine($"[spike] input layout -> {label} ({Describe(next)})");
        }
    }

    private static IntPtr LayoutOf(IntPtr hwnd)
    {
        var threadId = NativeMethods.GetWindowThreadProcessId(hwnd, out _);
        return threadId == 0 ? IntPtr.Zero : NativeMethods.GetKeyboardLayout(threadId);
    }

    private static bool TryNext(IntPtr current, out IntPtr next)
    {
        next = IntPtr.Zero;
        var count = NativeMethods.GetKeyboardLayoutListCount(0, IntPtr.Zero);
        if (count < 2)
        {
            return false;
        }

        var layouts = new IntPtr[count];
        var copied = NativeMethods.GetKeyboardLayoutList(layouts.Length, layouts);
        if (copied < 2)
        {
            return false;
        }

        if (copied < layouts.Length)
        {
            Array.Resize(ref layouts, copied);
        }

        var index = IndexOf(layouts, current);
        var start = index < 0 ? 0 : index;
        for (var step = 1; step <= layouts.Length; step++)
        {
            var candidate = layouts[(start + step) % layouts.Length];
            if (candidate != IntPtr.Zero && candidate != current)
            {
                next = candidate;
                return true;
            }
        }

        return false;
    }

    private static int IndexOf(IntPtr[] layouts, IntPtr current)
    {
        for (var i = 0; i < layouts.Length; i++)
        {
            if (layouts[i] == current)
            {
                return i;
            }
        }

        var lang = LangId(current);
        var device = DeviceId(current);
        for (var i = 0; i < layouts.Length; i++)
        {
            if (LangId(layouts[i]) == lang && DeviceId(layouts[i]) == device)
            {
                return i;
            }
        }

        for (var i = 0; i < layouts.Length; i++)
        {
            if (LangId(layouts[i]) == lang)
            {
                return i;
            }
        }

        return -1;
    }

    private static IntPtr FocusOf(IntPtr topLevel)
    {
        var threadId = NativeMethods.GetWindowThreadProcessId(topLevel, out _);
        var info = new NativeMethods.GuiThreadInfo
        {
            cbSize = Marshal.SizeOf<NativeMethods.GuiThreadInfo>(),
        };

        if (threadId != 0 &&
            NativeMethods.GetGUIThreadInfo(threadId, ref info) &&
            info.hwndFocus != IntPtr.Zero &&
            NativeMethods.IsWindow(info.hwndFocus))
        {
            return info.hwndFocus;
        }

        return topLevel;
    }

    private static IntPtr Root(IntPtr hwnd)
    {
        var root = NativeMethods.GetAncestor(hwnd, NativeMethods.GaRoot);
        return root == IntPtr.Zero ? hwnd : root;
    }

    private static bool IsOwnProcess(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            return false;
        }

        NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
        return pid == (uint)Environment.ProcessId;
    }

    private static string ShortName(IntPtr hkl)
    {
        var langId = LangId(hkl);
        if (langId == 0)
        {
            return "";
        }

        var native = NativeLanguageName(langId);
        var letters = new StringBuilder(3);
        foreach (var ch in native)
        {
            if (!char.IsLetter(ch))
            {
                if (letters.Length > 0)
                {
                    break;
                }

                continue;
            }

            letters.Append(ch);
            if (letters.Length == 3)
            {
                break;
            }
        }

        if (letters.Length == 0)
        {
            return "";
        }

        try
        {
            return letters.ToString().ToUpper(CultureInfo.GetCultureInfo(langId));
        }
        catch (CultureNotFoundException)
        {
            return letters.ToString().ToUpperInvariant();
        }
    }

    private static string NativeLanguageName(ushort langId)
    {
        try
        {
            var culture = CultureInfo.GetCultureInfo(langId);
            var language = culture;
            if (!culture.IsNeutralCulture &&
                culture.Parent.LCID != 0x7F &&
                !string.IsNullOrWhiteSpace(culture.Parent.NativeName))
            {
                language = culture.Parent;
            }

            var name = language.NativeName;
            var paren = name.IndexOf('(');
            if (paren > 0)
            {
                name = name[..paren];
            }

            var trimmed = name.Trim();
            if (trimmed.Length > 0)
            {
                return trimmed;
            }
        }
        catch (CultureNotFoundException)
        {
            // Layouts without a CultureInfo still have a native name in NLS.
        }

        var buffer = new StringBuilder(128);
        var written = NativeMethods.GetLocaleInfo(langId, NativeMethods.LocaleSNativeLangName, buffer, buffer.Capacity);
        return written > 0 ? buffer.ToString().Trim() : "";
    }

    private static ushort LangId(IntPtr hkl)
    {
        return unchecked((ushort)hkl.ToInt64());
    }

    private static ushort DeviceId(IntPtr hkl)
    {
        return unchecked((ushort)(hkl.ToInt64() >> 16));
    }

    private static string Describe(IntPtr hkl)
    {
        return unchecked((uint)hkl.ToInt64()).ToString("X8");
    }
}

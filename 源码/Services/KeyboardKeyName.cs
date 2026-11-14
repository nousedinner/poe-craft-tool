using System.Windows.Input;

namespace ShiKe.Services;

/// <summary>热键录制与按键槽位共用的 WPF 键名映射。</summary>
public static class KeyboardKeyName
{
    public static string? FromKey(Key key, bool lowercase = false)
    {
        string Named(string value) => lowercase ? value.ToLowerInvariant() : value;

        if (key is >= Key.A and <= Key.Z) return lowercase ? key.ToString().ToLowerInvariant() : key.ToString().ToUpperInvariant();
        if (key is >= Key.D0 and <= Key.D9) return ((int)key - (int)Key.D0).ToString();
        if (key is >= Key.NumPad0 and <= Key.NumPad9) return key.ToString();
        if (key is >= Key.F1 and <= Key.F24) return key.ToString().ToUpperInvariant();
        return key switch
        {
            Key.LeftCtrl or Key.RightCtrl => Named("Ctrl"),
            Key.LeftAlt or Key.RightAlt => Named("Alt"),
            Key.LeftShift or Key.RightShift => Named("Shift"),
            Key.LWin or Key.RWin => Named("Win"),
            Key.Enter or Key.Return => Named("Enter"),
            Key.Space => Named("Space"),
            Key.Tab => Named("Tab"),
            Key.Escape => lowercase ? "esc" : "Esc",
            Key.Back => lowercase ? "backspace" : "Backspace",
            Key.Delete => Named("Delete"),
            Key.Insert => Named("Insert"),
            Key.Home => Named("Home"),
            Key.End => Named("End"),
            Key.PageUp => Named("PageUp"),
            Key.PageDown => Named("PageDown"),
            Key.Up => Named("Up"),
            Key.Down => Named("Down"),
            Key.Left => Named("Left"),
            Key.Right => Named("Right"),
            Key.Oem3 => "Oem3",
            Key.OemMinus => "-",
            Key.OemPlus => "=",
            Key.OemOpenBrackets => "[",
            Key.Oem6 => "]",
            Key.Oem5 => "\\",
            Key.Oem1 => ";",
            Key.OemQuotes => "'",
            Key.OemComma => ",",
            Key.OemPeriod => ".",
            Key.Oem2 => "/",
            _ => null,
        };
    }

    public static Key Resolve(KeyEventArgs args) => args.Key switch
    {
        Key.System => args.SystemKey,
        Key.ImeProcessed when args.ImeProcessedKey is not (Key.None or Key.ImeProcessed) => args.ImeProcessedKey,
        Key.DeadCharProcessed when args.DeadCharProcessedKey is not (Key.None or Key.DeadCharProcessed) => args.DeadCharProcessedKey,
        Key.ImeProcessed or Key.DeadCharProcessed => Key.None,
        _ => args.Key,
    };
}

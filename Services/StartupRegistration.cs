using System;
using System.IO;
using Microsoft.Win32;

namespace popotranslation.Services;

// Per-user Run entries start unpackaged desktop apps at sign-in.
// https://learn.microsoft.com/en-us/windows/win32/setupapi/run-and-runonce-registry-keys (2026-10-29)
internal static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "PopoTranslation";

    private static string Command
    {
        get
        {
            var path = Environment.ProcessPath;
            if (string.IsNullOrEmpty(path) || !string.Equals(Path.GetFileName(path), "popotranslation.exe", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Cannot identify the application executable.");
            var command = $"\"{path}\"";
            if (command.Length > 260) throw new InvalidOperationException("Startup command is too long.");
            return command;
        }
    }

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        var value = key?.GetValue(ValueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        return value is string command && string.Equals(command, Command, StringComparison.OrdinalIgnoreCase);
    }

    public static void SetEnabled(bool enabled)
    {
        var command = Command;
        using var key = Registry.CurrentUser.CreateSubKey(RunKey, true) ?? throw new InvalidOperationException("Cannot open startup settings.");
        var current = key.GetValue(ValueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        if (current is not null && (current is not string existing || !string.Equals(existing, command, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("A different startup entry already exists.");
        if (enabled) key.SetValue(ValueName, command, RegistryValueKind.String);
        else key.DeleteValue(ValueName, false);
        if (IsEnabled() != enabled) throw new InvalidOperationException("Startup setting could not be verified.");
    }
}

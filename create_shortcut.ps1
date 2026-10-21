# Item 49: ярлык «Entity Analyzer» на рабочем столе.
#
# Зачем: подсказка ярлыка в проводнике — это поле Description. У старых
# ярлыков она была «?????»: WScript.Shell.CreateShortcut пишет описание
# через ANSI-интерфейс (IShellLinkA), и кириллица теряется на системах
# с не-русской ANSI-кодовой страницей. Здесь используется Unicode-интерфейс
# IShellLinkW (LPWStr) — описание сохраняется корректно.
#
# Запуск:  powershell -ExecutionPolicy Bypass -File create_shortcut.ps1
# (файл сохранён в UTF-8 с BOM — PowerShell 5.1 читает кириллицу корректно)

$ErrorActionPreference = 'Stop'

$exe = Join-Path $PSScriptRoot 'publish\EntityAnalyzer.exe'
if (-not (Test-Path $exe)) {
    Write-Host "Не найден опубликованный exe: $exe" -ForegroundColor Red
    Write-Host 'Сначала выполните:  dotnet publish src\EntityAnalyzer.App -c Release -o publish'
    exit 1
}

$description = 'Entity Analyzer — поиск и анализ упоминаний сущностей в текстовом корпусе. Кодекс, отчёты, граф совместных упоминаний.'
$desktop = [Environment]::GetFolderPath('Desktop')
$shortcutPath = Join-Path $desktop 'Entity Analyzer.lnk'

if (-not ('EaShellLink' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Text;

public static class EaShellLink
{
    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLinkClass { }

    [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cch, IntPtr pfd, uint fFlags);
        void GetIDList(out IntPtr ppidl);
        void SetIDList(IntPtr pidl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName, int cch);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir, int cch);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs, int cch);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
        void GetHotkey(out short pwHotkey);
        void SetHotkey(short wHotkey);
        void GetShowCmd(out int piShowCmd);
        void SetShowCmd(int iShowCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszIconPath, int cch, out int piIcon);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, uint dwReserved);
        void Resolve(IntPtr hwnd, uint fFlags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }

    [ComImport, Guid("0000010b-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPersistFile
    {
        void GetClassID(out Guid pClassID);
        [PreserveSig] int IsDirty();
        void Load([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, uint dwMode);
        void Save([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, [MarshalAs(UnmanagedType.Bool)] bool fRemember);
        void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string pszFileName);
        void GetCurFile([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder ppszFileName);
    }

    public static void Apply(string lnkPath, string targetPath, string iconPath, string workingDir, string description)
    {
        var link = new ShellLinkClass();
        try
        {
            var sl = (IShellLinkW)link;
            var pf = (IPersistFile)link;
            pf.Load(lnkPath, 2 /* STGM_READWRITE */);
            sl.SetPath(targetPath);
            sl.SetWorkingDirectory(workingDir);
            sl.SetIconLocation(iconPath, 0);
            sl.SetDescription(description);
            pf.Save(lnkPath, true);
        }
        finally
        {
            Marshal.ReleaseComObject(link);
        }
    }

    public static string ReadDescription(string lnkPath)
    {
        var link = new ShellLinkClass();
        try
        {
            var sl = (IShellLinkW)link;
            var pf = (IPersistFile)link;
            pf.Load(lnkPath, 0 /* STGM_READ */);
            var sb = new StringBuilder(1024);
            sl.GetDescription(sb, sb.Capacity);
            return sb.ToString();
        }
        finally
        {
            Marshal.ReleaseComObject(link);
        }
    }
}
'@
}

function Set-Shortcut([string]$Path, [bool]$CreateIfMissing) {
    if (-not (Test-Path $Path)) {
        if (-not $CreateIfMissing) { return $false }
    }
    [EaShellLink]::Apply($Path, $exe, $exe, (Split-Path $exe), $description)
    return $true
}

# 1) Создать основной ярлык (пересоздаётся всегда — описание актуализируется).
[void](Set-Shortcut $shortcutPath $true)
Write-Host "Ярлык готов: $shortcutPath" -ForegroundColor Green

# 2) Починить существующие ярлыки на этот же exe (в т.ч. старые с «?????»).
$fixed = 0
Get-ChildItem -Path $desktop -Filter '*.lnk' -File | ForEach-Object {
    if ($_.FullName -eq $shortcutPath) { return }
    try {
        $probe = (New-Object -ComObject WScript.Shell).CreateShortcut($_.FullName)
        $tp = $probe.TargetPath
        if ($tp -and $tp.EndsWith('EntityAnalyzer.exe', [StringComparison]::OrdinalIgnoreCase)) {
            [EaShellLink]::Apply($_.FullName, $exe, $exe, (Split-Path $exe), $description)
            $fixed++
            Write-Host "Обновлено описание ярлыка: $($_.Name)"
        }
    } catch {
        # Чужой/битый ярлык — не трогаем.
    }
}
Write-Host "Обновлено существующих ярлыков: $fixed"

# 3) Проверка: читаем описание обратно через Unicode-интерфейс.
$check = [EaShellLink]::ReadDescription($shortcutPath)
Write-Host ("Проверка описания ярлыка: " + $check)

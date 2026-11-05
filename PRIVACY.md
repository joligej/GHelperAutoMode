# GHelperAutoMode privacy policy

Updated 5 November 2026. This policy covers the GHelperAutoMode application and its installer.

## Information used on your computer

While running, AutoMode reads the foreground application's executable name, process ID, and CPU time to apply application rules. It also reads total CPU load, available CPU and NVIDIA GPU temperatures, GPU load, display-power state, Windows session events, and modifier-key state to avoid interfering with your shortcuts.

To work with G-Helper, AutoMode reads its configuration and checks its process, executable path, window handles, and privilege level. Optional lighting management reads Windows accent and Dynamic Lighting settings and updates the selected lighting owner's settings. Application rules can contain executable names you enter yourself.

The interface and diagnostics use this information to explain profile decisions, failed operations, startup registration, and lighting ownership. Diagnostic output can include foreground executable names and IDs, application-rule matches, timestamps, local file paths containing your Windows username, startup-task names containing your Windows account SID, hardware readings, and error messages. It does not read document contents, browser history, passwords, or typed text.

## Files and settings

Settings and logs belong to the signed-in Windows account, including with a machine-wide installation. They are stored under `%LOCALAPPDATA%\GHelperAutoMode`.

- `config.json` contains your preferences, thresholds, application rules, and lighting selection.
- `config.pre-v*.json` and `config.invalid.*.json` preserve configuration before migration or invalid-JSON recovery.
- `logs\automode.log` and its numbered rotations contain diagnostic events when **Save logs** is enabled. Periodic telemetry logging is a separate setting and defaults to off.
- `logs\last-recovery.txt` records the most recent abnormal tray exit and retry count. This small file is overwritten on the next recovery event, including when routine logging is disabled.

Routine logging defaults to a 5 MiB file limit with three retained rotations. You can change these values or disable routine logging in Settings. Configuration backups remain until you delete them.

If you enable managed keyboard lighting, AutoMode can update `%APPDATA%\GHelper\config.json` and the current user's Windows Dynamic Lighting settings. Its first G-Helper lighting change saves a recovery copy at `%APPDATA%\GHelper\config.before-GHelperAutoMode-lighting.json`.

Enabling login startup creates an account-specific Task Scheduler entry pointing to the installed executable. Windows Installer also maintains the installation registration, Start menu shortcut, and its standard package cache. Windows may create separate crash reports under its own diagnostic settings.

## Network access and sharing

AutoMode processes the information described above locally. The application and setup have no analytics service, upload endpoint, account system, or automatic network requests. The GPU worker uses local pipes to return readings to the tray process.

**Copy diagnostics** places the displayed diagnostic text on your clipboard. Sharing that text, a log, or a screenshot is your choice. Check it for executable names and local paths before posting it publicly. Opening a web link uses your browser. GitHub downloads, WinGet, Windows diagnostics, and G-Helper operate under their own settings and policies.

## Uninstalling and deleting data

Uninstalling removes the application files and Start menu shortcut and removes AutoMode's startup registration for the account performing the uninstall. Personal settings, logs, and configuration backups remain for a later reinstall.

To remove those files, exit AutoMode, uninstall it if desired, and delete `%LOCALAPPDATA%\GHelperAutoMode` for each account that used the app. You can separately delete the G-Helper recovery copy listed above. Lighting preferences already applied in Windows or G-Helper can be changed through those applications or through AutoMode's lighting menu.

## Questions

For questions about these data practices, open an issue in the project repository. Include only the diagnostic information you intend to make public. [GHelperAutoMode issues](https://github.com/joligej/GHelperAutoMode/issues)

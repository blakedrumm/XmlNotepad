## Settings

XML Notepad stores your user settings in a file located in
this folder, by default:

```
%APPDATA%\Microsoft\Xml Notepad\XmlNotepad.settings
```

The settings include the last size and location of the window,
a lost of most recently opened files, and all the things you see
in the [Options Dialog](options.md).

You can change where XML Notepad stores the settings file
if you go to the [Options Dialog](options.md) and change the
`Settings Location` option.  The options are:

| Name         | Location      |
| ------------- |-------------|
| Portable | Stored where ever XmlNotepad.exe was installed |
| Local   | %LOCALAPPDATA%\Microsoft\Xml Notepad\ |
| Roaming | %APPDATA%\Microsoft\Xml Notepad\ |

This `APPDATA` folder might be automatically migrated to all your machines because it associated with a [Roaming User Profile](https://blogs.windows.com/windowsdeveloper/2016/05/03/getting-started-with-roaming-app-data/).

The `Portable` option make it easy for you to `xcopy` the folder
to other machines and get the same settings.  This option will
will not be available if you are running XML Notepad in a folder that is read only (like `c:\Program Files`) or if you are running XML Notepad from a [ClickOnce install](../install.md).

Changing the `Settings Location` option moves the `XmlNotepad.settings` file
accordingly.  This means you should not have multiple XmlNotepad.settings
files in all these locations, if you do it will search in this priority order:

1. Portable
2. Local
3. Roaming

and it will use the first one that it finds.
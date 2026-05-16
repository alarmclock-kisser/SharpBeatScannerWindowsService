!include "MUI2.nsh"
!include "LogicLib.nsh"

; =========================================================
; SharpBeatScanner - NSIS Installer Script
; =========================================================

Name "SharpBeatScanner"
OutFile "SharpBeatScanner_Setup.exe"

; Standard-Installationsverzeichnis (kann im Installer angepasst werden)
InstallDir "$PROGRAMFILES\SharpBeatScanner_Service"

; Benötigt Administratorrechte (für C:\Program Files)
RequestExecutionLevel admin

; ---------------------------------------------------------
; Ansichts- und UI-Einstellungen
!define MUI_ABORTWARNING
!define MUI_FINISHPAGE_RUN "$INSTDIR\SharpBeatScanner.Cli.exe"
!define MUI_FINISHPAGE_RUN_TEXT "SharpBeatScanner jetzt starten"

; ---------------------------------------------------------
; Installer Seiten (Pages)
!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_COMPONENTS
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_PAGE_FINISH

; Uninstaller Seiten
!insertmacro MUI_UNPAGE_WELCOME
!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES
!insertmacro MUI_UNPAGE_FINISH

; ---------------------------------------------------------
; Sprachen
!insertmacro MUI_LANGUAGE "German"
!insertmacro MUI_LANGUAGE "English"

; ---------------------------------------------------------
; Installer: Hauptprogramm
Section "SharpBeatScanner (Core)" SecCore
	SectionIn RO ; Read-Only (Muss installiert werden, Checkbox ist fixiert)

	; Laufenden Prozess beenden, falls eine alte Version läuft
	nsExec::Exec 'taskkill /F /IM SharpBeatScanner.Cli.exe'
	Sleep 1000

	; Zielverzeichnis setzen
	SetOutPath "$INSTDIR"

	; ++++++++++++++++++++++++++++++++++++++++++++++++++++++++
	; WICHTIG: Passe hier den Pfad an, wo Visual Studio
	; deine fertig kompilierte Release-Version hinlegt!
	; Am besten nutzt du "dotnet publish -c Release", um
	; einen sauberen Ordner zu haben.
	; ++++++++++++++++++++++++++++++++++++++++++++++++++++++++
	File /r "SharpBeatScanner.Cli\bin\Release\net10.0-windows\*"

	; Uninstaller erstellen
	WriteUninstaller "$INSTDIR\Uninstall.exe"

	; Deinstallations-Eintrag in der Windows Systemsteuerung ("Apps & Features") erstellen
	WriteRegStr HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\SharpBeatScanner" "DisplayName" "SharpBeatScanner Background Service"
	WriteRegStr HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\SharpBeatScanner" "UninstallString" "$\"$INSTDIR\Uninstall.exe$\""
	WriteRegStr HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\SharpBeatScanner" "DisplayIcon" "$\"$INSTDIR\SharpBeatScanner.Cli.exe$\""

	; Startmenü Verknüpfung
	CreateShortcut "$SMPROGRAMS\SharpBeatScanner.lnk" "$INSTDIR\SharpBeatScanner.Cli.exe"
SectionEnd

; ---------------------------------------------------------
; Installer: Autostart
Section "Autostart bei Windows-Login (Taskmanager)" SecAutostart
	; Da der Installer als Administrator läuft, würde das direkte Schreiben 
	; in die HKCU (Current User Registry) die Registry des Admins treffen.
	; Daher tricksen wir: Wir modifizieren dynamisch die appsettings.json!
	; Wenn das Programm startet, liest es EnableAtStartup = true aus und
	; legt den Registry-Key für den angemeldeten Benutzer selbst an.

	nsExec::ExecToLog 'powershell.exe -ExecutionPolicy Bypass -NoProfile -WindowStyle Hidden -Command "(Get-Content -Path ''$INSTDIR\appsettings.json'') -replace ''\"EnableAtStartup\": false'', ''\"EnableAtStartup\": true'' | Set-Content -Path ''$INSTDIR\appsettings.json''"'
SectionEnd

; ---------------------------------------------------------
; Uninstaller
Section "Uninstall"
	; Prozess beenden
	nsExec::Exec 'taskkill /F /IM SharpBeatScanner.Cli.exe'
	Sleep 1000

	; Alle Dateien löschen
	Delete "$INSTDIR\*.*"
	RMDir /r "$INSTDIR"

	; Startmenü Verknüpfung löschen
	Delete "$SMPROGRAMS\SharpBeatScanner.lnk"

	; Einträge aus "Apps & Features" entfernen
	DeleteRegKey HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\SharpBeatScanner"

	; Autostart Registry Wert für den aktuellen User entfernen
	DeleteRegValue HKCU "SOFTWARE\Microsoft\Windows\CurrentVersion\Run" "SharpBeatScanner"
SectionEnd

; ---------------------------------------------------------
; Beschreibungen für die Components-Seite
LangString DESC_SecCore ${LANG_GERMAN} "Installiert die Kernanwendung und Hintergrunddienste."
LangString DESC_SecAutostart ${LANG_GERMAN} "Startet das Programm bei jeder Anmeldung automatisch minimiert (Im Taskmanager einstellbar)."

!insertmacro MUI_FUNCTION_DESCRIPTION_BEGIN
  !insertmacro MUI_DESCRIPTION_TEXT ${SecCore} $(DESC_SecCore)
  !insertmacro MUI_DESCRIPTION_TEXT ${SecAutostart} $(DESC_SecAutostart)
!insertmacro MUI_FUNCTION_DESCRIPTION_END

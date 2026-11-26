# Wallfly-Installer (Inno Setup)

`wallfly.iss` verpackt `windows\dist\Wallfly\` in ein Setup für **einen Benutzer, ohne Administratorrechte**.
Das Skript wird nicht automatisch gebaut, weil Inno Setup auf dem Build-Rechner nicht installiert ist.

| Punkt | Verhalten |
|---|---|
| Ziel | `%LOCALAPPDATA%\Programs\Wallfly` (`PrivilegesRequired=lowest`, nur `HKCU`) |
| Inhalt | alles aus `dist\Wallfly\`: `Wallfly.exe`, `Wallfly Detector.exe`, `wallfly-mcp.exe`, eine gemeinsame .NET-Runtime, `WallflyAudio.dll`, `whisper\`, `models\`, `third-party\`, `VERSION.txt` |
| Verknüpfungen | Startmenü „Wallfly“; Desktop optional (Aufgabe, standardmäßig aus) |
| Autostart (optional) | Aufgabe „Wallfly Detector bei der Anmeldung starten“ schreibt `HKCU\Software\Microsoft\Windows\CurrentVersion\Run\WallflyDetector` = `"<app>\Wallfly Detector.exe" --autostart` – derselbe Wert, den die App selbst setzt (`DetectorAutostart`) |
| Update | gleiche `AppId`; laufende Programme werden über den Restart Manager zum Schließen aufgefordert |
| Deinstallation | beendet `Wallfly Detector.exe` und `wallfly-mcp.exe`, entfernt Programmdateien, Verknüpfungen und den Run-Eintrag (nur wenn er in diese Installation zeigt). **Die Meeting-Bibliothek (`%LOCALAPPDATA%\Wallfly` bzw. der in der App gewählte Ort) und die Schlüssel im Credential Manager bleiben erhalten.** |
| Version | erste Zeile von `dist\Wallfly\VERSION.txt` (z. B. `0.1.0+2354125`), überschreibbar mit `/DAppVersion=…` |

## Bauen

1. Inno Setup 6.3 oder neuer installieren (pro Benutzer genügt), z. B. `winget install JRSoftware.InnoSetup`.
2. Programm veröffentlichen und Setup bauen:

```powershell
.\windows\build.ps1 -Native -Test -Publish
& "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe" .\windows\installer\wallfly.iss
# Ergebnis: windows\dist\installer\Wallfly-Setup-<Version>.exe
```

Stille Installation (z. B. Softwareverteilung): `Wallfly-Setup-<Version>.exe /VERYSILENT /SUPPRESSMSGBOXES /TASKS="detectorautostart"`.

## Authenticode-Signatur

Unsignierte Programme lösen SmartScreen-Warnungen aus und werden in Firmenumgebungen oft blockiert. Vorgesehen:

1. **Programmdateien signieren, bevor das Setup gebaut wird**: `Wallfly.exe`, `Wallfly Detector.exe`, `wallfly-mcp.exe`,
   die eigenen `Wallfly*.dll`/`wallfly-mcp.dll` und `WallflyAudio.dll`. `build.ps1 -Publish -Sign` zeigt diese Liste heute
   nur an (Platzhalter). Befehl, sobald ein Zertifikat da ist:
   `signtool sign /fd SHA256 /tr <RFC-3161-Zeitstempel-URL> /td SHA256 /n "<Zertifikatsname>" <Datei>`.
   Die Dateien der .NET-Runtime und von Drittanbietern sind bereits vom Hersteller signiert und bleiben unverändert.
2. **Setup und Deinstallationsprogramm signieren**: in Inno Setup einen Signaturbefehl namens `wallfly` hinterlegen
   (`ISCC.exe "/Swallfly=signtool sign /fd SHA256 /tr <URL> /td SHA256 /n `"<Zertifikat>`" `$f" …`) und in `wallfly.iss`
   die Zeilen `SignTool=wallfly` und `SignedUninstaller=yes` einkommentieren.
3. Immer mit Zeitstempel signieren, damit die Signatur das Ablaufdatum des Zertifikats überlebt. Ein EV- oder
   Azure-Trusted-Signing-Zertifikat bringt sofortige SmartScreen-Reputation; ein OV-Zertifikat muss sie erst aufbauen.

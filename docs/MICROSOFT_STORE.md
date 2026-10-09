# Microsoft Store packaging

The Microsoft Store route provides a Windows-trusted package without purchasing a private Authenticode certificate. Microsoft signs the submitted MSIX after certification and manages install, update, and uninstall. The standalone EXE remains a separate portable distribution.

## Account and identity

1. Register an individual Windows developer account in Partner Center. Microsoft currently provides free individual registration in supported markets and requires identity verification.
2. Reserve **Open Agent Workbench** or another available product name.
3. Open the product identity page and copy the exact **Package/Identity/Name**, **Package/Identity/Publisher**, and publisher display name. These values are assigned by Microsoft and must not be invented.
4. Build the Store candidate with those exact values:

```powershell
.\build_msix.ps1 `
  -Executable .\dist\opensource\OpenAgentWorkbench.exe `
  -OutputPath .\dist\msix\OpenAgentWorkbench-1.0.0-x64.msix `
  -Version 1.0.0.0 `
  -IdentityName '<Package/Identity/Name>' `
  -Publisher '<Package/Identity/Publisher>' `
  -PublisherDisplayName '<Publisher display name>' `
  -StoreSubmission
```

The build script creates an unsigned upload package. Partner Center signs a package only after certification. Do not distribute the unsigned test package as a trusted release.

## Full-trust capability declaration

The manifest declares `runFullTrust` because Open Agent Workbench is a desktop Agent workbench that launches user-selected local CLI tools and terminals, manages long-running background tasks, and reads or writes user-selected workspaces. These Win32 desktop scenarios require medium-integrity full trust. The app does not request elevation and remains within the current user's permissions.

The package also declares a disabled `windows.startupTask`. Users control it through Windows **Startup apps**. In Store mode the application does not write the Run registry key, replace its packaged EXE, or use the portable self-installer. Store services own those operations.

## Validation before submission

```powershell
.\tests\package-mode-selftest.ps1 -Executable .\dist\opensource\OpenAgentWorkbench.exe
.\tests\msix-packaging-selftest.ps1 -Executable .\dist\opensource\OpenAgentWorkbench.exe -PythonPath (Get-Command python).Source
```

Run the Windows App Certification Kit when it is installed, then upload the MSIX to Partner Center. Certification and Microsoft signing remain external gates; a successful local package build does not prove Store acceptance.

## Functional boundary

The packaged app still runs as a full-trust Win32 desktop process and retains CLI, terminal, workspace, Host, Skill, and MCP functions. Installation-directory writes are disabled in Store mode. Runtime data continues to live in the user's configured workspace and application data directories.

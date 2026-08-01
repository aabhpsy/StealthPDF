# Code Signing for StealthPDF

StealthPDF is a Windows (.NET Framework 4.8 / WPF) desktop app. Unsigned, Windows shows
"Unknown publisher" and SmartScreen may block downloads. Code signing fixes that and lets
the download page (and `SHA256SUMS.txt`) prove the binary is untampered.

> **Status:** `release.ps1` signs **locally** with SSL.com **SimplySign Desktop** + `signtool`
> (now updated to target `StealthPDF.csproj` / `StealthPDF.exe` / `namespace StealthPDF`).
> There is **no CI signing yet** — this document is the path to automate it in GitHub
> Actions. The ready-to-paste snippets live here (not in `.github/workflows/`) so you can
> drop them in when you're ready.

---

## 1. Get a code signing certificate

| Provider | Suitable cert | Approx. cost | CI/cloud signing | Notes |
|---|---|---|---|---|
| **Azure Trusted Signing** | OV/EV-equivalent | ~$9.99/mo (incl. signs) | native CI | Microsoft cloud, no cert file, EV-equivalent reputation. Best for CI. |
| **SSL.com** | IV (Individual) / EV | ~$200–400/yr | eSigner Cloud | You already use SSL.com (SimplySign). Enroll in eSigner for CI. |
| **Certum** | Open Source Developer | ~$25–70/yr | SimplySign Cloud | Cheapest legit option for open-source individuals. |
| **DigiCert / Sectigo** | OV / EV | ~$300–600/yr | DigiCert ONE / Sectigo | Premium; EV needs hardware/cloud. |

**OV vs EV**
- **OV (Organization/Individual Validation):** standard trust; SmartScreen reputation
  builds over time as users run the app. Can be exported to a `.pfx`.
- **EV (Extended Validation):** immediate SmartScreen reputation (no "unknown" flag).
  **Cannot** be a `.pfx` — requires a hardware token or cloud signing. Use a cloud
  backend (Azure Trusted Signing or SSL.com eSigner) for EV in CI.

**Recommendation for this fork:** Azure Trusted Signing (CI-native, EV-equivalent, the
"secret" is never a private key) — or SSL.com eSigner (you're already an SSL.com customer).

---

## 2. Choose a CI signing backend

GitHub Actions runners can't use a USB token or the SimplySign *Desktop* app, so use a
**cloud** signing backend. Pick one and add the listed repository secrets
(Settings → Secrets and variables → Actions).

### Option A — Azure Trusted Signing (recommended)
**One-time:** Azure portal → *Trusted Signing* → create account + certificate profile
(identity validation takes a few days). Then register an Entra ID App for CI access.

Secrets: `AZURE_TENANT_ID`, `AZURE_CLIENT_ID`, `AZURE_CLIENT_SECRET`,
`CODE_SIGNING_ACCOUNT_NAME`, `CODE_SIGNING_PROFILE_NAME`.
Action: `azure/trusted-signing-action@v1`.

### Option B — SSL.com eSigner Cloud
**One-time:** enroll your SSL.com cert in eSigner; retrieve the username, password,
credential ID, API secret, and TOTP secret.

Secrets: `ESIGNER_USERNAME`, `ESIGNER_PASSWORD`, `ESIGNER_CREDENTIAL_ID`,
`ESIGNER_CREDENTIAL_SECRET`, `ESIGNER_TOTP_SECRET`.
Action: `SSLcom/esigner-codesign@v2`.

### Option C — PFX as a GitHub secret (OV only, simplest)
Export an **OV** cert to `.pfx`, base64-encode it, store as a secret. EV certs can't be
exported to `.pfx`, so use Option A or B for EV.

Secrets: `CODESIGN_PFX_BASE64` (base64 of the .pfx), `CODESIGN_PFX_PASSWORD`.

---

## 3. The CI pipeline shape (build → sign → verify → release)

When you implement it, the workflow (`build-sign-release.yml`) on `windows-latest`
should mirror `release.ps1`:

1. **Checkout** + setup .NET (`windows-latest` ships VS/MSBuild + the Windows SDK + the
   .NET Framework 4.8 targeting pack).
2. **Pre-hash pdfium.dll** from the NuGet cache and write `BuildInfo.cs` (so the runtime
   integrity check knows the expected hash) — port §1 of `release.ps1`.
3. **Publish:** `dotnet publish StealthPDF.csproj /p:PublishProfile=FolderProfile1 -c Release`
   → `bin\Release\net48\publish\StealthPDF.exe` (the GPL `-src.zip` is produced
   automatically by the `BundleSource` target in the `.csproj`).
4. **Sign** `StealthPDF.exe` with the chosen backend (make it **conditional** so forks
   without secrets still produce an unsigned build + a warning).
5. **Verify:** `signtool verify /pa /v StealthPDF.exe` — fail the job if it doesn't pass.
6. **Hash:** SHA256 of `StealthPDF.exe` + the `-src.zip`, write `SHA256SUMS.txt`.
7. **Release:** upload artifacts; on a `v*` tag create a GitHub Release with the EXE,
   src zip, and `SHA256SUMS.txt`. The existing `chocolatey-release.yml` and
   `winget-release.yml` already consume a published release.

### Ready-to-paste signing step — Option A (Azure Trusted Signing)

Set a repo **variable** `ENABLE_AZURE_SIGNING=true` and the secrets below.

```yaml
      - name: Sign StealthPDF.exe (Azure Trusted Signing)
        if: ${{ vars.ENABLE_AZURE_SIGNING == 'true' }}
        uses: azure/trusted-signing-action@v1
        with:
          azure-tenant-id: ${{ secrets.AZURE_TENANT_ID }}
          azure-client-id: ${{ secrets.AZURE_CLIENT_ID }}
          azure-client-secret: ${{ secrets.AZURE_CLIENT_SECRET }}
          endpoint: https://<your-signing-endpoint>.codesigning.azure.net/
          code-signing-account-name: ${{ secrets.CODE_SIGNING_ACCOUNT_NAME }}
          certificate-profile-name: ${{ secrets.CODE_SIGNING_PROFILE_NAME }}
          files-folder: ${{ github.workspace }}\KillerPDF\bin\Release\net48\publish
          files-filter: StealthPDF.exe
```

### Ready-to-paste signing step — Option C (PFX from secret)

Set a repo **variable** `ENABLE_PFX_SIGNING=true` and the secrets below.

```yaml
      - name: Decode signing certificate
        if: ${{ vars.ENABLE_PFX_SIGNING == 'true' }}
        shell: pwsh
        run: |
          [IO.File]::WriteAllBytes("$env:RUNNER_TEMP\codesign.pfx",
            [Convert]::FromBase64String("${{ secrets.CODESIGN_PFX_BASE64 }}"))

      - name: Sign StealthPDF.exe (signtool + PFX)
        if: ${{ vars.ENABLE_PFX_SIGNING == 'true' }}
        shell: pwsh
        run: |
          $signtool = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin\*\x64\signtool.exe" |
                      Sort-Object FullName -Descending | Select-Object -First 1 -ExpandProperty FullName
          & $signtool sign /fd sha256 /tr http://timestamp.digicert.com /td sha256 `
            /f "$env:RUNNER_TEMP\codesign.pfx" /p "${{ secrets.CODESIGN_PFX_PASSWORD }}" `
            /d "StealthPDF" /du "https://stealthpdf.com" `
            "KillerPDF\bin\Release\net48\publish\StealthPDF.exe"
          & $signtool verify /pa /v "KillerPDF\bin\Release\net48\publish\StealthPDF.exe"
```

> ⚠️ Never store the `.pfx` or its password in the repo — use GitHub secrets.
> ⚠️ Don't ship unsigned builds as official releases. `release.ps1 -SkipSign` and an
> unsigned CI build are for testing only.

---

## 4. Verifying a signed build

```powershell
# Developer PowerShell:
signtool verify /pa /v StealthPDF.exe
# Or via Explorer: right-click the EXE → Properties → Digital Signatures
```

The signer should read `Open Source Developer, <your name>` (or your organization) and
the chain should validate to a trusted root. `release.ps1` already runs this as a
post-sign gate and aborts if it fails.
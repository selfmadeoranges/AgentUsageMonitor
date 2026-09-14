# SmallTV-Ultra standalone usage monitor

## Resulting architecture

The ESP8266 is the runtime collector and display. The Windows UsageMonitor is unchanged and is only an optional source for a one-time credential export.

```text
Claude usage endpoint ──HTTPS──┐
                              ├── SmallTV-Ultra ── 240 x 240 LCD
Codex usage endpoint ───HTTPS──┘          │
                                          ├── DHCP address on saved 2.4 GHz Wi-Fi
                                          ├── HTTP settings and authenticated OTA
                                          └── recovery AP at 192.168.4.1
```

The device calls these endpoints, based on the inspected UsageMonitor provider code:

| Provider | Usage | Token refresh |
|---|---|---|
| Claude | `https://api.anthropic.com/api/oauth/usage` | `https://console.anthropic.com/v1/oauth/token` |
| Codex | `https://chatgpt.com/backend-api/wham/usage` | `https://auth.openai.com/oauth/token` |

These are account/CLI paths rather than stable public usage contracts. The official OpenAI Usage API documents organization API consumption, not the ChatGPT/Codex subscription windows returned by `backend-api/wham/usage`: [OpenAI organization Usage API](https://developers.openai.com/api/reference/resources/admin/subresources/organization/subresources/usage). Anthropic documents Claude Code OAuth login and secure credential storage, but does not document the account usage response as a public API: [Claude Code setup](https://docs.anthropic.com/en/docs/claude-code/getting-started). Provider changes can therefore require a firmware update.

## Implemented device behavior

### Network and recovery

- Connects as a Wi-Fi station to a saved 2.4 GHz SSID and receives its address by DHCP.
- Supports a 0-300 second startup connection delay.
- Retries a lost connection every 30 seconds.
- Starts `UsageMonitor-XXXXXX` as a WPA2 recovery access point during every boot and keeps it available while the station is online.
- Uses `192.168.4.1/24` for recovery and answers captive-portal DNS requests while station recovery is needed.
- Keeps station reconnect attempts active in AP+STA mode. Losing the saved network therefore does not remove the recovery path.
- Shows the current DHCP address on every normal LCD page. Recovery mode shows its SSID, password, and `192.168.4.1`.
- Advertises `usage-smalltv.local` with mDNS while connected. The numeric address remains the reliable fallback.

A DHCP address change is not a fault. The new address appears on the LCD and the HTTP UI continues there. The fixed recovery address remains available if the new DHCP address is unknown.

### Direct usage collection

- Synchronizes UTC with NTP before starting HTTPS.
- Validates server certificates. TLS verification is not disabled.
- Polls Claude and Codex sequentially to keep ESP8266 peak memory use bounded.
- Refreshes access tokens on expiry and retries once after HTTP 401/403.
- Retains the last valid LCD values during network, authentication, parsing, and HTTP 429 failures.
- Backs off HTTP 429 responses for 2, 5, 10, then 15 minutes.
- Parses Claude 5-hour and weekly windows, then keeps only the Fable model-scoped weekly window.
- Selects only the weekly Codex rate-limit window and omits the shorter primary window.
- Sends every HTTPS request with the product user agent `UsageMonitor-SmallTV/<version>`. Earlier builds impersonated the `claude-code/` user agent because the Claude account endpoint returned a persistent HTTP 429 to the ESP8266 client; that impersonation was removed in 0.2.8 because bypassing a provider-side client check risks the account. If the endpoint again returns 429 with the honest user agent, treat it as a provider decision: lengthen the refresh interval or stop polling Claude from the device rather than restoring the impersonation.
- Uses HTTP/1.1 and lets ESP8266HTTPClient assemble chunked responses before JSON parsing.

The embedded TLS roots match the endpoint chains observed on 2026-09-14. A later certificate-chain change can require a firmware update. NTP failure prevents API calls because certificate validation without a correct clock is unsafe.

### LCD and original Settings features

Claude and Codex pages switch every six seconds. Claude shows 5 hours, Week, and Fable; Codex shows Week only. Each page includes usage percentages, progress bars, reset countdowns, provider state, and the assigned IP.

The main labels and percentages use TFT_eSPI Font 4. Reset countdowns, state, and IP use Font 2 so the three Claude rows remain readable on the physical 240 x 240 panel.

The following items from the original SmallTV settings page are retained. Theme selection and automatic theme switching, the original items 1 and 2, are intentionally omitted.

- Day brightness, 0-255
- Night mode enable, start/end hour, and night brightness
- Factory reset
- Reboot
- Firmware update

The settings page also includes UTC offset and usage refresh interval because night mode and API polling need them.

## HTTP web interface

The same web server is reachable at `http://<DHCP-IP>/` in normal mode and `http://192.168.4.1/` in recovery mode. Login is HTTP Basic user `admin` plus the device password shown on the recovery LCD. Define `ADMIN_PASSWORD` in `SmallTvConfig.h` for a fixed 12-31 character password; otherwise the firmware derives a stable password for that device.

This HTTP Basic login only protects the SmallTV settings pages and OTA upload. It is unrelated to Claude or ChatGPT OAuth. Entering the `admin` password does not sign the device into either provider.

All user-facing HTML pages, validation messages, and OTA success/failure responses are served in Korean with UTF-8 content types. Diagnostic `/status.json` property names and internal provider states remain stable English identifiers.

| Page | Purpose |
|---|---|
| `/` | Network and provider status, current IP |
| `/network.html` | Scan visible 2.4 GHz networks, enter hidden SSID/password, set connection delay |
| `/accounts.html` | Import or clear Claude/Codex OAuth credentials; saved secrets are never echoed |
| `/settings.html` | Brightness, night mode, reset, reboot, update link |
| `/status.json` | Authenticated status JSON with no tokens |
| `/update` | Authenticated firmware BIN upload |

The setup site is HTTP because that is what the ESP8266 recovery interface supports. Provision credentials over the device's WPA2 recovery AP or another trusted local network. LittleFS stores OAuth refresh tokens as plaintext from the ESP8266's point of view; physical flash access can recover them.

Factory reset writes an empty network configuration and deletes both provider credential files. It therefore returns to the recovery AP even if build-time Wi-Fi defaults were present.

## One-time account provisioning

Browser OAuth in the existing desktop app redirects to `localhost`. A browser cannot redirect that callback to a separate ESP8266, so initial login is not implemented on the device. Runtime operation is standalone after a one-time credential import.

The desktop and device roles are:

- The desktop UsageMonitor performs Claude and Codex browser OAuth with PKCE.
- Desktop-owned tokens are stored in Windows Credential Manager as `UsageMonitor/ClaudeOAuth` and `UsageMonitor/CodexOAuth`.
- When no desktop-owned Codex token exists, the export helper can read `%USERPROFILE%\.codex\auth.json` or `CODEX_HOME` instead. Claude Code's credential file is not used.
- The export helper copies the selected access token, refresh token, expiry, and Codex account ID into a provisioning bundle.
- SmallTV saves the imported credentials as `/claude.json` and `/codex.json` in LittleFS. It does not contact the desktop app during normal operation.
- Before an access token expires, or after HTTP 401/403, SmallTV uses the saved refresh token, saves any returned replacement tokens, and retries the usage request once.

1. Complete Claude and Codex login in the existing Windows UsageMonitor.
2. Run the export helper locally. It refuses to print secrets unless the explicit switch is present:

   ```powershell
   .\tools\export-smalltv-credentials.ps1 -IncludeSecrets
   ```

3. Copy the resulting JSON without saving it to a shared file.
4. Open `/accounts.html`, paste it in **Provisioning bundle**, and import it.
5. Clear the terminal after provisioning. Do not attach or commit the JSON.

The helper reads `UsageMonitor/ClaudeOAuth` and `UsageMonitor/CodexOAuth` from Windows Credential Manager. If the second entry is absent, it falls back to `%USERPROFILE%\.codex\auth.json` or `CODEX_HOME`. It does not modify, refresh, or delete desktop credentials.

Individual fields are also available on `/accounts.html`. Codex requires the access token, refresh token, and ChatGPT account ID. An expiry of `0` means the access token is tried until the server rejects it, after which the refresh token is used.

### Refresh-token ownership

The current provisioning method copies an existing refresh token; it does not create a separate OAuth session for the device. The PC and SmallTV can therefore initially hold the same refresh-token value. If a provider rotates the token and invalidates the previous value, the first client to refresh may leave the other client with a stale token. Simultaneous long-term refresh by both clients has not yet been verified.

If either side later reports an authentication error, sign in again on the PC, export a fresh bundle, and provision SmallTV again. A future device-specific OAuth provisioning flow would be required to give the PC and SmallTV independently managed sessions.

## Build

### Windows screen preview

The separate WPF preview reproduces the firmware's 240 x 240 coordinates, RGB565 colors, page counter, three usage rows, state line, IP footer, and recovery screen. It offers a DPI-corrected 240 x 240 physical-pixel mode and a 480 x 480 enlarged mode. It uses editable sample values and does not read OAuth credentials or contact the usage providers.

`shared/smalltv-display-layout.json` is the single source for display dimensions, coordinates, RGB565 colors, visible fixed text, and the page interval. The running preview watches that source file and reloads valid edits after a short debounce. An invalid edit leaves the last valid preview visible and reports the parse or validation error. PlatformIO runs `scripts/generate_display_layout.py` before compilation and creates the ignored `GeneratedDisplayLayout.h` from the same JSON.

```powershell
dotnet run --project src\UsageMonitor.SmallTV.Preview\UsageMonitor.SmallTV.Preview.csproj
```

The preview uses a Windows font, so glyph shapes and text widths can differ slightly from TFT_eSPI's built-in bitmap fonts. Positions, colors, progress lengths, page order, and visible strings follow `drawUsagePage()` and `drawRecoveryPage()` in the firmware.

### Firmware

```powershell
.\tools\Build-SmallTvFirmware.ps1
```

Run the script from the repository root. On its first run, it creates the ignored `tools\.platformio-venv`, installs the pinned PlatformIO Core, and reuses it for later builds. If `SmallTvConfig.h` is missing, the script copies the empty example; initial Wi-Fi can then be configured through the recovery AP. The resulting BIN is written to `firmware\UsageMonitor.SmallTV\.pio\build\smalltv-ultra\firmware.bin`, and the script prints its size and SHA-256.

The project targets PlatformIO `esp12e`, Arduino ESP8266, a 4 MB flash with a 1 MB application slot, and the SmallTV-Ultra ST7789 pin map:

| Signal | GPIO |
|---|---:|
| MOSI | 13 |
| SCLK | 14 |
| DC | 0 |
| RESET | 2 |
| Backlight, active low | 5 |
| CS | tied to GND |

The selected linker layout leaves a 1,044,464-byte application limit. Firmware 0.2.8 uses 541,727 bytes of flash and 33,008 bytes of static RAM; the generated BIN is 545,872 bytes. Dynamic TLS/JSON allocations still need physical-device soak testing.

## Verification and installation result

Verified on 2026-09-14:

- `dotnet build -c Release`: 0 warnings and 0 errors after keeping the PC application architecture unchanged.
- ESP8266 PlatformIO build: success, 51.9% flash and 40.3% static RAM.
- Host parser/validation test: all 16 checks passed for both provider response shapes, UTC reset time, invalid percentages, retry backoff, Wi-Fi, tokens, brightness, and night schedule bounds.
- A reviewed SmallTV loader was used to leave stock `Ultra-V9.0.20`, followed by successful custom-firmware OTA updates through 0.2.5.
- The physical unit rejoined its saved network through DHCP, reported the always-on recovery AP, synchronized time, and retained its LittleFS network and OAuth settings across OTA.
- Live device requests returned HTTP 200 and `state: ok` for both Claude and Codex. The Claude HTTP/1.1 chunked-response path was exercised on the device.
- `/`, `/network.html`, `/accounts.html`, `/settings.html`, and `/update` returned HTTP 200 with authentication and `/` returned HTTP 401 without it.
- All five HTML pages returned `charset=utf-8` and their expected Korean content. A second 0.2.5 upload verified the Korean OTA completion response and reboot path.

Still requiring longer or manual hardware validation:

- Firmware 0.2.8 (honest `UsageMonitor-SmallTV/0.2.8` user agent for all requests) has been built but has not been flashed yet. Confirm that the Claude endpoint still returns HTTP 200 without the `claude-code/` user agent before relying on the device for Claude usage.
- OAuth token rotation after the current access tokens expire
- Connection to the recovery SSID and captive-portal redirect from a second client
- mDNS discovery, night brightness transitions, and long-running heap/network stability

The generated application is larger than the small bridge loader used to leave the stock firmware. Install a compatible reviewed loader first when converting another stock unit. Once this custom firmware is installed, its authenticated `/update` route accepts subsequent application BIN files and preserves LittleFS settings.
